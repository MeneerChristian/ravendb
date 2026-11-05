using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes;
using Index = Raven.Server.Documents.Indexes.Index;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Server;

namespace Raven.Server.Documents.Queries
{
    internal sealed class JoinedIndexes
    {
        private readonly List<Index> _indexes = new();
        private DocumentDatabase _database;
        private readonly List<string> _innerNames = new();
        private Dictionary<Index, (long DocumentCutoff, long? ReferenceCutoff, long? CompareExchangeReferenceCutoff)> _cutoffs;

        // Null when the query has no joins
        public static JoinedIndexes Create(DocumentDatabase database, Query query)
        {
            if (query.Joins == null)
                return null;

            var result = new JoinedIndexes { _database = database };
            result.Add(database, query);
            foreach (var join in query.Joins)
                result._innerNames.Add(join.Query.From.From.FieldValue);

            return result;
        }

        private void Add(DocumentDatabase database, Query query)
        {
            if (query.Joins == null)
                return;

            foreach (var join in query.Joins)
            {
                var name = join.Query.From.From.FieldValue;
                var index = database.IndexStore.GetIndex(name)
                            ?? throw new IndexDoesNotExistException($"The JOIN sub-query refers to the index '{name}', which does not exist.");

                if (_indexes.Contains(index) == false)
                    _indexes.Add(index);

                Add(database, join.Query);
            }
        }

        // The index that the store has by the name is the one to read: a replacement registers the new index before it disposes the old one, which takes as long as the queries of the old
        // one run. An index of that name that was disposed and is still the one of the store (the replacement did not take its place yet) is waited for: this waits for the store to register
        // an index, for as long as the wait allows. A name that the store does not have any more is a deleted index.
        internal static Index CurrentOf(DocumentDatabase database, string name, Index index, TimeSpan wait)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                // taken before the store is read, so a registration in between is not missed
                var registered = database.IndexStore.GetIndexRegistrationAwaiter();
                var current = database.IndexStore.GetIndex(name);
                if (current != null && ReferenceEquals(current, index) == false)
                {
                    index = current;
                    continue;
                }

                if (index.IsDisposed == false)
                    return index;

                if (current == null)
                    throw new IndexDoesNotExistException($"The JOIN sub-query refers to the index '{name}', which does not exist.");

                var remaining = wait - watch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    throw new InvalidQueryException($"The JOIN sub-query reads the index '{name}', which is being replaced: no replacement took its place within {wait.TotalMilliseconds:N0} ms " +
                                                    $"(setting {RavenConfiguration.GetKey(x => x.Queries.JoinSourceWait)}).");

                registered.WaitAsync().Wait(remaining);
            }
        }

        // Taken before the staleness is checked, so an indexing batch that completes in between is not missed
        public Dictionary<Index, AsyncManualResetEvent.FrozenAwaiter> GetAwaiters()
        {
            for (var i = 0; i < _indexes.Count; i++)
            {
                var current = CurrentOf(_database, _indexes[i].Name, _indexes[i], _database.Configuration.Queries.JoinSourceWait.AsTimeSpan);
                if (ReferenceEquals(current, _indexes[i]) == false && _indexes.Contains(current) == false)
                    _indexes[i] = current;
            }

            var awaiters = new Dictionary<Index, AsyncManualResetEvent.FrozenAwaiter>(_indexes.Count);
            foreach (var index in _indexes)
                awaiters[index] = index.GetIndexingBatchAwaiter();

            return awaiters;
        }

        // The first joined index that is stale, or null
        public Index FindStale(QueryOperationContext queryContext, bool waitForNonStaleResults)
        {
            foreach (var index in _indexes)
            {
                (long DocumentCutoff, long? ReferenceCutoff, long? CompareExchangeReferenceCutoff)? cutoff = null;
                if (waitForNonStaleResults)
                {
                    _cutoffs ??= new();
                    if (_cutoffs.TryGetValue(index, out var existing) == false)
                        _cutoffs[index] = existing = index.GetCutoffEtag(queryContext);

                    cutoff = existing;
                }

                if (index.IsStaleForJoin(queryContext, cutoff))
                    return index;
            }

            return null;
        }

        // The join indexes of the outer index that join one of the indexes of the query, in the order they are tried: put join indexes first. A join index that is being replaced by another definition is not asked.
        // All join indexes of the same join are kept: the first that can answer the query answers it
        private List<JoinIndex> FindJoins(DocumentDatabase database, Index outer)
        {
            List<JoinIndex> result = null;
            foreach (var joinIndex in database.IndexStore.GetJoins())
            {
                if (string.Equals(joinIndex.OuterName, outer.Name, StringComparison.OrdinalIgnoreCase) == false ||
                    joinIndex.Name.StartsWith(Constants.Documents.Indexing.SideBySideIndexNamePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var inner in _innerNames)
                {
                    if (string.Equals(joinIndex.InnerName, inner, StringComparison.OrdinalIgnoreCase) == false)
                        continue;

                    result ??= new List<JoinIndex>(1);
                    result.Add(joinIndex);
                    break;
                }
            }

            return result;
        }

        // Opens the snapshot of every join index of the query before the query opens the transaction of any index, so a join index is never ahead of what the query reads. A join index that is
        // behind the indexes is awaited a little. Only reads: the join index is the only writer. Null when no join index is defined for the joins of the query.
        public async ValueTask<JoinQueryLease> OpenJoinsAsync(DocumentDatabase database, IndexQueryServerSide query, Index outer, CancellationToken token)
        {
            query.JoinLease = null;
            var joinIndexes = FindJoins(database, outer);
            if (joinIndexes == null)
                return null;

            var lease = new JoinQueryLease(query, token);
            try
            {
                foreach (var joinIndex in joinIndexes)
                    lease.Entries.Add(await OpenEntryAsync(database, joinIndex, outer, query.WaitForNonStaleResults, token).ConfigureAwait(false));
            }
            catch
            {
                lease.Dispose();
                throw;
            }

            query.JoinLease = lease;
            return lease;
        }

        private static async ValueTask<JoinQueryLease.Entry> OpenEntryAsync(DocumentDatabase database, JoinIndex joinIndex, Index outer, bool waitForNonStale, CancellationToken token)
        {
            if (joinIndex.State == IndexState.Disabled)
                return new JoinQueryLease.Entry(joinIndex, null, JoinFallbacks.Disabled);

            if (joinIndex.State == IndexState.Error)
                return new JoinQueryLease.Entry(joinIndex, null, JoinFallbacks.Faulted);

            var inner = database.IndexStore.GetIndex(joinIndex.InnerName);
            if (inner == null || joinIndex.Status == IndexRunningStatus.Pending)
                return new JoinQueryLease.Entry(joinIndex, null, JoinFallbacks.Unresolved);

            var budget = waitForNonStale ? joinIndex.QueryOptions.WaitForNonStale : joinIndex.QueryOptions.Wait;
            var watch = Stopwatch.StartNew();
            var waited = false;
            try
            {
                while (true)
                {
                    // taken before the snapshot, so a batch that the join index completes in between is not missed
                    var awaiter = joinIndex.GetIndexingBatchAwaiter();
                    if (joinIndex.TryOpenJoinSnapshot(out var snapshot) == false)
                        return new JoinQueryLease.Entry(joinIndex, null, JoinFallbacks.SnapshotUnavailable);

                    if (snapshot.Generation.OuterEnvironmentId == Guid.Empty)
                    {
                        // the lists are being built: they reflect no generation yet
                        snapshot.Dispose();
                        return new JoinQueryLease.Entry(joinIndex, null, JoinFallbacks.Building);
                    }

                    // a batch is counted by the join index after its stats were written, a while after its commit: only the indexes know whether the join index is behind
                    var remaining = budget - watch.Elapsed;
                    if (remaining <= TimeSpan.Zero || joinIndex.Status != IndexRunningStatus.Running || Peek(joinIndex, outer, inner, snapshot.Generation) != JoinServing.Outcome.Behind)
                        return new JoinQueryLease.Entry(joinIndex, snapshot, null);

                    snapshot.Dispose();
                    waited = true;
                    joinIndex.Expedite();
                    await awaiter.WaitAsync(remaining).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                if (waited)
                    joinIndex.QueryCounters.Waited(watch.ElapsedTicks);
            }
        }

        // Compares a generation with what the indexes have consumed now. Neither snapshot is kept
        private static JoinServing.Outcome Peek(JoinIndex joinIndex, Index outer, Index inner, JoinIndexGeneration published)
        {
            joinIndex.QueryCounters.Peeked();
            if (outer.TryOpenSnapshot(out var outerSnapshot) == false)
                return JoinServing.Outcome.OtherHistory;

            using (outerSnapshot)
            {
                if (inner.TryOpenSnapshot(out var innerSnapshot) == false)
                    return JoinServing.Outcome.OtherHistory;

                using (innerSnapshot)
                    return JoinServing.Compare(published, outerSnapshot.EnvironmentId, outerSnapshot.Consumed, innerSnapshot.EnvironmentId, innerSnapshot.Consumed);
            }
        }
    }
}
