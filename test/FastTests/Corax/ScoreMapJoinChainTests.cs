using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// Several joins of the main document, chains of joins, sub-queries with their own where and join
public class ScoreMapJoinChainTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const double Tolerance = 1e-4;
    private const string Everything = "limit 0, 1000000";

    private const string UserSearch = "w0 w1 w2";
    private const string PublisherSearch = "p0 p1";
    private const string CompanySearch = "c0 c1";
    private const string GameSearch = "x0 x1";
    private const int MinimumLevel = 3;

    private const string Main = "from index 'Games' as g";
    private const string Where = $"where search(g.Name, '{GameSearch}')";
    private const string JoinUsers = $"join (from index 'Users' where search(Name, '{UserSearch}')) as u on g.OwnerId = id(u)";
    private const string JoinPublishers = $"join (from index 'Publishers' where search(Name, '{PublisherSearch}')) as p on g.PublisherId = id(p)";
    private const string JoinCompanies = $"join (from index 'Companies' where search(Name, '{CompanySearch}')) as c on u.CompanyId = id(c)";

    // the sub-query has its own where on a field and its own join
    private const string JoinUsersWithOwnWhereAndJoin =
        $"join (from index 'Users' as u {JoinCompanies} where search(Name, '{UserSearch}') and Level >= 3) as u on g.OwnerId = id(u)";


    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TwoIndependentJoinsGiveTheIntersectionAndTheSumOfAllThreeScores(int seed)
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed);

        var oracle = new Oracle(scored);
        var expected = oracle.Games
            .Where(x => oracle.Users.ContainsKey(x.Entity.OwnerId) && oracle.Publishers.ContainsKey(x.Entity.PublisherId))
            .ToDictionary(x => x.Id, x => x.Score + oracle.Users[x.Entity.OwnerId].Score + oracle.Publishers[x.Entity.PublisherId].Score);

        // the rows are the intersection: dropping either join must give strictly more rows
        Assert.True(expected.Count < oracle.Games.Count(x => oracle.Users.ContainsKey(x.Entity.OwnerId)));
        Assert.True(expected.Count < oracle.Games.Count(x => oracle.Publishers.ContainsKey(x.Entity.PublisherId)));

        Verify(scored, unscored, oracle, $"{Main} {JoinUsers} {JoinPublishers} {Where}", expected, $"users + publishers, seed {seed}");
        Verify(scored, unscored, oracle, $"{Main} {JoinPublishers} {JoinUsers} {Where}", expected, $"publishers + users, seed {seed}");
    }


    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AChainFiltersAndScoresThroughTheWholeChain(int seed)
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed);

        var oracle = new Oracle(scored);
        var expected = oracle.Games
            .Where(x => oracle.ChainScore(x.Entity.OwnerId) != null)
            .ToDictionary(x => x.Id, x => x.Score + oracle.ChainScore(x.Entity.OwnerId).Value);

        Assert.True(expected.Count < oracle.Games.Count(x => oracle.Users.ContainsKey(x.Entity.OwnerId)), "the company must filter users out");

        Verify(scored, unscored, oracle, $"{Main} {JoinUsers} {JoinCompanies} {Where}", expected, $"chain, seed {seed}");
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void LinqJoinExtensionWithTwoJoins()
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed: 6);

        var oracle = new Oracle(scored);
        var expected = ExpectedTwoJoins(oracle);

        using var session = scored.OpenSession();
        var users = session.Query<User, UsersIndex>().Search(x => x.Name, UserSearch);
        var publishers = session.Query<Publisher, PublishersIndex>().Search(x => x.Name, PublisherSearch);

        var results = session.Query<Game, GamesIndex>()
            .Search(x => x.Name, GameSearch)
            .Join(users, x => x.OwnerId)
            .Join(publishers, x => x.PublisherId)
            .OrderByScore()
            .ToList();

        AssertSameAsTheOracle(results, session.Advanced, expected, "LINQ Join()");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task LinqJoinExtensionWithTwoJoinsAsync()
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed: 6);

        var oracle = new Oracle(scored);
        var expected = ExpectedTwoJoins(oracle);

        using var session = scored.OpenAsyncSession();
        var users = session.Query<User, UsersIndex>().Search(x => x.Name, UserSearch);
        var publishers = session.Query<Publisher, PublishersIndex>().Search(x => x.Name, PublisherSearch);

        var results = await session.Query<Game, GamesIndex>()
            .Search(x => x.Name, GameSearch)
            .Join(users, x => x.OwnerId)
            .Join(publishers, x => x.PublisherId)
            .OrderByScore()
            .ToListAsync();

        AssertSameAsTheOracle(results, session.Advanced, expected, "async LINQ Join()");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void LinqQuerySyntaxWithTwoJoins()
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed: 6);

        var oracle = new Oracle(scored);
        var expected = ExpectedTwoJoins(oracle);

        using var session = scored.OpenSession();
        var users = session.Query<User, UsersIndex>().Search(x => x.Name, UserSearch);
        var publishers = session.Query<Publisher, PublishersIndex>().Search(x => x.Name, PublisherSearch);
        var games = session.Query<Game, GamesIndex>().Search(x => x.Name, GameSearch);

        var results = (from g in games
                       join u in users on g.OwnerId equals u.Id
                       join p in publishers on g.PublisherId equals p.Id
                       select g)
            .OrderByScore()
            .ToList();

        AssertSameAsTheOracle(results, session.Advanced, expected, "query syntax");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void LinqQuerySyntaxWithThreeJoinsOnlyKeepsWhatAllOfThemMatch()
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed: 6);

        var oracle = new Oracle(scored);
        var twoJoins = ExpectedTwoJoins(oracle).Keys.ToHashSet();

        using var session = scored.OpenSession();
        var users = session.Query<User, UsersIndex>().Search(x => x.Name, UserSearch);
        var publishers = session.Query<Publisher, PublishersIndex>().Search(x => x.Name, PublisherSearch);
        var sameCountry = session.Query<User, UsersIndex>().Search(x => x.Name, UserSearch);
        var games = session.Query<Game, GamesIndex>().Search(x => x.Name, GameSearch);

        // the third join keys on the main document through two levels of the compiler's transparent identifiers
        var results = (from g in games
                       join u in users on g.OwnerId equals u.Id
                       join p in publishers on g.PublisherId equals p.Id
                       join c in sameCountry on g.Country equals c.Country
                       select g)
            .OrderByScore()
            .ToList();

        Assert.NotEmpty(twoJoins);
        Assert.NotEmpty(results);
        Assert.All(results, game => Assert.Contains(game.Id, twoJoins));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task LinqQuerySyntaxWithTwoJoinsAsync()
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed: 6);

        var oracle = new Oracle(scored);
        var expected = ExpectedTwoJoins(oracle);

        using var session = scored.OpenAsyncSession();
        var users = session.Query<User, UsersIndex>().Search(x => x.Name, UserSearch);
        var publishers = session.Query<Publisher, PublishersIndex>().Search(x => x.Name, PublisherSearch);
        var games = session.Query<Game, GamesIndex>().Search(x => x.Name, GameSearch);

        var results = await (from g in games
                             join u in users on g.OwnerId equals u.Id
                             join p in publishers on g.PublisherId equals p.Id
                             select g)
            .OrderByScore()
            .ToListAsync();

        AssertSameAsTheOracle(results, session.Advanced, expected, "async query syntax");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task AsyncDocumentQueryWithTwoJoinsAndAChain()
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed: 7);

        var oracle = new Oracle(scored);
        var expected = oracle.Games
            .Where(x => oracle.ChainScore(x.Entity.OwnerId) != null && oracle.Publishers.ContainsKey(x.Entity.PublisherId))
            .ToDictionary(x => x.Id, x => x.Score + oracle.ChainScore(x.Entity.OwnerId).Value + oracle.Publishers[x.Entity.PublisherId].Score);
        Assert.True(expected.Count > 10);

        using var session = scored.OpenAsyncSession();
        var companies = session.Advanced.AsyncDocumentQuery<Company, CompaniesIndex>().Search(x => x.Name, CompanySearch);
        var users = session.Advanced.AsyncDocumentQuery<User, UsersIndex>().Search(x => x.Name, UserSearch).Join(companies, x => x.CompanyId);
        var publishers = session.Advanced.AsyncDocumentQuery<Publisher, PublishersIndex>().Search(x => x.Name, PublisherSearch);

        var results = await session.Advanced.AsyncDocumentQuery<Game, GamesIndex>()
            .Search(x => x.Name, GameSearch)
            .Join(users, x => x.OwnerId)
            .Join(publishers, x => x.PublisherId)
            .OrderByScore()
            .ToListAsync();

        AssertSameAsTheOracle(results, session.Advanced, expected, "async DocumentQuery");
    }



    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ASubQueryWithItsOwnWhereAndItsOwnJoinFiltersAndScoresLikeStandaloneQueries(int seed)
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed);

        var oracle = new Oracle(scored);

        // the user on its own (search + level), and the same user query joined with the company on its own
        var levelUsers = Fetch<User>(scored, $"from index 'Users' where search(Name, '{UserSearch}') and Level >= {MinimumLevel} order by score() {Everything}")
            .ToDictionary(x => x.Id);
        Assert.True(levelUsers.Count < oracle.Users.Count, "the level condition must filter users out");

        var joinedUsers = Fetch<User>(scored, $"from index 'Users' as u {JoinCompanies} where search(Name, '{UserSearch}') and Level >= {MinimumLevel} order by score() {Everything}")
            .ToDictionary(x => x.Id);
        Assert.True(joinedUsers.Count > 0 && joinedUsers.Count < levelUsers.Count, "the company join must filter the users again");

        var expected = oracle.Games
            .Where(x => joinedUsers.ContainsKey(x.Entity.OwnerId))
            .ToDictionary(x => x.Id, x => x.Score + joinedUsers[x.Entity.OwnerId].Score);

        // the standalone sub-query already includes the company score; check that against the pieces too
        foreach (var user in joinedUsers.Values)
            Assert.Equal(levelUsers[user.Id].Score + oracle.Companies[user.Entity.CompanyId].Score, user.Score, Tolerance);

        Verify(scored, unscored, oracle, $"{Main} {JoinUsersWithOwnWhereAndJoin} {Where}", expected, $"sub-query where + join, seed {seed}");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ASubQueryWithItsOwnWhereAndJoinTogetherWithAnotherJoin()
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed: 5);

        var oracle = new Oracle(scored);
        var joinedUsers = Fetch<User>(scored, $"from index 'Users' as u {JoinCompanies} where search(Name, '{UserSearch}') and Level >= {MinimumLevel} order by score() {Everything}")
            .ToDictionary(x => x.Id);

        var expected = oracle.Games
            .Where(x => joinedUsers.ContainsKey(x.Entity.OwnerId) && oracle.Publishers.ContainsKey(x.Entity.PublisherId))
            .ToDictionary(x => x.Id, x => x.Score + joinedUsers[x.Entity.OwnerId].Score + oracle.Publishers[x.Entity.PublisherId].Score);

        Verify(scored, unscored, oracle, $"{Main} {JoinUsersWithOwnWhereAndJoin} {JoinPublishers} {Where}", expected, "sub-query where + join, and a publisher join");
    }


    private static Dictionary<string, double> ExpectedTwoJoins(Oracle oracle)
    {
        var expected = oracle.Games
            .Where(x => oracle.Users.ContainsKey(x.Entity.OwnerId) && oracle.Publishers.ContainsKey(x.Entity.PublisherId))
            .ToDictionary(x => x.Id, x => x.Score + oracle.Users[x.Entity.OwnerId].Score + oracle.Publishers[x.Entity.PublisherId].Score);
        Assert.True(expected.Count > 10 && expected.Count < oracle.Games.Count);
        return expected;
    }

    private static void AssertSameAsTheOracle(List<Game> results, IAdvancedDocumentSessionOperations session, Dictionary<string, double> expected, string label)
    {
        Assert.Equal(expected.Count, results.Count);
        Assert.Equal(expected.Keys.OrderBy(x => x).ToList(), results.Select(x => x.Id).OrderBy(x => x).ToList());

        var scores = results.Select(x => Convert.ToDouble(session.GetMetadataFor(x)[Constants.Documents.Metadata.IndexScore])).ToList();
        for (var i = 0; i < results.Count; i++)
            Assert.True(Math.Abs(expected[results[i].Id] - scores[i]) < Tolerance, $"{label}: {results[i].Id} expected {expected[results[i].Id]} but got {scores[i]}");

        AssertScoreSequenceEquals(expected.Values.OrderByDescending(x => x).ToList(), scores, label);
    }

    private void Verify(IDocumentStore scored, IDocumentStore unscored, Oracle oracle, string joinQuery, Dictionary<string, double> expected, string label)
    {
        Assert.True(expected.Count > 10, $"[{label}] the probe needs enough joined rows to mean something, got {expected.Count}");
        Assert.True(expected.Count < oracle.Games.Count, $"[{label}] some games must have been dropped by the joins");

        var best = Fetch<Game>(scored, $"{joinQuery} order by score() {Everything}");
        AssertSameRowsAndScores(best, expected, $"[{label}] order by score()");
        var fullBest = expected.Values.OrderByDescending(x => x).ToList();
        AssertScoreSequenceEquals(fullBest, best.Select(x => x.Score).ToList(), $"[{label}] order by score()");

        var weakest = Fetch<Game>(scored, $"{joinQuery} order by score() desc {Everything}");
        AssertSameRowsAndScores(weakest, expected, $"[{label}] order by score() desc");
        AssertScoreSequenceEquals(expected.Values.OrderBy(x => x).ToList(), weakest.Select(x => x.Score).ToList(), $"[{label}] order by score() desc");

        foreach (var (skip, take) in new[] { (0, 10), (10, 10), (3, 4) })
        {
            var page = Fetch<Game>(scored, $"{joinQuery} order by score() limit {skip}, {take}");
            AssertScoreSequenceEquals(fullBest.Skip(skip).Take(take).ToList(), page.Select(x => x.Score).ToList(), $"[{label}] page {skip},{take}");
        }

        using (var session = scored.OpenSession())
        {
            session.Advanced.RawQuery<Game>($"{joinQuery} order by score() limit 0, 5").Statistics(out var stats).ToList();
            Assert.Equal(expected.Count, stats.TotalResults);
        }

        var hidden = Fetch<Game>(unscored, $"{joinQuery} order by score() {Everything}");
        Assert.Equal(expected.Count, hidden.Count);
        Assert.All(hidden, row => Assert.False(row.HasScore));
        AssertScoreSequenceEquals(fullBest, hidden.Select(x => expected[x.Id]).ToList(), $"[{label}] order without exposed scores");
    }

    private static void AssertSameRowsAndScores(List<Row<Game>> actual, Dictionary<string, double> expected, string label)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Keys.OrderBy(x => x).ToList(), actual.Select(x => x.Id).OrderBy(x => x).ToList());
        foreach (var row in actual)
            Assert.True(Math.Abs(expected[row.Id] - row.Score) < Tolerance, $"{label}: {row.Id} expected score {expected[row.Id]} but got {row.Score}");
    }

    private static void AssertScoreSequenceEquals(List<double> expected, List<double> actual, string label)
    {
        Assert.True(expected.Count == actual.Count, $"{label}: expected {expected.Count} rows but got {actual.Count}");
        for (var i = 0; i < expected.Count; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) < Tolerance, $"{label}: position {i} should have score {expected[i]} but has {actual[i]}");
    }

    private sealed class Oracle
    {
        public readonly List<Row<Game>> Games;
        public readonly Dictionary<string, Row<User>> Users;
        public readonly Dictionary<string, Row<Publisher>> Publishers;
        public readonly Dictionary<string, Row<Company>> Companies;

        public Oracle(IDocumentStore scored)
        {
            Games = Fetch<Game>(scored, $"from index 'Games' as g where search(g.Name, '{GameSearch}') order by score() {Everything}");
            Users = Fetch<User>(scored, $"from index 'Users' where search(Name, '{UserSearch}') order by score() {Everything}").ToDictionary(x => x.Id);
            Publishers = Fetch<Publisher>(scored, $"from index 'Publishers' where search(Name, '{PublisherSearch}') order by score() {Everything}").ToDictionary(x => x.Id);
            Companies = Fetch<Company>(scored, $"from index 'Companies' where search(Name, '{CompanySearch}') order by score() {Everything}").ToDictionary(x => x.Id);
        }

        public double? ChainScore(string ownerId) =>
            Users.TryGetValue(ownerId, out var user) && user.Entity.CompanyId != null && Companies.TryGetValue(user.Entity.CompanyId, out var company)
                ? user.Score + company.Score
                : null;
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

    private sealed class Row
    {
        public string Name { get; set; }
        public string Owner { get; set; }
        public string Company { get; set; }
    }


    private IDocumentStore CreateStore(bool includeScores)
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        if (includeScores)
            options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();

        var store = GetDocumentStore(options);
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);
        new PublishersIndex().Execute(store);
        new CompaniesIndex().Execute(store);
        return store;
    }

    private void Seed(IDocumentStore first, IDocumentStore second, int seed)
    {
        var random = new Random(seed);
        string Words(string prefix, int vocabulary, int min, int max) =>
            string.Join(' ', Enumerable.Range(0, random.Next(min, max + 1)).Select(_ => $"{prefix}{random.Next(vocabulary)}"));

        const int companies = 60, publishers = 40, users = 400, games = 1500;

        foreach (var store in new[] { first, second })
        {
            random = new Random(seed);
            using (var bulk = store.BulkInsert())
            {
                for (var i = 0; i < companies; i++)
                    bulk.Store(new Company { Id = $"companies/{i}", Name = Words("c", 8, 1, 5) }, $"companies/{i}");
                for (var i = 0; i < publishers; i++)
                    bulk.Store(new Publisher { Id = $"publishers/{i}", Name = Words("p", 8, 1, 5) }, $"publishers/{i}");
                for (var i = 0; i < users; i++)
                {
                    var company = random.Next(12) == 0 ? null : random.Next(15) == 0 ? $"companies/missing{i}" : $"companies/{random.Next(companies)}";
                    bulk.Store(new User { Id = $"users/{i}", Name = Words("w", 12, 1, 6), CompanyId = company, Country = $"k{random.Next(6)}", Level = random.Next(8) }, $"users/{i}");
                }

                for (var i = 0; i < games; i++)
                {
                    var owner = random.Next(25) == 0 ? $"users/missing{i}" : $"users/{random.Next(users)}";
                    var publisher = random.Next(25) == 0 ? $"publishers/missing{i}" : $"publishers/{random.Next(publishers)}";
                    bulk.Store(new Game { Id = $"games/{i}", Name = Words("x", 10, 1, 5), OwnerId = owner, PublisherId = publisher, Country = $"k{random.Next(6)}" }, $"games/{i}");
                }
            }

            Indexes.WaitForIndexing(store);
        }
    }

    private class Company
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    private class Publisher
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    private class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string CompanyId { get; set; }
        public string Country { get; set; }
        public long Level { get; set; }
    }

    private class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string PublisherId { get; set; }
        public string Country { get; set; }
    }

    private class CompaniesIndex : AbstractIndexCreationTask<Company>
    {
        public CompaniesIndex()
        {
            Map = companies => from c in companies select new { c.Name };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Companies";
    }

    private class PublishersIndex : AbstractIndexCreationTask<Publisher>
    {
        public PublishersIndex()
        {
            Map = publishers => from p in publishers select new { p.Name };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Publishers";
    }

    private class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.CompanyId, u.Country, u.Level };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    private class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.PublisherId, g.Country };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
