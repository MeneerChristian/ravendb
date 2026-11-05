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

// A LEFT JOIN in a chain of joins
public class JoinLeftChainTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const double Tolerance = 1e-4;
    private const string Everything = "limit 0, 1000000";
    private const string Main = "from index 'LeftChainPlacements' as p";
    private const string Own = "search(p.Content, 'java')";

    private sealed class Placement { public string Id; public string Key; public string Content; public string CandidateId; }
    private sealed class Candidate { public string Id; public string Name; public string CurriculumVitaeId; }
    private sealed class FileInfo { public string Id; public string Text; }
    private sealed class Row { public string Key; public string CName; public string FText; }

    private sealed class PlacementsIndex : AbstractIndexCreationTask<Placement>
    {
        public override string IndexName => "LeftChainPlacements";
        public PlacementsIndex() { Map = docs => from d in docs select new { d.Content, d.CandidateId }; Index(x => x.Content, FieldIndexing.Search); }
    }

    private sealed class CandidatesIndex : AbstractIndexCreationTask<Candidate>
    {
        public override string IndexName => "LeftChainCandidates";
        public CandidatesIndex() { Map = docs => from d in docs select new { d.Name, d.CurriculumVitaeId }; Index(x => x.Name, FieldIndexing.Search); }
    }

    private sealed class FilesIndex : AbstractIndexCreationTask<FileInfo>
    {
        public override string IndexName => "LeftChainFiles";
        public FilesIndex() { Map = docs => from d in docs select new { d.Text }; Index(x => x.Text, FieldIndexing.Search); }
    }

    private sealed class Data
    {
        public Dictionary<string, Placement> Placements = new();
        public Dictionary<string, Candidate> Candidates = new();
        public Dictionary<string, string> Files = new();
        public Dictionary<string, double> C = new(), F = new(), Outer = new();
    }

    private static string Join(string kind, string boost, string index, string where, string alias, string on) =>
        $"{kind} (from index '{index}' where {where}) as {alias} on {on}{boost}";

    private static string JoinC(bool left, string boost = "") => Join(left ? "left join" : "join", boost, "LeftChainCandidates", "search(Name, 'anna bob')", "c", "p.CandidateId = id(c)");
    private static string JoinF(bool left, string boost = "") => Join(left ? "left join" : "join", boost, "LeftChainFiles", "search(Text, 'java')", "f", "c.CurriculumVitaeId = id(f)");

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
                var file = random.Next(10) switch { 0 => null, 1 => "files/missing", _ => $"files/{random.Next(40)}" };
                var candidate = new Candidate { Id = $"candidates/{i}", Name = Words(["anna", "bob", "carl", "dave"]), CurriculumVitaeId = file };
                data.Candidates[candidate.Id] = candidate;
                bulk.Store(candidate, candidate.Id);
            }

            for (var i = 0; i < 500; i++)
            {
                var candidate = random.Next(10) switch { 0 => null, 1 => "candidates/missing", _ => $"candidates/{random.Next(30)}" };
                var placement = new Placement { Id = $"placements/{i}", Key = $"k{i}", Content = Words(["java", "python", "sql", "linux"]), CandidateId = candidate };
                data.Placements[placement.Id] = placement;
                bulk.Store(placement, placement.Id);
            }
        }

        Indexes.WaitForIndexing(store);

        using var session = store.OpenSession();
        data.C = Scores(session, "from index 'LeftChainCandidates' where search(Name, 'anna bob')");
        data.F = Scores(session, "from index 'LeftChainFiles' where search(Text, 'java')");
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

    private static Candidate CandidateOf(Data d, Placement p) => p.CandidateId != null && d.Candidates.TryGetValue(p.CandidateId, out var c) ? c : null;
    private static bool Own_(Data d, Placement p) => d.Outer.ContainsKey(p.Id);
    private static bool CMatches(Data d, Placement p) => CandidateOf(d, p) is { } c && d.C.ContainsKey(c.Id);
    private static bool FMatches(Data d, Placement p) => CMatches(d, p) && CandidateOf(d, p).CurriculumVitaeId is { } f && d.F.ContainsKey(f);
    private static string FileText(Data d, Placement p) => d.Files[CandidateOf(d, p).CurriculumVitaeId];
    private static bool Has(string text, string word) => text.Split(' ').Contains(word);

    // c is a key of its sub-query when it matches and, with an inner f, leads to a matching f
    private static bool CKey(Data d, Placement p, bool fLeft) => fLeft ? CMatches(d, p) : FMatches(d, p);

    private static double Cost(Data d, Placement p, bool fLeft, double bc, double bf) =>
        CKey(d, p, fLeft) ? bc * (d.C[p.CandidateId] + (FMatches(d, p) ? bf * d.F[CandidateOf(d, p).CurriculumVitaeId] : 0)) : 0;

    public static IEnumerable<object[]> Shapes()
    {
        foreach (var cLeft in new[] { false, true })
        foreach (var fLeft in new[] { false, true })
        foreach (var own in new[] { false, true })
        foreach (var boosted in new[] { false, true })
            yield return [cLeft, fLeft, own, boosted];
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [MemberData(nameof(Shapes))]
    public void RowsScoresPagingAndProjectionMatchTheOracle(bool cLeft, bool fLeft, bool own, bool boosted)
    {
        using var store = CreateStore();
        var data = Seed(store, seed: 5);
        double bc = boosted ? 2 : 1, bf = boosted ? 0.5 : 1;
        var chain = $"{Main} {JoinC(cLeft, boosted ? " boost 2" : "")} {JoinF(fLeft, boosted ? " boost 0.5" : "")}";
        var where = own ? $"where {Own}" : "";

        bool Row(Placement p) => (own == false || Own_(data, p)) && (cLeft || CKey(data, p, fLeft));
        double Score(Placement p) => (own ? data.Outer.GetValueOrDefault(p.Id) : 0) + Cost(data, p, fLeft, bc, bf);

        var expected = data.Placements.Values.Where(Row).ToList();
        Assert.True(expected.Count > 3 && expected.Count <= data.Placements.Count, $"the data has to separate the rows: {expected.Count}");
        Assert.True(expected.Any(p => CKey(data, p, fLeft)) && (cLeft == false || expected.Any(p => CKey(data, p, fLeft) == false)), "the data has rows with and without the joins");

        using var session = store.OpenSession();
        var actual = session.Advanced.RawQuery<Row>($"{chain} {where} order by score() select p.Key as Key, c.Name as CName, f.Text as FText {Everything}").Statistics(out var statistics).ToList();

        Assert.Equal(expected.Select(x => x.Key).OrderBy(x => x), actual.Select(x => x.Key).OrderBy(x => x));
        Assert.Equal(expected.Count, statistics.TotalResults);

        foreach (var row in actual)
        {
            var placement = data.Placements.Values.Single(x => x.Key == row.Key);
            Assert.Equal(CKey(data, placement, fLeft) ? CandidateOf(data, placement).Name : null, row.CName);
            Assert.Equal(CKey(data, placement, fLeft) && FMatches(data, placement) ? FileText(data, placement) : null, row.FText);
        }

        var expectedScores = expected.Select(Score).OrderByDescending(x => x).ToList();
        List<double> ScoresOf(string rql, out QueryStatistics stats)
        {
            using var s = store.OpenSession();
            return s.Advanced.RawQuery<Placement>(rql).Statistics(out stats).ToList().Select(x => Convert.ToDouble(s.Advanced.GetMetadataFor(x)[Constants.Documents.Metadata.IndexScore])).ToList();
        }

        var scored = ScoresOf($"{chain} {where} order by score() {Everything}", out _);
        Assert.Equal(expectedScores.Count, scored.Count);
        for (var i = 0; i < scored.Count; i++)
            Assert.InRange(scored[i], expectedScores[i] - Tolerance, expectedScores[i] + Tolerance);

        var page = ScoresOf($"{chain} {where} order by score() limit 3, 7", out var pageStats);
        Assert.Equal(Math.Min(7, Math.Max(0, expected.Count - 3)), page.Count);
        Assert.Equal(expected.Count, pageStats.TotalResults);
        for (var i = 0; i < page.Count; i++)
            Assert.InRange(page[i], expectedScores[3 + i] - Tolerance, expectedScores[3 + i] + Tolerance);
    }

    private void Check(Data data, IDocumentStore store, string rql, Func<Placement, bool> rows, Func<Data, Placement, bool> cShown, Func<Data, Placement, bool> fShown)
    {
        var expected = data.Placements.Values.Where(rows).ToList();
        Assert.True(expected.Count > 2, $"[{rql}] the data has to separate the rows: {expected.Count}");

        using var session = store.OpenSession();
        var actual = session.Advanced.RawQuery<Row>($"{rql} select p.Key as Key, c.Name as CName, f.Text as FText {Everything}").Statistics(out var statistics).ToList();
        Assert.True(expected.Select(x => x.Key).OrderBy(x => x).SequenceEqual(actual.Select(x => x.Key).OrderBy(x => x)), $"[{rql}] expected {expected.Count} rows, got {actual.Count}");
        Assert.Equal(expected.Count, statistics.TotalResults);

        foreach (var row in actual)
        {
            var placement = data.Placements.Values.Single(x => x.Key == row.Key);
            Assert.True(cShown(data, placement) == (row.CName != null), $"[{rql}] c of {row.Key}");
            Assert.True(fShown(data, placement) == (row.FText != null), $"[{rql}] f of {row.Key}");
            if (cShown(data, placement))
                Assert.Equal(CandidateOf(data, placement).Name, row.CName);
            if (fShown(data, placement))
                Assert.Equal(FileText(data, placement), row.FText);
        }
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchesAndConditionsOnAliasesTurnTheChainIntoAFilter(bool cLeft)
    {
        using var store = CreateStore();
        var data = Seed(store, seed: 6);
        var chain = $"{Main} {JoinC(cLeft)} {JoinF(left: true)}";

        // matches(f): the placement has a matching c that leads to a matching f
        Check(data, store, $"{chain} where matches(f)", p => FMatches(data, p), FMatches, FMatches);
        Check(data, store, $"{chain} where {Own} or matches(f)", p => Own_(data, p) || FMatches(data, p), FMatches, FMatches);
        // matches(c): f stays optional
        Check(data, store, $"{chain} where matches(c)", p => CMatches(data, p), CMatches, FMatches);
        Check(data, store, $"{chain} where {Own} and matches(c)", p => Own_(data, p) && CMatches(data, p), CMatches, FMatches);
        // a condition on f is a filter: the row needs the whole path
        Check(data, store, $"{chain} where search(f.Text, 'cobol')", p => FMatches(data, p) && Has(FileText(data, p), "cobol"), FMatches, FMatches);
        Check(data, store, $"{chain} where {Own} and search(f.Text, 'cobol')", p => Own_(data, p) && FMatches(data, p) && Has(FileText(data, p), "cobol"), FMatches, FMatches);
        // a condition on c is a filter on c, f stays optional
        Check(data, store, $"{chain} where search(c.Name, 'carl')", p => CMatches(data, p) && Has(CandidateOf(data, p).Name, "carl"), CMatches, FMatches);
        // under an or the condition is one of the ways to match
        Check(data, store, $"{chain} where {Own} or search(f.Text, 'cobol')", p => Own_(data, p) || FMatches(data, p) && Has(FileText(data, p), "cobol"),
            (d, p) => FMatches(d, p) && Has(FileText(d, p), "cobol"), (d, p) => FMatches(d, p) && Has(FileText(d, p), "cobol"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ConditionsOnAliasesOfAnInnerFBelowALeftC()
    {
        using var store = CreateStore();
        var data = Seed(store, seed: 10);
        var chain = $"{Main} {JoinC(left: true)} {JoinF(left: false)}";

        // p left join (c join f): every p stays, c and f are null unless c matches and leads to a matching f
        Check(data, store, chain, p => true, FMatches, FMatches);
        Check(data, store, $"{chain} where {Own}", p => Own_(data, p), FMatches, FMatches);
        Check(data, store, $"{chain} where search(f.Text, 'cobol')", p => FMatches(data, p) && Has(FileText(data, p), "cobol"), FMatches, FMatches);
        Check(data, store, $"{chain} where matches(c)", p => FMatches(data, p), FMatches, FMatches);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void LeftJoinOfTheSecondLevelUnderTwoChains()
    {
        // the same f joined through the candidate of the placement
        using var store = CreateStore();
        var data = Seed(store, seed: 8);
        var chain = $"{Main} {JoinC(left: false)} {JoinF(left: true)} left join (from index 'LeftChainCandidates' where search(Name, 'carl')) as d on p.CandidateId = id(d)";
        var expected = data.Placements.Values.Where(p => CMatches(data, p)).Select(x => x.Key).OrderBy(x => x);

        using var session = store.OpenSession();
        var rows = session.Advanced.RawQuery<Row>($"{chain} select p.Key as Key, c.Name as CName, f.Text as FText {Everything}").ToList();
        Assert.Equal(expected, rows.Select(x => x.Key).OrderBy(x => x));
        foreach (var row in rows)
        {
            var placement = data.Placements.Values.Single(x => x.Key == row.Key);
            Assert.Equal(FMatches(data, placement) ? FileText(data, placement) : null, row.FText);
        }
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

        Rejected($"{Main} {JoinC(left: true)} {JoinF(left: true)} where not matches(f)", "cannot be negated", "after 'not'");
        // the order of a join that is part of a chain still has no position
        Rejected($"{Main} {JoinC(left: true)} {JoinF(left: true)} order by f.Text", "only the joins of the main document");
    }
}
