namespace Raven.Client.Documents.Indexes;

/// <summary>How the lists of a join index hold what a sub-query of the joined index matches.</summary>
public enum JoinStorageMode
{
    /// <summary>The server chooses when it builds the lists.</summary>
    Auto = 0,

    /// <summary>The lists hold the outer entries of the keys that match.</summary>
    Expanded = 1,

    /// <summary>The lists hold the keys that match; the outer entries of a key are read from the outer index when a query needs them. Not for a join on the id of the outer document.</summary>
    Indirect = 2
}
