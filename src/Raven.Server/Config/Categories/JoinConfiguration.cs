using System;
using System.ComponentModel;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config.Attributes;
using Raven.Server.Config.Settings;
using Sparrow;
using Sparrow.Server.LowMemory;

namespace Raven.Server.Config.Categories;

[ConfigurationCategory(ConfigurationCategoryType.Join)]
public sealed class JoinConfiguration : ConfigurationCategory
{
    /// <summary>The memory that a queued list change takes, with the pages of the lists it touches.</summary>
    internal const int BytesPerQueuedListChange = 128;

    private const int MaxDefaultMinQueuedListChanges = 1024 * 1024;

    public JoinConfiguration() : this(MemoryInformation.TotalPhysicalMemory)
    {
    }

    internal JoinConfiguration(Size totalPhysicalMemory)
    {
        // a quarter more than the limit that the server keeps free for itself (Memory.LowMemoryLimit is min(2 GB, total/10), so this is min(2.5 GB, total/8)): the gate refuses a build before the server's own low memory behavior would start.
        AutoMinimumAvailableMemory = Size.Min(new Size(2560, SizeUnit.Megabytes), totalPhysicalMemory / 8);

        // 1/128 of the machine, in changes of 128 bytes: 8 MiB (65,536 changes) at 1 GB, and the million changes that a refresh always queued from 16 GB on
        // the queue of a build is a sixteenth of the machine at most: 64 MiB (524,288 changes) at 1 GB, the million changes that a build always queued from 2 GB on
        MaxQueuedListChangesInBuild = (int)Math.Clamp(totalPhysicalMemory.GetValue(SizeUnit.Bytes) / 16 / BytesPerQueuedListChange, 1024, MaxDefaultMinQueuedListChanges);

        // what the resident caches may hold together: a sixteenth of the machine, at most 256 MB. The caches take what they hold, the budget only caps them
        ResidentCacheMaxMemory = Size.Min(new Size(256, SizeUnit.Megabytes), totalPhysicalMemory / 16);

        // the estimated memory of a build that an auto join index may have: half of the machine at most, and at most the 512 MB of before. Not less: the estimate starts at 192 MB (Join.AutoBuildMemoryBase), so a quarter would refuse every build below 1 GB
        AutoMaxBuildMemory = Size.Min(new Size(512, SizeUnit.Megabytes), totalPhysicalMemory / 2);

        long minQueuedBytes = Math.Min((long)MaxDefaultMinQueuedListChanges * BytesPerQueuedListChange, totalPhysicalMemory.GetValue(SizeUnit.Bytes) / 128);
        MinQueuedListChanges = (int)Math.Max(1024, minQueuedBytes / BytesPerQueuedListChange);
    }

    [Description("A refresh of a join index applies at most this many changed documents in one commit. With more changes it commits what it applied and goes on in the next commit.")]
    [DefaultValue(50_000)]
    [MinValue(1)]
    [ConfigurationEntry("Join.MaxIdsPerCommit", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public int MaxIdsPerCommit { get; set; }

    [Description("A refresh also reads the documents that were written after what an upstream index has consumed. They have a limit of their own, " +
                 "so that a bulk load that is ahead of the index does not build the lists again and again.")]
    [DefaultValue(1_000_000)]
    [MinValue(1)]
    [ConfigurationEntry("Join.MaxUnindexedIdsPerCommit", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public int MaxUnindexedIdsPerCommit { get; set; }

    [Description("A change of the inner documents of a join on a field moves the outer entries of the keys it changes in and out of the lists. When the refresh still has to make more such changes " +
                 "than this many for each outer entry that has a key, the lists are built again, which is cheaper. Zero turns it off.")]
    [DefaultValue(30)]
    [MinValue(0)]
    [ConfigurationEntry("Join.MaxInnerListChangesPerOuterEntry", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public int MaxInnerListChangesPerOuterEntry { get; set; }

    [Description("A refresh queues the changes of the lists and applies them when this many are queued: the most it ever queues. A change takes about 128 bytes of memory; the queue is also limited to a quarter of the available memory and is never shorter than 262,144 changes (32 MiB; a shorter queue flushes more often) while at least 64 MiB are available, and under that it is half of the available memory in changes, never fewer than 1,024. A list that is changed by many small flushes is rewritten each time.")]
    [DefaultValue(4_194_304)]
    [MinValue(1024)]
    [ConfigurationEntry("Join.MaxQueuedListChanges", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public int MaxQueuedListChanges { get; set; }

    [Description("The queue of list changes that a refresh uses when the available memory is not known; when it is known, the queue is a quarter of it (never shorter than 262,144 changes while at least 64 MiB are available, see Join.MaxQueuedListChanges) whatever the memory of the machine is. The default is 1,048,576 changes (128 MiB) from 16 GB of memory on, and 1/128 of the memory of the machine (or of its container limit) below that: 65,536 changes (8 MiB) at 1 GB. " +
                 "A shorter queue applies the changes to the lists more often.")]
    [DefaultValue(DefaultValueSetInConstructor)]
    [MinValue(1024)]
    [ConfigurationEntry("Join.MinQueuedListChanges", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public int MinQueuedListChanges { get; set; }

    [Description("The same for the build of the lists from the two indexes: the changes of the lists that it queues in native memory before it applies them. The default is 1,048,576 changes (128 MiB) from 2 GB of memory on and a sixteenth of the memory of the machine (or of its container limit) below that; the queue is also limited to a quarter of the available memory.")]
    [DefaultValue(DefaultValueSetInConstructor)]
    [MinValue(1024)]
    [ConfigurationEntry("Join.MaxQueuedListChangesInBuild", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public int MaxQueuedListChangesInBuild { get; set; }

    [Description("The build of the lists, and a refresh that moves the outer entries of keys into and out of atoms (a term of a declared field, a grouping, or any), write the list of an atom that has at least this many " +
                 "outer entries by one update, from the entries of the keys, instead of queueing every entry for it. The smaller atoms are queued. Zero queues every atom.")]
    [DefaultValue(1)]
    [MinValue(0)]
    [ConfigurationEntry("Join.MinAtomEntriesForDirectWrite", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public int MinAtomEntriesForDirectWrite { get; set; }

    [Description("The terms of the lists are never removed. When this share of the terms of a field has no list, the lists are built again.")]
    [DefaultValue(0.5f)]
    [ConfigurationEntry("Join.MaxEmptyTermRatio", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public float MaxEmptyTermRatio { get; set; }

    [Description("The empty term ratio is checked only for a field with at least this many terms.")]
    [DefaultValue(100_000L)]
    [ConfigurationEntry("Join.MinTermsForEmptyRatio", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public long MinTermsForEmptyRatio { get; set; }

    [Description("The batches of the two indexes that arrive within this time (in milliseconds) are brought to the lists by one refresh. " +
                 "A query that finds the join index behind does not wait for the end of the window. Zero refreshes after every batch. " +
                 "The index throttling of the definition (Indexing.Throttling.TimeIntervalInMs) takes precedence.")]
    [DefaultValue(0)]
    [TimeUnit(TimeUnit.Milliseconds)]
    [ConfigurationEntry("Join.RefreshWindowInMs", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public TimeSetting RefreshWindow { get; set; }

    [Description("An AND or a NOT in the sub-query of a join on a field checks the outer entries whose key has several inner entries one by one. " +
                 "A query that may have more such outer entries than this is read by the server instead.")]
    [DefaultValue(100_000L)]
    [ConfigurationEntry("Join.MaxResidualRows", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public long MaxResidualRows { get; set; }

    [Description("An AND or a NOT in the sub-query of a join on a field of a join index in the storage mode Indirect checks the keys whose group has several inner entries against the inner index. " +
                 "A query that may have more such keys than this is read by the server instead.")]
    [DefaultValue(20_000L)]
    [ConfigurationEntry("Join.MaxResidualKeys", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public long MaxResidualKeys { get; set; }

    [Description("How long a query waits (in milliseconds) for the join index to catch up with the indexes before it reads the join as usual.")]
    [DefaultValue(100)]
    [TimeUnit(TimeUnit.Milliseconds)]
    [ConfigurationEntry("Join.QueryWaitInMs", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public TimeSetting QueryWait { get; set; }

    [Description("The same wait for a query that asked to wait for non stale results.")]
    [DefaultValue(1000)]
    [TimeUnit(TimeUnit.Milliseconds)]
    [ConfigurationEntry("Join.QueryWaitForNonStaleInMs", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public TimeSetting QueryWaitForNonStale { get; set; }

    [Description("A JOIN that a join index could answer creates an auto join index (an auto index that keeps the join up to date), which later queries are answered from. " +
                 "The query that creates it is read as usual and never waits for it, unless it asked to wait for non stale results. " +
                 "It is off by default: only join indexes that are put explicitly serve JOINs, because an auto join index builds a structure of its own from the first query that could use it.")]
    [DefaultValue(false)]
    [ConfigurationEntry("Join.AutoEnabled", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public bool AutoEnabled { get; set; }

    [Description("An auto join index is not built while less memory than this (in megabytes) would be available to the machine after the build (the available memory minus the estimated memory of the build). " +
                 "The default is 1.25 times the low memory limit of the server (Memory.LowMemoryLimitInMb, by default min(2 GB, a tenth of the memory)): an eighth of the memory of the machine (or of its container limit), at most 2.5 GB. In a container the available memory counts only what the process holds (the page cache is not counted).")]
    [DefaultValue(DefaultValueSetInConstructor)]
    [SizeUnit(SizeUnit.Megabytes)]
    [ConfigurationEntry("Join.AutoMinimumAvailableMemoryInMb", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public Size AutoMinimumAvailableMemory { get; set; }

    [Description("An auto join index is not built when the estimated memory of its build (in megabytes) is more than this. The default is 512 MB, or half of the memory of the machine (or of its container limit) when that is less.")]
    [DefaultValue(DefaultValueSetInConstructor)]
    [SizeUnit(SizeUnit.Megabytes)]
    [ConfigurationEntry("Join.AutoMaxBuildMemoryInMb", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public Size AutoMaxBuildMemory { get; set; }

    [Description("The estimated memory of the build of an auto join index is this (in megabytes) plus the memory per entry of the two indexes that it joins.")]
    [DefaultValue(192)]
    [SizeUnit(SizeUnit.Megabytes)]
    [ConfigurationEntry("Join.AutoBuildMemoryBaseInMb", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public Size AutoBuildMemoryBase { get; set; }

    [Description("The memory (in bytes) that the estimate of the build of an auto join index adds for every entry of the two indexes that it joins.")]
    [DefaultValue(8)]
    [ConfigurationEntry("Join.AutoBuildMemoryPerEntryInBytes", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public long AutoBuildMemoryPerEntry { get; set; }

    [Description("How long a query that asked to wait for non stale results and has to create an auto join index waits (in milliseconds) for the join index to be built, when the query has no timeout of its own.")]
    [DefaultValue(15000)]
    [TimeUnit(TimeUnit.Milliseconds)]
    [ConfigurationEntry("Join.AutoBuildWaitForNonStaleInMs", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public TimeSetting AutoBuildWaitForNonStale { get; set; }

    [Description("The storage mode of the lists of a join index whose definition does not name one. Expanded lists hold the outer entries of the keys that match. Indirect lists hold the keys and are only used while Join.IndirectEnabled is true. " +
                 "A join index that was built keeps its mode; this applies to the join indexes that are built or reset afterwards. Auto (the default) chooses when the join index is built: Indirect for a join index on a field whose outer entries share their keys (Join.AutoIndirectMinRowsPerKey rows per key or more) " +
                 "when its resident cache fits the budget and the server is not short of memory, else Expanded. Indirect is used only while Join.IndirectEnabled is true.")]
    [DefaultValue(JoinStorageMode.Auto)]
    [ConfigurationEntry("Join.StorageModeDefault", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public JoinStorageMode StorageModeDefault { get; set; }

    [Description("The memory (in megabytes) that the resident key-list caches of all Indirect join indexes of the server may hold together; the cache of one join index is limited by it too. 0 switches the caches off. " +
                 "The auto join indexes are not built when the memory that the cache of the new join index would need is not left. The caches are shared by the databases of the server, so this can only be set for the server, not for a database. The default is 256 MB, or a sixteenth of the memory of the machine (or of its container limit) when that is less.")]
    [DefaultValue(DefaultValueSetInConstructor)]
    [SizeUnit(SizeUnit.Megabytes)]
    [ConfigurationEntry("Join.ResidentCacheMaxMemoryInMb", ConfigurationEntryScope.ServerWideOnly)]
    public Size ResidentCacheMaxMemory { get; set; }

    [Description("The number of outer entries per key of a join index on a field from which a join index whose storage mode is Auto is built Indirect.")]
    [DefaultValue(30)]
    [ConfigurationEntry("Join.AutoIndirectMinRowsPerKey", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public int AutoIndirectMinRowsPerKey { get; set; }

    [Description("Allows the storage mode Indirect for a join index on a field or on the id of the inner documents: in its definition, and (for a join index on a field) as Join.StorageModeDefault. It is not released: leave it false outside of tests.")]
    [DefaultValue(false)]
    [ConfigurationEntry("Join.IndirectEnabled", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public bool IndirectEnabled { get; set; }
}
