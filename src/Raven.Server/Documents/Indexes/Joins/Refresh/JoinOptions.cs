using System;
using System.Globalization;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config;
using Raven.Server.Config.Categories;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
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

    /// <summary>An AND or a NOT in the sub-query of a join on a field of an Indirect join index checks the keys with several inner entries against the inner index; a query that may have more such keys than this is read by the server.</summary>
    public long MaxResidualKeys { get; set; } = 20_000;

    /// <summary>
    /// The storage mode of the join indexes whose definition names none. Auto leaves the choice to the build: Indirect when the outer entries share their keys (see <see cref="AutoIndirectMinRowsPerKey"/>), else Expanded.
    /// </summary>
    public JoinStorageMode StorageModeDefault { get; set; } = JoinStorageMode.Auto;

    /// <summary>
    /// The outer entries per key from which a join index whose mode is chosen at the build is Indirect.
    /// </summary>
    public int AutoIndirectMinRowsPerKey { get; set; } = 30;

    /// <summary>The memory that must stay available after the resident cache of a join index that is built Indirect by the choice at the build (the floor of the auto join indexes).</summary>
    public long AutoModeMinimumAvailableBytes { get; set; }

    /// <summary>The bytes of the resident cache per key and per outer entry.</summary>
    public const double ResidentBytesPerKey = 69;

    public const double ResidentBytesPerRow = 4.5;

    /// <summary>What the choice of the mode at the build looks at.</summary>
    public readonly record struct ModeFacts(long OuterEntries, long DistinctKeys, long AvailableBytes, long CacheFreeBytes, bool LowMemory);

    /// <summary>What the choice of the mode reads from the process: the bytes that the budget of the resident caches has left and whether the server is in low memory.</summary>
    public readonly record struct ResidentInputs(long CacheFreeBytes, bool LowMemory);

    /// <summary>The bytes that the resident cache of an Indirect join index takes for the outer entries and the keys.</summary>
    public static long ResidentEstimateOf(long outerEntries, long distinctKeys) => (long)(distinctKeys * ResidentBytesPerKey + outerEntries * ResidentBytesPerRow);

    private static bool IndirectPossible(JoinDefinition definition) => definition.JoinsOnOuterId == false;

    /// <summary>The definition leaves the mode to the server and the server leaves it to the build: <see cref="ResolveBuildMode"/> decides when the file is built.</summary>
    public bool ChoosesModeAtBuild(JoinDefinition definition)
    {
        return definition.StorageMode == JoinStorageMode.Auto && StorageModeDefault == JoinStorageMode.Auto && IndirectEnabled && IndirectPossible(definition);
    }

    /// <summary>
    /// The mode of the file that is built now. Indirect when the entries have at least <see cref="AutoIndirectMinRowsPerKey"/> rows per key, the resident cache that it needs fits what the budget of the caches has left,
    /// and the machine is not in low memory and keeps its floor of available memory after the cache; else Expanded. The reason says which.
    /// </summary>
    public JoinStorageMode ResolveBuildMode(JoinDefinition definition, in ModeFacts facts, out string reason)
    {
        if (ChoosesModeAtBuild(definition) == false)
        {
            JoinStorageMode mode = ResolveMode(definition);
            reason = $"the mode is {mode} by the definition and the settings";
            return mode;
        }

        if (facts.DistinctKeys <= 0 || facts.OuterEntries <= 0)
        {
            reason = "the outer index has no keys to count rows per key";
            return JoinStorageMode.Expanded;
        }

        double rowsPerKey = facts.OuterEntries / (double)facts.DistinctKeys;
        if (rowsPerKey < AutoIndirectMinRowsPerKey)
        {
            reason = string.Create(CultureInfo.InvariantCulture, $"{rowsPerKey:F1} rows per key is less than {AutoIndirectMinRowsPerKey}");
            return JoinStorageMode.Expanded;
        }

        long estimate = ResidentEstimateOf(facts.OuterEntries, facts.DistinctKeys);
        long cacheLeft = Math.Max(0, Math.Min(ResidentCacheMaxBytes, facts.CacheFreeBytes));
        if (ResidentCacheMaxBytes <= 0 || estimate > cacheLeft)
        {
            reason = $"the resident cache needs {estimate} bytes and the budget has {cacheLeft} left";
            return JoinStorageMode.Expanded;
        }

        if (facts.LowMemory)
        {
            reason = "the server is in low memory";
            return JoinStorageMode.Expanded;
        }

        if (facts.AvailableBytes - estimate < AutoModeMinimumAvailableBytes)
        {
            reason = $"{facts.AvailableBytes} bytes are available, the cache needs {estimate} and {AutoModeMinimumAvailableBytes} must stay";
            return JoinStorageMode.Expanded;
        }

        reason = string.Create(CultureInfo.InvariantCulture, $"{rowsPerKey:F1} rows per key (at least {AutoIndirectMinRowsPerKey}), the resident cache needs {estimate} bytes");
        return JoinStorageMode.Indirect;
    }

    /// <summary>Whether the storage mode Indirect is allowed, in a definition and as the default.</summary>
    public bool IndirectEnabled { get; set; }

    /// <summary>The bytes of native memory that the resident cache of one Indirect join index may hold. 0 or less: the join index has no cache.</summary>
    public long ResidentCacheMaxBytes { get; set; } = ResidentCacheRegistry.DefaultBudgetBytes;

    /// <summary>The bytes of the resident lists of a field join per entry of the outer index that has a key, for the memory gate of the auto join indexes.</summary>
    public const double ResidentBytesPerOuterEntryOfAFieldJoin = 4.9;

    public const double ResidentBytesPerOuterEntryOfAnIdJoin = 11.6;

    /// <summary>The bytes that the resident cache of a join index that is built now is expected to take: nothing when a join index of this definition is not Indirect or the cache is off, else the estimate for the entries of the outer index, at most what the process budget has left.</summary>
    public long ResidentEstimateBytes(JoinDefinition definition, long outerEntries, long processFreeBytes)
    {
        if (ResidentCacheMaxBytes <= 0 || ResolveMode(definition) != JoinStorageMode.Indirect)
            return 0;

        long estimate = (long)(outerEntries * (definition.InnerField != null ? ResidentBytesPerOuterEntryOfAFieldJoin : ResidentBytesPerOuterEntryOfAnIdJoin));
        return Math.Min(estimate, Math.Min(ResidentCacheMaxBytes, processFreeBytes));
    }

    /// <summary>The mode in which a join index of this definition starts: never <see cref="JoinStorageMode.Auto"/>. A join index whose mode is chosen at the build starts as Expanded, see <see cref="ChoosesModeAtBuild"/>.</summary>
    public JoinStorageMode ResolveMode(JoinDefinition definition)
    {
        if (definition.StorageMode != JoinStorageMode.Auto)
            return definition.StorageMode;

        return StorageModeDefault == JoinStorageMode.Indirect && IndirectEnabled && IndirectPossible(definition) ? JoinStorageMode.Indirect : JoinStorageMode.Expanded;
    }

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
        options.MaxResidualKeys = Math.Max(0, joinIndex.MaxResidualKeys);
        options.StorageModeDefault = joinIndex.StorageModeDefault;
        options.IndirectEnabled = joinIndex.IndirectEnabled;
        options.AutoIndirectMinRowsPerKey = Math.Max(1, joinIndex.AutoIndirectMinRowsPerKey);
        options.AutoModeMinimumAvailableBytes = Math.Max(0, joinIndex.AutoMinimumAvailableMemory.GetValue(Sparrow.SizeUnit.Bytes));
        options.ResidentCacheMaxBytes = Math.Max(0, joinIndex.ResidentCacheMaxMemory.GetValue(Sparrow.SizeUnit.Bytes));
        return options;
    }
}
