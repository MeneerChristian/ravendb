using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// The joined document that a JOIN on a field projects
public class JoinProjectionTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Everything = "limit 0, 1000000";
    private const string Games = "from index 'Games' as g";

    // sub-queries over the tiers of the users
    private const string QueryA = "from index 'Users' where boost(Tier = 'a', 100) or boost(Tier = 'b', 10) or Tier = 'c'";
    private const string QueryB = "from index 'Users' where boost(Tier = 'c', 100) or Tier = 'b'";
    private const string QueryX = "from index 'Users' where boost(Tier = 'c', 100) or boost(Tier = 'b', 10) or Tier = 'a'";

    private static readonly string[] Countries = ["NL", "DE", "FR", "BE", "LU"];


    public sealed class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Tier { get; set; }
        public string Country { get; set; }
        public string Region { get; set; }
        public string[] Tags { get; set; }
        public int Order { get; set; }
    }

    public sealed class Dept
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Tag { get; set; }
    }

    public sealed class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Country { get; set; }
        public string Region { get; set; }
        public string[] Tags { get; set; }
        public string DeptId { get; set; }
    }

    public sealed class Row
    {
        public string Name { get; set; }
        public string UserName { get; set; }
        public string DeptName { get; set; }
    }

    private static List<User> UserData()
    {
        var random = new Random(31);
        return Enumerable.Range(0, 90).Select(i =>
        {
            // the regions r0..r2 have only tier a, r3..r5 only tier b, the others have every tier
            var region = random.Next(10);
            var tier = region switch { <= 2 => "a", <= 5 => "b", _ => new[] { "a", "b", "c", "x" }[random.Next(4)] };
            return new User
            {
                Id = $"users/{i}",
                Name = $"user {i}",
                Tier = tier,
                // some users have no country: that must never join (the null of a game does not match the null of a user)
                Country = i % 11 == 0 ? null : Countries[random.Next(Countries.Length)],
                Region = $"r{region}",
                Tags = Enumerable.Range(0, 1 + random.Next(3)).Select(_ => "t" + random.Next(25)).ToArray(),
                Order = i
            };
        }).ToList();
    }

    private static List<Dept> DeptData() => Enumerable.Range(0, 6).Select(i => new Dept { Id = $"depts/{i}", Name = $"dept {i}", Tag = i % 2 == 0 ? "even" : "odd" }).ToList();

    private static List<Game> GameData()
    {
        var random = new Random(32);
        return Enumerable.Range(0, 120).Select(i => new Game
        {
            Id = $"games/{i}",
            Name = $"g{i}",
            // a missing key, a value that no user has, and the values of the users
            Country = random.Next(10) switch { 0 => null, 1 => "ZZ", _ => Countries[random.Next(Countries.Length)] },
            Region = random.Next(10) switch { 0 => null, 1 => "r99", _ => $"r{random.Next(10)}" },
            Tags = random.Next(8) switch
            {
                0 => [],
                1 => ["t98", "t99"], // no user has them
                _ => Enumerable.Range(0, 1 + random.Next(4)).Select(_ => "t" + random.Next(30)).ToArray()
            },
            DeptId = random.Next(8) switch { 0 => null, 1 => "depts/missing", _ => $"depts/{random.Next(6)}" }
        }).ToList();
    }


    private IDocumentStore _store;
    private readonly Dictionary<string, Dictionary<string, double>> _scores = new();

    // The score the sub-query gives the user
    private double Score(string subQuery, User user)
    {
        if (_scores.TryGetValue(subQuery, out var scores) == false)
        {
            scores = new Dictionary<string, double>();
            using var session = _store.OpenSession();
            foreach (var found in session.Advanced.RawQuery<User>(subQuery + " order by score() limit 0, 1000000").ToList())
                scores[found.Id] = Convert.ToDouble(session.Advanced.GetMetadataFor(found)[Constants.Documents.Metadata.IndexScore]);
            _scores[subQuery] = scores;
        }

        return scores.TryGetValue(user.Id, out var score) ? score : 0;
    }

    private bool Better(string subQuery, User candidate, User current) =>
        current == null || Score(subQuery, candidate) > Score(subQuery, current) ||
        (Score(subQuery, candidate) == Score(subQuery, current) && candidate.Order < current.Order);

    // The best scoring document of the sub-query that has any of the values
    private User Winner(string subQuery, IEnumerable<string> values, Func<User, IEnumerable<string>> valuesOf, List<User> users)
    {
        var wanted = values.Where(x => string.IsNullOrEmpty(x) == false).Select(x => x.ToLowerInvariant()).ToHashSet();
        User best = null;
        foreach (var user in users)
        {
            if (Score(subQuery, user) == 0)
                continue;

            if (valuesOf(user).Any(x => x != null && wanted.Contains(x.ToLowerInvariant())) && Better(subQuery, user, best))
                best = user;
        }

        return best;
    }

    private static IEnumerable<string> One(string value) => value == null ? [] : [value];


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TwoFieldJoinsOnTheSameOuterFieldProjectEachItsOwnWinner()
    {
        using var store = Seed();
        var users = UserData();
        var games = GameData();

        var rql = $"{Games} join ({QueryA}) as u on g.Country = u.Country join ({QueryX}) as d on g.Country = d.Country select g.Name as Name, u.Name as UserName, d.Name as DeptName {Everything}";
        var expected = new Dictionary<string, (string, string)>();
        foreach (var game in games)
        {
            var u = Winner(QueryA, One(game.Country), x => One(x.Country), users);
            var d = Winner(QueryX, One(game.Country), x => One(x.Country), users);
            if (u != null && d != null)
                expected[game.Name] = (u.Name, d.Name);
        }

        Assert.True(expected.Count > 30);
        Assert.True(expected.Values.Any(x => x.Item1 != x.Item2), "the two joins have different winners");
        AssertRows(store, rql, expected);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TwoFieldJoinsOnTheSameOuterFieldUnderAnOrProjectNullForTheSideThatDidNotMatch()
    {
        using var store = Seed();
        var users = UserData();
        var games = GameData();

        // the tier a regions are matched by A only
        var rql = $"{Games} join ({QueryA}) as u on g.Region = u.Region join ({QueryB}) as d on g.Region = d.Region where matches(u) or matches(d) " +
                  $"select g.Name as Name, u.Name as UserName, d.Name as DeptName {Everything}";
        var expected = new Dictionary<string, (string, string)>();
        foreach (var game in games)
        {
            var u = Winner(QueryA, One(game.Region), x => One(x.Region), users);
            var d = Winner(QueryB, One(game.Region), x => One(x.Region), users);
            if (u != null || d != null)
                expected[game.Name] = (u?.Name, d?.Name);
        }

        Assert.Contains(expected.Values, x => x.Item1 != null && x.Item2 == null);
        Assert.Contains(expected.Values, x => x.Item1 != null && x.Item2 != null);
        AssertRows(store, rql, expected);

        // the other way round: only d can be missing from the rows of u, and u from the rows of d
        var swapped = $"{Games} join ({QueryB}) as d on g.Region = d.Region join ({QueryA}) as u on g.Region = u.Region where matches(d) or matches(u) " +
                      $"select g.Name as Name, u.Name as UserName, d.Name as DeptName {Everything}";
        AssertRows(store, swapped, expected);

        // one alias selected on its own
        using var session = store.OpenSession();
        var onlyD = session.Advanced.RawQuery<Row>($"{Games} join ({QueryA}) as u on g.Region = u.Region join ({QueryB}) as d on g.Region = d.Region where matches(u) or matches(d) " +
                                                    $"select g.Name as Name, d.Name as DeptName {Everything}").ToList().ToDictionary(x => x.Name, x => x.DeptName);
        Assert.Equal(expected.Count, onlyD.Count);
        foreach (var (name, (_, d)) in expected)
            Assert.Equal(d, onlyD[name]);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TwoFieldJoinsOnDifferentOuterFieldsProjectEachItsOwnWinner()
    {
        using var store = Seed();
        var users = UserData();
        var games = GameData();

        var and = $"{Games} join ({QueryA}) as u on g.Country = u.Country join ({QueryB}) as d on g.Region = d.Region select g.Name as Name, u.Name as UserName, d.Name as DeptName {Everything}";
        var expectedAnd = new Dictionary<string, (string, string)>();
        var expectedOr = new Dictionary<string, (string, string)>();
        foreach (var game in games)
        {
            var u = Winner(QueryA, One(game.Country), x => One(x.Country), users);
            var d = Winner(QueryB, One(game.Region), x => One(x.Region), users);
            if (u != null && d != null)
                expectedAnd[game.Name] = (u.Name, d.Name);
            if (u != null || d != null)
                expectedOr[game.Name] = (u?.Name, d?.Name);
        }

        Assert.True(expectedAnd.Count > 20);
        AssertRows(store, and, expectedAnd);

        var or = $"{Games} join ({QueryA}) as u on g.Country = u.Country join ({QueryB}) as d on g.Region = d.Region where matches(u) or matches(d) " +
                 $"select g.Name as Name, u.Name as UserName, d.Name as DeptName {Everything}";
        Assert.Contains(expectedOr.Values, x => x.Item1 == null || x.Item2 == null);
        AssertRows(store, or, expectedOr);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AnIdJoinAndAFieldJoinProjectEachItsOwnDocument()
    {
        using var store = Seed();
        var users = UserData();
        var games = GameData();
        var depts = DeptData().Where(x => x.Tag == "even").ToDictionary(x => x.Id.ToLowerInvariant());

        const string deptQuery = "from index 'Depts' where Tag = 'even'";
        var and = $"{Games} join ({QueryA}) as u on g.Country = u.Country join ({deptQuery}) as d on g.DeptId = id(d) select g.Name as Name, u.Name as UserName, d.Name as DeptName {Everything}";
        var or = $"{Games} join ({QueryA}) as u on g.Country = u.Country join ({deptQuery}) as d on g.DeptId = id(d) where matches(u) or matches(d) " +
                 $"select g.Name as Name, u.Name as UserName, d.Name as DeptName {Everything}";

        var expectedAnd = new Dictionary<string, (string, string)>();
        var expectedOr = new Dictionary<string, (string, string)>();
        foreach (var game in games)
        {
            var u = Winner(QueryA, One(game.Country), x => One(x.Country), users);
            var d = game.DeptId != null && depts.TryGetValue(game.DeptId.ToLowerInvariant(), out var dept) ? dept : null;
            if (u != null && d != null)
                expectedAnd[game.Name] = (u.Name, d.Name);
            if (u != null || d != null)
                expectedOr[game.Name] = (u?.Name, d?.Name);
        }

        Assert.NotEmpty(expectedAnd);
        Assert.Contains(expectedOr.Values, x => x.Item1 == null);
        Assert.Contains(expectedOr.Values, x => x.Item2 == null);
        AssertRows(store, and, expectedAnd);
        AssertRows(store, or, expectedOr);
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AFieldJoinOverAnArrayProjectsTheBestDocumentOfAllTheValues()
    {
        using var store = Seed();
        var users = UserData();
        var games = GameData();

        // the document of a game is the best scoring one among those that share ANY of its values
        var rql = $"{Games} join ({QueryA}) as u on g.Tags = u.Tags select g.Name as Name, u.Name as UserName {Everything}";
        var expected = new Dictionary<string, (string, string)>();
        foreach (var game in games)
        {
            var u = Winner(QueryA, game.Tags, x => x.Tags, users);
            if (u != null)
                expected[game.Name] = (u.Name, null);
        }

        Assert.True(expected.Count > 40);
        Assert.Contains(games, g => g.Tags.Length > 1);
        AssertRows(store, rql, expected, compareDept: false);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TwoFieldJoinsOverTheSameArrayProjectEachItsOwnBestDocument()
    {
        using var store = Seed();
        var users = UserData();
        var games = GameData();

        var and = $"{Games} join ({QueryA}) as u on g.Tags = u.Tags join ({QueryX}) as d on g.Tags = d.Tags select g.Name as Name, u.Name as UserName, d.Name as DeptName {Everything}";
        var or = $"{Games} join ({QueryA}) as u on g.Tags = u.Tags join ({QueryB}) as d on g.Tags = d.Tags where matches(u) or matches(d) " +
                 $"select g.Name as Name, u.Name as UserName, d.Name as DeptName {Everything}";
        var expectedAnd = new Dictionary<string, (string, string)>();
        var expectedOr = new Dictionary<string, (string, string)>();
        foreach (var game in games)
        {
            var u = Winner(QueryA, game.Tags, x => x.Tags, users);
            var x2 = Winner(QueryX, game.Tags, x => x.Tags, users);
            var d = Winner(QueryB, game.Tags, x => x.Tags, users);
            if (u != null && x2 != null)
                expectedAnd[game.Name] = (u.Name, x2.Name);
            if (u != null || d != null)
                expectedOr[game.Name] = (u?.Name, d?.Name);
        }

        Assert.True(expectedAnd.Count > 40);
        AssertRows(store, and, expectedAnd);
        AssertRows(store, or, expectedOr);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AnArrayJoinAndASingleValuedJoinProjectTogether()
    {
        using var store = Seed();
        var users = UserData();
        var games = GameData();

        var rql = $"{Games} join ({QueryA}) as u on g.Tags = u.Tags join ({QueryX}) as d on g.Country = d.Country select g.Name as Name, u.Name as UserName, d.Name as DeptName {Everything}";
        var expected = new Dictionary<string, (string, string)>();
        foreach (var game in games)
        {
            var u = Winner(QueryA, game.Tags, x => x.Tags, users);
            var d = Winner(QueryX, One(game.Country), x => One(x.Country), users);
            if (u != null && d != null)
                expected[game.Name] = (u.Name, d.Name);
        }

        Assert.NotEmpty(expected);
        AssertRows(store, rql, expected);
    }

    private static void AssertRows(IDocumentStore store, string rql, Dictionary<string, (string User, string Dept)> expected, bool compareDept = true)
    {
        using var session = store.OpenSession();
        var rows = session.Advanced.RawQuery<Row>(rql).ToList();
        Assert.Equal(expected.Count, rows.Count);
        var mismatches = new List<string>();
        foreach (var row in rows)
        {
            if (expected.TryGetValue(row.Name, out var want) == false)
            {
                mismatches.Add($"{row.Name}: not expected");
                continue;
            }

            if (row.UserName != want.User || (compareDept && row.DeptName != want.Dept))
                mismatches.Add($"{row.Name}: got ({row.UserName}, {row.DeptName}) expected ({want.User}, {want.Dept})");
        }

        Assert.True(mismatches.Count == 0, rql + Environment.NewLine + string.Join(Environment.NewLine, mismatches.Take(15)));
    }

    private IDocumentStore Seed()
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        var store = GetDocumentStore(options);
        _store = store;
        new UsersIndex().Execute(store);
        new DeptsIndex().Execute(store);
        new GamesIndex().Execute(store);

        using (var bulk = store.BulkInsert())
        {
            foreach (var user in UserData())
                bulk.Store(user, user.Id);
            foreach (var dept in DeptData())
                bulk.Store(dept, dept.Id);
            foreach (var game in GameData())
                bulk.Store(game, game.Id);
        }

        Indexes.WaitForIndexing(store);
        return store;
    }

    private sealed class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Tier, u.Country, u.Region, u.Tags };
        }

        public override string IndexName => "Users";
    }

    private sealed class DeptsIndex : AbstractIndexCreationTask<Dept>
    {
        public DeptsIndex()
        {
            Map = depts => from d in depts select new { d.Name, d.Tag };
        }

        public override string IndexName => "Depts";
    }

    private sealed class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.Country, g.Region, g.Tags, g.DeptId };
        }

        public override string IndexName => "Games";
    }
}
