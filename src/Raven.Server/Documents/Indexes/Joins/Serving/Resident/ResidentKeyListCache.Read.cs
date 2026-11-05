using System;
using System.Buffers;
using System.Numerics;
using System.Threading;

namespace Raven.Server.Documents.Indexes.Joins.Serving.Resident;

// The reader protocol: the epoch is compared with the snapshot of the reader, the lists are read under the shared lock, the lists that are missing are read from the store of the SAME snapshot and offered to the
// cache. A reader of another snapshot reads the store and keeps nothing.
public sealed unsafe partial class ResidentKeyListCache
{
    /// <summary>The shared lock of a reader whose snapshot is the epoch. No pointer to a list leaves it. It cannot be copied: dispose it once.</summary>
    public ref struct ReadLease
    {
        private ResidentKeyListCache _cache;

        internal ReadLease(ResidentKeyListCache cache)
        {
            _cache = cache;
        }

        public readonly bool IsValid => _cache != null;

        /// <summary>ORs the list of the key into the words (entry id -> bit) and says whether an entry of it was already set. False when the key has no resident list. Ids beyond the words are ignored.</summary>
        public readonly bool TryOrInto(long keyRef, ulong* words, int wordCount, out bool entryInSeveralKeys) => _cache.OrIntoShared(keyRef, words, wordCount, out entryInSeveralKeys);

        /// <summary>The number of entries of the resident list, or -1 when the key is not resident.</summary>
        public readonly int CountOf(long keyRef) => _cache.CountOfShared(keyRef);

        public void Dispose()
        {
            ResidentKeyListCache cache = _cache;
            _cache = null;
            cache?.ExitShared();
        }
    }

    /// <summary>Takes the shared lock when the epoch is the snapshot (environment id, Voron transaction id) of the reader. An invalid lease means: read the store and keep nothing.</summary>
    public ReadLease TryBeginRead(Guid outerEnvironmentId, long outerTransactionId)
    {
        if (EpochIs(outerEnvironmentId, outerTransactionId) == false)
        {
            Interlocked.Increment(ref _epochMisses);
            return default;
        }

        _lock.EnterReadLock();
        if (EpochIs(outerEnvironmentId, outerTransactionId) == false)
        {
            _lock.ExitReadLock();
            Interlocked.Increment(ref _epochMisses);
            return default;
        }

        return new ReadLease(this);
    }

    private void ExitShared() => _lock.ExitReadLock();

    private bool OrIntoShared(long keyRef, ulong* words, int wordCount, out bool entryInSeveralKeys)
    {
        int slot = Find(keyRef);
        if (slot < 0)
        {
            Interlocked.Increment(ref _misses);
            entryInSeveralKeys = false;
            return false;
        }

        _slots[slot].Referenced = 1;
        Interlocked.Increment(ref _hits);
        entryInSeveralKeys = OrInto(slot, words, wordCount);
        return true;
    }

    private int CountOfShared(long keyRef)
    {
        int slot = Find(keyRef);
        return slot < 0 ? -1 : _slots[slot].Count;
    }

    private bool OrInto(int slot, ulong* words, int wordCount)
    {
        ref Slot s = ref _slots[slot];
        bool twice = false;
        if (s.Form == 0)
        {
            var ids = (uint*)s.Data;
            int n = s.Count;
            for (int i = 0; i < n; i++)
            {
                uint id = ids[i];
                int w = (int)(id >> 6);
                if (w >= wordCount)
                    continue;

                ulong mask = 1UL << (int)(id & 63);
                ulong old = words[w];
                twice |= (old & mask) != 0;
                words[w] = old | mask;
            }
        }
        else
        {
            var source = (ulong*)s.Data;
            int n = Math.Min(s.Capacity, wordCount);
            for (int w = 0; w < n; w++)
            {
                ulong old = words[w];
                twice |= (old & source[w]) != 0;
                words[w] = old | source[w];
            }
        }

        return twice;
    }

    private static bool OrIds(ReadOnlySpan<long> ids, ulong* words, int wordCount)
    {
        bool twice = false;
        foreach (long id in ids)
        {
            long w = id >> 6;
            if (w >= wordCount)
                continue;

            ulong mask = 1UL << (int)(id & 63);
            ulong old = words[w];
            twice |= (old & mask) != 0;
            words[w] = old | mask;
        }

        return twice;
    }

    /// <summary>
    /// Offers a list that was read from the store of the snapshot (environment id, transaction id) of the caller. Kept only when that snapshot is the epoch and the budget allows it; with
    /// <see cref="ResidentAdmission.FillHold"/> no other list is evicted for it.
    /// </summary>
    public ResidentInsertResult TryInsert(Guid outerEnvironmentId, long outerTransactionId, long keyRef, ReadOnlySpan<long> ids) =>
        InsertShared(outerEnvironmentId, outerTransactionId, keyRef, ids, _options.Admission.HasFlag(ResidentAdmission.FillHold));

    private ResidentInsertResult InsertShared(Guid environmentId, long transactionId, long keyRef, ReadOnlySpan<long> ids, bool noEvict)
    {
        _lock.EnterWriteLock();
        Interlocked.Increment(ref _insertLockEntries);
        try
        {
            if (EpochIs(environmentId, transactionId) == false)
            {
                Interlocked.Increment(ref _epochMisses);
                return ResidentInsertResult.EpochMismatch;
            }

            return InsertLocked(keyRef, ids, noEvict);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>The lists of the keys, one after the other in <paramref name="ids"/>, <paramref name="ends"/> being where each of them ends, inserted under one hold of the write lock. Stops at the first epoch mismatch, which is the same for the rest.</summary>
    private int InsertBatchShared(Guid environmentId, long transactionId, ReadOnlySpan<long> keyRefs, ReadOnlySpan<int> ends, ReadOnlySpan<long> ids, bool noEvict)
    {
        int inserted = 0;
        _lock.EnterWriteLock();
        Interlocked.Increment(ref _insertLockEntries);
        try
        {
            if (EpochIs(environmentId, transactionId) == false)
            {
                Interlocked.Increment(ref _epochMisses);
                return 0;
            }

            int start = 0;
            for (int i = 0; i < keyRefs.Length; i++)
            {
                if (InsertLocked(keyRefs[i], ids.Slice(start, ends[i] - start), noEvict) == ResidentInsertResult.Inserted)
                    inserted++;

                start = ends[i];
            }

            return inserted;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    // the lists that a union reads from the store are inserted this many at a time, and not more ids than a pooled buffer holds, so the write lock is taken once for them and not once for each
    private const int InsertBatchKeys = 64;
    private const int InsertBatchIds = 32 * 1024;

    /// <summary>
    /// ORs the lists of the keys of an atom into the words: the resident lists when the reader's snapshot is the epoch, the store (<paramref name="source"/>, which must read the reader's snapshot) for the
    /// rest. The lists that came from the store are kept when the atom is admitted. With another epoch every list comes from the store and nothing is kept.
    /// </summary>
    public ResidentUnion UnionKeys<TSource>(Guid outerEnvironmentId, long outerTransactionId, long atomCode, ReadOnlySpan<long> keyRefs, ref TSource source, ulong* words, int wordCount)
        where TSource : IResidentListSource
    {
        var result = new ResidentUnion();
        _rates?.RecordRead(atomCode);

        int missingWords = (keyRefs.Length + 63) / 64;
        ulong[] rented = missingWords > 64 ? ArrayPool<ulong>.Shared.Rent(missingWords) : null;
        Span<ulong> missing = rented != null ? rented.AsSpan(0, missingWords) : stackalloc ulong[missingWords];
        long[] batchIds = null;
        try
        {
            missing.Clear();
            int missingCount = 0;
            using (ReadLease lease = TryBeginRead(outerEnvironmentId, outerTransactionId))
            {
                result.EpochHit = lease.IsValid;
                for (int i = 0; i < keyRefs.Length; i++)
                {
                    bool hit = false, twice = false;
                    if (lease.IsValid)
                        hit = lease.TryOrInto(keyRefs[i], words, wordCount, out twice);

                    if (hit)
                    {
                        result.KeysFromCache++;
                        result.EntryInSeveralKeys |= twice;
                        continue;
                    }

                    missing[i >> 6] |= 1UL << (i & 63);
                    missingCount++;
                }
            }

            if (missingCount == 0)
                return result;

            bool admit = result.EpochHit && AdmitAtom(atomCode, keyRefs, missing, ref source);
            bool noEvict = _options.Admission.HasFlag(ResidentAdmission.FillHold);
            Span<long> pendingKeys = stackalloc long[InsertBatchKeys];
            Span<int> pendingEnds = stackalloc int[InsertBatchKeys];
            int pending = 0, pendingIds = 0;
            for (int i = 0; i < keyRefs.Length; i++)
            {
                if ((missing[i >> 6] & (1UL << (i & 63))) == 0)
                    continue;

                ReadOnlySpan<long> ids = source.Read(keyRefs[i]);
                result.EntryInSeveralKeys |= OrIds(ids, words, wordCount);
                result.KeysFromStore++;
                if (admit == false)
                    continue;

                if (ids.Length > InsertBatchIds)
                {
                    // too large for the buffer: the lists that came before it first, then this one alone
                    result.KeysInserted += FlushPending(outerEnvironmentId, outerTransactionId, pendingKeys, pendingEnds, batchIds, ref pending, ref pendingIds, noEvict);
                    if (InsertShared(outerEnvironmentId, outerTransactionId, keyRefs[i], ids, noEvict) == ResidentInsertResult.Inserted)
                        result.KeysInserted++;

                    continue;
                }

                if (pending == InsertBatchKeys || pendingIds + ids.Length > InsertBatchIds)
                    result.KeysInserted += FlushPending(outerEnvironmentId, outerTransactionId, pendingKeys, pendingEnds, batchIds, ref pending, ref pendingIds, noEvict);

                batchIds ??= ArrayPool<long>.Shared.Rent(InsertBatchIds);
                ids.CopyTo(batchIds.AsSpan(pendingIds));
                pendingIds += ids.Length;
                pendingKeys[pending] = keyRefs[i];
                pendingEnds[pending] = pendingIds;
                pending++;
            }

            result.KeysInserted += FlushPending(outerEnvironmentId, outerTransactionId, pendingKeys, pendingEnds, batchIds, ref pending, ref pendingIds, noEvict);
            return result;
        }
        finally
        {
            if (rented != null)
                ArrayPool<ulong>.Shared.Return(rented);

            if (batchIds != null)
                ArrayPool<long>.Shared.Return(batchIds);
        }
    }

    private int FlushPending(Guid environmentId, long transactionId, ReadOnlySpan<long> keys, ReadOnlySpan<int> ends, long[] ids, ref int pending, ref int pendingIds, bool noEvict)
    {
        if (pending == 0)
            return 0;

        int inserted = InsertBatchShared(environmentId, transactionId, keys.Slice(0, pending), ends.Slice(0, pending), ids.AsSpan(0, pendingIds), noEvict);
        pending = 0;
        pendingIds = 0;
        return inserted;
    }

    private bool AdmitAtom<TSource>(long atomCode, ReadOnlySpan<long> keyRefs, ReadOnlySpan<ulong> missing, ref TSource source) where TSource : IResidentListSource
    {
        ResidentAdmission admission = _options.Admission;
        if (_budget <= 0)
            return false;

        if (admission.HasFlag(ResidentAdmission.AtomRate) && _rates.IsHot(atomCode) == false)
        {
            Interlocked.Increment(ref _notAdmitted);
            return false;
        }

        if (admission.HasFlag(ResidentAdmission.FillHold))
        {
            long need = 0;
            for (int i = 0; i < keyRefs.Length; i++)
            {
                if ((missing[i >> 6] & (1UL << (i & 63))) != 0)
                    need += EstimateListBytes(source.CountOf(keyRefs[i]));
            }

            long free = _budget - ResidentBytes;
            if (_registry != null)
                free = Math.Min(free, _registry.FreeBytes);

            if (need > free)
            {
                Interlocked.Increment(ref _notAdmitted);
                return false;
            }
        }

        return true;
    }

    private const int WarmUpSlice = 256;

    private bool IsResident(long keyRef)
    {
        _lock.EnterReadLock();
        try
        {
            return Find(keyRef) >= 0;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private static long EstimateListBytes(long idCount) => (idCount + idCount / 8 + 2) * sizeof(uint) + ListOverheadBytes;

    /// <summary>
    /// Loads the lists of the keys (in the given order) from the store of the snapshot of the epoch until the budget is full, without evicting anything: the first lists stay. Runs in slices and observes the
    /// token between them. The epoch must be established first.
    /// </summary>
    public ResidentWarmUpResult WarmUp<TSource>(ResidentEpoch epoch, ReadOnlySpan<long> keyRefs, ref TSource source, CancellationToken token) where TSource : IResidentListSource
    {
        var result = new ResidentWarmUpResult();
        for (int i = 0; i < keyRefs.Length; i++)
        {
            if (i % WarmUpSlice == 0)
                token.ThrowIfCancellationRequested();

            if (EpochIs(epoch.EnvironmentId, epoch.TransactionId) == false)
            {
                result.EpochChanged = true;
                break;
            }

            if (IsResident(keyRefs[i]))
            {
                result.Skipped++;
                continue;
            }

            long estimate = EstimateListBytes(source.CountOf(keyRefs[i]));
            long free = _budget - ResidentBytes;
            if (_registry != null)
                free = Math.Min(free, _registry.FreeBytes);

            if (estimate > _budget)
            {
                result.Skipped++;
                continue;
            }

            if (estimate > free)
            {
                result.Full = true;
                break;
            }

            ResidentInsertResult inserted = InsertShared(epoch.EnvironmentId, epoch.TransactionId, keyRefs[i], source.Read(keyRefs[i]), noEvict: true);
            switch (inserted)
            {
                case ResidentInsertResult.Inserted:
                    result.Loaded++;
                    break;
                case ResidentInsertResult.EpochMismatch:
                    result.EpochChanged = true;
                    break;
                case ResidentInsertResult.NoRoom:
                case ResidentInsertResult.Disabled:
                    result.Full = true;
                    break;
                default:
                    result.Skipped++;
                    break;
            }

            if (result.EpochChanged || result.Full)
                break;
        }

        Interlocked.Add(ref _warmUpKeys, result.Loaded);
        return result;
    }
}
