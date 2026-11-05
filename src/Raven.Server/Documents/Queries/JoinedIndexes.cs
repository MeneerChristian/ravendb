using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes;
using Index = Raven.Server.Documents.Indexes.Index;
using Raven.Server.Documents.Queries.AST;
using Sparrow.Server;

namespace Raven.Server.Documents.Queries
{
    internal sealed class JoinedIndexes
    {
        private readonly List<Index> _indexes = new();
        private DocumentDatabase _database;
        private Dictionary<Index, (long DocumentCutoff, long? ReferenceCutoff, long? CompareExchangeReferenceCutoff)> _cutoffs;

        // Null when the query has no joins
        public static JoinedIndexes Create(DocumentDatabase database, Query query)
        {
            if (query.Joins == null)
                return null;

            var result = new JoinedIndexes { _database = database };
            result.Add(database, query);
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
    }
}
