namespace Raven.Server.Documents.Indexes.Joins.Serving.Resident;

/// <summary>The counters of one cache, for the stats of the join index. Bytes are in the unit of the budget except <see cref="NativeBytes"/>, which is everything the cache allocated.</summary>
public readonly record struct ResidentCacheStats(
    long ResidentBytes,
    long NativeBytes,
    long PeakResidentBytes,
    int ResidentKeys,
    long Hits,
    long Misses,
    long EpochMisses,
    long Inserts,
    long Evictions,
    long Rejected,
    long NotAdmitted,
    long IdTooLarge,
    long PatchesApplied,
    long PatchAdds,
    long PatchRemoves,
    long Invalidations,
    long WarmUpKeys,
    long LowMemoryDrops);
