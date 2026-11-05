using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Raven.Server;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Compat.CompatDb;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>An index that a join index reads is replaced by a rolling deployment in a cluster of three nodes: during and after the rollout the join of every node is the join, and every join index ends current and serves again.</summary>
public class JoinCompatRollingTests(ITestOutputHelper output) : ClusterTestBase(output)
{
    private static readonly string[] Expected = ["games/000", "games/001", "games/004", "games/005", "games/008", "games/009"];

    private sealed class RollingUsersIndex : AbstractIndexCreationTask<CompatUser>
    {
        public RollingUsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Kind, u.Number, Replaced = true };
            Index(x => x.Name, FieldIndexing.Search);
            DeploymentMode = IndexDeploymentMode.Rolling;
        }

        public override string IndexName => Users;
    }

    private static IDocumentStore StoreOf(RavenServer node, string database) =>
        new DocumentStore { Urls = [node.WebUrl], Database = database, Conventions = { DisableTopologyUpdates = true } }.Initialize();

    private static List<string> Ids(IDocumentStore store)
    {
        using IDocumentSession session = store.OpenSession(new SessionOptions { NoCaching = true });
        List<CompatGame> games = session.Advanced.RawQuery<CompatGame>($"from index 'Games' as g {VipJoin} order by id() limit 0, 1000").ToList();
        return games.Select(x => session.Advanced.GetDocumentId(x)).ToList();
    }

    // the join index of a node is replaced with the index it reads: while it is, the node has none (-1)
    private static long Served(IDocumentStore store)
    {
        try
        {
            return store.Maintenance.Send(new GetIndexStatisticsOperation(ManualJoin)).Join.Served;
        }
        catch (InvalidDataException)
        {
            return -1;
        }
    }

    [RavenFact(RavenTestCategory.Cluster | RavenTestCategory.Indexes)]
    public Task ARollingReplacementOfTheInnerIndexOfOneNodeKeepsTheJoinExactAndTheJoinServing() => RollingReplacementAsync(1);

    [RavenFact(RavenTestCategory.Cluster | RavenTestCategory.Indexes, LicenseRequired = true)]
    public Task ARollingReplacementOfTheInnerIndexKeepsTheJoinOfEveryNodeOfThreeExactAndTheJoinsServing() => RollingReplacementAsync(3);

    private async Task RollingReplacementAsync(int nodeCount)
    {
        var cluster = await CreateRaftCluster(nodeCount, watcherCluster: true);
        using DocumentStore store = GetDocumentStoreForRollingIndexes(new Options
        {
            Server = cluster.Leader,
            ReplicationFactor = nodeCount,
            ModifyDatabaseRecord = record =>
            {
                record.Settings[RavenConfiguration.GetKey(x => x.Indexing.StaticIndexingEngineType)] = "Corax";
                record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
            }
        });

        await store.Maintenance.SendAsync(new PutIndexesOperation(new UsersIndex().CreateIndexDefinition(), new GamesIndex().CreateIndexDefinition(), JoinDefinition(ManualJoin)));
        using (IDocumentSession session = store.OpenSession())
        {
            session.Store(new CompatUser { Id = "users/strong", Name = "chris bob", Kind = "vip" });
            session.Store(new CompatUser { Id = "users/weak", Name = "chris", Kind = "vip" });
            session.Store(new CompatUser { Id = "users/other", Name = "alice", Kind = "plain" });
            var owners = new[] { "users/strong", "users/weak", "users/other", "users/missing" };
            for (int i = 0; i < 12; i++)
                session.Store(new CompatGame { Id = $"games/{i:D3}", Name = "zelda", OwnerId = owners[i % owners.Length], Rank = i }, $"games/{i:D3}");

            session.SaveChanges();
        }

        WaitForIndexingInTheCluster(store, store.Database);

        List<IDocumentStore> stores = cluster.Nodes.Select(x => StoreOf(x, store.Database)).ToList();
        try
        {
            foreach (IDocumentStore nodeStore in stores)
                Assert.True(await WaitForValueAsync(() => Task.FromResult(Ids(nodeStore).SequenceEqual(Expected) && Served(nodeStore) > 0), true, timeout: 60_000, interval: 100),
                    "a join index does not serve before the rollout");

            // every node is asked all the time while the index is replaced node by node
            var wrong = new ConcurrentBag<string>();
            var errors = new ConcurrentBag<string>();
            long asked = 0;
            using var stop = new CancellationTokenSource();
            Task[] pollers = stores.Select((nodeStore, node) => Task.Run(() =>
            {
                while (stop.IsCancellationRequested == false)
                {
                    try
                    {
                        List<string> ids = Ids(nodeStore);
                        Interlocked.Increment(ref asked);
                        if (ids.SequenceEqual(Expected) == false)
                            wrong.Add($"node {node}: {string.Join(",", ids)}");
                    }
                    catch (Exception e)
                    {
                        errors.Add($"node {node}: {e}");
                    }

                    Thread.Sleep(20);
                }
            })).ToArray();

            await store.ExecuteIndexAsync(new RollingUsersIndex());
            WaitForIndexingInTheCluster(store, store.Database);
            await Task.Delay(500);
            stop.Cancel();
            await Task.WhenAll(pollers);

            output.WriteLine($"asked {asked}, wrong {wrong.Count}, errors {errors.Count}: {string.Join(" | ", errors.Distinct().Take(5))}");
            Assert.True(asked >= 5, $"the pollers asked only {asked} times");
            Assert.True(wrong.IsEmpty, string.Join(Environment.NewLine, wrong.Take(5)));

            // a join that meets the inner index in the moment the replacement disposes it reads the replacement: no poller got an error
            Assert.True(errors.IsEmpty, string.Join(Environment.NewLine, errors.Distinct().Take(5)));

            // after the rollout every node has the new inner index, a current join index, and serves
            foreach (IDocumentStore nodeStore in stores)
            {
                bool ok = await WaitForValueAsync(() =>
                {
                    long before = Served(nodeStore);
                    List<string> ids = Ids(nodeStore);
                    return Task.FromResult(before >= 0 && ids.SequenceEqual(Expected) && Served(nodeStore) > before);
                }, true, timeout: 60_000, interval: 200);
                Assert.True(ok, "a join index does not serve again after the rolling replacement of the inner index");
                Assert.Contains("Replaced", (await nodeStore.Maintenance.SendAsync(new GetIndexOperation(Users))).Maps.Single());
            }
        }
        finally
        {
            foreach (IDocumentStore nodeStore in stores)
                nodeStore.Dispose();
        }
    }
}
