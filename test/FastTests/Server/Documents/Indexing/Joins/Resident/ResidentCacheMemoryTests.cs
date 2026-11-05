using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Sparrow.LowMemory;
using Sparrow.Utils;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

// The process-wide counter of the native allocations is the ground truth of the accounting tests: no other test may allocate native memory next to them
[CollectionDefinition(Name, DisableParallelization = true)]
public class ResidentNativeMemoryCollection
{
    public const string Name = nameof(ResidentNativeMemoryCollection);
}

/// <summary>The bytes of the cache: the budget, the accounting against the native allocations, the form of a list, eviction, admission, the registry of the process and the low memory notification.</summary>
[Collection(ResidentNativeMemoryCollection.Name)]
public unsafe class ResidentCacheMemoryTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    private static readonly Guid Env = Guid.NewGuid();
    private static readonly ResidentEpoch Epoch = new(Env, 100, 1);

    private static ResidentKeyListCache Established(ResidentCacheOptions options, ResidentCacheRegistry registry = null)
    {
        var cache = new ResidentKeyListCache(options, registry);
        Assert.True(cache.TryEstablish(Epoch, 1_000_000));
        return cache;
    }

    private static long[] Range(int from, int count, int step = 1) => Enumerable.Range(0, count).Select(i => from + (long)i * step).ToArray();

    private static long Allocated => NativeMemory.TotalAllocatedMemory;

    /// <summary>
    /// The counter of the process also sees what the servers of the tests that ran before free in the background (a database that is being disposed, a pool that is cleaned): the test starts when it has not moved for a while.
    /// </summary>
    private static long QuietAllocated()
    {
        long last = Allocated;
        for (int stable = 0, i = 0; stable < 5 && i < 300; i++)
        {
            Thread.Sleep(100);
            long now = Allocated;
            stable = now == last ? stable + 1 : 0;
            last = now;
        }

        return last;
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheNativeAllocationsMatchTheAccountingAndAreFreedOnEvictionClearAndDispose()
    {
        long before = QuietAllocated();
        var cache = Established(new ResidentCacheOptions { BudgetBytes = 200_000 });
        var random = new Random(7);
        for (int key = 0; key < 3_000; key++)
        {
            int n = 1 + random.Next(300);
            long[] ids = Range(random.Next(1000), n, 1 + random.Next(3));
            ResidentInsertResult result = cache.TryInsert(Env, Epoch.TransactionId, 1_000 + key * 13L, ids);
            Assert.True(result is ResidentInsertResult.Inserted or ResidentInsertResult.NoRoom, result.ToString());
            Assert.True(cache.ResidentBytes <= cache.BudgetBytes, $"{cache.ResidentBytes} bytes above the budget {cache.BudgetBytes}");
            Assert.Equal(Allocated - before, cache.NativeBytes);
        }

        ResidentCacheStats stats = cache.GetStats();
        Output.WriteLine($"resident {stats.ResidentBytes} bytes in {stats.ResidentKeys} lists, native {stats.NativeBytes}, evictions {stats.Evictions}, peak {stats.PeakResidentBytes}");
        Assert.True(stats.Evictions > 0 && stats.ResidentKeys > 10);
        Assert.True(stats.PeakResidentBytes <= cache.BudgetBytes);

        cache.Invalidate();
        Assert.Equal(0, cache.ResidentBytes);
        Assert.Equal(Allocated - before, cache.NativeBytes);
        Assert.True(cache.NativeBytes < 1_000, "a cleared cache holds only its first tables");

        Assert.True(cache.TryEstablish(Epoch, 10));
        cache.TryInsert(Env, Epoch.TransactionId, 5, Range(1, 100));
        cache.Dispose();
        Assert.Equal(before, Allocated);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheModelledOverheadOfAListCoversTheTablesWhenTheCacheOnlyGrows()
    {
        long before = QuietAllocated();
        using (var cache = Established(new ResidentCacheOptions()))
        {
            int lists = 0;
            foreach (int count in new[] { 100, 1_000, 10_000, 50_000 })
            {
                for (; lists < count; lists++)
                    Assert.Equal(ResidentInsertResult.Inserted, cache.TryInsert(Env, Epoch.TransactionId, 1_000 + lists * 31L, Range(lists, 60, 7)));

                Output.WriteLine($"{lists} lists: resident {cache.ResidentBytes}, native {cache.NativeBytes}, native / resident {(double)cache.NativeBytes / cache.ResidentBytes:F3}");
                Assert.True(cache.NativeBytes <= cache.ResidentBytes + 24L * lists + 1024, $"native {cache.NativeBytes} resident {cache.ResidentBytes} lists {lists}");
                Assert.Equal(Allocated - before, cache.NativeBytes);
            }
        }

        Assert.Equal(before, Allocated);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheFormOfAListIsTheBitmapOnlyWhenItIsSmallerThanTheArray()
    {
        using ResidentKeyListCache cache = Established(new ResidentCacheOptions());
        void Insert(long key, long[] ids) => Assert.Equal(ResidentInsertResult.Inserted, cache.TryInsert(Env, Epoch.TransactionId, key, ids));

        // dense: 2000 ids in 32 words (256 bytes) against 2000 uints (8000 bytes)
        long before = cache.ResidentBytes;
        Insert(1, Range(1, 2_000));
        Assert.Equal(1, cache.FormOf(1));
        Assert.Equal(32 * 8 + ResidentKeyListCache.ListOverheadBytes, cache.ResidentBytes - before);

        // sparse: the words of the range are larger than the ids
        before = cache.ResidentBytes;
        Insert(2, Range(0, 1_000, 100));
        Assert.Equal(0, cache.FormOf(2));
        Assert.Equal((1_000 + 1_000 / 8 + 2) * 4 + ResidentKeyListCache.ListOverheadBytes, cache.ResidentBytes - before);

        // below 32 ids the array is used even where the bitmap is smaller
        Insert(3, Range(0, 31));
        Assert.Equal(0, cache.FormOf(3));

        // 32 ids: 15 words (120 bytes) are smaller than 128 bytes, 16 words are not
        Insert(4, Range(0, 32, 30)); // the last id is 930
        Assert.Equal(1, cache.FormOf(4));
        Insert(5, Range(0, 31, 30).Append(1_023).ToArray());
        Assert.Equal(0, cache.FormOf(5));

        // an empty list is a list
        Insert(6, []);
        Assert.True(cache.TryGetList(6, out long[] empty));
        Assert.Empty(empty);

        foreach ((long key, long[] ids) in new[] { (1L, Range(1, 2_000)), (2L, Range(0, 1_000, 100)), (4L, Range(0, 32, 30)) })
        {
            Assert.True(cache.TryGetList(key, out long[] stored));
            Assert.Equal(ids, stored);
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheListsOfBothFormsGiveTheSameUnionAndFollowPatches()
    {
        using ResidentKeyListCache cache = Established(new ResidentCacheOptions());
        long[] dense = Range(1, 2_000), sparse = Range(0, 400, 50);
        Assert.Equal(ResidentInsertResult.Inserted, cache.TryInsert(Env, Epoch.TransactionId, 1, dense));
        Assert.Equal(ResidentInsertResult.Inserted, cache.TryInsert(Env, Epoch.TransactionId, 2, sparse));
        Assert.Equal(1, cache.FormOf(1));
        Assert.Equal(0, cache.FormOf(2));

        var words = new ulong[400];
        fixed (ulong* w = words)
        {
            using ResidentKeyListCache.ReadLease lease = cache.TryBeginRead(Env, Epoch.TransactionId);
            Assert.True(lease.TryOrInto(1, w, words.Length, out bool twice));
            Assert.False(twice);
            Assert.True(lease.TryOrInto(2, w, words.Length, out twice));
            Assert.True(twice); // 50, 100 ... are in both
            Assert.Equal(2_000, lease.CountOf(1));
            Assert.Equal(-1, lease.CountOf(99));
        }

        Assert.Equal(dense.Union(sparse).Count(), words.Sum(x => (long)System.Numerics.BitOperations.PopCount(x)));

        // a patch inside the range of the bitmap keeps it, an id beyond it that would make it larger than the array turns it into the array
        ResidentEpoch epoch = Epoch;
        ResidentEpoch next = epoch with { TransactionId = 101 };
        using (var patch = new ResidentPatch())
        {
            patch.AddRow(2_050, [1]);
            patch.RemoveRow(10, [1]);
            patch.AddRow(3_000_000_000, [2]);
            patch.AddRow(4_000_000_000, [1]);
            Assert.Equal(ResidentPatchOutcome.Applied, cache.ApplyPatch(epoch, next, 4_000_000_000, patch));
        }

        Assert.Equal(0, cache.FormOf(1)); // 4,000,000,000 would need 62 million words
        Assert.Equal(0, cache.FormOf(2));
        Assert.True(cache.TryGetList(1, out long[] one));
        Assert.Equal(dense.Where(x => x != 10).Append(2_050).Append(4_000_000_000).ToArray(), one);
        Assert.True(cache.TryGetList(2, out long[] two));
        Assert.Equal(sparse.Append(3_000_000_000).ToArray(), two);

        // a bitmap that grows within the range of the array stays a bitmap
        Assert.True(cache.TryEstablish(next with { TransactionId = 102 }, 0));
        Assert.Equal(ResidentInsertResult.Inserted, cache.TryInsert(Env, 102, 3, dense));
        using (var patch = new ResidentPatch())
        {
            patch.AddRow(2_100, [3]);
            Assert.Equal(ResidentPatchOutcome.Applied, cache.ApplyPatch(next with { TransactionId = 102 }, next with { TransactionId = 103 }, 2_100, patch));
        }

        Assert.Equal(1, cache.FormOf(3));
        Assert.True(cache.TryGetList(3, out long[] three));
        Assert.Equal(dense.Append(2_100).ToArray(), three);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AListThatIsLargerThanTheBudgetIsRefusedAndTheClockSparesTheListsThatWereUsed()
    {
        // eight ids: 11 uints (44 bytes) and the overhead; three of them fill the budget
        long one = (8 + 1 + 2) * 4 + ResidentKeyListCache.ListOverheadBytes;
        using ResidentKeyListCache cache = Established(new ResidentCacheOptions { BudgetBytes = 3 * one });
        long[] ids = Range(1, 8);
        Assert.Equal(ResidentInsertResult.TooLarge, cache.TryInsert(Env, Epoch.TransactionId, 99, Range(1, 5_000, 3)));
        foreach (long key in new long[] { 1, 2, 3 })
            Assert.Equal(ResidentInsertResult.Inserted, cache.TryInsert(Env, Epoch.TransactionId, key, ids));

        Assert.Equal(3 * one, cache.ResidentBytes);

        // the fourth list takes the place of the first one: all were just inserted, the hand clears their marks and evicts the first
        Assert.Equal(ResidentInsertResult.Inserted, cache.TryInsert(Env, Epoch.TransactionId, 4, ids));
        Assert.False(cache.TryGetList(1, out _));

        // list 2 is used since the hand passed: the next list takes list 3
        var words = new ulong[1];
        fixed (ulong* w = words)
        {
            using ResidentKeyListCache.ReadLease lease = cache.TryBeginRead(Env, Epoch.TransactionId);
            Assert.True(lease.TryOrInto(2, w, 1, out _));
        }

        Assert.Equal(ResidentInsertResult.Inserted, cache.TryInsert(Env, Epoch.TransactionId, 5, ids));
        Assert.True(cache.TryGetList(2, out _));
        Assert.False(cache.TryGetList(3, out _));
        Assert.Equal(3 * one, cache.ResidentBytes);
        Assert.Equal(2, cache.GetStats().Evictions);
    }

    private sealed class FixedSource(Dictionary<long, long[]> lists) : IResidentListSource
    {
        public int Reads;

        public long CountOf(long keyRef) => lists[keyRef].Length;

        public ReadOnlySpan<long> Read(long keyRef)
        {
            Reads++;
            return lists[keyRef];
        }
    }

    private static ResidentUnion Union(ResidentKeyListCache cache, long atom, long[] keys, FixedSource source)
    {
        var words = new ulong[64];
        fixed (ulong* w = words)
        {
            var hand = new SourceBox(source);
            return cache.UnionKeys(Env, Epoch.TransactionId, atom, keys, ref hand, w, words.Length);
        }
    }

    private readonly struct SourceBox(FixedSource source) : IResidentListSource
    {
        public long CountOf(long keyRef) => source.CountOf(keyRef);

        public ReadOnlySpan<long> Read(long keyRef) => source.Read(keyRef);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AtomRateAdmissionKeepsTheListsOfAnAtomOnlyWhileItIsReadOften()
    {
        var lists = Enumerable.Range(1, 12).ToDictionary(k => (long)k, k => Range(k * 10, 5));
        var source = new FixedSource(lists);
        using ResidentKeyListCache cache = Established(new ResidentCacheOptions { Admission = ResidentAdmission.AtomRate, AtomRateWindow = 4, AtomRateMinReads = 3 });

        // the first two reads of an atom are not enough, the third keeps the lists
        Assert.Equal(0, Union(cache, 1, [1, 2], source).KeysInserted);
        Assert.Equal(0, Union(cache, 1, [1, 2], source).KeysInserted);
        Assert.Equal(0, cache.ResidentKeys);
        Assert.Equal(2, Union(cache, 1, [1, 2], source).KeysInserted);
        Assert.Equal(2, cache.ResidentKeys);
        ResidentUnion hit = Union(cache, 1, [1, 2], source);
        Assert.Equal(2, hit.KeysFromCache);

        // another atom that is read once is not admitted
        Assert.Equal(0, Union(cache, 2, [5, 6], source).KeysInserted);

        // the counts of the atoms age: after two windows of reads of other atoms atom 1 is cold again, and a list it gets later is not kept
        for (int i = 0; i < 8; i++)
            Union(cache, 9, [7], source);

        Assert.Equal(0, Union(cache, 1, [1, 2, 3], source).KeysInserted);
        Assert.False(cache.TryGetList(3, out _));
        Assert.True(cache.GetStats().NotAdmitted >= 4);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void FillHoldAdmitsAnAtomOnlyWhenAllItsListsFitAndNeverEvictsForIt()
    {
        long listBytes = (5 + 5 / 8 + 2) * 4 + ResidentKeyListCache.ListOverheadBytes;
        var lists = Enumerable.Range(1, 12).ToDictionary(k => (long)k, k => Range(k * 10, 5));
        var source = new FixedSource(lists);

        // room for five lists: the first atom of three takes three places, the second atom of three would need one more than is free
        using (ResidentKeyListCache hold = Established(new ResidentCacheOptions { Admission = ResidentAdmission.FillHold, BudgetBytes = 5 * listBytes }))
        {
            Assert.Equal(3, Union(hold, 1, [1, 2, 3], source).KeysInserted);
            Assert.Equal(0, Union(hold, 2, [4, 5, 6], source).KeysInserted);
            Assert.Equal(3, hold.ResidentKeys);
            Assert.Equal(1, Union(hold, 3, [7], source).KeysInserted);
            Assert.Equal(0, hold.GetStats().Evictions);

            // the key level check: a list that does not fit is not kept by evicting another
            Assert.Equal(ResidentInsertResult.Inserted, hold.TryInsert(Env, Epoch.TransactionId, 8, lists[8]));
            Assert.Equal(ResidentInsertResult.NoRoom, hold.TryInsert(Env, Epoch.TransactionId, 9, lists[9]));
            Assert.Equal(0, hold.GetStats().Evictions);
            Assert.True(hold.TryGetList(1, out _));
        }

        // the same budget without fill-hold: the second atom takes the places of the first
        using ResidentKeyListCache always = Established(new ResidentCacheOptions { BudgetBytes = 5 * listBytes });
        Assert.Equal(3, Union(always, 1, [1, 2, 3], source).KeysInserted);
        Assert.Equal(3, Union(always, 2, [4, 5, 6], source).KeysInserted);
        Assert.True(always.GetStats().Evictions > 0);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void WarmUpFillsTheBudgetInOrderWithoutEvictingAndSkipsAListThatCanNeverFit()
    {
        long listBytes = (5 + 5 / 8 + 2) * 4 + ResidentKeyListCache.ListOverheadBytes;
        var lists = Enumerable.Range(1, 10).ToDictionary(k => (long)k, k => Range(k * 10, 5));
        lists[3] = Range(0, 10_000, 3);
        var source = new SourceBox(new FixedSource(lists));
        using ResidentKeyListCache cache = Established(new ResidentCacheOptions { BudgetBytes = 4 * listBytes });
        ResidentWarmUpResult result = cache.WarmUp(Epoch, Enumerable.Range(1, 10).Select(k => (long)k).ToArray(), ref source, default);
        Assert.Equal(4, result.Loaded);
        Assert.Equal(1, result.Skipped);
        Assert.True(result.Full);
        Assert.Equal(0, cache.GetStats().Evictions);
        foreach (long key in new long[] { 1, 2, 4, 5 })
            Assert.True(cache.TryGetList(key, out _), $"key {key}");

        Assert.False(cache.TryGetList(3, out _));
        Assert.False(cache.TryGetList(6, out _));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheRegistryChargesEveryCacheAndNeverGoesOverItsBudget()
    {
        var registry = new ResidentCacheRegistry(30_000, registerWithLowMemoryNotification: false);
        var first = Established(new ResidentCacheOptions(), registry);
        var second = Established(new ResidentCacheOptions(), registry);
        Assert.Equal(2, registry.CacheCount);
        var random = new Random(3);
        for (int i = 0; i < 2_000; i++)
        {
            ResidentKeyListCache cache = i % 2 == 0 ? first : second;
            cache.TryInsert(Env, Epoch.TransactionId, 1_000 + i * 7L, Range(random.Next(500), 1 + random.Next(80)));
            Assert.True(registry.ChargedBytes <= registry.BudgetBytes, $"{registry.ChargedBytes} charged above {registry.BudgetBytes}");
            Assert.Equal(first.ResidentBytes + second.ResidentBytes, registry.ChargedBytes);
        }

        Assert.True(first.ResidentBytes > 0 && second.ResidentBytes > 0);
        Assert.True(first.GetStats().Evictions + second.GetStats().Evictions > 0);

        // growth by patch is checked against the registry too
        ResidentEpoch next = Epoch with { TransactionId = 101 };
        using (var patch = new ResidentPatch())
        {
            for (int i = 0; i < 200; i++)
            {
                for (int key = 0; key < 20; key++)
                    patch.AddRow(i * 100L + key, [1_000 + (2 * key) * 7L]);
            }

            first.ApplyPatch(Epoch, next, 100_000, patch);
        }

        Assert.True(registry.ChargedBytes <= registry.BudgetBytes);
        Assert.Equal(first.ResidentBytes + second.ResidentBytes, registry.ChargedBytes);

        first.Dispose();
        Assert.Equal(second.ResidentBytes, registry.ChargedBytes);
        Assert.Equal(1, registry.CacheCount);
        second.Dispose();
        Assert.Equal(0, registry.ChargedBytes);
        Assert.Equal(0, registry.CacheCount);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void LowMemoryClearsEveryCacheAndKeepsNewListsOutUntilItIsOver()
    {
        var registry = new ResidentCacheRegistry(1 << 20, registerWithLowMemoryNotification: false);
        using ResidentKeyListCache first = Established(new ResidentCacheOptions(), registry);
        using ResidentKeyListCache second = Established(new ResidentCacheOptions(), registry);
        foreach (ResidentKeyListCache cache in new[] { first, second })
        {
            for (long key = 1; key <= 20; key++)
                Assert.Equal(ResidentInsertResult.Inserted, cache.TryInsert(Env, Epoch.TransactionId, key, Range(1, 50)));
        }

        long charged = registry.ChargedBytes;
        Assert.True(charged > 0);
        registry.LowMemory(LowMemorySeverity.None);
        Assert.Equal(charged, registry.ChargedBytes);

        registry.LowMemory(LowMemorySeverity.ExtremelyLow);
        Assert.True(registry.IsLowMemory);
        foreach (ResidentKeyListCache cache in new[] { first, second })
        {
            Assert.Equal(0, cache.ResidentKeys);
            Assert.Equal(0, cache.ResidentBytes);
            Assert.Equal(Epoch, cache.Epoch); // an empty cache is valid for its epoch
            Assert.Equal(1, cache.GetStats().LowMemoryDrops);
            Assert.Equal(ResidentInsertResult.Disabled, cache.TryInsert(Env, Epoch.TransactionId, 1, Range(1, 5)));
        }

        Assert.Equal(0, registry.ChargedBytes);

        registry.LowMemoryOver();
        Assert.False(registry.IsLowMemory);
        Assert.Equal(ResidentInsertResult.Inserted, first.TryInsert(Env, Epoch.TransactionId, 1, Range(1, 5)));
        Assert.Equal(first.ResidentBytes, registry.ChargedBytes);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ThePatchCollectsRowsAsTheRefreshSeesThem()
    {
        long before = QuietAllocated();
        using (var patch = new ResidentPatch())
        {
            // the same entry with the same keys: nothing to do
            patch.ChangeRow(5, [1, 2], 5, [2, 1]);
            Assert.Equal(0, patch.AddCount + patch.RemoveCount);

            // the same entry, one key lost and one gained
            patch.ChangeRow(6, [1, 2], 6, [2, 3]);
            Assert.Equal(1, patch.RemoveCount);
            Assert.Equal(1, patch.AddCount);

            // another entry: all old keys lose the old one and all new keys get the new one, also the keys that stay
            patch.ChangeRow(7, [1, 2], 8, [2, 3]);
            Assert.Equal(3, patch.RemoveCount);
            Assert.Equal(3, patch.AddCount);

            // many keys take the sorted path and give the same answer
            long[] oldKeys = Enumerable.Range(0, 100).Select(x => (long)x).ToArray();
            long[] newKeys = Enumerable.Range(50, 100).Select(x => (long)x).Reverse().ToArray();
            patch.ChangeRow(9, oldKeys, 9, newKeys);
            Assert.Equal(3 + 50, patch.RemoveCount);
            Assert.Equal(3 + 50, patch.AddCount);

            patch.AddRow(10, [4, 5]);
            patch.RemoveRow(11, [4]);
            Assert.Equal(53 + 1, patch.RemoveCount);
            Assert.Equal(53 + 2, patch.AddCount);
            Assert.Equal(6, patch.Documents);
            Assert.Throws<ArgumentOutOfRangeException>(() => patch.AddRow(-1, [1]));
        }

        Assert.Equal(before, Allocated);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void EstimateAndEnableFollowTheBudget()
    {
        using var small = new ResidentKeyListCache(new ResidentCacheOptions { BudgetBytes = 10_000 });
        using var big = new ResidentKeyListCache(new ResidentCacheOptions { BudgetBytes = 64L << 20 });
        Assert.False(small.ShouldEnable(1_000_000, 5_005));
        Assert.True(big.ShouldEnable(1_000_000, 5_005));
        Assert.False(big.ShouldEnable(100_000_000, 10_000_000));
    }
}
