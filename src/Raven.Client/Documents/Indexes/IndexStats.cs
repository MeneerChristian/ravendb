using System;
using System.Collections.Generic;
using Raven.Client.Documents.DataArchival;
using Raven.Client.Documents.Operations.DataArchival;
using Raven.Client.Util;

namespace Raven.Client.Documents.Indexes
{
    public sealed class IndexStats
    {
        /// <summary>
        /// Index name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Indicates how many times the database tried to index documents (map) using this index.
        /// </summary>
        public int MapAttempts { get; set; }

        /// <summary>
        /// Indicates how many indexing attempts succeeded.
        /// </summary>
        public int MapSuccesses { get; set; }

        /// <summary>
        /// Indicates how many indexing attempts failed.
        /// </summary>
        public int MapErrors { get; set; }

        /// <summary>
        /// Indicates how many times the database tried to index referenced documents (map) using this index.
        /// </summary>
        public int? MapReferenceAttempts { get; set; }

        /// <summary>
        /// Indicates how many indexing attempts of referenced documents succeeded.
        /// </summary>
        public int? MapReferenceSuccesses { get; set; }

        /// <summary>
        /// Indicates how many indexing attempts of referenced documents failed.
        /// </summary>
        public int? MapReferenceErrors { get; set; }

        /// <summary>
        /// Indicates how many times database tried to index documents (reduce) using this index.
        /// </summary>
        public long? ReduceAttempts { get; set; }

        /// <summary>
        /// Indicates how many reducing attempts succeeded.
        /// </summary>
        public long? ReduceSuccesses { get; set; }

        /// <summary>
        /// Indicates how many reducing attempts failed.
        /// </summary>
        public long? ReduceErrors { get; set; }
        
        /// <summary>
        /// The reduce output collection.
        /// </summary>
        public string ReduceOutputCollection { get; set; }
        
        /// <summary>
        /// Pattern for creating IDs for the reduce output reference-collection 
        /// </summary>
        public string ReduceOutputReferencePattern { get; set; }
        
        /// <summary>
        /// Collection name for reduce output reference-collection 
        /// </summary>
        public string PatternReferencesCollectionName { get; set; }

        /// <summary>
        /// The value of docs/sec rate for the index over the last minute
        /// </summary>
        public double MappedPerSecondRate { get; set; }

        /// <summary>
        /// The value of reduces/sec rate for the index over the last minute
        /// </summary>
        public double ReducedPerSecondRate { get; set; }

        /// <summary>
        /// Indicates the maximum number of produced indexing outputs from a single document
        /// </summary>
        public int MaxNumberOfOutputsPerDocument { get; set; }

        public Dictionary<string, CollectionStats> Collections { get; set; }

        /// <summary>
        /// Time of last query for this index.
        /// </summary>
        public DateTime? LastQueryingTime { get; set; }

        /// <summary>
        /// Index state (Normal, Disabled, Idle, Abandoned, Error)
        /// </summary>
        public IndexState State { get; set; }

        /// <summary>
        /// Index priority (Low, Normal, High)
        /// </summary>
        public IndexPriority Priority { get; set; }

        /// <summary>
        /// Date of index creation.
        /// </summary>
        public DateTime CreatedTimestamp { get; set; }

        /// <summary>
        /// Time of last indexing (map or reduce) for this index.
        /// </summary>
        public DateTime? LastIndexingTime { get; set; }

        public bool IsStale { get; set; }

        /// <summary>
        /// Indicates current lock mode:
        /// <para>- Unlock - all index definition changes acceptable</para>
        /// <para>- LockedIgnore - all index definition changes will be ignored, only log entry will be created</para>
        /// <para>- LockedError - all index definition changes will raise exception</para>
        /// </summary>
        public IndexLockMode LockMode { get; set; }

        /// <summary>
        /// Indicates index type.
        /// </summary>
        public IndexType Type { get; set; }
        
        /// <summary>
        /// Indicates search engine.
        /// </summary>
        public SearchEngineType SearchEngineType { get; set; }
        
        /// <summary>
        /// Indicates whether archived, unarchived or all documents will be indexed
        /// </summary>
        public ArchivedDataProcessingBehavior? ArchivedDataProcessingBehavior { get; set; }

        public IndexRunningStatus Status { get; set; }

        /// <summary>
        /// Total number of entries in this index.
        /// </summary>
        public long EntriesCount { get; set; }

        public int ErrorsCount { get; set; }
        
        public IndexSourceType SourceType { get; set; }
        
        /// <summary>
        /// Returns the names of referenced collections.
        /// </summary>
        public HashSet<string> ReferencedCollections { get; set; }

        /// <summary>What a join index reports about the two indexes it joins. Null for every other index.</summary>
        public JoinIndexStats Join { get; set; }

#if FEATURE_TEST_INDEX
        /// <summary>
        /// Indicates if this is a test index (works on a limited data set - for testing purposes only)
        /// </summary>
        public bool IsTestIndex { get; set; }
#endif

        /// <summary>
        /// Determines if index is invalid. If more than 15% of attempts (map or reduce) are errors then value will be <c>true</c>.
        /// </summary>
        public bool IsInvalidIndex => IndexFailureInformation.CheckIndexInvalid(MapAttempts, MapErrors, MapReferenceAttempts, MapReferenceErrors, ReduceAttempts, ReduceErrors, IsStale);

        public MemoryStats Memory { get; set; }

        public IndexingPerformanceBasicStats LastBatchStats { get; set; }

        public sealed class MemoryStats
        {
            public MemoryStats()
            {
                DiskSize = new Size();
                ThreadAllocations = new Size();
                MemoryBudget = new Size();
            }

            public Size DiskSize { get; set; }
            public Size ThreadAllocations { get; set; }
            public Size MemoryBudget { get; set; }
        }

        public sealed class CollectionStats
        {
            public CollectionStats()
            {
                DocumentLag = -1;
                TombstoneLag = -1;
            }

            public long LastProcessedDocumentEtag { get; set; }

            public long LastProcessedTombstoneEtag { get; set; }

            public long LastProcessedTimeSeriesDeletedRangeEtag { get; set; }

            public long DocumentLag { get; set; }

            public long TombstoneLag { get; set; }
        }
    }
    /// <summary>The part of <see cref="IndexStats"/> that only a join index has.</summary>
    public sealed class JoinIndexStats
    {
        /// <summary>The name of the outer index.</summary>
        public string OuterIndex { get; set; }

        /// <summary>The name of the inner index.</summary>
        public string InnerIndex { get; set; }

        /// <summary>Why the join index cannot read the indexes it joins now (one is missing, disabled or in error); null when it can.</summary>
        public string PendingReason { get; set; }

        /// <summary>Why the join index went to the error state: an index it joins is in a definition a join index cannot read. It is enabled again by itself when they are valid again; null when it did not refuse them.</summary>
        public string RefusedReason { get; set; }

        /// <summary>The number of batches of the outer index that changed it, as the join index was told.</summary>
        public long OuterBatches { get; set; }

        /// <summary>The number of batches of the inner index that changed it, as the join index was told.</summary>
        public long InnerBatches { get; set; }

        /// <summary>The number of refreshes that changed the lists since the index was opened.</summary>
        public long Refreshes { get; set; }

        /// <summary>The number of refreshes that found nothing to do.</summary>
        public long NoOpRefreshes { get; set; }

        /// <summary>The number of times the lists were built from scratch.</summary>
        public long Builds { get; set; }

        /// <summary>The number of changed documents that the refreshes looked at.</summary>
        public long IdsProcessed { get; set; }

        /// <summary>The number of lists that the refreshes changed.</summary>
        public long ListUpdates { get; set; }

        /// <summary>How long the phases of the last refresh took.</summary>
        public string LastRefreshBreakdown { get; set; }

        /// <summary>How many times the lists were thrown away and built again, by the reason.</summary>
        public Dictionary<string, long> RebuildsByReason { get; set; } = new();

        /// <summary>The joins that were answered from the lists of this join index since the index was opened.</summary>
        public long Served { get; set; }

        /// <summary>The joins that were read as usual although this join index was asked: the sum of <see cref="Fallbacks"/>.</summary>
        public long FallbackTotal { get; set; }

        /// <summary>The joins that were read as usual although this join index was asked, by the reason.</summary>
        public Dictionary<string, long> Fallbacks { get; set; } = new();

        /// <summary>The queries that waited for the join index to catch up with the indexes.</summary>
        public long QueryWaits { get; set; }

        public double QueryWaitTotalInMs { get; set; }

        public double QueryWaitMaxInMs { get; set; }
    }
}
