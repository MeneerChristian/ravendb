using System;
using Raven.Server.Config;
using Raven.Server.Config.Categories;
using Raven.Server.Documents.Indexes.Joins.Storage;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>The settings of the refresh work. They are read from the configuration of the database (RavenConfiguration.Join, the keys Join.*).</summary>
public sealed class JoinOptions
{
    /// <summary>A refresh applies at most this many changed documents in one commit; with more changes the rest is applied by the next commits.</summary>
    public int MaxIdsPerCommit { get; set; } = 50_000;

    /// <summary>
    /// A refresh also reads the documents that were written after what an upstream index has consumed; they are not changes of its snapshot, and the next refresh reads them again.
    /// They have a limit of their own, so that a bulk load that is ahead of the index does not build the lists again and again.
    /// </summary>
    public int MaxUnindexedIdsPerCommit { get; set; } = 1_000_000;

    /// <summary>
    /// A change of the inner documents of a join on a field moves the outer entries of the keys it changes in and out of the lists, one change per list. When the changes that the refresh still has
    /// to make are more than this many for each outer entry that has a key, building the lists again is cheaper. Zero or less never builds them again for this.
    /// </summary>
    public int MaxInnerListChangesPerOuterEntry { get; set; } = 30;

    /// <summary>
    /// The most changes of lists that a refresh queues before it applies them (24 bytes each in the queue and about 100 more in the pages of the transaction; a quarter of the available memory at 128 bytes a change is the other limit, and 1,048,576 the least). A list that is changed by many small flushes is rewritten each time, so a refresh
    /// that makes many changes is cheaper with fewer flushes.
    /// </summary>
    public int MaxQueuedListChanges { get; set; } = 4 * 1024 * 1024;

    /// <summary>The same for the build of the lists from the two indexes.</summary>
    public int MaxQueuedListChangesInBuild { get; set; } = 1024 * 1024;

    /// <summary>The shortest queue of list changes of a refresh, whatever the available memory.</summary>
    public int MinQueuedListChanges { get; set; } = JoinWriter.DefaultFlushThreshold;

    /// <summary>
    /// The build and a refresh that moves the outer entries of keys into and out of atoms write the list of an atom with at least this many outer entries by one update, from the outer lists of the keys,
    /// instead of queueing the entries: a list that is changed by many flushes is rewritten by each. The smaller atoms are queued. Zero queues every atom.
    /// </summary>
    public int MinAtomEntriesForDirectWrite { get; set; } = 1;

    /// <summary>The terms of the lists are never removed. When this share of the terms of a field has no list the lists are built again.</summary>
    public float MaxEmptyTermRatio { get; set; } = 0.5f;

    public long MinTermsForEmptyRatio { get; set; } = 100_000;

    /// <summary>
    /// The batches of the two indexes that arrive within this time are brought to the lists by one refresh. A query that finds the join index behind does not wait for the end of the window:
    /// it starts the refresh. Zero refreshes after every batch. The index throttling of the definition (Indexing.Throttling.TimeIntervalInMs) takes precedence.
    /// </summary>
    public TimeSpan RefreshWindow { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// An AND or a NOT in the sub-query of a join on a field checks the outer entries whose key has several inner entries entry by entry. A query that may have more such outer entries than this
    /// (by the sizes of the lists) is read by the server, which does the same work for every entry.
    /// </summary>
    public long MaxResidualRows { get; set; } = 100_000;

    public static JoinOptions From(RavenConfiguration configuration)
    {
        var options = new JoinOptions();
        if (configuration == null)
            return options;

        JoinConfiguration joinIndex = configuration.Join;
        options.MaxIdsPerCommit = Math.Max(1, joinIndex.MaxIdsPerCommit);
        options.MaxUnindexedIdsPerCommit = Math.Max(1, joinIndex.MaxUnindexedIdsPerCommit);
        options.MaxInnerListChangesPerOuterEntry = joinIndex.MaxInnerListChangesPerOuterEntry;
        options.MaxQueuedListChanges = Math.Max(1024, joinIndex.MaxQueuedListChanges);
        options.MaxQueuedListChangesInBuild = Math.Max(1024, joinIndex.MaxQueuedListChangesInBuild);
        options.MinQueuedListChanges = Math.Max(1024, joinIndex.MinQueuedListChanges);
        options.MinAtomEntriesForDirectWrite = Math.Max(0, joinIndex.MinAtomEntriesForDirectWrite);
        options.MaxEmptyTermRatio = joinIndex.MaxEmptyTermRatio;
        options.MinTermsForEmptyRatio = joinIndex.MinTermsForEmptyRatio;
        options.MaxResidualRows = Math.Max(0, joinIndex.MaxResidualRows);
        options.RefreshWindow = joinIndex.RefreshWindow.AsTimeSpan;
        return options;
    }
}
