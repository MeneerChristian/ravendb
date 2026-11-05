using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// Proves several joins and chains of joins with an independent oracle, like ScoreMapJoinAccuracy does for one join
public class ScoreMapJoinMultiAccuracy(ITestOutputHelper output) : RavenTestBase(output)
{
    private const double Tolerance = 1e-4;
    private const string Everything = "limit 0, 1000000";

    private const string UserSearch = "w0 w1 w2";
    private const string PublisherSearch = "p0 p1";
    private const string CompanySearch = "c0 c1";
    private const string GameSearch = "x0 x1";

    private const string Main = "from index 'Games' as g";
    private const string JoinUsers = $"join (from index 'Users' where search(Name, '{UserSearch}')) as u on g.OwnerId = id(u)";
    private const string JoinPublishers = $"join (from index 'Publishers' where search(Name, '{PublisherSearch}')) as p on g.PublisherId = id(p)";
    private const string JoinCompanies = $"join (from index 'Companies' where search(Name, '{CompanySearch}')) as c on u.CompanyId = id(c)";
    private const string Where = $"where search(g.Name, '{GameSearch}')";

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TwoJoinsOfTheMainDocumentAddBothScores(int seed)
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed);

        var oracle = new Oracle(scored);
        var expected = oracle.Games
            .Where(x => oracle.Users.ContainsKey(x.Entity.OwnerId) && oracle.Publishers.ContainsKey(x.Entity.PublisherId))
            .ToDictionary(x => x.Id, x => x.Score + oracle.Users[x.Entity.OwnerId].Score + oracle.Publishers[x.Entity.PublisherId].Score);

        Verify(scored, unscored, oracle, $"{Main} {JoinUsers} {JoinPublishers} {Where}", expected, $"two joins, seed {seed}");
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AJoinOnAJoinedDocumentAddsTheScoreOfTheWholeChain(int seed)
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed);

        var oracle = new Oracle(scored);
        double? Chain(string ownerId) =>
            oracle.Users.TryGetValue(ownerId, out var user) && user.Entity.CompanyId != null && oracle.Companies.TryGetValue(user.Entity.CompanyId, out var company)
                ? user.Score + company.Score
                : null;

        var expected = oracle.Games
            .Where(x => Chain(x.Entity.OwnerId) != null)
            .ToDictionary(x => x.Id, x => x.Score + Chain(x.Entity.OwnerId).Value);

        // written flat (the join on the company comes after the join on the user) ...
        Verify(scored, unscored, oracle, $"{Main} {JoinUsers} {JoinCompanies} {Where}", expected, $"chain, seed {seed}");

        // ... and nested, which is the same query
        var nested = $"{Main} join (from index 'Users' as u {JoinCompanies} where search(Name, '{UserSearch}')) as u on g.OwnerId = id(u) {Where}";
        Verify(scored, unscored, oracle, nested, expected, $"nested chain, seed {seed}");
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(1)]
    [InlineData(2)]
    public void ChainsAndSeveralJoinsTogether(int seed)
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed);

        var oracle = new Oracle(scored);
        double? Chain(string ownerId) =>
            oracle.Users.TryGetValue(ownerId, out var user) && user.Entity.CompanyId != null && oracle.Companies.TryGetValue(user.Entity.CompanyId, out var company)
                ? user.Score + company.Score
                : null;

        var expected = oracle.Games
            .Where(x => Chain(x.Entity.OwnerId) != null && oracle.Publishers.ContainsKey(x.Entity.PublisherId))
            .ToDictionary(x => x.Id, x => x.Score + Chain(x.Entity.OwnerId).Value + oracle.Publishers[x.Entity.PublisherId].Score);

        // the order the joins are written in does not matter
        Verify(scored, unscored, oracle, $"{Main} {JoinUsers} {JoinPublishers} {JoinCompanies} {Where}", expected, $"user, publisher, company, seed {seed}");
        Verify(scored, unscored, oracle, $"{Main} {JoinPublishers} {JoinUsers} {JoinCompanies} {Where}", expected, $"publisher, user, company, seed {seed}");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ClientWithSeveralJoinsAndAChainMatchesTheOracle()
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed: 6);

        var oracle = new Oracle(scored);
        double? Chain(string ownerId) =>
            oracle.Users.TryGetValue(ownerId, out var user) && user.Entity.CompanyId != null && oracle.Companies.TryGetValue(user.Entity.CompanyId, out var company)
                ? user.Score + company.Score
                : null;

        var expected = oracle.Games
            .Where(x => Chain(x.Entity.OwnerId) != null && oracle.Publishers.ContainsKey(x.Entity.PublisherId))
            .ToDictionary(x => x.Id, x => x.Score + Chain(x.Entity.OwnerId).Value + oracle.Publishers[x.Entity.PublisherId].Score);
        Assert.True(expected.Count > 25 && expected.Count < oracle.Games.Count);

        // DocumentQuery: a source query that is joined itself is a chain
        using (var session = scored.OpenSession())
        {
            var companies = session.Advanced.DocumentQuery<Company, CompaniesIndex>().Search(x => x.Name, CompanySearch);
            var users = session.Advanced.DocumentQuery<User, UsersIndex>().Search(x => x.Name, UserSearch).Join(companies, x => x.CompanyId);
            var publishers = session.Advanced.DocumentQuery<Publisher, PublishersIndex>().Search(x => x.Name, PublisherSearch);

            var query = session.Advanced.DocumentQuery<Game, GamesIndex>()
                .Search(x => x.Name, GameSearch)
                .Join(users, x => x.OwnerId)
                .Join(publishers, x => x.PublisherId)
                .OrderByScore();

            AssertSameAsTheOracle(session, query.ToList(), expected, "DocumentQuery");
        }

        using (var session = scored.OpenSession())
        {
            var companies = session.Query<Company, CompaniesIndex>().Search(x => x.Name, CompanySearch);
            var users = session.Query<User, UsersIndex>().Search(x => x.Name, UserSearch).Join(companies, x => x.CompanyId);
            var publishers = session.Query<Publisher, PublishersIndex>().Search(x => x.Name, PublisherSearch);

            var results = session.Query<Game, GamesIndex>()
                .Search(x => x.Name, GameSearch)
                .Join(users, x => x.OwnerId)
                .Join(publishers, x => x.PublisherId)
                .OrderByScore()
                .ToList();

            AssertSameAsTheOracle(session, results, expected, "LINQ");
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
    public void JoinedDocumentsCanBeProjectedIncludingTheOnesOfAChain()
    {
        using var scored = CreateStore(includeScores: true);
        using var unscored = CreateStore(includeScores: false);
        Seed(scored, unscored, seed: 5);

        var oracle = new Oracle(scored);
        var query = $"{Main} {JoinUsers} {JoinCompanies} {JoinPublishers} {Where} order by score()";

        var ids = Fetch<Game>(scored, $"{query} {Everything}").Select(x => x.Id).ToList();
        Assert.NotEmpty(ids);

        // the documents joined with the main document can be projected
        var rows = FetchRows(scored, $"{query} select g.Id as Id, g.Name as Name, u.Name as Owner, p.Name as Publisher {Everything}");

        Assert.Equal(ids, rows.Select(x => x.Id).ToList());
        var games = oracle.Games.ToDictionary(x => x.Id, x => x.Entity);
        foreach (var row in rows)
        {
            var game = games[row.Id];
            Assert.Equal(oracle.Users[game.OwnerId].Entity.Name, row.Owner);
            Assert.Equal(oracle.Publishers[game.PublisherId].Entity.Name, row.Publisher);
        }

        // 'c' is only reachable through 'u', alone and together with 'u'
        var chainRows = FetchRows(scored, $"{query} select g.Id as Id, c.Name as Company {Everything}");
        Assert.Equal(ids, chainRows.Select(x => x.Id).ToList());
        foreach (var row in chainRows)
        {
            var owner = oracle.Users[games[row.Id].OwnerId].Entity;
            Assert.Equal(oracle.Companies[owner.CompanyId].Entity.Name, row.Company);
        }

        var both = FetchRows(scored, $"{query} select g.Id as Id, u.Name as Owner, c.Name as Company {Everything}");
        Assert.Equal(ids, both.Select(x => x.Id).ToList());
        foreach (var row in both)
        {
            var owner = oracle.Users[games[row.Id].OwnerId].Entity;
            Assert.Equal(owner.Name, row.Owner);
            Assert.Equal(oracle.Companies[owner.CompanyId].Entity.Name, row.Company);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void EveryJoinReportsItsOwnStatisticsInTheTimings()
    {
        using var scored = CreateStore(includeScores: false);
        using var other = CreateStore(includeScores: false);
        Seed(scored, other, seed: 4);

        using var session = scored.OpenSession();
        session.Advanced.RawQuery<Game>($"{Main} {JoinUsers} {JoinCompanies} {JoinPublishers} {Where} order by score() include timings()")
            .Timings(out var timings).ToList();

        var join = timings.Timings["Join"];
        Assert.Equal(new[] { "p", "u" }, join.Timings.Keys.OrderBy(x => x).ToArray()); // the chained join 'c' belongs to 'u'

        // every join stops its clock when it has been read
        foreach (var alias in new[] { "u", "p" })
            Assert.True(join.Timings[alias].DurationInMs <= join.DurationInMs + 1, $"the timing of {alias} is longer than the timing of the joins together");
        Assert.True(join.Timings["u"].Timings["c"].DurationInMs <= join.Timings["u"].DurationInMs + 1);

        foreach (var alias in new[] { "u", "p" })
        {
            Assert.True(join.Timings[alias].Counters["SourceMatches"] > 0, alias);
            Assert.True(join.Timings[alias].Counters["ScanAllocatedManagedBytes"] > 0, alias);
            Assert.True(join.Timings[alias].Timings.ContainsKey("Scan"), alias);
        }

        // the scope of 'u' also holds the join of its sub-query
        var u = join.Timings["u"];
        Assert.True(u.Timings.ContainsKey("c"), "the chained join is read inside the sub-query of 'u'");
        Assert.True(u.Timings["c"].Counters["SourceMatches"] > 0);

        Assert.True(join.Counters["TotalAllocatedManagedBytes"] >= join.Timings["u"].Counters["ScanAllocatedManagedBytes"] + join.Timings["p"].Counters["ScanAllocatedManagedBytes"]);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task AChangeInTheLastIndexOfAChainInvalidatesTheCachedResult()
    {
        using var scored = CreateStore(includeScores: false);
        using var other = CreateStore(includeScores: false);
        Seed(scored, other, seed: 8);

        var rql = $"{Main} {JoinUsers} {JoinCompanies} {JoinPublishers} {Where} order by score() limit 0, 10";

        async Task<(HttpStatusCode Status, string ETag)> Post(string ifNoneMatch)
        {
            using var http = new System.Net.Http.HttpClient();
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"{scored.Urls[0]}/databases/{scored.Database}/queries")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { Query = rql, QueryParameters = new { } }), Encoding.UTF8, "application/json")
            };
            if (ifNoneMatch != null)
                request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

            using var response = await http.SendAsync(request);
            return (response.StatusCode, response.Headers.NonValidated.TryGetValues("ETag", out var values) ? values.First() : null);
        }

        var first = await Post(null);
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.NotModified, (await Post(first.ETag)).Status);

        // only the company index changes: it is the last index of the chain game -> user -> company
        using (var session = scored.OpenSession())
        {
            session.Load<Company>("companies/0").Name = "something else entirely";
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(scored);

        var afterCompanyChange = await Post(first.ETag);
        Assert.Equal(HttpStatusCode.OK, afterCompanyChange.Status);
        Assert.NotEqual(first.ETag, afterCompanyChange.ETag);

        // ... and so does the publishers index, the other join of the main document
        using (var session = scored.OpenSession())
        {
            session.Load<Publisher>("publishers/0").Name = "something else entirely";
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(scored);

        var afterPublisherChange = await Post(afterCompanyChange.ETag);
        Assert.Equal(HttpStatusCode.OK, afterPublisherChange.Status);
        Assert.NotEqual(afterCompanyChange.ETag, afterPublisherChange.ETag);
        Assert.Equal(HttpStatusCode.NotModified, (await Post(afterPublisherChange.ETag)).Status);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FacetsOverSeveralJoinsAndAChainMatchTheOracle(int seed)
    {
        using var scored = CreateStore(includeScores: false);
        using var other = CreateStore(includeScores: false);
        Seed(scored, other, seed);

        var oracle = new Oracle(scored);
        bool ChainMatches(string ownerId) =>
            oracle.Users.TryGetValue(ownerId, out var user) && user.Entity.CompanyId != null && oracle.Companies.ContainsKey(user.Entity.CompanyId);

        var joined = oracle.Games
            .Where(x => ChainMatches(x.Entity.OwnerId) && oracle.Publishers.ContainsKey(x.Entity.PublisherId))
            .ToList();
        Assert.True(joined.Count > 25 && joined.Count < oracle.Games.Count);

        var joinQuery = $"{Main} {JoinUsers} {JoinCompanies} {JoinPublishers} {Where}";

        async Task<Dictionary<string, int>> Facet(string rql, string field)
        {
            using var http = new System.Net.Http.HttpClient();
            using var response = await http.PostAsync($"{scored.Urls[0]}/databases/{scored.Database}/queries",
                new StringContent(JsonSerializer.Serialize(new { Query = rql, QueryParameters = new { } }), Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);

            using var document = System.Text.Json.JsonDocument.Parse(body);
            var facet = document.RootElement.GetProperty("Results").EnumerateArray().Single(x => x.GetProperty("Name").GetString() == field);
            return facet.GetProperty("Values").EnumerateArray().ToDictionary(x => x.GetProperty("Range").GetString(), x => x.GetProperty("Count").GetInt32());
        }

        // facet terms are lower cased by the default analyzer
        var byPublisher = joined.GroupBy(x => x.Entity.PublisherId.ToLowerInvariant()).ToDictionary(x => x.Key, x => x.Count());
        Assert.Equal(byPublisher.OrderBy(x => x.Key), (await Facet($"{joinQuery} select facet(PublisherId)", "PublisherId")).OrderBy(x => x.Key));

        var byOwner = joined.GroupBy(x => x.Entity.OwnerId.ToLowerInvariant()).ToDictionary(x => x.Key, x => x.Count());
        Assert.Equal(byOwner.OrderBy(x => x.Key), (await Facet($"{joinQuery} select facet(OwnerId)", "OwnerId")).OrderBy(x => x.Key));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheSameAliasTwiceIsRejected()
    {
        using var scored = CreateStore(includeScores: false);
        using var session = scored.OpenSession();

        var e = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<Game>($"{Main} {JoinUsers} {JoinUsers} {Where}").ToList());
        Assert.Contains("used more than once", e.ToString());
    }


    private void Verify(IDocumentStore scored, IDocumentStore unscored, Oracle oracle, string joinQuery, Dictionary<string, double> expected, string label)
    {
        Assert.True(expected.Count > 25, $"[{label}] the probe needs enough joined rows to mean something, got {expected.Count}");
        Assert.True(expected.Count < oracle.Games.Count, $"[{label}] some games must have been dropped by the joins, otherwise the filter is not tested");

        var best = Fetch<Game>(scored, $"{joinQuery} order by score() {Everything}");
        AssertSameRowsAndScores(best, expected, $"[{label}] order by score()");
        var fullBest = expected.Values.OrderByDescending(x => x).ToList();
        AssertScoreSequenceEquals(fullBest, best.Select(x => x.Score).ToList(), $"[{label}] order by score()");

        var weakest = Fetch<Game>(scored, $"{joinQuery} order by score() desc {Everything}");
        AssertSameRowsAndScores(weakest, expected, $"[{label}] order by score() desc");
        AssertScoreSequenceEquals(expected.Values.OrderBy(x => x).ToList(), weakest.Select(x => x.Score).ToList(), $"[{label}] order by score() desc");

        foreach (var (skip, take) in new[] { (0, 20), (20, 20), (7, 5), (fullBest.Count - 3, 10), (fullBest.Count, 5) })
        {
            var page = Fetch<Game>(scored, $"{joinQuery} order by score() limit {skip}, {take}");
            AssertScoreSequenceEquals(fullBest.Skip(skip).Take(take).ToList(), page.Select(x => x.Score).ToList(), $"[{label}] page {skip},{take}");
            foreach (var row in page)
                Assert.Equal(expected[row.Id], row.Score, Tolerance);
        }

        // known positive: the joins really change which games make the first page
        var outerTop = oracle.Games.Take(20).Select(x => x.Id).ToHashSet();
        Assert.True(best.Take(20).Select(x => x.Id).Except(outerTop).Any(), $"[{label}] the first page must differ from the first page of the outer query alone");

        using (var session = scored.OpenSession())
        {
            session.Advanced.RawQuery<Game>($"{joinQuery} order by score() limit 0, 5").Statistics(out var stats).ToList();
            Assert.Equal(expected.Count, stats.TotalResults);
        }

        // scores are not returned unless requested, but they still decide the order
        var hidden = Fetch<Game>(unscored, $"{joinQuery} order by score() {Everything}");
        Assert.Equal(expected.Count, hidden.Count);
        Assert.All(hidden, row => Assert.False(row.HasScore, $"[{label}] the score must not be exposed when it was not requested"));
        Assert.All(hidden, row => Assert.True(expected.ContainsKey(row.Id), $"[{label}] unexpected row {row.Id}"));
        AssertScoreSequenceEquals(fullBest, hidden.Select(x => expected[x.Id]).ToList(), $"[{label}] order without exposed scores");

        // ordering by a field: the joins are only filters then
        var byRank = Fetch<Game>(unscored, $"{joinQuery} order by g.Rank as long {Everything}");
        Assert.Equal(expected.Keys.OrderBy(x => x).ToList(), byRank.Select(x => x.Id).OrderBy(x => x).ToList());
        var ranks = byRank.Select(x => x.Entity.Rank).ToList();
        Assert.Equal(ranks.OrderBy(x => x).ToList(), ranks);
    }

    private static void AssertSameRowsAndScores(List<Row<Game>> actual, Dictionary<string, double> expected, string label)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(actual.Count, actual.Select(x => x.Id).Distinct().Count());
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

    // the oracle: every part on its own

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

    private sealed class ProjectedRow
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Owner { get; set; }
        public string Company { get; set; }
        public string Publisher { get; set; }
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
            // the same random sequence for both stores, so they hold exactly the same data
            random = new Random(seed);
            using (var bulk = store.BulkInsert())
            {
                for (var i = 0; i < companies; i++)
                    bulk.Store(new Company { Id = $"companies/{i}", Name = Words("c", 8, 1, 5) }, $"companies/{i}");
                for (var i = 0; i < publishers; i++)
                    bulk.Store(new Publisher { Id = $"publishers/{i}", Name = Words("p", 8, 1, 5) }, $"publishers/{i}");
                for (var i = 0; i < users; i++)
                {
                    // some users have no company, some point to a company that does not exist
                    var company = random.Next(12) == 0 ? null : random.Next(15) == 0 ? $"companies/missing{i}" : $"companies/{random.Next(companies)}";
                    bulk.Store(new User { Id = $"users/{i}", Name = Words("w", 12, 1, 6), CompanyId = company }, $"users/{i}");
                }

                for (var i = 0; i < games; i++)
                {
                    var owner = random.Next(25) == 0 ? $"users/missing{i}" : $"users/{random.Next(users)}";
                    var publisher = random.Next(25) == 0 ? $"publishers/missing{i}" : $"publishers/{random.Next(publishers)}";
                    bulk.Store(new Game { Id = $"games/{i}", Name = Words("x", 10, 1, 5), OwnerId = owner, PublisherId = publisher, Rank = random.Next(50) }, $"games/{i}");
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
    }

    private class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string PublisherId { get; set; }
        public long Rank { get; set; }
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
            Map = users => from u in users select new { u.Name, u.CompanyId };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    private class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.PublisherId, g.Rank };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
