using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Server;
using Raven.Server.Config;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes.Joins;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins;

/// <summary>A static join index in a cluster: the definition is on every node, every node serves from its own copy, a node that restarts loads its join indexes again, and a write on the leader refreshes the join indexes of the followers.</summary>
public class JoinIndexClusterTests(ITestOutputHelper output) : ClusterTestBase(output)
{
    private const string FirstSub = " where Country = 'nl' or Name = 'n1' or Rank > 10 or IsActive = true";
    private const string FirstTail = " order by m.Title limit 0, 1000";

    private static string RqlOf(string outerIndex) =>
        $"from index '{outerIndex}' as m join (from index '{JoinIndexHarness.InnerName}'{FirstSub}) as c on m.ContextId = id(c){FirstTail} include timings()";

    private static (List<string> Ids, long Served) Run(IDocumentStore store, string outerIndex)
    {
        using IDocumentSession session = store.OpenSession(new SessionOptions { NoCaching = true });
        List<JoinIndexHarness.Conversation> documents = session.Advanced.RawQuery<JoinIndexHarness.Conversation>(RqlOf(outerIndex)).Timings(out QueryTimings timings).ToList();
        long served = JoinIndexQueryOracle.CountersOf(timings).GetValueOrDefault("Join/c/JoinIndexServed");
        // the order of documents that tie is the order of the index entries, which differs between nodes
        return (documents.Select(x => session.Advanced.GetDocumentId(x)).OrderBy(x => x, StringComparer.Ordinal).ToList(), served);
    }

    private static IDocumentStore StoreOf(RavenServer node, string database) =>
        new DocumentStore { Urls = [node.WebUrl], Database = database, Conventions = { DisableTopologyUpdates = true } }.Initialize();

    private async Task AssertEveryNodeServesAsync(List<IDocumentStore> stores, string outerIndex, List<string> expected, string what)
    {
        for (int i = 0; i < stores.Count; i++)
        {
            IDocumentStore store = stores[i];
            List<string> last = null;
            long served = 0;
            bool done = await WaitForValueAsync(() =>
            {
                (last, served) = Run(store, outerIndex);
                return Task.FromResult(served > 0 && last.SequenceEqual(expected));
            }, true, timeout: 60_000, interval: 100);

            Assert.True(done, $"node {i} did not serve {what} from its join index with the expected documents (served {served}, documents {last?.Count}, expected {expected.Count})");
            Assert.Equal(1, served);
        }
    }

    [RavenFact(RavenTestCategory.Cluster | RavenTestCategory.Indexes, LicenseRequired = true)]
    public async Task AStaticJoinIsOnEveryNodeSurvivesARestartAndIsRefreshedByAWriteOnTheLeader()
    {
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Indexing.StaticIndexingEngineType)] = "Corax" };
        (List<RavenServer> nodes, RavenServer leader) = await CreateRaftCluster(3, shouldRunInMemory: false, customSettings: settings);
        string database = GetDatabaseName();
        var record = new DatabaseRecord(database);
        record.Settings[RavenConfiguration.GetKey(x => x.Indexing.StaticIndexingEngineType)] = "Corax";
        await CreateDatabaseInCluster(record, 3, leader.WebUrl);

        using IDocumentStore leaderStore = new DocumentStore { Urls = [leader.WebUrl], Database = database }.Initialize();
        await leaderStore.Maintenance.SendAsync(new PutIndexesOperation(JoinIndexHarness.OuterIndex(), JoinIndexHarness.InnerIndex(), JoinIndexHarness.JoinIndexDefinition()));

        var random = new Random(21);
        using (IDocumentSession session = leaderStore.OpenSession())
        {
            for (int i = 0; i < 14; i++)
            {
                session.Store(new JoinIndexHarness.Context { Country = i % 3 == 0 ? "nl" : "be", Name = "n" + i % 5, Rank = i * 5, IsActive = i % 2 == 0, Title = "w" + i }, $"contexts/{i}");
            }

            for (int i = 0; i < 80; i++)
                session.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{random.Next(18)}", Title = i % 2 == 0 ? "a" : "b" }, $"conversations/{i}");

            session.SaveChanges();
        }

        WaitForIndexingInTheCluster(leaderStore, database);

        var stores = nodes.Select(x => StoreOf(x, database)).ToList();
        try
        {
            // the first query is the join of the server: it is the answer to compare with
            (List<string> expected, long firstServed) = Run(stores[0], JoinIndexHarness.OuterName);
            Assert.True(expected.Count > 0);
            Assert.Equal(0, firstServed);

            foreach (RavenServer node in nodes)
            {
                DocumentDatabase nodeDatabase = await node.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(database);
                Assert.True(await WaitForValueAsync(() => nodeDatabase.IndexStore.GetIndex(JoinIndexHarness.JoinName) is JoinIndex, true, timeout: 30_000), $"{JoinIndexHarness.JoinName} is not a join index of node {node.ServerStore.NodeTag}");
            }

            foreach (IDocumentStore nodeStore in stores)
                Assert.Equal(IndexType.Join, (await nodeStore.Maintenance.SendAsync(new GetIndexOperation(JoinIndexHarness.JoinName))).Type);

            DatabaseRecordWithEtag current = await leaderStore.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(database));
            Assert.Contains(JoinIndexHarness.JoinName, current.Indexes.Keys);

            await AssertEveryNodeServesAsync(stores, JoinIndexHarness.OuterName, expected, "the static join index");

            // a follower restarts: its join indexes are loaded from its storage and serve again
            int restarted = nodes.FindIndex(x => x != leader);
            var info = await DisposeServerAndWaitForFinishOfDisposalAsync(nodes[restarted]);
            stores[restarted].Dispose();
            var options = new ServerCreationOptions
            {
                RunInMemory = false,
                DeletePrevious = false,
                RegisterForDisposal = true,
                CustomSettings = new Dictionary<string, string>(DefaultClusterSettings) { [RavenConfiguration.GetKey(x => x.Indexing.StaticIndexingEngineType)] = "Corax" }
            };
            nodes[restarted] = await ReviveNodeAsync(info, options);
            stores[restarted] = StoreOf(nodes[restarted], database);

            DocumentDatabase revived = await nodes[restarted].ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(database);
            Assert.True(await WaitForValueAsync(() => revived.IndexStore.GetIndex(JoinIndexHarness.JoinName) is JoinIndex, true, timeout: 60_000), $"{JoinIndexHarness.JoinName} is not a join index of the restarted node");

            await AssertEveryNodeServesAsync(stores, JoinIndexHarness.OuterName, expected, "the static join index after the restart");

            // a write on the leader reaches the followers by replication and refreshes their join indexes: the new conversation is in the answer, served by the join index
            using (IDocumentSession session = leaderStore.OpenSession())
            {
                session.Store(new JoinIndexHarness.Context { Country = "nl", Name = "n9", Rank = 500, IsActive = true, Title = "late" }, "contexts/100");
                session.Store(new JoinIndexHarness.Conversation { ContextId = "contexts/100", Title = "zzz" }, "conversations/100");
                session.SaveChanges();
            }

            List<string> afterWrite = expected.Append("conversations/100").Order(StringComparer.Ordinal).ToList();
            await AssertEveryNodeServesAsync(stores, JoinIndexHarness.OuterName, afterWrite, "the static join index after a write");
        }
        finally
        {
            foreach (IDocumentStore nodeStore in stores)
                nodeStore.Dispose();
        }
    }
}
