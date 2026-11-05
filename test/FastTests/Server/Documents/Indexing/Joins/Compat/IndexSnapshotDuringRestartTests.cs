using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes;
using Tests.Infrastructure;
using Xunit;
using Index = Raven.Server.Documents.Indexes.Index;
using EngineType = Raven.Client.Documents.Indexes.SearchEngineType;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>The snapshot of an index that a replacement restarts: the join index opens snapshots of the indexes that it joins while a replacement may shut the storage environment of one of them down.</summary>
public class IndexSnapshotDuringRestartTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private sealed class Item
    {
        public string Id { get; set; }
        public string Kind { get; set; }
    }

    private sealed class Items : AbstractIndexCreationTask<Item>
    {
        public Items()
        {
            Map = items => from i in items select new { i.Kind };
            SearchEngineType = EngineType.Corax;
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Indexes)]
    public async Task AnIndexWhoseEnvironmentIsRestartedHasNoSnapshotForThatMomentAndOneAfterIt()
    {
        Options options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.RunInMemory = false;
        using IDocumentStore store = GetDocumentStore(options);
        new Items().Execute(store);
        using (var session = store.OpenSession())
        {
            session.Store(new Item { Id = "items/1", Kind = "a" });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
        var database = await GetDatabase(store.Database);
        Index index = database.IndexStore.GetIndex(new Items().IndexName);

        // like the thread of the indexing of a replacement: it inherited the mark that it holds the lock that drains the queries, and does not hold it, so a reader is let in while it restarts the environment
        using var down = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception restartFailure = null;
        Thread restarter;
        using (index.DrainRunningQueries())
        {
            restarter = new Thread(() =>
            {
                try
                {
                    using (index.RestartEnvironment())
                    {
                        down.Set();
                        release.Wait(TimeSpan.FromSeconds(60));
                    }
                }
                catch (Exception e)
                {
                    restartFailure = e;
                    down.Set();
                }
            });
            restarter.Start();
            Thread.Sleep(100);
        }

        Assert.True(down.Wait(TimeSpan.FromSeconds(60)));
        Assert.Null(restartFailure);

        Assert.False(index.TryOpenSnapshot(out IndexSnapshot snapshot));
        Assert.Null(snapshot);

        release.Set();
        restarter.Join();
        Assert.Null(restartFailure);

        Assert.True(index.TryOpenSnapshot(out snapshot));
        snapshot.Dispose();
    }
}
