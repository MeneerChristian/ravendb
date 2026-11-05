using System;

namespace Raven.Server.Documents.Indexes.Joins.Serving.Resident;

/// <summary>Which key lists that were read from the store are kept. The options combine: a list is kept only when every selected rule says yes.</summary>
[Flags]
public enum ResidentAdmission
{
    /// <summary>Every list read from the store is kept; the clock evicts the lists that were not used.</summary>
    Always = 0,

    /// <summary>The lists of an atom are kept only while the atom was read often in the last window of atom reads.</summary>
    AtomRate = 1,

    /// <summary>The lists of an atom are kept only when all of them fit into the free budget, and no list is kept by evicting another: the first lists that were read stay.</summary>
    FillHold = 2
}

public sealed class ResidentCacheOptions
{
    /// <summary>The budget of this cache in bytes of native memory: the allocation of every list plus <see cref="ResidentKeyListCache.ListOverheadBytes"/> per list. 0 or less switches the cache off.</summary>
    public long BudgetBytes { get; init; } = 256L * 1024 * 1024;

    public ResidentAdmission Admission { get; init; } = ResidentAdmission.Always;

    /// <summary>The window of <see cref="ResidentAdmission.AtomRate"/>: the number of atom reads after which the counts of the atoms age.</summary>
    public int AtomRateWindow { get; init; } = 64;

    /// <summary>An atom is hot when it was read at least this many times in the current and the previous window.</summary>
    public int AtomRateMinReads { get; init; } = 6;

    /// <summary>A patch of more documents than this is not applied: the cache is cleared and has no epoch until the next complete refresh establishes one. A refresh changes at most Join.MaxIdsPerCommit documents.</summary>
    public int MaxPatchDocuments { get; init; } = 100_000;
}
