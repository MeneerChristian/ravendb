using System;
using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Exceptions;
using Tests.Infrastructure;
using Xunit;
using EngineType = Raven.Client.Documents.Indexes.SearchEngineType;

namespace FastTests.Corax;

// What the server says about a JOIN that cannot work
public class JoinValidationTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private sealed class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
    }

    private sealed class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ManagerId { get; set; }
    }

    private sealed class CoraxGames : AbstractIndexCreationTask<Game>
    {
        public CoraxGames()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId };
            SearchEngineType = EngineType.Corax;
        }
    }

    private sealed class CoraxUsers : AbstractIndexCreationTask<User>
    {
        public CoraxUsers()
        {
            Map = users => from u in users select new { u.Name, u.ManagerId };
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

    private IDocumentStore Seed()
    {
        var store = GetDocumentStore();
        new CoraxGames().Execute(store);
        new CoraxUsers().Execute(store);
        new LuceneUsers().Execute(store);

        using (var session = store.OpenSession())
        {
            session.Store(new User { Id = "users/1", Name = "ann" });
            session.Store(new Game { Id = "games/1", Name = "zelda", OwnerId = "users/1" });
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

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoinedIndexOnLuceneIsRejectedAsInvalidQuery()
    {
        using var store = Seed();

        var e = Rejected(store, "from index 'CoraxGames' as g join (from index 'LuceneUsers') as u on g.OwnerId = id(u)");

        Assert.Contains("Corax", e.Message);
        Assert.Contains("LuceneUsers", e.Message);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ConditionOnTheAliasOfAJoinThatComparesItWithTheMainDocumentOfAJoinOnAFieldIsRejectedWithAnExplanation()
    {
        using var store = Seed();

        var e = Rejected(store, "from index 'CoraxGames' as g join (from index 'CoraxUsers') as u on g.Name = u.Name where u.Name = g.Name");

        Assert.Contains("'u'", e.Message);
        Assert.Contains("ambiguous", e.Message);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData("from index 'CoraxGames' as g left join (from index 'CoraxUsers') as u on g.OwnerId = id(u) boost", "Expected a number or a parameter after BOOST")]
    public void ParseFailuresOfAJoinReachTheClientAsARavenExceptionWithTheParseError(string rql, string text)
    {
        using var store = Seed();
        using var session = store.OpenSession();

        // the query parser reports through its own ParseException, which the server sends as a plain RavenException
        var e = Assert.Throws<RavenException>(() => session.Advanced.RawQuery<Game>(rql).ToList());

        Assert.Contains("ParseException", e.Message);
        Assert.Contains(text, e.Message);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ProjectionAndOrderingOfATypedJoinRunOnTheServer()
    {
        using var store = Seed();
        using var session = store.OpenSession();

        var names = session.Advanced.DocumentQuery<Game, CoraxGames>()
            .Join(session.Advanced.DocumentQuery<User, CoraxUsers>().WhereEquals(x => x.Name, "ann"), x => x.OwnerId)
            .OrderBy(x => x.Name)
            .SelectFields<Game>("Name")
            .ToList()
            .Select(x => x.Name)
            .ToList();

        Assert.Equal(["zelda"], names);
    }
}
