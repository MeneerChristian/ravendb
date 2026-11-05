using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Exceptions;
using Tests.Infrastructure;
using Xunit;
using EngineType = Raven.Client.Documents.Indexes.SearchEngineType;

namespace FastTests.Corax;

// Forms of a JOIN that must be rejected with an explanation instead of silently matching nothing or ignoring the join
public class JoinServerHardeningTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private sealed class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public long Rank { get; set; }
    }

    private sealed class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public long Limit { get; set; }
    }

    private sealed class Games : AbstractIndexCreationTask<Game>
    {
        public Games()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.Rank };
            SearchEngineType = EngineType.Corax;
        }
    }

    private sealed class GamesExactOwner : AbstractIndexCreationTask<Game>
    {
        public GamesExactOwner()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.Rank };
            Index(x => x.OwnerId, FieldIndexing.Exact);
            SearchEngineType = EngineType.Corax;
        }
    }

    private sealed class Users : AbstractIndexCreationTask<User>
    {
        public Users()
        {
            Map = users => from u in users select new { u.Name, u.Limit };
            SearchEngineType = EngineType.Corax;
        }
    }

    private IDocumentStore Seed()
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        var store = GetDocumentStore(options);
        new Games().Execute(store);
        new GamesExactOwner().Execute(store);
        new Users().Execute(store);

        using (var session = store.OpenSession())
        {
            session.Store(new User { Id = "users/1", Name = "ann", Limit = 5 });
            session.Store(new User { Id = "users/2", Name = "bob", Limit = 9 });
            session.Store(new Game { Id = "games/1", Name = "zelda", OwnerId = "users/1", Rank = 7 });
            session.Store(new Game { Id = "games/2", Name = "mario", OwnerId = "users/2", Rank = 3 });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
        return store;
    }

    private static InvalidQueryException Rejected(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<Game>(rql).ToList());
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData("filter u.Name = 'ann'")]
    [InlineData("filter id(u) = 'users/1'")]
    [InlineData("where g.Rank > 0 filter u.Name = 'ann' or g.Rank = 3")]
    public void FilterOnTheAliasOfAJoinIsRejected(string clause)
    {
        using var store = Seed();

        var e = Rejected(store, $"from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) {clause}");

        Assert.Contains("filter", e.Message);
        Assert.Contains("'u'", e.Message);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void FilterOnTheMainDocumentStillWorksNextToAJoin()
    {
        using var store = Seed();
        using var session = store.OpenSession();

        var ids = session.Advanced.RawQuery<Game>("from index 'Games' as g join (from index 'Users' where Name = 'ann') as u on g.OwnerId = id(u) filter g.Rank > 1").ToList().Select(x => x.Id);

        Assert.Equal(["games/1"], ids);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void CompareThroughAnExactKeyFieldIsRejectedAsAJoinThatWouldNeverMatch()
    {
        using var store = Seed();

        var e = Rejected(store, "from index 'GamesExactOwner' as g join (from index 'Users') as u on g.OwnerId = id(u) where g.Rank > u.Limit limit 0, 100");

        Assert.Contains("would never match", e.Message);
        Assert.Contains("OwnerId", e.Message);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void CompareThroughTheDefaultKeyFieldFindsTheJoinedDocument()
    {
        using var store = Seed();
        using var session = store.OpenSession();

        var ids = session.Advanced.RawQuery<Game>("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) where g.Rank > u.Limit").ToList().Select(x => x.Id);

        Assert.Equal(["games/1"], ids);
    }
}
