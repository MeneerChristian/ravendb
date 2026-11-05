using System;
using System.ComponentModel;
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
        // 1/128 of the machine, in changes of 128 bytes: 8 MiB (65,536 changes) at 1 GB, and the million changes that a refresh always queued from 16 GB on
        // the queue of a build is a sixteenth of the machine at most: 64 MiB (524,288 changes) at 1 GB, the million changes that a build always queued from 2 GB on
        MaxQueuedListChangesInBuild = (int)Math.Clamp(totalPhysicalMemory.GetValue(SizeUnit.Bytes) / 16 / BytesPerQueuedListChange, 1024, MaxDefaultMinQueuedListChanges);

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

    [Description("A refresh queues the changes of the lists and applies them when this many are queued: the most it ever queues. A change takes about 128 bytes of memory; the queue is also limited to a quarter of the available memory and is never shorter than Join.MinQueuedListChanges. A list that is changed by many small flushes is rewritten each time.")]
    [DefaultValue(4_194_304)]
    [MinValue(1024)]
    [ConfigurationEntry("Join.MaxQueuedListChanges", ConfigurationEntryScope.ServerWideOrPerDatabase)]
    public int MaxQueuedListChanges { get; set; }

    [Description("The shortest queue of list changes that a refresh uses when little memory is available. The default is 1,048,576 changes (128 MiB) from 16 GB of memory on, and 1/128 of the memory of the machine (or of its container limit) below that: 65,536 changes (8 MiB) at 1 GB. " +
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
}
