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

// Harness only, not for merge (T2: LoadDocument vs join index vs server join, write cells A and D, query Q, throughput P). Opt-in: RAVEN_JOIN_T2=<arm>,<outer>,<hub>,<reps>,<queries>[,<qps seconds>[,<qps reps>]] (old: RAVEN_JOIN_FINAL=<arm>,<outer documents>,<documents on the hub>,<reps>,<queries per query cell>
// arm = Expanded | Cache (Indirect, resident cache on) | Off (Indirect, cache off) | LoadDoc (no join index: an index that does LoadDocument on the same documents).
// The data: 1,000 context documents (contexts/0 is the hub), <outer documents> conversations of which the first <hub> point at the hub and the others at the other contexts by a hash.
// Cells: Q (the joined query as the join index serves it, as the raw RQL join of the server, as the LoadDocument index), D (one change of the hub document: Country alternates).
// Lines go to RAVEN_JOIN_DIAG_FILE when it is set. Only counters that exist on both builds are read.
public class JoinT2Benchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Join = "T2IdJoin";
    private const string LoadDocIndex = "Conversations/LoadContext";
    private static readonly int Contexts = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_T2_CONTEXTS") ?? "1000");

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

    private static long CgroupFile(string file, string key)
    {
        try
        {
            foreach (string line in File.ReadLines("/sys/fs/cgroup/" + file))
            {
                if (key == null)
                    return long.Parse(line.Trim());

                if (line.StartsWith(key + " ", StringComparison.Ordinal))
                    return long.Parse(line.AsSpan(key.Length + 1));
            }
        }
        catch
        {
        }

        return -1;
    }

    private static long CgroupWriteBytes()
    {
        try
        {
            long sum = 0;
            foreach (string line in File.ReadLines("/sys/fs/cgroup/io.stat"))
                foreach (string part in line.Split(' '))
                    if (part.StartsWith("wbytes=", StringComparison.Ordinal))
                        sum += long.Parse(part.AsSpan(7));

            return sum;
        }
        catch
        {
            return -1;
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task LoadDocumentJoinAndServerJoin()
    {
        string[] parts = (Environment.GetEnvironmentVariable("RAVEN_JOIN_T2") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
            return;

        string arm = parts[0];
        int outer = int.Parse(parts[1]);
        int hub = int.Parse(parts[2]);
        int reps = int.Parse(parts[3]);
        int queries = int.Parse(parts[4]);
        int qpsSeconds = parts.Length > 5 ? int.Parse(parts[5]) : 30;
        int qpsReps = parts.Length > 6 ? int.Parse(parts[6]) : 5;
        string cells = Environment.GetEnvironmentVariable("RAVEN_JOIN_T2_CELLS") ?? "ADQ";
        int aN = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_T2_N") ?? "10000");
        bool joinArm = arm != "LoadDoc";
        string shape = $"{arm}: outer {outer}, hub {hub}" + (Contexts == 1000 ? "" : $", contexts {Contexts}");

        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.IndirectEnabled)] = "true" };
        var serverSettings = new Dictionary<string, string>();
        foreach (string pair in (Environment.GetEnvironmentVariable("RAVEN_JOIN_DIAG_SERVER") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            serverSettings[pair[..pair.IndexOf('=')]] = pair[(pair.IndexOf('=') + 1)..];
        if (serverSettings.Count > 0)
            DoNotReuseServer(serverSettings);

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
        string OuterContext(long i) => "contexts/" + (1 + Hash(5, i) % (Contexts - 1));

        var load = Stopwatch.StartNew();
        await using (var bulk = h.Store.BulkInsert())
        {
            await bulk.StoreAsync(Inner(0, "hub0"), "contexts/0");
            for (int i = 1; i < Contexts; i++)
                await bulk.StoreAsync(Inner(i, "t" + Hash(32, i) % 100), $"contexts/{i}");

            for (int i = 0; i < outer; i++)
            {
                string key = i < hub ? "contexts/0" : OuterContext(i);
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
                    StorageMode = arm == "Expanded" ? JoinStorageMode.Expanded : arm == "Auto" ? JoinStorageMode.Auto : JoinStorageMode.Indirect
                }.AddField("Country").AddField("Name").AddGrouping("IsActive", "true")
            };

            var build = Stopwatch.StartNew();
            await h.PutJoinAsync(definition);
            await WaitQuietAsync(h, true);
            var js = h.Store.Maintenance.Send(new GetIndexStatisticsOperation(Join)).Join;
            Report($"[{shape}] build: {build.Elapsed.TotalSeconds:F2} s wall; StorageMode={js?.StorageMode} ResolvedStorageMode={js?.ResolvedStorageMode} AutoModeReason={js?.AutoModeReason}");
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

        void QueryCell(string name, string country, string mode, string rql, bool joinIndex, int round = 1)
        {
            for (int i = 0; i < 5; i++)
                RunTotal(rql, joinIndex);

            GC.Collect(2, GCCollectionMode.Forced, true, true);
            long servedBefore = joinArm ? oracle.Info.Served : 0;
            var cacheBefore = joinArm ? h.Join(Join).Resident?.Cache.GetStats() : null;
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
            var cacheAfter = joinArm ? h.Join(Join).Resident?.Cache.GetStats() : null;
            Report($"[{shape}] query {name} country={country} mode={mode} round={round} warm={round == 0} hitsPerQuery={(cacheAfter == null ? 0 : (cacheAfter.Value.Hits - cacheBefore.Value.Hits) / (double)queries):F1} missesPerQuery={(cacheAfter == null ? 0 : (cacheAfter.Value.Misses - cacheBefore.Value.Misses) / (double)queries):F1} n={queries} total={total} served={served} medMs={sorted[sorted.Length / 2]:F2} minMs={sorted[0]:F2} maxMs={sorted[^1]:F2} " +
                   $"allocKBperQuery={allocated / 1024.0 / queries:F1} nativePeakAboveStartMB={Mb(sampler.Peak - sampler.Start):F1} msAll={string.Join(';', samples.Select(x => x.ToString("F2")))}");
        }

        void Queries(string phase)
        {
            foreach ((string name, string country) in new[] { ("pct1", "t5"), ("hub", phase) })
            {
                for (int round = 0; round <= (int.TryParse(Environment.GetEnvironmentVariable("RAVEN_JOIN_T2_QROUNDS"), out int qr) ? qr : Math.Max(reps, 1)); round++)
                {
                    if (joinArm)
                    {
                        oracle.ForceFallback(false);
                        QueryCell(name, country, "join", JoinRql(country), true, round);
                        oracle.ForceFallback(true);
                        try
                        {
                            QueryCell(name, country, "rql-join", JoinRql(country), true, round);
                        }
                        finally
                        {
                            oracle.ForceFallback(false);
                        }
                    }
                    else
                    {
                        QueryCell(name, country, "loaddoc", LoadRql(country), false, round);
                    }
                }
            }
        }

        async Task Measure(string cell, int n, int rep, Func<Task> write, int quietWindowMs, string effectIndex, long effectMaps)
        {
            await WaitQuietAsync(h, joinArm);
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            JoinIndex j = joinArm ? h.Join(Join) : null;
            var before = h.Store.Maintenance.Send(new GetIndexesStatisticsOperation()).ToDictionary(x => x.Name, x => (long)x.MapAttempts + (x.MapReferenceAttempts ?? 0));
            long refreshTicks = j?.Counters.TotalRefreshTicks ?? 0, seenBefore = j == null ? 0 : j.Counters.Refreshes + j.Counters.NoOps;
            long queued = j?.Counters.QueuedOps ?? 0, listUpdates = j?.Counters.ListUpdates ?? 0, flushes = j?.Counters.Flushes ?? 0, keyListChanges = j?.Counters.KeyListChanges ?? 0;
            long keyLookups = j?.Counters.KeyLookups ?? 0, commits = j?.Counters.Commits ?? 0, refreshAlloc = j?.Counters.RefreshAllocatedBytes ?? 0;
            long alloc = GC.GetTotalAllocatedBytes(true);
            int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
            TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime;
            long io = CgroupWriteBytes();
            using var sampler = new NativeSampler();
            using var refreshSampler = j == null ? null : new RefreshAllocSampler(j);
            var watch = Stopwatch.StartNew();
            await write();
            double storeMs = watch.Elapsed.TotalMilliseconds;
            double wallMs = -1, quietSince = -1;
            while (true)
            {
                var stats = h.Store.Maintenance.Send(new GetIndexesStatisticsOperation());
                bool quiet = stats.All(x => x.IsStale == false) && (long)stats.First(x => x.Name == effectIndex).MapAttempts + (stats.First(x => x.Name == effectIndex).MapReferenceAttempts ?? 0) - before[effectIndex] >= effectMaps && (j == null || j.Counters.Refreshes + j.Counters.NoOps > seenBefore);
                double now = watch.Elapsed.TotalMilliseconds;
                if (quiet)
                {
                    if (quietSince < 0)
                        quietSince = now;

                    if (now - quietSince >= quietWindowMs)
                    {
                        wallMs = quietSince;
                        break;
                    }
                }
                else
                {
                    quietSince = -1;
                }

                if (now > 600000)
                    throw new TimeoutException("the change was never indexed");

                Thread.Sleep(quietWindowMs > 0 ? 5 : 1);
            }

            double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
            sampler.Dispose();
            refreshSampler?.Dispose();
            long allocated = GC.GetTotalAllocatedBytes(true) - alloc;
            var after = h.Store.Maintenance.Send(new GetIndexesStatisticsOperation()).ToDictionary(x => x.Name, x => (long)x.MapAttempts + (x.MapReferenceAttempts ?? 0));
            string maps = string.Join(';', after.Select(kv => $"{kv.Key}={kv.Value - (before.TryGetValue(kv.Key, out long b) ? b : 0)}"));
            Report($"[{shape}] cell {cell} n={n} rep={rep} warm={rep == 0}: storeMs={storeMs:F0} wallMs={wallMs:F0} cpuMs={cpuMs:F0} joinBusyMs={(j == null ? double.NaN : Ms(j.Counters.TotalRefreshTicks - refreshTicks)):F0} " +
                   $"mapAttemptsDelta=[{maps}] refreshes={(j == null ? 0 : j.Counters.Refreshes + j.Counters.NoOps - seenBefore)} queuedOps={(j?.Counters.QueuedOps ?? 0) - queued} listUpdates={(j?.Counters.ListUpdates ?? 0) - listUpdates} " +
                   $"flushes={(j?.Counters.Flushes ?? 0) - flushes} keyListChanges={(j?.Counters.KeyListChanges ?? 0) - keyListChanges} keyLookups={(j?.Counters.KeyLookups ?? 0) - keyLookups} commits={(j?.Counters.Commits ?? 0) - commits} " +
                   $"refreshAllocMB={Mb((j?.Counters.RefreshAllocatedBytes ?? 0) - refreshAlloc):F1} refreshAllocSampledMB={(refreshSampler == null ? double.NaN : Mb(refreshSampler.Sum)):F1} processAllocMB={Mb(allocated):F1} gc0={GC.CollectionCount(0) - gc0} gc2={GC.CollectionCount(2) - gc2} " +
                   $"nativePeakAboveStartMB={Mb(sampler.Peak - sampler.Start):F1} cgroupWriteMB={(io < 0 ? -1 : Mb(CgroupWriteBytes() - io)):F1}");
        }

        if (cells.Contains('Q'))
            Queries("hub0");

        if (cells.Contains('A'))
        {
            for (int rep = 0; rep <= reps; rep++)
            {
                int r = rep;
                await Measure("A-insert", aN, rep, async () =>
                {
                    await using var bulk = h.Store.BulkInsert();
                    for (int i = 0; i < aN; i++)
                        await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextIds = [OuterContext(7_000_000L + i)], Title = "t" }, $"conversations/a{r}_{i}");
                }, 300, joinArm ? JoinIndexHarness.OuterName : LoadDocIndex, aN);

                await WaitQuietAsync(h, joinArm);
                await Task.Run(() =>
                {
                    var ids = Enumerable.Range(0, aN).Select(i => $"conversations/a{r}_{i}").ToList();
                    foreach (var chunk in ids.Chunk(1000))
                    {
                        using var s = h.Store.OpenSession();
                        foreach (string id in chunk)
                            s.Delete(id);
                        s.SaveChanges();
                    }
                });
            }
        }

        string current = "hub0";
        if (cells.Contains('D'))
        {
            for (int rep = 0; rep <= reps; rep++)
            {
                string next = "hub" + (rep + 1);
                await Measure("D-hub-change", hub, rep, () =>
                {
                    using var session = h.Store.OpenSession();
                    var doc = session.Load<JoinIndexHarness.Context>("contexts/0");
                    doc.Country = next;
                    session.SaveChanges();
                    return Task.CompletedTask;
                }, 0, joinArm ? JoinIndexHarness.InnerName : LoadDocIndex, joinArm ? 1 : hub);
                current = next;
            }
        }

        if (cells.Contains('Q') && cells.Contains('D'))
            Queries(current);

        if (cells.Contains('M'))
        {
            double[] ratios = (Environment.GetEnvironmentVariable("RAVEN_JOIN_T2_RATIOS") ?? "0.01,0.001,0.01,0.02,0.05,0.1,1").Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            int innerChanges = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_T2_INNER") ?? "10000");
            string readRql = joinArm ? JoinRql("t5") : LoadRql("t5");
            for (int ph = 0; ph < ratios.Length; ph++)
            {
                double ratio = ratios[ph];
                int q = (int)Math.Round(ratio * aN);
                await WaitQuietAsync(h, joinArm);
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                JoinIndex j = joinArm ? h.Join(Join) : null;
                long seenBefore = j == null ? 0 : j.Counters.Refreshes + j.Counters.NoOps;
                long queued = j?.Counters.QueuedOps ?? 0, keyLookups = j?.Counters.KeyLookups ?? 0, flushes = j?.Counters.Flushes ?? 0, listUpdates = j?.Counters.ListUpdates ?? 0, keyListChanges = j?.Counters.KeyListChanges ?? 0;
                long cg0 = CgroupFile("cpu.stat", "usage_usec"), user0 = CgroupFile("cpu.stat", "user_usec");
                TimeSpan cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
                long alloc0 = GC.GetTotalAllocatedBytes(true);
                int p = ph;
                long errors = 0, done = 0;
                var watch = Stopwatch.StartNew();
                var writer = Task.Run(async () =>
                {
                    await using var bulk = h.Store.BulkInsert();
                    for (int i = 0; i < aN; i++)
                        await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextIds = [OuterContext(9_000_000L + i)], Title = "t" }, $"conversations/m{p}_{i}");
                });
                var changer = Task.Run(() =>
                {
                    foreach (var chunk in Enumerable.Range(1, innerChanges).Chunk(1000))
                    {
                        using var s2 = h.Store.OpenSession();
                        foreach ((string key, var doc) in s2.Load<JoinIndexHarness.Context>(chunk.Select(id => "contexts/" + id)))
                            doc.Name = "m" + p + "_" + key;
                        s2.SaveChanges();
                    }
                });
                var reader = Task.Run(() =>
                {
                    for (int i = 0; i < q; i++)
                    {
                        try
                        {
                            RunTotal(readRql, false);
                            Interlocked.Increment(ref done);
                        }
                        catch
                        {
                            Interlocked.Increment(ref errors);
                        }
                    }
                });
                await Task.WhenAll(writer, changer, reader);
                double storeMs = watch.Elapsed.TotalMilliseconds;
                double quietSince = -1, wallMs;
                while (true)
                {
                    var stats = h.Store.Maintenance.Send(new GetIndexesStatisticsOperation());
                    bool quiet = stats.All(x => x.IsStale == false);
                    double now = watch.Elapsed.TotalMilliseconds;
                    if (quiet)
                    {
                        if (quietSince < 0)
                            quietSince = now;

                        if (now - quietSince >= 300)
                        {
                            wallMs = quietSince;
                            break;
                        }
                    }
                    else
                    {
                        quietSince = -1;
                    }

                    Thread.Sleep(5);
                }

                long cg1 = CgroupFile("cpu.stat", "usage_usec"), user1 = CgroupFile("cpu.stat", "user_usec");
                double procCpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
                Report($"[{shape}] cell M-mixed ratio={ratio} warm={ph == 0}: inserts={aN} innerChanges={innerChanges} queries={q} done={done} errors={errors} storeAndReadMs={storeMs:F0} wallMs={wallMs:F0} cgroupCpuMs={(cg1 - cg0) / 1000.0:F0} cgroupUserMs={(user1 - user0) / 1000.0:F0} processCpuMs={procCpuMs:F0} " +
                       $"processAllocMB={Mb(GC.GetTotalAllocatedBytes(true) - alloc0):F0} memoryPeakMB={CgroupFile("memory.peak", null) / 1048576.0:F0} refreshes={(j == null ? 0 : j.Counters.Refreshes + j.Counters.NoOps - seenBefore)} queuedOps={(j?.Counters.QueuedOps ?? 0) - queued} keyListChanges={(j?.Counters.KeyListChanges ?? 0) - keyListChanges} " +
                       $"listUpdates={(j?.Counters.ListUpdates ?? 0) - listUpdates} flushes={(j?.Counters.Flushes ?? 0) - flushes} keyLookups={(j?.Counters.KeyLookups ?? 0) - keyLookups} {JoinIndirectDiagBenchmark.ResidentText(j?.Resident)}");

                await Task.Run(() =>
                {
                    foreach (var chunk in Enumerable.Range(0, aN).Select(i => $"conversations/m{p}_{i}").Chunk(1000))
                    {
                        using var s3 = h.Store.OpenSession();
                        foreach (string id in chunk)
                            s3.Delete(id);
                        s3.SaveChanges();
                    }
                });
            }
        }

        if (cells.Contains('P'))
        {
            await WaitQuietAsync(h, joinArm);
            var modes = joinArm ? new[] { "join", "rql-join" } : new[] { "loaddoc" };
            string rqlJoin = JoinRql("t5"), rqlLoad = LoadRql("t5");
            for (int w = 0; w < 300; w++)
                RunTotal(joinArm ? rqlJoin : rqlLoad, joinArm);

            for (int rep = 1; rep <= qpsReps; rep++)
            {
                for (int mi = 0; mi < modes.Length; mi++)
                {
                    string mode = modes[(mi + rep) % modes.Length];
                    foreach (int clients in new[] { 1, 4 })
                    {
                        if (joinArm)
                            oracle.ForceFallback(mode == "rql-join");

                        string rql = joinArm ? rqlJoin : rqlLoad;
                        long servedBefore = joinArm ? oracle.Info.Served : 0;
                        var lat = new List<double>[clients];
                        long[] errors = new long[clients];
                        long total = -1;
                        var end = DateTime.UtcNow.AddSeconds(qpsSeconds);
                        var threads = Enumerable.Range(0, clients).Select(c => new Thread(() =>
                        {
                            lat[c] = new List<double>(100000);
                            while (DateTime.UtcNow < end)
                            {
                                long t0 = Stopwatch.GetTimestamp();
                                try
                                {
                                    using var s = h.Store.OpenSession(new Raven.Client.Documents.Session.SessionOptions { NoCaching = true });
                                    s.Advanced.RawQuery<JoinIndexHarness.Conversation>(rql).Statistics(out var st).ToList();
                                    total = st.TotalResults;
                                    lat[c].Add(Ms(Stopwatch.GetTimestamp() - t0));
                                }
                                catch
                                {
                                    errors[c]++;
                                }
                            }
                        })).ToArray();
                        var wall = Stopwatch.StartNew();
                        foreach (var t in threads) t.Start();
                        foreach (var t in threads) t.Join();
                        double secs = wall.Elapsed.TotalSeconds;
                        double[] all = lat.SelectMany(x => x).OrderBy(x => x).ToArray();
                        long served = joinArm ? oracle.Info.Served - servedBefore : 0;
                        Report($"[{shape}] qps pct1 mode={mode} clients={clients} rep={rep} seconds={secs:F1} requests={all.Length} errors={errors.Sum()} qps={all.Length / secs:F0} total={total} served={served} " +
                               $"p50Ms={all[all.Length / 2]:F3} p99Ms={all[(int)(all.Length * 0.99)]:F3} meanMs={all.Average():F3} maxMs={all[^1]:F2}");
                        if (joinArm)
                            oracle.ForceFallback(false);
                    }
                }
            }
        }

        await WaitQuietAsync(h, joinArm);
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
