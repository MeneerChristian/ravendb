using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;

namespace FastTests.Corax;

// Opt-in: RAVEN_JOIN_COMPARE_BENCH=&lt;rows&gt
public class JoinCompareBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Main = "from index 'BPlacements' as p";
    private const string Join = "join (from index 'BCandidates') as c on p.CandidateId = id(c)";

    private sealed class Placement { public string Id { get; set; } public string CandidateId { get; set; } public long Salary { get; set; } public string Name { get; set; } public string Status { get; set; } }
    private sealed class Candidate { public string Id { get; set; } public long MaxSalary { get; set; } public string Name { get; set; } }

    private sealed class PlacementsIndex : AbstractIndexCreationTask<Placement>
    {
        public override string IndexName => "BPlacements";
        public PlacementsIndex() { Map = docs => from d in docs select new { d.CandidateId, d.Salary, d.Name, d.Status }; }
    }

    private sealed class CandidatesIndex : AbstractIndexCreationTask<Candidate>
    {
        public override string IndexName => "BCandidates";
        public CandidatesIndex() { Map = docs => from d in docs select new { d.MaxSalary, d.Name }; }
    }

    [JoinBenchmarkFact("RAVEN_JOIN_COMPARE_BENCH", RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void Run()
    {
        var rows = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_COMPARE_BENCH"));
        var reps = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_COMPARE_BENCH_REPS") ?? "9");
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        using var store = GetDocumentStore(options);
        new PlacementsIndex().Execute(store);
        new CandidatesIndex().Execute(store);

        var random = new Random(5);
        string[] names = ["alpha", "bravo", "charlie", "delta", "echo"], statuses = ["open", "closed", "draft", "hold"];
        var candidates = new Candidate[rows];
        var placements = new Placement[rows];
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < rows; i++)
                bulk.Store(candidates[i] = new Candidate { Id = $"candidates/{i}", MaxSalary = random.Next(1000, 9000), Name = names[random.Next(names.Length)] }, $"candidates/{i}");
            for (var i = 0; i < rows; i++)
                bulk.Store(placements[i] = new Placement { Id = $"placements/{i}", CandidateId = $"candidates/{random.Next(rows)}", Salary = random.Next(1000, 9000), Name = names[random.Next(names.Length)], Status = statuses[random.Next(statuses.Length)] }, $"placements/{i}");
        }

        Candidate CandidateOf(Placement p) => candidates[int.Parse(p.CandidateId["candidates/".Length..])];
        var expected = new Dictionary<string, long>
        {
            ["join, p.Salary > c.MaxSalary, first page"] = placements.Count(p => p.Salary > CandidateOf(p).MaxSalary),
            ["join, p.Salary > c.MaxSalary and p.Status = 'open', first page"] = placements.Count(p => p.Salary > CandidateOf(p).MaxSalary && p.Status == "open"),
            ["join, p.Salary > c.MaxSalary or p.Status = 'open', first page"] = placements.Count(p => p.Salary > CandidateOf(p).MaxSalary || p.Status == "open"),
            ["join, p.Name = c.Name (text), first page"] = placements.Count(p => p.Name == CandidateOf(p).Name),
            ["join, no comparison, first page"] = placements.Count(p => p.Status == "open"),
        };

        Indexes.WaitForIndexing(store, timeout: TimeSpan.FromMinutes(60));
        GC.Collect();

        var report = new StringBuilder();
        report.AppendLine($"=== {rows} placements, {rows} candidates, {reps} reps ({DateTime.Now:yyyy-MM-dd HH:mm}) ===");
        report.AppendLine("query | rows total | server ms (median / min) | managed KB (median)");
        var queries = new (string Name, string Rql)[]
        {
            ("no join, p.Salary > 5000, first page", $"{Main} where p.Salary > 5000 order by p.Name limit 0, 20"),
            ("join, no comparison, first page", $"{Main} {Join} where p.Status = 'open' order by p.Name limit 0, 20"),
            ("join, p.Salary > c.MaxSalary, first page", $"{Main} {Join} where p.Salary > c.MaxSalary order by p.Name limit 0, 20"),
            ("join, p.Salary > c.MaxSalary and p.Status = 'open', first page", $"{Main} {Join} where p.Salary > c.MaxSalary and p.Status = 'open' order by p.Name limit 0, 20"),
            ("join, p.Salary > c.MaxSalary or p.Status = 'open', first page", $"{Main} {Join} where p.Salary > c.MaxSalary or p.Status = 'open' order by p.Name limit 0, 20"),
            ("join, p.Name = c.Name (text), first page", $"{Main} {Join} where p.Name = c.Name order by p.Name limit 0, 20"),
            ("join, no comparison, last page", $"{Main} {Join} where p.Status = 'open' order by p.Name limit {rows / 8}, 20"),
            ("join, p.Salary > c.MaxSalary, last page", $"{Main} {Join} where p.Salary > c.MaxSalary order by p.Name limit {rows / 3}, 20"),
            ("join, p.Salary > c.MaxSalary, projection c.Name", $"{Main} {Join} where p.Salary > c.MaxSalary order by p.Name select p.Name as Name, c.Name as CandidateName limit 0, 20"),
        };

        foreach (var (name, rql) in queries)
        {
            for (var i = 0; i < 3; i++)
                Execute(store, rql, out _, out _);

            var server = new List<double>();
            var bytes = new List<double>();
            long total = 0;
            for (var i = 0; i < reps; i++)
            {
                total = Execute(store, rql, out var serverMs, out var managed);
                server.Add(serverMs);
                bytes.Add(managed);
            }

            if (expected.TryGetValue(name, out var count))
                Assert.Equal(count, total);

            report.AppendLine($"{name} | {total:N0} | {Median(server):F0} / {server.Min():F0} | {Median(bytes) / 1024:N0}");
        }

        output.WriteLine(report.ToString());
        AppendReport("RAVEN_JOIN_COMPARE_BENCH_OUT", "joincompare.txt", report);
    }

    private sealed class Projection { public string Name { get; set; } public string CandidateName { get; set; } }

    private static long Execute(IDocumentStore store, string rql, out double serverMs, out double managedBytes)
    {
        using var session = store.OpenSession();
        QueryStatistics stats;
        QueryTimings timings;
        _ = rql.Contains(" select ")
            ? session.Advanced.RawQuery<Projection>(rql + " include timings()").NoCaching().Statistics(out stats).Timings(out timings).ToList().Count
            : session.Advanced.RawQuery<Placement>(rql + " include timings()").NoCaching().Statistics(out stats).Timings(out timings).ToList().Count;
        serverMs = stats.DurationInMs;
        managedBytes = timings.Timings["Query"].Counters["AllocatedManagedBytes"];
        return stats.TotalResults;
    }
}
