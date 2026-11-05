using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Corax;
using Corax.Querying;
using Raven.Server.Config;
using Raven.Client.Documents.Indexes;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Documents.Indexes;
using Index = Raven.Server.Documents.Indexes.Index;
using Raven.Server.Documents.Indexes.Persistence.Corax;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Queries.Timings;
using Raven.Server.Logging;
using Raven.Server.ServerWide;
using Sparrow;
using Sparrow.Logging;
using Sparrow.Server.Logging;
using NativeMemory = Sparrow.Utils.NativeMemory;
using Raven.Server.ServerWide.Context;
using Voron.Impl;

namespace Raven.Server.Documents.Queries
{
    // The joins of a query. Every sub-query is read straight from its index
    internal sealed class JoinSources : IDisposable
    {
        private static readonly RavenLogger Logger = RavenLogManager.Instance.GetLoggerForServer<JoinSources>();

        private readonly List<IDisposable> _disposables = new();

        public List<JoinResult> Results { get; } = new();

        // the index transaction that each join (also of the chains) reads, by the clause of the query
        public Dictionary<JoinClause, TransactionOperationContext> Snapshots { get; } = new();

        // outerIndex and outerTransaction: the index the query runs on and the transaction it reads with
        public static JoinSources Open(DocumentDatabase database, IndexQueryServerSide outerQuery, Index outerIndex, RavenTransaction outerTransaction,
            DocumentsOperationContext documentsContext, QueryTimingsScope timings, bool returnsScores, bool keepWinners, CancellationToken token)
        {
            var sources = new JoinSources();
            try
            {
                foreach (var join in outerQuery.Metadata.Query.Joins)
                    sources.OpenJoin(database, outerQuery, outerIndex, outerTransaction, join, documentsContext, timings, returnsScores, keepWinners, token);

                return sources;
            }
            catch
            {
                sources.Dispose();
                throw;
            }
        }

        private static Index ResolveInnerIndex(DocumentDatabase database, string indexName)
        {
            return database.IndexStore.GetIndex(indexName)
                   ?? throw new IndexDoesNotExistException($"The JOIN sub-query refers to the index '{indexName}', which does not exist.");
        }

        // The index is held after a replacement that took its place was left behind: the replacement registers the new index and then waits for the queries of the old one before it disposes it.
        // A join that meets an index that is disposed with no replacement registered yet waits for the store to register one, and one that meets an index that a replacement holds
        // waits for the replacement to release it. Only a replacement that takes longer than the setting fails the join, and the error says so.
        private static Index HoldInnerIndex(DocumentDatabase database, string name, Index index, out IDisposable inUse)
        {
            var wait = database.Configuration.Queries.JoinSourceWait.AsTimeSpan;
            var watch = Stopwatch.StartNew();
            while (true)
            {
                database.IndexStore.ForTestingPurposes?.BeforeJoinSourceIsHeld?.Invoke(index);
                index = JoinedIndexes.CurrentOf(database, name, index, wait - watch.Elapsed);
                switch (index.TryBeginQueryAsJoinSource(wait - watch.Elapsed, out inUse))
                {
                    case Index.JoinSourceHold.Held:
                        return index;
                    case Index.JoinSourceHold.Disposed:
                        continue;
                    default:
                        throw new InvalidQueryException($"The JOIN sub-query reads the index '{index.Name}', which is being replaced and was not available within {wait.TotalMilliseconds:N0} ms " +
                                                        $"(setting {RavenConfiguration.GetKey(x => x.Queries.JoinSourceWait)}).");
                }
            }
        }

        private void OpenJoin(DocumentDatabase database, IndexQueryServerSide outerQuery, Index outerIndex, RavenTransaction outerTransaction, JoinClause join,
            DocumentsOperationContext documentsContext, QueryTimingsScope timings, bool returnsScores, bool keepWinners, CancellationToken token)
        {
            // the entry of the joined document of a row is read to compare its fields
            keepWinners |= join.Compare != null;
            var allocatedBefore = timings != null ? GC.GetAllocatedBytesForCurrentThread() : 0;
            var nativeBefore = timings != null ? NativeMemory.CurrentThreadStats.TotalAllocated : 0;

            if (join.Query.From.Index == false)
                throw new InvalidQueryException("The JOIN sub-query must query an index (FROM INDEX '...')", outerQuery.Query, outerQuery.QueryParameters);

            var indexName = join.Query.From.From.FieldValue;
            var index = ResolveInnerIndex(database, indexName);

            // the joined index is queried too: it has to stay alive (and not be idle) for as long as this query reads it
            index = HoldInnerIndex(database, indexName, index, out IDisposable inUse);
            _disposables.Add(inUse);

            if (index.SearchEngineType != SearchEngineType.Corax)
                throw new InvalidQueryException($"JOIN is supported only for Corax indexes, but the JOIN sub-query of '{join.Alias}' reads the index '{indexName}', which uses {index.SearchEngineType}.",
                    outerQuery.Query, outerQuery.QueryParameters);

            foreach (var pair in join.Pairs)
            {
                if (pair.InnerField != null && index.Definition.IndexFields.ContainsKey(pair.InnerField) == false)
                    throw new InvalidQueryException($"The JOIN matches on the field '{pair.InnerField}' of '{join.Alias}', but that field is not indexed in the index '{indexName}'.",
                        outerQuery.Query, outerQuery.QueryParameters);
            }

            var innerOrder = InnerOrderOf(join, outerQuery);
            if (innerOrder != null && index.Definition.HasDynamicFields == false)
            {
                foreach (var order in innerOrder)
                {
                    var field = ((FieldExpression)order.Expression).FieldValue;
                    if (index.Definition.IndexFields.ContainsKey(field) == false)
                        throw new InvalidQueryException($"The JOIN orders by the field '{field}' of '{join.Alias}', but that field is not indexed in the index '{indexName}'.", outerQuery.Query, outerQuery.QueryParameters);
                }
            }

            var subQuery = CreateSubQuery(join.Query, outerQuery, innerOrder);
            var joinTimings = timings?.For(join.Alias.Value);
            subQuery.JoinTimings = joinTimings;

            var contextScope = index._contextPool.AllocateOperationContext(out TransactionOperationContext indexContext);
            _disposables.Add(contextScope);

            var transaction = indexContext.OpenReadTransaction();
            _disposables.Add(transaction);
            Snapshots[join] = indexContext;

            var reader = index.IndexPersistence.OpenIndexReader(transaction.InnerTransaction, subQuery);
            _disposables.Add(reader);
            if (reader is not CoraxIndexReadOperation corax)
                throw new InvalidQueryException($"JOIN is supported only for Corax indexes, but '{indexName}' is not one.", outerQuery.Query, outerQuery.QueryParameters);

            // a chain of joins: the sub-query joins other documents itself, those joins have to be read before the sub-query can be
            List<JoinResult> nestedResults = null;
            if (subQuery.Metadata.Query.Joins != null)
            {
                var nested = Open(database, subQuery, index, transaction, documentsContext, joinTimings, false, keepWinners, token);
                _disposables.Add(nested);
                foreach ((JoinClause nestedJoin, TransactionOperationContext nestedContext) in nested.Snapshots)
                    Snapshots[nestedJoin] = nestedContext;
                subQuery.JoinResults = nestedResults = nested.Results;
            }

            JoinTermScores keys;
            JoinOrderedKeys orderedKeys = null;

            // the best joined document of a key is projected; the documents of a join on the id of the main document are looked up for every row, not taken from the winners
            var keepsWinner = join.ProjectsDocument && join.On.Left is not MethodExpression;
            try
            {
                using (joinTimings?.For(nameof(QueryTimingsScope.Names.Scan)))
                {
                    if (innerOrder != null)
                    {
                        // the rank sorts the main query, but the score it returns (and the score() that the order by may use) is the relevance
                        var scoreQuery = returnsScores || outerQuery.Metadata.OrderBy.Any(x => x.OrderingType == OrderByFieldType.Score) ? CreateSubQuery(join.Query, outerQuery, null) : null;
                        if (scoreQuery != null)
                            scoreQuery.JoinTimings = joinTimings;
                        if (join.InnerField != null)
                            keys = corax.ScanTermRanks(subQuery, documentsContext, scoreQuery, join.InnerField, keepsWinner, token);
                        else if (scoreQuery == null && OrderedWalkMayApply(join, outerQuery, keepWinners))
                            keys = corax.ScanIdsInOrder(subQuery, documentsContext, JoinOrderedWalkSettings.MinimumKeys, out orderedKeys, token);
                        else
                            keys = corax.ScanIdsAndRanks(subQuery, documentsContext, scoreQuery, token);
                    }
                    else if (join.IsComposite)
                        keys = corax.ScanCompositeKeys(subQuery, documentsContext, join.Pairs.Select(x => x.InnerField).ToArray(), token);
                    else if (join.InnerField != null)
                        keys = corax.ScanTermScores(subQuery, documentsContext, join.InnerField, CoraxIndexReadOperation.JoinFieldScan.Auto, keepsWinner, token);
                    else
                        keys = corax.ScanIdsAndScores(subQuery, documentsContext, keepWinners, OuterKeyTable(outerQuery, outerIndex, outerTransaction?.InnerTransaction.LowLevelTransaction, join, index), token);
                }
            }
            catch (JoinKeysMemoryExceededException e)
            {
                throw new InvalidQueryException($"The keys of the JOIN sub-query of '{join.Alias}' hold more than {database.Configuration.Queries.JoinMaxKeysMemory} " +
                                                $"(setting {RavenConfiguration.GetKey(x => x.Queries.JoinMaxKeysMemory)}), the sub-query matched more than {e.Keys:N0} documents; narrow the sub-query or raise the setting.",
                    outerQuery.Query, outerQuery.QueryParameters, e);
            }

            JoinTermScores firstTerms = null;
            string[] pairProbes = null, pairOuterPaths = null;
            FieldIndexingMode[] pairModes = null;
            if (join.IsComposite)
            {
                firstTerms = CompositeJoinKeys.FirstTerms(corax.Allocator, keys);
                var pairs = join.Pairs.ToArray();
                pairProbes = new string[pairs.Length];
                pairModes = new FieldIndexingMode[pairs.Length];
                pairOuterPaths = new string[pairs.Length];
                for (var i = 0; i < pairs.Length; i++)
                {
                    pairProbes[i] = corax.AnalyzeJoinKeyProbe(pairs[i].InnerField, out pairModes[i]);
                    pairOuterPaths[i] = OuterPathOf(pairs[i].On, outerQuery);
                }
            }

            // from here on the result owns the keys
            var result = new JoinResult
            {
                Alias = join.Alias.Value,
                RootAlias = (join.RootAlias ?? join.Alias).Value,
                Nested = nestedResults,
                Keys = keys,
                KeysAreTerms = join.InnerField != null,
                RankedByInnerOrder = innerOrder != null,
                OrderedKeys = orderedKeys,
                KeyProbe = corax.AnalyzeJoinKeyProbe(join.InnerField, out var innerMode),
                CompositeFirstTerms = firstTerms,
                PairKeyProbes = pairProbes,
                PairInnerModes = pairModes,
                PairOuterPaths = pairOuterPaths,
                InnerAllocator = corax.Allocator,
                InnerIndexName = indexName,
                InnerIndexingMode = innerMode,
                Timings = joinTimings,
                Reader = join.InnerField == null || join.ProjectsDocument ? corax : null,
                OuterPath = OuterPathOf(join.On, outerQuery),
                IsOptional = (join.ReferencedInWhere || join.IsLeft) && join.InnerField == null,
                IsLeft = join.IsLeft,
                Boost = BoostOf(join, outerQuery),
                Compare = join.Compare,
                OnFieldName = keepWinners && join.InnerField == null ? QueryBuilderHelper.ExtractIndexFieldName(outerQuery.Metadata.Query, outerQuery.QueryParameters, join.On.Left, outerQuery.Metadata) : null,
                SubQuery = subQuery,
                InnerIndex = index,
                DocumentsContext = documentsContext,
                InnerField = join.InnerField,
                IsOnIdOfMain = join.On.Left is MethodExpression
            };
            _disposables.Add(result);

            // the statistics of the join come back with the query timings
            var scanBytes = timings != null ? GC.GetAllocatedBytesForCurrentThread() - allocatedBefore : 0;
            joinTimings?.AddCounter("SourceMatches", keys.Count);
            joinTimings?.AddCounter("KeysEncoded", keys.IsEncoded ? 1 : 0);
            joinTimings?.AddCounter("KeysHeldBytes", keys.HeldBytes);
            joinTimings?.AddCounter("ScanAllocatedManagedBytes", scanBytes);
            joinTimings?.AddCounter("ScanHeldUnmanagedBytes", timings != null ? NativeMemory.CurrentThreadStats.TotalAllocated - nativeBefore : 0);

            result.ScanAllocatedManagedBytes = scanBytes;
            Results.Add(result);

            // reading this join is over: stop its clock (counters can still be added to the scope later)
            joinTimings?.Dispose();
        }

        // The table of the dictionary of the outer index when the ids of the join can be kept encoded for it, null when they are decoded
        private static ulong? OuterKeyTable(IndexQueryServerSide outerQuery, Index outerIndex, LowLevelTransaction outerTransaction, JoinClause join, Index innerIndex)
        {
            if (outerIndex == null || outerTransaction == null || join.On.Left is not FieldExpression field)
                return null;

            var outerField = QueryBuilderHelper.ExtractIndexFieldName(outerQuery.Metadata.Query, outerQuery.QueryParameters, field, outerQuery.Metadata).Value;
            return JoinEncodedKeys.KeysReachTreeUnchanged(innerIndex, outerIndex, outerField) ? JoinEncodedKeys.TableOf(outerTransaction) : null;
        }

        // The outer query can answer a join that orders the result by walking the joined index in that order, when the page is small
        private static bool OrderedWalkMayApply(JoinClause join, IndexQueryServerSide outerQuery, bool keepWinners)
        {
            var query = outerQuery.Metadata.Query;
            if (JoinOrderedWalkSettings.Enabled == false || keepWinners || outerQuery.IsJoinSource)
                return false;

            if (query.Joins.Count != 1 || join.IsLeft || join.IsComposite || join.ReferencedInWhere || join.Compare != null || join.Query.Joins != null)
                return false;

            if (outerQuery.PageSize <= 0 || (long)outerQuery.Start + outerQuery.PageSize > JoinOrderedWalkSettings.MaximumTake)
                return false;

            foreach (var order in outerQuery.Metadata.OrderBy)
            {
                if (order.JoinAlias != join.Alias.Value)
                    return false;
            }

            return true;
        }

        // BOOST of the join: a non-negative finite number, or a parameter that holds one
        private static float BoostOf(JoinClause join, IndexQueryServerSide outerQuery)
        {
            if (join.Boost == null)
                return 1f;

            var metadata = outerQuery.Metadata;
            var (value, type) = QueryBuilderHelper.GetValue(metadata.Query, metadata, outerQuery.QueryParameters, join.Boost);
            double boost;
            switch (value)
            {
                case float f:
                    boost = f;
                    break;
                case double d:
                    boost = d;
                    break;
                case long l:
                    boost = l;
                    break;
                default:
                    throw new InvalidQueryException($"The BOOST of the JOIN with '{join.Alias}' must be a number, but it is {value} ({type}).", outerQuery.Query, outerQuery.QueryParameters);
            }

            if (double.IsFinite(boost) == false || boost < 0)
                throw new InvalidQueryException($"The BOOST of the JOIN with '{join.Alias}' must be a number that is not negative, but it is {boost}.", outerQuery.Query, outerQuery.QueryParameters);

            return (float)boost;
        }

        private static string OuterPathOf(BinaryExpression on, IndexQueryServerSide outerQuery)
        {
            if (on.Left is not FieldExpression field)
                return null;

            return QueryMetadata.ParseExpressionPath(field, field.FieldValue, outerQuery.Metadata.Query.From.Alias).Path;
        }

        // The ORDER BY fields of the main query that belong to this join, as the ORDER BY of the sub-query
        private static List<(QueryExpression Expression, OrderByFieldType FieldType, bool Ascending, NullsOrderingType NullsOrdering)> InnerOrderOf(JoinClause join, IndexQueryServerSide outerQuery)
        {
            var orderBy = outerQuery.Metadata.OrderBy;
            if (orderBy == null)
                return null;

            List<(QueryExpression Expression, OrderByFieldType FieldType, bool Ascending, NullsOrderingType NullsOrdering)> result = null;
            var lastIndex = -1;
            string otherJoin = null;
            for (var i = 0; i < orderBy.Length; i++)
            {
                var field = orderBy[i];
                if (field.JoinAlias == null)
                    continue;

                if (field.JoinAlias != join.Alias.Value)
                {
                    otherJoin = field.JoinAlias;
                    continue;
                }

                if (otherJoin != null)
                    throw Reject($"ORDER BY joined fields of two joins ('{otherJoin}' and '{join.Alias}') is not supported, only one join can order the result");

                if (lastIndex != -1 && lastIndex != i - 1)
                    throw Reject($"the ORDER BY fields of '{join.Alias}' have to follow each other, other fields cannot be sorted between them");

                if (join.ReferencedInWhere)
                    throw Reject($"ORDER BY {join.Alias}.{field.Name} cannot be combined with matches({join.Alias}) in the where clause: a row that only matches the other side of the OR has no position in the order of the join");

                if (join.IsComposite)
                    throw Reject($"ORDER BY {join.Alias}.{field.Name} is not supported because '{join.Alias}' joins on several fields");

                if (join.Query.Joins != null)
                    throw Reject($"ORDER BY {join.Alias}.{field.Name} is not supported because the sub-query of '{join.Alias}' has joins of its own (a chain of joins)");

                if (field.OrderingType is OrderByFieldType.Custom or OrderByFieldType.Random or OrderByFieldType.Distance or OrderByFieldType.Score)
                    throw Reject($"ORDER BY {join.Alias}.{field.Name} cannot use the {field.OrderingType} ordering");

                lastIndex = i;
                result ??= [];
                var name = field.Name.Value;
                var path = new List<StringSegment>();
                for (var start = 0; start <= name.Length;)
                {
                    var dot = name.IndexOf('.', start);
                    var end = dot == -1 ? name.Length : dot;
                    path.Add(new StringSegment(name, start, end - start));
                    start = end + 1;
                }

                result.Add((new FieldExpression(path) { IsQuoted = field.Name.IsQuoted }, field.OrderingType, field.Ascending, field.NullsOrdering));
            }

            if (result != null && otherJoin != null)
                throw Reject($"ORDER BY joined fields of two joins ('{otherJoin}' and '{join.Alias}') is not supported, only one join can order the result");

            return result;

            InvalidQueryException Reject(string reason) => new InvalidQueryException($"ORDER BY a joined field is not supported here: {reason}.", outerQuery.Query, outerQuery.QueryParameters);
        }

        private static IndexQueryServerSide CreateSubQuery(Query subQuery, IndexQueryServerSide outerQuery,
            List<(QueryExpression Expression, OrderByFieldType FieldType, bool Ascending, NullsOrderingType NullsOrdering)> orderBy)
        {
            // only the matching documents and their scores matter
            subQuery = subQuery.ShallowCopy();
            subQuery.OrderBy = orderBy;
            subQuery.Select = null;
            subQuery.SelectFunctionBody = default;
            subQuery.Load = null;
            subQuery.Include = null;

            return new IndexQueryServerSide(new QueryMetadata(subQuery, outerQuery.QueryParameters, cacheKey: 0, addSpatialProperties: false))
            {
                Query = subQuery.QueryText,
                QueryParameters = outerQuery.QueryParameters,
                IsJoinSource = true,
                SkipDuplicateChecking = outerQuery.SkipDuplicateChecking
            };
        }

        public void Dispose()
        {
            for (var i = _disposables.Count - 1; i >= 0; i--)
                _disposables[i].Dispose();

            _disposables.Clear();
        }
    }
}
