using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// Order by score() of a JOIN on realistic text
public class JoinScoreOrderTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const double Tolerance = 1e-4;
    private const string Everything = "limit 0, 1000000";
    private const int Vocabulary = 5000;
    private static readonly string[] Countries = ["nl", "de", "fr", "be", "uk", "es", "it"];

    // frequent enough to match a good share of the documents, rare enough to differ in document frequency
    private const string InnerSearch = "w2 w6 w15 w40 w120 w400";
    private const string OuterSearch = "w1 w4 w12 w30 w90 w300";

    private const string Games = "from index 'Games' as g";
    private const string OuterWhere = $"where search(g.Name, '{OuterSearch}')";
    private const string InnerQuery = $"from index 'Users' where search(Name, '{InnerSearch}')";

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ScoreOrderOfJoinsAt20k() => Run(users: 12_000, games: 20_000, seed: 20);

    [JoinBenchmarkFact("RAVEN_JOIN_SCORE_BIG", RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ScoreOrderOfJoinsAt200k() => Run(users: 200_000, games: 200_000, seed: 200);

    private void Run(int users, int games, int seed)
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        using var store = GetDocumentStore(options);
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);

        var generator = new Generator(seed);
        var userList = generator.Users(users);
        var gameList = generator.Games(games, users);
        using (var bulk = store.BulkInsert())
        {
            foreach (var user in userList)
                bulk.Store(user, user.Id);
            foreach (var game in gameList)
                bulk.Store(game, game.Id);
        }

        Indexes.WaitForIndexing(store, timeout: TimeSpan.FromMinutes(30));

        var outer = Fetch(store, $"{Games} {OuterWhere} order by score() {Everything}");
        var inner = Fetch(store, $"{InnerQuery} order by score() {Everything}");
        var report = new StringBuilder();
        report.AppendLine($"## {users} users, {games} games (seed {seed}); outer alone matches {outer.Count}, inner alone matches {inner.Count}");
        AppendStats(report, "outer score alone", outer.Select(x => x.Score).ToList(), 20);
        AppendStats(report, "inner score alone", inner.Select(x => x.Score).ToList(), 20);

        var innerById = inner.ToDictionary(x => x.Id, x => x.Score);

        var expectedById = outer
            .Where(x => x.OwnerId != null && innerById.ContainsKey(x.OwnerId))
            .ToDictionary(x => x.Id, x => x.Score + innerById[x.OwnerId]);
        VerifyJoin(store, $"{Games} join ({InnerQuery}) as u on g.OwnerId = id(u) {OuterWhere}", expectedById, "id join", report);

        // join on a low-cardinality field: the inner score is the best one of the country
        var bestByCountry = inner.GroupBy(x => x.Country).ToDictionary(x => x.Key, x => x.Max(u => u.Score));
        var expectedByCountry = outer
            .Where(x => bestByCountry.ContainsKey(x.Country))
            .ToDictionary(x => x.Id, x => x.Score + bestByCountry[x.Country]);
        VerifyJoin(store, $"{Games} join ({InnerQuery}) as u on g.Country = u.Country {OuterWhere}", expectedByCountry, "field join, Country (7 values)", report,
            innerValues: bestByCountry.Values.ToList());

        var bestByPostcode = inner.GroupBy(x => x.Postcode).ToDictionary(x => x.Key, x => x.Max(u => u.Score));
        var expectedByPostcode = outer
            .Where(x => bestByPostcode.ContainsKey(x.Postcode))
            .ToDictionary(x => x.Id, x => x.Score + bestByPostcode[x.Postcode]);
        VerifyJoin(store, $"{Games} join ({InnerQuery}) as u on g.Postcode = u.Postcode {OuterWhere}", expectedByPostcode, "field join, Postcode (10k values)", report,
            innerValues: bestByPostcode.Values.ToList());

        output.WriteLine(report.ToString());
        var path = Environment.GetEnvironmentVariable("RAVEN_JOIN_SCORE_REPORT");
        if (string.IsNullOrEmpty(path) == false)
            File.AppendAllText(path, report + Environment.NewLine);
    }

    private void VerifyJoin(IDocumentStore store, string joinQuery, Dictionary<string, double> expected, string label, StringBuilder report, List<double> innerValues = null)
    {
        Assert.True(expected.Count >= 2000, $"[{label}] the check needs at least 2000 joined rows, the oracle has {expected.Count}");

        var best = Fetch(store, $"{joinQuery} order by score() {Everything}");
        Assert.Equal(expected.Count, best.Count);
        Assert.Equal(best.Count, best.Select(x => x.Id).Distinct().Count());
        Assert.Equal(expected.Keys.OrderBy(x => x).ToList(), best.Select(x => x.Id).OrderBy(x => x).ToList());

        // the returned score of every row is outer + inner
        foreach (var row in best)
            Assert.True(Math.Abs(expected[row.Id] - row.Score) < Tolerance, $"[{label}] {row.Id}: oracle {expected[row.Id]} but the join returned {row.Score}");

        // best first: never increasing, and exactly the oracle's sorted sums at every position
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

        // pages: the first pages carry exactly the top of the oracle
        foreach (var (skip, take) in new[] { (0, 20), (20, 20), (1000, 10) })
        {
            var page = Fetch(store, $"{joinQuery} order by score() limit {skip}, {take}");
            Assert.Equal(take, page.Count);
            for (var i = 0; i < take; i++)
            {
                Assert.True(Math.Abs(oracleOrder[skip + i] - page[i].Score) < Tolerance, $"[{label}] page {skip},{take} position {i}: expected {oracleOrder[skip + i]} but got {page[i].Score}");
                Assert.True(Math.Abs(expected[page[i].Id] - page[i].Score) < Tolerance, $"[{label}] page {skip},{take}: {page[i].Id} has the wrong score");
            }
        }

        report.AppendLine($"### {label}: {expected.Count} joined rows, ordering, sums, paging: all match the oracle");
        if (innerValues != null)
            AppendStats(report, "inner score (best per field value, over the values)", innerValues, 20);
        AppendStats(report, "join score (outer + inner)", expected.Values.ToList(), 20);
    }

    private static void AppendStats(StringBuilder report, string name, List<double> scores, int top)
    {
        var sorted = scores.OrderBy(x => x).ToList();
        double Percentile(double p) => sorted[(int)Math.Round(p * (sorted.Count - 1))];

        var distinct = sorted.Select(x => Math.Round(x, 5)).Distinct().Count();
        var threshold = sorted[Math.Max(0, sorted.Count - top)];
        var tie = sorted.Count(x => Math.Abs(x - threshold) < 1e-5);
        var below = sorted.Count(x => x < threshold - 1e-5);
        report.AppendLine($"  {name}: n={sorted.Count} min={sorted[0]:F4} p50={Percentile(0.5):F4} p90={Percentile(0.9):F4} p99={Percentile(0.99):F4} max={sorted[^1]:F4} " +
                          $"distinct={distinct} top{top}-threshold={threshold:F4} tie-group-at-threshold={tie} rows-strictly-below-threshold={below} ({100.0 * below / sorted.Count:F2}%)");
    }

    private sealed class Row
    {
        public string Id;
        public double Score;
        public string OwnerId;
        public string Country;
        public string Postcode;
    }

    // one session per query: a session hands the same tracked entity to every query that returns the same id
    private static List<Row> Fetch(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        var rows = new List<Row>();
        foreach (var entity in session.Advanced.RawQuery<Doc>(rql).ToList())
        {
            var metadata = session.Advanced.GetMetadataFor(entity);
            Assert.True(metadata.ContainsKey(Constants.Documents.Metadata.IndexScore), "the score must be returned");
            rows.Add(new Row
            {
                Id = (string)metadata[Constants.Documents.Metadata.Id],
                Score = Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore]),
                OwnerId = entity.OwnerId,
                Country = entity.Country,
                Postcode = entity.Postcode
            });
        }

        return rows;
    }

    internal sealed class Generator
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

        // 1-12 words, skewed to short values; some values repeat one of their words
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

        public List<Game> Games(int count, int users) =>
            Enumerable.Range(0, count).Select(i => new Game
            {
                Id = $"games/{i}",
                Name = Text(),
                // some games point to a user that does not exist
                OwnerId = _random.Next(25) == 0 ? $"users/missing{i}" : $"users/{_random.Next(users)}",
                Country = Countries[_random.Next(Countries.Length)],
                Postcode = $"pc{_random.Next(10_000)}"
            }).ToList();
    }

    internal sealed class User : Doc;

    internal sealed class Game : Doc;

    internal class Doc
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string Country { get; set; }
        public string Postcode { get; set; }
    }

    internal sealed class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Country, u.Postcode };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    internal sealed class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.Country, g.Postcode };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
