using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Sparrow.Utils;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

// Opt-in: RAVEN_JOIN_RESIDENT_BENCH=1; the results are appended to RAVEN_JOIN_RESIDENT_BENCH_FILE when it is set.
// The warm read of the atoms of a field join and of an id join at one million outer entries, the patch of one row, and the warm-up of all lists. The store is synthetic (the lists are computed into a buffer),
// so the warm-up numbers are the cost of the cache without the read of the posting lists of the outer index.
public unsafe class ResidentCacheBenchmark(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    private const int Outer = 1_000_000;
    private static readonly Guid Env = Guid.NewGuid();
    private static readonly ResidentEpoch Epoch = new(Env, 100, 1);

    // the entry of outer row i (1 based) has the key i % keys
    private sealed class ModuloSource(int keys) : IResidentListSource
    {
        private long[] _buffer = new long[Outer / keys + 2];

        public long CountOf(long keyRef) => Count((int)keyRef - 1);

        private long Count(int key) => key < keys ? (Outer - key + keys - 1) / keys : 0;

        public ReadOnlySpan<long> Read(long keyRef)
        {
            int key = (int)keyRef - 1;
            int n = 0;
            for (long id = key + 1; id <= Outer; id += keys)
                _buffer[n++] = id;

            return new ReadOnlySpan<long>(_buffer, 0, n);
        }
    }

    private readonly struct Box(IResidentListSource source) : IResidentListSource
    {
        public long CountOf(long keyRef) => source.CountOf(keyRef);

        public ReadOnlySpan<long> Read(long keyRef) => source.Read(keyRef);
    }

    private void Report(string line)
    {
        Output.WriteLine(line);
        string file = Environment.GetEnvironmentVariable("RAVEN_JOIN_RESIDENT_BENCH_FILE");
        if (string.IsNullOrEmpty(file) == false)
            File.AppendAllText(file, line + Environment.NewLine);
    }

    private static double Median(Func<double> run, int rounds, int warmUp)
    {
        for (int i = 0; i < warmUp; i++)
            run();

        double[] times = new double[rounds];
        for (int i = 0; i < rounds; i++)
            times[i] = run();

        Array.Sort(times);
        return times[rounds / 2];
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void WarmReadPatchAndWarmUpAtOneMillionOuterIds()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RAVEN_JOIN_RESIDENT_BENCH")))
            return;

        Report($"--- {DateTime.Now:s} resident cache, {Outer} outer ids, cores {Environment.ProcessorCount}");
        Shape("field join (5005 keys, 200 rows per key)", 5_005, [("Tag", 896), ("Any", 5_005), ("5 hub keys x 20000", 5)]);
        Shape("id join (100000 keys, 10 rows per key)", 100_000, [("Tag", 1_959), ("Any", 100_000)]);
    }

    private void Shape(string name, int keys, (string Atom, int Keys)[] atoms)
    {
        var source = new ModuloSource(keys);
        var box = new Box(source);
        long[] allKeys = Enumerable.Range(1, keys).Select(k => (long)k).ToArray();
        int wordCount = (Outer >> 6) + 2;
        var words = (ulong*)NativeMemory.AllocateMemory((long)wordCount * sizeof(ulong));
        long[] drain = new long[Outer + 1];
        try
        {
            long before = NativeMemory.CurrentThreadStats.TotalAllocated;
            using var cache = new ResidentKeyListCache(new ResidentCacheOptions { BudgetBytes = 1L << 30 });
            Assert.True(cache.TryEstablish(Epoch, Outer));

            double warmUp = Median(() =>
            {
                cache.Invalidate();
                Assert.True(cache.TryEstablish(Epoch, Outer));
                var sw = Stopwatch.StartNew();
                ResidentWarmUpResult result = cache.WarmUp(Epoch, allKeys, ref box, CancellationToken.None);
                Assert.Equal(keys, result.Loaded);
                return sw.Elapsed.TotalMilliseconds;
            }, 7, 2);
            long ids = Outer;
            Report($"{name}: warm-up of all {keys} lists {warmUp:F2} ms ({warmUp * 1000 / keys:F2} us per key), resident {cache.ResidentBytes} bytes = {(double)cache.ResidentBytes / ids:F2} B per outer id, native {cache.NativeBytes} (allocated by the thread {NativeMemory.CurrentThreadStats.TotalAllocated - before})");

            foreach ((string atom, int atomKeys) in atoms)
            {
                // the keys of the atom: spread over the key space
                long[] keyRefs = Enumerable.Range(0, atomKeys).Select(i => 1L + (long)i * keys / atomKeys).ToArray();
                long atomIds = keyRefs.Sum(k => source.CountOf(k));
                if (atom.StartsWith("5 hub"))
                {
                    // five keys with 20,000 rows each: a field join with hubs
                    using var hubCache = new ResidentKeyListCache(new ResidentCacheOptions { BudgetBytes = 1L << 30 });
                    var hubSource = new ModuloSource(50);
                    var hubBox = new Box(hubSource);
                    Assert.True(hubCache.TryEstablish(Epoch, Outer));
                    keyRefs = [1, 2, 3, 4, 5];
                    atomIds = keyRefs.Sum(k => hubSource.CountOf(k));
                    hubCache.WarmUp(Epoch, keyRefs, ref hubBox, CancellationToken.None);
                    double hubRead = Median(() => Read(hubCache, keyRefs, ref hubBox, words, wordCount, drain, out _), 15, 3);
                    Report($"{name} atom {atom} ({atomIds} ids): warm read {hubRead:F3} ms");
                    continue;
                }

                double union = Median(() => Read(cache, keyRefs, ref box, words, wordCount, null, out _), 15, 3);
                double withDrain = Median(() => Read(cache, keyRefs, ref box, words, wordCount, drain, out _), 15, 3);
                cache.Invalidate();
                Assert.True(cache.TryEstablish(Epoch, Outer));
                double cold = Median(() =>
                {
                    cache.Invalidate();
                    Assert.True(cache.TryEstablish(Epoch, Outer));
                    return Read(cache, keyRefs, ref box, words, wordCount, drain, out _);
                }, 7, 1);
                cache.WarmUp(Epoch, allKeys, ref box, CancellationToken.None);
                Report($"{name} atom {atom} ({atomKeys} keys, {atomIds} ids): warm union {union:F3} ms, warm union and extract {withDrain:F3} ms, cold read-through {cold:F3} ms");
            }

            // the patch of one outer row that moves from a key to another with a new entry id, then the same with the id kept
            ResidentEpoch epoch = Epoch;
            int rounds = 2_000;
            var sw2 = Stopwatch.StartNew();
            for (int i = -2_000; i < rounds; i++)
            {
                if (i == 0)
                    sw2.Restart();

                long entry = 1 + (Math.Abs(i) * 7919L) % Outer;
                using var patch = new ResidentPatch();
                patch.ChangeRow(entry, [1 + entry % keys], Outer + 1 + i + 2_000, [1 + (entry + 1) % keys]);
                ResidentEpoch next = epoch with { TransactionId = epoch.TransactionId + 1 };
                Assert.Equal(ResidentPatchOutcome.Applied, cache.ApplyPatch(epoch, next, Outer + rounds + 1, patch));
                epoch = next;
            }

            double newId = sw2.Elapsed.TotalMilliseconds / rounds;
            sw2.Restart();
            for (int i = 0; i < rounds; i++)
            {
                long entry = Outer + 1 + i + 2_000;
                using var patch = new ResidentPatch();
                patch.ChangeRow(entry, [1 + (entry - 1) % keys], entry, [1 + (entry + 3) % keys]);
                ResidentEpoch next = epoch with { TransactionId = epoch.TransactionId + 1 };
                Assert.Equal(ResidentPatchOutcome.Applied, cache.ApplyPatch(epoch, next, Outer + rounds + 1, patch));
                epoch = next;
            }

            double kept = sw2.Elapsed.TotalMilliseconds / rounds;
            Report($"{name}: patch of one row (collect and apply, per patch) new entry id {newId:F4} ms, kept entry id {kept:F4} ms");
        }
        finally
        {
            NativeMemory.Free((byte*)words, (long)wordCount * sizeof(ulong));
        }
    }

    private static double Read(ResidentKeyListCache cache, long[] keyRefs, ref Box source, ulong* words, int wordCount, long[] drain, out ResidentUnion union)
    {
        var sw = Stopwatch.StartNew();
        new Span<ulong>(words, wordCount).Clear();
        union = cache.UnionKeys(Env, Epoch.TransactionId, 1, keyRefs, ref source, words, wordCount);
        if (drain != null)
        {
            int n = 0;
            for (int w = 0; w < wordCount; w++)
            {
                ulong word = words[w];
                while (word != 0)
                {
                    drain[n++] = ((long)w << 6) + BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                }
            }
        }

        return sw.Elapsed.TotalMilliseconds;
    }
}
