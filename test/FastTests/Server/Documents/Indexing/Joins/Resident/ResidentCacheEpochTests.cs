using System;
using System.Linq;
using System.Threading;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Resident.ResidentOuterModel;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

/// <summary>The rules of the epoch: which reader may use the lists, when a patch is applied, dropped or refused, what clears the cache, and the cases of entry ids.</summary>
public class ResidentCacheEpochTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    private static readonly long A = KeyRef(1), B = KeyRef(2), C = KeyRef(3), Hub = KeyRef(0);

    private static ResidentCacheRig RigOf(ResidentOuterModel model, ResidentCacheOptions options = null, params long[] keys)
    {
        var rig = new ResidentCacheRig(model, options ?? new ResidentCacheOptions(), establish: false) { AllKeys = keys };
        rig.Establish();
        return rig;
    }

    private static long[] Ids(ResidentCacheRig rig, long key)
    {
        Assert.True(rig.Cache.TryGetList(key, out long[] ids), $"key {key} is not resident");
        return ids;
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AReaderWithAnotherOuterSnapshotThanTheListsReadsThroughTheStore()
    {
        var model = new ResidentOuterModel(IdPolicy.AlwaysNew);
        model.Write([("o0", [A]), ("o1", [A]), ("o2", [B])], []);
        using ResidentCacheRig rig = RigOf(model, null, A, B);
        Assert.Equal(2, rig.Cache.ResidentKeys);

        Snapshot warm = model.Current;
        long[] all = rig.Union(warm, 1, [A, B], out ResidentUnion union);
        Assert.True(union.EpochHit);
        Assert.Equal(2, union.KeysFromCache);
        Assert.Equal(3, all.Length);

        // a reader that opened its outer snapshot before the write
        Snapshot old = model.Current;
        Assert.Equal(ResidentPatchOutcome.Applied, rig.Refresh([("o0", [B]), ("o3", [A])], []));
        Snapshot fresh = model.Current;
        rig.AssertUnion(fresh, 1, [A, B], "reader of the snapshot of the epoch");
        long storeReads = rig.ReadsFromStore;

        // the old reader reads the store of ITS snapshot: the patched lists are not valid for it, and it keeps nothing
        Assert.Equal(old.RowsOfAny([A, B]), rig.Union(old, 1, [A, B], out union));
        Assert.False(union.EpochHit);
        Assert.Equal(2, union.KeysFromStore);
        Assert.Equal(storeReads + 2, rig.ReadsFromStore);
        rig.AssertInvariant("after the old reader");

        // a write whose patch has not arrived yet: a reader of the newer snapshot must not use the older lists
        model.Write([("o4", [B])], []);
        long[] viaStore = rig.Union(model.Current, 1, [A, B], out union);
        Assert.False(union.EpochHit);
        Assert.Equal(model.Current.RowsOfAny([A, B]), viaStore);
        Assert.Equal(5, viaStore.Length);

        // the same snapshot number in another environment is not the epoch
        var other = new Snapshot { EnvironmentId = Guid.NewGuid(), TransactionId = rig.Cache.Epoch.TransactionId, LastEntryId = fresh.LastEntryId, Lists = fresh.Lists, Rows = fresh.Rows };
        rig.Union(other, 1, [A, B], out union);
        Assert.False(union.EpochHit);
        Assert.True(rig.Cache.GetStats().EpochMisses >= 3);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AnEmptyTransactionLeavesTheReadersOnTheStoreUntilTheNextRefresh()
    {
        var model = new ResidentOuterModel(IdPolicy.KeepOnUpdate);
        model.Write([("o0", [A]), ("o1", [B])], []);
        using ResidentCacheRig rig = RigOf(model, null, A, B);

        model.Touch();
        rig.Union(model.Current, 1, [A, B], out ResidentUnion union);
        Assert.False(union.EpochHit);

        // the refresh that follows has nothing to patch, but moves the epoch: the cache is still valid for it
        Assert.Equal(ResidentPatchOutcome.Applied, rig.Refresh([("o2", [A])], []));
        rig.Union(model.Current, 1, [A, B], out union);
        Assert.True(union.EpochHit);
        rig.AssertInvariant("after the refresh");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void APatchThatIsDroppedAfterARollbackChangesNothingAndTheRetryIsExact()
    {
        var model = new ResidentOuterModel(IdPolicy.AlwaysNew);
        model.Write([("o0", [A]), ("o1", [A, B]), ("o2", [C])], []);
        using ResidentCacheRig rig = RigOf(model, null, A, B, C);
        Snapshot s0 = model.Current;
        ResidentEpoch epoch0 = rig.Cache.Epoch;
        long[] a0 = Ids(rig, A), b0 = Ids(rig, B), c0 = Ids(rig, C);

        // the first attempt: the refresh collected the ops of a write, the join index transaction rolled back, the patch is disposed unapplied
        var firstChanges = model.Write([("o0", [B]), ("o3", [A, C])], ["o2"]);
        using (ResidentPatch first = PatchOf(firstChanges))
            Assert.True(first.AddCount > 0 && first.RemoveCount > 0);

        Assert.Equal(epoch0, rig.Cache.Epoch);
        Assert.Equal(a0, Ids(rig, A));
        Assert.Equal(b0, Ids(rig, B));
        Assert.Equal(c0, Ids(rig, C));
        rig.AssertInvariant("after the dropped patch");

        // the document changes again before the retry; the retry sees the changes between the file (the snapshot of the epoch) and the new snapshot, not the changes of the first attempt
        model.Write([("o3", [B]), ("o0", [C])], []);
        using (ResidentPatch retry = PatchOf(ResidentOuterModel.Diff(s0, model.Current)))
            Assert.Equal(ResidentPatchOutcome.Applied, rig.Apply(epoch0, retry));

        rig.AssertInvariant("after the retry");
        foreach (long key in new[] { A, B, C })
            Assert.Equal(model.Current.ListOf(key), Ids(rig, key));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AnExceptionInTheMiddleOfAPatchClearsTheCacheAndTheEpoch()
    {
        var model = new ResidentOuterModel(IdPolicy.AlwaysNew);
        model.Write(Enumerable.Range(0, 10).Select(i => ("o" + i, new[] { i % 2 == 0 ? A : B })), []);
        using ResidentCacheRig rig = RigOf(model, null, A, B);
        Assert.Equal(2, rig.Cache.ResidentKeys);

        ResidentEpoch before = rig.Cache.Epoch;
        var changes = model.Write(Enumerable.Range(0, 6).Select(i => ("o" + i, new[] { i % 2 == 0 ? B : A })), []);
        rig.Cache.ForTestingPurposes = new ResidentKeyListCache.TestHooks { BeforePatchOp = n => { if (n == 3) throw new InvalidOperationException("the hook failed half-way"); } };
        using (ResidentPatch patch = PatchOf(changes))
        {
            Assert.True(patch.RemoveCount + patch.AddCount > 3);
            Assert.Throws<InvalidOperationException>(() => rig.Cache.ApplyPatch(before, rig.EpochOf(model.Current), model.Current.LastEntryId, patch));
        }

        rig.Cache.ForTestingPurposes = null;
        Assert.Equal(ResidentEpoch.None, rig.Cache.Epoch);
        Assert.Equal(0, rig.Cache.ResidentKeys);
        Assert.Equal(0, rig.Cache.ResidentBytes);
        rig.AssertUnion(model.Current, 1, [A, B], "after the exception");
        rig.Establish();
        rig.AssertInvariant("established again");
        Assert.Equal(2, rig.Cache.ResidentKeys);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AnInvalidatedCacheServesNothingUntilItIsEstablishedAgain()
    {
        var model = new ResidentOuterModel(IdPolicy.KeepOnUpdate);
        model.Write([("o0", [A]), ("o1", [B])], []);
        using ResidentCacheRig rig = RigOf(model, null, A, B);
        Assert.Equal(2, rig.Cache.ResidentKeys);

        // the first chunk of a refresh that is cut: the cache is not the snapshot of any transaction
        rig.Cache.Invalidate();
        Assert.Equal(ResidentEpoch.None, rig.Cache.Epoch);
        Assert.Equal(0, rig.Cache.ResidentKeys);
        Assert.Equal(0, rig.Cache.ResidentBytes);
        model.Write([("o2", [A])], []);
        rig.AssertUnion(model.Current, 1, [A, B], "between chunks");
        rig.Union(model.Current, 1, [A, B], out ResidentUnion union);
        Assert.False(union.EpochHit);
        Assert.Equal(0, rig.Cache.ResidentKeys);

        // the commit that ends the outer side
        rig.Establish();
        rig.Union(model.Current, 1, [A, B], out union);
        Assert.True(union.EpochHit);
        rig.AssertInvariant("after the last chunk");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ARestartIsAnEmptyCacheAndTheFirstPatchEstablishesAgain()
    {
        var model = new ResidentOuterModel(IdPolicy.AlwaysNew);
        model.Write([("o0", [A]), ("o1", [A, B])], []);
        using ResidentCacheRig rig = RigOf(model, null, A, B);
        rig.Restart();
        Assert.Equal(ResidentEpoch.None, rig.Cache.Epoch);
        Assert.Equal(0, rig.Cache.ResidentKeys);
        rig.AssertUnion(model.Current, 1, [A, B], "after the restart");

        // the refresh that runs first after the open does not start from an epoch of the cache: nothing is guessed, the cache is established at its snapshot
        Assert.Equal(ResidentPatchOutcome.EpochMismatch, rig.Refresh([("o2", [B])], []));
        rig.Union(model.Current, 1, [A, B], out ResidentUnion union);
        Assert.True(union.EpochHit);
        rig.AssertInvariant("after the restart and a refresh");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void APatchThatDoesNotStartFromTheEpochOfTheCacheClearsIt()
    {
        var model = new ResidentOuterModel(IdPolicy.AlwaysNew);
        model.Write([("o0", [A]), ("o1", [B])], []);
        using ResidentCacheRig rig = RigOf(model, null, A, B);
        ResidentEpoch current = rig.Cache.Epoch;

        foreach (ResidentEpoch other in new[] { current with { Generation = current.Generation + 1 }, current with { TransactionId = current.TransactionId + 1 }, current with { EnvironmentId = Guid.NewGuid() }, ResidentEpoch.None })
        {
            using var patch = new ResidentPatch();
            patch.AddRow(500, [A]);
            Assert.Equal(ResidentPatchOutcome.EpochMismatch, rig.Cache.ApplyPatch(other, current with { TransactionId = current.TransactionId + 5 }, 600, patch));
            Assert.Equal(ResidentEpoch.None, rig.Cache.Epoch);
            Assert.Equal(0, rig.Cache.ResidentKeys);
            rig.Establish();
            current = rig.Cache.Epoch;
            Assert.Equal(2, rig.Cache.ResidentKeys);
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void APatchOfMoreDocumentsThanOneLockHoldClearsTheCache()
    {
        var model = new ResidentOuterModel(IdPolicy.AlwaysNew);
        model.Write(Enumerable.Range(0, 40).Select(i => ("o" + i, new[] { A })), []);
        using ResidentCacheRig rig = RigOf(model, new ResidentCacheOptions { MaxPatchDocuments = 10 }, A, B);
        Assert.Equal(ResidentPatchOutcome.Applied, rig.Refresh(Enumerable.Range(0, 10).Select(i => ("o" + i, new[] { B })), []));
        Assert.Equal(ResidentPatchOutcome.TooLarge, rig.Refresh(Enumerable.Range(10, 11).Select(i => ("o" + i, new[] { B })), []));
        Assert.True(rig.Cache.GetStats().Invalidations >= 1);

        // established again by the rig: the answers are exact and the cache serves
        rig.AssertUnion(model.Current, 1, [A, B], "after the large patch");
        rig.AssertInvariant("after the large patch");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void EntryIdsAbove2To32AreRefusedWithoutAnException()
    {
        const long Limit = uint.MaxValue;
        var model = new ResidentOuterModel(IdPolicy.AlwaysNew, firstEntry: Limit - 3);
        model.Write([("o0", [A]), ("o1", [A]), ("o2", [B])], []); // entries Limit-3 .. Limit-1

        using ResidentCacheRig rig = RigOf(model, null, A, B);
        Assert.Equal(2, rig.Cache.ResidentKeys);
        Assert.Equal(Limit - 1, model.Current.LastEntryId);

        // the entries of the next write pass 2^32: the patch is refused, the cache is cleared and has no epoch, the store path serves
        ResidentEpoch before = rig.Cache.Epoch;
        model.Write([("o3", [A]), ("o4", [A]), ("o5", [B])], []);
        Assert.True(model.Current.LastEntryId > Limit);
        using (ResidentPatch patch = PatchOf(ResidentOuterModel.Diff(model.History[before.TransactionId], model.Current)))
            Assert.Equal(ResidentPatchOutcome.IdTooLarge, rig.Cache.ApplyPatch(before, rig.EpochOf(model.Current), model.Current.LastEntryId, patch));

        Assert.Equal(ResidentEpoch.None, rig.Cache.Epoch);
        Assert.Equal(0, rig.Cache.ResidentKeys);
        using (ResidentKeyListCache.ReadLease lease = rig.Cache.TryBeginRead(model.EnvironmentId, model.Current.TransactionId))
            Assert.False(lease.IsValid);

        // no epoch can be established at such an outer index, and reading does not keep anything
        Assert.False(rig.Cache.TryEstablish(rig.EpochOf(model.Current), model.Current.LastEntryId));
        Assert.Equal(ResidentEpoch.None, rig.Cache.Epoch);
        Assert.Equal(ResidentInsertResult.EpochMismatch, rig.Cache.TryInsert(model.EnvironmentId, model.Current.TransactionId, A, model.Current.ListOf(A)));
        Assert.Equal(0, rig.Cache.ResidentKeys);
        Assert.True(rig.Cache.GetStats().IdTooLarge >= 2);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AListWithAnEntryAbove2To32IsNotKeptAndAResidentListThatWouldNeedOneIsDropped()
    {
        var env = Guid.NewGuid();
        var epoch = new ResidentEpoch(env, 7, 1);
        using var cache = new ResidentKeyListCache(new ResidentCacheOptions());
        Assert.True(cache.TryEstablish(epoch, 100));
        Assert.Equal(ResidentInsertResult.IdTooLarge, cache.TryInsert(env, 7, A, [1, 2, uint.MaxValue + 1L]));
        Assert.Equal(ResidentInsertResult.Inserted, cache.TryInsert(env, 7, B, [1, 2, uint.MaxValue]));
        Assert.Equal(ResidentInsertResult.Inserted, cache.TryInsert(env, 7, C, [3]));
        Assert.False(cache.TryGetList(A, out _));

        // a caller that passes a last entry id below the limit with an op above it is inconsistent: the list of the key is dropped, the others are kept
        using var patch = new ResidentPatch();
        patch.AddRow(uint.MaxValue + 5L, [B]);
        patch.AddRow(4, [C]);
        Assert.Equal(ResidentPatchOutcome.Applied, cache.ApplyPatch(epoch, epoch with { TransactionId = 8 }, 100, patch));
        Assert.False(cache.TryGetList(B, out _));
        Assert.True(cache.TryGetList(C, out long[] c));
        Assert.Equal([3L, 4L], c);

        // a removal of an id the uint form cannot hold removes nothing and keeps the list
        using var removal = new ResidentPatch();
        removal.RemoveRow(uint.MaxValue + 9L, [C]);
        Assert.Equal(ResidentPatchOutcome.Applied, cache.ApplyPatch(epoch with { TransactionId = 8 }, epoch with { TransactionId = 9 }, 100, removal));
        Assert.True(cache.TryGetList(C, out c));
        Assert.Equal([3L, 4L], c);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ARowReachableThroughTwoKeysIsPatchedOnBothAndLosesOnlyTheKeyThatLostIt()
    {
        var model = new ResidentOuterModel(IdPolicy.KeepOnUpdate);
        model.Write([("o0", [A, B]), ("o1", [A]), ("o2", [B])], []);
        using ResidentCacheRig rig = RigOf(model, null, A, B, C);
        long entry = model.RowOf("o0").Entry;
        long adds = rig.Cache.GetStats().PatchAdds, removes = rig.Cache.GetStats().PatchRemoves;

        // same id, keys {A,B} -> {A,C}: only B loses the entry and only C gets it, the list of A is not touched
        Assert.Equal(ResidentPatchOutcome.Applied, rig.Refresh([("o0", [A, C])], []));
        Assert.Equal(model.RowOf("o0").Entry, entry);
        ResidentCacheStats stats = rig.Cache.GetStats();
        Assert.Equal(1, stats.PatchAdds - adds);
        Assert.Equal(1, stats.PatchRemoves - removes);
        Assert.Contains(entry, Ids(rig, A));
        Assert.DoesNotContain(entry, Ids(rig, B));
        Assert.Contains(entry, Ids(rig, C));
        rig.AssertInvariant("kept id, one key lost");

        // the row is still reachable through A: a union of A and B contains it, and it is no longer reachable through two of them
        Assert.Contains(entry, rig.Union(model.Current, 1, [A, B], out ResidentUnion union));
        Assert.False(union.EntryInSeveralKeys);
        rig.Union(model.Current, 1, [A, C], out union);
        Assert.True(union.EntryInSeveralKeys);

        // deleted: gone from every key it had
        Assert.Equal(ResidentPatchOutcome.Applied, rig.Refresh([], ["o0"]));
        foreach (long key in new[] { A, B, C })
            Assert.DoesNotContain(entry, Ids(rig, key));

        rig.AssertInvariant("deleted row");
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(IdPolicy.KeepOnUpdate)]
    [InlineData(IdPolicy.AlwaysNew)]
    [InlineData(IdPolicy.ReuseFreed)]
    public void TheSameIdWithOtherKeysAndAnOldIdRemovedWithANewOneAddedForTheSameDocument(IdPolicy policy)
    {
        var model = new ResidentOuterModel(policy);
        model.Write([("o0", [A]), ("o1", [A, B]), ("o2", [B])], []);
        using ResidentCacheRig rig = RigOf(model, null, A, B, C, Hub);
        long old = model.RowOf("o0").Entry;

        Assert.Equal(ResidentPatchOutcome.Applied, rig.Refresh([("o0", [B])], []));
        long now = model.RowOf("o0").Entry;
        if (policy == IdPolicy.AlwaysNew)
            Assert.NotEqual(old, now);
        else
            Assert.Equal(old, now); // kept, or the freed id comes back

        Assert.Contains(now, Ids(rig, B));
        if (now != old)
            Assert.DoesNotContain(old, Ids(rig, A));

        Assert.DoesNotContain(now, Ids(rig, A));
        rig.AssertInvariant("o0 moved from A to B");

        // to the hub and back
        Assert.Equal(ResidentPatchOutcome.Applied, rig.Refresh([("o0", [Hub, B])], []));
        rig.AssertInvariant("o0 to the hub");
        Assert.Equal(ResidentPatchOutcome.Applied, rig.Refresh([("o0", [C])], []));
        rig.AssertInvariant("o0 from the hub");
        Assert.Equal(ResidentPatchOutcome.Applied, rig.Refresh([("o0", [])], []));
        rig.AssertInvariant("o0 without a key");
        Assert.Equal(ResidentPatchOutcome.Applied, rig.Refresh([("o0", [Hub])], []));
        rig.AssertInvariant("o0 back");
        rig.AssertUnion(model.Current, 1, [A, B, C, Hub], "all");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AnEntryIdThatIsReusedByAnotherDocumentInTheSameBatchStaysInTheList()
    {
        var model = new ResidentOuterModel(IdPolicy.ReuseFreed);
        model.Write([("o0", [A]), ("o1", [A]), ("o2", [B])], []);
        using ResidentCacheRig rig = RigOf(model, null, A, B);
        long freed = model.RowOf("o0").Entry;

        // o0 is deleted and o3 is inserted in the same batch and gets the id of o0 with the same key
        Assert.Equal(ResidentPatchOutcome.Applied, rig.Refresh([("o3", [A])], ["o0"]));
        Assert.Equal(freed, model.RowOf("o3").Entry);
        Assert.Contains(freed, Ids(rig, A));
        rig.AssertInvariant("id reused");

        // whatever the order in which the ops were recorded, the removals go first
        long entry = model.RowOf("o3").Entry;
        using var patch = new ResidentPatch();
        patch.AddRow(entry, [A]);
        patch.RemoveRow(entry, [A]);
        Assert.Equal(ResidentPatchOutcome.Applied, rig.Apply(rig.Cache.Epoch, patch));
        Assert.Contains(entry, Ids(rig, A));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ARowThatChangesToAKeyWithoutAListLeavesNoTraceAndOneThatIsNotResidentIsIgnored()
    {
        var model = new ResidentOuterModel(IdPolicy.AlwaysNew);
        model.Write([("o0", [A]), ("o1", [B])], []);
        using ResidentCacheRig rig = RigOf(model, null, A); // only A is resident
        Assert.Equal(1, rig.Cache.ResidentKeys);
        Assert.Equal(ResidentPatchOutcome.Applied, rig.Refresh([("o1", [C]), ("o0", [B])], []));
        Assert.Equal(1, rig.Cache.ResidentKeys);
        Assert.Empty(Ids(rig, A));
        rig.AssertUnion(model.Current, 1, [A, B, C], "keys without a resident list");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheEpochIsNotKeptWhenTheCacheIsSwitchedOff()
    {
        var model = new ResidentOuterModel(IdPolicy.AlwaysNew);
        model.Write([("o0", [A])], []);
        using ResidentCacheRig rig = RigOf(model, new ResidentCacheOptions { BudgetBytes = 0 }, A);
        Assert.Equal(ResidentEpoch.None, rig.Cache.Epoch);
        rig.AssertUnion(model.Current, 1, [A], "off");
        Assert.Equal(0, rig.Cache.ResidentKeys);
        Assert.False(rig.Cache.ShouldEnable(10, 1));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void WarmUpStopsWhenTheEpochChangesAndWhenTheTokenIsCancelled()
    {
        var model = new ResidentOuterModel(IdPolicy.AlwaysNew);
        model.Write(Enumerable.Range(0, 20).Select(i => ("o" + i, new[] { KeyRef(i) })), []);
        long[] keys = Enumerable.Range(0, 20).Select(KeyRef).ToArray();
        using var rig = new ResidentCacheRig(model, new ResidentCacheOptions(), establish: false, warmUp: false) { AllKeys = keys };
        Snapshot snapshot = model.Current;
        ResidentEpoch epoch = rig.EpochOf(snapshot);
        Assert.True(rig.Cache.TryEstablish(epoch, snapshot.LastEntryId));

        var reads = new long[1];
        var source = new SnapshotSource(snapshot, reads);
        ResidentWarmUpResult result = rig.Cache.WarmUp(epoch, keys, ref source, CancellationToken.None);
        Assert.Equal(20, result.Loaded);
        Assert.False(result.Full || result.EpochChanged);
        Assert.Equal(20, rig.Cache.GetStats().WarmUpKeys);

        // already resident lists are not read again
        long before = reads[0];
        result = rig.Cache.WarmUp(epoch, keys, ref source, CancellationToken.None);
        Assert.Equal(0, result.Loaded);
        Assert.Equal(before, reads[0]);
        Assert.Equal(20, result.Skipped);

        rig.Cache.Invalidate();
        result = rig.Cache.WarmUp(epoch, keys, ref source, CancellationToken.None);
        Assert.True(result.EpochChanged);
        Assert.Equal(0, result.Loaded);

        Assert.True(rig.Cache.TryEstablish(epoch, snapshot.LastEntryId));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
        {
            var again = new SnapshotSource(snapshot, reads);
            rig.Cache.WarmUp(epoch, keys, ref again, cancelled.Token);
        });
    }
}
