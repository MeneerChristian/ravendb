using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing;

public class IndexBatchCommittedTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private sealed class Item
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    private sealed class ItemsIndex : AbstractIndexCreationTask<Item>
    {
        public ItemsIndex()
        {
            Map = items => from i in items select new { i.Name };
        }

        public override string IndexName => "Items";
    }

    private static void Store(IDocumentStore store, string id)
    {
        using var session = store.OpenSession();
        session.Store(new Item { Name = id }, id);
        session.SaveChanges();
    }

    [RavenFact(RavenTestCategory.Indexes | RavenTestCategory.Corax)]
    public async Task BatchCommittedTellsWhetherTheBatchDidWork()
    {
        using IDocumentStore store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        new ItemsIndex().Execute(store);
        DocumentDatabase database = await GetDatabase(store.Database);

        var events = new ConcurrentQueue<(string Index, bool DidWork)>();
        database.IndexStore.BatchCommitted += (index, didWork) => events.Enqueue((index, didWork));

        Store(store, "items/1");
        Indexes.WaitForIndexing(store);
        Assert.Contains(events, x => x.Index == "Items" && x.DidWork);

        // nothing was written: whatever batch runs now did no work, and says so
        await Task.Delay(500);
        events.Clear();
        await Task.Delay(500);
        Assert.DoesNotContain(events, x => x.DidWork);
    }

    [RavenFact(RavenTestCategory.Indexes | RavenTestCategory.Corax)]
    public async Task HandlerThatThrowsDoesNotStopIndexing()
    {
        using IDocumentStore store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        new ItemsIndex().Execute(store);
        DocumentDatabase database = await GetDatabase(store.Database);

        int calls = 0;
        database.IndexStore.BatchCommitted += (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("a handler that fails");
        };

        Store(store, "items/1");
        Indexes.WaitForIndexing(store);
        Store(store, "items/2");
        Indexes.WaitForIndexing(store);

        Assert.True(calls >= 2, $"the handler was called {calls} times");
        using var session = store.OpenSession();
        Assert.Equal(2, session.Query<Item, ItemsIndex>().Customize(x => x.WaitForNonStaleResults()).Count());
    }
}
