using System;
using System.Collections.Generic;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>An index as the transaction of a query sees it.</summary>
internal sealed class JoinIndexState
{
    public Guid EnvironmentId { get; init; }

    /// <summary>Per collection: the last document etag indexed and the last tombstone etag processed.</summary>
    public IReadOnlyDictionary<string, (long Doc, long Tombstone)> Consumed { get; init; }
}
