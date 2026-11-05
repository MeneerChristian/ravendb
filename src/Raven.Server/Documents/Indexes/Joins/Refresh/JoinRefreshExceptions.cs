using System;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>The indexes hold something a join index cannot represent. The join index stops and queries use the normal join.</summary>
public sealed class JoinUnsupportedException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

/// <summary>The file cannot be brought to the state of the indexes by changes: it has to be built again.</summary>
public sealed class JoinRebuildRequiredException(JoinRebuildReason reason, string details = null)
    : Exception(details == null ? reason.ToString() : $"{reason}: {details}")
{
    public JoinRebuildReason Reason { get; } = reason;
}

public enum JoinRebuildReason
{
    Initial,
    EnvironmentChanged,
    CursorsWentBackwards,
    CollectionsChanged,
    TooManyUnindexedChanges,
    InnerChangesTooCostly,
    HashCollision,
    EmptyTerms,
    FileReset,
    Unsupported,
    Requested
}
