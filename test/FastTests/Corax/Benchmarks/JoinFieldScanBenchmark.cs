using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Persistence.Corax;
using Raven.Server.Documents.Queries;
using Raven.Server.ServerWide.Context;
using Tests.Infrastructure;
using static FastTests.Corax.JoinTestHelpers;
using JoinFieldScan = Raven.Server.Documents.Indexes.Persistence.Corax.CoraxIndexReadOperation.JoinFieldScan;

namespace FastTests.Corax;

public partial class JoinFieldScanTests
{
    private sealed class BenchUser
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string K7 { get; set; }
        public string K100 { get; set; }
        public string K1k { get; set; }
        public string K10k { get; set; }
        public string K100k { get; set; }
        public string KUnique { get; set; }
        public string[] Tags { get; set; }
    }

    private sealed class BenchUsersIndex : AbstractIndexCreationTask<BenchUser>
    {
        public BenchUsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.K7, u.K100, u.K1k, u.K10k, u.K100k, u.KUnique, u.Tags };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "BenchUsers";
    }

    [JoinBenchmarkFact("RAVEN_JOIN_SCAN_BENCH", RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task Bench()
    {
        var users = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_SCAN_BENCH"));
        var reps = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_SCAN_BENCH_REPS") ?? "21");
        using var store = GetDocumentStore(CoraxOptions());
        new BenchUsersIndex().Execute(store);

        var random = new Random(5);
        using (var bulk = store.BulkInsert())
        {
            var generator = new JoinScoreOrderTests.Generator(200);
            var i = 0;
            foreach (var user in generator.Users(users))
            {
                bulk.Store(new BenchUser
                {
                    Id = user.Id,
                    Name = user.Name,
                    K7 = user.Country,
                    K10k = user.Postcode,
                    K100 = "a" + random.Next(100),
                    K1k = "b" + random.Next(1000),
                    K100k = "c" + random.Next(100_000),
                    KUnique = "u" + i++,
                    Tags = [ "t" + random.Next(100), "t" + random.Next(100), "t" + random.Next(100) ]
                }, user.Id);
            }
        }

        Indexes.WaitForIndexing(store, timeout: TimeSpan.FromMinutes(60));
        GC.Collect();
        var database = await Databases.GetDocumentDatabaseInstanceFor(store);

        var report = new StringBuilder();
        report.AppendLine($"=== {users} users, {reps} reps (median, warm): whole scan ms, managed KB allocated by the whole scan ===");
        var inners = new (string Label, string Rql)[]
        {
            ("wide", $"from index 'BenchUsers' where search(Name, '{InnerSearch}')"),
            ("mid", "from index 'BenchUsers' where search(Name, 'w60 w150 w350')"),
            ("small", "from index 'BenchUsers' where search(Name, 'w300 w700 w1100')"),
            ("narrow", "from index 'BenchUsers' where search(Name, 'w1500 w2500 w3300')")
        };
        var fields = new[] { "K7", "K100", "K1k", "K10k", "K100k", "KUnique", "Tags" };
        var strategies = new[] { JoinFieldScan.EntryTerms, JoinFieldScan.TermWalk, JoinFieldScan.BulkMap };

        // burn-in: every strategy on every field once, so tiered JIT does not favour whoever runs last
        foreach (var field in fields)
        {
            foreach (var strategy in strategies)
            {
                for (var i = 0; i < 6; i++)
                {
                    using var warm = new ScanSession(database, "BenchUsers", inners[0].Rql);
                    Measure(warm, field, strategy);
                }
            }
        }

        // what the check of the single-valued flag against the data costs, per field
        using (var guardSession = new ScanSession(database, "BenchUsers", inners[0].Rql))
        {
            foreach (var field in fields)
            {
                var times = new List<double>();
                var covers = false;
                for (var i = 0; i < 9; i++)
                {
                    var sw = Stopwatch.StartNew();
                    covers = guardSession.Searcher.EntryTermMapCoversPostings(field);
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }

                var termCount = guardSession.Searcher.GetTermAmountInField(guardSession.Searcher.FieldMetadataBuilder(field));
                report.AppendLine($"guard {field,-8} terms={termCount,9:N0} covers={covers,-5} median {Median(times):F3} ms, {Median(times) * 1_000_000 / Math.Max(1, termCount):F0} ns per term");
            }
        }

        foreach (var (label, rql) in inners)
        {
            foreach (var field in fields)
            {
                var line = new StringBuilder();
                var keys = 0;
                foreach (var strategy in strategies)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        using var warm = new ScanSession(database, "BenchUsers", rql);
                        Measure(warm, field, strategy);
                    }

                    var total = new List<double>();
                    var wholes = new List<double>();
                    for (var i = 0; i < reps; i++)
                    {
                        using var session = new ScanSession(database, "BenchUsers", rql);
                        var sw = Stopwatch.StartNew();
                        var before = GC.GetAllocatedBytesForCurrentThread();
                        var count = Measure(session, field, strategy);
                        var whole = GC.GetAllocatedBytesForCurrentThread() - before;
                        sw.Stop();
                        total.Add(sw.Elapsed.TotalMilliseconds);
                        wholes.Add(whole);
                        keys = count;
                    }

                    line.Append($" | {strategy}: {Median(total),6:F1} ms, {Median(wholes) / 1024,7:N0} KB");
                }

                report.AppendLine($"{label,-6} field={field,-8} keys={keys,8:N0}{line}");
            }
        }

        output.WriteLine(report.ToString());
        AppendReport("RAVEN_JOIN_SCAN_BENCH_OUT", "joinfieldscan.txt", report);
    }

    private static int Measure(ScanSession session, string field, JoinFieldScan strategy)
    {
        using var scores = session.Reader.ScanTermScores(session.Query, session.DocumentsContext, field, strategy, false, CancellationToken.None);
        return scores.Count;
    }
}
