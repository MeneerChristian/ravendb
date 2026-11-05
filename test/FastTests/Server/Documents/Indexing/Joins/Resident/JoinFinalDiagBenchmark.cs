using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Utils;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

// Harness only, not for merge. Opt-in: RAVEN_JOIN_FINAL=<arm>,<outer documents>,<documents on the hub>,<reps>,<queries per query cell>
// arm = Expanded | Cache (Indirect, resident cache on) | Off (Indirect, cache off) | LoadDoc (no join index: an index that does LoadDocument on the same documents).
// The data: 1,000 context documents (contexts/0 is the hub), <outer documents> conversations of which the first <hub> point at the hub and the others at the other contexts by a hash.
// Cells: Q (the joined query as the join index serves it, as the raw RQL join of the server, as the LoadDocument index), D (one change of the hub document: Country alternates).
// Lines go to RAVEN_JOIN_DIAG_FILE when it is set. Only counters that exist on both builds are read.
public class JoinFinalDiagBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Join = "FinalIdJoin";
    private const string LoadDocIndex = "Conversations/LoadContext";
    private const int Contexts = 1000;

    private void Report(string line)
    {
        Output.WriteLine(line);
        string file = Environment.GetEnvironmentVariable("RAVEN_JOIN_DIAG_FILE");
        if (string.IsNullOrEmpty(file) == false)
            File.AppendAllText(file, line + Environment.NewLine);
    }

    private static long Hash(int salt, long i)
    {
        ulong z = (ulong)i + 0x9E3779B97F4A7C15UL * (ulong)(salt + 1);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return (long)((z ^ (z >> 31)) & 0x7fffffffUL);
    }

    private sealed class NativeSampler : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        public readonly long Start = NativeMemory.TotalAllocatedMemory;
        public long Peak = NativeMemory.TotalAllocatedMemory;

        public NativeSampler()
        {
            _thread = new Thread(() =>
            {
                while (_stop == false)
                {
                    long now = NativeMemory.TotalAllocatedMemory;
                    if (now > Peak)
                        Peak = now;

                    Thread.Sleep(2);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
        }
    }

    private static double Mb(long bytes) => bytes / 1048576.0;

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static async Task WaitQuietAsync(JoinIndexHarness h, bool joinIndex)
    {
        DateTime until = DateTime.UtcNow.AddMinutes(30);
        long last = -1;
        int stable = 0;
        while (DateTime.UtcNow < until)
        {
            var stats = h.Store.Maintenance.Send(new GetIndexesStatisticsOperation());
            JoinIndex j = joinIndex ? h.Join(Join) : null;
            long seen = j == null ? -1 : j.Counters.Refreshes * 1000003 + j.Counters.NoOps * 1009 + j.Counters.Builds;
            if (stats.All(x => x.IsStale == false) && seen == last)
            {
                if (++stable >= 40)
                    return;
            }
            else
            {
                stable = 0;
                last = seen;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("the indexes stayed busy");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheHubChangeAndTheJoinedQuery()
    {
        string[] parts = (Environment.GetEnvironmentVariable("RAVEN_JOIN_FINAL") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
            return;

        string arm = parts[0];
        int outer = int.Parse(parts[1]);
        int hub = int.Parse(parts[2]);
        int reps = int.Parse(parts[3]);
        int queries = int.Parse(parts[4]);
        string cells = Environment.GetEnvironmentVariable("RAVEN_JOIN_FINAL_CELLS") ?? "QD";
        bool joinArm = arm != "LoadDoc";
        string shape = $"{arm}: outer {outer}, hub {hub}";

        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.IndirectEnabled)] = "true" };
        if (arm == "Off")
            settings["Join.ResidentCacheMaxMemoryInMb"] = "0";

        IndexDefinition loadDoc = new()
        {
            Name = LoadDocIndex,
            Maps =
            {
                "from c in docs.Conversations from id in c.ContextIds let ctx = LoadDocument(id, \"Contexts\") select new { c.Title, Country = ctx.Country, Name = ctx.Name }"
            },
            Fields = { ["Name"] = new IndexFieldOptions { Indexing = FieldIndexing.Exact } }
        };

        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, settings, putJoin: false, runInMemory: false,
            indexes: joinArm ? [JoinIndexHarness.MultiOuterIndex(), JoinIndexHarness.InnerIndex()] : [loadDoc]);

        JoinIndexHarness.Context Inner(int i, string country) => new() { Country = country, Name = "r" + Hash(33, i) % 200, Rank = 0, IsActive = true, Title = "x" };

        var load = Stopwatch.StartNew();
        await using (var bulk = h.Store.BulkInsert())
        {
            await bulk.StoreAsync(Inner(0, "hub0"), "contexts/0");
            for (int i = 1; i < Contexts; i++)
                await bulk.StoreAsync(Inner(i, "t" + Hash(32, i) % 100), $"contexts/{i}");

            for (int i = 0; i < outer; i++)
            {
                string key = i < hub ? "contexts/0" : "contexts/" + (1 + Hash(5, i) % (Contexts - 1));
                await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextIds = [key], Title = "t" }, $"conversations/{i}");
            }
        }

        await WaitQuietAsync(h, false);
        Report($"[{shape}] loaded and indexed in {load.Elapsed.TotalSeconds:F1} s");

        if (joinArm)
        {
            var definition = new IndexDefinition
            {
                Name = Join,
                Join = new JoinDefinition
                {
                    OuterIndex = JoinIndexHarness.OuterName, OuterField = "ContextId", InnerIndex = JoinIndexHarness.InnerName,
                    StorageMode = arm == "Expanded" ? JoinStorageMode.Expanded : JoinStorageMode.Indirect
                }.AddField("Country").AddField("Name").AddGrouping("IsActive", "true")
            };

            var build = Stopwatch.StartNew();
            await h.PutJoinAsync(definition);
            await WaitQuietAsync(h, true);
            Report($"[{shape}] build: {build.Elapsed.TotalSeconds:F2} s wall");
        }

        var oracle = joinArm ? new JoinIndexQueryOracle(h, Join) : null;

        string JoinRql(string country) =>
            $"from index '{JoinIndexHarness.OuterName}' as m join (from index '{JoinIndexHarness.InnerName}' where Country = '{country}') as c on m.ContextId = id(c) limit 0, 20";

        string LoadRql(string country) => $"from index '{LoadDocIndex}' where Country = '{country}' limit 0, 20";

        long RunTotal(string rql, bool joinIndex)
        {
            if (joinIndex)
                return oracle.Run(rql).Total;

            using var session = h.Store.OpenSession(new Raven.Client.Documents.Session.SessionOptions { NoCaching = true });
            session.Advanced.RawQuery<JoinIndexHarness.Conversation>(rql).Statistics(out var stats).ToList();
            return stats.TotalResults;
        }

        void QueryCell(string name, string country, string mode, string rql, bool joinIndex)
        {
            for (int i = 0; i < 5; i++)
                RunTotal(rql, joinIndex);

            GC.Collect(2, GCCollectionMode.Forced, true, true);
            long servedBefore = joinArm ? oracle.Info.Served : 0;
            long alloc = GC.GetTotalAllocatedBytes(true);
            long total = 0;
            var samples = new List<double>();
            using var sampler = new NativeSampler();
            for (int i = 0; i < queries; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                total = RunTotal(rql, joinIndex);
                samples.Add(Ms(Stopwatch.GetTimestamp() - t0));
            }

            sampler.Dispose();
            long allocated = GC.GetTotalAllocatedBytes(true) - alloc;
            long served = joinArm ? oracle.Info.Served - servedBefore : 0;
            double[] sorted = samples.OrderBy(x => x).ToArray();
            Report($"[{shape}] query {name} country={country} mode={mode} n={queries} total={total} served={served} medMs={sorted[sorted.Length / 2]:F2} minMs={sorted[0]:F2} maxMs={sorted[^1]:F2} " +
                   $"allocKBperQuery={allocated / 1024.0 / queries:F1} nativePeakAboveStartMB={Mb(sampler.Peak - sampler.Start):F1} msAll={string.Join(';', samples.Select(x => x.ToString("F2")))}");
        }

        void Queries(string phase)
        {
            foreach ((string name, string country) in new[] { ("nonhub", "t5"), ("hub", phase) })
            {
                if (joinArm)
                {
                    oracle.ForceFallback(false);
                    QueryCell(name, country, "join", JoinRql(country), true);
                    oracle.ForceFallback(true);
                    try
                    {
                        QueryCell(name, country, "rql-join", JoinRql(country), true);
                    }
                    finally
                    {
                        oracle.ForceFallback(false);
                    }
                }
                else
                {
                    QueryCell(name, country, "loaddoc", LoadRql(country), false);
                }
            }
        }

        if (cells.Contains('Q'))
            Queries("hub0");

        if (cells.Contains('D'))
        {
            string current = "hub0";
            for (int rep = 0; rep <= reps; rep++)
            {
                string next = "hub" + (rep + 1);
                await WaitQuietAsync(h, joinArm);
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                JoinIndex j = joinArm ? h.Join(Join) : null;
                long refreshTicks = j?.Counters.TotalRefreshTicks ?? 0, seenBefore = j == null ? 0 : j.Counters.Refreshes + j.Counters.NoOps;
                long alloc = GC.GetTotalAllocatedBytes(true);
                using var sampler = new NativeSampler();
                using var refreshSampler = j == null ? null : new RefreshAllocSampler(j);
                var watch = Stopwatch.StartNew();
                using (var session = h.Store.OpenSession())
                {
                    var doc = session.Load<JoinIndexHarness.Context>("contexts/0");
                    doc.Country = next;
                    session.SaveChanges();
                }

                double storeMs = watch.Elapsed.TotalMilliseconds;
                double wallMs;
                while (true)
                {
                    var stats = h.Store.Maintenance.Send(new GetIndexesStatisticsOperation());
                    bool seen = j == null || j.Counters.Refreshes + j.Counters.NoOps > seenBefore;
                    if (seen && stats.All(x => x.IsStale == false))
                    {
                        wallMs = watch.Elapsed.TotalMilliseconds;
                        break;
                    }

                    if (watch.Elapsed.TotalMinutes > 10)
                        throw new TimeoutException("the change was never indexed");

                    Thread.Sleep(1);
                }

                sampler.Dispose();
                refreshSampler?.Dispose();
                long allocated = GC.GetTotalAllocatedBytes(true) - alloc;
                Report($"[{shape}] cell D-hub-change entries={hub} n=1 rep={rep} warm={rep == 0}: storeMs={storeMs:F0} wallMs={wallMs:F0} joinBusyMs={(j == null ? double.NaN : Ms(j.Counters.TotalRefreshTicks - refreshTicks)):F0} " +
                       $"refreshAllocSampledMB={(refreshSampler == null ? double.NaN : Mb(refreshSampler.Sum)):F1} processAllocMB={Mb(allocated):F1} nativePeakAboveStartMB={Mb(sampler.Peak - sampler.Start):F1} refreshes={(j == null ? 0 : j.Counters.Refreshes + j.Counters.NoOps - seenBefore)}");
                current = next;
            }

            if (cells.Contains('Q'))
                Queries(current);
        }

        if (joinArm)
        {
            oracle.ForceFallback(false);
            long servedBefore = oracle.Info.Served;
            long viaJoin = oracle.Run(JoinRql("t5")).Total;
            bool served = oracle.Info.Served > servedBefore;
            oracle.ForceFallback(true);
            long viaScan = oracle.Run(JoinRql("t5")).Total;
            oracle.ForceFallback(false);
            Report($"[{shape}] end check t5: join index total {viaJoin}, rql-join total {viaScan}, served {served}");
            Assert.Equal(viaScan, viaJoin);
        }
    }
}
