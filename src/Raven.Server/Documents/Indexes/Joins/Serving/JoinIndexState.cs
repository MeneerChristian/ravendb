using System;
using System.Collections.Generic;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>An index as the transaction of a query sees it.</summary>
internal sealed class JoinIndexState
{
    public Guid EnvironmentId { get; init; }

    /// <summary>The Voron transaction of the snapshot the query reads: with the environment id it names the outer snapshot that a resident list is valid for. 0 when it is not known, which no cache serves.</summary>
    public long TransactionId { get; init; }

    /// <summary>Per collection: the last document etag indexed and the last tombstone etag processed.</summary>
    public IReadOnlyDictionary<string, (long Doc, long Tombstone)> Consumed { get; init; }
}
