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
