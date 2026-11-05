using System;
using System.Collections.Generic;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>The resident cache a union may read: the cache, the scope its epoch is in (the outer environment and the file the key references belong to), and the transaction of the outer snapshot of the query.</summary>
internal readonly record struct JoinResidentRead(JoinResident Resident, Guid Scope, long OuterTransactionId, long AtomCode, IReadOnlyDictionary<string, (long Doc, long Tombstone)> Consumed)
{
    public ResidentKeyListCache Cache => Resident?.Cache;

    public bool IsUsable => Resident != null && OuterTransactionId > 0;
}
