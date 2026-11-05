using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents;
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
using static FastTests.Server.Documents.Indexing.Joins.Auto.JoinIndexAutoQueryTests;

namespace FastTests.Server.Documents.Indexing.Joins.Auto;

/// <summary>An auto join index is put by a cluster command: every node of the cluster creates and builds it, and serves from its own copy.</summary>
public class JoinIndexAutoClusterTests(ITestOutputHelper output) : ClusterTestBase(output)
{
    private static readonly string Rql = $"from index '{JoinIndexHarness.OuterName}' as m join (from index '{JoinIndexHarness.InnerName}'{FirstSub}) as c on m.ContextId = id(c){FirstTail} include timings()";

    private static (List<string> Ids, long Served) Run(IDocumentStore store)
    {
        using IDocumentSession session = store.OpenSession(new SessionOptions { NoCaching = true });
        List<JoinIndexHarness.Conversation> documents = session.Advanced.RawQuery<JoinIndexHarness.Conversation>(Rql).Timings(out QueryTimings timings).ToList();
        long served = JoinIndexQueryOracle.CountersOf(timings).GetValueOrDefault("Join/c/JoinIndexServed");
        // the order of documents that tie is the order of the index entries, which differs between nodes
        return (documents.Select(x => session.Advanced.GetDocumentId(x)).OrderBy(x => x, StringComparer.Ordinal).ToList(), served);
    }

    [RavenFact(RavenTestCategory.Cluster | RavenTestCategory.Indexes, LicenseRequired = true)]
    public async Task AJoinOnOneNodeCreatesTheAutoJoinOnEveryNodeAndEveryNodeServesFromItsOwn()
    {
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Indexing.StaticIndexingEngineType)] = "Corax" };
        (List<RavenServer> nodes, RavenServer leader) = await CreateRaftCluster(3, customSettings: settings);
        string database = GetDatabaseName();
        var record = new DatabaseRecord(database);
        record.Settings[RavenConfiguration.GetKey(x => x.Indexing.StaticIndexingEngineType)] = "Corax";
        record.Settings[RavenConfiguration.GetKey(x => x.Join.AutoEnabled)] = true.ToString();
        await CreateDatabaseInCluster(record, 3, leader.WebUrl);

        using IDocumentStore store = new DocumentStore { Urls = [leader.WebUrl], Database = database }.Initialize();
        await store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndexHarness.OuterIndex(), JoinIndexHarness.InnerIndex()));
        var random = new Random(21);
        using (IDocumentSession session = store.OpenSession())
        {
            for (int i = 0; i < 14; i++)
            {
                session.Store(new JoinIndexHarness.Context { Country = i % 3 == 0 ? "nl" : "be", Name = "n" + i % 5, Rank = i * 5, IsActive = i % 2 == 0, Title = "w" + i }, $"contexts/{i}");
            }

            for (int i = 0; i < 80; i++)
                session.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{random.Next(18)}", Title = i % 2 == 0 ? "a" : "b" }, $"conversations/{i}");

            session.SaveChanges();
        }

        WaitForIndexingInTheCluster(store, database);

        // one store for each node: the topology of the database is not used, the query goes to that node
        var stores = nodes.Select(x => (IDocumentStore)new DocumentStore { Urls = [x.WebUrl], Database = database, Conventions = { DisableTopologyUpdates = true } }.Initialize()).ToList();
        try
        {
            (List<string> expected, long served) = Run(stores[1]);
            Assert.True(expected.Count > 0);
            Assert.Equal(0, served);

            string name = "Auto/Conversations/ByContext/Join/Contexts/Search/OnContextIdToId";
            foreach (RavenServer node in nodes)
            {
                DocumentDatabase nodeDatabase = await node.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(database);
                Assert.True(await WaitForValueAsync(() => nodeDatabase.IndexStore.GetIndex(name) is JoinIndex, true, timeout: 30_000), $"the auto join index is not an index of node {node.ServerStore.NodeTag}");
            }

            // every node builds it and then answers the join from it, with the answer of the join of the server
            foreach (IDocumentStore nodeStore in stores)
            {
                long servedByNode = 0;
                Assert.True(await WaitForValueAsync(() =>
                {
                    (List<string> ids, long count) = Run(nodeStore);
                    Assert.Equal(expected, ids);
                    servedByNode = count;
                    return Task.FromResult(count > 0);
                }, true, timeout: 30_000, interval: 100), "the node did not serve the join from its auto join index");
                Assert.Equal(1, servedByNode);
            }

            // one record entry, of the type of an auto join index, for the cluster
            DatabaseRecordWithEtag current = await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(database));
            Assert.Equal([name], current.AutoIndexes.Where(x => x.Value.Type == Raven.Client.Documents.Indexes.IndexType.AutoJoin).Select(x => x.Key));
        }
        finally
        {
            foreach (IDocumentStore nodeStore in stores)
                nodeStore.Dispose();
        }
    }
}
