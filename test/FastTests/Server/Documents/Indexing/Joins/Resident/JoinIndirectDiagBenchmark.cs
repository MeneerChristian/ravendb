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
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Utils;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

// Harness only, not for merge. Opt-in: RAVEN_JOIN_DIAG=<arm>,<outer documents>,<keys = inner documents>,<keys per outer document>[,<n documents per cell, default 100000>[,<reps, default 2>]]
// arm = Expanded | Cache (Indirect, resident cache on) | Off (Indirect, cache off). ONE join index (the id join o.ContextId = id(i)) per run, so nothing else shares the CPUs or the allocator.
// RAVEN_JOIN_DIAG_CELLS (default ABC) picks the cells (A, B, C), RAVEN_JOIN_DIAG_MICRO=1 runs the key-lookup micro loop, RAVEN_JOIN_DIAG_CHECK=1 runs the invariant checker at the end.
// Cells: A outer insert of n documents (and the delete of them), B update of n outer documents that keep their keys, C change of n inner documents (the country), each after one warm-up repetition.
// Every line carries the busy time, allocations and counters of the join index of that cell. Lines go to RAVEN_JOIN_DIAG_FILE when it is set.
// Sums LastRefreshAllocatedBytes of every refresh it sees (a refresh is seen when LastRefreshTicks changes), so a build without the total counter still has the allocation of the refreshes of a cell.
internal sealed class RefreshAllocSampler : IDisposable
{
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _sum;

    public long Sum => Interlocked.Read(ref _sum);

    public RefreshAllocSampler(JoinIndex joinIndex)
    {
        long last = joinIndex.Counters.LastRefreshTicks;
        _thread = new Thread(() =>
        {
            while (_stop == false)
            {
                long now = joinIndex.Counters.LastRefreshTicks;
                if (now != last)
                {
                    last = now;
                    Thread.Sleep(1);
                    Interlocked.Add(ref _sum, joinIndex.Counters.LastRefreshAllocatedBytes);
                }

                Thread.Sleep(1);
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

public class JoinIndirectDiagBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Join = "DiagIdJoin";

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
        public string PeakThreads = "";

        public NativeSampler()
        {
            _thread = new Thread(() =>
            {
                while (_stop == false)
                {
                    long now = NativeMemory.TotalAllocatedMemory;
                    if (now > Peak)
                    {
                        Peak = now;
                        if (now - Start > 100L * 1024 * 1024)
                        {
                            PeakThreads = string.Join(",", NativeMemory.AllThreadStats.OrderByDescending(x => x.TotalAllocated).Take(3)
                                .Select(x => $"{x.Name.Replace(',', '_').Replace(';', '_')}:{x.TotalAllocated / 1048576}"));
                        }
                    }

                    Thread.Sleep(5);
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

    private static async Task WaitQuietAsync(JoinIndexHarness h)
    {
        DateTime until = DateTime.UtcNow.AddMinutes(30);
        long last = -1;
        int stable = 0;
        while (DateTime.UtcNow < until)
        {
            var stats = h.Store.Maintenance.Send(new GetIndexesStatisticsOperation());
            JoinIndex j = h.Join(Join);
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

    private static double Mb(long bytes) => bytes / 1048576.0;

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string ResidentText(JoinResident resident)
    {
        if (resident == null)
            return "residentMB=n/a";

        var stats = resident.Cache.GetStats();
        return $"residentMB={Mb(stats.ResidentBytes):F2} residentNativeMB={Mb(stats.NativeBytes):F2} residentPeakMB={Mb(stats.PeakResidentBytes):F2} residentKeys={stats.ResidentKeys} cacheHits={stats.Hits} cacheMisses={stats.Misses} cacheEvictions={stats.Evictions} cacheRejected={stats.Rejected} cacheNotAdmitted={stats.NotAdmitted} cacheLowMemoryDrops={stats.LowMemoryDrops} cacheInvalidations={stats.Invalidations}";
    }

    private async Task CellAsync(JoinIndexHarness h, string shape, string cell, int n, bool warm, Func<Task> write)
    {
        await WaitQuietAsync(h);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        JoinIndex j = h.Join(Join);
        long refreshTicks = j.Counters.TotalRefreshTicks, refreshes = j.Counters.Refreshes, noOps = j.Counters.NoOps, refreshAlloc = j.Counters.RefreshAllocatedBytes;
        long keyLookups = j.Counters.KeyLookups, keyAtomUpdates = j.Counters.KeyAtomUpdates, outerKeyReads = j.Counters.OuterKeyReads, commits = j.Counters.Commits;
        long queued = j.Counters.QueuedOps, listUpdates = j.Counters.ListUpdates, flushes = j.Counters.Flushes, partial = j.Counters.PartialRefreshes, keyListChanges = j.Counters.KeyListChanges;
        long gcAlloc = GC.GetTotalAllocatedBytes(true);
        int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
        JoinResident resident = j.Resident;
        long establishes = resident?.Establishes ?? 0, applied = resident?.PatchesApplied ?? 0, tooLarge = resident?.PatchTooLarge ?? 0, warmUps = resident?.WarmUps ?? 0;
        using var sampler = new NativeSampler();
        using var refreshSampler = new RefreshAllocSampler(j);
        var watch = Stopwatch.StartNew();
        await write();
        double storeMs = watch.Elapsed.TotalMilliseconds;
        await WaitQuietAsync(h);
        double wallMs = watch.Elapsed.TotalMilliseconds - 1000;
        sampler.Dispose();
        refreshSampler.Dispose();
        Report($"[{shape}] cell {cell} n={n} warm={warm}: storeMs={storeMs:F0} wallMs~={wallMs:F0} joinBusyMs={Ms(j.Counters.TotalRefreshTicks - refreshTicks):F0} refreshes={j.Counters.Refreshes - refreshes} noOps={j.Counters.NoOps - noOps} partial={j.Counters.PartialRefreshes - partial} " +
               $"refreshAllocMB={Mb(j.Counters.RefreshAllocatedBytes - refreshAlloc):F1} refreshAllocSampledMB={Mb(refreshSampler.Sum):F1} processAllocMB={Mb(GC.GetTotalAllocatedBytes(true) - gcAlloc):F1} gc0={GC.CollectionCount(0) - gc0} gc2={GC.CollectionCount(2) - gc2} nativePeakAboveStartMB={Mb(sampler.Peak - sampler.Start):F1} nativePeakThreads={sampler.PeakThreads} " +
               $"queuedOps={j.Counters.QueuedOps - queued} listUpdates={j.Counters.ListUpdates - listUpdates} flushes={j.Counters.Flushes - flushes} keyListChanges={j.Counters.KeyListChanges - keyListChanges} " +
               $"keyLookups={j.Counters.KeyLookups - keyLookups} keyAtomUpdates={j.Counters.KeyAtomUpdates - keyAtomUpdates} outerKeyReads={j.Counters.OuterKeyReads - outerKeyReads} commits={j.Counters.Commits - commits} {ResidentText(resident)} " +
               $"establishes={(resident?.Establishes ?? 0) - establishes} patchesApplied={(resident?.PatchesApplied ?? 0) - applied} patchTooLarge={(resident?.PatchTooLarge ?? 0) - tooLarge} warmedKeys={(resident?.WarmUps ?? 0) - warmUps}");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task WhereTheIndirectIdJoinSpendsItsCells()
    {
        string[] parts = (Environment.GetEnvironmentVariable("RAVEN_JOIN_DIAG") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return;

        string arm = parts[0];
        int outer = int.Parse(parts[1]);
        int keys = int.Parse(parts[2]);
        int perEntry = int.Parse(parts[3]);
        int n = parts.Length > 4 ? int.Parse(parts[4]) : 100_000;
        int reps = parts.Length > 5 ? int.Parse(parts[5]) : 2;
        string cells = Environment.GetEnvironmentVariable("RAVEN_JOIN_DIAG_CELLS") ?? "ABC";
        bool micro = Environment.GetEnvironmentVariable("RAVEN_JOIN_DIAG_MICRO") == "1";
        bool check = Environment.GetEnvironmentVariable("RAVEN_JOIN_DIAG_CHECK") == "1";
        string shape = $"{arm}: outer {outer}, keys {keys}, {perEntry} per entry ({outer * (double)perEntry / keys:F1} rows per key)";

        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.IndirectEnabled)] = "true" };
        foreach (string pair in (Environment.GetEnvironmentVariable("RAVEN_JOIN_DIAG_SETTINGS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            settings[pair[..pair.IndexOf('=')]] = pair[(pair.IndexOf('=') + 1)..];

        // settings of the server (RAVEN_JOIN_DIAG_SERVER=key=value;key=value), for example Memory.UseTotalDirtyMemInsteadOfMemUsage=true: the test server does not read the RAVEN_ variables of the runner
        var serverSettings = new Dictionary<string, string>();
        foreach (string pair in (Environment.GetEnvironmentVariable("RAVEN_JOIN_DIAG_SERVER") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            serverSettings[pair[..pair.IndexOf('=')]] = pair[(pair.IndexOf('=') + 1)..];

        if (serverSettings.Count > 0)
            DoNotReuseServer(serverSettings);

        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, settings, putJoin: false, runInMemory: false, indexes: [JoinIndexHarness.MultiOuterIndex(), JoinIndexHarness.InnerIndex()]);

        JoinIndexHarness.Context Inner(int i, int version) => new()
        {
            Country = "t" + (Hash(32, i) + version) % 100, Name = "r" + Hash(33, i) % 2000, Rank = 0, IsActive = true, Title = "x"
        };

        string[] OuterKeys(long i, int salt)
        {
            var ids = new HashSet<string>();
            for (int j = 0; j < perEntry; j++)
                ids.Add("contexts/" + (i < keys && j == 0 && salt == 0 ? i : Hash(31 + salt * 7 + j, i) % keys));

            return ids.ToArray();
        }

        var load = Stopwatch.StartNew();
        await using (var bulk = h.Store.BulkInsert())
        {
            for (int i = 0; i < keys; i++)
                await bulk.StoreAsync(Inner(i, 0), $"contexts/{i}");

            for (int i = 0; i < outer; i++)
                await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextIds = OuterKeys(i, 0), Title = "t" }, $"conversations/{i}");
        }

        await WaitQuietAsync(h);
        Report($"[{shape}] loaded and indexed in {load.Elapsed.TotalSeconds:F1} s");

        var definition = new IndexDefinition
        {
            Name = Join,
            Join = new JoinDefinition
            {
                OuterIndex = JoinIndexHarness.OuterName, OuterField = "ContextId", InnerIndex = JoinIndexHarness.InnerName,
                StorageMode = arm == "Expanded" ? JoinStorageMode.Expanded : JoinStorageMode.Indirect
            }.AddField("Country").AddField("Name").AddGrouping("IsActive", "true")
        };

        long allocBefore = GC.GetTotalAllocatedBytes(true);
        var build = Stopwatch.StartNew();
        await h.PutJoinAsync(definition);
        await WaitQuietAsync(h);
        JoinIndex joinIndex = h.Join(Join);
        if (arm == "Off")
            joinIndex.UseResidentCache(null);

        Report($"[{shape}] build: {build.Elapsed.TotalSeconds:F2} s wall, build thread allocated {Mb(joinIndex.Counters.LastBuildAllocatedBytes):F1} MB, process allocated {Mb(GC.GetTotalAllocatedBytes(true) - allocBefore):F1} MB");

        if (joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot sizes))
        {
            using (sizes)
            {
                // pages of 8 KB by structure of the file, after the build
                var pages = sizes.GetPagesByStructure();
                Report($"[{shape}] file pages after the build: total {pages.Where(x => x.Key.EndsWith("(free)") == false).Sum(x => x.Value)} ({pages.Where(x => x.Key.EndsWith("(free)") == false).Sum(x => x.Value) * 8 / 1024.0:F1} MB), {string.Join(", ", pages.OrderByDescending(x => x.Value).Select(x => $"{x.Key}={x.Value}"))}");
            }
        }

        if (arm != "Expanded" && micro)
        {
            Assert.True(joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
            using (snapshot)
            {
                int count = Math.Min(keys, 100_000);
                byte[][] terms = Enumerable.Range(0, count).Select(i => System.Text.Encoding.UTF8.GetBytes("contexts/" + i)).ToArray();
                long[] refs = new long[count];
                Span<byte> copy = stackalloc byte[JoinLimits.MaxTermLength];
                for (int round = 0; round < 4; round++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    long a0 = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < count; i++)
                        Assert.True(snapshot.TryGetKey(terms[i], out refs[i]));

                    long t1 = Stopwatch.GetTimestamp();
                    long a1 = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < count; i++)
                        snapshot.CopyKey(refs[i], copy);

                    long t2 = Stopwatch.GetTimestamp();
                    if (snapshot.Schema.KeysAreInnerIds == false)
                    {
                        long t3 = Stopwatch.GetTimestamp();
                        long any = JoinReader.CodeOfAny();
                        long sum = 0;
                        for (int i = 0; i < count; i++)
                            sum += snapshot.GetPairCount(refs[(i * 7919L) % count == 0 ? i : (int)((i * 7919L) % count)], any);

                        long t4 = Stopwatch.GetTimestamp();
                        Report($"[{shape}] micro round {round} Int64 lookup (GetPairCount, random order): {(t4 - t3) * 1e6 / Stopwatch.Frequency / count:F3} us per call, sum {sum}");
                    }

                    Report($"[{shape}] micro round {round}: TryGetKey {(t1 - t0) * 1e6 / Stopwatch.Frequency / count:F3} us and {(a1 - a0) / (double)count:F0} B per call, CopyKey {(t2 - t1) * 1e6 / Stopwatch.Frequency / count:F3} us per call");
                }
            }
        }

        int next = outer + 10_000_000;
        for (int rep = 0; cells.Contains('A') && rep <= reps; rep++)
        {
            bool warm = rep == 0;
            int first = next;
            next += n;

            await CellAsync(h, shape, "A-insert", n, warm, async () =>
            {
                await using var bulk = h.Store.BulkInsert();
                for (int i = 0; i < n; i++)
                    await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextIds = OuterKeys(first + i, 1), Title = "t" }, $"conversations/{first + i}");
            });

            await CellAsync(h, shape, "A-delete", n, warm, async () =>
            {
                await Task.Yield();
                for (int i = 0; i < n; i += 1000)
                {
                    using var session = h.Store.OpenSession();
                    for (int k = 0; k < 1000 && i + k < n; k++)
                        session.Delete($"conversations/{first + i + k}");

                    session.SaveChanges();
                }
            });
        }

        for (int rep = 0; cells.Contains('B') && rep <= reps; rep++)
        {
            await CellAsync(h, shape, "B-update-keep-keys", Math.Min(n, outer), rep == 0, async () =>
            {
                await using var bulk = h.Store.BulkInsert();
                for (int i = 0; i < Math.Min(n, outer); i++)
                    await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextIds = OuterKeys(i, 0), Title = "u" + rep }, $"conversations/{i}");
            });
        }

        for (int rep = 0; cells.Contains('C') && rep <= reps; rep++)
        {
            int count = Math.Min(n, keys);
            await CellAsync(h, shape, "C-inner-change", count, rep == 0, async () =>
            {
                await using var bulk = h.Store.BulkInsert();
                for (int i = 0; i < count; i++)
                    await bulk.StoreAsync(Inner(i, 100 + rep), $"contexts/{i}");
            });
        }


        if (cells.Contains('D') || cells.Contains('Q'))
        {
            var oracle = new JoinIndexQueryOracle(h, Join);
            string JoinRql(string country) =>
                $"from index '{JoinIndexHarness.OuterName}' as m join (from index '{JoinIndexHarness.InnerName}' where Country = '{country}') as c on m.ContextId = id(c) limit 0, 20";

            string hubCountry = "hub0";
            if (cells.Contains('D'))
            {
                for (int rep = 0; rep <= reps; rep++)
                {
                    string nextHub = "hub" + (rep + 1);
                    await WaitQuietAsync(h);
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    JoinIndex j = h.Join(Join);
                    long refreshTicks = j.Counters.TotalRefreshTicks, seenBefore = j.Counters.Refreshes + j.Counters.NoOps;
                    long alloc = GC.GetTotalAllocatedBytes(true);
                    using var sampler = new NativeSampler();
                    var watch = Stopwatch.StartNew();
                    using (var session = h.Store.OpenSession())
                    {
                        var doc = session.Load<JoinIndexHarness.Context>("contexts/0");
                        doc.Country = nextHub;
                        session.SaveChanges();
                    }

                    double wallMs;
                    while (true)
                    {
                        var stats = h.Store.Maintenance.Send(new GetIndexesStatisticsOperation());
                        if (j.Counters.Refreshes + j.Counters.NoOps > seenBefore && stats.All(x => x.IsStale == false))
                        {
                            wallMs = watch.Elapsed.TotalMilliseconds;
                            break;
                        }

                        if (watch.Elapsed.TotalMinutes > 10)
                            throw new TimeoutException("the change was never indexed");

                        Thread.Sleep(1);
                    }

                    sampler.Dispose();
                    Report($"[{shape}] cell D-key-change n=1 rep={rep} warm={rep == 0}: wallMs={wallMs:F0} joinBusyMs={Ms(j.Counters.TotalRefreshTicks - refreshTicks):F0} processAllocMB={Mb(GC.GetTotalAllocatedBytes(true) - alloc):F1} nativePeakAboveStartMB={Mb(sampler.Peak - sampler.Start):F1} {ResidentText(j.Resident)}");
                    hubCountry = nextHub;
                }
            }

            if (cells.Contains('Q'))
            {
                int queries = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_DIAG_QUERIES") ?? "30");
                foreach ((string name, string country) in new[] { ("pct1", "t5"), ("onekey", hubCountry) })
                {
                    for (int round = 0; round <= reps; round++)
                    {
                        foreach (bool fallback in new[] { false, true })
                        {
                            oracle.ForceFallback(fallback);
                            try
                            {
                                string rql = JoinRql(country);
                                for (int i = 0; i < 5; i++)
                                    oracle.Run(rql);

                                long servedBefore = oracle.Info.Served;
                                long alloc = GC.GetTotalAllocatedBytes(true);
                                long total = 0;
                                var samples = new List<double>();
                                for (int i = 0; i < queries; i++)
                                {
                                    long t0 = Stopwatch.GetTimestamp();
                                    total = oracle.Run(rql).Total;
                                    samples.Add(Ms(Stopwatch.GetTimestamp() - t0));
                                }

                                double[] sorted = samples.OrderBy(x => x).ToArray();
                                Report($"[{shape}] query {name} country={country} mode={(fallback ? "rql-join" : "join")} round={round} warm={round == 0} n={queries} total={total} served={oracle.Info.Served - servedBefore} medMs={sorted[sorted.Length / 2]:F3} minMs={sorted[0]:F3} maxMs={sorted[^1]:F3} allocKBperQuery={(GC.GetTotalAllocatedBytes(true) - alloc) / 1024.0 / queries:F1}");
                            }
                            finally
                            {
                                oracle.ForceFallback(false);
                            }
                        }
                    }
                }
            }
        }

        JoinResident end = h.Join(Join).Resident;
        if (end != null)
            Report($"[{shape}] resident at the end: {Mb(end.Cache.GetStats().ResidentBytes):F2} MB, keys {end.Cache.GetStats().ResidentKeys}, establishes {end.Establishes}, patches applied {end.PatchesApplied}, too large {end.PatchTooLarge}, epoch mismatches {end.PatchEpochMismatches}, {ResidentText(end)}, budgetMB={Mb(end.Cache.BudgetBytes):F0}, workingSetMB={Mb(Environment.WorkingSet):F0}, gcHeapMB={Mb(GC.GetTotalMemory(false)):F0}, nativeTotalMB={Mb(NativeMemory.TotalAllocatedMemory):F0}");

        if (check == false)
            return;

        h.TrackJoin(Join);
        JoinIndexInvariantChecker.Result result = await h.CheckAsync(TimeSpan.FromMinutes(10), Join);
        Report($"[{shape}] invariant check: ok {result.Ok}, compared {result.Compared}");
        Assert.True(result.Ok, result.ToString());
    }
}
