using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>
/// The time and the allocations of one query of a scored join and of the same query asked again, as a join index of the mode Indirect answers them. With RAVEN_READPATH_KEYS (the number of joined documents; the outer
/// side has twice as many) it is a measurement (output in RAVEN_READPATH_OUT when set), without it a small run that checks the counters. The allocations are those of the process (the server runs in it), so they
/// include what the client allocates for the same query: compare two builds, not the number with zero.
/// </summary>
public class JoinIndexReadPathBenchmark(ITestOutputHelper output) : RavenTestBase(output), IIndirectModeTest
{
    private static int Keys => int.TryParse(Environment.GetEnvironmentVariable("RAVEN_READPATH_KEYS"), out int keys) ? keys : 300;

    private static int Rounds => int.TryParse(Environment.GetEnvironmentVariable("RAVEN_READPATH_ROUNDS"), out int rounds) ? rounds : 30;

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AScoredJoinAndARepeatedOne()
    {
        int keys = Keys;
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, settings);
        for (int from = 0; from < keys; from += 2_000)
        {
            int start = from;
            h.Save(s =>
            {
                for (int i = start; i < Math.Min(keys, start + 2_000); i++)
                    s.Store(new JoinIndexHarness.Context { Country = i % 5 == 0 ? "de" : "nl", Name = "n" + i, Rank = i, IsActive = true, Title = "red fox" }, $"contexts/{i}");
            });
        }

        await h.AssertCurrentAsync();
        for (int from = 0; from < keys * 2; from += 2_000)
        {
            int start = from;
            h.Save(s =>
            {
                for (int i = start; i < Math.Min(keys * 2, start + 2_000); i++)
                    s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{i % keys}", Title = "t" + i }, $"conversations/{i}");
            });
        }

        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        var report = new List<string> { $"keys {keys}, outer {keys * 2}, rounds {Rounds}, load {Process.GetCurrentProcess().Id} host {Environment.ProcessorCount} cpus" };

        foreach ((string name, string sub, string tail) in new[]
        {
            ("scored join, order by score", " where Country = 'nl'", " order by score() limit 0, 50"),
            ("scored join in the where clause", " where Country = 'nl'", " where matches(c) or m.Title = 't45' order by score() limit 0, 50"),
            ("scored join, any, in the where clause", "", " where matches(c) or m.Title = 't45' order by score() limit 0, 50"),
        })
        {
            string rql = Join(sub, tail);
            long firstTicks = Stopwatch.GetTimestamp();
            QueryAnswer first = oracle.Run(rql);
            double firstMs = Stopwatch.GetElapsedTime(firstTicks).TotalMilliseconds;
            Assert.True(first.Total > 0, name);
            Assert.True(first.Counters.GetValueOrDefault("Join/c/JoinIndexServed") == 1, $"{name}: not served: {string.Join(", ", first.Counters)}");

            // asked again and again: the probe is in the cache, the lists are resident
            var times = new List<double>();
            long allocated = GC.GetTotalAllocatedBytes(true);
            QueryAnswer last = first;
            for (int i = 0; i < Rounds; i++)
            {
                long ticks = Stopwatch.GetTimestamp();
                last = oracle.Run(rql);
                times.Add(Stopwatch.GetElapsedTime(ticks).TotalMilliseconds);
            }

            allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            times.Sort();
            report.Add($"{name}: first {firstMs:F1} ms {Fmt(first)} | repeated: median {times[times.Count / 2]:F2} ms, min {times[0]:F2} ms, {allocated / Rounds / 1024.0:F1} KiB per query | {Fmt(last)}");
        }

        foreach (string line in report)
            Output.WriteLine(line);

        string path = Environment.GetEnvironmentVariable("RAVEN_READPATH_OUT");
        if (string.IsNullOrEmpty(path) == false)
            File.AppendAllLines(path, report);

        static string Fmt(QueryAnswer a) => $"total {a.Total}, " + string.Join(", ", a.Counters.Where(x => x.Key.StartsWith("Join/c/Join")).Select(x => $"{x.Key.Substring(7)}={x.Value}"));
    }
}
