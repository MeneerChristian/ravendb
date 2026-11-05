namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>Why a join was read by the server as usual instead of from the join index. The names are the keys of the fallback counters.</summary>
public static class JoinFallbacks
{
    public const string Disabled = "Disabled";
    public const string Building = "Building";
    public const string Unresolved = "IndexMissing";
    public const string Faulted = "Faulted";
    public const string SnapshotUnavailable = "SnapshotUnavailable";
    public const string EnvironmentChanged = "EnvironmentChanged";
    public const string BehindTimeout = "BehindTimeout";
    public const string Ahead = "Ahead";
    public const string Failed = "Failed";
    public const string KindNotServedPrefix = "KindNotServed:";
    public const string UndeclaredFieldPrefix = KindNotServedPrefix + "UndeclaredField:";

    /// <summary>A text of the sub-query that the analyzer of its field turns into several terms, or into none (a stop word): the lists hold one term for an entry.</summary>
    public static readonly string AnalyzerSplitsValue = KindNotServed("AnalyzerSplitsValue");

    public static string KindNotServed(string detail) => KindNotServedPrefix + detail;

    /// <summary>The score of the join is used and an outer entry is joined with several documents (a join on a field: holds several keys that have an inner entry in their group): the join adds the score of every one of them to the entry.</summary>
    public static readonly string ScoresMultiKey = KindNotServed("ScoresMultiKey");

    /// <summary>
    /// An AND or a NOT over the lists, and an outer entry is joined with several documents: the entry may be in the list of each atom through another document. For a join on a field it is the budget:
    /// more outer entries have to be checked against their group than <see cref="Refresh.JoinOptions.MaxResidualRows"/> allows.
    /// </summary>
    public static readonly string GroupResidual = KindNotServed("GroupResidual");

    /// <summary>The query is ordered by a field of the joined documents and an outer entry is joined with several of them: no value orders the entry.</summary>
    public static readonly string OrderByMultiKey = KindNotServed("OrderByMultiKey");

    /// <summary>The query is ordered by a field of the joined documents and the join index keeps keys, not outer entries: the page is read from the lists of the outer entries of an Expanded file.</summary>
    public static readonly string OrderByIndirect = KindNotServed("OrderByIndirect");
}
