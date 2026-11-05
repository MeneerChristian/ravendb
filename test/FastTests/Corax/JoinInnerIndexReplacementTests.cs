using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes;
using Index = Raven.Server.Documents.Indexes.Index;
using Tests.Infrastructure;
using Xunit;
using EngineType = Raven.Client.Documents.Indexes.SearchEngineType;

namespace FastTests.Corax;

public class JoinInnerIndexReplacementTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Join = "from index 'Games' as g join (from index 'Users' where Kind = 'vip') as u on g.OwnerId = id(u) order by id() limit 0, 100";

    private sealed class Game
    {
        public string Id { get; set; }
        public string OwnerId { get; set; }
    }

    private sealed class User
    {
        public string Id { get; set; }
        public string Kind { get; set; }
    }

    private sealed class Games : AbstractIndexCreationTask<Game>
    {
        public Games()
        {
            Map = games => from g in games select new { g.OwnerId };
            SearchEngineType = EngineType.Corax;
        }
    }

    private class Users : AbstractIndexCreationTask<User>
    {
        public Users()
        {
            Map = users => from u in users select new { u.Kind };
            SearchEngineType = EngineType.Corax;
        }
    }

    private sealed class UsersReplaced : Users
    {
        public UsersReplaced()
        {
            Map = users => from u in users select new { u.Kind, Replaced = true };
        }

        public override string IndexName => "Users";
    }

    private static bool IsDisposed(Index index)
    {
        if (index.TryBeginQueryAsJoinSource(out IDisposable inUse) == false)
            return true;

        inUse.Dispose();
        return false;
    }

    private IDocumentStore Seed(int? joinSourceWaitInMs = null)
    {
        Options options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        if (joinSourceWaitInMs != null)
            options.ModifyDatabaseRecord = record => record.Settings[RavenConfiguration.GetKey(x => x.Queries.JoinSourceWait)] = joinSourceWaitInMs.ToString();

        IDocumentStore store = GetDocumentStore(options);
        new Games().Execute(store);
        new Users().Execute(store);
        using (IDocumentSession session = store.OpenSession())
        {
            session.Store(new User { Id = "users/1", Kind = "vip" });
            session.Store(new User { Id = "users/2", Kind = "plain" });
            session.Store(new Game { Id = "games/1", OwnerId = "users/1" });
            session.Store(new Game { Id = "games/2", OwnerId = "users/2" });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
        return store;
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Indexes | RavenTestCategory.Querying)]
    public async Task AJoinThatResolvedTheInnerIndexBeforeItsReplacementReadsTheReplacement()
    {
        using IDocumentStore store = Seed();
        var database = await GetDatabase(store.Database);
        int replaced = 0;
        database.IndexStore.ForTestingPurposesOnly().BeforeJoinSourceIsHeld = index =>
        {
            // the first time the join holds the index that it resolved, the index is replaced and the replacement took its place: the one it holds was disposed
            if (Interlocked.Exchange(ref replaced, 1) == 1)
                return;

            new UsersReplaced().Execute(store);
            Assert.True(SpinWait.SpinUntil(() => ReferenceEquals(database.IndexStore.GetIndex("Users"), index) == false, TimeSpan.FromSeconds(30)), "the index was not replaced");
            Assert.True(SpinWait.SpinUntil(() => IsDisposed(index), TimeSpan.FromSeconds(30)), "the replaced index was not disposed");
        };

        using IDocumentSession session = store.OpenSession(new SessionOptions { NoCaching = true });
        string[] ids = session.Advanced.RawQuery<Game>(Join).ToList().Select(x => session.Advanced.GetDocumentId(x)).ToArray();

        Assert.Equal(1, replaced);
        Assert.Equal(["games/1"], ids);
        Assert.Contains("Replaced", store.Maintenance.Send(new Raven.Client.Documents.Operations.Indexes.GetIndexOperation("Users")).Maps.Single());
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Indexes | RavenTestCategory.Querying)]
    public async Task AJoinWithAnInnerIndexThatStaysDisposedFailsWithAClearError()
    {
        using IDocumentStore store = Seed(joinSourceWaitInMs: 300);
        var database = await GetDatabase(store.Database);
        database.IndexStore.GetIndex("Users").Dispose();

        using IDocumentSession session = store.OpenSession(new SessionOptions { NoCaching = true });
        InvalidQueryException e = Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<Game>(Join).ToList());

        Assert.Contains("'Users'", e.Message);
        Assert.Contains("being replaced", e.Message);
        Assert.Contains("300 ms", e.Message);
        Assert.DoesNotContain("was already disposed", e.Message);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Indexes | RavenTestCategory.Querying)]
    public async Task AJoinThatMeetsTheInnerIndexWhileItsReplacementWaitsForItsQueriesReadsTheReplacementAndDoesNotWaitForThem()
    {
        using IDocumentStore store = Seed();
        var database = await GetDatabase(store.Database);
        IDisposable otherQuery = null;
        int replaced = 0;

        // the join resolved the index, and then a replacement registers the new index and waits for a query that still reads the old one before it disposes it: the old index is held by the
        // replacement, it is not disposed and not available. The join waits for the replacement and does not fail after a few quick tries of the old index.
        database.IndexStore.ForTestingPurposesOnly().BeforeJoinSourceIsHeld = index =>
        {
            if (Interlocked.Exchange(ref replaced, 1) == 1)
                return;

            Assert.True(index.TryBeginQueryAsJoinSource(out otherQuery));
            new UsersReplaced().Execute(store);
            Assert.True(SpinWait.SpinUntil(() => ReferenceEquals(database.IndexStore.GetIndex("Users"), index) == false, TimeSpan.FromSeconds(30)), "the index was not replaced");
            Assert.True(SpinWait.SpinUntil(() => index.TryBeginQueryAsJoinSource(TimeSpan.Zero, out _) == Index.JoinSourceHold.Busy, TimeSpan.FromSeconds(30)), "the replacement does not wait for the query of the old index");
            Thread.Sleep(1000);
        };

        try
        {
            using IDocumentSession session = store.OpenSession(new SessionOptions { NoCaching = true });
            string[] ids = session.Advanced.RawQuery<Game>(Join).ToList().Select(x => session.Advanced.GetDocumentId(x)).ToArray();

            Assert.Equal(1, replaced);
            Assert.Equal(["games/1"], ids);
            Assert.False(((Index)database.IndexStore.GetIndex("Users")).IsDisposed);
        }
        finally
        {
            otherQuery?.Dispose();
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Indexes | RavenTestCategory.Querying)]
    public async Task AJoinWaitsForTheStoreToRegisterTheReplacementOfADisposedInnerIndex()
    {
        using IDocumentStore store = Seed();
        var database = await GetDatabase(store.Database);
        Index old = database.IndexStore.GetIndex("Users");
        old.Dispose();

        // the disposed index is what the store has until the replacement is registered
        Task<string[]> join = Task.Run(() =>
        {
            using IDocumentSession session = store.OpenSession(new SessionOptions { NoCaching = true });
            return session.Advanced.RawQuery<Game>(Join).ToList().Select(x => session.Advanced.GetDocumentId(x)).ToArray();
        });

        await Task.Delay(1500);
        Assert.False(join.IsCompleted, "the join did not wait for the replacement: " + (join.IsFaulted ? join.Exception?.GetBaseException().Message : "answered"));

        new UsersReplaced().Execute(store);
        Assert.Equal(["games/1"], await join);
    }
}
