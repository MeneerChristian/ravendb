using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// matches(alias) of a chain of joins
public class JoinChainMatchesTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const double Tolerance = 1e-4;
    private const string Everything = "limit 0, 1000000";

    private const string Main = "from index 'ChainPlacements' as p";
    private const string JoinC = "join (from index 'ChainCandidates' where search(Name, 'anna bob')) as c on p.CandidateId = id(c)";
    private const string JoinF = "join (from index 'ChainFiles' where search(Text, 'java')) as f on c.CurriculumVitaeId = id(f)";
    private const string Own = "search(p.Content, 'java')";
    private const string Chain = $"{Main} {JoinC} {JoinF}";

    private sealed class Placement { public string Id; public string Key; public string Content; public string CandidateId; }
    private sealed class Candidate { public string Id; public string Name; public string CurriculumVitaeId; }
    private sealed class FileInfo { public string Id; public string Text; }
    private sealed class Row { public string Key; public string CName; public string FText; }

    private sealed class PlacementsIndex : AbstractIndexCreationTask<Placement>
    {
        public override string IndexName => "ChainPlacements";
        public PlacementsIndex() { Map = docs => from d in docs select new { d.Content, d.CandidateId }; Index(x => x.Content, FieldIndexing.Search); }
    }

    private sealed class CandidatesIndex : AbstractIndexCreationTask<Candidate>
    {
        public override string IndexName => "ChainCandidates";
        public CandidatesIndex() { Map = docs => from d in docs select new { d.Name, d.CurriculumVitaeId }; Index(x => x.Name, FieldIndexing.Search); }
    }

    private sealed class FilesIndex : AbstractIndexCreationTask<FileInfo>
    {
        public override string IndexName => "ChainFiles";
        public FilesIndex() { Map = docs => from d in docs select new { d.Text }; Index(x => x.Text, FieldIndexing.Search); }
    }

    private sealed class Data
    {
        public Dictionary<string, Placement> Placements = new();
        public Dictionary<string, Candidate> Candidates = new();
        public Dictionary<string, string> Files = new();
        public Dictionary<string, double> C = new(), F = new(), Outer = new();
    }

    private Data Seed(IDocumentStore store, int seed)
    {
        var random = new Random(seed);
        string Words(string[] vocabulary) => string.Join(' ', Enumerable.Range(0, random.Next(1, 4)).Select(_ => vocabulary[random.Next(vocabulary.Length)]));
        var data = new Data();
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < 40; i++)
            {
                var file = new FileInfo { Id = $"files/{i}", Text = Words(["java", "cobol", "rust", "go"]) };
                data.Files[file.Id] = file.Text;
                bulk.Store(file, file.Id);
            }

            for (var i = 0; i < 30; i++)
            {
                // a candidate without a file, or with a file that does not exist, is a candidate that cannot lead to an f
                var file = random.Next(10) switch { 0 => null, 1 => "files/missing", _ => $"files/{random.Next(40)}" };
                var candidate = new Candidate { Id = $"candidates/{i}", Name = Words(["anna", "bob", "carl", "dave"]), CurriculumVitaeId = file };
                data.Candidates[candidate.Id] = candidate;
                bulk.Store(candidate, candidate.Id);
            }

            for (var i = 0; i < 120; i++)
            {
                var candidate = random.Next(10) switch { 0 => null, 1 => "candidates/missing", _ => $"candidates/{random.Next(30)}" };
                var placement = new Placement { Id = $"placements/{i}", Key = $"k{i}", Content = Words(["java", "python", "sql", "linux"]), CandidateId = candidate };
                data.Placements[placement.Id] = placement;
                bulk.Store(placement, placement.Id);
            }
        }

        Indexes.WaitForIndexing(store);

        using var session = store.OpenSession();
        data.C = Scores(session, "from index 'ChainCandidates' where search(Name, 'anna bob')");
        data.F = Scores(session, "from index 'ChainFiles' where search(Text, 'java')");
        data.Outer = Scores(session, $"{Main} where {Own}");
        Assert.True(data.F.Count > 5 && data.F.Count < 35);
        Assert.True(data.C.Count > 5 && data.C.Count < 25);
        return data;
    }

    private static Dictionary<string, double> Scores(IDocumentSession session, string rql)
    {
        var result = new Dictionary<string, double>();
        foreach (var entity in session.Advanced.RawQuery<object>($"{rql} order by score() {Everything}").ToList())
        {
            var metadata = session.Advanced.GetMetadataFor(entity);
            result[(string)metadata[Constants.Documents.Metadata.Id]] = Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore]);
        }

        return result;
    }

    private IDocumentStore CreateStore()
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        var store = GetDocumentStore(options);
        new PlacementsIndex().Execute(store);
        new CandidatesIndex().Execute(store);
        new FilesIndex().Execute(store);
        return store;
    }

    // what a placement has: its own text, a matched c, a matched c that leads to a matched f
    private static bool Own_(Data d, Placement p) => d.Outer.ContainsKey(p.Id);
    private static Candidate CandidateOf(Data d, Placement p) => p.CandidateId != null && d.Candidates.TryGetValue(p.CandidateId, out var c) ? c : null;
    private static bool MatchesC(Data d, Placement p) => CandidateOf(d, p) is { } c && d.C.ContainsKey(c.Id);
    private static bool MatchesF(Data d, Placement p) => MatchesC(d, p) && CandidateOf(d, p).CurriculumVitaeId is { } f && d.F.ContainsKey(f);
    private static double Sc(Data d, Placement p) => d.C[p.CandidateId];
    private static double Sf(Data d, Placement p) => d.F[CandidateOf(d, p).CurriculumVitaeId];

    // where clause, rows, the score of a row, the placements whose c / f are returned by the projection
    private static IEnumerable<(string Where, Func<Data, Placement, bool> Rows, Func<Data, Placement, double> Score, Func<Data, Placement, bool> CShown, Func<Data, Placement, bool> FShown)> Cases()
    {
        yield return ("matches(f)", (d, p) => MatchesF(d, p), (d, p) => Sc(d, p) + Sf(d, p), MatchesF, MatchesF);
        yield return ($"{Own} or matches(f)", (d, p) => Own_(d, p) || MatchesF(d, p),
            (d, p) => (Own_(d, p) ? d.Outer[p.Id] : 0) + (MatchesF(d, p) ? Sc(d, p) + Sf(d, p) : 0), MatchesF, MatchesF);
        yield return ("matches(c) or matches(f)", (d, p) => MatchesC(d, p),
            (d, p) => Sc(d, p) + (MatchesF(d, p) ? Sc(d, p) + Sf(d, p) : 0), MatchesC, MatchesF);
        yield return ("matches(f) or matches(c)", (d, p) => MatchesC(d, p),
            (d, p) => Sc(d, p) + (MatchesF(d, p) ? Sc(d, p) + Sf(d, p) : 0), MatchesC, MatchesF);
        yield return ("matches(c) and matches(f)", (d, p) => MatchesF(d, p), (d, p) => 2 * Sc(d, p) + Sf(d, p), MatchesC, MatchesF);
        // f is not mentioned: it stays an inner join of c
        yield return ("matches(c)", (d, p) => MatchesF(d, p), (d, p) => Sc(d, p) + Sf(d, p), MatchesF, MatchesF);
        yield return ($"{Own} and (matches(c) or matches(f))", (d, p) => Own_(d, p) && MatchesC(d, p),
            (d, p) => d.Outer[p.Id] + Sc(d, p) + (MatchesF(d, p) ? Sc(d, p) + Sf(d, p) : 0), MatchesC, MatchesF);
        yield return ($"({Own} or matches(f)) and matches(c)", (d, p) => (Own_(d, p) || MatchesF(d, p)) && MatchesC(d, p),
            (d, p) => (Own_(d, p) ? d.Outer[p.Id] : 0) + (MatchesF(d, p) ? Sc(d, p) + Sf(d, p) : 0) + Sc(d, p), MatchesC, MatchesF);
    }

    public static IEnumerable<object[]> CaseNumbers() => Cases().Select((_, i) => new object[] { i });

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [MemberData(nameof(CaseNumbers))]
    public void RowsScoresAndProjectionMatchTheOracle(int number)
    {
        var (where, rows, score, cShown, fShown) = Cases().ElementAt(number);
        using var store = CreateStore();
        var data = Seed(store, seed: 1 + number % 2);

        var expected = data.Placements.Values.Where(p => rows(data, p)).ToList();
        Assert.True(expected.Count > 0 && expected.Count < data.Placements.Count, $"the data has to separate the rows: {expected.Count}");

        using var session = store.OpenSession();
        var actual = session.Advanced.RawQuery<Row>($"{Chain} where {where} order by score() select p.Key as Key, c.Name as CName, f.Text as FText {Everything}")
            .Statistics(out var statistics).ToList();

        Assert.Equal(expected.Select(x => x.Key).OrderBy(x => x), actual.Select(x => x.Key).OrderBy(x => x));
        Assert.Equal(expected.Count, statistics.TotalResults);

        foreach (var row in actual)
        {
            var placement = data.Placements.Values.Single(x => x.Key == row.Key);
            Assert.Equal(cShown(data, placement) ? CandidateOf(data, placement).Name : null, row.CName);
            Assert.Equal(fShown(data, placement) ? data.Files[CandidateOf(data, placement).CurriculumVitaeId] : null, row.FText);
        }

        // the scores in the order of the result, and the page
        var expectedScores = expected.Select(x => score(data, x)).OrderByDescending(x => x).ToList();
        var scored = session.Advanced.RawQuery<Placement>($"{Chain} where {where} order by score() {Everything}").ToList()
            .Select(x => Convert.ToDouble(session.Advanced.GetMetadataFor(x)[Constants.Documents.Metadata.IndexScore])).ToList();
        Assert.Equal(expectedScores.Count, scored.Count);
        for (var i = 0; i < scored.Count; i++)
            Assert.InRange(scored[i], expectedScores[i] - Tolerance, expectedScores[i] + Tolerance);

        using var pageSession = store.OpenSession();
        var page = pageSession.Advanced.RawQuery<Placement>($"{Chain} where {where} order by score() limit 0, 5").Statistics(out var pageStatistics).ToList()
            .Select(x => Convert.ToDouble(pageSession.Advanced.GetMetadataFor(x)[Constants.Documents.Metadata.IndexScore])).ToList();
        Assert.Equal(Math.Min(5, expected.Count), page.Count);
        Assert.Equal(expected.Count, pageStatistics.TotalResults);
        for (var i = 0; i < page.Count; i++)
            Assert.InRange(page[i], expectedScores[i] - Tolerance, expectedScores[i] + Tolerance);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ChainsThatTheWhereClauseDoesNotMentionStayInner()
    {
        using var store = CreateStore();
        var data = Seed(store, seed: 3);

        using var session = store.OpenSession();
        var keys = session.Advanced.RawQuery<Placement>($"{Chain} {Everything}").ToList().Select(x => x.Key).OrderBy(x => x);
        Assert.Equal(data.Placements.Values.Where(p => MatchesF(data, p)).Select(x => x.Key).OrderBy(x => x), keys);

        keys = session.Advanced.RawQuery<Placement>($"{Chain} where {Own} {Everything}").ToList().Select(x => x.Key).OrderBy(x => x);
        Assert.Equal(data.Placements.Values.Where(p => Own_(data, p) && MatchesF(data, p)).Select(x => x.Key).OrderBy(x => x), keys);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void RejectedForms()
    {
        using var store = CreateStore();
        using var session = store.OpenSession();

        void Rejected(string rql, params string[] messages)
        {
            var exception = Assert.ThrowsAny<RavenException>(() => session.Advanced.RawQuery<Placement>(rql).ToList());
            Assert.True(messages.Any(message => exception.Message.Contains(message)), exception.Message);
        }

        Rejected($"{Chain} where not matches(f)", "cannot be negated", "after 'not'");
        Rejected($"{Chain} where matches(c) or matches(g)", "not the alias of a JOIN");
        // the rows of the order of a join that is cut from the chain have no position
        Rejected($"{Chain} where matches(f) order by c.Name", "cannot be combined with matches()");
    }
}
