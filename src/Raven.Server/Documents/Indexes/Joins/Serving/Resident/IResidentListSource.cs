using System;

namespace Raven.Server.Documents.Indexes.Joins.Serving.Resident;

/// <summary>The store of the lists: the posting lists of the key terms in ONE outer snapshot, the snapshot of the reader (or of the warm-up) that passes the source.</summary>
public interface IResidentListSource
{
    /// <summary>The number of entries of the key's list, read from its header without reading the list. 0 when the key has no list.</summary>
    long CountOf(long keyRef);

    /// <summary>The entry ids of the key's list in ascending order. The span is valid until the next call.</summary>
    ReadOnlySpan<long> Read(long keyRef);
}

public enum ResidentInsertResult
{
    Inserted,
    AlreadyResident,

    /// <summary>The epoch of the cache is not the snapshot of the caller: nothing is kept.</summary>
    EpochMismatch,

    /// <summary>The list has an entry id above 2^32 that the uint form cannot hold.</summary>
    IdTooLarge,

    /// <summary>The list alone is larger than the budget.</summary>
    TooLarge,

    /// <summary>No room without evicting (fill-hold) or nothing left to evict.</summary>
    NoRoom,

    /// <summary>The server is short of memory, or the cache is switched off.</summary>
    Disabled
}

public enum ResidentPatchOutcome
{
    Applied,

    /// <summary>The cache was not at the epoch the patch starts from: it is cleared and has no epoch.</summary>
    EpochMismatch,

    /// <summary>Too many documents for one lock hold: cleared, no epoch.</summary>
    TooLarge,

    /// <summary>The outer index has entry ids above 2^32: cleared, no epoch, the store path serves.</summary>
    IdTooLarge
}

/// <summary>What a union of key lists read.</summary>
public struct ResidentUnion
{
    /// <summary>False when the epoch of the cache was not the reader's snapshot: every list came from the store and nothing was kept.</summary>
    public bool EpochHit;

    public int KeysFromCache;
    public int KeysFromStore;
    public int KeysInserted;

    /// <summary>Some entry is in the lists of two of the keys: a row that is reachable through several keys.</summary>
    public bool EntryInSeveralKeys;
}

public struct ResidentWarmUpResult
{
    public int Loaded;
    public int Skipped;

    /// <summary>The budget is full: the rest of the keys were not read.</summary>
    public bool Full;

    /// <summary>The epoch of the cache changed during the warm-up: it stopped.</summary>
    public bool EpochChanged;
}
