using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

/// <summary>The lists that a union reads from the store are inserted a batch at a time: the write lock is taken once for many lists, and what the cache holds and the union returns are the same as for one list at a time.</summary>
[Collection(ResidentNativeMemoryCollection.Name)]
public unsafe class ResidentCacheBatchInsertTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    private static readonly Guid Env = Guid.NewGuid();
    private static readonly ResidentEpoch Epoch = new(Env, 100, 1);

    private sealed class Lists(Dictionary<long, long[]> lists) : IResidentListSource
    {
        public long CountOf(long keyRef) => lists[keyRef].Length;

        public ReadOnlySpan<long> Read(long keyRef) => lists[keyRef];
    }

    private static Dictionary<long, long[]> MakeLists(int keys, int maxIds, int seed)
    {
        var random = new Random(seed);
        var lists = new Dictionary<long, long[]>();
        for (int k = 0; k < keys; k++)
        {
            int n = 1 + random.Next(maxIds);
            lists[1_000 + k * 7L] = Enumerable.Range(0, n).Select(i => (long)random.Next(0, 60_000)).Distinct().Order().ToArray();
        }

        return lists;
    }

    private static ResidentKeyListCache Established(ResidentCacheOptions options)
    {
        var cache = new ResidentKeyListCache(options);
        Assert.True(cache.TryEstablish(Epoch, 1_000_000));
        return cache;
    }

    private static (ulong[] Words, ResidentUnion Union) Union(ResidentKeyListCache cache, Dictionary<long, long[]> lists, long[] keys)
    {
        var words = new ulong[1_000];
        var source = new Lists(lists);
        fixed (ulong* w = words)
            return (words, cache.UnionKeys(Env, Epoch.TransactionId, 1, keys, ref source, w, words.Length));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheListsOfAUnionAreInsertedUnderFewHoldsOfTheWriteLock()
    {
        Dictionary<long, long[]> lists = MakeLists(1_000, 20, 5);
        long[] keys = lists.Keys.ToArray();
        using ResidentKeyListCache cache = Established(new ResidentCacheOptions());

        (ulong[] words, ResidentUnion union) = Union(cache, lists, keys);
        Assert.Equal(1_000, union.KeysFromStore);
        Assert.Equal(1_000, union.KeysInserted);
        Assert.Equal(1_000, cache.ResidentKeys);

        // 64 lists to a batch: 16 holds of the lock, not 1,000
        Assert.True(cache.InsertLockEntries <= 1_000 / 64 + 2, $"{cache.InsertLockEntries} holds of the write lock");

        foreach (long key in keys)
        {
            Assert.True(cache.TryGetList(key, out long[] ids), key.ToString());
            Assert.Equal(lists[key], ids);
        }

        // the second union reads every list from the cache and gives the same entries
        (ulong[] again, ResidentUnion second) = Union(cache, lists, keys);
        Assert.Equal(1_000, second.KeysFromCache);
        Assert.Equal(0, second.KeysFromStore);
        Assert.Equal(words, again);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AUnionThatFillsTheBudgetInTheMiddleOfABatchLeavesTheCacheOfOneInsertAtATime()
    {
        Dictionary<long, long[]> lists = MakeLists(400, 60, 9);
        long[] keys = lists.Keys.ToArray();

        // one list at a time, in the order of the keys, with eviction when the budget is full
        using ResidentKeyListCache single = Established(new ResidentCacheOptions { BudgetBytes = 20_000 });
        foreach (long key in keys)
            single.TryInsert(Env, Epoch.TransactionId, key, lists[key]);

        using ResidentKeyListCache batch = Established(new ResidentCacheOptions { BudgetBytes = 20_000 });
        (_, ResidentUnion union) = Union(batch, lists, keys);

        Assert.Equal(400, union.KeysFromStore);
        ResidentCacheStats expected = single.GetStats();
        ResidentCacheStats actual = batch.GetStats();
        Assert.True(expected.Evictions > 0 && expected.ResidentKeys < 400, "the budget fills in the middle");
        Assert.Equal(expected.ResidentKeys, actual.ResidentKeys);
        Assert.Equal(expected.ResidentBytes, actual.ResidentBytes);
        Assert.Equal(expected.Inserts, actual.Inserts);
        Assert.Equal(expected.Evictions, actual.Evictions);
        Assert.Equal(expected.Rejected, actual.Rejected);
        foreach (long key in keys)
        {
            bool inSingle = single.TryGetList(key, out long[] a);
            Assert.Equal(inSingle, batch.TryGetList(key, out long[] b));
            if (inSingle)
                Assert.Equal(a, b);
        }

        Assert.True(batch.InsertLockEntries < 400 / 8, $"{batch.InsertLockEntries} holds of the write lock");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AListThatIsLargerThanTheBatchIsInsertedAfterTheListsBeforeItInTheOrderOfTheKeys()
    {
        var random = new Random(3);
        var lists = new Dictionary<long, long[]>();
        long[] keys = Enumerable.Range(0, 12).Select(k => 10L + k).ToArray();
        foreach (long key in keys)
        {
            int n = key == 15 ? 40_000 : 1 + random.Next(30);
            lists[key] = Enumerable.Range(0, n).Select(i => (long)(i * 3 + (int)(key % 3))).ToArray();
        }

        using ResidentKeyListCache cache = Established(new ResidentCacheOptions());
        var words = new ulong[2_000];
        var source = new Lists(lists);
        ResidentUnion union;
        fixed (ulong* w = words)
            union = cache.UnionKeys(Env, Epoch.TransactionId, 1, keys, ref source, w, words.Length);

        Assert.Equal(12, union.KeysInserted);
        foreach (long key in keys)
        {
            Assert.True(cache.TryGetList(key, out long[] ids), key.ToString());
            Assert.Equal(lists[key], ids);
        }

        // 11 small lists before and after the large one: two batches and the large list alone
        Assert.Equal(3, cache.InsertLockEntries);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AnotherEpochKeepsNothingAndTakesNoWriteLockForTheLists()
    {
        Dictionary<long, long[]> lists = MakeLists(200, 10, 11);
        long[] keys = lists.Keys.ToArray();
        using ResidentKeyListCache cache = Established(new ResidentCacheOptions());
        var words = new ulong[1_000];
        var source = new Lists(lists);
        ResidentUnion union;
        fixed (ulong* w = words)
            union = cache.UnionKeys(Env, Epoch.TransactionId + 1, 1, keys, ref source, w, words.Length);

        Assert.False(union.EpochHit);
        Assert.Equal(200, union.KeysFromStore);
        Assert.Equal(0, union.KeysInserted);
        Assert.Equal(0, cache.ResidentKeys);
        Assert.Equal(0, cache.InsertLockEntries);
    }
}
