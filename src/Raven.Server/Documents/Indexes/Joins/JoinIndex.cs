using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Exceptions;
using Raven.Server.Config;
using Raven.Server.Documents.Includes;
using Raven.Server.Documents.Indexes.Configuration;
using Raven.Server.Documents.Indexes.Debugging;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Raven.Server.Documents.Indexes.Persistence;
using Raven.Server.Documents.Indexes.Workers;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.Facets;
using Raven.Server.Documents.Queries.Results;
using Raven.Server.Documents.Queries.Suggestions;
using Raven.Server.Documents.Queries.Timings;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.LowMemory;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Voron;
using Voron.Util;
using IndexFieldType = Raven.Server.Documents.Indexes.Debugging.IndexFieldType;

namespace Raven.Server.Documents.Indexes.Joins;

/// <summary>
/// An index that keeps up to date what a JOIN of two other indexes needs. It is an index like any other (state, priority, lock mode, reset, side-by-side replacement,
/// backup, encryption, statistics), except that it has no entries to query: it is read by the JOINs that it serves, and refuses every query of its own.
/// </summary>
public sealed class JoinIndex : Index<JoinIndexDefinition, JoinIndexField>
{
    private static readonly TimeSpan PendingLookInterval = TimeSpan.FromSeconds(2);

    private bool? _isSideBySide;
    private string _pendingReason;
    private long _outerBatches;
    private long _innerBatches;
    private Timer _lookAgain;
    private long _lifecycle;
    private (long Outer, long Inner, long Lifecycle)? _handled;
    private Index _validatedOuter;
    private Index _validatedInner;
    private int _resetRequested;
    private string _refusal;
    private readonly object _recoveryLock = new();
    private JoinPlan _plan;
    private JoinIncrementalRefresh _engine;
    private JoinOptions _options;
    private JoinQueryOptions _queryOptions;
    internal readonly JoinCounters Counters = new();

    /// <summary>Called at named points of a refresh, for tests; an exception that it throws ends the refresh.</summary>
    internal Action<string> ForTestingPurposes { get; set; }

    private JoinIndex(JoinIndexDefinition definition)
        : base(IndexType.Join, IndexSourceType.Documents, definition, compiled: null)
    {
    }

    /// <summary>The message of every query that names a join index.</summary>
    internal static BadRequestException NotQueryable(string name)
    {
        return new BadRequestException($"The join index '{name}' answers JOINs of the indexes it joins. It is not queried directly.");
    }

    protected override IIndexingWork[] CreateIndexWorkExecutors()
    {
        return [new JoinRefreshWork(this)];
    }

    internal JoinDefinition Join => Definition.IndexDefinition.Join;

    internal JoinPlan Plan => _plan ??= new JoinPlan(Join);

    internal JoinOptions Options => _options ??= JoinOptions.From(DocumentDatabase.Configuration);

    internal JoinIncrementalRefresh Engine => _engine ??= new JoinIncrementalRefresh(Plan, Options, DocumentDatabase.DocumentsStorage, Counters)
    {
        AvailableMemoryBytes = () => DocumentDatabase.ServerStore.Server.MetricCacher.GetValue<MemoryInfoResult>(MetricCacher.Keys.Server.MemoryInfo).AvailableMemoryForProcessing.GetValue(SizeUnit.Bytes)
    };

    protected override void OnInitialization()
    {
        base.OnInitialization();

        // heard also while the indexing thread is stopped, which is the case in the error state
        if (DocumentDatabase != null)
            DocumentDatabase.Changes.OnIndexChange += OnUpstreamChanged;

        // the throttling of the index is a window in which the wakes of the batches of the two indexes are one
        if (Configuration.ThrottlingTimeInterval == null && Options.RefreshWindow > TimeSpan.Zero)
            _mre.Update(Options.RefreshWindow);
    }

    /// <summary>A query waits for the join index: it refreshes now, not at the end of the window.</summary>
    internal void Expedite() => _mre?.Set(ignoreThrottling: true);

    protected override void DisposeIndex()
    {
        if (DocumentDatabase != null)
            DocumentDatabase.Changes.OnIndexChange -= OnUpstreamChanged;

        base.DisposeIndex();
        _engine?.Dispose();
        _engine = null;
        _plan?.Dispose();
        _plan = null;
    }

    internal string OuterName => Definition.IndexDefinition.Join.OuterIndex;

    internal string InnerName => Definition.IndexDefinition.Join.InnerIndex;

    /// <summary>What the queries did with this join index since the index was opened.</summary>
    internal JoinQueryCounters QueryCounters { get; } = new();

    internal JoinQueryOptions QueryOptions => _queryOptions ??= JoinQueryOptions.From(DocumentDatabase.Configuration);

    internal JoinIndexQueryHooks QueryHooks { get; set; }

    internal JoinScoreProbeCache ScoreProbes { get; } = new();

    /// <summary>Pending while one of the two indexes that the join index joins is missing, disabled or in error: there is nothing to read.</summary>
    public override IndexRunningStatus Status
    {
        get
        {
            IndexRunningStatus status = base.Status;
            return status == IndexRunningStatus.Running && Volatile.Read(ref _pendingReason) != null ? IndexRunningStatus.Pending : status;
        }
    }

    /// <summary>Looks the two indexes up and records why the join index cannot read them, null when it can.</summary>
    internal bool TryResolveUpstream(out Index outer, out Index inner)
    {
        inner = null;

        string reason = Check(OuterName, "outer", out outer) ?? Check(InnerName, "inner", out inner);
        if (reason != null)
        {
            Volatile.Write(ref _pendingReason, reason);
            Interlocked.Increment(ref _lifecycle);
            return false;
        }

        return true;
    }

    /// <summary>Called once the two indexes were found to be the ones the lists reflect: until then a join index that is about to be reset is not reported as running.</summary>
    internal void ClearPending() => Volatile.Write(ref _pendingReason, null);

    /// <summary>The join index cannot read an index it joins right now (it is busy with a storage operation or was not opened yet): it is pending and looks again soon.</summary>
    internal void SetPending(string reason)
    {
        Volatile.Write(ref _pendingReason, reason);
        Interlocked.Increment(ref _lifecycle);
    }

    private string Check(string name, string role, out Index upstream)
    {
        upstream = DocumentDatabase.IndexStore.GetIndex(name);
        if (upstream == null)
            return $"The {role} index '{name}' does not exist.";

        if (upstream.State is IndexState.Disabled or IndexState.Error)
            return $"The {role} index '{name}' is {upstream.State}.";

        return null;
    }

    /// <summary>Opens a snapshot of each of the two indexes. False (and the join index is pending, to look again soon) when one of them cannot be read now.</summary>
    internal bool TryOpenViews(Index outer, Index inner, JoinPlan plan, out IndexView outerView, out IndexView innerView)
    {
        outerView = null;
        innerView = null;

        if (outer.TryOpenSnapshot(out IndexSnapshot outerSnapshot) == false)
        {
            SetPending($"The outer index '{outer.Name}' cannot be read now.");
            return false;
        }

        outerView = new IndexView(outerSnapshot, plan.IdField);
        if (inner.TryOpenSnapshot(out IndexSnapshot innerSnapshot) == false)
        {
            outerView.Dispose();
            outerView = null;
            SetPending($"The inner index '{inner.Name}' cannot be read now.");
            return false;
        }

        innerView = new IndexView(innerSnapshot, plan.IdField);
        return true;
    }

    internal void WriteConsumed(TransactionOperationContext indexContext, string collection, long document, long tombstone)
    {
        _indexStorage.WriteLastIndexedEtag(indexContext.Transaction, collection, document);
        _indexStorage.WriteLastTombstoneEtag(indexContext.Transaction, collection, tombstone);
    }

    /// <summary>Opens a read transaction on the lists of the join index. False when the index is not open or is busy with a storage operation.</summary>
    internal bool TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot)
    {
        snapshot = null;

        IndexQueryDoneRunning queryLock = CurrentlyInUse(out bool available);
        IDisposable context = null;
        IDisposable transaction = null;
        try
        {
            if (available == false || _contextPool == null)
                return false;

            context = _contextPool.AllocateOperationContext(out TransactionOperationContext indexContext);
            RavenTransaction indexTx = indexContext.OpenReadTransaction();
            transaction = indexTx;
            IDisposable owned = new DisposableAction(() =>
            {
                indexTx.Dispose();
                context.Dispose();
                queryLock.Dispose();
            });
            snapshot = new JoinIndexSnapshot(indexTx.InnerTransaction, Plan.Schema, owned);
            return true;
        }
        finally
        {
            if (snapshot == null)
            {
                transaction?.Dispose();
                context?.Dispose();
                queryLock.Dispose();
            }
        }
    }

    /// <summary>What the join index is woken by, read before the indexes are opened: what is added after that is seen by the next run.</summary>
    internal (long Outer, long Inner, long Lifecycle) ReadWakeState() => (Interlocked.Read(ref _outerBatches), Interlocked.Read(ref _innerBatches), Interlocked.Read(ref _lifecycle));

    /// <summary>True when nothing woke the join index since the last run that was committed: no batch of the two indexes did work and none of them changed.</summary>
    internal bool IsHandled((long Outer, long Inner, long Lifecycle) state) => _handled == state;

    internal void MarkHandledAfterCommit(TransactionOperationContext indexContext, (long Outer, long Inner, long Lifecycle) state)
    {
        indexContext.Transaction.InnerTransaction.LowLevelTransaction.AfterCommitWhenNewTransactionsPrevented += _ => _handled = state;
    }

    internal bool IsValidatedAgainst(Index outer, Index inner) => ReferenceEquals(outer, _validatedOuter) && ReferenceEquals(inner, _validatedInner);

    internal void SetValidated(Index outer, Index inner)
    {
        _validatedOuter = outer;
        _validatedInner = inner;
    }

    /// <summary>
    /// The lists cannot be brought to the state of the indexes by changes: the index is reset in place, which builds them again. It runs on another thread because a reset stops the thread of this index.
    /// </summary>
    internal void RequestReset(JoinRebuildReason reason)
    {
        if (Interlocked.Exchange(ref _resetRequested, 1) != 0)
            return;

        Counters.Rebuild(reason);
        Volatile.Write(ref _pendingReason, $"The join index is rebuilt: {reason}.");
        if (_logger.IsInfoEnabled)
            _logger.Info($"The join index '{Name}' is reset: {reason}.");

        string name = Name;
        DocumentDatabase database = DocumentDatabase;
        _ = Task.Run(() =>
        {
            try
            {
                ForTestingPurposes?.Invoke("BeforeReset");
                database.IndexStore.ResetIndex(name);
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref _resetRequested, 0);
                if (_logger.IsErrorEnabled)
                    _logger.Error($"Could not reset the join index '{name}'.", e);
            }
        });
    }

    protected override void SubscribeToChanges(DocumentDatabase documentDatabase)
    {
        if (documentDatabase == null)
            return;

        documentDatabase.IndexStore.BatchCommitted += OnBatchCommitted;
        documentDatabase.Changes.OnIndexChange += OnIndexChange;

        // the batches of the two indexes that were committed while the join index was stopped were not heard: it has to look at them
        Interlocked.Increment(ref _lifecycle);

        // not every change of the state of an index is announced: a join index that is pending looks at the indexes again from time to time
        _lookAgain = new Timer(_ =>
        {
            if (Volatile.Read(ref _pendingReason) != null)
                _mre?.Set();
        }, null, PendingLookInterval, PendingLookInterval);
    }

    protected override void UnsubscribeFromChanges(DocumentDatabase documentDatabase)
    {
        if (documentDatabase == null)
            return;

        documentDatabase.IndexStore.BatchCommitted -= OnBatchCommitted;
        documentDatabase.Changes.OnIndexChange -= OnIndexChange;
        Interlocked.Exchange(ref _lookAgain, null)?.Dispose();
    }

    /// <summary>A batch of one of the two indexes changed it: the join index has something to read. A batch that did no work changed nothing and wakes nobody.</summary>
    private void OnBatchCommitted(string indexName, bool didWork)
    {
        if (didWork == false)
            return;

        bool inner = false;
        if (string.Equals(indexName, OuterName, StringComparison.OrdinalIgnoreCase))
            Interlocked.Increment(ref _outerBatches);
        else if (string.Equals(indexName, InnerName, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _innerBatches);
            inner = true;
        }
        else
            return;

        // the window is for the many small batches of the outer index; a change of the joined documents can move many entries and is not waited for
        _mre.Set(ignoreThrottling: inner);
    }

    /// <summary>Why the join index refused the indexes it joins and went to the error state, null when it did not (or has recovered).</summary>
    internal string Refusal => Volatile.Read(ref _refusal);

    /// <summary>The indexes it joins are in a definition that a join index cannot read: the join index stops, with an alert that says why and what ends it.</summary>
    internal void Refuse(string reason)
    {
        Volatile.Write(ref _refusal, reason);
        DocumentDatabase.NotificationCenter.Add(AlertRaised.Create(DocumentDatabase.Name, $"The join index '{Name}' was stopped",
            $"{reason} The join index starts again by itself when the indexes it joins are in a definition it can read, or when it is enabled.",
            AlertReason.Indexing_JoinRefused, NotificationSeverity.Error, key: Name));
    }

    /// <summary>
    /// An index that the join index joins changed while the join index is stopped because it refused an index: when both indexes are in a definition the join index can read, it is enabled again. It then goes on from what it
    /// published like any join index that starts: it is served only when it equals the indexes, and it is built again when an index has another history (a replaced index has another environment).
    /// </summary>
    private void OnUpstreamChanged(IndexChange change)
    {
        if (change.Type == IndexChangeTypes.BatchCompleted || Refusal == null)
            return;

        if (string.Equals(change.Name, OuterName, StringComparison.OrdinalIgnoreCase) == false && string.Equals(change.Name, InnerName, StringComparison.OrdinalIgnoreCase) == false)
            return;

        // the change is announced while the index store is busy with it
        _ = Task.Run(RecoverFromRefusal);
    }

    private void RecoverFromRefusal()
    {
        try
        {
            lock (_recoveryLock)
            {
                if (Refusal == null || State != IndexState.Error || DocumentDatabase.DatabaseShutdown.IsCancellationRequested)
                    return;

                if (Check(OuterName, "outer", out Index outer) != null || Check(InnerName, "inner", out Index inner) != null)
                    return;

                if (JoinRefreshWork.FindRefusal(this, outer, inner) != null)
                    return;

                Volatile.Write(ref _refusal, null);
                DocumentDatabase.NotificationCenter.Dismiss(AlertRaised.GetKey(AlertReason.Indexing_JoinRefused, Name));
                if (_logger.IsInfoEnabled)
                    _logger.Info($"The indexes that the join index '{Name}' joins are in a definition it can read again: it is enabled.");

                Enable();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (_logger.IsErrorEnabled)
                _logger.Error($"Could not enable the join index '{Name}' after the indexes it joins were changed.", e);
        }
    }

    /// <summary>One of the two indexes was added, removed, replaced, renamed, disabled or is in error: the join index looks at them again at once.</summary>
    private void OnIndexChange(IndexChange change)
    {
        if (change.Type == IndexChangeTypes.BatchCompleted)
            return;

        if (string.Equals(change.Name, OuterName, StringComparison.OrdinalIgnoreCase) == false && string.Equals(change.Name, InnerName, StringComparison.OrdinalIgnoreCase) == false)
            return;

        Interlocked.Increment(ref _lifecycle);
        _mre.Set(ignoreThrottling: true);
    }

    public override IndexStats GetStats(bool calculateLag = false, bool calculateStaleness = false, bool calculateMemoryStats = false, bool calculateLastBatchStats = false,
        QueryOperationContext queryContext = null)
    {
        IndexStats stats = base.GetStats(calculateLag, calculateStaleness, calculateMemoryStats, calculateLastBatchStats, queryContext);
        stats.Join = new JoinIndexStats
        {
            OuterIndex = OuterName,
            InnerIndex = InnerName,
            PendingReason = Volatile.Read(ref _pendingReason),
            RefusedReason = Refusal,
            OuterBatches = Interlocked.Read(ref _outerBatches),
            InnerBatches = Interlocked.Read(ref _innerBatches),
            Refreshes = Counters.Refreshes,
            PartialRefreshes = Counters.PartialRefreshes,
            NoOpRefreshes = Counters.NoOps,
            Builds = Counters.Builds,
            IdsProcessed = Counters.IdsProcessed,
            ListUpdates = Counters.ListUpdates,
            LastRefreshBreakdown = Counters.LastBreakdown
        };
        if (TryOpenJoinSnapshot(out JoinIndexSnapshot published))
        {
            using (published)
            {
                stats.Join.Generation = published.Generation.Serial;
                stats.Join.IsPartial = published.Generation.IsPartial;
            }
        }

        foreach ((string reason, long count) in Counters.RebuildsByReason)
            stats.Join.RebuildsByReason[reason] = count;

        stats.Join.Served = QueryCounters.ServedCount;
        stats.Join.FallbackTotal = QueryCounters.FallbackTotal;
        foreach ((string reason, long count) in QueryCounters.Fallbacks)
            stats.Join.Fallbacks[reason] = count;

        stats.Join.QueryWaits = QueryCounters.WaitCount;
        stats.Join.QueryWaitTotalInMs = QueryCounters.WaitTotalInMs;
        stats.Join.QueryWaitMaxInMs = QueryCounters.WaitMaxInMs;

        return stats;
    }

    protected override void HandleDocumentChange(DocumentChange change)
    {
    }

    public override void ResetIsSideBySideAfterReplacement()
    {
        _isSideBySide = null;
    }

    protected override bool ShouldReplace()
    {
        return StaticIndexHelper.ShouldReplace(this, ref _isSideBySide);
    }

    public override IIndexedItemEnumerator GetMapEnumerator(IEnumerable<IndexItem> items, string collection, TransactionOperationContext indexContext, IndexingStatsScope stats, IndexType type)
    {
        throw new NotSupportedException($"The join index '{Name}' has no map.");
    }

    public override void HandleDelete(Tombstone tombstone, string collection, Lazy<IndexWriteOperationBase> writer, TransactionOperationContext indexContext, IndexingStatsScope stats)
    {
        throw new NotSupportedException($"The join index '{Name}' has no map.");
    }

    public override int HandleMap(IndexItem indexItem, IEnumerable mapResults, Lazy<IndexWriteOperationBase> writer, TransactionOperationContext indexContext, IndexingStatsScope stats)
    {
        throw new NotSupportedException($"The join index '{Name}' has no map.");
    }

    public override IQueryResultRetriever GetQueryResultRetriever(IndexQueryServerSide query, QueryTimingsScope queryTimings, DocumentsOperationContext documentsContext,
        SearchEngineType searchEngineType, FieldsToFetch fieldsToFetch, IncludeDocumentsCommand includeDocumentsCommand,
        IncludeCompareExchangeValuesCommand includeCompareExchangeValuesCommand, IncludeRevisionsCommand includeRevisionsCommand)
    {
        throw NotQueryable(Name);
    }

    public override void SaveLastState()
    {
    }

    public override ICollection<FieldDebugInfo> GetEntriesFields()
    {
        return Definition.MapFields.Keys.Select(x => new FieldDebugInfo(x, IndexFieldType.Static, IndexedValueType.Term)).ToHashSet();
    }

    public override Task<DocumentQueryResult> Query(IndexQueryServerSide query, QueryOperationContext queryContext, OperationCancelToken token, QueryTimeScope queryTime = null)
    {
        throw NotQueryable(Name);
    }

    public override Task<DocumentIdQueryResult> IdQuery(IndexQueryServerSide query, QueryOperationContext queryContext, DeterminateProgress progress,
        Action<DeterminateProgress> onProgress, OperationCancelToken token)
    {
        throw NotQueryable(Name);
    }

    public override Task StreamQuery(HttpResponse response, IStreamQueryResultWriter<Document> writer, IndexQueryServerSide query, QueryOperationContext queryContext,
        OperationCancelToken token, QueryTimeScope queryTime = null)
    {
        throw NotQueryable(Name);
    }

    public override Task StreamIndexEntriesQuery(HttpResponse response, IStreamQueryResultWriter<BlittableJsonReaderObject> writer, IndexQueryServerSide query,
        QueryOperationContext queryContext, bool ignoreLimit, OperationCancelToken token)
    {
        throw NotQueryable(Name);
    }

    public override Task<FacetedQueryResult> FacetedQuery(FacetQuery facetQuery, QueryOperationContext queryContext, OperationCancelToken token, QueryTimeScope queryTime = null)
    {
        throw NotQueryable(Name);
    }

    public override Task<SuggestionQueryResult> SuggestionQuery(IndexQueryServerSide query, QueryOperationContext queryContext, OperationCancelToken token)
    {
        throw NotQueryable(Name);
    }

    public override Task<IndexEntriesQueryResult> IndexEntries(IndexQueryServerSide query, QueryOperationContext queryContext, bool ignoreLimit, OperationCancelToken token)
    {
        throw NotQueryable(Name);
    }

    public override TermsQueryResultServerSide GetTerms(string field, string fromValue, long pageSize, QueryOperationContext queryContext, OperationCancelToken token)
    {
        throw NotQueryable(Name);
    }

    /// <summary>Creates a new join index and its storage. <paramref name="collections"/> are the collections of the two indexes that it joins.</summary>
    public static JoinIndex CreateNew(IndexDefinition definition, DocumentDatabase documentDatabase, IEnumerable<string> collections, SingleIndexConfiguration forcedConfiguration = null)
    {
        SingleIndexConfiguration configuration = forcedConfiguration ?? new SingleIndexConfiguration(definition.Configuration, documentDatabase.Configuration);
        var instance = new JoinIndex(new JoinIndexDefinition(definition, collections, IndexDefinitionBaseServerSide.IndexVersion.CurrentVersion));
        instance.Initialize(documentDatabase, configuration, documentDatabase.Configuration.PerformanceHints);
        return instance;
    }

    /// <summary>Opens the join index that was persisted in the environment.</summary>
    public static JoinIndex Open(StorageEnvironment environment, DocumentDatabase documentDatabase)
    {
        JoinIndexDefinition definition = JoinIndexDefinition.Load(environment);
        var instance = new JoinIndex(definition);
        instance.Initialize(environment, documentDatabase, new SingleIndexConfiguration(definition.IndexDefinition.Configuration, documentDatabase.Configuration),
            documentDatabase.Configuration.PerformanceHints);
        return instance;
    }

    /// <summary>Applies a change of the definition that does not need a new index: the priority, the lock mode, the state and the configuration.</summary>
    public static void Update(Index index, IndexDefinition definition, DocumentDatabase documentDatabase)
    {
        var joinIndex = (JoinIndex)index;
        var updated = new JoinIndexDefinition(definition, joinIndex.Definition.Collections, joinIndex.Definition.Version);
        joinIndex.Update(updated, new SingleIndexConfiguration(definition.Configuration, documentDatabase.Configuration));
    }
}
