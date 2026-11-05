using System;
using System.Runtime.InteropServices;
using System.Threading;
using Sparrow.Utils;

namespace Raven.Server.Documents.Indexes.Joins.Serving.Resident;

/// <summary>
/// The resident lists of the outer entries of keys (key reference -> ascending entry ids) in native memory, an accelerator of the Indirect storage mode: a union of key lists is read from here instead of
/// from the posting lists of the outer index. Nothing in it is durable and nothing in it is trusted without proof:
/// <list type="bullet">
/// <item>Every list equals the posting list of its key in ONE outer snapshot, the <see cref="Epoch"/>. A reader uses the lists only when its own outer snapshot (environment id, Voron transaction id) is the
/// epoch; otherwise it reads the store and keeps nothing.</item>
/// <item>The epoch moves only by <see cref="ApplyPatch"/>, which the refresh calls after the join index transaction committed. A patch that does not start from the current epoch, one that is too large,
/// and any exception clear the cache and drop the epoch. <see cref="Invalidate"/> does the same for a partial refresh.</item>
/// <item>Readers share a lock, every change of the lists is exclusive, and no pointer to a list leaves the lock.</item>
/// <item>The lists are 32 bit: an entry id above 2^32 is refused (no epoch, no list, a resident list that would have to hold one is dropped), never an exception.</item>
/// </list>
/// A list is a sorted uint array, or a bitmap from entry 0 when that is smaller when the list is inserted. The budget counts the native allocation of every list plus <see cref="ListOverheadBytes"/> for
/// its slot and its share of the hash table; <see cref="NativeBytes"/> is everything that the cache allocated.
/// </summary>
public sealed unsafe partial class ResidentKeyListCache : IDisposable
{
    /// <summary>The slot (32 bytes) and the share of the hash table (at most 24 bytes while it grows) of a list.</summary>
    public const int ListOverheadBytes = 64;

    private const int InitialSlots = 16, InitialTable = 32;
    private const int BitmapMinIds = 32;

    private sealed class EpochBox(ResidentEpoch value)
    {
        public readonly ResidentEpoch Value = value;
    }

    internal sealed class TestHooks
    {
        public Action<int> BeforePatchOp;
    }

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly ResidentCacheOptions _options;
    private readonly long _budget;
    private readonly ResidentCacheRegistry _registry;
    private readonly ResidentAtomRates _rates;
    private volatile EpochBox _epoch;
    private bool _disposed;

    private long _bytes, _peakBytes, _dataBytes, _structBytes;
    private long _hits, _misses, _epochMisses, _inserts, _evictions, _rejected, _notAdmitted, _idTooLarge;
    private long _insertLockEntries;
    private long _patches, _patchAdds, _patchRemoves, _invalidations, _warmUpKeys, _lowMemoryDrops;

    internal TestHooks ForTestingPurposes;

    public ResidentKeyListCache(ResidentCacheOptions options, ResidentCacheRegistry registry = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _budget = options.BudgetBytes;
        _registry = registry;
        if (options.Admission.HasFlag(ResidentAdmission.AtomRate))
            _rates = new ResidentAtomRates(options.AtomRateWindow, options.AtomRateMinReads);

        InitStructures();
        registry?.Register(this);
    }

    /// <summary>The snapshot that the lists are valid for, or None. Readers compare their snapshot with it before they take the lock; it is checked again under the lock.</summary>
    public ResidentEpoch Epoch => _epoch?.Value ?? ResidentEpoch.None;

    public long BudgetBytes => _budget;

    /// <summary>The bytes in the unit of the budget: the allocation of all lists plus <see cref="ListOverheadBytes"/> per list. Never above <see cref="BudgetBytes"/> when a call returns.</summary>
    public long ResidentBytes => Volatile.Read(ref _bytes);

    /// <summary>Every byte that the cache allocated: the lists, the slot table, the hash table and the atom rates.</summary>
    public long NativeBytes => Volatile.Read(ref _dataBytes) + Volatile.Read(ref _structBytes) + (_rates?.NativeBytes ?? 0);

    public int ResidentKeys => Volatile.Read(ref _used);

    /// <summary>The bytes of the lists of a join index with that many entry ids in its lists and that many keys, for the decision to enable the cache at all.</summary>
    public static long EstimateBytes(long idCount, long keyCount) => idCount * sizeof(uint) * 9 / 8 + keyCount * (2 * sizeof(uint) + ListOverheadBytes);

    public bool ShouldEnable(long idCount, long keyCount) => _budget > 0 && EstimateBytes(idCount, keyCount) <= _budget;

    /// <summary>How often the write lock was taken to insert lists that a reader read from the store.</summary>
    internal long InsertLockEntries => Interlocked.Read(ref _insertLockEntries);

    public ResidentCacheStats GetStats() => new(
        ResidentBytes, NativeBytes, Volatile.Read(ref _peakBytes), ResidentKeys,
        Interlocked.Read(ref _hits), Interlocked.Read(ref _misses), Interlocked.Read(ref _epochMisses), Interlocked.Read(ref _inserts), Interlocked.Read(ref _evictions),
        Interlocked.Read(ref _rejected), Interlocked.Read(ref _notAdmitted), Interlocked.Read(ref _idTooLarge), Interlocked.Read(ref _patches), Interlocked.Read(ref _patchAdds),
        Interlocked.Read(ref _patchRemoves), Interlocked.Read(ref _invalidations), Interlocked.Read(ref _warmUpKeys), Interlocked.Read(ref _lowMemoryDrops));

    /// <summary>
    /// Sets the epoch of an empty cache (the lists are valid for the snapshot that the warm-up and the read-through use). Another epoch than the current one clears the lists first. False, with no epoch,
    /// when the cache is switched off or the outer index has entry ids that the 32 bit form cannot hold.
    /// </summary>
    public bool TryEstablish(ResidentEpoch epoch, long outerLastEntryId)
    {
        if (epoch.IsNone)
            throw new ArgumentException("An epoch has an environment id", nameof(epoch));

        _lock.EnterWriteLock();
        try
        {
            if (_budget <= 0)
            {
                DropEpochLocked();
                return false;
            }

            if (outerLastEntryId > uint.MaxValue)
            {
                Interlocked.Increment(ref _idTooLarge);
                DropEpochLocked();
                return false;
            }

            if (_epoch?.Value == epoch)
                return true;

            DropAllListsLocked();
            _epoch = new EpochBox(epoch);
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Clears the cache and drops the epoch: a partial refresh, a rollback that cannot be repaired, an unknown change of the outer index. The readers use the store until the next establish.</summary>
    public void Invalidate()
    {
        _lock.EnterWriteLock();
        try
        {
            DropEpochLocked();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Drops the lists and keeps the epoch (low memory): an empty cache is valid for its epoch.</summary>
    internal void DropAllLists()
    {
        _lock.EnterWriteLock();
        try
        {
            if (_disposed)
                return;

            if (_used > 0)
                Interlocked.Increment(ref _lowMemoryDrops);

            DropAllListsLocked();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void DropEpochLocked()
    {
        DropAllListsLocked();
        if (_epoch != null)
            Interlocked.Increment(ref _invalidations);

        _epoch = null;
    }

    private bool EpochIs(Guid environmentId, long transactionId)
    {
        EpochBox box = _epoch;
        return box != null && box.Value.IsSnapshotOf(environmentId, transactionId);
    }

    public void Dispose()
    {
        _lock.EnterWriteLock();
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            _epoch = null;
            DropAllListsLocked();
            FreeStructures();
            _rates?.Dispose();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        _registry?.Unregister(this);
        _lock.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Slot
    {
        public long KeyRef;
        public byte* Data;
        public int Count;
        public int Capacity; // ids (array form) or words (bitmap form)
        public int NextFree;
        public byte Form; // 0 sorted uint array, 1 bitmap
        public byte Referenced;
        public byte InUse;
    }
}
