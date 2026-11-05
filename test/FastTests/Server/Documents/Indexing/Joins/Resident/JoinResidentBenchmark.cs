using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using Voron.Util;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

// Opt-in: RAVEN_JOIN_RESIDENT_PROD_BENCH=<outer documents, 1000000 for the FF1 shape>[,<inner documents, default 100000>]; the results are appended to RAVEN_JOIN_RESIDENT_BENCH_FILE when it is set.
// The Indirect join index with its cache and the Expanded join index follow the same two indexes (5,000 keys of 20 inner documents each, a tag of 100 values, 180 outer rows per key, 10 percent of the outer rows on 5 hub keys).
// In process: the warm read of the entries of the keys of a tag, of a rare value and of every key (the stored Expanded list against the key lists through the cache and through the store), then the cost of
// outer writes (1,000 re-keys in one transaction) and of a bulk change of every inner document, as the busy time of each join index, and the resident bytes.
public class JoinResidentBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string ExpandedName = "PairsExpanded";
    private const string IndirectName = "PairsIndirect";

    private void Report(string line)
    {
        Output.WriteLine(line);
        string file = Environment.GetEnvironmentVariable("RAVEN_JOIN_RESIDENT_BENCH_FILE");
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

    private static async Task WaitAsync(JoinIndexHarness h, params string[] joinIndexes)
    {
        DateTime until = DateTime.UtcNow.AddMinutes(20);
        while (DateTime.UtcNow < until)
        {
            var stats = h.Store.Maintenance.Send(new GetIndexesStatisticsOperation());
            if (stats.All(x => x.IsStale == false) && joinIndexes.All(j => h.Join(j).Resident != null || j == ExpandedName))
                return;

            await Task.Delay(50);
        }

        throw new TimeoutException("the indexes stayed stale");
    }

    private static (double Median, double P95) Time(Func<long> run, int rounds)
    {
        for (int i = 0; i < 20; i++)
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

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheIndirectJoinWithItsCacheAgainstTheStoredExpandedLists()
    {
        string[] parts = (Environment.GetEnvironmentVariable("RAVEN_JOIN_RESIDENT_PROD_BENCH") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1)
            return;

        int outer = int.Parse(parts[0]);
        int inner = parts.Length > 1 ? int.Parse(parts[1]) : 100_000;
        const int hubs = 5;
        int innerKeys = Math.Max(1, inner / 20);

        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.IndirectEnabled)] = "true",
            [RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = "5000000",
            ["Indexing.MapBatchSize"] = "5000000"
        }, putJoin: false, runInMemory: false);

        JoinIndexHarness.Context InnerDocument(int i, int version) => new()
        {
            Code = i < inner ? "k/" + i / 20 : "k/hub" + (i - inner) / 20 % hubs,
            Country = "t" + (Hash(32, i) + version) % 100, Name = "r" + Hash(33, i) % 2000, Rank = 0, IsActive = true, Title = "x"
        };

        string OuterCode(int i) => i % 10 == 0 ? "k/hub" + Hash(30, i) % hubs : "k/" + Hash(31, i) % innerKeys;

        var load = Stopwatch.StartNew();
        await using (var bulk = h.Store.BulkInsert())
        {
            for (int i = 0; i < inner + 100; i++)
                await bulk.StoreAsync(InnerDocument(i, 0), $"contexts/{i}");

            for (int i = 0; i < outer; i++)
                await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextId = OuterCode(i), Title = "t" }, $"conversations/{i}");
        }

        await WaitAsync(h);
        Report($"FF1 shape, outer {outer}, inner {inner + 100}: loaded and indexed in {load.Elapsed.TotalSeconds:F1} s");

        IndexDefinition expanded = JoinIndexHarness.FieldJoinIndexDefinition(ExpandedName);
        IndexDefinition indirect = JoinIndexHarness.FieldJoinIndexDefinition(IndirectName);
        indirect.Join.StorageMode = JoinStorageMode.Indirect;
        var sw = Stopwatch.StartNew();
        await h.PutJoinAsync(expanded);
        await WaitAsync(h);
        Report($"build of the Expanded join index: {sw.Elapsed.TotalSeconds:F1} s wall, build itself {h.Join(ExpandedName).Counters.TotalBuildTicks / (double)Stopwatch.Frequency:F2} s");
        sw.Restart();
        await h.PutJoinAsync(indirect);
        await WaitAsync(h, IndirectName);
        JoinIndex indirectJoin = h.Join(IndirectName);
        JoinIndex expandedJoin = h.Join(ExpandedName);
        Report($"build of the Indirect join index: {sw.Elapsed.TotalSeconds:F1} s wall, build itself {indirectJoin.Counters.TotalBuildTicks / (double)Stopwatch.Frequency:F2} s");

        // the first run after the open warms the cache from the any list
        JoinIndexHarness.UseResident(indirectJoin, ResidentMode.Default, ResidentCacheRegistry.Instance);
        for (int i = 0; i < 6000 && (indirectJoin.Resident?.Cache.GetStats().WarmUpKeys ?? 0) == 0; i++)
            await Task.Delay(20);

        ResidentKeyListCache cache = indirectJoin.Resident.Cache;
        ResidentCacheStats warm = cache.GetStats();
        Report($"warm-up: {warm.WarmUpKeys} keys, resident {warm.ResidentBytes / 1048576.0:F2} MB = {warm.ResidentBytes / (double)outer:F2} bytes per outer entry, native {warm.NativeBytes / 1048576.0:F2} MB");

        Raven.Server.Documents.Indexes.Index outerIndex = h.Database.IndexStore.GetIndex(JoinIndexHarness.OuterName);
        Assert.True(outerIndex.TryOpenSnapshot(out IndexSnapshot outerSnapshot));
        using (outerSnapshot)
        {
            Assert.True(indirectJoin.TryOpenJoinSnapshot(out JoinIndexSnapshot indirectFile));
            Assert.True(expandedJoin.TryOpenJoinSnapshot(out JoinIndexSnapshot expandedFile));
            using (indirectFile)
            using (expandedFile)
            {
                long transaction = outerSnapshot.Transaction.LowLevelTransaction.Id;
                JoinResident resident = indirectJoin.Resident;
                JoinResidentRead cached = resident.ReadOf(outerSnapshot.EnvironmentId, transaction, indirectFile.Seed, 0, outerSnapshot.Consumed);
                JoinResidentRead store = default;

                foreach ((string label, string term, bool any) in new[] { ("Tag (Country = t5)", "t5", false), ("Rare (Name = r5)", "r5", false), ("Any (every key)", null, true) })
                {
                    byte[] bytes = term == null ? null : Encoding.UTF8.GetBytes(term);
                    int field = term == null ? 0 : term[0] == 't' ? 0 : 1;
                    long ExpandedRead()
                    {
                        long handle = any ? expandedFile.SlotHandle(JoinReader.AnySlot) : expandedFile.TryGetTermList(field, bytes, out long h2) ? h2 : ListStore.EmptyHandle;
                        return Drain(JoinMatchFactory.OfList(outerSnapshot.Searcher, expandedFile, handle));
                    }

                    var keys = new ContextBoundNativeList<long>(outerSnapshot.Searcher.Allocator);
                    if (any)
                        indirectFile.ReadAtomKeys(JoinReader.AtomOfAny(), ref keys);
                    else if (indirectFile.TryGetTermList(field, bytes, out long listHandle))
                        indirectFile.ReadList(listHandle, ref keys);

                    long[] keyArray = keys.ToSpan().ToArray();
                    keys.Dispose();

                    long IndirectRead(JoinResidentRead read) => Drain(JoinKeyUnion.Create(outerSnapshot.Searcher, indirectFile, keyArray, "ContextId", read));

                    long expectedRows = ExpandedRead();
                    long rows = IndirectRead(cached);
                    Assert.True(expectedRows == rows, $"{label}: Expanded {expectedRows} rows, Indirect with the cache {rows}");
                    Assert.Equal(expectedRows, IndirectRead(store));

                    int rounds = any ? 60 : 300;
                    var e = Time(ExpandedRead, rounds);
                    var c = Time(() => IndirectRead(cached), rounds);
                    var s = Time(() => IndirectRead(store), rounds);
                    Report($"{label}: {keyArray.Length} keys, {rows} rows: stored Expanded list {e.Median:F3} ms (p95 {e.P95:F3}), Indirect through the cache {c.Median:F3} ms (p95 {c.P95:F3}) = {c.Median / e.Median:F2}x, Indirect through the store {s.Median:F3} ms (p95 {s.P95:F3}) = {s.Median / e.Median:F2}x");
                }
            }
        }

        ResidentCacheStats afterReads = cache.GetStats();
        Report($"cache after the reads: hits {afterReads.Hits}, misses {afterReads.Misses}, epoch misses {afterReads.EpochMisses}");

        // outer writes: 1,000 rows change their key in one transaction
        for (int round = 1; round <= 5; round++)
        {
            long expandedTicks = expandedJoin.Counters.TotalRefreshTicks, indirectTicks = indirectJoin.Counters.TotalRefreshTicks;
            long expandedRefreshes = expandedJoin.Counters.Refreshes, indirectRefreshes = indirectJoin.Counters.Refreshes;
            long applied = indirectJoin.Resident.PatchesApplied;
            using (var session = h.Store.OpenSession())
            {
                for (int i = 0; i < 1000; i++)
                    session.Store(new JoinIndexHarness.Conversation { ContextId = "k/" + Hash(40 + round, i) % innerKeys, Title = "t" }, $"conversations/{i + round * 1000}");

                session.SaveChanges();
            }

            await WaitAsync(h);
            await Task.Delay(300);
            double e = (expandedJoin.Counters.TotalRefreshTicks - expandedTicks) * 1000.0 / Stopwatch.Frequency / 1000;
            double i2 = (indirectJoin.Counters.TotalRefreshTicks - indirectTicks) * 1000.0 / Stopwatch.Frequency / 1000;
            Report($"outer write round {round} (1,000 re-keys): join index busy per write, Expanded {e:F3} ms ({expandedJoin.Counters.Refreshes - expandedRefreshes} refreshes), Indirect with the cache {i2:F3} ms ({indirectJoin.Counters.Refreshes - indirectRefreshes} refreshes, {indirectJoin.Resident.PatchesApplied - applied} patches applied)");
        }

        // a bulk change of every inner document in one transaction
        {
            long expandedTicks = expandedJoin.Counters.TotalRefreshTicks, indirectTicks = indirectJoin.Counters.TotalRefreshTicks;
            long rebuildsE = expandedJoin.Counters.RebuildsByReason.Values.Sum(), rebuildsI = indirectJoin.Counters.RebuildsByReason.Values.Sum();
            var bulk = Stopwatch.StartNew();
            await using (var b = h.Store.BulkInsert())
            {
                for (int i = 0; i < inner; i++)
                    await b.StoreAsync(InnerDocument(i, 7), $"contexts/{i}");
            }

            await WaitAsync(h);
            await Task.Delay(500);
            Report($"inner bulk change of {inner} documents: join index busy, Expanded {(expandedJoin.Counters.TotalRefreshTicks - expandedTicks) / (double)Stopwatch.Frequency:F2} s, Indirect {(indirectJoin.Counters.TotalRefreshTicks - indirectTicks) / (double)Stopwatch.Frequency:F2} s (join indexes reset: {h.Join(ExpandedName) != expandedJoin}, {h.Join(IndirectName) != indirectJoin}); wall {bulk.Elapsed.TotalSeconds:F1} s");
            ResidentCacheStats end = indirectJoin.Resident.Cache.GetStats();
            Report($"resident after the changes: {end.ResidentBytes / 1048576.0:F2} MB, {end.ResidentKeys} keys, patches {end.PatchesApplied}, invalidations {end.Invalidations}");
        }
    }
}
