using System;

namespace Raven.Server.Documents.Indexes.Joins.Serving.Resident;

public sealed unsafe partial class ResidentKeyListCache
{
    /// <summary>
    /// Moves the epoch to <paramref name="next"/> without changing a list, when the cache is at <paramref name="expectedCurrent"/> and the lists are known to be the posting lists of the newer snapshot too
    /// (the caller proves that: the outer index committed without moving what it consumed). Unlike <see cref="ApplyPatch"/> a cache that is not at the expected epoch is left as it is. False when it did not move.
    /// </summary>
    public bool TryAdvanceEpoch(ResidentEpoch expectedCurrent, ResidentEpoch next, long outerLastEntryId)
    {
        if (next.IsNone)
            throw new ArgumentException("An epoch has an environment id", nameof(next));

        _lock.EnterWriteLock();
        try
        {
            if (_disposed || _epoch == null || _epoch.Value != expectedCurrent || outerLastEntryId > uint.MaxValue)
                return false;

            _epoch = new EpochBox(next);
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private static int EndOfKey(ReadOnlySpan<ResidentPatch.Op> ops, int start)
    {
        long key = ops[start].KeyRef;
        int end = start + 1;
        while (end < ops.Length && ops[end].KeyRef == key)
            end++;

        return end;
    }

    /// <summary>
    /// Applies the changes of one refresh, after its join index transaction committed, under one exclusive lock hold, and moves the epoch to <paramref name="next"/> (the outer snapshot of the refresh).
    /// The cache must be at <paramref name="expectedCurrent"/>, the epoch that the refresh started from: when it is not (cold, ahead of or behind the file, another environment), when the patch has
    /// more documents than <see cref="ResidentCacheOptions.MaxPatchDocuments"/>, or when the outer index has entry ids above 2^32, the cache is cleared and has no epoch; the caller establishes one
    /// again at its snapshot. Any exception clears the cache too and is rethrown.
    /// </summary>
    public ResidentPatchOutcome ApplyPatch(ResidentEpoch expectedCurrent, ResidentEpoch next, long outerLastEntryId, ResidentPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (next.IsNone)
            throw new ArgumentException("An epoch has an environment id", nameof(next));

        // the changes of a key are put together before the lock is taken: a key is found once and a long list is merged in one pass
        patch.Sort();

        _lock.EnterWriteLock();
        try
        {
            try
            {
                if (_epoch == null || _epoch.Value != expectedCurrent)
                {
                    DropEpochLocked();
                    return ResidentPatchOutcome.EpochMismatch;
                }

                if (outerLastEntryId > uint.MaxValue)
                {
                    _idTooLarge++;
                    DropEpochLocked();
                    return ResidentPatchOutcome.IdTooLarge;
                }

                if (patch.Documents > _options.MaxPatchDocuments)
                {
                    DropEpochLocked();
                    return ResidentPatchOutcome.TooLarge;
                }

                // removals first: an entry that one row loses and another row gets (an id that is reused) must end up in the list
                int n = 0;
                ReadOnlySpan<ResidentPatch.Op> removes = patch.Removes;
                for (int start = 0; start < removes.Length;)
                {
                    int end = EndOfKey(removes, start);
                    ReadOnlySpan<ResidentPatch.Op> group = removes.Slice(start, end - start);
                    for (int i = 0; i < group.Length; i++)
                        ForTestingPurposes?.BeforePatchOp?.Invoke(n++);

                    PatchRemoveGroup(group[0].KeyRef, group);
                    start = end;
                }

                ReadOnlySpan<ResidentPatch.Op> adds = patch.Adds;
                for (int start = 0; start < adds.Length;)
                {
                    int end = EndOfKey(adds, start);
                    ReadOnlySpan<ResidentPatch.Op> group = adds.Slice(start, end - start);
                    for (int i = 0; i < group.Length; i++)
                        ForTestingPurposes?.BeforePatchOp?.Invoke(n++);

                    PatchAddGroup(group[0].KeyRef, group);
                    start = end;
                }

                _epoch = new EpochBox(next);
                _patches++;
                return ResidentPatchOutcome.Applied;
            }
            catch
            {
                DropEpochLocked();
                throw;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}
