using System;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>A join index is refused by a sharded database: through the operation of the client, through a creation task, and the join of a query is refused and makes no auto join index.</summary>
public class JoinShardedPutTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Message = "[ShardedDatabase] A join index is not supported for a sharded database.";

    private sealed class GamesWithVipOwner : AbstractJoinCreationTask<CompatGame, CompatUser>
    {
        public GamesWithVipOwner()
        {
            UseOuterIndex<CompatDb.GamesIndex>();
            UseInnerIndex<CompatDb.UsersIndex>();
            JoinOn(x => x.OwnerId);
            AddField(x => x.Kind);
        }

        public override string IndexName => "Games/WithVipOwnerByTask";
    }

    private DocumentStore ShardedStore()
    {
        RavenTestBase.Options options = Options.ForMode(RavenDatabaseMode.Sharded);
        Action<DatabaseRecord> sharded = options.ModifyDatabaseRecord;
        options.ModifyDatabaseRecord = record =>
        {
            sharded?.Invoke(record);
            record.Settings[RavenConfiguration.GetKey(x => x.Indexing.StaticIndexingEngineType)] = "Corax";
            record.Settings[RavenConfiguration.GetKey(x => x.Indexing.AutoIndexingEngineType)] = "Corax";
        };

        return GetDocumentStore(options);
    }

    private static string[] IndexNames(IDocumentStore store) => store.Maintenance.Send(new GetIndexNamesOperation(0, 100)).OrderBy(x => x).ToArray();

    [RavenFact(RavenTestCategory.Sharding | RavenTestCategory.Indexes)]
    public async Task AJoinIsRefusedByAShardedDatabaseAndNothingIsCreated()
    {
        using DocumentStore store = ShardedStore();
        await new CompatDb.UsersIndex().ExecuteAsync(store);
        await new CompatDb.GamesIndex().ExecuteAsync(store);
        string[] before = IndexNames(store);
        Assert.Equal(new[] { "Games", "Users" }, before);

        // the operation of the client
        IndexInvalidException put = await Assert.ThrowsAnyAsync<IndexInvalidException>(() => store.Maintenance.SendAsync(new PutIndexesOperation(CompatDb.JoinDefinition(CompatDb.ManualJoin))));
        Assert.Contains(Message, put.Message);
        Assert.Equal(before, IndexNames(store));
        Assert.DoesNotContain(CompatDb.ManualJoin, store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database)).Indexes.Keys);

        // a creation task
        IndexInvalidException task = await Assert.ThrowsAnyAsync<IndexInvalidException>(() => new GamesWithVipOwner().ExecuteAsync(store));
        Assert.Contains(Message, task.Message);
        Assert.Equal(before, IndexNames(store));

        // together with an index: the put of several indexes is not atomic (as for any index that is refused), the index before the join index is put and the join index is not
        IndexDefinition games = new CompatDb.GamesIndex().CreateIndexDefinition();
        games.Name = "Games2";
        IndexDefinition joinIndex = CompatDb.JoinDefinition("Games2/WithVipOwner");
        joinIndex.Join.OuterIndex = "Games2";
        IndexInvalidException together = await Assert.ThrowsAnyAsync<IndexInvalidException>(() => store.Maintenance.SendAsync(new PutIndexesOperation(games, joinIndex)));
        Assert.Contains(Message, together.Message);
        Assert.Equal(before.Append("Games2").OrderBy(x => x), IndexNames(store));

        // the refusal leaves the database as it was: an index is still put and a plain query still works
        IndexDefinition plain = new CompatDb.GamesIndex().CreateIndexDefinition();
        plain.Name = "Games3";
        await store.Maintenance.SendAsync(new PutIndexesOperation(plain));
        Assert.Equal(new[] { "Games", "Games2", "Games3", "Users" }, IndexNames(store));
    }

    [RavenFact(RavenTestCategory.Sharding | RavenTestCategory.Indexes | RavenTestCategory.Querying)]
    public async Task AJoinInAShardedDatabaseIsRefusedAndMakesNoAutoJoin()
    {
        using DocumentStore store = ShardedStore();
        await new CompatDb.UsersIndex().ExecuteAsync(store);
        await new CompatDb.GamesIndex().ExecuteAsync(store);
        using (var session = store.OpenSession())
        {
            session.Store(new CompatUser { Id = "users/strong", Name = "chris", Kind = "vip" });
            for (int i = 0; i < 10; i++)
                session.Store(new CompatGame { Id = $"games/{i}", Name = "zelda", OwnerId = "users/strong", Rank = i });

            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        // asked twice: the second query is the one that would be answered by an auto join index
        for (int i = 0; i < 2; i++)
        {
            using var session = store.OpenSession();
            Exception e = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<CompatGame>($"from index 'Games' as g {CompatDb.VipJoin}").ToList());
            Assert.Contains("JOIN is not supported in sharding", e.ToString());
        }

        await Task.Delay(500);
        Assert.Equal(new[] { "Games", "Users" }, IndexNames(store));
        DatabaseRecordWithEtag record = store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database));
        Assert.Empty(record.AutoIndexes ?? new());
        Assert.DoesNotContain(record.Indexes.Keys, x => x.Contains("Join", StringComparison.Ordinal));
    }
}
