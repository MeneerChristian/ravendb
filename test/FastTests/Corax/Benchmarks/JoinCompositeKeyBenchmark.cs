using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;

namespace FastTests.Corax;

// Opt-in: RAVEN_JOIN_COMPOSITE_BENCH=&lt;rows&gt
public class JoinCompositeKeyBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Main = "from index 'BkPlaces' as p";

    private sealed class Place { public string Id { get; set; } public string Country { get; set; } public string City { get; set; } public string Name { get; set; } public string Status { get; set; } }
    private sealed class Cand { public string Id { get; set; } public string Country { get; set; } public string City { get; set; } public string Tag { get; set; } }

    private sealed class PlacesIndex : AbstractIndexCreationTask<Place>
    {
        public override string IndexName => "BkPlaces";
        public PlacesIndex() { Map = docs => from d in docs select new { d.Country, d.City, d.Name, d.Status }; }
    }

    private sealed class CandsIndex : AbstractIndexCreationTask<Cand>
    {
        public override string IndexName => "BkCands";
        public CandsIndex() { Map = docs => from d in docs select new { d.Country, d.City, d.Tag }; }
    }

    [JoinBenchmarkFact("RAVEN_JOIN_COMPOSITE_BENCH", RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void Run()
    {
        var rows = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_COMPOSITE_BENCH"));
        var reps = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_COMPOSITE_BENCH_REPS") ?? "7");
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        using var store = GetDocumentStore(options);
        new PlacesIndex().Execute(store);
        new CandsIndex().Execute(store);

        var random = new Random(5);
        var cands = new Cand[rows];
        var places = new Place[rows];
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < rows; i++)
                bulk.Store(cands[i] = new Cand { Id = $"cands/{i}", Country = $"c{random.Next(40)}", City = $"city{random.Next(500)}", Tag = random.Next(100) == 0 ? "rare" : random.Next(2) == 0 ? "a" : "b" }, $"cands/{i}");
            for (var i = 0; i < rows; i++)
                bulk.Store(places[i] = new Place { Id = $"places/{i}", Country = $"C{random.Next(40)}", City = $"city{random.Next(500)}", Name = $"p{random.Next(rows)}", Status = random.Next(4) == 0 ? "open" : "closed" }, $"places/{i}");
        }

        Indexes.WaitForIndexing(store, timeout: TimeSpan.FromMinutes(60));
        GC.Collect();

        const string rare = "join (from index 'BkCands' where Tag = 'rare') as c on ";
        const string wide = "join (from index 'BkCands' where Tag in ('a', 'b')) as c on ";
        const string one = "p.Country = c.Country";
        const string two = "p.Country = c.Country and p.City = c.City";
        var queries = new (string Name, string Rql)[]
        {
            ("rare sub-query, one field, first page", $"{Main} {rare}{one} order by p.Name limit 0, 20"),
            ("rare sub-query, two fields, first page", $"{Main} {rare}{two} order by p.Name limit 0, 20"),
            ("rare sub-query, two fields, where p.Status = 'open'", $"{Main} {rare}{two} where p.Status = 'open' order by p.Name limit 0, 20"),
            ("wide sub-query, one field, first page", $"{Main} {wide}{one} order by p.Name limit 0, 20"),
            ("wide sub-query, two fields, first page", $"{Main} {wide}{two} order by p.Name limit 0, 20"),
            ("rare sub-query, two fields, order by score", $"{Main} {rare}{two} order by score() limit 0, 20"),
        };

        var rareCands = cands.Where(c => c.Tag == "rare").ToList();
        var rareKeys = rareCands.Select(c => (c.Country.ToLowerInvariant(), c.City)).ToHashSet();
        var expectedTwo = places.Count(p => rareKeys.Contains((p.Country.ToLowerInvariant(), p.City)));
        var expectedTwoOpen = places.Count(p => p.Status == "open" && rareKeys.Contains((p.Country.ToLowerInvariant(), p.City)));
        var rareCountries = rareCands.Select(c => c.Country.ToLowerInvariant()).ToHashSet();
        var expectedOne = places.Count(p => rareCountries.Contains(p.Country.ToLowerInvariant()));

        var report = new StringBuilder();
        report.AppendLine($"=== {rows} places, {rows} candidates, {reps} reps ({DateTime.Now:yyyy-MM-dd HH:mm}) ===");
        report.AppendLine("query | rows total | server ms (median / min) | managed KB (median)");
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

            if (name == "rare sub-query, two fields, first page")
                Assert.Equal(expectedTwo, total);
            else if (name == "rare sub-query, one field, first page")
                Assert.Equal(expectedOne, total);
            else if (name.Contains("where p.Status"))
                Assert.Equal(expectedTwoOpen, total);

            report.AppendLine($"{name} | {total:N0} | {Median(server):F0} / {server.Min():F0} | {Median(bytes) / 1024:N0}");
        }

        output.WriteLine(report.ToString());
        AppendReport("RAVEN_JOIN_COMPOSITE_BENCH_OUT", "joincomposite.txt", report);
    }

    private static long Execute(IDocumentStore store, string rql, out double serverMs, out double managedBytes)
    {
        using var session = store.OpenSession();
        var count = session.Advanced.RawQuery<Place>(rql + " include timings()").NoCaching().Statistics(out var stats).Timings(out var timings).ToList().Count;
        Assert.True(count > 0);
        serverMs = stats.DurationInMs;
        managedBytes = timings.Timings["Query"].Counters["AllocatedManagedBytes"];
        return stats.TotalResults;
    }
}
