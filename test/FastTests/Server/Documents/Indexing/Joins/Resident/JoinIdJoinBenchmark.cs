using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Corax.Querying.Matches.Meta;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Server;
using Tests.Infrastructure;
using Voron;
using Voron.Util;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

// Harness only, not for merge. Opt-in: RAVEN_JOIN_IDJOIN_BENCH=<outer documents>,<keys = inner documents>[,<repetitions, default 3>]; lines are appended to RAVEN_JOIN_IDJOIN_BENCH_FILE when it is set.
// The join o.ContextId = id(i), the same two indexes, three join indexes side by side: Expanded, Indirect with its resident cache, Indirect without a cache. The outer document i names the inner document i % keys for i < keys
// and a hashed one after that, so outer = keys is one outer row per key and outer = 10 x keys is ten.
public class JoinIdJoinBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string ExpandedName = "IdExpanded";
    private const string CacheName = "IdIndirectCache";
    private const string OffName = "IdIndirectOff";

    private static readonly FieldInfo EnvironmentField = typeof(Raven.Server.Documents.Indexes.Index).GetField("_environment", BindingFlags.Instance | BindingFlags.NonPublic);

    private void Report(string line)
    {
        Output.WriteLine(line);
        string file = Environment.GetEnvironmentVariable("RAVEN_JOIN_IDJOIN_BENCH_FILE");
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

    private static async Task WaitAsync(JoinIndexHarness h, params string[] residentJoins)
    {
        DateTime until = DateTime.UtcNow.AddMinutes(30);
        while (DateTime.UtcNow < until)
        {
            var stats = h.Store.Maintenance.Send(new GetIndexesStatisticsOperation());
            if (stats.All(x => x.IsStale == false) && residentJoins.All(j => h.Join(j).Resident != null))
                return;

            await Task.Delay(20);
        }

        throw new TimeoutException("the indexes stayed stale");
    }

    private static (double Median, double P95) Time(Func<long> run, int rounds)
    {
        for (int i = 0; i < 10; i++)
            run();

        var times = new double[rounds];
        for (int i = 0; i < rounds; i++)
        {
            long start = Stopwatch.GetTimestamp();
            run();
            times[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }

        Array.Sort(times);
        return (times[rounds / 2], times[(int)(rounds * 0.95)]);
    }

    private static long Drain(IQueryMatch match)
    {
        var buffer = new long[4096];
        long count = 0;
        int read;
        while ((read = match.Fill(buffer)) > 0)
            count += read;

        return count;
    }

    private (double UsedMb, double AllocatedMb) FileSize(JoinIndexHarness h, string name)
    {
        var environment = (StorageEnvironment)EnvironmentField.GetValue(h.Database.IndexStore.GetIndex(name));
        var stats = environment.Stats();
        return (stats.UsedDataFileSizeInBytes / 1048576.0, stats.AllocatedDataFileSizeInBytes / 1048576.0);
    }

    private sealed record Marks(long Ticks, long Refreshes, long Alloc);

    private static Marks Mark(JoinIndex j) => new(j.Counters.TotalRefreshTicks, j.Counters.Refreshes, j.Counters.LastRefreshAllocatedBytes);

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string Busy(JoinIndex j, Marks before, int rows) =>
        $"{Ms(j.Counters.TotalRefreshTicks - before.Ticks):F2} ms ({Ms(j.Counters.TotalRefreshTicks - before.Ticks) / Math.Max(1, rows) * 1000:F2} us per row; {j.Counters.Refreshes - before.Refreshes} refreshes, last refresh allocated {j.Counters.LastRefreshAllocatedBytes / 1024.0:F0} KB)";

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheIdJoinExpandedAgainstIndirectWithAndWithoutTheCache()
    {
        string[] parts = (Environment.GetEnvironmentVariable("RAVEN_JOIN_IDJOIN_BENCH") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return;

        int outer = int.Parse(parts[0]);
        int keys = int.Parse(parts[1]);
        int reps = parts.Length > 2 ? int.Parse(parts[2]) : 3;
        string shape = $"id join, outer {outer}, keys {keys} ({outer / (double)keys:F1} outer rows per key)";

        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.IndirectEnabled)] = "true",
            [RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = "5000000",
            ["Indexing.MapBatchSize"] = "5000000"
        }, putJoin: false, runInMemory: false);

        JoinIndexHarness.Context Inner(int i, int version) => new()
        {
            Country = "t" + (Hash(32, i) + version) % 100, Name = "r" + Hash(33, i) % 2000, Rank = 0, IsActive = true, Title = "x"
        };

        string OuterKey(long i, int salt = 0) => "contexts/" + (i < keys && salt == 0 ? i : Hash(31 + salt, i) % keys);

        var load = Stopwatch.StartNew();
        await using (var bulk = h.Store.BulkInsert())
        {
            for (int i = 0; i < keys; i++)
                await bulk.StoreAsync(Inner(i, 0), $"contexts/{i}");

            for (int i = 0; i < outer; i++)
                await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextId = OuterKey(i), Title = "t" }, $"conversations/{i}");
        }

        await WaitAsync(h);
        Report($"[{shape}] loaded and indexed in {load.Elapsed.TotalSeconds:F1} s");

        IndexDefinition Definition(string name, JoinStorageMode mode)
        {
            var definition = new IndexDefinition
            {
                Name = name,
                Join = new JoinDefinition { OuterIndex = JoinIndexHarness.OuterName, OuterField = "ContextId", InnerIndex = JoinIndexHarness.InnerName, StorageMode = mode }
                    .AddField("Country").AddField("Name").AddGrouping("IsActive", "true")
            };
            return definition;
        }

        foreach ((string name, JoinStorageMode mode) in new[] { (ExpandedName, JoinStorageMode.Expanded), (CacheName, JoinStorageMode.Indirect), (OffName, JoinStorageMode.Indirect) })
        {
            var sw = Stopwatch.StartNew();
            long alloc = GC.GetTotalAllocatedBytes(true);
            await h.PutJoinAsync(Definition(name, mode));
            await WaitAsync(h, name == CacheName ? [name] : []);
            JoinIndex joinIndex = h.Join(name);
            (double used, double allocated) = FileSize(h, name);
            Report($"[{shape}] build {name}: {sw.Elapsed.TotalSeconds:F2} s wall, build itself {joinIndex.Counters.TotalBuildTicks / (double)Stopwatch.Frequency:F2} s, build thread allocated {joinIndex.Counters.LastBuildAllocatedBytes / 1048576.0:F1} MB, file used {used:F1} MB allocated {allocated:F1} MB, journal+disk see file");
        }

        JoinIndex expanded = h.Join(ExpandedName);
        JoinIndex cached = h.Join(CacheName);
        JoinIndex off = h.Join(OffName);
        JoinIndexHarness.UseResident(off, ResidentMode.Off);
        JoinIndexHarness.UseResident(cached, ResidentMode.Default, ResidentCacheRegistry.Instance);
        for (int i = 0; i < 6000 && (cached.Resident?.Cache.GetStats().WarmUpKeys ?? 0) == 0; i++)
            await Task.Delay(20);

        ResidentCacheStats warm = cached.Resident.Cache.GetStats();
        Report($"[{shape}] cache warm-up: {warm.WarmUpKeys} keys, resident {warm.ResidentBytes / 1048576.0:F2} MB = {warm.ResidentBytes / (double)outer:F2} bytes per outer entry");

        // ---- reads: the warm read of the outer entries of the keys of an atom
        Raven.Server.Documents.Indexes.Index outerIndex = h.Database.IndexStore.GetIndex(JoinIndexHarness.OuterName);
        Assert.True(outerIndex.TryOpenSnapshot(out IndexSnapshot outerSnapshot));
        using (outerSnapshot)
        {
            Assert.True(cached.TryOpenJoinSnapshot(out JoinIndexSnapshot indirectFile));
            Assert.True(expanded.TryOpenJoinSnapshot(out JoinIndexSnapshot expandedFile));
            using (indirectFile)
            using (expandedFile)
            {
                long transaction = outerSnapshot.Transaction.LowLevelTransaction.Id;
                JoinResidentRead resident = cached.Resident.ReadOf(outerSnapshot.EnvironmentId, transaction, indirectFile.Seed, 0, outerSnapshot.Consumed);

                foreach ((string label, string term, bool any) in new[] { ("Any (every key)", null, true), ("Tag (Country = t5, 1 % of the keys)", "t5", false), ("Rare (Name = r5, 0.05 % of the keys)", "r5", false) })
                {
                    byte[] bytes = term == null ? null : Encoding.UTF8.GetBytes(term);
                    int field = term == null ? 0 : term[0] == 't' ? 0 : 1;
                    long ExpandedRead()
                    {
                        long handle = any ? expandedFile.SlotHandle(JoinReader.AnySlot) : expandedFile.TryGetTermList(field, bytes, out long h2) ? h2 : ListStore.EmptyHandle;
                        return Drain(JoinMatchFactory.OfList(outerSnapshot.Searcher, expandedFile, handle));
                    }

                    var keyList = new ContextBoundNativeList<long>(outerSnapshot.Searcher.Allocator);
                    if (any)
                        indirectFile.ReadAtomKeys(JoinReader.AtomOfAny(), ref keyList);
                    else if (indirectFile.TryGetTermList(field, bytes, out long listHandle))
                        indirectFile.ReadList(listHandle, ref keyList);

                    long[] keyArray = keyList.ToSpan().ToArray();
                    keyList.Dispose();

                    long IndirectRead(JoinResidentRead read) => Drain(JoinKeyUnion.Create(outerSnapshot.Searcher, indirectFile, keyArray, "ContextId", read));

                    long expectedRows = ExpandedRead();
                    long rows = IndirectRead(resident);
                    Assert.True(expectedRows == rows, $"{label}: Expanded {expectedRows} rows, Indirect with the cache {rows}");
                    Assert.Equal(expectedRows, IndirectRead(default));

                    int rounds = any ? Math.Max(20, 6_000_000 / Math.Max(1, outer) * 10) : 300;
                    for (int rep = 0; rep < reps; rep++)
                    {
                        var e = Time(ExpandedRead, rounds);
                        var c = Time(() => IndirectRead(resident), rounds);
                        var s = Time(() => IndirectRead(default), rounds);
                        Report($"[{shape}] read {label}: {keyArray.Length} keys, {rows} rows (rep {rep + 1}): stored Expanded list {e.Median:F3} ms (p95 {e.P95:F3}), Indirect through the cache {c.Median:F3} ms (p95 {c.P95:F3}) = {c.Median / e.Median:F2}x, Indirect cache off (store) {s.Median:F3} ms (p95 {s.P95:F3}) = {s.Median / e.Median:F2}x");
                    }
                }
            }
        }

        ResidentCacheStats afterReads = cached.Resident.Cache.GetStats();
        Report($"[{shape}] cache after the reads: hits {afterReads.Hits}, misses {afterReads.Misses}, epoch misses {afterReads.EpochMisses}");

        // ---- outer writes: insert, re-key and delete of n documents in one transaction
        int next = outer + 10_000_000;
        foreach (int n in new[] { 1000, 10_000 })
        {
            for (int rep = 0; rep < reps + 1; rep++)
            {
                int first = next;
                next += n;
                foreach (string op in new[] { "insert", "update (re-key)", "delete" })
                {
                    Marks e = Mark(expanded), c = Mark(cached), o = Mark(off);
                    long applied = cached.Resident.PatchesApplied;
                    using (var session = h.Store.OpenSession())
                    {
                        for (int i = 0; i < n; i++)
                        {
                            string id = $"conversations/{first + i}";
                            if (op == "delete")
                                session.Delete(id);
                            else
                                session.Store(new JoinIndexHarness.Conversation { ContextId = OuterKey(first + i, op == "insert" ? 1 : 2), Title = "t" }, id);
                        }

                        session.SaveChanges();
                    }

                    await WaitAsync(h);
                    await Task.Delay(200);
                    Report($"[{shape}] outer {op} of {n} ({(rep == 0 ? "warm-up" : "rep " + rep)}): join index busy, Expanded {Busy(expanded, e, n)}; Indirect+cache {Busy(cached, c, n)} ({cached.Resident.PatchesApplied - applied} patches); Indirect cache off {Busy(off, o, n)}");
                }
            }
        }

        // ---- one inner document changes (one key): its country changes
        {
            var perJoin = new Dictionary<string, List<double>> { [ExpandedName] = [], [CacheName] = [], [OffName] = [] };
            int changes = 30;
            for (int i = 0; i < changes; i++)
            {
                var before = new Dictionary<string, long>();
                foreach (string name in perJoin.Keys)
                    before[name] = h.Join(name).Counters.TotalRefreshTicks;

                using (var session = h.Store.OpenSession())
                {
                    session.Store(Inner(7, i + 1), "contexts/7");
                    session.SaveChanges();
                }

                await WaitAsync(h);
                await Task.Delay(30);
                foreach (string name in perJoin.Keys)
                    perJoin[name].Add(Ms(h.Join(name).Counters.TotalRefreshTicks - before[name]));
            }

            string Summary(string name)
            {
                List<double> times = perJoin[name].Skip(5).OrderBy(x => x).ToList();
                return $"{times[times.Count / 2]:F2} ms median [{times[0]:F2}-{times[^1]:F2}], p95 {times[(int)(times.Count * 0.95)]:F2}";
            }

            Report($"[{shape}] one inner document changes (one key, {outer / (double)keys:F1} outer rows; {changes} changes, first 5 dropped): join index busy, Expanded {Summary(ExpandedName)}; Indirect+cache {Summary(CacheName)}; Indirect cache off {Summary(OffName)}");
        }

        // ---- a bulk change of the inner documents (the country of every one of them)
        {
            int bulkCount = Math.Min(keys, 100_000);
            int rounds = reps + 1;
            for (int round = 0; round < rounds; round++)
            {
                Marks e = Mark(expanded), c = Mark(cached), o = Mark(off);
                var bulkWatch = Stopwatch.StartNew();
                await using (var b = h.Store.BulkInsert())
                {
                    for (int i = 0; i < bulkCount; i++)
                        await b.StoreAsync(Inner(i, 100 + round), $"contexts/{i}");
                }

                await WaitAsync(h);
                await Task.Delay(300);
                Report($"[{shape}] inner bulk change of {bulkCount} documents ({(round == 0 ? "warm-up" : "round " + round)}): join index busy, Expanded {Busy(expanded, e, bulkCount)}; Indirect+cache {Busy(cached, c, bulkCount)}; Indirect cache off {Busy(off, o, bulkCount)}; wall {bulkWatch.Elapsed.TotalSeconds:F1} s");
            }
        }

        ResidentCacheStats end = cached.Resident.Cache.GetStats();
        Report($"[{shape}] resident at the end: {end.ResidentBytes / 1048576.0:F2} MB, {end.ResidentKeys} keys, patches {end.PatchesApplied}, invalidations {end.Invalidations}, epoch misses {end.EpochMisses}");
        foreach (string name in new[] { ExpandedName, CacheName, OffName })
        {
            (double used, double allocated) = FileSize(h, name);
            JoinIndex j = h.Join(name);
            Report($"[{shape}] end {name}: file used {used:F1} MB allocated {allocated:F1} MB, builds {j.Counters.Builds}, refreshes {j.Counters.Refreshes}, rebuilds {j.Counters.RebuildsByReason.Values.Sum()}, join index reset: {h.Join(name) != j}");
        }

        foreach (string name in new[] { ExpandedName, CacheName, OffName })
        {
            h.TrackJoin(name);
            JoinIndexInvariantChecker.Result result = await h.CheckAsync(TimeSpan.FromMinutes(10), name);
            Report($"[{shape}] invariant check {name}: ok {result.Ok}, compared {result.Compared}");
            Assert.True(result.Ok, result.ToString());
        }
    }
}
