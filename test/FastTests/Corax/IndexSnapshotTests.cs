using System;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes;
using Raven.Server.ServerWide.Context;
using Tests.Infrastructure;
using Xunit;
using EngineType = Raven.Client.Documents.Indexes.SearchEngineType;
using Index = Raven.Server.Documents.Indexes.Index;

namespace FastTests.Corax;

public class IndexSnapshotTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private sealed class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    private sealed class CoraxUsers : AbstractIndexCreationTask<User>
    {
        public CoraxUsers()
        {
            Map = users => from u in users select new { u.Name };
            SearchEngineType = EngineType.Corax;
        }
    }

    private sealed class LuceneUsers : AbstractIndexCreationTask<User>
    {
        public LuceneUsers()
        {
            Map = users => from u in users select new { u.Name };
            SearchEngineType = EngineType.Lucene;
        }
    }

    private void Store(Raven.Client.Documents.IDocumentStore store, params string[] names)
    {
        using var session = store.OpenSession();
        foreach (string name in names)
            session.Store(new User { Name = name });
        session.SaveChanges();
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Indexes)]
    public async Task SnapshotHoldsTheSearcherAndTheCursorsOfTheSameTransaction()
    {
        using var store = GetDocumentStore();
        new CoraxUsers().Execute(store);
        Store(store, "a", "b", "c");
        Indexes.WaitForIndexing(store);

        DocumentDatabase database = await Databases.GetDocumentDatabaseInstanceFor(store);
        Index index = database.IndexStore.GetIndex("CoraxUsers");

        long lastEtag;
        using (database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
        using (context.OpenReadTransaction())
            lastEtag = database.DocumentsStorage.GetLastDocumentEtag(context.Transaction.InnerTransaction, "Users");

        Assert.True(index.TryOpenSnapshot(out IndexSnapshot first));
        long entries;
        using (first)
        {
            Assert.Equal(lastEtag, first.Consumed["Users"].Doc);
            Assert.Equal(0, first.Consumed["Users"].Tombstone);
            Assert.Equal(3, first.Searcher.NumberOfEntries);
            Assert.NotNull(first.Transaction);
            Assert.Equal(index._environment.DbId, first.EnvironmentId);

            // the snapshot is not affected by what the index consumes afterwards
            Store(store, "d");
            Indexes.WaitForIndexing(store);

            Assert.Equal(lastEtag, first.Consumed["Users"].Doc);
            Assert.Equal(3, first.Searcher.NumberOfEntries);
            entries = first.Searcher.NumberOfEntries;
        }

        Assert.True(index.TryOpenSnapshot(out IndexSnapshot second));
        using (second)
        {
            Assert.True(second.Consumed["Users"].Doc > lastEtag);
            Assert.Equal(entries + 1, second.Searcher.NumberOfEntries);
            Assert.Equal(first.EnvironmentId, second.EnvironmentId);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Indexes)]
    public async Task ResettingTheIndexChangesTheEnvironmentId()
    {
        using var store = GetDocumentStore();
        new CoraxUsers().Execute(store);
        Store(store, "a");
        Indexes.WaitForIndexing(store);

        DocumentDatabase database = await Databases.GetDocumentDatabaseInstanceFor(store);
        Guid before;
        Assert.True(database.IndexStore.GetIndex("CoraxUsers").TryOpenSnapshot(out IndexSnapshot snapshot));
        using (snapshot)
            before = snapshot.EnvironmentId;

        store.Maintenance.Send(new ResetIndexOperation("CoraxUsers"));
        Indexes.WaitForIndexing(store);

        Assert.True(database.IndexStore.GetIndex("CoraxUsers").TryOpenSnapshot(out IndexSnapshot reset));
        using (reset)
            Assert.NotEqual(before, reset.EnvironmentId);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Indexes)]
    public async Task LuceneIndexHasNoSnapshot()
    {
        using var store = GetDocumentStore();
        new LuceneUsers().Execute(store);
        Store(store, "a");
        Indexes.WaitForIndexing(store);

        DocumentDatabase database = await Databases.GetDocumentDatabaseInstanceFor(store);

        Assert.False(database.IndexStore.GetIndex("LuceneUsers").TryOpenSnapshot(out IndexSnapshot snapshot));
        Assert.Null(snapshot);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Indexes)]
    public async Task OpenSnapshotMakesAStorageOperationWaitForIt()
    {
        using var store = GetDocumentStore();
        new CoraxUsers().Execute(store);
        Store(store, "a");
        Indexes.WaitForIndexing(store);

        DocumentDatabase database = await Databases.GetDocumentDatabaseInstanceFor(store);
        Index index = database.IndexStore.GetIndex("CoraxUsers");

        Assert.True(index.TryOpenSnapshot(out IndexSnapshot snapshot));
        Task drain = Task.Run(() =>
        {
            using (index.DrainRunningQueries())
            {
            }
        });

        Assert.False(await Task.WhenAny(drain, Task.Delay(500)) == drain);
        snapshot.Dispose();
        await drain.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(index.TryOpenSnapshot(out IndexSnapshot again));
        again.Dispose();
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Indexes)]
    public async Task DisabledIndexGivesNoSnapshot()
    {
        using var store = GetDocumentStore();
        new CoraxUsers().Execute(store);
        Store(store, "a");
        Indexes.WaitForIndexing(store);
        DocumentDatabase database = await Databases.GetDocumentDatabaseInstanceFor(store);
        Index index = database.IndexStore.GetIndex("CoraxUsers");

        store.Maintenance.Send(new DisableIndexOperation("CoraxUsers"));

        Assert.False(index.TryOpenSnapshot(out _));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Indexes)]
    public async Task SnapshotCanBeDisposedOnAnotherThreadAndMoreThanOnce()
    {
        using var store = GetDocumentStore();
        new CoraxUsers().Execute(store);
        Store(store, "a");
        Indexes.WaitForIndexing(store);
        DocumentDatabase database = await Databases.GetDocumentDatabaseInstanceFor(store);
        Index index = database.IndexStore.GetIndex("CoraxUsers");

        Assert.True(index.TryOpenSnapshot(out IndexSnapshot snapshot));
        await Task.Run(() => Parallel.Invoke(snapshot.Dispose, snapshot.Dispose));

        // the read lock of the index is released: a storage operation does not wait
        await Task.Run(() =>
        {
            using (index.DrainRunningQueries())
            {
            }
        }).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
