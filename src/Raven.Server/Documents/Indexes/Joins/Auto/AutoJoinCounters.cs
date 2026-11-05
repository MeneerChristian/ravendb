using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Raven.Server.Documents.Indexes.Joins.Auto;

/// <summary>What the queries of a database asked of the auto join indexes, and what came of it. A skipped creation has no join index to count it, so these are the counters of the database.</summary>
public sealed class AutoJoinCounters
{
    public const string SkippedMemory = "AutoJoinSkippedMemory";
    public const string SkippedClusterVersion = "AutoJoinSkippedClusterVersion";
    public const string NotEligiblePrefix = "AutoJoinNotEligible:";

    private long _requested;
    private long _created;
    private long _failed;
    private long _skippedMemory;
    private long _skippedClusterVersion;
    private readonly ConcurrentDictionary<string, long> _notEligible = new();

    /// <summary>The creation of an auto join index was started by a query.</summary>
    public long Requested => Interlocked.Read(ref _requested);

    /// <summary>The auto join index was put into the database record and is a join index of this node.</summary>
    public long Created => Interlocked.Read(ref _created);

    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>No join index was built for a join that one could answer because the memory does not allow it.</summary>
    public long SkippedForMemory => Interlocked.Read(ref _skippedMemory);

    /// <summary>No join index was put because a node of the cluster does not know the command yet.</summary>
    public long SkippedForClusterVersion => Interlocked.Read(ref _skippedClusterVersion);

    /// <summary>The joins that no join index could answer, by the reason (a name of the fallbacks of the join index, or the rule that was not met).</summary>
    public IEnumerable<KeyValuePair<string, long>> NotEligible => _notEligible;

    internal void Request() => Interlocked.Increment(ref _requested);

    internal void Create() => Interlocked.Increment(ref _created);

    internal void Fail() => Interlocked.Increment(ref _failed);

    internal void SkipForMemory() => Interlocked.Increment(ref _skippedMemory);

    internal void SkipForClusterVersion() => Interlocked.Increment(ref _skippedClusterVersion);

    internal void NotEligibleFor(string reason) => _notEligible.AddOrUpdate(reason, 1, static (_, count) => count + 1);
}
