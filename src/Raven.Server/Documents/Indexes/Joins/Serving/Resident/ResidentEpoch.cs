using System;

namespace Raven.Server.Documents.Indexes.Joins.Serving.Resident;

/// <summary>
/// The outer snapshot that every resident list is valid for: the outer environment and the Voron transaction of the snapshot. <see cref="Generation"/> is a stamp of the caller (the serial of the join index
/// generation the lists were patched to): readers ignore it, <see cref="ResidentKeyListCache.ApplyPatch"/> compares it as part of the epoch the patch starts from.
/// </summary>
public readonly record struct ResidentEpoch(Guid EnvironmentId, long TransactionId, long Generation)
{
    public static ResidentEpoch None => default;

    public bool IsNone => EnvironmentId == Guid.Empty;

    public bool IsSnapshotOf(Guid environmentId, long transactionId) => IsNone == false && EnvironmentId == environmentId && TransactionId == transactionId;
}
