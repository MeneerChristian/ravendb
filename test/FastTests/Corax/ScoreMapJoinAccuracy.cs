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

// Proves the scores, the sorting and the paging of a JOIN with an independent oracle
public class ScoreMapJoinAccuracy(ITestOutputHelper output) : RavenTestBase(output)
{
    private const double Tolerance = 1e-5;
    private const string Everything = "limit 0, 1000000";

    private static readonly string[] UserWords = Enumerable.Range(0, 12).Select(i => $"w{i}").ToArray();
    private static readonly string[] GameWords = Enumerable.Range(0, 10).Select(i => $"x{i}").ToArray();

    private const string InnerSearch = "w0 w1 w2";
    private const string OuterSearch = "x0 x1";

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(1, 300, 900)]
    [InlineData(2, 300, 900)]
    [InlineData(3, 1000, 3000)]
    [InlineData(4, 50, 400)] // many games per user
    [InlineData(5, 2000, 500)] // few games per user, many users without games
    public void JoinScoresSortingAndPagingMatchTheOracle(int seed, int users, int games)
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        var data = new Data(seed, users, games);
        data.Store(scored);
        data.Store(unscored);
        Indexes.WaitForIndexing(scored);
        Indexes.WaitForIndexing(unscored);

        Verify(scored, unscored, $"seed {seed}");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoinStaysAccurateWhenTheJoinedDocumentsChange()
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        var data = new Data(seed: 7, users: 400, games: 1200);
        data.Store(scored);
        data.Store(unscored);
        Indexes.WaitForIndexing(scored);
        Indexes.WaitForIndexing(unscored);
        Verify(scored, unscored, "before the update");

        // make some matching users stop matching, some others match much better, and delete some games
        foreach (var store in new[] { scored, unscored })
        {
            using var session = store.OpenSession();
            var changed = session.Load<User>(Enumerable.Range(0, 40).Select(i => $"users/{i}"));
            for (var i = 0; i < 40; i++)
                changed[$"users/{i}"].Name = i % 2 == 0 ? "nothing relevant" : "w0 w1 w2 w0 w1 w2";

            for (var i = 0; i < 60; i++)
                session.Delete($"games/{i}");

            session.SaveChanges();
        }

        Indexes.WaitForIndexing(scored);
        Indexes.WaitForIndexing(unscored);
        Verify(scored, unscored, "after the update");
    }

    private void Verify(IDocumentStore scored, IDocumentStore unscored, string label)
    {
        // the oracle: both sides on their own
        var outer = Fetch<Game>(scored, $"from index 'Games' as g where search(g.Name, '{OuterSearch}') order by score() {Everything}");
        var inner = Fetch<User>(scored, $"from index 'Users' where search(Name, '{InnerSearch}') order by score() {Everything}");
        var innerScore = inner.ToDictionary(x => x.Id, x => x.Score);

        var expected = outer
            .Where(x => innerScore.ContainsKey(x.Entity.OwnerId))
            .ToDictionary(x => x.Id, x => x.Score + innerScore[x.Entity.OwnerId]);

        Assert.True(expected.Count > 30, $"[{label}] the probe needs enough joined rows to mean something, got {expected.Count}");
        Assert.True(expected.Count < outer.Count, $"[{label}] some games must have been dropped by the join, otherwise the filter is not tested");

        var joinQuery = $"from index 'Games' as g join (from index 'Users' where search(Name, '{InnerSearch}')) as u on g.OwnerId = id(u) where search(g.Name, '{OuterSearch}')";

        var best = Fetch<Game>(scored, $"{joinQuery} order by score() {Everything}");
        AssertSameRowsAndScores(best, expected, $"[{label}] order by score()");
        AssertOrdered(best.Select(x => x.Score).ToList(), descending: true, $"[{label}] order by score()");
        AssertScoreSequenceEquals(expected.Values.OrderByDescending(x => x).ToList(), best.Select(x => x.Score).ToList(), $"[{label}] order by score()");

        // weakest first (order by score() desc, RavenDB reverses the meaning)
        var weakest = Fetch<Game>(scored, $"{joinQuery} order by score() desc {Everything}");
        AssertSameRowsAndScores(weakest, expected, $"[{label}] order by score() desc");
        AssertScoreSequenceEquals(expected.Values.OrderBy(x => x).ToList(), weakest.Select(x => x.Score).ToList(), $"[{label}] order by score() desc");

        // paging: every page holds exactly the scores of the same slice of the full ordering
        var fullBest = expected.Values.OrderByDescending(x => x).ToList();
        foreach (var (skip, take) in new[] { (0, 20), (20, 20), (7, 5), (fullBest.Count - 3, 10), (fullBest.Count, 5) })
        {
            var page = Fetch<Game>(scored, $"{joinQuery} order by score() limit {skip}, {take}");
            AssertScoreSequenceEquals(fullBest.Skip(skip).Take(take).ToList(), page.Select(x => x.Score).ToList(), $"[{label}] page {skip},{take}");
            foreach (var row in page)
                Assert.Equal(expected[row.Id], row.Score, Tolerance);
        }

        // known positive: the sub-query really changes which games make the first page
        var outerTop = outer.Take(20).Select(x => x.Id).ToHashSet();
        var joinTop = best.Take(20).Select(x => x.Id).ToHashSet();
        Assert.True(joinTop.Except(outerTop).Any(), $"[{label}] the first page of the join must differ from the first page of the outer query alone, or the probe cannot tell them apart");

        // total hit count covers everything, whatever the page
        using (var session = scored.OpenSession())
        {
            session.Advanced.RawQuery<Game>($"{joinQuery} order by score() limit 0, 5").Statistics(out var stats).ToList();
            Assert.Equal(expected.Count, stats.TotalResults);
        }

        // scores are not returned unless requested, but they still decide the order
        var hidden = FetchWithoutTracking<Game>(unscored, $"{joinQuery} order by score() {Everything}");
        Assert.Equal(expected.Count, hidden.Count);
        Assert.All(hidden, row => Assert.False(row.HasScore, $"[{label}] the score must not be exposed when it was not requested"));
        Assert.All(hidden, row => Assert.True(expected.ContainsKey(row.Id), $"[{label}] unexpected row {row.Id}"));
        AssertScoreSequenceEquals(fullBest, hidden.Select(x => expected[x.Id]).ToList(), $"[{label}] order without exposed scores");

        // ordering by a field never depends on the scores, and the join is only a filter then
        var byRank = Fetch<Game>(unscored, $"{joinQuery} order by g.Rank as long {Everything}");
        Assert.Equal(expected.Keys.OrderBy(x => x).ToList(), byRank.Select(x => x.Id).OrderBy(x => x).ToList());
        var ranks = byRank.Select(x => x.Entity.Rank).ToList();
        Assert.Equal(ranks.OrderBy(x => x).ToList(), ranks);

        var projected = FetchRows(scored, $"{joinQuery} order by score() load g.OwnerId as owner select g.Id as Id, g.Name as Name, owner.Name as Owner {Everything}");
        Assert.Equal(best.Select(x => x.Id).ToList(), projected.Select(x => x.Id).ToList());
        var ownerNames = Fetch<User>(scored, $"from index 'Users' where search(Name, '{InnerSearch}') {Everything}").ToDictionary(x => x.Id, x => x.Entity.Name);
        foreach (var row in projected)
            Assert.Equal(ownerNames[best.Single(x => x.Id == row.Id).Entity.OwnerId], row.Owner);
    }

    private static void AssertSameRowsAndScores<T>(List<Row<T>> actual, Dictionary<string, double> expected, string label)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(actual.Count, actual.Select(x => x.Id).Distinct().Count()); // no duplicates
        Assert.Equal(expected.Keys.OrderBy(x => x).ToList(), actual.Select(x => x.Id).OrderBy(x => x).ToList());
        foreach (var row in actual)
        {
            var difference = Math.Abs(expected[row.Id] - row.Score);
            Assert.True(difference < Tolerance, $"{label}: {row.Id} expected score {expected[row.Id]} but got {row.Score}");
        }
    }

    private static void AssertOrdered(List<double> scores, bool descending, string label)
    {
        for (var i = 1; i < scores.Count; i++)
        {
            var ordered = descending ? scores[i - 1] >= scores[i] - Tolerance : scores[i - 1] <= scores[i] + Tolerance;
            Assert.True(ordered, $"{label}: position {i} breaks the order ({scores[i - 1]} then {scores[i]})");
        }
    }

    private static void AssertScoreSequenceEquals(List<double> expected, List<double> actual, string label)
    {
        Assert.True(expected.Count == actual.Count, $"{label}: expected {expected.Count} rows but got {actual.Count}");
        for (var i = 0; i < expected.Count; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) < Tolerance, $"{label}: position {i} should have score {expected[i]} but has {actual[i]}");
    }

    private sealed class Row<T>
    {
        public string Id;
        public double Score;
        public bool HasScore;
        public T Entity;
    }

    private static List<Row<T>> Fetch<T>(IDocumentStore store, string rql) => FetchWithoutTracking<T>(store, rql);

    // one session per query: a session hands the same tracked entity to every query that returns the same id
    private static List<Row<T>> FetchWithoutTracking<T>(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        var rows = new List<Row<T>>();
        foreach (var entity in session.Advanced.RawQuery<T>(rql).ToList())
        {
            var metadata = session.Advanced.GetMetadataFor(entity);
            var hasScore = metadata.ContainsKey(Constants.Documents.Metadata.IndexScore);
            rows.Add(new Row<T>
            {
                Id = (string)metadata[Constants.Documents.Metadata.Id],
                Score = hasScore ? Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore]) : double.NaN,
                HasScore = hasScore,
                Entity = entity
            });
        }

        return rows;
    }

    private sealed class ProjectedRow
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Owner { get; set; }
    }

    private static List<ProjectedRow> FetchRows(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<ProjectedRow>(rql).ToList();
    }

    private IDocumentStore CreateStore(bool includeScores)
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        if (includeScores)
            options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();

        var store = GetDocumentStore(options);
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);
        return store;
    }

    private sealed class Data
    {
        private readonly List<User> _users = new();
        private readonly List<Game> _games = new();

        public Data(int seed, int users, int games)
        {
            var random = new Random(seed);
            string Words(string[] vocabulary, int min, int max) =>
                string.Join(' ', Enumerable.Range(0, random.Next(min, max + 1)).Select(_ => vocabulary[random.Next(vocabulary.Length)]));

            for (var i = 0; i < users; i++)
                _users.Add(new User { Id = $"users/{i}", Name = Words(UserWords, 1, 6) });

            for (var i = 0; i < games; i++)
            {
                // some games point to a user that does not exist
                var owner = random.Next(25) == 0 ? $"users/missing{i}" : $"users/{random.Next(users)}";
                _games.Add(new Game { Id = $"games/{i}", Name = Words(GameWords, 1, 5), OwnerId = owner, Rank = random.Next(50) });
            }
        }

        public void Store(IDocumentStore store)
        {
            using (var bulk = store.BulkInsert())
            {
                foreach (var user in _users)
                    bulk.Store(user, user.Id);
                foreach (var game in _games)
                    bulk.Store(game, game.Id);
            }

        }
    }

    private class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    private class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public long Rank { get; set; }
    }

    private class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    private class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.Rank };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
