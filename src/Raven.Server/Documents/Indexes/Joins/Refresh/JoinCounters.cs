using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

public sealed class JoinCounters
{
    public long Refreshes;
    public long PartialRefreshes;
    public long NoOps;
    public long Commits;
    public long Builds;
    public long IdsProcessed;
    public long ListUpdates;
    public long ListFlushes;
    public long Errors;
    // harness: totals over all refreshes
    public long Flushes;
    public long QueuedOps;
    public long ThresholdFlushes;
    public long RefreshAllocatedBytes;
    public readonly ConcurrentDictionary<string, long> RebuildsByReason = new();

    public long LastRefreshTicks;
    public long LastRefreshDurationTicks;
    public long LastRefreshIds;
    public long LastListChangeEstimate;
    public long LastQueueLimit;
    public long TotalRefreshTicks;
    public long TotalBuildTicks;
    public long LastRefreshAllocatedBytes;
    public long LastBuildTicks;
    public long LastBuildDurationTicks;
    public long LastBuildAllocatedBytes;
    public long LastBuildDirectAtoms;

    /// <summary>The lists of atoms that refreshes changed by one update of the entries of all their keys, and the lists that were changed by queued changes.</summary>
    public long AtomListsUpdatedAtOnce;
    public long AtomListsQueued;

    /// <summary>The changes of the atom lists of an Indirect file: one for every pair of a key and an atom whose count went from or to zero.</summary>
    public long KeyListChanges;

    /// <summary>The updates of the list of the atoms of a key (one for the whole of what a refresh changes for the key) and the lookups of a key term in the dictionary of the keys that a refresh made.</summary>
    public long KeyAtomUpdates;
    public long KeyLookups;

    /// <summary>The lookups of the count of a pair of a key and an atom that refreshes made: one for every pair whose count changed, for the estimate and the write together.</summary>
    public long PairCountLookups;

    /// <summary>The reads of the terms of the key field of an outer entry that refreshes made.</summary>
    public long OuterKeyReads;

    /// <summary>The builds whose mode was chosen by the rule of the Auto mode, by the mode they got, and why the last one got it.</summary>
    public long AutoModeIndirect;
    public long AutoModeExpanded;
    public string LastAutoModeReason;

    /// <summary>The outer entries per key that the rule of the Auto mode saw at the last build that it chose the mode of; NaN when there was none.</summary>
    public double LastAutoRowsPerKey = double.NaN;
    public long LastBuildQueuedAtoms;
    public long LastBuildListFlushes;
    public long LastBuildListUpdates;
    public long BusyTicks;
    public long BusyCpuTicks;
    public string LastBreakdown;
    public string LastError;
    public long LastErrorTicks;

    public void Add(ref long counter, long value) => Interlocked.Add(ref counter, value);

    public void Rebuild(JoinRebuildReason reason) => RebuildsByReason.AddOrUpdate(reason.ToString(), 1, (_, count) => count + 1);

    public static DateTime? ToDate(long ticks) => ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
}
