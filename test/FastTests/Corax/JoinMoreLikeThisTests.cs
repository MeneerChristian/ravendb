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

// A JOIN acts on the result of a more-like-this condition like it acts on any other where match
public class JoinMoreLikeThisTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Everything = "limit 0, 1000000";
    private const string MltOptions = "'{\"Fields\":[\"Title\"],\"MinimumTermFrequency\":1,\"MinimumDocumentFrequency\":1}'";
    private const string Mlt = "morelikethis(id() = 'jobs/0', " + MltOptions + ")";
    private const string Main = "from index 'MltJobs' as j";
    private const string JoinC = "join (from index 'MltCompanies' where Country = 'nl') as c on j.CompanyId = id(c)";

    private sealed class Job { public string Id; public string CompanyId; public string Title; public long N; }
    private sealed class Company { public string Id; public string Country; public string Name; public string Notes; }
    private sealed class Row { public string Id; public string Name; public string CompanyName; }

    private sealed class JobsIndex : AbstractIndexCreationTask<Job>
    {
        public override string IndexName => "MltJobs";
        public JobsIndex() { Map = docs => from d in docs select new { d.CompanyId, d.Title, d.N }; Index(x => x.Title, FieldIndexing.Search); }
    }

    private sealed class CompaniesIndex : AbstractIndexCreationTask<Company>
    {
        public override string IndexName => "MltCompanies";
        public CompaniesIndex() { Map = docs => from d in docs select new { d.Country, d.Name, d.Notes }; Index(x => x.Notes, FieldIndexing.Search); }
    }

    private sealed class Data
    {
        public readonly Dictionary<string, Job> Jobs = new();
        public readonly Dictionary<string, Company> Companies = new();
    }

    private IDocumentStore CreateStore(out Data data)
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        var store = GetDocumentStore(options);
        new JobsIndex().Execute(store);
        new CompaniesIndex().Execute(store);

        var random = new Random(5);
        string[] countries = ["nl", "de", "fr"], words = ["java", "dotnet", "sql", "cloud", "react", "devops", "data", "mobile"], notes = ["fast", "cheap", "good"];
        data = new Data();
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < 30; i++)
            {
                var company = new Company { Id = $"companies/{i}", Country = countries[random.Next(3)], Name = "company" + i, Notes = notes[random.Next(3)] };
                data.Companies[company.Id] = company;
                bulk.Store(company, company.Id);
            }

            for (var i = 0; i < 120; i++)
            {
                var title = string.Join(' ', Enumerable.Range(0, random.Next(2, 6)).Select(_ => words[random.Next(words.Length)]));
                var job = new Job { Id = $"jobs/{i}", N = i, Title = i == 0 ? "java dotnet sql cloud" : title, CompanyId = random.Next(8) == 0 ? null : $"companies/{random.Next(32)}" };
                data.Jobs[job.Id] = job;
                bulk.Store(job, job.Id);
            }
        }

        Indexes.WaitForIndexing(store);
        return store;
    }

    private static List<(string Id, double Score)> Rows(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        var result = new List<(string, double)>();
        foreach (var entity in session.Advanced.RawQuery<Job>(rql).ToList())
        {
            var metadata = session.Advanced.GetMetadataFor(entity);
            result.Add((entity.Id, Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore])));
        }

        return result;
    }

    private static Dictionary<string, double> Scores(IDocumentStore store, string rql) => Rows(store, rql).ToDictionary(x => x.Id, x => x.Score);

    private static void AssertSameScores(Dictionary<string, double> expected, List<(string Id, double Score)> actual, string name)
    {
        Assert.True(expected.Count > 0 && expected.Count < 120, $"[{name}] the data does not tell the rows apart ({expected.Count})");
        Assert.True(expected.Keys.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(actual.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal)),
            $"[{name}] expected {expected.Count} rows, got {actual.Count}: missing {string.Join(",", expected.Keys.Except(actual.Select(x => x.Id)).Take(5))}, extra {string.Join(",", actual.Select(x => x.Id).Except(expected.Keys).Take(5))}");
        foreach (var (id, score) in actual)
            Assert.True(Math.Abs(expected[id] - score) < 0.001, $"[{name}] {id}: expected score {expected[id]}, got {score}");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void InnerJoinFiltersTheMoreLikeThisResult()
    {
        using var store = CreateStore(out var d);
        var plain = Scores(store, $"{Main} where {Mlt} {Everything}");
        var nl = d.Companies.Values.Where(c => c.Country == "nl").Select(c => c.Id).ToHashSet();

        var companyScores = new Dictionary<string, double>();
        using (var session = store.OpenSession())
            foreach (var company in session.Advanced.RawQuery<Company>("from index 'MltCompanies' where Country = 'nl' order by score() limit 0, 1000").ToList())
                companyScores[company.Id] = Convert.ToDouble(session.Advanced.GetMetadataFor(company)[Constants.Documents.Metadata.IndexScore]);

        var expected = plain.Where(x => d.Jobs[x.Key].CompanyId is { } id && nl.Contains(id)).ToDictionary(x => x.Key, x => x.Value + companyScores[d.Jobs[x.Key].CompanyId]);
        Assert.True(expected.Count < plain.Count);

        var rows = Rows(store, $"{Main} {JoinC} where {Mlt} {Everything}");
        // the score is the more-like-this score plus the score of the joined document, in the order of the score
        AssertSameScores(expected, rows, "inner join");
        Assert.True(rows.Zip(rows.Skip(1)).All(p => p.First.Score >= p.Second.Score - 0.0001));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ScoreIsTheMoreLikeThisScorePlusTheJoinedScore()
    {
        using var store = CreateStore(out var d);
        const string joinS = "join (from index 'MltCompanies' where search(Notes, 'fast good')) as c on j.CompanyId = id(c)";
        var plain = Scores(store, $"{Main} where {Mlt} {Everything}");
        var companyScores = new Dictionary<string, double>();
        using (var session = store.OpenSession())
            foreach (var company in session.Advanced.RawQuery<Company>("from index 'MltCompanies' where search(Notes, 'fast good') order by score() limit 0, 1000").ToList())
                companyScores[company.Id] = Convert.ToDouble(session.Advanced.GetMetadataFor(company)[Constants.Documents.Metadata.IndexScore]);

        var expected = plain.Where(x => d.Jobs[x.Key].CompanyId is { } id && companyScores.ContainsKey(id)).ToDictionary(x => x.Key, x => x.Value + companyScores[d.Jobs[x.Key].CompanyId]);
        AssertSameScores(expected, Rows(store, $"{Main} {joinS} where {Mlt} order by score() {Everything}"), "join score");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void LeftJoinKeepsEveryMoreLikeThisRow()
    {
        using var store = CreateStore(out var d);
        var plain = Rows(store, $"{Main} where {Mlt} {Everything}");
        var left = Rows(store, $"{Main} left join (from index 'MltCompanies' where Country = 'nl') as c on j.CompanyId = id(c) where {Mlt} {Everything}");
        Assert.Equal(plain.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal), left.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));

        using var session = store.OpenSession();
        var projected = session.Advanced.RawQuery<Row>($"{Main} left join (from index 'MltCompanies' where Country = 'nl') as c on j.CompanyId = id(c) where {Mlt} select id() as Id, c.Name as CompanyName {Everything}").ToList();
        Assert.Equal(plain.Count, projected.Count);
        foreach (var row in projected)
        {
            var company = d.Jobs[row.Id].CompanyId is { } id && d.Companies.TryGetValue(id, out var c) && c.Country == "nl" ? c : null;
            Assert.Equal(company?.Name, row.CompanyName);
        }

        Assert.Contains(projected, x => x.CompanyName == null);
        Assert.Contains(projected, x => x.CompanyName != null);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AliasConditionsInTheOuterWhere()
    {
        using var store = CreateStore(out var d);
        var plain = Scores(store, $"{Main} where {Mlt} {Everything}");
        Company Of(string jobId) => d.Jobs[jobId].CompanyId is { } id && d.Companies.TryGetValue(id, out var c) ? c : null;

        void Check(string name, string where, Func<string, Company, bool> keep)
        {
            var expected = plain.Where(x => keep(x.Key, Of(x.Key))).Select(x => x.Key).ToHashSet();
            var actual = Rows(store, $"{Main} {JoinC} where ({Mlt}) and ({where}) {Everything}").Select(x => x.Id).ToHashSet();
            Assert.True(expected.Count > 0, name);
            Assert.True(expected.SetEquals(actual), $"[{name}] expected {expected.Count}, got {actual.Count}: missing {string.Join(',', expected.Except(actual).Select(x => x + '/' + d.Jobs[x].CompanyId))}, extra {string.Join(',', actual.Except(expected).Select(x => x + '/' + d.Jobs[x].CompanyId))}");
        }

        Check("alias condition", "c.Name = 'company1' or c.Name = 'company2' or c.Name = 'company3' or c.Name = 'company4' or c.Name = 'company5' or c.Name = 'company6' or c.Name = 'company7' or c.Name = 'company8'",
            (_, c) => c is { Country: "nl" } && int.Parse(c.Name.Substring(7)) is >= 1 and <= 8);
        Check("matches", "matches(c) and j.N > 30", (id, c) => c is { Country: "nl" } && d.Jobs[id].N > 30);
        Check("outer condition", "j.N > 60", (id, c) => c is { Country: "nl" } && d.Jobs[id].N > 60);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void PagingAndTotalResults()
    {
        using var store = CreateStore(out _);
        var all = Rows(store, $"{Main} {JoinC} where {Mlt} order by score() {Everything}").Select(x => x.Id).ToList();
        Assert.True(all.Count > 6);

        using var session = store.OpenSession();
        var paged = new List<string>();
        for (var start = 0; start < all.Count; start += 4)
        {
            var page = session.Advanced.RawQuery<Job>($"{Main} {JoinC} where {Mlt} order by score() limit {start}, 4").Statistics(out var stats).ToList();
            // a more-like-this query reports the rows it returned, with or without the join
            Assert.Equal(page.Count, stats.TotalResults);
            paged.AddRange(page.Select(x => x.Id));
        }

        Assert.Equal(all, paged);

        var everything = session.Advanced.RawQuery<Job>($"{Main} {JoinC} where {Mlt} {Everything}").Statistics(out var total).ToList();
        Assert.Equal(all.Count, everything.Count);
        Assert.Equal(all.Count, total.TotalResults);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByAliasField()
    {
        using var store = CreateStore(out var d);
        var plain = Scores(store, $"{Main} where {Mlt} {Everything}");
        var expected = plain.Keys.Where(id => d.Jobs[id].CompanyId is { } c && d.Companies.TryGetValue(c, out var company) && company.Country == "nl")
            .OrderBy(id => d.Companies[d.Jobs[id].CompanyId].Name, StringComparer.Ordinal).ThenBy(id => id, StringComparer.Ordinal).ToList();

        var actual = Rows(store, $"{Main} {JoinC} where {Mlt} order by c.Name, id() {Everything}").Select(x => x.Id).ToList();
        Assert.Equal(expected, actual);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByMainFieldAndScore()
    {
        using var store = CreateStore(out var d);
        var plain = Scores(store, $"{Main} where {Mlt} {Everything}");
        var kept = plain.Keys.Where(id => d.Jobs[id].CompanyId is { } c && d.Companies.TryGetValue(c, out var company) && company.Country == "nl").ToList();

        var byField = Rows(store, $"{Main} {JoinC} where {Mlt} order by j.N as long desc {Everything}").Select(x => x.Id).ToList();
        Assert.Equal(kept.OrderByDescending(id => d.Jobs[id].N).ToList(), byField);

        // order by score() is the best match first, score() desc the worst first
        var best = Rows(store, $"{Main} {JoinC} where {Mlt} order by score() {Everything}");
        var worst = Rows(store, $"{Main} {JoinC} where {Mlt} order by score() desc {Everything}");
        Assert.Equal(kept.Count, best.Count);
        Assert.Equal(kept.Count, worst.Count);
        Assert.True(best.Zip(best.Skip(1)).All(p => p.First.Score >= p.Second.Score - 0.0001), "score() is by descending score");
        Assert.True(worst.Zip(worst.Skip(1)).All(p => p.First.Score <= p.Second.Score + 0.0001), "score() desc is by ascending score");
        // the default order of a more-like-this query is the best match first
        var defaultOrder = Rows(store, $"{Main} {JoinC} where {Mlt} {Everything}");
        Assert.True(defaultOrder.Zip(defaultOrder.Skip(1)).All(p => p.First.Score >= p.Second.Score - 0.0001));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void LeftJoinOrderedByAliasFieldPutsRowsWithoutJoinedDocumentLast()
    {
        using var store = CreateStore(out var d);
        var plain = Scores(store, $"{Main} where {Mlt} {Everything}");
        Company Of(string jobId) => d.Jobs[jobId].CompanyId is { } id && d.Companies.TryGetValue(id, out var c) && c.Country == "nl" ? c : null;

        var rows = Rows(store, $"{Main} left join (from index 'MltCompanies' where Country = 'nl') as c on j.CompanyId = id(c) where {Mlt} order by c.Name {Everything}").Select(x => x.Id).ToList();
        Assert.Equal(plain.Count, rows.Count);
        var matched = rows.Where(id => Of(id) != null).ToList();
        Assert.True(matched.Count > 0 && matched.Count < rows.Count);
        Assert.Equal(matched.OrderBy(id => Of(id).Name, StringComparer.Ordinal).Select(id => Of(id).Name), matched.Select(id => Of(id).Name));
        Assert.True(rows.Take(matched.Count).All(id => Of(id) != null), "the rows that have a joined document come first");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void BoostScalesTheJoinedScore()
    {
        using var store = CreateStore(out var d);
        var plain = Scores(store, $"{Main} where {Mlt} {Everything}");
        var companyScores = new Dictionary<string, double>();
        using (var session = store.OpenSession())
            foreach (var company in session.Advanced.RawQuery<Company>("from index 'MltCompanies' where search(Notes, 'fast good') order by score() limit 0, 1000").ToList())
                companyScores[company.Id] = Convert.ToDouble(session.Advanced.GetMetadataFor(company)[Constants.Documents.Metadata.IndexScore]);

        var expected = plain.Where(x => d.Jobs[x.Key].CompanyId is { } id && companyScores.ContainsKey(id)).ToDictionary(x => x.Key, x => x.Value + 2 * companyScores[d.Jobs[x.Key].CompanyId]);
        var actual = Rows(store, $"{Main} join (from index 'MltCompanies' where search(Notes, 'fast good')) as c on j.CompanyId = id(c) boost 2 where {Mlt} order by score() {Everything}");
        AssertSameScores(expected, actual, "boost");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void MoreLikeThisOfAJsonDocument()
    {
        using var store = CreateStore(out var d);
        const string json = "morelikethis('{\"Title\":\"cloud\"}', " + MltOptions + ")";
        var plain = Scores(store, $"{Main} where {json} {Everything}");
        var nl = d.Companies.Values.Where(c => c.Country == "nl").Select(c => c.Id).ToHashSet();
        var expected = plain.Where(x => d.Jobs[x.Key].CompanyId is { } id && nl.Contains(id)).Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(expected.Count > 0 && expected.Count < plain.Count);

        var rows = Rows(store, $"{Main} {JoinC} where {json} {Everything}").Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, rows);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void MoreLikeThisFilterOnTheJoinAliasIsRejected()
    {
        using var store = CreateStore(out _);
        using var session = store.OpenSession();
        var e = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<Job>($"{Main} {JoinC} where morelikethis(c.Name = 'company1', {MltOptions}) {Everything}").ToList());
        Assert.Contains("morelikethis() is not supported in the JOIN sub-query", e.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void MoreLikeThisInsideASubQueryIsRejected()
    {
        using var store = CreateStore(out _);
        using var session = store.OpenSession();
        var e = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<Job>(
            $"{Main} join (from index 'MltCompanies' as k where morelikethis(id() = 'companies/0', '{{\"Fields\":[\"Notes\"]}}')) as c on j.CompanyId = id(c) {Everything}").ToList());
        Assert.Contains("morelikethis() is not supported in the JOIN sub-query", e.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
