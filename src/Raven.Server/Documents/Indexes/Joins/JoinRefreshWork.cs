using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Refresh;
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
        if (index.IsHandled(wake))
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
        var file = new JoinIndexFile(indexContext.Transaction.InnerTransaction, plan.Schema);
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
                if (advanced == false)
                {
                    counters.Add(ref counters.NoOps, 1);
                    index.MarkHandledAfterCommit(indexContext, wake);
                    return (false, Index.CanContinueBatchResult.True);
                }

                JoinRefreshOutcome outcome = RunEngine(file, outerView, innerView, published, startedAt, token);
                current = outcome.Generation;
                counters.Add(ref counters.Refreshes, 1);
                Record(counters, startedAt, allocated, outcome.Ids, ref counters.LastRefreshTicks, ref counters.LastRefreshDurationTicks, ref counters.LastRefreshAllocatedBytes);
                counters.LastRefreshIds = outcome.Ids;
                counters.Add(ref counters.TotalRefreshTicks, Stopwatch.GetTimestamp() - startedAt);

                if (outcome.EmptyTerms)
                    index.RequestReset(JoinRebuildReason.EmptyTerms);
            }

            WriteEtags(indexContext, index, current);
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

    private JoinRefreshOutcome RunEngine(JoinIndexFile file, IndexView outer, IndexView inner, JoinIndexGeneration published, long startedAt, CancellationToken token)
    {
        JoinIncrementalRefresh engine = index.Engine;
        engine.OnPhase = index.ForTestingPurposes;
        return engine.Run(file, outer, inner, published, startedAt, token);
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

        var validator = new JoinIndexValidator(name => JoinIndexFactsReader.Read(joinIndex.DocumentDatabase.IndexStore.GetIndex(name)), isShardedDatabase: false);
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
