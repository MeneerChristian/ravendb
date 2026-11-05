using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;

namespace FastTests.Corax;

// An OR between JOINs: matches(alias) in the where clause stands for "the document is joined with a document that the sub-query of that alias matched". from index 'Games' as g join (from index 'Users' where search(Name, 'x')) as u on g.OwnerId = id(u) join as d on g.DeptId = id(d) where matches(u) or matches(d) order by score() The oracle does not use the implementation
public partial class JoinOrTests(ITestOutputHelper output, SharedJoinDatabase shared) : RavenTestBase(output), IClassFixture<SharedJoinDatabase>
{
    private const double Tolerance = 1e-4;
    private const string Everything = "limit 0, 1000000";
    private const int Vocabulary = 5000;
    private static readonly string[] Countries = ["nl", "de", "fr", "be", "uk", "es", "it"];

    private const string OuterSearch = "w1 w4 w12 w30 w90 w300";
    private const string UserSearch = "w2 w6 w15 w40 w120 w400";
    private const string DeptSearch = "w3 w9 w25 w70 w200";
    private const string Nothing = "zzzzzz";

    private const string Games = "from index 'Games' as g";
    private static string UsersQuery(string search) => $"from index 'Users' where search(Name, '{search}')";
    private static string DeptsQuery(string search) => $"from index 'Depts' where search(Name, '{search}')";

    private static string Joins(string userSearch = UserSearch, string deptSearch = DeptSearch) =>
        $"join ({UsersQuery(userSearch)}) as u on g.OwnerId = id(u) join ({DeptsQuery(deptSearch)}) as d on g.DeptId = id(d)";

    private static string Outer => $"search(g.Name, '{OuterSearch}')";

    // a small, hand made data set

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void EveryCaseOfTheMatrixOnHandMadeData()
    {
        using var store = SeedShared();

        string[] Names(string rql)
        {
            using var session = store.OpenSession();
            return session.Advanced.RawQuery<Doc>($"{rql} order by score() {Everything}").ToList().Select(x => x.Name.Split(' ')[0]).OrderBy(x => x).ToArray();
        }

        var joins = Joins("zelda", "zelda");
        // g1: owner matches, dept does not
        Assert.Equal(["g1", "g2", "g3", "g6", "g7"], Names($"{Games} {joins} where matches(u) or matches(d)"));
        Assert.Equal(["g1", "g2", "g3", "g6", "g7"], Names($"{Games} {joins} where matches(d) or matches(u)"));
        Assert.Equal(["g3"], Names($"{Games} {joins} where matches(u) and matches(d)"));
        Assert.Equal(["g3"], Names($"{Games} {joins}")); // today's behaviour: both joins are inner joins
        Assert.Equal(["g1", "g3", "g7"], Names($"{Games} join ({UsersQuery("zelda")}) as u on g.OwnerId = id(u) where matches(u)"));
        Assert.Equal(["g1", "g3", "g7"], Names($"{Games} join ({UsersQuery("zelda")}) as u on g.OwnerId = id(u)"));

        // a join that is not mentioned stays an inner join, ANDed with the rest
        Assert.Equal(["g3"], Names($"{Games} {joins} where matches(u)")); // d is not mentioned: it has to match as well
        Assert.Equal(["g3"], Names($"{Games} {joins} where matches(d)"));
        Assert.Equal(["g3"], Names($"{Games} {joins} where search(g.Name, 'game') and matches(u)"));

        // the outer clause is combined with the joins like any other where operand
        Assert.Equal(["g1", "g3"], Names($"{Games} {joins} where search(g.Name, 'one three') and (matches(u) or matches(d))"));
        Assert.Equal(["g1", "g2", "g3", "g7"], Names($"{Games} {joins} where search(g.Name, 'one two three seven four five') and (matches(u) or matches(d))"));
        Assert.Equal(["g1", "g2", "g3", "g6", "g7"], Names($"{Games} {joins} where (search(g.Name, 'one') and matches(u)) or matches(d) or search(g.Name, 'seven')"));
        Assert.Equal(["g1", "g3"], Names($"{Games} {joins} where search(g.Name, 'one') or matches(d)")); // u is not mentioned: an inner join on top

        // one sub-query matches nothing: the OR is the other one, the AND is empty
        var noUsers = Joins(Nothing, "zelda");
        Assert.Equal(["g2", "g3", "g6"], Names($"{Games} {noUsers} where matches(u) or matches(d)"));
        Assert.Empty(Names($"{Games} {noUsers} where matches(u) and matches(d)"));
        var noDepts = Joins("zelda", Nothing);
        Assert.Equal(["g1", "g3", "g7"], Names($"{Games} {noDepts} where matches(u) or matches(d)"));
        Assert.Empty(Names($"{Games} {Joins(Nothing, Nothing)} where matches(u) or matches(d)"));

        // select: the alias of a join that did not match is null, not an error
        using (var session = store.OpenSession())
        {
            var rows = session.Advanced.RawQuery<Projection>($"{Games} {joins} where matches(u) or matches(d) order by score() select g.Name as Name, u.Name as UserName, d.Name as DeptName {Everything}").ToList()
                .ToDictionary(x => x.Name.Split(' ')[0]);
            Assert.Equal(["g1", "g2", "g3", "g6", "g7"], rows.Keys.OrderBy(x => x).ToArray());
            Assert.Equal(("zelda user", null), (rows["g1"].UserName, rows["g1"].DeptName)); // the department exists, it did not match
            Assert.Equal((null, "zelda dept"), (rows["g2"].UserName, rows["g2"].DeptName)); // the owner exists, it did not match
            Assert.Equal(("zelda user", "zelda dept"), (rows["g3"].UserName, rows["g3"].DeptName));
            Assert.Equal((null, "zelda dept too"), (rows["g6"].UserName, rows["g6"].DeptName)); // the owner does not exist
            Assert.Equal(("zelda user two", null), (rows["g7"].UserName, rows["g7"].DeptName)); // the department is null
        }

        // the score is the sum of the scores of the joins that matched
        using (var session = store.OpenSession())
        {
            var scores = ScoresOf(session, $"{Games} {joins} where matches(u) or matches(d) order by score() {Everything}");
            Assert.True(scores["g3"] > scores["g1"] + Tolerance, "a game that matches both joins scores more than a game that matches one");
            Assert.True(scores["g3"] > scores["g2"] + Tolerance);
            var best = ScoresInOrder(session, $"{Games} {joins} where matches(u) or matches(d) order by score() {Everything}");
            Assert.Equal("g3", best[0].Name);
        }

        // the counters of every join keep working
        using (var session = store.OpenSession())
        {
            session.Advanced.RawQuery<Doc>($"{Games} {joins} where matches(u) or matches(d) order by score() include timings()").Timings(out var timings).ToList();
            var join = timings.Timings["Join"];
            foreach (var alias in new[] { "u", "d" })
            {
                var counters = join.Timings[alias].Counters;
                Assert.Equal(alias == "u" ? 2 : 3, counters["SourceMatches"]); // 2 matching users, 3 matching departments
                Assert.True(counters.ContainsKey("OuterMatchKeys"));
                Assert.True(counters.ContainsKey("OuterMatchAllocatedManagedBytes"));
                Assert.True(counters.ContainsKey("ScanAllocatedManagedBytes"));
            }

            Assert.True(join.Counters.ContainsKey("TotalAllocatedManagedBytes"));
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void UnsupportedUsesAreRejected()
    {
        using var store = SeedShared();
        var joins = Joins("zelda", "zelda");

        void Rejected(string where, string message, string joinClauses = null)
        {
            using var session = store.OpenSession();
            var exception = Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<Doc>($"{Games} {joinClauses ?? joins} {where}").ToList());
            Assert.True(message.Split('|').Any(x => exception.Message.Contains(x)), $"[{where}] {exception.Message}");
        }

        // the parser does not take a leading 'not' in a where clause (negation works after and/or)
        Rejected("where not matches(u)", "after 'not'|Method 'not'");
        Rejected("where matches(x)", "not the alias of a JOIN");
        Rejected("where matches(u) = true", "can only be combined with 'and' and 'or'");
        Rejected("where boost(matches(u), 2)", "can only be combined with 'and' and 'or'");
        Rejected("where matches(g.OwnerId)", "expects the alias of a JOIN");
        Rejected("where matches(u, d)", "expects the alias of a JOIN");

        // a JOIN sub-query cannot refer to the alias of its own join
        Rejected("where matches(u)", "cannot be used in the where clause of a JOIN sub-query",
            $"join (from index 'Users' where matches(u)) as u on g.OwnerId = id(u)");
        Rejected("where matches(d)", "cannot be used in the where clause of a JOIN sub-query",
            $"join (from index 'Users' where search(Name, 'zelda') and matches(d)) as u on g.OwnerId = id(u) join ({DeptsQuery("zelda")}) as d on g.DeptId = id(d)");

        // no JOIN at all
        using (var session = store.OpenSession())
        {
            var exception = Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<Doc>($"{Games} where matches(u)").ToList());
            Assert.Contains("not the alias of a JOIN", exception.Message);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ParametersAndStringCasing()
    {
        using var store = SeedShared();

        using var session = store.OpenSession();
        var rql = $"{Games} join (from index 'Users' where search(Name, $users)) as u on g.OwnerId = id(u) join (from index 'Depts' where search(Name, $depts)) as d on g.DeptId = id(d) " +
                  "where MATCHES(u) OR Matches(d) order by score() limit 0, 100";
        var names = session.Advanced.RawQuery<Doc>(rql).AddParameter("users", "zelda").AddParameter("depts", "zelda").ToList().Select(x => x.Name.Split(' ')[0]).OrderBy(x => x).ToArray();
        Assert.Equal(["g1", "g2", "g3", "g6", "g7"], names);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TwoJoinsOnTheSameOuterFieldProjectEachAliasOnItsOwn()
    {
        using var store = GetStore();
        new UsersByNameIndex().Execute(store);

        // one set of owners seen through two indexes: u matches the 'zelda' ones, d the 'mario' ones
        string[] owners = ["users/1", "users/2", "users/3", "users/4", "users/missing", null];
        var names = new Dictionary<string, string> { ["users/1"] = "zelda user", ["users/2"] = "mario user", ["users/3"] = "zelda mario", ["users/4"] = "plain user" };
        using (var session = store.OpenSession())
        {
            foreach (var (id, name) in names)
                session.Store(new User { Name = name }, id);

            var number = 0;
            foreach (var owner in owners)
                foreach (var dept in owners)
                    session.Store(new Game { Name = $"g{number++}", OwnerId = owner, DeptId = dept }, $"games/{number}");

            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        const string uJoin = "join (from index 'Users' where search(Name, 'zelda')) as u on g.OwnerId = id(u)";
        string[] matchedByU = ["users/1", "users/3"];
        string[] matchedByD = ["users/2", "users/3"];

        void Verify(string label, string dJoin, Func<Game, string> dOuter)
        {
            using var session = store.OpenSession();
            var rows = session.Advanced.RawQuery<Projection>($"{Games} {uJoin} {dJoin} where matches(u) or matches(d) " +
                                                              $"select g.Name as Name, u.Name as UserName, d.Name as DeptName {Everything}").ToList().ToDictionary(x => x.Name);

            var expectedRows = 0;
            foreach (var game in session.Query<Game, GamesIndex>().Customize(x => x.WaitForNonStaleResults()).Take(1000).ToList())
            {
                var uMatches = game.OwnerId != null && matchedByU.Contains(game.OwnerId);
                var dKey = dOuter(game);
                var dMatches = dKey != null && matchedByD.Contains(dKey);
                if (uMatches == false && dMatches == false)
                {
                    Assert.False(rows.ContainsKey(game.Name), $"[{label}] {game.Name} matches neither join");
                    continue;
                }

                expectedRows++;
                Assert.True(rows.TryGetValue(game.Name, out var row), $"[{label}] {game.Name} is missing");
                Assert.Equal(uMatches ? names[game.OwnerId] : null, row.UserName);
                Assert.Equal(dMatches ? names[dKey] : null, row.DeptName);
            }

            Assert.Equal(expectedRows, rows.Count);
            Assert.True(expectedRows > 0);
        }

        // the same outer field: the owner exists in both, only the alias that matched is projected
        Verify("same field", "join (from index 'UsersByName' where search(Name, 'mario')) as d on g.OwnerId = id(d)", g => g.OwnerId);
        // two different outer fields
        Verify("other field", "join (from index 'UsersByName' where search(Name, 'mario')) as d on g.DeptId = id(d)", g => g.DeptId);
    }

    // the tests on the hand made data only read it: they share one database (see SharedJoinDatabase)
    private IDocumentStore SeedShared() => shared.Get("hand made", CoraxOptions(), o => GetDocumentStore(o), store =>
    {
        DeployIndexes(store);
        Seed(store);
    });

    private void Seed(IDocumentStore store)
    {
        using (var session = store.OpenSession())
        {
            session.Store(new User { Name = "zelda user" }, "users/1");
            session.Store(new User { Name = "mario user" }, "users/2");
            session.Store(new User { Name = "zelda user two" }, "users/3");
            session.Store(new Dept { Name = "zelda dept" }, "depts/1");
            session.Store(new Dept { Name = "mario dept" }, "depts/2");
            session.Store(new Dept { Name = "zelda dept too" }, "depts/3");
            session.Store(new Dept { Name = "zelda dept again" }, "depts/4");

            session.Store(new Game { Name = "g1 game one", OwnerId = "users/1", DeptId = "depts/2" }, "games/1");
            session.Store(new Game { Name = "g2 game two", OwnerId = "users/2", DeptId = "depts/1" }, "games/2");
            session.Store(new Game { Name = "g3 game three", OwnerId = "users/1", DeptId = "depts/1" }, "games/3");
            session.Store(new Game { Name = "g4 game four", OwnerId = "users/2", DeptId = "depts/2" }, "games/4");
            session.Store(new Game { Name = "g5 game five" }, "games/5");
            session.Store(new Game { Name = "g6 game six", OwnerId = "users/missing", DeptId = "depts/3" }, "games/6");
            session.Store(new Game { Name = "g7 game seven", OwnerId = "users/3" }, "games/7");
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrOfJoinsAgainstTheOracleAt20k() => RunOracle(users: 10_000, depts: 1_500, games: 15_000, seed: 20);

    private void RunOracle(int users, int depts, int games, int seed)
    {
        using var store = GetStore();
        var generator = new Generator(seed);
        var userList = generator.Users(users);
        var deptList = generator.Depts(depts);
        var gameList = generator.Games(games, users, depts);
        using (var bulk = store.BulkInsert())
        {
            foreach (var user in userList)
                bulk.Store(user, user.Id);
            foreach (var dept in deptList)
                bulk.Store(dept, dept.Id);
            foreach (var game in gameList)
                bulk.Store(game, game.Id);
        }

        Indexes.WaitForIndexing(store, timeout: TimeSpan.FromMinutes(30));

        // the oracle's inputs: every query on its own, no join involved
        var outerScores = Fetch(store, $"{Games} where {Outer} order by score() {Everything}").ToDictionary(x => x.Id, x => x.Score);
        var allScores = Fetch(store, $"{Games} order by score() {Everything}").ToDictionary(x => x.Id, x => x.Score);
        var uScores = Fetch(store, $"{UsersQuery(UserSearch)} order by score() {Everything}").ToDictionary(x => x.Id, x => x.Score);
        var dScores = Fetch(store, $"{DeptsQuery(DeptSearch)} order by score() {Everything}").ToDictionary(x => x.Id, x => x.Score);
        var report = new StringBuilder();
        report.AppendLine($"## {users} users, {depts} depts, {games} games: outer alone {outerScores.Count}, users alone {uScores.Count}, depts alone {dScores.Count}");
        output.WriteLine($"outer-only score without a where: min {allScores.Values.Min()} max {allScores.Values.Max()}");

        // 1. the where clause is an outer clause AND (u OR d)
        Verify(store, "search and (u or d)", $"{Games} {Joins()} where {Outer} and (matches(u) or matches(d))", gameList, uScores, dScores, report,
            (g, u, d) => outerScores.TryGetValue(g.Id, out var o) && (u != null || d != null) ? o + (u ?? 0) + (d ?? 0) : null,
            plain: $"{Games} where {Outer} and (g.OwnerId in ($u) or g.DeptId in ($d))");

        // 2. only the joins
        Verify(store, "u or d", $"{Games} {Joins()} where matches(u) or matches(d)", gameList, uScores, dScores, report,
            (g, u, d) => u != null || d != null ? allScores[g.Id] + (u ?? 0) + (d ?? 0) : null,
            plain: $"{Games} where g.OwnerId in ($u) or g.DeptId in ($d)");

        // 3. the outer clause binds to one of the joins only
        Verify(store, "(search and u) or d", $"{Games} {Joins()} where ({Outer} and matches(u)) or matches(d)", gameList, uScores, dScores, report,
            (g, u, d) =>
            {
                // the AND scores only the rows it matches: an outer score or a u score of a row it does not match is not part of the OR
                var andMatches = outerScores.TryGetValue(g.Id, out var o) && u != null;
                if (andMatches == false && d == null)
                    return null;
                return (andMatches ? o + u.Value : 0) + (d ?? 0);
            },
            plain: $"{Games} where ({Outer} and g.OwnerId in ($u)) or g.DeptId in ($d)");

        // 4. u AND d is what the two ANDed joins do today, in rows and in scores
        var andExpected = new Func<Game, double?, double?, double?>((g, u, d) => outerScores.TryGetValue(g.Id, out var o) && u != null && d != null ? o + u.Value + d.Value : null);
        var andPlain = $"{Games} where {Outer} and g.OwnerId in ($u) and g.DeptId in ($d)";
        Verify(store, "search and u and d", $"{Games} {Joins()} where {Outer} and matches(u) and matches(d)", gameList, uScores, dScores, report, andExpected, andPlain);
        Verify(store, "two joins, no where reference (today)", $"{Games} {Joins()} where {Outer}", gameList, uScores, dScores, report, andExpected, andPlain);
        SameRowsAndScores(store, $"{Games} {Joins()} where {Outer} and matches(u) and matches(d)", $"{Games} {Joins()} where {Outer}");

        // 5. a join that is not mentioned is an inner join on top
        Verify(store, "search and u, d not mentioned", $"{Games} {Joins()} where {Outer} and matches(u)", gameList, uScores, dScores, report, andExpected, andPlain);

        // 6. one sub-query is empty, then both
        var emptyUsers = new Dictionary<string, double>();
        Verify(store, "u empty: search and (u or d)", $"{Games} {Joins(Nothing)} where {Outer} and (matches(u) or matches(d))", gameList, emptyUsers, dScores, report,
            (g, u, d) => outerScores.TryGetValue(g.Id, out var o) && d != null ? o + d.Value : null,
            plain: $"{Games} where {Outer} and g.DeptId in ($d)");
        var emptyDepts = new Dictionary<string, double>();
        Verify(store, "d empty: u or d", $"{Games} {Joins(deptSearch: Nothing)} where matches(u) or matches(d)", gameList, uScores, emptyDepts, report,
            (g, u, d) => u != null ? allScores[g.Id] + u.Value : null,
            plain: $"{Games} where g.OwnerId in ($u)");
        Verify(store, "both empty: u or d", $"{Games} {Joins(Nothing, Nothing)} where matches(u) or matches(d)", gameList, emptyUsers, emptyDepts, report,
            (g, u, d) => null, plain: null);
        Verify(store, "u empty: u and d", $"{Games} {Joins(Nothing)} where matches(u) and matches(d)", gameList, emptyUsers, dScores, report,
            (g, u, d) => null, plain: null);

        // 7. a join on a field next to a join on the id
        var bestByPostcode = Fetch(store, $"{UsersQuery(UserSearch)} order by score() {Everything}").GroupBy(x => x.Postcode).ToDictionary(x => x.Key, x => x.Max(u => u.Score));
        var fieldJoinQuery = $"{Games} join ({UsersQuery(UserSearch)}) as u on g.Postcode = u.Postcode join ({DeptsQuery(DeptSearch)}) as d on g.DeptId = id(d) " +
                             $"where {Outer} and (matches(u) or matches(d))";
        Verify(store, "field join u (Postcode) or id join d", fieldJoinQuery, gameList, bestByPostcode, dScores, report,
            (g, u, d) => outerScores.TryGetValue(g.Id, out var o) && (u != null || d != null) ? o + (u ?? 0) + (d ?? 0) : null,
            plain: null, uKey: g => g.Postcode);

        // 8. order by an outer field (the scores are not needed then), and paging
        VerifyOrderedByField(store, $"{Games} {Joins()} where {Outer} and (matches(u) or matches(d))", gameList,
            g => outerScores.ContainsKey(g.Id) && (uScores.ContainsKey(g.OwnerId ?? "") || dScores.ContainsKey(g.DeptId ?? "")));

        // 9. the projection of an alias that did not match is null
        VerifyProjection(store, $"{Games} {Joins()} where {Outer} and (matches(u) or matches(d))", gameList, userList, deptList, uScores, dScores, outerScores);

        output.WriteLine(report.ToString());
    }

    // The expected score of a game from its own data and the scores of its owner and department in the sub-queries
    private void Verify(IDocumentStore store, string label, string joinQuery, List<Game> games, Dictionary<string, double> uScores, Dictionary<string, double> dScores,
        StringBuilder report, Func<Game, double?, double?, double?> expectedScore, string plain, Func<Game, string> uKey = null)
    {
        uKey ??= g => g.OwnerId;
        var expected = new Dictionary<string, double>();
        foreach (var game in games)
        {
            double? u = uScores.TryGetValue(uKey(game) ?? "", out var us) ? us : null;
            double? d = dScores.TryGetValue(game.DeptId ?? "", out var ds) ? ds : null;
            if (expectedScore(game, u, d) is { } score)
                expected[game.Id] = score;
        }

        // the plain two-step form returns the same rows (it cannot say anything about the scores)
        if (plain != null)
        {
            var rows = new HashSet<string>(PlainIds(store, plain, uScores.Keys, dScores.Keys));
            Assert.True(rows.SetEquals(expected.Keys), $"[{label}] the plain form returns {rows.Count} rows, the oracle {expected.Count}: the oracle itself is wrong");
        }

        VerifyJoin(store, joinQuery, expected, label, report);
    }

    private static List<string> PlainIds(IDocumentStore store, string rql, IEnumerable<string> uIds, IEnumerable<string> dIds)
    {
        var u = uIds.ToArray();
        var d = dIds.ToArray();
        using var session = store.OpenSession();
        // an empty IN list matches nothing: dropped by the caller's clause, so evaluate the empty side by hand
        rql = rql.Replace("g.OwnerId in ($u)", u.Length == 0 ? "g.OwnerId = 'none/none'" : "g.OwnerId in ($u)")
            .Replace("g.DeptId in ($d)", d.Length == 0 ? "g.DeptId = 'none/none'" : "g.DeptId in ($d)");
        var query = session.Advanced.RawQuery<Doc>($"{rql} {Everything}");
        if (u.Length > 0)
            query.AddParameter("u", u);
        if (d.Length > 0)
            query.AddParameter("d", d);

        var ids = new List<string>();
        foreach (var entity in query.ToList())
            ids.Add((string)session.Advanced.GetMetadataFor(entity)[Constants.Documents.Metadata.Id]);
        return ids;
    }

    private void VerifyJoin(IDocumentStore store, string joinQuery, Dictionary<string, double> expected, string label, StringBuilder report)
    {
        var best = Fetch(store, $"{joinQuery} order by score() {Everything}");
        Assert.Equal(expected.Count, best.Count);
        Assert.Equal(best.Count, best.Select(x => x.Id).Distinct().Count());
        Assert.Equal(expected.Keys.OrderBy(x => x).ToList(), best.Select(x => x.Id).OrderBy(x => x).ToList());

        foreach (var row in best)
            Assert.True(Math.Abs(expected[row.Id] - row.Score) < Tolerance, $"[{label}] {row.Id}: oracle {expected[row.Id]} but the join returned {row.Score}");

        for (var i = 1; i < best.Count; i++)
            Assert.True(best[i - 1].Score >= best[i].Score - Tolerance, $"[{label}] position {i} breaks the order ({best[i - 1].Score} then {best[i].Score})");

        var oracleOrder = expected.Values.OrderByDescending(x => x).ToList();
        for (var i = 0; i < oracleOrder.Count; i++)
            Assert.True(Math.Abs(oracleOrder[i] - best[i].Score) < Tolerance, $"[{label}] position {i} should have score {oracleOrder[i]} but has {best[i].Score} ({best[i].Id})");

        var weakest = Fetch(store, $"{joinQuery} order by score() desc {Everything}");
        Assert.Equal(expected.Count, weakest.Count);
        var oracleAscending = oracleOrder.AsEnumerable().Reverse().ToList();
        for (var i = 0; i < oracleAscending.Count; i++)
            Assert.True(Math.Abs(oracleAscending[i] - weakest[i].Score) < Tolerance, $"[{label}] desc position {i} should have score {oracleAscending[i]} but has {weakest[i].Score}");

        foreach (var (skip, take) in new[] { (0, 20), (20, 20), (1000, 10) })
        {
            var page = Fetch(store, $"{joinQuery} order by score() limit {skip}, {take}");
            var expectedCount = Math.Max(0, Math.Min(take, expected.Count - skip));
            Assert.Equal(expectedCount, page.Count);
            for (var i = 0; i < expectedCount; i++)
            {
                Assert.True(Math.Abs(oracleOrder[skip + i] - page[i].Score) < Tolerance, $"[{label}] page {skip},{take} position {i}: expected {oracleOrder[skip + i]} but got {page[i].Score}");
                Assert.True(Math.Abs(expected[page[i].Id] - page[i].Score) < Tolerance, $"[{label}] page {skip},{take}: {page[i].Id} has the wrong score");
            }
        }

        // the total of a page is the total of the query
        using (var session = store.OpenSession())
        {
            session.Advanced.RawQuery<Doc>($"{joinQuery} order by score() limit 0, 20").Statistics(out var stats).ToList();
            Assert.Equal(expected.Count, stats.TotalResults);
        }

        var distinctScores = oracleOrder.Select(x => Math.Round(x, 5)).Distinct().Count();
        report.AppendLine($"### {label}: {expected.Count} rows ({distinctScores} distinct scores): rows, scores, order, paging and total match the oracle");
    }

    private void SameRowsAndScores(IDocumentStore store, string left, string right)
    {
        var a = Fetch(store, $"{left} order by score() {Everything}").OrderBy(x => x.Id).ToList();
        var b = Fetch(store, $"{right} order by score() {Everything}").OrderBy(x => x.Id).ToList();
        Assert.Equal(b.Count, a.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(b[i].Id, a[i].Id);
            Assert.True(Math.Abs(a[i].Score - b[i].Score) < Tolerance, $"{a[i].Id}: {a[i].Score} vs {b[i].Score}");
        }
    }

    private void VerifyOrderedByField(IDocumentStore store, string joinQuery, List<Game> games, Func<Game, bool> expectedRow)
    {
        var expected = games.Where(expectedRow).Select(x => x.Postcode).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(expected.Count > 100);
        var all = Fetch(store, $"{joinQuery} order by Postcode {Everything}");
        Assert.Equal(expected, all.Select(x => x.Postcode).ToList());

        foreach (var (skip, take) in new[] { (0, 20), (20, 20), (1000, 10) })
        {
            var page = Fetch(store, $"{joinQuery} order by Postcode limit {skip}, {take}");
            Assert.Equal(expected.Skip(skip).Take(take).ToList(), page.Select(x => x.Postcode).ToList());
        }
    }

    private void VerifyProjection(IDocumentStore store, string joinQuery, List<Game> games, List<User> users, List<Dept> depts, Dictionary<string, double> uScores,
        Dictionary<string, double> dScores, Dictionary<string, double> outerScores)
    {
        var userNames = users.ToDictionary(x => x.Id, x => x.Name);
        var deptNames = depts.ToDictionary(x => x.Id, x => x.Name);

        using var session = store.OpenSession();
        var rows = session.Advanced.RawQuery<Projection>($"{joinQuery} order by score() select g.Name as Name, g.OwnerId as OwnerId, g.DeptId as DeptId, u.Name as UserName, d.Name as DeptName {Everything}").ToList();
        var expectedCount = games.Count(g => outerScores.ContainsKey(g.Id) && (uScores.ContainsKey(g.OwnerId ?? "") || dScores.ContainsKey(g.DeptId ?? "")));
        Assert.Equal(expectedCount, rows.Count);

        var nullUsers = 0;
        var nullDepts = 0;
        foreach (var row in rows)
        {
            var userMatched = row.OwnerId != null && uScores.ContainsKey(row.OwnerId);
            var deptMatched = row.DeptId != null && dScores.ContainsKey(row.DeptId);
            Assert.True(userMatched || deptMatched);

            Assert.Equal(userMatched ? userNames[row.OwnerId] : null, row.UserName);
            Assert.Equal(deptMatched ? deptNames[row.DeptId] : null, row.DeptName);
            nullUsers += row.UserName == null ? 1 : 0;
            nullDepts += row.DeptName == null ? 1 : 0;
        }

        Assert.True(nullUsers > 10 && nullDepts > 10, $"the check needs rows where an alias did not match ({nullUsers} users, {nullDepts} depts)");
    }

    private static void DeployIndexes(IDocumentStore store)
    {
        new UsersIndex().Execute(store);
        new DeptsIndex().Execute(store);
        new GamesIndex().Execute(store);
    }

    private IDocumentStore GetStore()
    {
        var store = GetDocumentStore(CoraxOptions());
        DeployIndexes(store);
        return store;
    }

    private sealed class Row
    {
        public string Id;
        public double Score;
        public string Postcode;
    }

    private static Dictionary<string, double> ScoresOf(IDocumentSession session, string rql) =>
        ScoresInOrder(session, rql).ToDictionary(x => x.Name, x => x.Score);

    private static List<(string Id, string Name, double Score)> ScoresInOrder(IDocumentSession session, string rql)
    {
        var result = new List<(string, string, double)>();
        foreach (var entity in session.Advanced.RawQuery<Doc>(rql).ToList())
        {
            var metadata = session.Advanced.GetMetadataFor(entity);
            result.Add(((string)metadata[Constants.Documents.Metadata.Id], entity.Name.Split(' ')[0], Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore])));
        }

        return result;
    }

    // one session per query: a session hands the same tracked entity to every query that returns the same id
    private static List<Row> Fetch(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        var rows = new List<Row>();
        foreach (var entity in session.Advanced.RawQuery<Doc>(rql).ToList())
        {
            var metadata = session.Advanced.GetMetadataFor(entity);
            rows.Add(new Row
            {
                Id = (string)metadata[Constants.Documents.Metadata.Id],
                Score = metadata.ContainsKey(Constants.Documents.Metadata.IndexScore) ? Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore]) : 0,
                Postcode = entity.Postcode
            });
        }

        return rows;
    }

    private sealed class Generator
    {
        private readonly Random _random;
        private readonly double[] _cumulative = new double[Vocabulary];

        public Generator(int seed)
        {
            _random = new Random(seed);
            var sum = 0.0;
            for (var i = 0; i < Vocabulary; i++)
            {
                sum += 1.0 / (i + 1);
                _cumulative[i] = sum;
            }
        }

        private string Word()
        {
            var target = _random.NextDouble() * _cumulative[^1];
            var index = Array.BinarySearch(_cumulative, target);
            if (index < 0)
                index = ~index;
            return $"w{Math.Min(index, Vocabulary - 1) + 1}";
        }

        private string Text()
        {
            var length = 1 + (int)(11 * Math.Pow(_random.NextDouble(), 1.7));
            var words = new List<string>();
            for (var i = 0; i < length; i++)
                words.Add(Word());
            if (length >= 2 && _random.Next(100) < 30)
                words[_random.Next(length)] = words[_random.Next(length)];
            if (length >= 3 && _random.Next(100) < 10)
                words.Add(words[0]);
            return string.Join(' ', words);
        }

        public List<User> Users(int count) =>
            Enumerable.Range(0, count).Select(i => new User { Id = $"users/{i}", Name = Text(), Country = Countries[_random.Next(Countries.Length)], Postcode = $"pc{_random.Next(10_000)}" }).ToList();

        public List<Dept> Depts(int count) =>
            Enumerable.Range(0, count).Select(i => new Dept { Id = $"depts/{i}", Name = Text() }).ToList();

        // some games have no owner (null), some point to an owner that does not exist; the same for the department
        public List<Game> Games(int count, int users, int depts) =>
            Enumerable.Range(0, count).Select(i => new Game
            {
                Id = $"games/{i}",
                Name = Text(),
                OwnerId = Reference(i, "users", users),
                DeptId = Reference(i, "depts", depts),
                Country = Countries[_random.Next(Countries.Length)],
                Postcode = $"pc{_random.Next(10_000)}"
            }).ToList();

        private string Reference(int i, string collection, int size)
        {
            var roll = _random.Next(100);
            if (roll < 5)
                return null;
            if (roll < 9)
                return $"{collection}/missing{i}";
            return $"{collection}/{_random.Next(size)}";
        }
    }

    private sealed class User : Doc;

    private sealed class Dept : Doc;

    private sealed class Game : Doc;

    private class Doc
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string DeptId { get; set; }
        public string Country { get; set; }
        public string Postcode { get; set; }
    }

    private sealed class Projection
    {
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string DeptId { get; set; }
        public string UserName { get; set; }
        public string DeptName { get; set; }
    }

    private sealed class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Country, u.Postcode };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    private sealed class UsersByNameIndex : AbstractIndexCreationTask<User>
    {
        public UsersByNameIndex()
        {
            Map = users => from u in users select new { u.Name };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "UsersByName";
    }

    private sealed class DeptsIndex : AbstractIndexCreationTask<Dept>
    {
        public DeptsIndex()
        {
            Map = depts => from d in depts select new { d.Name };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Depts";
    }

    private sealed class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.DeptId, g.Country, g.Postcode };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
