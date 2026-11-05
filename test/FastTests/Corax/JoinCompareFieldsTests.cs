using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Exceptions;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// The where clause compares a field of the main document with a field of the document it is joined with on the id
public class JoinCompareFieldsTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Everything = "limit 0, 1000000";
    private const string Main = "from index 'KPlacements' as p";
    private const string JoinC = "join (from index 'KCandidates') as c on p.CandidateId = id(c)";
    private const string JoinM = "join (from index 'KManagers') as m on c.ManagerId = id(m)";
    private static readonly string[] Operators = [">", "<", ">=", "<=", "=", "!="];

    private sealed class Placement { public string Id; public string CandidateId; public long? Salary; public double? Bid; public string Name; public DateTime? Start; public string Status; public long N; public List<long> Levels; public string Mixed; }
    private sealed class Candidate { public string Id; public long? MaxSalary; public double? MinRate; public string Name; public DateTime? Available; public string ManagerId; public string Mixed; public List<long> Levels; public string Country; }
    private sealed class Manager { public string Id; public long? Limit; public string Name; public double? Rate; }

    private sealed class PlacementsIndex : AbstractIndexCreationTask<Placement>
    {
        public override string IndexName => "KPlacements";
        public PlacementsIndex() { Map = docs => from d in docs select new { d.CandidateId, d.Salary, d.Bid, d.Name, d.Start, d.Status, d.N, d.Levels, d.Mixed }; }
    }

    private sealed class CandidatesIndex : AbstractIndexCreationTask<Candidate>
    {
        public override string IndexName => "KCandidates";
        public CandidatesIndex() { Map = docs => from d in docs select new { d.MaxSalary, d.MinRate, d.Name, d.Available, d.ManagerId, d.Mixed, d.Levels, d.Country }; }
    }

    private sealed class ManagersIndex : AbstractIndexCreationTask<Manager>
    {
        public override string IndexName => "KManagers";
        public ManagersIndex() { Map = docs => from d in docs select new { d.Limit, d.Name, d.Rate }; }
    }

    private sealed class Data
    {
        public readonly Dictionary<string, Placement> Placements = new();
        public readonly Dictionary<string, Candidate> Candidates = new();
        public readonly Dictionary<string, Manager> Managers = new();

        public Candidate CandidateOf(Placement p) => p.CandidateId != null && Candidates.TryGetValue(p.CandidateId, out var c) ? c : null;
        public Manager ManagerOf(Candidate c) => c?.ManagerId != null && Managers.TryGetValue(c.ManagerId, out var m) ? m : null;
    }

    private IDocumentStore CreateStore(out Data data, bool arrays = false)
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        var store = GetDocumentStore(options);
        new PlacementsIndex().Execute(store);
        new CandidatesIndex().Execute(store);
        new ManagersIndex().Execute(store);

        var random = new Random(17);
        string[] names = ["Alpha", "bravo", "Charlie", "delta", "Echo"], statuses = ["open", "closed", "draft"];
        var day0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        long? Maybe(long? value) => random.Next(6) == 0 ? null : value;
        double? MaybeD(double? value) => random.Next(6) == 0 ? null : value;
        data = new Data();
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < 12; i++)
            {
                var manager = new Manager { Id = $"managers/{i}", Limit = Maybe(random.Next(8, 14) * 1000), Name = names[random.Next(names.Length)], Rate = MaybeD(random.Next(0, 6) * 0.5) };
                data.Managers[manager.Id] = manager;
                bulk.Store(manager, manager.Id);
            }

            for (var i = 0; i < 40; i++)
            {
                var candidate = new Candidate
                {
                    Id = $"candidates/{i}", MaxSalary = Maybe(random.Next(8, 14) * 1000), MinRate = MaybeD(random.Next(0, 6) * 0.5), Name = names[random.Next(names.Length)],
                    Available = random.Next(6) == 0 ? null : day0.AddDays(random.Next(0, 6)), Country = random.Next(2) == 0 ? "nl" : "de",
                    ManagerId = random.Next(8) switch { 0 => null, 1 => "managers/missing", _ => $"managers/{random.Next(12)}" },
                    Mixed = random.Next(2) == 0 ? "x" : "12", Levels = arrays ? [1, 2] : null
                };
                data.Candidates[candidate.Id] = candidate;
                bulk.Store(candidate, candidate.Id);
            }

            for (var i = 0; i < 200; i++)
            {
                var placement = new Placement
                {
                    Id = $"placements/{i}", N = i, Salary = Maybe(random.Next(8, 14) * 1000), Bid = MaybeD(random.Next(0, 6) * 0.5), Name = names[random.Next(names.Length)],
                    Start = random.Next(6) == 0 ? null : day0.AddDays(random.Next(0, 6)), Status = statuses[random.Next(3)],
                    CandidateId = random.Next(10) switch { 0 => null, 1 => "candidates/missing", _ => $"candidates/{random.Next(40)}" },
                    Mixed = random.Next(2) == 0 ? "x" : "12"
                };
                data.Placements[placement.Id] = placement;
                bulk.Store(placement, placement.Id);
            }
        }

        Indexes.WaitForIndexing(store);
        return store;
    }

    private static string[] Ids(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<Placement>(rql).ToList().Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static void Check(IDocumentStore store, Data data, string where, Func<Placement, Candidate, bool> expected, string joins = JoinC)
    {
        var rows = data.Placements.Values.Where(p => expected(p, data.CandidateOf(p))).Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var actual = Ids(store, $"{Main} {joins} where {where} {Everything}");
        Assert.True(rows.Length > 0 && rows.Length < data.Placements.Count, $"[{where}] the data does not tell the rows apart ({rows.Length} rows)");
        Assert.True(rows.SequenceEqual(actual), $"[{where}] expected {rows.Length} rows, got {actual.Length}: missing {string.Join(",", rows.Except(actual).Take(5))}, extra {string.Join(",", actual.Except(rows).Take(5))}");
    }

    private static bool Holds(string op, int compare) => op switch
    {
        ">" => compare > 0, "<" => compare < 0, ">=" => compare >= 0, "<=" => compare <= 0, "=" => compare == 0, "!=" => compare != 0,
        _ => throw new ArgumentException(op)
    };

    private static bool Holds<T>(string op, T? a, T? b) where T : struct, IComparable<T> => a != null && b != null && Holds(op, a.Value.CompareTo(b.Value));

    // the terms of a text field are lowercased by the index
    private static bool Holds(string op, string a, string b) => a != null && b != null && Holds(op, string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant()));

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void NumbersStringsAndDatesOfTheRowAndOfTheJoinedDocument()
    {
        using var store = CreateStore(out var d);
        foreach (var op in Operators)
        {
            Check(store, d, $"p.Salary {op} c.MaxSalary", (p, c) => c != null && Holds(op, p.Salary, c.MaxSalary));
            Check(store, d, $"c.MaxSalary {op} p.Salary", (p, c) => c != null && Holds(op, c.MaxSalary, p.Salary));
            Check(store, d, $"p.Bid {op} c.MinRate", (p, c) => c != null && Holds(op, p.Bid, c.MinRate));
            Check(store, d, $"p.Name {op} c.Name", (p, c) => c != null && Holds(op, p.Name, c.Name));
            Check(store, d, $"p.Start {op} c.Available", (p, c) => c != null && Holds(op, p.Start, c.Available));
            // a whole number against a fraction: compared as doubles
        }

        foreach (var op in new[] { "<", "<=", "!=" })
            Check(store, d, $"p.Bid {op} c.MaxSalary", (p, c) => c != null && Holds(op, p.Bid, (double?)c.MaxSalary));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void CombinedWithOtherConditionsAndOrNot()
    {
        using var store = CreateStore(out var d);
        bool Gt(Placement p, Candidate c) => c != null && Holds(">", p.Salary, c.MaxSalary);

        Check(store, d, "p.Salary > c.MaxSalary and p.Status = 'open'", (p, c) => Gt(p, c) && p.Status == "open");
        Check(store, d, "p.Salary > c.MaxSalary and c.Country = 'nl'", (p, c) => Gt(p, c) && c.Country == "nl");
        Check(store, d, "p.Salary > c.MaxSalary or p.Status = 'open'", (p, c) => Gt(p, c) || p.Status == "open");
        Check(store, d, "p.Salary > c.MaxSalary or c.Country = 'nl'", (p, c) => Gt(p, c) || c is { Country: "nl" });
        Check(store, d, "p.N >= 0 and not (p.Salary > c.MaxSalary)", (p, c) => !Gt(p, c));
        Check(store, d, "p.Status = 'open' and not (p.Salary > c.MaxSalary)", (p, c) => p.Status == "open" && !Gt(p, c));
        Check(store, d, "(p.Salary > c.MaxSalary or p.Bid < c.MinRate) and p.Status != 'draft'", (p, c) => (Gt(p, c) || (c != null && Holds("<", p.Bid, c.MinRate))) && p.Status != "draft");
        Check(store, d, "p.Salary >= c.MaxSalary and p.Start < c.Available", (p, c) => c != null && Holds(">=", p.Salary, c.MaxSalary) && Holds("<", p.Start, c.Available));
        Check(store, d, "p.Salary < c.MaxSalary or p.Name = c.Name or p.N = 1", (p, c) => (c != null && (Holds("<", p.Salary, c.MaxSalary) || Holds("=", p.Name, c.Name))) || p.N == 1);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ChainOfJoins()
    {
        using var store = CreateStore(out var d);
        var joins = $"{JoinC} {JoinM}";
        // a joined document of a chain only exists when every document along the chain does
        foreach (var op in new[] { ">", "<=", "=", "!=" })
        {
            Check(store, d, $"c.MaxSalary {op} m.Limit", (p, c) => d.ManagerOf(c) is { } m && Holds(op, c.MaxSalary, m.Limit), joins);
            Check(store, d, $"p.Salary {op} m.Limit", (p, c) => d.ManagerOf(c) is { } m && Holds(op, p.Salary, m.Limit), joins);
            Check(store, d, $"m.Name {op} p.Name", (p, c) => d.ManagerOf(c) is { } m && Holds(op, m.Name, p.Name), joins);
        }

        Check(store, d, "c.MinRate > m.Rate and p.Status = 'open'", (p, c) => d.ManagerOf(c) is { } m && Holds(">", c.MinRate, m.Rate) && p.Status == "open", joins);
        Check(store, d, "c.MaxSalary > m.Limit or p.Status = 'closed'", (p, c) => (d.ManagerOf(c) is { } m && Holds(">", c.MaxSalary, m.Limit)) || p.Status == "closed", joins);
    }

    private sealed class Row { public long N; public string CandidateName; }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void LeftJoinOrderByProjectionPagingAndFacets()
    {
        using var store = CreateStore(out var d);
        bool Gt(Placement p, Candidate c) => c != null && Holds(">", p.Salary, c.MaxSalary);
        var left = "left join (from index 'KCandidates') as c on p.CandidateId = id(c)";

        // a left join with the comparison filters: a row without a joined document does not satisfy it
        Check(store, d, "p.Salary > c.MaxSalary", Gt, left);
        Check(store, d, "p.Salary > c.MaxSalary or p.Status = 'open'", (p, c) => Gt(p, c) || p.Status == "open", left);
        Check(store, d, "p.N >= 0 and not (p.Salary > c.MaxSalary)", (p, c) => !Gt(p, c), left);

        using (var session = store.OpenSession())
        {
            var rows = session.Advanced.RawQuery<Placement>($"{Main} {JoinC} where p.Salary > c.MaxSalary order by c.MaxSalary as long {Everything}").ToList();
            var expected = d.Placements.Values.Where(p => Gt(p, d.CandidateOf(p))).Select(p => p.Id).OrderBy(x => x).ToArray();
            Assert.Equal(expected, rows.Select(x => x.Id).OrderBy(x => x).ToArray());
            var limits = rows.Select(x => d.CandidateOf(x).MaxSalary).ToList();
            Assert.Equal(limits.OrderBy(x => x).ToList(), limits);

            var byN = session.Advanced.RawQuery<Placement>($"{Main} {JoinC} where p.Salary > c.MaxSalary order by p.N as long desc {Everything}").ToList();
            Assert.Equal(expected.OrderByDescending(x => d.Placements[x].N).ToArray(), byN.Select(x => x.Id).ToArray());

            var projected = session.Advanced.RawQuery<Row>($"{Main} {JoinC} where p.Salary > c.MaxSalary select p.N as N, c.Name as CandidateName {Everything}").ToList();
            Assert.Equal(expected.Length, projected.Count);
            Assert.All(projected, x => Assert.Equal(d.CandidateOf(d.Placements[$"placements/{x.N}"]).Name, x.CandidateName));
        }

        // paging and the total: every candidate row is evaluated
        using (var session = store.OpenSession())
        {
            var expected = d.Placements.Values.Where(p => Gt(p, d.CandidateOf(p)) || p.Status == "open").Select(p => p.Id).OrderBy(x => x).ToArray();
            var all = new List<string>();
            for (var start = 0; start < expected.Length; start += 20)
            {
                var page = session.Advanced.RawQuery<Placement>($"{Main} {JoinC} where p.Salary > c.MaxSalary or p.Status = 'open' order by p.N as long limit {start}, 20").Statistics(out var stats).ToList();
                Assert.Equal(expected.Length, stats.TotalResults);
                all.AddRange(page.Select(x => x.Id));
            }

            Assert.Equal(expected, all.OrderBy(x => x).ToArray());
        }

        using (var session = store.OpenSession())
        {
            var facets = session.Advanced.RawQuery<Placement>($"{Main} {JoinC} where p.Salary > c.MaxSalary select facet(Status)").ExecuteAggregation();
            var expected = d.Placements.Values.Where(p => Gt(p, d.CandidateOf(p))).GroupBy(p => p.Status).ToDictionary(x => x.Key, x => x.Count());
            Assert.Equal(expected, facets["Status"].Values.Where(x => x.Count > 0).ToDictionary(x => x.Range, x => (int)x.Count));
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void NumberAndTextAreNeverEqualOrOrdered()
    {
        using var store = CreateStore(out var d);
        // Mixed holds the text "12" or "x" on both sides, Salary a number: nothing compares across the types
        foreach (var op in Operators)
            Assert.Empty(Ids(store, $"{Main} {JoinC} where p.Salary {op} c.Name {Everything}"));

        // the same type on both sides compares as it is
        foreach (var op in Operators)
            Check(store, d, $"p.Mixed {op} c.Mixed", (p, c) => c != null && Holds(op, p.Mixed, c.Mixed));
    }

    private void Rejected(IDocumentStore store, string rql, string message)
    {
        using var session = store.OpenSession();
        var exception = Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<Placement>(rql).ToList());
        Assert.Contains(message, exception.Message);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void Rejections()
    {
        using var store = CreateStore(out _);

        // a join on a field can join several documents to one row
        Rejected(store, $"{Main} join (from index 'KCandidates') as c on p.Name = c.Name where p.Salary > c.MaxSalary", "ambiguous");
        // two aliases that are not joined through each other
        Rejected(store, $"{Main} {JoinC} join (from index 'KManagers') as m on p.Name = id(m) where c.MaxSalary > m.Limit", "not joined through each other");
        Rejected(store, $"{Main} {JoinC} where p.Salary > c.Unknown", "not indexed");
        Rejected(store, $"{Main} {JoinC} where p.Unknown > c.MaxSalary", "not indexed");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AFieldWithSeveralValuesIsRejectedWhenItIsRead()
    {
        using var store = CreateStore(out _, arrays: true);
        Rejected(store, $"{Main} {JoinC} where p.Salary > c.Levels {Everything}", "The field 'Levels' of 'c' holds several values");
        Rejected(store, $"{Main} {JoinC} where p.Salary > c.Levels {Everything}", "full-text analyzer");
    }
}
