using System;
using System.ComponentModel;
using Raven.Server.Config.Attributes;
using Raven.Server.Config.Settings;
using Raven.Server.Documents.Indexes.Configuration;
using Sparrow;
using Sparrow.Server.LowMemory;

namespace Raven.Server.Config.Categories
{
    [ConfigurationCategory(ConfigurationCategoryType.Query)]
    public sealed class QueryConfiguration : ConfigurationCategory
    {
        private const long MaxDefaultJoinKeysMemoryInMb = 512;

        public QueryConfiguration() : this(MemoryInformation.TotalPhysicalMemory)
        {
        }

        internal QueryConfiguration(Size totalPhysicalMemory)
        {
            // the keys of a JOIN sub-query are held in native memory while the query runs: at most an eighth of the machine (or of its container limit), and 512 MB from 4 GB on
            JoinMaxKeysMemory = new Size(Math.Min(MaxDefaultJoinKeysMemoryInMb, Math.Max(1, totalPhysicalMemory.GetValue(SizeUnit.Megabytes) / 8)), SizeUnit.Megabytes);
        }

        [DefaultValue(null)]//1024 is Lucene.net default, but we don't set it by default
        [ConfigurationEntry("Query.MaxClauseCount", ConfigurationEntryScope.ServerWideOnly)]
        public int? MaxClauseCount { get; set; }
        
        [Description("Timeout for Regex in regex query.")]
        [TimeUnit(TimeUnit.Milliseconds)]
        [DefaultValue(100)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Query.RegexTimeoutInMs", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public TimeSetting RegexTimeout { get; set; }

        [Description("The most native memory that the keys of one JOIN sub-query may hold while the server reads the join (about 42 bytes for each matched document). " +
                     "A JOIN whose sub-query matches more documents than fit fails with an error that names this setting; narrow the sub-query or raise the limit. " +
                     "The default is an eighth of the memory of the machine (or of its container limit), at most 512 MB.")]
        [DefaultValue(DefaultValueSetInConstructor)]
        [MinValue(1)]
        [SizeUnit(SizeUnit.Megabytes)]
        [ConfigurationEntry("Query.JoinMaxKeysMemoryInMb", ConfigurationEntryScope.ServerWideOnly)]
        public Size JoinMaxKeysMemory { get; set; }

        [Description("How long a JOIN waits for the index of its sub-query when a replacement of that index holds it or has not yet taken its place, before it fails with an error that says so.")]
        [DefaultValue(10_000)]
        [TimeUnit(TimeUnit.Milliseconds)]
        [ConfigurationEntry("Query.JoinSourceWaitInMs", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public TimeSetting JoinSourceWait { get; set; }
    }
}
