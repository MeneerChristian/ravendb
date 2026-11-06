using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Raven.Server.Documents.Indexes.Persistence;
using Raven.Server.Documents.Indexes.Workers;
using Raven.Server.Exceptions;
using Raven.Server.ServerWide.Context;
using Constants = Raven.Client.Constants;

namespace Raven.Server.Documents.Indexes.Joins;

/// <summary>
/// The indexing work of a join index: brings the lists of the join index to the state of the two indexes it joins, in the write transaction of the index. The generation is written in the same
/// commit as the lists and the etags of the collections are the ones the lists reflect, so the staleness and the lag of the join index are the standard ones. While one of the two indexes is
/// missing, disabled or in error the join index is pending and has nothing to do. When the lists cannot follow the indexes by changes (an index was reset or changed its collections, too many
/// documents are written above an index that has not consumed them) the join index is reset, which builds the lists again in one transaction. Many changes of the indexes are applied in
/// several commits, each an exact intermediate state that is not served.
/// </summary>
internal sealed class JoinRefreshWork(JoinIndex index) : IIndexingWork
{
    public string Name => "JoinRefresh";

    public (bool MoreWorkFound, Index.CanContinueBatchResult BatchContinuationResult) Execute(QueryOperationContext queryContext, TransactionOperationContext indexContext,
        Lazy<IndexWriteOperationBase> writeOperation, IndexingStatsScope stats, CancellationToken token)
    {
        (long Outer, long Inner, long Lifecycle) wake = index.ReadWakeState();
        // a cache that has no epoch is established by a run that looks at the indexes, although they did not change
        if (index.IsHandled(wake) && index.ResidentWarmUpRequested == false)
        {
            index.Counters.Add(ref index.Counters.NoOps, 1);
            return (false, Index.CanContinueBatchResult.True);
        }

        if (index.TryResolveUpstream(out Index outer, out Index inner) == false)
            return (false, Index.CanContinueBatchResult.True);

        long startedAt = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread();

        AssertSupported(outer, inner);

        try
        {
            return Refresh(indexContext, outer, inner, wake, startedAt, allocated, token);
        }
        catch (JoinUnsupportedException e)
        {
            throw new CriticalIndexingException(e);
        }
        catch (JoinRebuildRequiredException e)
        {
            index.RequestReset(e.Reason);

            // changes that were written are not a state of the indexes: the exception ends the transaction without committing them
            if (index.Engine.HasWritten)
                throw;

            return (false, Index.CanContinueBatchResult.True);
        }
    }

    private (bool MoreWorkFound, Index.CanContinueBatchResult BatchContinuationResult) Refresh(TransactionOperationContext indexContext, Index outer, Index inner,
        (long Outer, long Inner, long Lifecycle) wake, long startedAt, long allocated, CancellationToken token)
    {

        JoinPlan plan = index.Plan;
        if (JoinFileRows.Exists(indexContext.Transaction.InnerTransaction) == false)
            index.ChooseBuildMode(outer, plan);
        else if (plan.Definition.StorageMode == JoinStorageMode.Auto && JoinFileRows.ReadMode(indexContext.Transaction.InnerTransaction) is { } fileMode && fileMode != plan.BuildMode
                 && fileMode is JoinStorageMode.Expanded or JoinStorageMode.Indirect)
            plan.ChooseBuildMode(fileMode);

        var file = new JoinIndexFile(indexContext.Transaction.InnerTransaction, plan.SchemaOfFile(indexContext.Transaction.InnerTransaction));
        JoinIndexGeneration published = file.Published;

        if (index.TryOpenViews(outer, inner, plan, out IndexView outerView, out IndexView innerView) == false)
            return (false, Index.CanContinueBatchResult.True);

        try
        {
            JoinIndexGeneration current = published;
            JoinCounters counters = index.Counters;

            if (published.OuterEnvironmentId == Guid.Empty)
            {
                index.ClearPending();
                current = Build(file, plan, outerView, innerView, token);
                if (file.Schema.IsIndirect)
                    DropResidentAfterBuild(indexContext);

                counters.Add(ref counters.Builds, 1);
                counters.Rebuild(JoinRebuildReason.Initial);
                Record(counters, startedAt, allocated, 0, ref counters.LastBuildTicks, ref counters.LastBuildDurationTicks, ref counters.LastBuildAllocatedBytes);
                counters.Add(ref counters.TotalBuildTicks, Stopwatch.GetTimestamp() - startedAt);
            }
            else
            {
                JoinRebuildReason? reset = null;
                bool advanced = JoinIncrementalRefresh.Classify(outerView, published.OuterEnvironmentId, published.Outer, ref reset);
                advanced |= JoinIncrementalRefresh.Classify(innerView, published.InnerEnvironmentId, published.Inner, ref reset);

                if (reset != null)
                {
                    index.RequestReset(reset.Value);
                    return (false, Index.CanContinueBatchResult.True);
                }

                index.ClearPending();
                JoinResident.Run residentRun = BeginResidentRun(indexContext, file, plan, outerView, published, token);
                if (advanced == false)
                {
                    counters.Add(ref counters.NoOps, 1);

                    // the indexes are the generation of the file: the outer index may have committed without a change of its entries, so the lists are the entries of the newer snapshot
                    ApplyResidentAfterCommit(indexContext, residentRun, published.Serial, outerApplied: true, outerPartial: false);
                    index.MarkHandledAfterCommit(indexContext, wake);
                    return (false, Index.CanContinueBatchResult.True);
                }

                JoinRefreshOutcome outcome = RunEngine(file, outerView, innerView, published, startedAt, residentRun?.Patch, token);
                current = outcome.Generation;
                if (residentRun != null)
                {
                    bool outerPartial = current.Mode == JoinStorageMode.Indirect && current.OuterPartial;
                    bool innerPartial = current.Mode == JoinStorageMode.Indirect && current.InnerPartial;
                    ApplyResidentAfterCommit(indexContext, residentRun, current.Serial, outerApplied: outerPartial == false && innerPartial == false, outerPartial);
                }

                counters.Add(ref counters.Refreshes, 1);
                Record(counters, startedAt, allocated, outcome.Ids, ref counters.LastRefreshTicks, ref counters.LastRefreshDurationTicks, ref counters.LastRefreshAllocatedBytes);
                counters.LastRefreshIds = outcome.Ids;
                counters.Add(ref counters.TotalRefreshTicks, Stopwatch.GetTimestamp() - startedAt);
                counters.Add(ref counters.RefreshAllocatedBytes, counters.LastRefreshAllocatedBytes);

                if (outcome.EmptyTerms)
                    index.RequestReset(JoinRebuildReason.EmptyTerms);
            }

            WriteEtags(indexContext, index, current);

            // the changes are all in the transaction of the index and it is not committed yet
            index.ForTestingPurposes?.Invoke("BeforeIndexCommit");
            if (current.IsPartial)
            {
                // the changes were cut at the budget of a commit: this transaction commits them and the next batch goes on at once
                counters.Add(ref counters.PartialRefreshes, 1);
                return (true, Index.CanContinueBatchResult.False);
            }

            index.MarkHandledAfterCommit(indexContext, wake);
            return (false, Index.CanContinueBatchResult.True);
        }
        finally
        {
            outerView.Dispose();
            innerView.Dispose();
        }
    }

    private JoinRefreshOutcome RunEngine(JoinIndexFile file, IndexView outer, IndexView inner, JoinIndexGeneration published, long startedAt, ResidentPatch patch, CancellationToken token)
    {
        JoinIncrementalRefresh engine = index.Engine;
        engine.OnPhase = index.ForTestingPurposes;
        engine.Patch = patch;
        try
        {
            return engine.Run(file, outer, inner, published, startedAt, token);
        }
        finally
        {
            engine.Patch = null;
        }
    }

    /// <summary>
    /// The resident cache of an Indirect file is brought to the epoch the file's generation says before the changes are made: a cold or dropped cache is established at the outer snapshot of this refresh and
    /// warmed up from it. Null when the file is Expanded or the cache is switched off.
    /// </summary>
    private JoinResident.Run BeginResidentRun(TransactionOperationContext indexContext, JoinIndexFile file, JoinPlan plan, IndexView outer, JoinIndexGeneration published, CancellationToken token)
    {
        JoinResident resident = file.Schema.IsIndirect ? index.EnsureResident() : null;
        if (resident == null)
            return null;

        index.ClearResidentWarmUpRequest();
        JoinResident.Run run = resident.BeginRun(outer.Snapshot.EnvironmentId, outer.Snapshot.Transaction.LowLevelTransaction.Id, outer.Searcher.LastEntryId, file.Seed, published.Serial, outer.Snapshot.Consumed,
            outerPartial: published.Mode == JoinStorageMode.Indirect && published.OuterPartial);

        // the run is dropped with the transaction whether it commits or not: a rolled back refresh leaves the cache as it was
        indexContext.Transaction.InnerTransaction.LowLevelTransaction.OnDispose += _ => run.Dispose();
        if (run.NeedsWarmUp)
        {
            using JoinReader reader = file.OpenReader();
            resident.WarmUp(run, reader, outer.Searcher, plan.OuterKeyField, plan.Definition.OuterField, token);
        }

        return run;
    }

    // the changes are applied to the cache only once the transaction that holds them committed: before that a rollback would leave them in lists that the retry never removes them from
    private static void ApplyResidentAfterCommit(TransactionOperationContext indexContext, JoinResident.Run run, long serial, bool outerApplied, bool outerPartial)
    {
        if (run == null)
            return;

        indexContext.Transaction.InnerTransaction.LowLevelTransaction.AfterCommitWhenNewTransactionsPrevented += _ => run.Apply(serial, outerApplied, outerPartial);
    }

    private void DropResidentAfterBuild(TransactionOperationContext indexContext)
    {
        JoinResident resident = index.EnsureResident();
        if (resident != null)
            indexContext.Transaction.InnerTransaction.LowLevelTransaction.AfterCommitWhenNewTransactionsPrevented += _ => resident.DropAfterBuild();
    }

    private JoinIndexGeneration Build(JoinIndexFile file, JoinPlan plan, IndexView outer, IndexView inner, CancellationToken token)
    {
        index.ForTestingPurposes?.Invoke("BuildStarted");
        using var builder = new JoinBuilder(file, plan, outer, inner, token, index.Engine.QueueLimit(index.Options.MaxQueuedListChangesInBuild), index.Options.MinAtomEntriesForDirectWrite);
        JoinIndexGeneration generation = builder.Run();

        JoinCounters counters = index.Counters;
        counters.LastBuildDirectAtoms = builder.DirectAtoms;
        counters.LastBuildQueuedAtoms = builder.QueuedAtoms;
        counters.LastBuildListFlushes = builder.ListFlushes;
        counters.LastBuildListUpdates = builder.ListUpdates;
        return generation;
    }

    private static void Record(JoinCounters counters, long startedAt, long allocated, long ids, ref long lastTicks, ref long lastDuration, ref long lastAllocated)
    {
        Interlocked.Exchange(ref lastTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref lastDuration, Stopwatch.GetTimestamp() - startedAt);
        Interlocked.Exchange(ref lastAllocated, GC.GetAllocatedBytesForCurrentThread() - allocated);
    }

    /// <summary>
    /// The etags of the collections are what the lists reflect: the lower of what the two indexes had consumed. A collection that only one of them reads has its etags.
    /// </summary>
    private void WriteEtags(TransactionOperationContext indexContext, JoinIndex joinIndex, JoinIndexGeneration generation)
    {
        var etags = new Dictionary<string, (long Document, long Tombstone)>(StringComparer.OrdinalIgnoreCase);
        Add(etags, generation.Outer);
        Add(etags, generation.Inner);

        foreach (string collection in joinIndex.Collections)
        {
            (long document, long tombstone) = etags.GetValueOrDefault(collection);
            joinIndex.WriteConsumed(indexContext, collection, document, tombstone);
        }

        static void Add(Dictionary<string, (long Document, long Tombstone)> into, JoinCursors cursors)
        {
            foreach ((string collection, long document, long tombstone) in cursors.Collections)
                into[collection] = into.TryGetValue(collection, out (long Document, long Tombstone) existing) ? (Math.Min(existing.Document, document), Math.Min(existing.Tombstone, tombstone)) : (document, tombstone);
        }
    }

    private void AssertSupported(Index outer, Index inner)
    {
        string refusal = FindRefusal(index, outer, inner);
        if (refusal == null)
            return;

        index.Refuse(refusal);
        throw new CriticalIndexingException(new JoinUnsupportedException(refusal));
    }

    /// <summary>Why the join index cannot read the two indexes in their present definition, null when it can.</summary>
    internal static string FindRefusal(JoinIndex joinIndex, Index outer, Index inner)
    {
        foreach (Index upstream in new[] { outer, inner })
        {
            if (upstream.MaxNumberOfOutputsPerDocument > 1)
                return $"The index '{upstream.Name}' produced several entries for one document: a join index holds one entry per document.";
        }

        if (joinIndex.IsValidatedAgainst(outer, inner))
            return null;

        var validator = new JoinIndexValidator(name => JoinIndexFactsReader.Read(joinIndex.DocumentDatabase.IndexStore.GetIndex(name)), isShardedDatabase: false, indirectEnabled: joinIndex.DocumentDatabase.Configuration.Join.IndirectEnabled);
        var errors = validator.Validate(joinIndex.Join);
        if (errors.Count > 0)
            return JoinValidationException.BuildMessage(errors);

        foreach (Index upstream in new[] { outer, inner })
        {
            if (upstream.Collections.Contains(Constants.Documents.Collections.AllDocumentsCollection))
                return $"The index '{upstream.Name}' indexes all documents: a join index needs the collections of an index.";
        }

        joinIndex.SetValidated(outer, inner);
        return null;
    }
}
