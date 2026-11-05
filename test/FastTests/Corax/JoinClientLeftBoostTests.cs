using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// The left join and the boost written from C# against the server
public class JoinClientLeftBoostTests(ITestOutputHelper output, SharedJoinDatabase shared) : RavenTestBase(output), IClassFixture<SharedJoinDatabase>
{
    private const string Everything = "limit 0, 1000000";
    private const int Users = 300;
    private const int Games = 600;
    private static readonly string[] Countries = ["c0", "c1", "c2", "c3", "c4"];
    private const string UserSearch = "word3 word5";
    private const string GameSearch = "g1 g4 common";

    private sealed record Row(string Id, double Score);

    private static string GameOwner(int i) => i % 11 == 0 ? null : $"users/{i * 7 % 450}";

    public static IEnumerable<object[]> Forms()
    {
        foreach (var form in new[] { "id", "field" })
        {
            yield return [form, false, null];
            yield return [form, true, null];
            yield return [form, true, 0.0];
            yield return [form, true, 0.5];
            yield return [form, false, 2.0];
            yield return [form, true, 2.0];
            yield return [form, false, 0.0];
        }
    }

    // the RQL the client has to be equal to
    private static string HandWritten(string form, bool left, double? boost)
    {
        var on = form == "id" ? "g.OwnerId = id(u)" : "g.Country = u.Country";
        var sub = form == "id" ? $"from index 'Users' where search(Name, '{UserSearch}')" : "from index 'Users' where Rank < 10";
        return $"from index 'Games' as g {(left ? "left join" : "join")} ({sub}) as u on {on}{(boost.HasValue ? $" boost {boost.Value.ToString(CultureInfo.InvariantCulture)}" : "")} where search(g.Name, '{GameSearch}') order by score() {Everything}";
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    [MemberData(nameof(Forms))]
    public void DocumentQueryReturnsTheRowsOfTheHandWrittenRql(string form, bool left, double? boost)
    {
        using var store = Seed();
        var expected = Rows(store, HandWritten(form, left, boost));

        using var session = store.OpenSession();
        var users = SubQuery(session.Advanced.DocumentQuery<User, UsersIndex>(), form);
        var query = session.Advanced.DocumentQuery<Game, GamesIndex>().Search(x => x.Name, GameSearch);
        query = form == "id" ? query.Join(users, x => x.OwnerId, left, boost) : query.Join(users, x => x.Country, u => u.Country, left, boost);
        var actual = ToRows(session, query.OrderByScore().Take(1000000).ToList());

        AssertSame(expected, actual);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    [MemberData(nameof(Forms))]
    public async Task AsyncDocumentQueryReturnsTheRowsOfTheHandWrittenRql(string form, bool left, double? boost)
    {
        using var store = Seed();
        var expected = Rows(store, HandWritten(form, left, boost));

        using var session = store.OpenAsyncSession();
        var users = SubQuery(session.Advanced.AsyncDocumentQuery<User, UsersIndex>(), form);
        var query = session.Advanced.AsyncDocumentQuery<Game, GamesIndex>().Search(x => x.Name, GameSearch);
        query = form == "id" ? query.Join(users, x => x.OwnerId, left, boost) : query.Join(users, x => x.Country, u => u.Country, left, boost);
        var results = await query.OrderByScore().Take(1000000).ToListAsync();
        var actual = results.Select(x => new Row(session.Advanced.GetDocumentId(x), Score(session, x))).ToList();

        AssertSame(expected, actual);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    [MemberData(nameof(Forms))]
    public void LinqReturnsTheRowsOfTheHandWrittenRql(string form, bool left, double? boost)
    {
        using var store = Seed();
        var expected = Rows(store, HandWritten(form, left, boost));

        using var session = store.OpenSession();
        var users = form == "id"
            ? session.Query<User, UsersIndex>().Search(x => x.Name, UserSearch)
            : session.Query<User, UsersIndex>().Where(x => x.Rank < 10);
        var query = session.Query<Game, GamesIndex>().Search(x => x.Name, GameSearch);
        query = form == "id" ? query.Join(users, x => x.OwnerId, left, boost) : query.Join(users, x => x.Country, u => u.Country, left, boost);
        var actual = ToRows(session, query.OrderByScore().Take(1000000).ToList());

        AssertSame(expected, actual);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LeftJoinKeepsEveryRowOfTheQueryWithoutTheJoin()
    {
        using var store = Seed();
        using var session = store.OpenSession();
        var plain = session.Advanced.DocumentQuery<Game, GamesIndex>().Search(x => x.Name, GameSearch).OrderByScore().Take(1000000).ToList();
        var plainRows = plain.ToDictionary(x => session.Advanced.GetDocumentId(x), x => Score(session, x));

        using var session2 = store.OpenSession();
        var users = SubQuery(session2.Advanced.DocumentQuery<User, UsersIndex>(), "id");
        var left = ToRows(session2, session2.Advanced.DocumentQuery<Game, GamesIndex>().Search(x => x.Name, GameSearch).Join(users, x => x.OwnerId, left: true, boost: 0).OrderByScore().Take(1000000).ToList());

        // boost 0: the join does not change a score
        Assert.Equal(plainRows.Count, left.Count);
        foreach (var row in left)
            Assert.Equal(plainRows[row.Id], row.Score, 5);
    }

    private sealed class GameName
    {
        public string Name { get; set; }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void ProjectionWithAJoinReturnsTheProjectedFieldsOfTheJoinedRows()
    {
        using var store = Seed();
        var expected = Rows(store, HandWritten("id", false, null)).Select(x => x.Id).OrderBy(x => x).ToList();

        using var session = store.OpenSession();
        var users = SubQuery(session.Advanced.DocumentQuery<User, UsersIndex>(), "id");
        var projected = session.Advanced.DocumentQuery<Game, GamesIndex>().Search(x => x.Name, GameSearch).Join(users, x => x.OwnerId)
            .SelectFields<GameName>("Name").OrderByScore().Take(1000000).ToList();

        Assert.Equal(expected.Count, projected.Count);
        Assert.All(projected, x => Assert.StartsWith("game", x.Name));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void HighlightingWithAJoinReturnsFragments()
    {
        using var store = Seed();
        using var session = store.OpenSession();
        var users = SubQuery(session.Advanced.DocumentQuery<User, UsersIndex>(), "id");
        var results = session.Advanced.DocumentQuery<Game, GamesIndex>().Search(x => x.Name, "g1").Join(users, x => x.OwnerId)
            .Highlight("Name", 18, 2, out var highlightings).Take(50).ToList();

        Assert.NotEmpty(results);
        foreach (var result in results)
            Assert.NotEmpty(highlightings.GetFragments(session.Advanced.GetDocumentId(result)));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void RenderingAJoinQueryDoesNotChangeTheQuery()
    {
        using var store = Seed();
        using var session = store.OpenSession();
        var users = SubQuery(session.Advanced.DocumentQuery<User, UsersIndex>(), "id");
        var query = session.Advanced.DocumentQuery<Game, GamesIndex>().Search(x => x.Name, GameSearch).Join(users, x => x.OwnerId).OrderBy(x => x.Name);

        var first = query.ToString();
        Assert.Equal(first, query.ToString());
        Assert.Contains("__alias_join.Name", first);

        // the query is still the one the user built: it can be extended and rendered again
        var extended = query.WhereEquals(x => x.Country, "c1").ToString();
        Assert.Contains("__alias_join.Country = $", extended);
        Assert.Equal(1, extended.Split("order by __alias_join.Name").Length - 1);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void ARenderedJoinQueryCanBeRenderedConcurrently()
    {
        using var store = Seed();
        using var session = store.OpenSession();
        var users = SubQuery(session.Advanced.DocumentQuery<User, UsersIndex>(), "id");
        var query = session.Advanced.DocumentQuery<Game, GamesIndex>().Search(x => x.Name, GameSearch).Join(users, x => x.OwnerId).OrderBy(x => x.Name);
        var expected = query.ToString();

        var failures = new ConcurrentBag<string>();
        Parallel.For(0, 2000, _ =>
        {
            var rql = query.ToString();
            if (rql != expected)
                failures.Add(rql);
        });

        Assert.Empty(failures);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void QuerySyntaxWithSeveralJoinsReturnsTheRowsOfTheHandWrittenRql()
    {
        using var store = Seed();
        var expected = Rows(store, "from index 'Games' as g join (from index 'Users' where search(Name, 'word3 word5')) as j on g.OwnerId = id(j) " +
                                   "join (from index 'Users' where Rank < 100) as j1 on g.Country = j1.Country " +
                                   $"where g.Country != 'c4' order by score() {Everything}");

        using var session = store.OpenSession();
        var owners = session.Query<User, UsersIndex>().Search(x => x.Name, UserSearch);
        var countries = session.Query<User, UsersIndex>().Where(x => x.Rank < 100);
        var games = (from g in session.Query<Game, GamesIndex>().Where(x => x.Country != "c4")
                     join u in owners on g.OwnerId equals u.Id
                     join c in countries on g.Country equals c.Country
                     select g).OrderByScore().Take(1000000).ToList();

        AssertSame(expected, ToRows(session, games));
    }

    private static IDocumentQuery<User> SubQuery(IDocumentQuery<User> query, string form) =>
        form == "id" ? query.Search(x => x.Name, UserSearch) : query.WhereLessThan(x => x.Rank, 10L);

    private static IAsyncDocumentQuery<User> SubQuery(IAsyncDocumentQuery<User> query, string form) =>
        form == "id" ? query.Search(x => x.Name, UserSearch) : query.WhereLessThan(x => x.Rank, 10L);

    private static void AssertSame(List<Row> expected, List<Row> actual)
    {
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Count, actual.Count);
        var byId = actual.ToDictionary(x => x.Id, x => x.Score);
        Assert.Equal(expected.Count, byId.Count);
        foreach (var row in expected)
        {
            Assert.True(byId.TryGetValue(row.Id, out var score), $"{row.Id} is missing");
            Assert.Equal(row.Score, score, 5);
        }
    }

    private static List<Row> Rows(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return ToRows(session, session.Advanced.RawQuery<Game>(rql).ToList());
    }

    private static List<Row> ToRows(IDocumentSession session, List<Game> games) => games.Select(x => new Row(session.Advanced.GetDocumentId(x), Score(session, x))).ToList();

    private static double Score(IAsyncDocumentSession session, Game game) => ScoreOf(session.Advanced.GetMetadataFor(game));
    private static double Score(IDocumentSession session, Game game) => ScoreOf(session.Advanced.GetMetadataFor(game));

    private static double ScoreOf(IMetadataDictionary metadata) =>
        metadata.ContainsKey(Constants.Documents.Metadata.IndexScore) ? Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore]) : 0;

    private IDocumentStore Seed() => shared.Get("client-left-boost", Options.ForSearchEngine(RavenSearchEngineMode.Corax), o => GetDocumentStore(o), store =>
    {
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);

        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < Users; i++)
                bulk.Store(new User { Id = $"users/{i}", Name = $"word{i % 40} word{i % 7} common", Country = Countries[i % Countries.Length], Rank = i });
            for (var i = 0; i < Games; i++)
                bulk.Store(new Game { Id = $"games/{i}", Name = $"game{i} g{i % 13} common", OwnerId = GameOwner(i), Country = Countries[i % Countries.Length] });
        }

        Indexes.WaitForIndexing(store);
    });

    private sealed class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Country { get; set; }
        public long Rank { get; set; }
    }

    private sealed class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string Country { get; set; }
    }

    private sealed class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Country, u.Rank };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    private sealed class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.Country };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
