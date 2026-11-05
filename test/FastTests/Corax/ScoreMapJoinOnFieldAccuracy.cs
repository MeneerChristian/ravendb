using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// Joins on a field of the joined index instead of its id, proven with an independent oracle
public class ScoreMapJoinOnFieldAccuracy(ITestOutputHelper output) : RavenTestBase(output)
{
    private const double Tolerance = 1e-4;
    private const string Everything = "limit 0, 1000000";

    private const string UserSearch = "w0 w1 w2";
    private const string CompanySearch = "c0 c1";
    private const string GameSearch = "x0 x1";

    private const string Main = "from index 'Games' as g";
    private const string Where = $"where search(g.Name, '{GameSearch}')";
    private const string UsersWhere = $"from index 'Users' where search(Name, '{UserSearch}')";

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void JoinOnAFieldTakesTheBestScoreOfTheDocumentsThatShareTheValue(int seed)
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed);
        var oracle = new Oracle(scored);

        // country values are written in upper case in the users and in lower case in the games
        var bestByCountry = oracle.Users.Values.GroupBy(x => x.Entity.Country.ToLowerInvariant()).ToDictionary(x => x.Key, x => x.Max(u => u.Score));
        var expected = oracle.Games
            .Where(x => x.Entity.Country != null && bestByCountry.ContainsKey(x.Entity.Country.ToLowerInvariant()))
            .ToDictionary(x => x.Id, x => x.Score + bestByCountry[x.Entity.Country.ToLowerInvariant()]);

        Verify(scored, unscored, oracle, $"{Main} join ({UsersWhere}) as u on g.Country = u.Country {Where}", expected, $"country, seed {seed}");

        // the field of the joined documents may be written first
        Verify(scored, unscored, oracle, $"{Main} join ({UsersWhere}) as u on u.Country = g.Country {Where}", expected, $"country, swapped, seed {seed}");
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(1)]
    [InlineData(2)]
    public void JoinOnAMultiValuedFieldMatchesAnyOfItsValues(int seed)
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed);
        var oracle = new Oracle(scored);

        var bestByTag = new Dictionary<string, double>();
        foreach (var user in oracle.Users.Values)
        foreach (var tag in user.Entity.Tags.Select(x => x.ToLowerInvariant()))
            bestByTag[tag] = bestByTag.TryGetValue(tag, out var current) ? Math.Max(current, user.Score) : user.Score;

        var expected = oracle.Games
            .Where(x => x.Entity.Tag != null && bestByTag.ContainsKey(x.Entity.Tag.ToLowerInvariant()))
            .ToDictionary(x => x.Id, x => x.Score + bestByTag[x.Entity.Tag.ToLowerInvariant()]);

        Verify(scored, unscored, oracle, $"{Main} join ({UsersWhere}) as u on g.Tag = u.Tags {Where}", expected, $"tags, seed {seed}");
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(1)]
    [InlineData(2)]
    public void JoinsOnTheIdOnAFieldAndInAChainAddTheirScores(int seed)
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed);
        var oracle = new Oracle(scored);

        // games -> users on the id, games -> users' companies through a chain
        var bestCompany = oracle.Companies.Values.GroupBy(x => x.Entity.Code.ToLowerInvariant()).ToDictionary(x => x.Key, x => x.Max(c => c.Score));
        var bestPublisher = oracle.Publishers.Values.GroupBy(x => x.Entity.Country.ToLowerInvariant()).ToDictionary(x => x.Key, x => x.Max(p => p.Score));

        double? Chain(string ownerId) =>
            oracle.Users.TryGetValue(ownerId, out var user) && user.Entity.CompanyCode != null && bestCompany.TryGetValue(user.Entity.CompanyCode.ToLowerInvariant(), out var company)
                ? user.Score + company
                : null;

        var expected = oracle.Games
            .Where(x => Chain(x.Entity.OwnerId) != null && x.Entity.Country != null && bestPublisher.ContainsKey(x.Entity.Country.ToLowerInvariant()))
            .ToDictionary(x => x.Id, x => x.Score + Chain(x.Entity.OwnerId).Value + bestPublisher[x.Entity.Country.ToLowerInvariant()]);

        var query = $"{Main} join ({UsersWhere}) as u on g.OwnerId = id(u) " +
                    $"join (from index 'Companies' where search(Name, '{CompanySearch}')) as c on u.CompanyCode = c.Code " +
                    $"join (from index 'Publishers') as p on g.Country = p.Country {Where}";

        Verify(scored, unscored, oracle, query, expected, $"id + chain on fields + field, seed {seed}");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ClientJoinsOnAFieldMatchTheOracle()
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed: 6);
        var oracle = new Oracle(scored);

        var bestByCountry = oracle.Users.Values.GroupBy(x => x.Entity.Country.ToLowerInvariant()).ToDictionary(x => x.Key, x => x.Max(u => u.Score));
        var expected = oracle.Games
            .Where(x => x.Entity.Country != null && bestByCountry.ContainsKey(x.Entity.Country.ToLowerInvariant()))
            .ToDictionary(x => x.Id, x => x.Score + bestByCountry[x.Entity.Country.ToLowerInvariant()]);
        Assert.True(expected.Count > 20 && expected.Count < oracle.Games.Count);

        using (var session = scored.OpenSession())
        {
            var users = session.Advanced.DocumentQuery<User, UsersIndex>().Search(x => x.Name, UserSearch);
            var results = session.Advanced.DocumentQuery<Game, GamesIndex>()
                .Search(x => x.Name, GameSearch)
                .Join(users, x => x.Country, u => u.Country)
                .OrderByScore()
                .ToList();

            AssertSameAsTheOracle(session, results, expected, "DocumentQuery");
        }

        using (var session = scored.OpenSession())
        {
            var users = session.Query<User, UsersIndex>().Search(x => x.Name, UserSearch);
            var results = session.Query<Game, GamesIndex>()
                .Search(x => x.Name, GameSearch)
                .Join(users, x => x.Country, u => u.Country)
                .OrderByScore()
                .ToList();

            AssertSameAsTheOracle(session, results, expected, "LINQ");
        }

        // LINQ query syntax: a property that is not the id joins on that field
        using (var session = scored.OpenSession())
        {
            var users = session.Query<User, UsersIndex>().Search(x => x.Name, UserSearch);
            var games = session.Query<Game, GamesIndex>().Search(x => x.Name, GameSearch);
            var results = (from g in games
                           join u in users on g.Country equals u.Country
                           select g)
                .OrderByScore()
                .ToList();

            AssertSameAsTheOracle(session, results, expected, "query syntax");
        }
    }

    private static void AssertSameAsTheOracle(IDocumentSession session, List<Game> results, Dictionary<string, double> expected, string label)
    {
        Assert.Equal(expected.Count, results.Count);
        Assert.Equal(expected.Keys.OrderBy(x => x).ToList(), results.Select(x => x.Id).OrderBy(x => x).ToList());

        var scores = results.Select(x => Convert.ToDouble(session.Advanced.GetMetadataFor(x)[Constants.Documents.Metadata.IndexScore])).ToList();
        for (var i = 0; i < results.Count; i++)
            Assert.True(Math.Abs(expected[results[i].Id] - scores[i]) < Tolerance, $"{label}: {results[i].Id} expected {expected[results[i].Id]} but got {scores[i]}");

        AssertScoreSequenceEquals(expected.Values.OrderByDescending(x => x).ToList(), scores, label);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void FieldsOfADocumentJoinedOnAFieldCanBeProjected()
    {
        using var scored = CreateStore(includeScores: false);
        using var other = CreateStore(includeScores: false);
        Seed(scored, other, seed: 4);

        using var session = scored.OpenSession();
        var games = session.Advanced.RawQuery<Game>($"{Main} join ({UsersWhere}) as u on g.Country = u.Country {Where} order by g.Name").ToList();
        var rows = session.Advanced.RawQuery<NameAndOwner>($"{Main} join ({UsersWhere}) as u on g.Country = u.Country {Where} order by g.Name select g.Name, u.Name as Owner").ToList();

        // the projection does not change which games are returned, and every one of them gets the name of one of the joined users
        Assert.NotEmpty(games);
        Assert.Equal(games.Count, rows.Count);
        Assert.All(rows, row => Assert.False(string.IsNullOrEmpty(row.Owner)));
    }

    private class NameAndOwner
    {
        public string Name { get; set; }
        public string Owner { get; set; }
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData("g.Nope = u.Country")]
    [InlineData("g.Nope = id(u)")]
    public void TheFieldOfTheMainDocumentHasToBeIndexedToo(string on)
    {
        using var scored = CreateStore(includeScores: false);
        using var other = CreateStore(includeScores: false);
        Seed(scored, other, seed: 4);

        using var session = scored.OpenSession();
        var e = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<Game>($"{Main} join ({UsersWhere}) as u on {on} {Where}").ToList());
        Assert.Contains("Field Nope not found in Index 'Games'", e.ToString());
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheJoinReadsTheIndexesAndNoDocuments()
    {
        using var scored = CreateStore(includeScores: false);
        using var other = CreateStore(includeScores: false);
        Seed(scored, other, seed: 4);

        using var session = scored.OpenSession();
        session.Advanced.RawQuery<Game>($"{Main} join ({UsersWhere}) as u on g.Country = u.Country join (from index 'Publishers') as p on g.Country = p.Country {Where} order by score() limit 0, 10 include timings()")
            .Timings(out var timings).ToList();

        // everything under the join is the scan of an index
        var scopes = new List<string>();
        void Collect(QueryTimings node)
        {
            foreach (var (name, child) in node.Timings ?? new Dictionary<string, QueryTimings>())
            {
                scopes.Add(name);
                Collect(child);
            }
        }

        Collect(timings.Timings["Join"]);

        Assert.Equal(new[] { "Scan", "Scan", "p", "u" }, scopes.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(scopes, x => x is "Storage" or "Load" or "Retriever" or "Projection");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoiningOnAFieldThatIsNotIndexedFailsClearly()
    {
        using var scored = CreateStore(includeScores: false);
        using var other = CreateStore(includeScores: false);
        Seed(scored, other, seed: 4);

        using var session = scored.OpenSession();
        var e = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<Game>($"{Main} join ({UsersWhere}) as u on g.Country = u.Nope {Where}").ToList());
        Assert.Contains("not indexed", e.ToString());
    }


    private void Verify(IDocumentStore scored, IDocumentStore unscored, Oracle oracle, string joinQuery, Dictionary<string, double> expected, string label)
    {
        Assert.True(expected.Count > 20, $"[{label}] the probe needs enough joined rows to mean something, got {expected.Count}");
        Assert.True(expected.Count < oracle.Games.Count, $"[{label}] some games must have been dropped by the join, otherwise the filter is not tested");

        var best = Fetch<Game>(scored, $"{joinQuery} order by score() {Everything}");
        Assert.Equal(expected.Count, best.Count);
        Assert.Equal(best.Count, best.Select(x => x.Id).Distinct().Count()); // a game matched by several joined documents appears once
        Assert.Equal(expected.Keys.OrderBy(x => x).ToList(), best.Select(x => x.Id).OrderBy(x => x).ToList());
        foreach (var row in best)
            Assert.True(Math.Abs(expected[row.Id] - row.Score) < Tolerance, $"[{label}] {row.Id} expected score {expected[row.Id]} but got {row.Score}");

        var fullBest = expected.Values.OrderByDescending(x => x).ToList();
        AssertScoreSequenceEquals(fullBest, best.Select(x => x.Score).ToList(), $"[{label}] order by score()");

        foreach (var (skip, take) in new[] { (0, 20), (20, 20), (7, 5), (fullBest.Count - 3, 10) })
        {
            var page = Fetch<Game>(scored, $"{joinQuery} order by score() limit {skip}, {take}");
            AssertScoreSequenceEquals(fullBest.Skip(skip).Take(take).ToList(), page.Select(x => x.Score).ToList(), $"[{label}] page {skip},{take}");
        }

        using (var session = scored.OpenSession())
        {
            session.Advanced.RawQuery<Game>($"{joinQuery} order by score() limit 0, 5").Statistics(out var stats).ToList();
            Assert.Equal(expected.Count, stats.TotalResults);
        }

        // without exposed scores the order is still decided by them, and ordering by a field only filters
        var hidden = Fetch<Game>(unscored, $"{joinQuery} order by score() {Everything}");
        Assert.Equal(expected.Count, hidden.Count);
        Assert.All(hidden, row => Assert.False(row.HasScore));
        AssertScoreSequenceEquals(fullBest, hidden.Select(x => expected[x.Id]).ToList(), $"[{label}] order without exposed scores");

        var byRank = Fetch<Game>(unscored, $"{joinQuery} order by g.Rank as long {Everything}");
        Assert.Equal(expected.Keys.OrderBy(x => x).ToList(), byRank.Select(x => x.Id).OrderBy(x => x).ToList());
    }

    private static void AssertScoreSequenceEquals(List<double> expected, List<double> actual, string label)
    {
        Assert.True(expected.Count == actual.Count, $"{label}: expected {expected.Count} rows but got {actual.Count}");
        for (var i = 0; i < expected.Count; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) < Tolerance, $"{label}: position {i} should have score {expected[i]} but has {actual[i]}");
    }

    // the oracle: every part on its own

    private sealed class Oracle
    {
        public readonly List<Row<Game>> Games;
        public readonly Dictionary<string, Row<User>> Users;
        public readonly Dictionary<string, Row<Company>> Companies;
        public readonly Dictionary<string, Row<Publisher>> Publishers;

        public Oracle(IDocumentStore scored)
        {
            Games = Fetch<Game>(scored, $"from index 'Games' as g where search(g.Name, '{GameSearch}') order by score() {Everything}");
            Users = Fetch<User>(scored, $"{UsersWhere} order by score() {Everything}").ToDictionary(x => x.Id);
            Companies = Fetch<Company>(scored, $"from index 'Companies' where search(Name, '{CompanySearch}') order by score() {Everything}").ToDictionary(x => x.Id);
            Publishers = Fetch<Publisher>(scored, $"from index 'Publishers' order by score() {Everything}").ToDictionary(x => x.Id);
        }
    }

    private sealed class Row<T>
    {
        public string Id;
        public double Score;
        public bool HasScore;
        public T Entity;
    }

    // one session per query: a session hands the same tracked entity to every query that returns the same id
    private static List<Row<T>> Fetch<T>(IDocumentStore store, string rql)
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


    private IDocumentStore CreateStore(bool includeScores)
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        if (includeScores)
            options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();

        var store = GetDocumentStore(options);
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);
        new CompaniesIndex().Execute(store);
        new PublishersIndex().Execute(store);
        return store;
    }

    private void Seed(IDocumentStore first, IDocumentStore second, int seed)
    {
        var random = new Random(seed);
        string Words(string prefix, int vocabulary, int min, int max) =>
            string.Join(' ', Enumerable.Range(0, random.Next(min, max + 1)).Select(_ => $"{prefix}{random.Next(vocabulary)}"));

        // 14 countries, 40 codes: some values exist only on one side, so some games find no match
        string CountryOf(int n) => $"c{n:00}";
        const int countries = 14, codes = 40, companies = 60, publishers = 30, users = 400, games = 1500;

        foreach (var store in new[] { first, second })
        {
            random = new Random(seed); // the same random sequence for both stores, so they hold exactly the same data
            using (var bulk = store.BulkInsert())
            {
                for (var i = 0; i < companies; i++)
                    bulk.Store(new Company { Id = $"companies/{i}", Name = Words("c", 8, 1, 5), Code = $"K{random.Next(codes)}" }, $"companies/{i}");

                for (var i = 0; i < publishers; i++)
                    bulk.Store(new Publisher { Id = $"publishers/{i}", Country = CountryOf(random.Next(countries - 4)).ToUpperInvariant() }, $"publishers/{i}");

                for (var i = 0; i < users; i++)
                {
                    var tags = Enumerable.Range(0, random.Next(1, 4)).Select(_ => $"T{random.Next(25)}").ToList();
                    bulk.Store(new User
                    {
                        Id = $"users/{i}",
                        Name = Words("w", 12, 1, 6),
                        Country = CountryOf(random.Next(countries - 2)).ToUpperInvariant(), // upper case in the users
                        Tags = tags,
                        CompanyCode = random.Next(10) == 0 ? null : $"K{random.Next(codes)}"
                    }, $"users/{i}");
                }

                for (var i = 0; i < games; i++)
                {
                    bulk.Store(new Game
                    {
                        Id = $"games/{i}",
                        Name = Words("x", 10, 1, 5),
                        OwnerId = random.Next(25) == 0 ? $"users/missing{i}" : $"users/{random.Next(users)}",
                        Country = random.Next(20) == 0 ? null : CountryOf(random.Next(countries)), // lower case in the games, some values no user has
                        Tag = random.Next(15) == 0 ? null : $"t{random.Next(30)}", // some tags no user has
                        Rank = random.Next(50)
                    }, $"games/{i}");
                }
            }

            Indexes.WaitForIndexing(store);
        }
    }

    private class Company
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Code { get; set; }
    }

    private class Publisher
    {
        public string Id { get; set; }
        public string Country { get; set; }
    }

    private class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Country { get; set; }
        public List<string> Tags { get; set; }
        public string CompanyCode { get; set; }
    }

    private class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string Country { get; set; }
        public string Tag { get; set; }
        public long Rank { get; set; }
    }

    private class CompaniesIndex : AbstractIndexCreationTask<Company>
    {
        public CompaniesIndex()
        {
            Map = companies => from c in companies select new { c.Name, c.Code };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Companies";
    }

    private class PublishersIndex : AbstractIndexCreationTask<Publisher>
    {
        public PublishersIndex()
        {
            Map = publishers => from p in publishers select new { p.Country };
        }

        public override string IndexName => "Publishers";
    }

    private class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Country, u.Tags, u.CompanyCode };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    private class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.Country, g.Tag, g.Rank };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
