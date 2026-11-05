using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;
using static FastTests.Server.Documents.Indexing.Joins.Resident.ResidentOuterModel;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

/// <summary>
/// A refresh that changes many documents patches the lists of the cache instead of clearing it: the changes of a key are applied together (a long array is merged in one pass), and a patch of the size of
/// one refresh is not too large for the cache.
/// </summary>
public class ResidentPatchBulkTests(ITestOutputHelper output) : RavenTestBase(output), IIndirectModeTest
{
    private const int Keys = 5;
    private static readonly Guid Env = Guid.NewGuid();

    public static IEnumerable<object[]> Seeds()
    {
        for (int seed = 1; seed <= 12; seed++)
            yield return [seed, (IdPolicy)((seed - 1) % 3)];
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [MemberData(nameof(Seeds))]
    public void BulkRefreshesOfManyDocumentsAreAppliedAndEveryListEqualsTheModel(int seed, IdPolicy policy)
    {
        var random = new Random(seed);
        var model = new ResidentOuterModel(policy, firstEntry: seed % 2 == 0 ? 1 : 5_000);
        long[] allKeys = Enumerable.Range(0, Keys).Select(KeyRef).ToArray();

        // key 0 is a hub (dense), key 4 is sparse (a row in a hundred), keys 1-3 are the middle
        long[] RandomKeys(int doc)
        {
            var keys = new List<int>();
            if (random.Next(3) == 0)
                keys.Add(0);
            if (doc % 100 == 0)
                keys.Add(4);
            if (random.Next(2) == 0)
                keys.Add(1 + random.Next(3));
            if (random.Next(5) == 0)
                keys.Add(1 + random.Next(3));

            return KeyRefs(keys.ToArray());
        }

        model.Write(Enumerable.Range(0, 3_000).Select(i => ("d" + i, RandomKeys(i))), []);
        using var rig = new ResidentCacheRig(model, new ResidentCacheOptions(), establish: false) { AllKeys = allKeys };
        rig.Establish();
        Assert.Equal(Keys, rig.Cache.ResidentKeys);

        int largest = 0;
        for (int step = 0; step < 14; step++)
        {
            int count = 150 + random.Next(600);
            var taken = new HashSet<int>();
            var upserts = new List<(string, long[])>();
            var deletes = new List<string>();
            for (int n = 0; n < count; n++)
            {
                int doc = random.Next(4_000);
                if (taken.Add(doc) == false)
                    continue;

                if (model.Has("d" + doc) && random.Next(8) == 0)
                    deletes.Add("d" + doc);
                else
                    upserts.Add(("d" + doc, RandomKeys(doc)));
            }

            largest = Math.Max(largest, upserts.Count + deletes.Count);
            Assert.Equal(ResidentPatchOutcome.Applied, rig.Refresh(upserts, deletes));
            Assert.Equal(Keys, rig.AssertInvariant($"seed {seed} step {step}"));
            rig.AssertUnion(model.Current, 1, allKeys, $"seed {seed} step {step} union");
        }

        Assert.True(largest > 150);
        Assert.Equal(0, rig.Cache.GetStats().Invalidations);
        Assert.Equal(1, rig.Cache.FormOf(KeyRef(0)));
        Assert.Equal(0, rig.Cache.FormOf(KeyRef(4)));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheChangesOfOneKeyAreMergedIntoAnArrayWithDuplicatesPresentAndAbsentEntries()
    {
        var epoch = new ResidentEpoch(Env, 100, 1);
        using var cache = new ResidentKeyListCache(new ResidentCacheOptions(), null);
        Assert.True(cache.TryEstablish(epoch, 1_000_000));

        // a sparse list: the array form
        long[] list = Enumerable.Range(0, 2_000).Select(i => i * 100L).ToArray();
        Assert.Equal(ResidentInsertResult.Inserted, cache.TryInsert(Env, epoch.TransactionId, 7, list));
        Assert.Equal(0, cache.FormOf(7));
        Assert.Equal(ResidentInsertResult.Inserted, cache.TryInsert(Env, epoch.TransactionId, 8, list));

        var expected = new SortedSet<long>(list);
        using (var patch = new ResidentPatch())
        {
            // present entries again, new entries between and after the last one, the same new entry twice, entries that are removed and absent ones
            long[] adds = [0, 100, 150, 150, 151, 99_950, 199_900, 199_901, 500_000, 500_001, 7];
            long[] removes = [200, 300, 350, 199_900_000, 1_900, 5, 1_900];
            foreach (long entry in adds)
                patch.AddRow(entry, [7]);

            foreach (long entry in removes)
                patch.RemoveRow(entry, [7]);

            // key 8 gets the same changes as single operations of a short group: not merged
            patch.AddRow(150, [8]);
            patch.RemoveRow(200, [8]);

            // removals first: 1_900 is removed and 150 added, as one row
            Assert.Equal(ResidentPatchOutcome.Applied, cache.ApplyPatch(epoch, epoch with { TransactionId = 101 }, 1_000_000, patch));
        }

        foreach (long entry in new long[] { 200, 300, 350, 1_900 })
            expected.Remove(entry);

        foreach (long entry in new long[] { 0, 100, 150, 151, 99_950, 199_900, 199_901, 500_000, 500_001, 7 })
            expected.Add(entry);

        Assert.True(cache.TryGetList(7, out long[] actual));
        Assert.True(expected.SequenceEqual(actual), $"expected {expected.Count} entries, the list has {actual.Length}");

        var shortGroup = new SortedSet<long>(list);
        shortGroup.Remove(200);
        shortGroup.Add(150);
        Assert.True(cache.TryGetList(8, out long[] other));
        Assert.True(shortGroup.SequenceEqual(other));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void RemovingMostOfALongArrayInOnePassShrinksItAndAKeyThatIsNotResidentIsLeftAlone()
    {
        var epoch = new ResidentEpoch(Env, 100, 1);
        using var cache = new ResidentKeyListCache(new ResidentCacheOptions(), null);
        Assert.True(cache.TryEstablish(epoch, 1_000_000));
        long[] list = Enumerable.Range(0, 3_000).Select(i => i * 100L).ToArray();
        Assert.Equal(ResidentInsertResult.Inserted, cache.TryInsert(Env, epoch.TransactionId, 7, list));
        long before = cache.ResidentBytes;

        using (var patch = new ResidentPatch())
        {
            for (int i = 0; i < 2_900; i++)
                patch.RemoveRow(i * 100L, [7, 99]);

            Assert.Equal(ResidentPatchOutcome.Applied, cache.ApplyPatch(epoch, epoch with { TransactionId = 101 }, 1_000_000, patch));
        }

        Assert.True(cache.TryGetList(7, out long[] actual));
        Assert.True(list.Skip(2_900).SequenceEqual(actual));
        Assert.True(cache.ResidentBytes < before / 4, $"{cache.ResidentBytes} bytes after {before}");
        Assert.False(cache.TryGetList(99, out _));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ThePatchOfOneRefreshIsNotTooLargeForTheCacheByDefault()
    {
        Assert.True(new ResidentCacheOptions().MaxPatchDocuments >= new JoinOptions().MaxIdsPerCommit);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ABulkOfMoreDocumentsThanTheOldLimitKeepsTheCacheAndItsListsAreTheOuterPostingLists()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, new Dictionary<string, string> { ["Indexing.MapBatchSize"] = "100000" }, arrays: false);
        for (int i = 0; i < 8; i++)
            h.PutCodedContext(i, "k" + i, i % 2 == 0 ? "nl" : "de", "n" + i % 4, i * 5, true, "w" + i);

        for (int i = 0; i < 100; i++)
            h.PutConversation(i, "k" + i % 8);

        await h.AssertCurrentAsync();
        await WaitForAsync(() => h.Join().Resident is { } resident && resident.Cache.Epoch.IsNone == false, "the cache has an epoch");
        JoinResident resident = h.Join().Resident;
        long establishes = resident.Establishes;
        long applied = resident.PatchesApplied;

        await using (var bulk = h.Store.BulkInsert())
        {
            for (int i = 0; i < 12_000; i++)
                await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextId = "k" + i % 8, Title = "t" }, $"conversations/{1_000 + i}");
        }

        await h.AssertCurrentAsync();
        await WaitForAsync(() => resident.PatchesApplied > applied, "a patch was applied");
        Assert.Equal(0, resident.PatchTooLarge);
        Assert.Equal(establishes, resident.Establishes);
        Assert.False(resident.Cache.Epoch.IsNone);

        // a reader moves the epoch to the newest snapshot of the outer index when the index committed without a change of its entries
        new JoinIndexQueryOracle(h).Check($"{Outer} join (from index 'Contexts/Search') as c on m.ContextId = c.Code order by m.Title limit 0, 50");
        Assert.True(await h.AssertResidentAsync(atLeast: 1) > 0);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 1000 && condition() == false; i++)
            await Task.Delay(20);

        Assert.True(condition(), what);
    }
}
