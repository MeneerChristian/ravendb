using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// The fields of a join alias written from C# (conditions, matches(), order by, select) against the server
public class JoinClientAliasTests(ITestOutputHelper output, SharedJoinDatabase shared) : RavenTestBase(output), IClassFixture<SharedJoinDatabase>
{
    private const string Everything = "limit 0, 1000000";
    private const int Users = 300;
    private const int Games = 600;
    private static readonly string[] Countries = ["c0", "c1", "c2", "c3", "c4"];
    private const string UserSearch = "word3 word5";

    private const string MainRql = "from index 'Games' as g";
    private const string JoinU = "join (from index 'Users' where search(Name, 'word3 word5')) as u on g.OwnerId = id(u)";
    private const string JoinA = "left join (from index 'Users' where search(Name, 'word3 word5')) as a on g.OwnerId = id(a)";
    private const string JoinB = "left join (from index 'Users' where Rank < 10) as b on g.Country = b.Country";

    private sealed record Row(string Id, double Score);

    private static string GameOwner(int i) => i % 11 == 0 ? null : $"users/{i * 7 % 450}";

    // the main query with the join u (the owners that match UserSearch), as the RQL JoinU
    private static IDocumentQuery<Game> WithU(IDocumentSession session) =>
        session.Advanced.DocumentQuery<Game, GamesIndex>().Join(session.Advanced.DocumentQuery<User, UsersIndex>().Search(x => x.Name, UserSearch), x => x.OwnerId).JoinAlias("u");

    private static IDocumentQuery<Game> WithAB(IDocumentSession session) =>
        session.Advanced.DocumentQuery<Game, GamesIndex>()
            .Join(session.Advanced.DocumentQuery<User, UsersIndex>().Search(x => x.Name, UserSearch), x => x.OwnerId, left: true).JoinAlias("a")
            .Join(session.Advanced.DocumentQuery<User, UsersIndex>().WhereLessThan(x => x.Rank, 10L), x => x.Country, u => u.Country, left: true).JoinAlias("b");

    public static IEnumerable<object[]> WhereCases()
    {
        yield return ["and", $"{MainRql} {JoinU} where g.Country = 'c1' and u.Rank > 100", 0];
        yield return ["or", $"{MainRql} {JoinU} where g.Country = 'c0' or u.Country = 'c3'", 1];
        yield return ["not", $"{MainRql} {JoinU} where g.Country = 'c0' and u.Country != 'c2'", 2];
        yield return ["subclause", $"{MainRql} {JoinU} where (u.Country = 'c1' or u.Rank < 100) and search(g.Name, 'g1 g2 g3')", 3];
        yield return ["not group", $"{MainRql} {JoinU} where g.Country = 'c0' and not (u.Country = 'c2' or u.Rank > 100)", 11];
        yield return ["in", $"{MainRql} {JoinU} where u.Country in ('c1', 'c2') and g.Country != 'c1'", 4];
        yield return ["between", $"{MainRql} {JoinU} where u.Rank between 10 and 200 or g.Country = 'c4'", 5];
        yield return ["startsWith", $"{MainRql} {JoinU} where startsWith(u.Country, 'c') and g.Country = 'c2'", 6];
        yield return ["two conditions of the alias", $"{MainRql} {JoinU} where u.Country = 'c1' and u.Rank > 50", 7];
        yield return ["matches or", $"{MainRql} {JoinA} {JoinB} where matches(a) or matches(b)", 8];
        yield return ["matches and condition", $"{MainRql} {JoinA} {JoinB} where matches(a) and g.Country = 'c1' or b.Rank < 3", 9];
        yield return ["not matches", $"{MainRql} {JoinA} {JoinB} where g.Country = 'c2' and not matches(a)", 10];
    }

    private static IDocumentQuery<Game> BuildWhere(IDocumentSession session, int index) => index switch
    {
        0 => WithU(session).WhereEquals("Country", "c1").AndAlso().WhereGreaterThan("u.Rank", 100L),
        1 => WithU(session).WhereEquals("Country", "c0").OrElse().WhereEquals("u.Country", "c3"),
        2 => WithU(session).WhereEquals("Country", "c0").AndAlso().Not.WhereEquals("u.Country", "c2"),
        3 => WithU(session).OpenSubclause().WhereEquals("u.Country", "c1").OrElse().WhereLessThan("u.Rank", 100L).CloseSubclause().AndAlso().Search("Name", "g1 g2 g3"),
        4 => WithU(session).WhereIn("u.Country", new[] { "c1", "c2" }).AndAlso().WhereNotEquals("Country", "c1"),
        5 => WithU(session).WhereBetween("u.Rank", 10L, 200L).OrElse().WhereEquals("Country", "c4"),
        6 => WithU(session).WhereStartsWith("u.Country", "c").AndAlso().WhereEquals("Country", "c2"),
        7 => WithU(session).WhereEquals("u.Country", "c1").AndAlso().WhereGreaterThan("u.Rank", 50L),
        8 => WithAB(session).WhereMatches("a").OrElse().WhereMatches("b"),
        9 => WithAB(session).WhereMatches("a").AndAlso().WhereEquals("Country", "c1").OrElse().WhereLessThan("b.Rank", 3L),
        10 => WithAB(session).WhereEquals("Country", "c2").AndAlso().Not.WhereMatches("a"),
        11 => WithU(session).WhereEquals("Country", "c0").AndAlso().Not.OpenSubclause().WhereEquals("u.Country", "c2").OrElse().WhereGreaterThan("u.Rank", 100L).CloseSubclause(),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    [MemberData(nameof(WhereCases))]
    public void ConditionsOnTheAliasReturnTheRowsOfTheHandWrittenRql(string name, string rql, int index)
    {
        using var store = Seed();
        var expected = Rows(store, rql + " order by score() " + Everything);

        using var session = store.OpenSession();
        var q = BuildWhere(session, index).OrderByScore();
        var actual = ToRows(session, q.Take(1000000).ToList());

        Assert.True(expected.Count > 0, name);
        AssertSame(expected, actual);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public async Task AsyncDocumentQueryAcceptsTheAliasToo()
    {
        using var store = Seed();
        var expected = Rows(store, $"{MainRql} {JoinU} where u.Rank > 20 and g.Country != 'c1' order by score(), u.Rank desc {Everything}");

        using var session = store.OpenAsyncSession();
        var results = await session.Advanced.AsyncDocumentQuery<Game, GamesIndex>()
            .Join(session.Advanced.AsyncDocumentQuery<User, UsersIndex>().Search(x => x.Name, UserSearch), x => x.OwnerId).JoinAlias("u")
            .WhereGreaterThan("u.Rank", 20L).AndAlso().WhereNotEquals("Country", "c1")
            .OrderByScore().OrderByDescending("u.Rank").Take(1000000).ToListAsync();

        AssertSame(expected, results.Select(x => new Row(session.Advanced.GetDocumentId(x), ScoreOf(session.Advanced.GetMetadataFor(x)))).ToList());
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void DefaultAliasesAreJAndJ1ByPosition()
    {
        using var store = Seed();
        using var session = store.OpenSession();
        var query = session.Advanced.DocumentQuery<Game, GamesIndex>()
            .Join(session.Advanced.DocumentQuery<User, UsersIndex>().Search(x => x.Name, UserSearch), x => x.OwnerId)
            .Join(session.Advanced.DocumentQuery<User, UsersIndex>().WhereLessThan(x => x.Rank, 10L), x => x.Country, u => u.Country, left: true)
            .WhereEquals("Country", "c1").AndAlso().WhereGreaterThan("j.Rank", 100L).AndAlso().WhereMatches("j1")
            .OrderByDescending("j.Rank").OrderByScore();

        var rql = query.ToString();
        Assert.Contains("where __alias_join.Country = $", rql);
        Assert.Contains("and j.Rank > $", rql);
        Assert.Contains("and matches(j1)", rql);
        Assert.Contains("order by j.Rank desc, score()", rql);

        var expected = Rows(store, $"{MainRql} join (from index 'Users' where search(Name, 'word3 word5')) as j on g.OwnerId = id(j) left join (from index 'Users' where Rank < 10) as j1 on g.Country = j1.Country " +
                                   $"where g.Country = 'c1' and j.Rank > 100 and matches(j1) order by j.Rank desc, score() {Everything}");
        AssertSame(expected, ToRows(session, query.Take(1000000).ToList()));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void RenderedRqlKeepsTheMainFieldsApartFromTheAlias()
    {
        using var store = Seed();
        using var session = store.OpenSession();
        var rql = WithU(session).WhereEquals("Country", "c1").AndAlso().WhereEquals("u.Country", "c2").OrderBy("Name").OrderBy("u.Rank", OrderingType.Long).ToString();

        Assert.Contains("as u on __alias_join.OwnerId = id(u)", rql);
        Assert.Contains("where __alias_join.Country = $p", rql);
        Assert.Contains("and u.Country = $p", rql);
        Assert.Contains("order by __alias_join.Name, u.Rank as long", rql);
    }

    public static IEnumerable<object[]> OrderCases()
    {
        yield return ["u.Rank", "order by u.Rank", 0];
        yield return ["u.Rank desc", "order by u.Rank desc", 1];
        yield return ["score, u.Rank", "order by score(), u.Rank", 2];
        yield return ["u.Rank, score desc", "order by u.Rank, score() desc", 3];
        yield return ["u.Rank as long desc, Name", "order by u.Rank as long desc, g.Name", 4];
        yield return ["Name, score, u.Country", "order by g.Name, score(), u.Country", 5];
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    [MemberData(nameof(OrderCases))]
    public void OrderByAJoinedFieldReturnsTheRowsInTheOrderOfTheHandWrittenRql(string name, string order, int index)
    {
        using var store = Seed();
        var expected = Rows(store, $"{MainRql} {JoinU} {order} {Everything}");

        using var session = store.OpenSession();
        var query = WithU(session);
        query = index switch
        {
            0 => query.OrderBy("u.Rank"),
            1 => query.OrderByDescending("u.Rank"),
            2 => query.OrderByScore().OrderBy("u.Rank"),
            3 => query.OrderBy("u.Rank").OrderByScoreDescending(),
            4 => query.OrderByDescending("u.Rank", OrderingType.Long).OrderBy("Name"),
            5 => query.OrderBy("Name").OrderByScore().OrderBy("u.Country"),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
        var actual = ToRows(session, query.Take(1000000).ToList());

        Assert.True(expected.Count > 0, name);
        // rows that tie on every sort field can come in any order: the sort keys have to be the same, row by row
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Select(x => x.Id).OrderBy(x => x), actual.Select(x => x.Id).OrderBy(x => x));
        var owners = OwnerRanks(store);
        Assert.Equal(Keys(expected, owners, index), Keys(actual, owners, index));
    }

    private static List<string> Keys(List<Row> rows, Dictionary<string, long> ownerRank, int index) =>
        rows.Select(x => index switch
        {
            0 or 1 => $"{ownerRank[x.Id]}",
            2 or 3 => $"{ownerRank[x.Id]}|{Math.Round(x.Score, 4)}",
            _ => $"{ownerRank[x.Id]}|{Math.Round(x.Score, 4)}|{x.Id}"
        }).ToList();

    private Dictionary<string, long> OwnerRanks(IDocumentStore store)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<Game>($"from index 'Games'  {Everything}").ToList()
            .Where(x => x.OwnerId != null)
            .ToDictionary(x => session.Advanced.GetDocumentId(x), x => long.Parse(x.OwnerId.Substring("users/".Length)));
    }

    private sealed class GameAndOwner
    {
        public string GameName { get; set; }
        public string OwnerName { get; set; }
        public string OwnerCountry { get; set; }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void SelectFieldsOfTheAliasReturnTheProjectionOfTheHandWrittenRql()
    {
        using var store = Seed();
        List<GameAndOwner> expected;
        using (var session = store.OpenSession())
            expected = session.Advanced.RawQuery<GameAndOwner>($"{MainRql} {JoinU} order by g.Name select g.Name as GameName, u.Name as OwnerName, u.Country as OwnerCountry {Everything}").ToList();

        using (var session = store.OpenSession())
        {
            var actual = WithU(session)
                .OrderBy("Name")
                .SelectFields<GameAndOwner>(new QueryData(["Name", "u.Name", "u.Country"], ["GameName", "OwnerName", "OwnerCountry"]))
                .Take(1000000).ToList();

            Assert.NotEmpty(expected);
            Assert.All(expected, x => Assert.NotNull(x.OwnerName));
            Assert.Equal(expected.Select(x => (x.GameName, x.OwnerName, x.OwnerCountry)), actual.Select(x => (x.GameName, x.OwnerName, x.OwnerCountry)));
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void SelectFieldsOfTheAliasOfALeftJoinAreNullWithoutAJoinedDocument()
    {
        using var store = Seed();
        List<GameAndOwner> expected;
        using (var session = store.OpenSession())
            expected = session.Advanced.RawQuery<GameAndOwner>($"{MainRql} left join (from index 'Users' where search(Name, 'word3 word5')) as u on g.OwnerId = id(u) order by g.Name select g.Name as GameName, u.Name as OwnerName {Everything}").ToList();

        using (var session = store.OpenSession())
        {
            var actual = session.Advanced.DocumentQuery<Game, GamesIndex>()
                .Join(session.Advanced.DocumentQuery<User, UsersIndex>().Search(x => x.Name, UserSearch), x => x.OwnerId, left: true).JoinAlias("u")
                .OrderBy("Name")
                .SelectFields<GameAndOwner>(new QueryData(["Name", "u.Name"], ["GameName", "OwnerName"]))
                .Take(1000000).ToList();

            Assert.Contains(expected, x => x.OwnerName == null);
            Assert.Contains(expected, x => x.OwnerName != null);
            Assert.Equal(expected.Select(x => (x.GameName, x.OwnerName)), actual.Select(x => (x.GameName, x.OwnerName)));
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void JoinAliasValidatesItsArgument()
    {
        using var store = Seed();
        using var session = store.OpenSession();
        var query = session.Advanced.DocumentQuery<Game, GamesIndex>();

        Assert.Throws<InvalidOperationException>(() => query.JoinAlias("u"));

        var joined = query.Join(session.Advanced.DocumentQuery<User, UsersIndex>(), x => x.OwnerId);
        Assert.Throws<ArgumentException>(() => joined.JoinAlias(""));
        Assert.Throws<ArgumentException>(() => joined.JoinAlias("a b"));
        Assert.Throws<ArgumentException>(() => joined.JoinAlias("order"));
        Assert.Throws<ArgumentException>(() => joined.JoinAlias("1a"));
        Assert.Throws<ArgumentException>(() => joined.JoinAlias("__alias_join"));
        Assert.Throws<ArgumentException>(() => joined.WhereMatches(" "));

        joined.JoinAlias("u");
        var twice = joined.Join(session.Advanced.DocumentQuery<User, UsersIndex>(), x => x.Country, u => u.Country);
        Assert.Throws<ArgumentException>(() => twice.JoinAlias("u"));
        twice.JoinAlias("v");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void AnUnknownAliasIsRejectedByTheServer()
    {
        using var store = Seed();
        using var session = store.OpenSession();
        var ex = Assert.ThrowsAny<Exception>(() => WithU(session).WhereMatches("nope").ToList());
        Assert.Contains("nope", ex.Message);
    }

    private static IRavenQueryable<User> OwnersQuery(IDocumentSession session) => session.Query<User, UsersIndex>().Search(x => x.Name, UserSearch);
    private static IRavenQueryable<User> SmallRanks(IDocumentSession session) => session.Query<User, UsersIndex>().Where(x => x.Rank < 10);

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqQuerySyntaxConditionsOnTheJoinedVariableReturnTheRowsOfTheHandWrittenRql()
    {
        using var store = Seed();
        using var session = store.OpenSession();
        var owners = OwnersQuery(session);

        var cases = new (string Rql, List<Game> Games)[]
        {
            ($"{MainRql} {JoinU} where u.Country = 'c1' and g.Country != 'c2'",
                (from g in session.Query<Game, GamesIndex>() join u in owners on g.OwnerId equals u.Id where u.Country == "c1" && g.Country != "c2" select g).OrderByScore().Take(1000000).ToList()),
            ($"{MainRql} {JoinU} where u.Country = 'c1' or g.Country = 'c0'",
                (from g in session.Query<Game, GamesIndex>() join u in owners on g.OwnerId equals u.Id where u.Country == "c1" || g.Country == "c0" select g).OrderByScore().Take(1000000).ToList()),
            ($"{MainRql} {JoinU} where u.Rank > 100 and not (u.Country = 'c2')",
                (from g in session.Query<Game, GamesIndex>() join u in owners on g.OwnerId equals u.Id where u.Rank > 100 && u.Country != "c2" select g).OrderByScore().Take(1000000).ToList()),
            ($"{MainRql} {JoinU} where g.Country = 'c3' and (u.Rank < 50 or u.Country = 'c4')",
                (from g in session.Query<Game, GamesIndex>() join u in owners on g.OwnerId equals u.Id where g.Country == "c3" && (u.Rank < 50 || u.Country == "c4") select g).OrderByScore().Take(1000000).ToList()),
            ($"{MainRql} {JoinU} where u.Country = 'c1' and g.Country = 'c3'",
                (from g in session.Query<Game, GamesIndex>() join u in owners on g.OwnerId equals u.Id where u.Country == "c1" where g.Country == "c3" select g).OrderByScore().Take(1000000).ToList()),
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var expected = Rows(store, cases[i].Rql + " order by score() " + Everything);
            Assert.True(expected.Count > 0, $"case {i}");
            AssertSame(expected, ToRows(session, cases[i].Games));
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqQuerySyntaxMatchesCombinesTheJoins()
    {
        using var store = Seed();
        using var session = store.OpenSession();
        var expected = Rows(store, $"{MainRql} join (from index 'Users' where search(Name, 'word3 word5')) as a on g.OwnerId = id(a) join (from index 'Users' where Rank < 10) as b on g.Country = b.Country " +
                                   $"where matches(a) or matches(b) order by score() {Everything}");

        var games = (from g in session.Query<Game, GamesIndex>()
                     join a in OwnersQuery(session) on g.OwnerId equals a.Id
                     join b in SmallRanks(session) on g.Country equals b.Country
                     where RavenQuery.Matches(a) || RavenQuery.Matches(b)
                     select g).OrderByScore().Take(1000000).ToList();

        AssertSame(expected, ToRows(session, games));
        Assert.Throws<NotSupportedException>(() => (from g in session.Query<Game, GamesIndex>() join a in OwnersQuery(session) on g.OwnerId equals a.Id where RavenQuery.Matches(g) select g).ToList());
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqQuerySyntaxOrderByTheJoinedVariable()
    {
        using var store = Seed();
        using var session = store.OpenSession();
        var owners = OwnersQuery(session);
        var ownerRank = OwnerRanks(store);

        var expected = Rows(store, $"{MainRql} {JoinU} order by u.Rank as long desc, g.Name {Everything}");
        var games = (from g in session.Query<Game, GamesIndex>() join u in owners on g.OwnerId equals u.Id orderby u.Rank descending, g.Name select g).Take(1000000).ToList();
        var actual = ToRows(session, games);
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Select(x => x.Id), actual.Select(x => x.Id));

        expected = Rows(store, $"{MainRql} {JoinU} order by score(), u.Rank as long {Everything}");
        games = (from g in session.Query<Game, GamesIndex>() join u in owners on g.OwnerId equals u.Id orderby u.Rank select g).OrderByScore().Take(1000000).ToList();
        actual = ToRows(session, games);
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(Keys(expected, ownerRank, 2), Keys(actual, ownerRank, 2));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqQuerySyntaxProjectsTheFieldsOfTheJoinedVariable()
    {
        using var store = Seed();
        List<GameAndOwner> expected;
        using (var session = store.OpenSession())
            expected = session.Advanced.RawQuery<GameAndOwner>($"{MainRql} {JoinU} order by g.Name select g.Name as GameName, u.Name as OwnerName, u.Country as OwnerCountry {Everything}").ToList();

        using (var session = store.OpenSession())
        {
            var owners = OwnersQuery(session);

            var direct = (from g in session.Query<Game, GamesIndex>().OrderBy(x => x.Name)
                          join u in owners on g.OwnerId equals u.Id
                          select new GameAndOwner { GameName = g.Name, OwnerName = u.Name, OwnerCountry = u.Country }).Take(1000000).ToList();
            Assert.NotEmpty(expected);
            Assert.Equal(expected.Select(x => (x.GameName, x.OwnerName, x.OwnerCountry)), direct.Select(x => (x.GameName, x.OwnerName, x.OwnerCountry)));

            var later = (from g in session.Query<Game, GamesIndex>()
                         join u in owners on g.OwnerId equals u.Id
                         orderby g.Name
                         select new GameAndOwner { GameName = g.Name, OwnerName = u.Name, OwnerCountry = u.Country }).Take(1000000).ToList();
            Assert.Equal(expected.Select(x => (x.GameName, x.OwnerName, x.OwnerCountry)), later.Select(x => (x.GameName, x.OwnerName, x.OwnerCountry)));

            var anonymous = (from g in session.Query<Game, GamesIndex>()
                             join u in owners on g.OwnerId equals u.Id
                             where u.Rank > 20
                             orderby g.Name
                             select new { Game = g.Name, Owner = u.Name }).Take(1000000).ToList();
            var expectedFiltered = session.Advanced.RawQuery<GameAndOwner>($"{MainRql} {JoinU} where u.Rank > 20 order by g.Name select g.Name as GameName, u.Name as OwnerName {Everything}").ToList();
            Assert.NotEmpty(expectedFiltered);
            Assert.Equal(expectedFiltered.Select(x => (x.GameName, x.OwnerName)), anonymous.Select(x => (x.Game, x.Owner)));

            Assert.Throws<NotSupportedException>(() => (from g in session.Query<Game, GamesIndex>() join u in owners on g.OwnerId equals u.Id select u).ToList());
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqQuerySyntaxLeftJoinReturnsTheRowsOfTheHandWrittenRql()
    {
        using var store = Seed();
        using var session = store.OpenSession();
        var owners = OwnersQuery(session);
        const string left = "left join (from index 'Users' where search(Name, 'word3 word5')) as u on g.OwnerId = id(u)";

        var all = (from g in session.Query<Game, GamesIndex>()
                   join u in owners on g.OwnerId equals u.Id into joined
                   from u in joined.DefaultIfEmpty()
                   select g).OrderByScore().Take(1000000).ToList();
        AssertSame(Rows(store, $"{MainRql} {left} order by score() {Everything}"), ToRows(session, all));
        Assert.Equal(Games, all.Count);

        var filtered = (from g in session.Query<Game, GamesIndex>()
                        join u in owners on g.OwnerId equals u.Id into joined
                        from u in joined.DefaultIfEmpty()
                        where g.Country == "c1"
                        select g).OrderByScore().Take(1000000).ToList();
        AssertSame(Rows(store, $"{MainRql} {left} where g.Country = 'c1' order by score() {Everything}"), ToRows(session, filtered));

        List<GameAndOwner> expected = session.Advanced.RawQuery<GameAndOwner>($"{MainRql} {left} order by g.Name select g.Name as GameName, u.Name as OwnerName {Everything}").ToList();
        var projected = (from g in session.Query<Game, GamesIndex>().OrderBy(x => x.Name)
                         join u in owners on g.OwnerId equals u.Id into joined
                         from u in joined.DefaultIfEmpty()
                         select new GameAndOwner { GameName = g.Name, OwnerName = u.Name }).Take(1000000).ToList();
        Assert.Contains(expected, x => x.OwnerName == null);
        Assert.Contains(expected, x => x.OwnerName != null);
        Assert.Equal(expected.Select(x => (x.GameName, x.OwnerName)), projected.Select(x => (x.GameName, x.OwnerName)));

        // a where on the joined variable of a left join keeps only the rows that have a joined document
        var onJoined = (from g in session.Query<Game, GamesIndex>()
                        join u in owners on g.OwnerId equals u.Id into joined
                        from u in joined.DefaultIfEmpty()
                        where u.Country == "c1"
                        select g).OrderByScore().Take(1000000).ToList();
        AssertSame(Rows(store, $"{MainRql} {left} where u.Country = 'c1' order by score() {Everything}"), ToRows(session, onJoined));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqQuerySyntaxNamesTheJoinsAfterTheRangeVariables()
    {
        using var store = Seed();
        using var session = store.OpenSession();
        var query = from g in session.Query<Game, GamesIndex>() join owner in OwnersQuery(session) on g.OwnerId equals owner.Id where owner.Rank > 3 select g;

        var rql = query.ToString();
        Assert.Contains("as owner on", rql);
        Assert.Contains("owner.Rank > $", rql);
    }

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

    private static List<Row> ToRows(IDocumentSession session, List<Game> games) => games.Select(x => new Row(session.Advanced.GetDocumentId(x), ScoreOf(session.Advanced.GetMetadataFor(x)))).ToList();

    private static double ScoreOf(IMetadataDictionary metadata) =>
        metadata.ContainsKey(Constants.Documents.Metadata.IndexScore) ? Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore]) : 0;

    private IDocumentStore Seed() => shared.Get("client-join-alias", Options.ForSearchEngine(RavenSearchEngineMode.Corax), o => GetDocumentStore(o), store =>
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
