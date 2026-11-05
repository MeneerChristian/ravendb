namespace Raven.Client.Documents.Indexes;

/// <summary>How a field of the joined index is indexed, which decides how a sub-query can compare it.</summary>
public enum JoinFieldKind
{
    /// <summary>One term per entry, compared as indexed (default, exact or key indexing).</summary>
    Exact = 0,

    /// <summary>A numeric field.</summary>
    Long = 1,

    /// <summary>An analyzed field with several terms per entry.</summary>
    Text = 2
}
