using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Corax;
using Corax.Querying;
using Corax.Mappings;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;
using Corax.Querying.Matches.SortingMatches;
using Corax.Querying.Matches.SortingMatches.Meta;
using Corax.Utils;
using Voron.Data.CompactTrees;
using Voron.Util;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Queries.Explanation;
using Raven.Client.Documents.Queries.MoreLikeThis;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Corax;
using Raven.Server.Documents.Indexes.Debugging;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Raven.Server.Documents.Indexes.Static.Spatial;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Queries.Highlightings;
using Raven.Server.Documents.Queries.MoreLikeThis.Corax;
using Raven.Server.Documents.Queries.Results;
using Raven.Server.Documents.Queries.Timings;
using Raven.Server.Indexing;
using Raven.Server.Json;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Json;
using Sparrow.Server;
using Sparrow.Server.Utils.VxSort;
using Voron;
using Voron.Impl;
using Constants = Raven.Client.Constants;
using CoraxConstants = Corax.Constants;
using IndexSearcher = Corax.Querying.IndexSearcher;
using CoraxSpatialResult = global::Corax.Utils.Spatial.SpatialResult;
using Sparrow.Server.Logging;
using Voron.Data.Graphs;
using IndexFieldType = Raven.Server.Documents.Indexes.Debugging.IndexFieldType;

namespace Raven.Server.Documents.Indexes.Persistence.Corax
{
    public class CoraxIndexReadOperation : IndexReadOperationBase
    {
        // PERF: This is a hack in order to deal with RavenDB-19597. The ArrayPool creates contention under high requests environments.
        // There are 2 ways to avoid this contention, one is to avoid using it altogether and the other one is separating the pools from
        // the actual executing thread. While the correct approach would be to amp-up the usage of shared buffers (which would make) this
        // hack irrelevant, the complexity it introduces is much greater than what it make sense to be done at the moment. Therefore, 
        // we are building a quick fix that allow us to avoid the locking convoys and we will defer the real fix to RavenDB-19665. 
        [ThreadStatic] 
        private static ArrayPool<long> _queryPool;
        
        [ThreadStatic] 
        private static ArrayPool<float> _queryScorePool;

        [ThreadStatic] 
        private static ArrayPool<CoraxSpatialResult> _queryDistancePool;

        [ThreadStatic]
        private static ArrayPool<byte> _queryIdBytesPool;


        public static ArrayPool<long> QueryPool
        {
            get
            {
                _queryPool ??= ArrayPool<long>.Create();
                return _queryPool;
            }
        }

        private static ArrayPool<float> ScorePool
        {
            get
            {
                _queryScorePool ??= ArrayPool<float>.Create();
                return _queryScorePool;
            }
        }

        private static ArrayPool<CoraxSpatialResult> DistancePool
        {
            get
            {
                _queryDistancePool ??= ArrayPool<CoraxSpatialResult>.Create();
                return _queryDistancePool;
            }
        }

        private static ArrayPool<byte> IdBytesPool
        {
            get
            {
                _queryIdBytesPool ??= ArrayPool<byte>.Create();
                return _queryIdBytesPool;
            }
        }

        protected readonly IndexSearcher IndexSearcher;

        internal IndexSearcher Searcher => IndexSearcher;

        private readonly IndexFieldsMapping _fieldMappings;

        private Dictionary<string, VectorChunkHighlightingCapture> _vectorChunkHighlightings;

        internal void CaptureVectorChunkHighlighting(string fieldName, string taskId, float minimumSimilarity, List<byte[]> queryVectors)
        {
            _vectorChunkHighlightings ??= new Dictionary<string, VectorChunkHighlightingCapture>(StringComparer.OrdinalIgnoreCase);

            // Multiple vector.search() calls can target the same field (e.g. searching for several query texts). They all
            // resolve against the same embeddings, so merge their query vectors instead of letting the last call overwrite
            // the earlier ones - otherwise only chunks near the last query text would be surfaced.
            if (_vectorChunkHighlightings.TryGetValue(fieldName, out VectorChunkHighlightingCapture existing) && existing.TaskId == taskId)
            {
                existing.QueryVectors.AddRange(queryVectors);
                // The merged vectors are matched as a union (nearest of any of them), so keep the least restrictive
                // threshold - a chunk that satisfied one of the clauses must not be dropped because another was stricter.
                existing.RelaxMinimumSimilarity(minimumSimilarity);
                return;
            }

            _vectorChunkHighlightings[fieldName] = new VectorChunkHighlightingCapture(fieldName, taskId, minimumSimilarity, queryVectors);
        }
        private readonly ByteStringContext _allocator;
        private readonly LowLevelTransaction _lowLevelTransaction;

        private readonly int _maxNumberOfOutputsPerDocument;

        private TermsReader _documentIdReader;

        public override bool IsSharded => false;

        public CoraxIndexReadOperation(Index index, RavenLogger logger, Transaction readTransaction, QueryBuilderFactories queryBuilderFactories, IndexFieldsMapping fieldsMapping, IndexQueryServerSide query) : base(index, logger, queryBuilderFactories, query)
        {
            _allocator = readTransaction.Allocator;
            _lowLevelTransaction = readTransaction.LowLevelTransaction;
            _fieldMappings = fieldsMapping;
            IndexSearcher = new IndexSearcher(readTransaction, _fieldMappings)
            {
                MaxMemoizationSizeInBytes = index.Configuration.MaxMemoizationSize.GetValue(SizeUnit.Bytes),
            };

            // Pick up the per-field HNSW vector node caches that CoraxIndexPersistence attached
            // to this transaction's ImmutableExternalState at creation time. The transaction
            // holds the only reference the IndexSearcher needs; once the transaction disposes
            // and no other transaction is still keeping the cache alive, GC reclaims it.
            if (readTransaction.LowLevelTransaction.ImmutableExternalState is IndexTransactionCache txCache
                && txCache.VectorNodeCaches is { Count: > 0 } vectorCaches)
            {
                IndexSearcher.AttachVectorNodeCaches(vectorCaches);
            }

            if (index is {_forTestingPurposes: {CoraxConfiguration: not null}})
                IndexSearcher.SetTestingConfiguration(index._forTestingPurposes.CoraxConfiguration);

            var primaryKey = index.Type.IsMap()
                ? Constants.Documents.Indexing.Fields.DocumentIdFieldName
                : Constants.Documents.Indexing.Fields.ReduceKeyHashFieldName; // map reduce
            _documentIdReader = IndexSearcher.TermsReaderFor(primaryKey);
            _maxNumberOfOutputsPerDocument = index.MaxNumberOfOutputsPerDocument;
        }

        public override long EntriesCount() => IndexSearcher.NumberOfEntries;

        private string _storedValueDocumentId;
        private long _storedValueEntryId = -1;
        private CompactKey _storedValueKey;

        // The key of the entry terms reader of the query results
        private CompactKey _entryTermsKey;

        internal bool TryGetStoredValue(JsonOperationContext context, string documentId, string fieldName, out object value)
        {
            value = null;

            if (_index.Type.IsMap() == false ||
                _index.Definition.IndexFields.TryGetValue(fieldName, out var field) == false ||
                field.Storage != FieldStorage.Yes)
                return false;

            if (string.Equals(_storedValueDocumentId, documentId, StringComparison.Ordinal) == false)
            {
                var idField = _fieldMappings.GetByFieldId(0).Metadata.ChangeAnalyzer(FieldIndexingMode.Exact, null);
                var match = IndexSearcher.TermQuery(idField, documentId);
                Span<long> ids = stackalloc long[2];
                var read = match.Fill(ids);
                _storedValueDocumentId = documentId;
                _storedValueEntryId = read == 1 ? ids[0] : -1;
            }

            if (_storedValueEntryId < 0)
                return false;

            // the reader initializes the key
            _storedValueKey ??= new CompactKey();
            Page page = default;
            var termsReader = IndexSearcher.GetEntryTermsReader(_storedValueEntryId, ref page, _storedValueKey);
            var input = new RetrieverInput(IndexSearcher, _fieldMappings, in termsReader, documentId, _index.IndexFieldsPersistence.HasTimeValues);
            try
            {
                return QueryResultRetrieverBase.TryGetValueFromCoraxIndex(context, field.Name ?? field.OriginalName, ref input, out value);
            }
            finally
            {
                _storedValueKey.Reset();
            }
        }

        // the keys of a join are held in native memory while the query runs: the server bounds them
        private JoinTermScores Bounded(JoinTermScores keys)
        {
            keys.MaxHeldBytes = _index.DocumentDatabase.Configuration.Queries.JoinMaxKeysMemory.GetValue(SizeUnit.Bytes);
            return keys;
        }

        internal JoinTermScores ScanIdsAndScores(IndexQueryServerSide query, DocumentsOperationContext documentsContext, CancellationToken token)
            => ScanIdsAndScores(query, documentsContext, keepWinners: false, outerKeyTable: null, token);

        // outerKeyTable: the table of the dictionary of the index that is joined with the ids. When it is the table of this index the ids are kept encoded
        internal JoinTermScores ScanIdsAndScores(IndexQueryServerSide query, DocumentsOperationContext documentsContext, bool keepWinners, ulong? outerKeyTable, CancellationToken token)
        {
            var keepEncoded = keepWinners == false && outerKeyTable != null && outerKeyTable == DocumentIdTableHash();
            var output = keepEncoded
                ? Bounded(JoinTermScores.CreateEncoded(_allocator, _lowLevelTransaction, _documentIdReader.DictionaryId))
                : Bounded(new JoinTermScores(_allocator, keepWinners));
            ContextBoundNativeList<long> ids = default;
            ContextBoundNativeList<float> scores = default;
            try
            {
                if (TryScoreSubQuery(query, documentsContext, out ids, out scores, token) == false)
                    return output;

                if (keepEncoded)
                    EncodedDocumentIdsWithScores(ids.ToSpan(), scores.ToSpan(), output);
                else
                    DocumentIdsWithScores(ids.ToSpan(), scores.ToSpan(), output, winners: keepWinners);
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
            finally
            {
                ids.Dispose();
                scores.Dispose();
            }
        }

        private IQueryMatch BuildSubQuery(IndexQueryServerSide query, DocumentsOperationContext documentsContext, out OrderMetadata[] sortMetadata, CancellationToken token)
        {
            var fieldsToFetch = new FieldsToFetch(query, _index.Definition, _index.Type);
            var builderParameters = new CoraxQueryBuilder.Parameters(IndexSearcher, _allocator, serverContext: null, documentsContext, query, _index, query.QueryParameters,
                QueryBuilderFactories, _fieldMappings, fieldsToFetch, highlightingTerms: null, take: CoraxConstants.IndexSearcher.TakeAll, deduplicationDisabled: false,
                indexReadOperation: this, token: token);

            return CoraxQueryBuilder.BuildQuery(builderParameters, out sortMetadata);
        }

        // The ids of every entry the sub-query matches and their scores, in the native memory of the transaction
        private bool TryScoreSubQuery(IndexQueryServerSide query, DocumentsOperationContext documentsContext, out ContextBoundNativeList<long> ids,
            out ContextBoundNativeList<float> scores, CancellationToken token)
        {
            ids = default;
            scores = default;
            var queryMatch = BuildSubQuery(query, documentsContext, out _, token);
            if (queryMatch is null)
                return false;

            ids = ReadAllEntries(queryMatch, token);
            try
            {
                if (ids.Count == 0)
                    return false;

                Sort.Run(ids.ToSpan());

                scores = new ContextBoundNativeList<float>(_allocator);
                scores.GrowFitting(ids.Count + 1);
                scores.Count = ids.Count;
                scores.ToSpan().Fill(Bm25Relevance.InitialScoreValue);
                queryMatch.Score(ids.ToSpan(), scores.ToSpan(), 1f);

                return true;
            }
            catch
            {
                ids.Dispose();
                scores.Dispose();
                throw;
            }
        }

        // The documents of the entries that the sub-query matches and whose field holds this term (as the index stores it), added to the list in the order of their ids
        internal void AddDocumentIdsOfTerm(IndexQueryServerSide query, DocumentsOperationContext documentsContext, string field, ReadOnlySpan<byte> term, List<string> documentIds,
            CancellationToken token)
        {
            if (_fieldMappings.TryGetByFieldName(_allocator, field, out var binding) == false)
                return;

            var queryMatch = BuildSubQuery(query, documentsContext, out _, token);
            if (queryMatch is null)
                return;

            // the term is the one the index stores: it is looked up as it is
            using var termScope = Slice.From(_allocator, term, ByteStringType.Immutable, out var termSlice);
            var termMatch = IndexSearcher.TermQuery(binding.Metadata.ChangeAnalyzer(FieldIndexingMode.Exact, null), termSlice);
            var entries = ReadAllEntries(IndexSearcher.And<IQueryMatch, IQueryMatch>(termMatch, queryMatch, token), token);
            try
            {
                var first = documentIds.Count;
                foreach (var entry in entries.ToSpan())
                    documentIds.Add(GetDocumentIdOfEntry(entry));

                documentIds.Sort(first, documentIds.Count - first, StringComparer.Ordinal);
            }
            finally
            {
                entries.Dispose();
            }
        }

        // The term that a query on this index looks for when it asks for a value of a field: analyzed the way the index analyzes the field. No term when the index has no such field, a split when the analyzer does not make one term of the value
        internal EncodedTerm EncodeTerm(string field, string text)
        {
            if (_fieldMappings.TryGetByFieldName(_allocator, field, out var binding) == false)
                return default;

            return IndexSearcher.TryEncodeAndApplyAnalyzer(binding.Metadata, text, out var term)
                ? new EncodedTerm(term.AsReadOnlySpan().ToArray())
                : EncodedTerm.Split;
        }

        // The score the sub-query gives to the entries it matches, scored the way the scan of the sub-query scores them: uniform when every entry has the score of the first
        internal JoinScoreProbe ProbeSubQueryScore(IndexQueryServerSide query, DocumentsOperationContext documentsContext, CancellationToken token)
        {
            if (TryScoreSubQuery(query, documentsContext, out var ids, out var scores, token) == false)
                return default;

            try
            {
                var span = scores.ToSpan();
                var first = span[0];
                for (var i = 1; i < span.Length; i++)
                {
                    if (span[i] != first)
                        return new JoinScoreProbe(hasMatches: true, isUniform: false, first);
                }

                return new JoinScoreProbe(hasMatches: true, isUniform: true, first);
            }
            finally
            {
                ids.Dispose();
                scores.Dispose();
            }
        }

        // The probe key of a JOIN as this index stores it
        internal string AnalyzeJoinKeyProbe(string field, out FieldIndexingMode mode)
        {
            mode = FieldIndexingMode.Normal;
            IndexFieldBinding binding;
            if (field == null)
                binding = _fieldMappings.GetByFieldId(CoraxConstants.IndexWriter.PrimaryKeyFieldId);
            else if (_fieldMappings.TryGetByFieldName(_allocator, field, out binding) == false)
                return null;

            mode = binding.FieldIndexingMode;
            return CoraxQueryBuilder.AnalyzeJoinKeyProbe(IndexSearcher, binding.Metadata);
        }

        // The ids of every entry the match returns, in the native memory of the transaction
        private ContextBoundNativeList<long> ReadAllEntries(IQueryMatch queryMatch, CancellationToken token)
        {
            var entries = new ContextBoundNativeList<long>(_allocator);
            const int bufferSize = 4096;
            var buffer = QueryPool.Rent(bufferSize);
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    var read = queryMatch.Fill(buffer.AsSpan(0, bufferSize));
                    if (read == 0)
                        break;

                    // doubles in blocks that fill what the allocator keeps for them (a power of two takes twice as much)
                    if (entries.Count + read >= entries.Capacity)
                        entries.GrowFitting(Math.Max(entries.Count + read + 1, (int)Math.Min(entries.Capacity * 2L, int.MaxValue / 16)));

                    entries.AddRange(new ReadOnlySpan<long>(buffer, 0, read));
                }
            }
            catch
            {
                entries.Dispose();
                throw;
            }
            finally
            {
                QueryPool.Return(buffer);
            }

            return entries;
        }

        // The sub-query of a JOIN that orders by its own index
        private float[] RankSubQueryEntries(IndexQueryServerSide query, DocumentsOperationContext documentsContext, out ContextBoundNativeList<long> entries, CancellationToken token)
        {
            entries = default;
            try
            {
                if (ReadOrderedSubQueryEntries(query, documentsContext, out entries, out var sortMetadata, token) == false)
                    return null;

                return RankOrderedEntries(query, sortMetadata, entries);
            }
            catch
            {
                entries.Dispose();
                throw;
            }
        }

        // The ids of the entries the sub-query matches, in the order of the sub-query
        private bool ReadOrderedSubQueryEntries(IndexQueryServerSide query, DocumentsOperationContext documentsContext, out ContextBoundNativeList<long> entries,
            out OrderMetadata[] sortMetadata, CancellationToken token)
        {
            entries = default;
            var stopwatch = Stopwatch.StartNew();
            var queryMatch = BuildSubQuery(query, documentsContext, out sortMetadata, token);
            if (queryMatch is null)
                return false;

            entries = ReadAllEntries(queryMatch, token);
            query.JoinTimings?.AddCounter("RankSortAndFillMicroseconds", stopwatch.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
            return true;
        }

        // The dense rank of every entry (in the order of the sub-query); the entries are then sorted by id, each one keeps its rank
        private float[] RankOrderedEntries(IndexQueryServerSide query, OrderMetadata[] sortMetadata, ContextBoundNativeList<long> entries)
        {
            const int MaxExactRank = 1 << 24; // a float holds every integer up to here
            ContextBoundNativeList<bool> tiedWithPrevious = default;
            float[] ranks = null;
            long[] keys = null;
            try
            {
                var stopwatch = Stopwatch.StartNew();

                // the entries come back in the order of the sub-query
                var entriesSpan = entries.ToSpan();
                ranks = ScorePool.Rent(entriesSpan.Length);
                keys = QueryPool.Rent(entriesSpan.Length);
                tiedWithPrevious = new ContextBoundNativeList<bool>(_allocator, entriesSpan.Length);
                tiedWithPrevious.Count = entriesSpan.Length;

                var ranksSpan = ranks.AsSpan(0, entriesSpan.Length);
                var keysSpan = keys.AsSpan(0, entriesSpan.Length);
                var tiedSpan = tiedWithPrevious.ToSpan();
                tiedSpan.Fill(true);
                foreach (var order in sortMetadata ?? [])
                {
                    IndexSearcher.ReadSortKeys(order, entriesSpan, keysSpan);
                    for (var i = 1; i < keysSpan.Length; i++)
                        tiedSpan[i] &= keysSpan[i] == keysSpan[i - 1];
                }

                query.JoinTimings?.AddCounter("RankSortKeysMicroseconds", stopwatch.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);

                var rank = 0;
                for (var i = 0; i < ranksSpan.Length; i++)
                {
                    if (i == 0 || tiedSpan[i] == false)
                        rank++;

                    ranksSpan[i] = rank;
                }

                if (rank > MaxExactRank)
                    throw new InvalidQueryException($"ORDER BY of a joined index is supported for up to {MaxExactRank} distinct values, but the JOIN sub-query has {rank}.", query.Query, query.QueryParameters);

                // the bulk readers need the ids in order: the ids are sorted in place, each one keeps its rank
                MemoryExtensions.Sort(entriesSpan, ranksSpan);
                var result = ranks;
                ranks = null;
                return result;
            }
            finally
            {
                if (ranks != null)
                    ScorePool.Return(ranks);
                if (keys != null)
                    QueryPool.Return(keys);
                tiedWithPrevious.Dispose();
            }
        }

        // The sub-query side of a JOIN on the id that orders by its own index, when the outer query may read only the first entries in that order:
        // the keys are not ranked (ranks are made when the outer query needs them), and the order is kept. Null when the sub-query has too few entries for that.
        internal JoinTermScores ScanIdsInOrder(IndexQueryServerSide query, DocumentsOperationContext documentsContext, int minimumKeys, out JoinOrderedKeys ordered, CancellationToken token)
        {
            ordered = null;
            var output = Bounded(new JoinTermScores(_allocator));
            ContextBoundNativeList<long> entries = default;
            ContextBoundNativeList<long> sorted = default;
            ContextBoundNativeList<int> positions = default;
            ContextBoundNativeList<int> keyAt = default;
            float[] scores = null;
            float[] ranks = null;
            try
            {
                if (ReadOrderedSubQueryEntries(query, documentsContext, out entries, out var sortMetadata, token) == false)
                {
                    output.ScoresAreRanks = true;
                    return output;
                }

                var count = entries.Count;
                if (count < minimumKeys)
                {
                    // too small for the walk to pay: the ranks as always
                    output.ScoresAreRanks = true;
                    ranks = RankOrderedEntries(query, sortMetadata, entries);
                    DocumentIdsWithScores(entries.ToSpan(), ranks.AsSpan(0, count), output);
                    return output;
                }

                // the bulk readers need the ids in order, the walk needs the order: the position of every key in it
                sorted = new ContextBoundNativeList<long>(_allocator, count);
                sorted.AddRange(entries.ToSpan());
                positions = new ContextBoundNativeList<int>(_allocator, count);
                for (var i = 0; i < count; i++)
                    positions.Add(i);
                MemoryExtensions.Sort(sorted.ToSpan(), positions.ToSpan());

                // nothing looks at the scores of the keys until the ranks replace them
                scores = ScorePool.Rent(count);
                scores.AsSpan(0, count).Clear();
                DocumentIdsWithScores(sorted.ToSpan(), scores.AsSpan(0, count), output);
                if (output.Count != count)
                    throw new InvalidOperationException($"The sub-query has {count} entries but {output.Count} document ids.");

                keyAt = new ContextBoundNativeList<int>(_allocator, count);
                keyAt.Count = count;
                var positionSpan = positions.ToSpan();
                for (var key = 0; key < count; key++)
                    keyAt[positionSpan[key]] = key;

                ordered = new JoinOrderedKeys(IndexSearcher, sortMetadata, entries, keyAt);
                entries = default;
                keyAt = default;
                return output;
            }
            catch
            {
                output.Dispose();
                keyAt.Dispose();
                throw;
            }
            finally
            {
                entries.Dispose();
                sorted.Dispose();
                positions.Dispose();
                if (scores != null)
                    ScorePool.Return(scores);
                if (ranks != null)
                    ScorePool.Return(ranks);
            }
        }

        // The relevance the sub-query gives every one of the entries, in step with them
        private void RelevanceOfEntries(IndexQueryServerSide scoreQuery, DocumentsOperationContext documentsContext, ReadOnlySpan<long> entries, Span<float> relevance, CancellationToken token)
        {
            ContextBoundNativeList<long> scoredIds = default;
            ContextBoundNativeList<float> sourceScores = default;
            try
            {
                relevance.Fill(Bm25Relevance.InitialScoreValue);
                if (TryScoreSubQuery(scoreQuery, documentsContext, out scoredIds, out sourceScores, token) == false)
                    return;

                var ids = scoredIds.ToSpan();
                var from = 0;
                for (var i = 0; i < entries.Length && from < ids.Length; i++)
                {
                    var found = ids.Slice(from).BinarySearch(entries[i]);
                    if (found < 0)
                    {
                        from += ~found;
                        continue;
                    }

                    relevance[i] = sourceScores[from + found];
                    from += found + 1;
                }
            }
            finally
            {
                scoredIds.Dispose();
                sourceScores.Dispose();
            }
        }

        // The sub-query side of a JOIN on the id that orders by its own index
        internal JoinTermScores ScanIdsAndRanks(IndexQueryServerSide query, DocumentsOperationContext documentsContext, CancellationToken token)
            => ScanIdsAndRanks(query, documentsContext, scoreQuery: null, token);

        internal JoinTermScores ScanIdsAndRanks(IndexQueryServerSide query, DocumentsOperationContext documentsContext, IndexQueryServerSide scoreQuery, CancellationToken token)
        {
            var output = Bounded(new JoinTermScores(_allocator, keepRanks: scoreQuery != null) { ScoresAreRanks = scoreQuery == null });
            ContextBoundNativeList<long> entries = default;
            ContextBoundNativeList<float> relevance = default;
            float[] ranks = null;
            try
            {
                ranks = RankSubQueryEntries(query, documentsContext, out entries, token);
                if (ranks == null)
                    return output;

                var stopwatch = Stopwatch.StartNew();
                var ranksSpan = ranks.AsSpan(0, entries.Count);
                if (scoreQuery == null)
                {
                    DocumentIdsWithScores(entries.ToSpan(), ranksSpan, output);
                }
                else
                {
                    // both lists hold the same entries in the same order
                    relevance = new ContextBoundNativeList<float>(_allocator, entries.Count);
                    relevance.Count = entries.Count;
                    RelevanceOfEntries(scoreQuery, documentsContext, entries.ToSpan(), relevance.ToSpan(), token);
                    DocumentIdsWithScores(entries.ToSpan(), relevance.ToSpan(), output, ranksSpan);
                }

                query.JoinTimings?.AddCounter("RankDocumentIdsMicroseconds", stopwatch.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
            finally
            {
                if (ranks != null)
                    ScorePool.Return(ranks);
                entries.Dispose();
                relevance.Dispose();
            }
        }

        // The sub-query side of a JOIN on a field that orders by its own index
        internal JoinTermScores ScanTermRanks(IndexQueryServerSide query, DocumentsOperationContext documentsContext, IndexQueryServerSide scoreQuery, string joinField, bool keepWinners,
            CancellationToken token)
        {
            var output = Bounded(new JoinTermScores(_allocator, keepWinners, keepRanks: scoreQuery != null));
            ContextBoundNativeList<long> entries = default;
            float[] ranks = null;
            JoinTermScores relevance = null;
            try
            {
                ranks = RankSubQueryEntries(query, documentsContext, out entries, token);
                if (ranks == null)
                {
                    output.ScoresAreRanks = scoreQuery == null;
                    return output;
                }

                // a scan keeps the best score of a term, and the best rank is the lowest: the scan reads the negated ranks
                var ranksSpan = ranks.AsSpan(0, entries.Count);
                for (var i = 0; i < ranksSpan.Length; i++)
                    ranksSpan[i] = -ranksSpan[i];

                ScanFieldTerms(joinField, JoinFieldScan.Auto, entries.ToSpan(), ranksSpan, output);
                if (scoreQuery != null)
                    relevance = ScanTermScores(scoreQuery, documentsContext, joinField, JoinFieldScan.Auto, keepWinners: false, token);

                output.TurnScoresIntoRanks(relevance);
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
            finally
            {
                if (ranks != null)
                    ScorePool.Return(ranks);
                entries.Dispose();
                relevance?.Dispose();
            }
        }

        private void DocumentIdsWithScores(ReadOnlySpan<long> ids, ReadOnlySpan<float> scores, JoinTermScores output, ReadOnlySpan<float> ranks = default, bool winners = false)
        {
            // the ids are sorted and unique, the bulk reader returns the terms in that same order
            const int chunkSize = 1024;
            const int minimumIdBytes = 12;
            var chunk = QueryPool.Rent(chunkSize);
            try
            {
                var keysSeen = 0;
                long bytesSeen = 0;
                for (var start = 0; start < ids.Length;)
                {
                    var remaining = ids.Length - start;
                    var averageBytes = keysSeen == 0 ? minimumIdBytes : (int)Math.Max(minimumIdBytes, (bytesSeen + keysSeen - 1) / keysSeen);
                    output.EnsureCapacity(remaining, (int)Math.Min((long)remaining * averageBytes, int.MaxValue));

                    var length = Math.Min(chunkSize, remaining);
                    ids.Slice(start, length).CopyTo(chunk);

                    var read = _documentIdReader.GetAllTermsFromSet(chunk.AsSpan(0, length), out var terms);
                    for (var i = 0; i < read; i++)
                    {
                        var term = _documentIdReader.GetDecodedTerm(terms[i]);
                        if (winners)
                            output.Add(term, scores[start + i], ids[start + i]);
                        else if (ranks.IsEmpty)
                            output.Add(term, scores[start + i]);
                        else
                            output.Add(term, scores[start + i], ranks[start + i]);
                        bytesSeen += term.Length;
                    }

                    keysSeen += read;
                    start += read;
                }
            }
            finally
            {
                QueryPool.Return(chunk);
            }
        }

        // The table of the dictionary that encodes the ids of the documents, null when the index has none
        internal ulong? DocumentIdTableHash()
        {
            var dictionaryId = _documentIdReader.DictionaryId;
            return dictionaryId > 0 ? PersistentDictionary.GetTableHash(_lowLevelTransaction, dictionaryId) : null;
        }

        // The ids stay as the entries hold them: encoded
        private void EncodedDocumentIdsWithScores(ReadOnlySpan<long> ids, ReadOnlySpan<float> scores, JoinTermScores output)
        {
            const int chunkSize = 1024;
            const int minimumEncodedBytes = 6;
            var chunk = QueryPool.Rent(chunkSize);
            try
            {
                var keysSeen = 0;
                long bytesSeen = 0;
                for (var start = 0; start < ids.Length;)
                {
                    var remaining = ids.Length - start;
                    var averageBytes = keysSeen == 0 ? minimumEncodedBytes : (int)Math.Max(minimumEncodedBytes, (bytesSeen + keysSeen - 1) / keysSeen);
                    output.EnsureCapacity(remaining, (int)Math.Min((long)remaining * averageBytes, int.MaxValue));

                    var length = Math.Min(chunkSize, remaining);
                    ids.Slice(start, length).CopyTo(chunk);

                    var read = _documentIdReader.GetAllTermsFromSet(chunk.AsSpan(0, length), out var terms);
                    for (var i = 0; i < read; i++)
                    {
                        var encoded = TermsReader.EncodedTermOf(terms[i], out var bits);
                        output.AddEncoded(encoded, bits, scores[start + i]);
                        bytesSeen += encoded.Length;
                    }

                    keysSeen += read;
                    start += read;
                }
            }
            finally
            {
                QueryPool.Return(chunk);
            }
        }

        internal bool IsDocumentIdAmong(JoinTermScores keys, ReadOnlySpan<byte> id)
        {
            var isAscii = Ascii.IsValid(id);
            if (isAscii)
            {
                // the analyzer of the id lowers the case of ASCII letters and changes nothing else
                const int stackIdBytes = 512;
                var rented = id.Length > stackIdBytes ? IdBytesPool.Rent(id.Length) : null;
                try
                {
                    Span<byte> lowered = rented is null ? stackalloc byte[stackIdBytes] : rented;
                    lowered = lowered.Slice(0, id.Length);
                    for (var i = 0; i < id.Length; i++)
                        lowered[i] = id[i] is >= (byte)'A' and <= (byte)'Z' ? (byte)(id[i] + 32) : id[i];

                    return keys.TryFind(lowered, out _);
                }
                finally
                {
                    if (rented != null)
                        IdBytesPool.Return(rented);
                }
            }

            if (_fieldMappings.TryGetByFieldName(_allocator, Constants.Documents.Indexing.Fields.DocumentIdFieldName, out var binding) == false)
                return keys.TryFind(id, out _);

            IndexSearcher.ApplyAnalyzer(binding.Metadata, binding.Analyzer, id, out var analyzed);
            try
            {
                return keys.TryFind(analyzed.AsReadOnlySpan(), out _);
            }
            finally
            {
                analyzed.Release(_allocator);
            }
        }

        internal ByteStringContext Allocator => _allocator;

        internal JoinTermScores ScanCompositeKeys(IndexQueryServerSide query, DocumentsOperationContext documentsContext, string[] fields, CancellationToken token)
        {
            var rows = Bounded(new JoinTermScores(_allocator, keepWinners: true));
            ContextBoundNativeList<long> ids = default;
            ContextBoundNativeList<float> scores = default;
            try
            {
                if (TryScoreSubQuery(query, documentsContext, out ids, out scores, token))
                    IndexSearcher.CompositeKeysOfEntries(fields, ids.ToSpan(), scores.ToSpan(), rows);

                return CompositeJoinKeys.MergeBest(_allocator, rows);
            }
            catch (CompositeKeyLimitException e)
            {
                throw new InvalidQueryException($"The JOIN sub-query has an entry whose fields ({string.Join(", ", fields)}) hold more combinations of values than the {CompositeKeyBuilder.MaxCombinations} that are supported.",
                    query.Query, query.QueryParameters, e);
            }
            finally
            {
                rows.Dispose();
                ids.Dispose();
                scores.Dispose();
            }
        }

        internal enum JoinFieldScan
        {
            Auto,
            // Opens the entry-terms reader of every matched entry, keeps the best per term id
            EntryTerms,
            // Walks the terms of the field and intersects each posting list with the matched ids
            TermWalk,
            // Bulk reads the entries -> term map
            BulkMap
        }

        internal string GetDocumentIdOfEntry(long entryId) => _documentIdReader.GetTermFor(entryId);

        internal JoinTermScores ScanTermScores(IndexQueryServerSide query, DocumentsOperationContext documentsContext, string joinField, JoinFieldScan strategy, bool keepWinners,
            CancellationToken token)
        {
            var output = Bounded(new JoinTermScores(_allocator, keepWinners));
            ContextBoundNativeList<long> ids = default;
            ContextBoundNativeList<float> scores = default;
            try
            {
                if (TryScoreSubQuery(query, documentsContext, out ids, out scores, token) == false)
                    return output;

                ScanFieldTerms(joinField, strategy, ids.ToSpan(), scores.ToSpan(), output);
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
            finally
            {
                ids.Dispose();
                scores.Dispose();
            }
        }

        // The sub-query side of a JOIN on a field, restricted to some keys (the terms of the join field, one after the other in keyBytes, keyEnds holds where each of them ends):
        // the terms of the field that the entries of the sub-query have, among those keys. Only the entries of the groups of the given keys are looked at.
        internal JoinTermScores ScanTermScoresAmong(IndexQueryServerSide query, DocumentsOperationContext documentsContext, string joinField, ReadOnlySpan<byte> keyBytes, ReadOnlySpan<int> keyEnds,
            CancellationToken token)
        {
            var output = new JoinTermScores(_allocator);
            var among = new JoinTermScores(_allocator);
            ContextBoundNativeList<long> ids = default;
            ContextBoundNativeList<float> scores = default;
            try
            {
                var queryMatch = BuildSubQuery(query, documentsContext, out _, token);
                if (queryMatch is null || keyEnds.Length == 0)
                    return output;

                among.EnsureCapacity(keyEnds.Length, keyBytes.Length);
                var start = 0;
                foreach (var end in keyEnds)
                {
                    among.Add(keyBytes.Slice(start, end - start), 0f);
                    start = end;
                }

                // the keys are terms of the joined index: they are matched as they are
                var groups = IndexSearcher.InQuery(IndexSearcher.FieldMetadataBuilder(joinField), among, keysAreIndexedTerms: true, token);
                ids = ReadAllEntries(IndexSearcher.And(queryMatch, groups, token), token);
                if (ids.Count == 0)
                    return output;

                Sort.Run(ids.ToSpan());

                // nothing reads the scores of these entries
                scores = new ContextBoundNativeList<float>(_allocator);
                scores.GrowFitting(ids.Count + 1);
                scores.Count = ids.Count;
                scores.ToSpan().Fill(Bm25Relevance.InitialScoreValue);

                ScanFieldTerms(joinField, JoinFieldScan.Auto, ids.ToSpan(), scores.ToSpan(), output);
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
            finally
            {
                among.Dispose();
                ids.Dispose();
                scores.Dispose();
            }
        }

        // Every term the field has in the entries with the best of their scores, by the given scan
        private void ScanFieldTerms(string joinField, JoinFieldScan strategy, ReadOnlySpan<long> ids, ReadOnlySpan<float> scores, JoinTermScores output)
        {
            if (strategy == JoinFieldScan.Auto)
                strategy = ChooseJoinFieldScan(joinField, ids.Length);
            else if (strategy == JoinFieldScan.BulkMap && CanReadEntryTermMap(joinField) == false)
                strategy = JoinFieldScan.EntryTerms; // the map keeps one term per entry: a multi-valued field would lose keys

            switch (strategy)
            {
                case JoinFieldScan.TermWalk:
                    IndexSearcher.ScoresByWalkingTermsOfField(joinField, ids, scores, ref output);
                    break;
                case JoinFieldScan.BulkMap:
                    IndexSearcher.ScoresByEntryTermMap(joinField, ids, scores, output);
                    break;
                default:
                    IndexSearcher.ScoresByEntryTerms(joinField, ids, scores, output);
                    break;
            }
        }

        // The entries -> term map keeps one term per entry
        private bool CanReadEntryTermMap(string field) => IndexSearcher.FieldHoldsOneTermPerEntry(field) && IndexSearcher.EntryTermMapCoversPostings(field);

        // A single-valued field is read through the entries -> term map
        private JoinFieldScan ChooseJoinFieldScan(string field, int matched)
        {
            var terms = Math.Max(1, IndexSearcher.GetTermAmountInField(IndexSearcher.FieldMetadataBuilder(field)));
            if (IndexSearcher.FieldHoldsOneTermPerEntry(field) && terms <= matched * MaxTermsToVerifyPerMatchedEntry && IndexSearcher.EntryTermMapCoversPostings(field))
                return JoinFieldScan.BulkMap;

            var walkMicroseconds = IndexSearcher.NumberOfEntries * (WalkNanosecondsPerEntry + WalkNanosecondsPerEntryPerDecade * Math.Log10(terms)) / 1000;
            return matched * EntryWalkMicrosecondsPerMatchedEntry >= walkMicroseconds ? JoinFieldScan.TermWalk : JoinFieldScan.EntryTerms;
        }

        // relative costs of the two scans (term walk per entry, growing with the log of the terms; entries -> term scan per matched entry): only their ratio matters
        private const double WalkNanosecondsPerEntry = 3;
        private const double WalkNanosecondsPerEntryPerDecade = 4.5;
        private const double EntryWalkMicrosecondsPerMatchedEntry = 0.25;

        // Checking the map reads the posting list state of every term of the field
        private const double MaxTermsToVerifyPerMatchedEntry = 4;

        protected interface ISupportsHighlighting
        {
            QueryTimingsScope TimingsScope { get; }
            Dictionary<string, CoraxHighlightingTermIndex> Terms { get; }
            void Initialize(IndexQueryServerSide query, QueryTimingsScope scope);
            void Setup(IndexQueryServerSide query, DocumentsOperationContext context);

            Dictionary<string, Dictionary<string, string[]>> Execute(IndexQueryServerSide query, DocumentsOperationContext context, IndexFieldsMapping fieldMappings,
                ref EntryTermsReader entryReader, FieldsToFetch highlightingFields, Document document, IndexSearcher indexSearcher);
        }

        private struct NoHighlighting : ISupportsHighlighting
        {
            public QueryTimingsScope TimingsScope => null;
            public Dictionary<string, CoraxHighlightingTermIndex> Terms => null;
            public void Initialize(IndexQueryServerSide query, QueryTimingsScope scope) { }
            public void Setup(IndexQueryServerSide query, DocumentsOperationContext context) { }

            public Dictionary<string, Dictionary<string, string[]>> Execute(IndexQueryServerSide query, DocumentsOperationContext context,
                IndexFieldsMapping fieldMappings, ref EntryTermsReader entryReader, FieldsToFetch highlightingFields, Document document, IndexSearcher indexSearcher)
                => null;
        }

        private struct HasHighlighting : ISupportsHighlighting
        {
            private QueryTimingsScope _timingsScope;
            private Dictionary<string, CoraxHighlightingTermIndex> _terms;

            public QueryTimingsScope TimingsScope => _timingsScope;
            public Dictionary<string, CoraxHighlightingTermIndex> Terms => _terms;

            public void Initialize(IndexQueryServerSide query, QueryTimingsScope scope)
            {
                _timingsScope = scope?.For(nameof(QueryTimingsScope.Names.Highlightings), start: false);
                _terms = new Dictionary<string, CoraxHighlightingTermIndex>();
            }

            public void Setup(IndexQueryServerSide query, DocumentsOperationContext context)
            {
                using (_timingsScope?.For(nameof(QueryTimingsScope.Names.Setup)))
                {
                    foreach (var term in Terms)
                    {
                        string[] nls;
                        switch (term.Value.Values)
                        {
                            case string s:
                                nls = new string[] { s.TrimEnd('*').TrimStart('*') };
                                break;
                            case List<string> ls:
                                nls = new string[ls.Count];
                                for (int i = 0; i < ls.Count; i++)
                                    nls[i] = ls[i].TrimEnd('*').TrimStart('*');
                                break;
                            case Tuple<string, string> t2:
                                nls = new string[] { t2.Item1.TrimEnd('*').TrimStart('*'), t2.Item2.TrimEnd('*').TrimStart('*') };
                                break;
                            case string[] as1:
                                nls = new string[as1.Length];
                                for (int i = 0; i < as1.Length; i++)
                                    nls[i] = as1[i].TrimEnd('*').TrimStart('*');
                                break;
                            case List<(string Term, bool Exact)> termsWithExact: // In query
                                nls = new string[termsWithExact.Count];
                                for (int i = 0; i < termsWithExact.Count; i++)
                                    nls[i] = termsWithExact[i].Term.TrimEnd('*').TrimStart('*');
                                break;
                            case null:
                                continue;
                            default:
                                throw new NotSupportedException($"The type '{term.Value.Values.GetType().FullName}' is not supported.");
                        }

                        term.Value.Values = nls;
                        term.Value.PreTags = null;
                        term.Value.PostTags = null;
                    }

                    var highlightingTerms = _terms;
                    foreach (var highlighting in query.Metadata.Highlightings)
                    {
                        var options = highlighting.GetOptions(context, query.QueryParameters);
                        if (options == null)
                            continue;

                        var numberOfPreTags = options.PreTags?.Length ?? 0;
                        var numberOfPostTags = options.PostTags?.Length ?? 0;
                        if (numberOfPreTags != numberOfPostTags)
                            throw new InvalidOperationException("Number of pre-tags and post-tags must match.");

                        var fieldName = highlighting.Field.Value;

                        if (highlightingTerms.TryGetValue(fieldName, out var termIndex) == false)
                        {
                            // the case when we have to create MapReduce highlighter
                            termIndex = new()
                            {
                                FieldName = highlighting.Field.Value,
                                DynamicFieldName = AutoIndexField.GetSearchAutoIndexFieldName(highlighting.Field.Value),
                                GroupKey = options.GroupKey
                            };
                            highlightingTerms.Add(query.Metadata.IsDynamic ? termIndex.DynamicFieldName : termIndex.FieldName, termIndex);
                        }

                        if (termIndex is not null)
                            termIndex.GroupKey = options.GroupKey;
                        else
                            continue;

                        if (numberOfPreTags > 0)
                        {
                            termIndex.PreTags = options.PreTags;
                            termIndex.PostTags = options.PostTags;
                        }
                    }
                }
            }

            public Dictionary<string, Dictionary<string, string[]>> Execute(IndexQueryServerSide query, DocumentsOperationContext context,
                IndexFieldsMapping fieldMappings, ref EntryTermsReader entryReader, FieldsToFetch highlightingFields, Document document, IndexSearcher indexSearcher)
            {
                using (_timingsScope?.For(nameof(QueryTimingsScope.Names.Fill)))
                {
                    var highlightings = new Dictionary<string, Dictionary<string, string[]>>();
                    var highlightingTerms = Terms;
                    var allocator = context.Allocator;

                    // If we have highlightings then we need to setup the Corax objects that will attach to the evaluator in order
                    // to retrieve the fields and perform the transformations required by Highlightings. 
                    foreach (var current in query.Metadata.Highlightings)
                    {
                        // We get the actual highlight description. 
                        var fieldName = current.Field.Value;
                        string key = document.Id;

                        if (highlightingTerms.TryGetValue(fieldName, out var fieldDescription) == false)
                        {
                            continue;
                        }

                        
                        //We have to get analyzer so dynamic field have priority over normal name
                        // We get the field binding to ensure that we are running the analyzer to find the actual tokens.
                        if (fieldMappings.TryGetByFieldName(allocator, fieldDescription.DynamicFieldName ?? fieldDescription.FieldName, out var fieldBinding) == false)
                            continue;

                        // We will get the actual tokens dictionary for this field. If it exists we get it immediately, if not we create
                        if (highlightings.TryGetValue(fieldDescription.FieldName, out var tokensDictionary) == false)
                        {
                            tokensDictionary = new(StringComparer.OrdinalIgnoreCase);
                            highlightings[fieldDescription.FieldName] = tokensDictionary;
                        }

                        List<string> fragments = new();
                        
                        // We need to get the actual field, not the dynamic field. 
                        int propIdx = document.Data.GetPropertyIndex(fieldDescription.FieldName);
                        if (propIdx < 0)
                        {
                            bool isDirectlyFromIndex = false;
                            
                            long fieldRootPage = indexSearcher.FieldCache.GetLookupRootPage(fieldName);
                            entryReader.Reset();
                            int maxFragments = current.FragmentCount;
                            while (entryReader.MoveNextStoredField())
                            {
                                if (entryReader.FieldRootPage != fieldRootPage)
                                    continue;

                                isDirectlyFromIndex = true;
                                
                                if (entryReader.StoredField == null)
                                    break;

                                if (entryReader.IsRaw)
                                    break;
                                
                                var span = entryReader.StoredField.Value;
                                var fieldValue = span.ToStringValue();
                                
                                if (entryReader.IsList)
                                {
                                    maxFragments -= ProcessHighlightings(current, fieldDescription, fieldValue, fragments, maxFragments);
                                    continue;
                                }
                                
                                ProcessHighlightings(current, fieldDescription, fieldValue, fragments, current.FragmentCount);
                            }

                            if (isDirectlyFromIndex == false)
                                continue;
                            else
                                goto Finish;
                        }

                        BlittableJsonReaderObject.PropertyDetails property = default;
                        document.Data.GetPropertyByIndex(propIdx, ref property);

                        if (property.Token == BlittableJsonToken.String)
                        {
                            var fieldValue = ((LazyStringValue)property.Value).ToString(CultureInfo.InvariantCulture);
                            ProcessHighlightings(current, fieldDescription, fieldValue, fragments, current.FragmentCount);
                        }
                        else if (property.Token == BlittableJsonToken.CompressedString)
                        {
                            var fieldValue = ((LazyCompressedStringValue)property.Value).ToString();
                            ProcessHighlightings(current, fieldDescription, fieldValue, fragments, current.FragmentCount);
                        }
                        else if ((property.Token & ~BlittableJsonToken.PositionMask) == BlittableJsonToken.StartArray)
                        {
                            // This is an array, now we need to know if it is compressed or not. 
                            int maxFragments = current.FragmentCount;
                            foreach (var item in ((BlittableJsonReaderArray)property.Value).Items)
                            {
                                var fieldValue = item.ToString();
                                maxFragments -= ProcessHighlightings(current, fieldDescription, fieldValue, fragments, maxFragments);
                            }
                        }
                        else 
                            continue;

                        if (string.IsNullOrWhiteSpace(fieldDescription.GroupKey) == false)
                        {
                            int groupKey;
                            if ((groupKey = document.Data.GetPropertyIndex(fieldDescription.GroupKey)) != -1)
                            {
                                document.Data.GetPropertyByIndex(groupKey, ref property);

                                key = property.Token switch
                                {
                                    BlittableJsonToken.String => ((LazyStringValue)property.Value).ToString(CultureInfo.InvariantCulture),
                                    BlittableJsonToken.CompressedString => ((LazyCompressedStringValue)property.Value).ToString(),
                                    _ => throw new NotSupportedException($"The token type '{property.Token.ToString()}' is not supported.")
                                };
                            }
                        }
                        
                        Finish:
                        if (fragments.Count <= 0) 
                            continue;
                            
                        if (tokensDictionary.TryGetValue(key, out var existingHighlights))
                            throw new NotSupportedInCoraxException("Multiple highlightings for the same field and group key are not supported.");

                        tokensDictionary[key] = fragments.ToArray();
                    }

                    return highlightings;
                }
            }
        }

        protected interface IHasDistinct
        {
        }

        private struct NoDistinct : IHasDistinct { }

        private struct HasDistinct : IHasDistinct { }


        // Even if there are no distinct statements we have to be sure that we are not including
        // documents that we have already included during this request. 
        protected struct IdentityTracker<TDistinct> where TDistinct : struct, IHasDistinct
        {
            private Index _index;
            private IndexQueryServerSide _query;
            private IndexSearcher _searcher;
            private IndexFieldsMapping _fieldsMapping;
            private IQueryResultRetriever _retriever;

            private bool _isMap;

            private GrowableHashSet<UnmanagedSpan> _alreadySeenDocumentKeysInPreviousPage;
            private GrowableHashSet<ulong> _alreadySeenProjections;
            public long QueryStart;
            private TermsReader _documentIdReader;
            private bool _canPerformPaginationBasedOnEntriesIds;
            
            public void Initialize(Index index, IndexQueryServerSide query, IndexSearcher searcher, TermsReader documentIdReader, IndexFieldsMapping fieldsMapping, IQueryResultRetriever retriever)
            {
                _index = index;
                _query = query;
                _searcher = searcher;
               
                _fieldsMapping = fieldsMapping;
                _retriever = retriever;
                _documentIdReader = documentIdReader;

                QueryStart = _query.Start;
                _isMap = index.Type.IsMap();

                _canPerformPaginationBasedOnEntriesIds = searcher.EntryIdPaginationSupportStatus == EntryIdPaginationSupportStatus.Supported;

                if (_canPerformPaginationBasedOnEntriesIds == false)
                    _alreadySeenDocumentKeysInPreviousPage = new(UnmanagedSpanComparer.Instance);
            }

            public long RegisterDuplicates<TProjection>(ref TProjection hasProjection, long currentIdx, Span<long> ids, CancellationToken token)
                where TProjection : struct, IHasProjection
            {
                // From now on, we know we will try to skip duplicates.
                long limit;

                // If query start is effectively bigger than the one we are starting on. 
                if (QueryStart > currentIdx)
                {
                    // If the query start before the current read ids, then we have to divide the ids in those
                    // that need to be processed for discarding and those that don't. 
                    if (QueryStart < currentIdx + ids.Length)
                        limit = QueryStart - currentIdx;
                    else
                        limit = ids.Length;
                }
                else return 0; // we left it behind, so we are going to continue going for 0. 

                var distinctIds = ids.Slice(0, (int)limit);

                if (hasProjection.IsProjection == false)
                {
                    if (_canPerformPaginationBasedOnEntriesIds == false)
                    {
                        Sort.Run(distinctIds);
                        while (_documentIdReader.GetAllTermsFromSet(distinctIds, out var termsSet) is var read and > 0)
                        {
                            foreach (var key in termsSet)
                                _alreadySeenDocumentKeysInPreviousPage.Add(key);
                            distinctIds = distinctIds[read..];
                        }
                    }

                    return limit;
                }

                if (typeof(TDistinct) == typeof(HasDistinct))
                {
                    _alreadySeenProjections ??= new();

                    var retriever = _retriever;
                    Page page = default;
                    var entryTermsKey = new CompactKey(); // shared by the readers of all the entries
                    foreach (var id in distinctIds)
                    {
                        var reader = _searcher.GetEntryTermsReader(id, ref page, entryTermsKey);

                        var key = _documentIdReader.GetTermFor(id);
                        var retrieverInput = new RetrieverInput(_searcher, _fieldsMapping, reader, key, _index.IndexFieldsPersistence.HasTimeValues);
                        var result = retriever.Get(ref retrieverInput, token);

                        if (result.Document != null)
                        {
                            if (result.Document.Data.Count > 0)
                            {
                                // we don't consider empty projections to be relevant for distinct operations
                                _alreadySeenProjections.Add(result.Document.DataHash);
                            }
                        }
                        else if (result.List != null)
                        {
                            foreach (Document item in result.List)
                            {
                                if (item.Data.Count > 0)
                                {
                                    // we don't consider empty projections to be relevant for distinct operations
                                    _alreadySeenProjections.Add(item.DataHash);
                                }
                            }
                        }
                    }
                }

                return limit;
            }

            public bool ShouldIncludeIdentity<TProjection>(ref TProjection hasProjection, UnmanagedSpan identity)
                where TProjection : struct, IHasProjection
            {
                if (hasProjection.IsProjection)
                    return true;

                if (_canPerformPaginationBasedOnEntriesIds == false)
                    return _alreadySeenDocumentKeysInPreviousPage.Add(identity);

                return true;
            }

            public bool ShouldIncludeDocument<TProjection>(ref TProjection hasProjection, Document doc)
                where TProjection : struct, IHasProjection
            {
                if (doc == null)
                    return false;

                if (typeof(TDistinct) == typeof(HasDistinct))
                {
                    _alreadySeenProjections ??= new();
                    if (_alreadySeenProjections.Add(doc.DataHash) == false)
                        return false;
                }

                return true;
            }
        }

        protected interface ISupportsQueryFilter : IDisposable
        {
            FilterResult Apply(ref RetrieverInput input, string key);
        }

        private readonly struct NoQueryFilter : ISupportsQueryFilter
        {
            public void Dispose() { }

            public FilterResult Apply(ref RetrieverInput input, string key) => FilterResult.Accepted;
        }

        private readonly struct HasQueryFilter : ISupportsQueryFilter
        {
            private readonly QueryFilter _filter;
            public HasQueryFilter([NotNull] QueryFilter filter)
            {
                _filter = filter;
            }
            public void Dispose()
            {
                _filter.Dispose();
            }

            public FilterResult Apply(ref RetrieverInput input, string key) => _filter.Apply(ref input, key);
        }

        protected interface IHasProjection
        {
            bool IsProjection { get; }
        }

        private struct NoProjection : IHasProjection
        {
            public bool IsProjection => false;
        }

        private struct HasProjection : IHasProjection
        {
            public bool IsProjection => true;
        }

        private static bool WillAlwaysIncludeInResults(IndexType indexType, FieldsToFetch fieldsToFetch, IndexQueryServerSide query)
        {
            return fieldsToFetch.IsDistinct || query.SkipDuplicateChecking || indexType.IsMapReduce();
        }

        private IEnumerable<QueryResult> QueryInternal<THighlighting, TQueryFilter, THasProjection, TDistinct>(
                    IndexQueryServerSide query, QueryTimingsScope queryTimings, FieldsToFetch fieldsToFetch,
                    Reference<long> totalResults, Reference<long> skippedResults, Reference<long> scannedDocuments,
                    IQueryResultRetriever retriever, DocumentsOperationContext documentsContext,
                    Func<string, SpatialField> getSpatialField,
                    QueryTimeScope queryTime, CancellationToken token)
                where TDistinct : struct, IHasDistinct
                where THasProjection : struct, IHasProjection
                where THighlighting : struct, ISupportsHighlighting
                where TQueryFilter : struct, ISupportsQueryFilter
        {
            // The query method will have to deal with 2 different usages. The first is when the user requests a query and everything
            // fits into a single page. In those cases it is easy because the client pages size and the internal Corax buffer size
            // may be the same and will not introduce any inconsistency. 

            // However, the user may not care about the page size because he is streaming or counting. In those cases the process itself
            // will finish when the user either stops or we don't have any more matches to deal with. 

            // In the case of distinct, pagination is not such a big deal because we will have to calculate distinct anyways therefore
            // we can just count the current returned document number, number of skipped documents but in the end we just iterating over
            // the entire set. As we don't keep track of 'follow up' information when the next page comes, we will just recalculate the
            // distinct.


            var identityTracker = new IdentityTracker<TDistinct>();
            identityTracker.Initialize(_index, query, IndexSearcher, _documentIdReader, _fieldMappings, retriever);

            long pageSize = query.PageSize;

            if (query.Metadata.HasExplanations)
                ThrowExplanationsIsNotImplementedInCorax();

            long take = pageSize + query.Start;
            if (take > IndexSearcher.NumberOfEntries || fieldsToFetch.IsDistinct)
                take = CoraxConstants.IndexSearcher.TakeAll;

            bool isDistinctCount = query.PageSize == 0 && typeof(TDistinct) == typeof(HasDistinct);
            if (isDistinctCount)
            {
                if (pageSize > int.MaxValue)
                    ThrowDistinctOnBiggerCollectionThanInt32();

                pageSize = int.MaxValue;
                take = CoraxConstants.IndexSearcher.TakeAll;
            }

            THasProjection hasProjections = default;
            THighlighting highlightings = default;
            highlightings.Initialize(query, queryTimings);
            _vectorChunkHighlightings = null;

            long docsToLoad = pageSize;
            bool runQuery = true;
            while (runQuery)
            {
                IQueryMatch queryMatch;
                OrderMetadata[] orderByFields;

                CoraxQueryBuilder.Parameters builderParameters;
                using (var coraxTimings = queryTimings?.For(nameof(QueryTimingsScope.Names.Corax), start: false)?.Start())
                {
                    IDisposable releaseServerContext = null;
                    IDisposable closeServerTransaction = null;
                    TransactionOperationContext serverContext = null;

                    try
                    {
                        if (query.Metadata.HasCmpXchg)
                        {
                            releaseServerContext = documentsContext.DocumentDatabase.ServerStore.ContextPool.AllocateOperationContext(out serverContext);
                            closeServerTransaction = serverContext.OpenReadTransaction();
                        }

                        builderParameters = new CoraxQueryBuilder.Parameters(IndexSearcher, _allocator, serverContext, documentsContext, query, _index,
                            query.QueryParameters, QueryBuilderFactories, _fieldMappings, fieldsToFetch, highlightings.Terms, (int)take, deduplicationDisabled: false, indexReadOperation: this, token: token, queryTime: queryTime, queryTimings: coraxTimings)
                        {
                            AllowJoinOrderedWalk = true,
                            AllowJoinFirstEntries = true
                        };

                        using (closeServerTransaction)
                        {
                            if ((queryMatch = CoraxQueryBuilder.BuildQuery(builderParameters, out orderByFields)) is null)
                                yield break;
                        }

                    }
                    finally
                    {
                        releaseServerContext?.Dispose();
                    }
                }

                highlightings.Setup(query, documentsContext);

                int bufferSize = CoraxBufferSize(IndexSearcher, take, query);
                var ids = QueryPool.Rent(bufferSize);
                SortingDataTransfer sortingData = default;
                using var queryFilter = GetQueryFilter();
                Page page = default;
                bool willAlwaysIncludeInResults = WillAlwaysIncludeInResults(_index.Type, fieldsToFetch, query);
                totalResults.Value = 0;

                var hasOrderByDistance = query.Metadata.OrderBy is [{OrderingType: OrderByFieldType.Distance}, ..] && _index.Configuration.CoraxIncludeSpatialDistance;
                if (builderParameters.HasBoost || hasOrderByDistance)
                {
                    sortingData = new()
                    {
                        ScoresBuffer = builderParameters.NeedsScoresBuffer()
                            ? ScorePool.Rent(bufferSize)
                            : null,
                        DistancesBuffer = _index.Configuration.CoraxIncludeSpatialDistance && hasOrderByDistance
                            ? DistancePool.Rent(bufferSize)
                            : null
                    };
                    
                    switch (queryMatch)
                    {
                        case SortingMatch sm:
                            sm.SetScoreAndDistanceBuffer(sortingData);
                            queryMatch = sm;
                            break;
                        case SortingMultiMatch smm:
                            smm.SetSortingDataTransfer(sortingData);
                            queryMatch = smm;
                            break;
                        case IPresortedMatch presorted:
                            presorted.SetScoreAndDistanceBuffer(sortingData);
                            break;
                    }
                }

                // We don't need to do any processing for the query beyond counting if we are getting a count.
                while (query.IsCountQuery == false || typeof(TDistinct) == typeof(HasDistinct))
                {
                    token.ThrowIfCancellationRequested();

                    // We look for items that was haven't seen before in the case of paging. 
                    int read = queryMatch.Fill(ids);
                    if (read == 0)
                        goto Done;

                    // If we are going to skip, we've better do it knowing how many we have passed. 
                    // After this call the order of ids from 0 to `i` may be changed, and we cannot rely on it (a sorting case).
                    long i = identityTracker.RegisterDuplicates(ref hasProjections, totalResults.Value, ids.AsSpan(0, read), token);
                    totalResults.Value += read; // important that this is *after* RegisterDuplicates

                    // Now for every document that was selected. document it. 
                    for (; docsToLoad != 0 && i < read; ++i, --docsToLoad)
                    {
                        token.ThrowIfCancellationRequested();

                        long indexEntryId = ids[i];

                        // If we are going to include no matter what, lets skip everything else.
                        if (willAlwaysIncludeInResults)
                            goto Include;

                        // Ok, we will need to check for duplicates, then we will have to work. In some cases (like TimeSeries) we don't "have" unique identifier so we skip checking.
                        var identityExists = retriever.TryGetKeyCorax(_documentIdReader, indexEntryId, out var rawIdentity);

                        // If we have figured out that this document identity has already been seen, we are skipping it.
                        if (identityExists && identityTracker.ShouldIncludeIdentity(ref hasProjections, rawIdentity) == false)
                        {
                            docsToLoad++;
                            skippedResults.Value++;
                            continue;
                        }

                        if (typeof(TDistinct) == typeof(HasDistinct) && query.IsCountQuery)
                            continue;

                        // Now we know this is a new candidate document to be return therefore, we are going to be getting the
                        // actual data and apply the rest of the filters. 
                        Include:

                        float? documentScore = sortingData.IncludeScores ? sortingData.ScoresBuffer[i] : null;
                        CoraxSpatialResult? documentDistance = hasOrderByDistance ? sortingData.DistancesBuffer[i] : null;

                        var key = _documentIdReader.GetTermFor(indexEntryId);
                        EntryTermsReader entryTermsReader = IndexSearcher.GetEntryTermsReader(indexEntryId, ref page, _entryTermsKey ??= new CompactKey());
                        var retrieverInput = new RetrieverInput(IndexSearcher, _fieldMappings, in entryTermsReader, key, _index.IndexFieldsPersistence.HasTimeValues, documentScore, documentDistance);

                        var filterResult = queryFilter.Apply(ref retrieverInput, key);
                        if (filterResult is not FilterResult.Accepted)
                        {
                            docsToLoad++;
                            if (filterResult is FilterResult.Skipped)
                                continue;

                            if (filterResult is FilterResult.LimitReached)
                                break;
                        }

                        bool markedAsSkipped = false;
                        var fetchedDocument = retriever.Get(ref retrieverInput, token);
                        if (fetchedDocument.Document != null)
                        {
                            var qr = CreateQueryResult(ref identityTracker, fetchedDocument.Document, query, documentsContext, ref entryTermsReader, fieldsToFetch, orderByFields, ref highlightings, skippedResults, ref hasProjections, ref markedAsSkipped);
                            if (qr.Result is null)
                            {
                                docsToLoad++;
                                continue;
                            }

                            yield return qr;
                        }
                        else if (fetchedDocument.List != null)
                        {
                            foreach (Document item in fetchedDocument.List)
                            {
                                var qr = CreateQueryResult(ref identityTracker, item, query, documentsContext, ref entryTermsReader, fieldsToFetch, orderByFields, ref highlightings, skippedResults, ref hasProjections, ref markedAsSkipped);
                                if (qr.Result is null)
                                {
                                    docsToLoad++;
                                    continue;
                                }

                                yield return qr;
                            }
                        }
                        else
                        {
                            skippedResults.Value++;
                        }
                    }

                    // No need to continue filling buffers as there are no more docs to load and we are skipping statistics anyways.
                    if (docsToLoad <= 0)
                        break;
                }


                // If we are going to just return count() then we don't care about anything else than memoize the results.
                if (query.IsCountQuery || query.SkipStatistics == false)
                {
                    int read;
                    do
                    {
                        // Instead of memoizing, we just continue filling the buffer. First, because we don't need to keep the 
                        // value or deduplicate at this stage; just to know how many potential matches we have left. Also memoizing
                        // is not supported for SortingMatch. 
                        read = queryMatch.Fill(ids);
                        totalResults.Value += read;
                    } 
                    while (read != 0);
                }
                
                

                Done:
                // Since some primitives are lazily initialized, we must call Inspect after at least one Fill call.
                queryTimings?.SetQueryPlan(queryMatch.Inspect());
                
                QueryPool.Return(ids);
                if (sortingData.IncludeScores)
                    ScorePool.Return(sortingData.ScoresBuffer);
                if (sortingData.IncludeDistances)
                    DistancePool.Return(sortingData.DistancesBuffer);
                
                if (queryMatch is not SortingMatch && queryMatch is not SortingMultiMatch && queryMatch is not JoinOrderedWalkMatch && queryMatch is not JoinFirstEntriesMatch && queryMatch is not IPresortedMatch)
                    break; // this is only relevant if we are sorting, since we may have filtered items and need to read more, see: RavenDB-20294

                var sortingMatchTotalResults = 0L;
                if (queryMatch is SortingMatch) 
                    sortingMatchTotalResults = ((SortingMatch)queryMatch).TotalResults;
                else if (queryMatch is JoinOrderedWalkMatch walk)
                    sortingMatchTotalResults = walk.TotalResults;
                else if (queryMatch is JoinFirstEntriesMatch first)
                    sortingMatchTotalResults = first.TotalResults;
                else if (queryMatch is IPresortedMatch presortedMatch)
                    sortingMatchTotalResults = presortedMatch.TotalResults;
                else 
                    sortingMatchTotalResults = ((SortingMultiMatch)queryMatch).TotalResults;

                if (docsToLoad == 0 ||
                    sortingMatchTotalResults == totalResults.Value ||
                    scannedDocuments.Value >= query.FilterLimit)
                {
                    totalResults.Value = (int)Math.Min(sortingMatchTotalResults, int.MaxValue);
                    runQuery = false;
                }
                else
                {
                    Debug.Assert(_maxNumberOfOutputsPerDocument > 0);
                    take += (pageSize - (pageSize - docsToLoad)) * _maxNumberOfOutputsPerDocument;
                    if (take < 0) // handle overflow
                        take = int.MaxValue;
                    // start *after* all the items we already read and returned to the caller
                    identityTracker.QueryStart = totalResults.Value;
                }
            }

            if (isDistinctCount)
                totalResults.Value -= skippedResults.Value;

            TQueryFilter GetQueryFilter()
            {
                if (typeof(TQueryFilter) == typeof(NoQueryFilter))
                    return (TQueryFilter)(object)new NoQueryFilter();
                if (typeof(TQueryFilter) == typeof(HasQueryFilter))
                {
                    return (TQueryFilter)(object)new HasQueryFilter(
                        new QueryFilter(_index, query, documentsContext, skippedResults, scannedDocuments, retriever, queryTimings)
                    );
                }
                
                throw new NotSupportedException($"The type {typeof(TQueryFilter)} is not supported.");
            }
        }

        protected virtual QueryResult CreateQueryResult<TDistinct, THasProjection, THighlighting>(ref IdentityTracker<TDistinct> tracker, Document document,
            IndexQueryServerSide query, DocumentsOperationContext documentsContext, ref EntryTermsReader entryReader, FieldsToFetch highlightingFields, OrderMetadata[] orderByFields, ref THighlighting highlightings,
            Reference<long> skippedResults,
            ref THasProjection hasProjections, ref bool markedAsSkipped)
            where TDistinct : struct, IHasDistinct
            where THasProjection : struct, IHasProjection
            where THighlighting : struct, ISupportsHighlighting
        {
            if (tracker.ShouldIncludeDocument(ref hasProjections, document) == false)
            {
                document?.Dispose();

                if (markedAsSkipped == false)
                {
                    skippedResults.Value++;
                    markedAsSkipped = true;
                }

                return default;
            }

            var highlightingResult = highlightings.Execute(query, documentsContext, _fieldMappings, ref entryReader, highlightingFields, document, IndexSearcher);

            if (_vectorChunkHighlightings is { Count: > 0 } && document != null)
            {
                highlightingResult ??= new Dictionary<string, Dictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase);
                CoraxVectorChunkHighlighter.Apply(highlightingResult, _vectorChunkHighlightings, query, document, documentsContext, _index.DocumentDatabase);
            }

            return new QueryResult
            {
                Result = document,
                Highlightings = highlightingResult,
            };
        }

        public override IEnumerable<QueryResult> Query(IndexQueryServerSide query, QueryTimingsScope queryTimings, FieldsToFetch fieldsToFetch,
            Reference<long> totalResults, Reference<long> skippedResults,
            Reference<long> scannedDocuments, IQueryResultRetriever retriever, DocumentsOperationContext documentsContext, Func<string, SpatialField> getSpatialField,
            QueryTimeScope queryTime, CancellationToken token)
        {
            // We've a chain-like builder here.
            return BuildHighlightings();

            IEnumerable<QueryResult> BuildHighlightings() => query.Metadata.HasHighlightings switch
                {
                    true => BuildFilterScript<HasHighlighting>(),
                    _ => BuildFilterScript<NoHighlighting>()
                };

            IEnumerable<QueryResult> BuildFilterScript<THighlighting>()
                where THighlighting : struct, ISupportsHighlighting
                => (query.Metadata.FilterScript is null) switch
                {
                    true => BuildProjection<THighlighting, NoQueryFilter>(),
                    _ => BuildProjection<THighlighting, HasQueryFilter>()
                };

            IEnumerable<QueryResult> BuildProjection<THighlighting, TQueryFilter>()
                where THighlighting : struct, ISupportsHighlighting
                where TQueryFilter : struct, ISupportsQueryFilter
                => fieldsToFetch.IsProjection switch
                {
                    true => BuildDistinct<THighlighting, TQueryFilter, HasProjection>(),
                    _ => BuildDistinct<THighlighting, TQueryFilter, NoProjection>()
                };

            IEnumerable<QueryResult> BuildDistinct<THighlighting, TQueryFilter, THasProjection>()
                where THighlighting : struct, ISupportsHighlighting
                where TQueryFilter : struct, ISupportsQueryFilter
                where THasProjection : struct, IHasProjection
                => query.Metadata.IsDistinct switch
                {
                    true => BuildInternalQuery<THighlighting, TQueryFilter, THasProjection, HasDistinct>(),
                    _ => BuildInternalQuery<THighlighting, TQueryFilter, THasProjection, NoDistinct>()
                };
            
            IEnumerable<QueryResult> BuildInternalQuery<THighlighting, TQueryFilter, THasProjection, TDistinct>()
                where THighlighting : struct, ISupportsHighlighting
                where TQueryFilter : struct, ISupportsQueryFilter
                where THasProjection : struct, IHasProjection
                where TDistinct : struct, IHasDistinct
                => QueryInternal<THighlighting, TQueryFilter, THasProjection, TDistinct>(
                    query, queryTimings, fieldsToFetch,
                    totalResults, skippedResults, scannedDocuments,
                    retriever, documentsContext,
                    getSpatialField,
                    queryTime, token);
        }

        private static int ProcessHighlightings(HighlightingField current, CoraxHighlightingTermIndex highlightingTerm, ReadOnlySpan<char> fieldFragment, List<string> fragments, int maxFragmentCount)
        {
            int totalFragments = 0;

            // For each potential token we are looking for, and for each token that we need to find... we will test every analyzed token
            // and decide if we create a highlightings fragment for it or not.
            string[] values = (string[])highlightingTerm.Values;
            for (int i = 0; i < values.Length; i++)
            {
                // We have reached the amount of fragments we required.
                if (totalFragments >= maxFragmentCount)
                    break;

                var value = values[i];
                var preTag = highlightingTerm.GetPreTagByIndex(i);
                var postTag = highlightingTerm.GetPostTagByIndex(i);

                int currentIndex = 0;
                while (true)
                {
                    // We have reached the amount of fragments we required.
                    if (totalFragments >= maxFragmentCount)
                        break;

                    // We found an exact match in the property value.
                    var index = fieldFragment.Slice(currentIndex)
                        .IndexOf(value, StringComparison.InvariantCultureIgnoreCase);
                    if (index < 0)
                        break;

                    index += currentIndex; // Adjusting to absolute positioning

                    // We will look for a whitespace before the match to start the token. 
                    int tokenStart = fieldFragment.Slice(0, index)
                        .LastIndexOf(' ');
                    if (tokenStart < 0)
                        tokenStart = 0;

                    // We will look for a whitespace after the match to end the token. 
                    int tokenEnd = fieldFragment.Slice(index)
                        .IndexOf(' ');
                    if (tokenEnd < 0)
                        tokenEnd = fieldFragment.Length - index;

                    tokenEnd += index; // Adjusting to absolute positioning

                    int expectedFragmentRestEnd = Math.Min(current.FragmentLength - tokenEnd, fieldFragment.Length);
                    string fragment;
                    if (expectedFragmentRestEnd < 0)
                    {
                        fragment = $"{preTag}{fieldFragment[tokenStart..tokenEnd]}{postTag}";
                    }
                    else
                    {
                        var fieldFragmentSpan = fieldFragment.Length - tokenEnd < expectedFragmentRestEnd
                                                    ? fieldFragment.Slice(tokenEnd)
                                                    : fieldFragment.Slice(tokenEnd, expectedFragmentRestEnd);

                        int fragmentEnd = fieldFragmentSpan.LastIndexOf(' ');
                        if (fragmentEnd > 0)
                            expectedFragmentRestEnd = tokenEnd + fragmentEnd;
                        else
                            expectedFragmentRestEnd = fieldFragment.Length;

                        fragment = $"{preTag}{fieldFragment[tokenStart..tokenEnd]}{postTag}{fieldFragment[tokenEnd..expectedFragmentRestEnd]}";
                    }

                    fragments.Add(fragment);

                    totalFragments++;
                    currentIndex = tokenEnd;
                }
            }

            return totalFragments;
        }

        public override IEnumerable<QueryResult> IntersectQuery(IndexQueryServerSide query, FieldsToFetch fieldsToFetch, Reference<long> totalResults,
            Reference<long> skippedResults, Reference<long> scannedDocuments, IQueryResultRetriever retriever,
            DocumentsOperationContext documentsContext, Func<string, SpatialField> getSpatialField, QueryTimeScope queryTime, CancellationToken token)
        {
            throw new NotSupportedInCoraxException($"{nameof(Corax)} does not support intersect queries.");
        }

        public override List<string> Terms(string field, string fromValue, long pageSize, CancellationToken token)
        {
            if (IndexSearcher.TryGetVectorsOfField(field, out var vectorsRetriever))
            {
                List<string> terms = new();
                return TermsInternal(fromValue, pageSize, vectorsRetriever, terms, token);
            }

            if (IndexSearcher.TryGetTermsOfField(IndexSearcher.FieldMetadataBuilder(field), out var termsRetriever))
            {
                SortedSet<string> terms = new(StringComparer.Ordinal);
                return TermsInternal(fromValue, pageSize, termsRetriever, terms, token).ToList();
            }

            return [];
        }

        private TResult TermsInternal<TRetriever, TResult>(string fromValue, long pageSize, TRetriever retriever, TResult results, CancellationToken token)
            where TRetriever : IIndexedTermsRetriever
            where TResult : ICollection<string> 
        {
            try
            {
                if (string.IsNullOrEmpty(fromValue) == false)
                {
                    Span<byte> fromValueBytes = StringToValue(fromValue);
                    while (retriever.GetNextTerm(out var currentTerm) && currentTerm.SequenceEqual(fromValueBytes) == false)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }

                while (pageSize > 0 && retriever.GetNextTerm(out var currentTerm))
                {
                    token.ThrowIfCancellationRequested();
                    results.Add(ValueToString(currentTerm));
                    pageSize--;
                }

                return results;
            }
            finally
            {
                // using snapshots a struct *before* key is acquired, must use finally
                retriever.Dispose();
            }

            string ValueToString(ReadOnlySpan<byte> bytes)
            {
                return retriever.Type switch
                {
                    ConvertTo.Base64 => Convert.ToBase64String(bytes),
                    ConvertTo.String => Encodings.Utf8.GetString(bytes),
                    _ => throw new NotSupportedException($"The type {retriever.Type} is not supported.")
                };
            }

            Span<byte> StringToValue(string value)
            {
                return retriever.Type switch
                {
                    ConvertTo.Base64 => Convert.FromBase64String(value),
                    ConvertTo.String => Encodings.Utf8.GetBytes(value),
                    _ => throw new NotSupportedException($"The type {retriever.Type} is not supported.")
                };
            }
        }
        
        public override IEnumerable<QueryResult> MoreLikeThis(IndexQueryServerSide query, IQueryResultRetriever retriever, DocumentsOperationContext context,
            CancellationToken token)
        {
            var sortedQuery = PrepareMoreLikeThis(query, context, out var mlt, out var baseDocId, token);

            using var _ = mlt;

            if (query.PageSize == 0)
                yield break;

            var pageSize = CoraxBufferSize(IndexSearcher, query.PageSize, query);

            var ravenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (baseDocId.HasValue)
            {
                // by id, not by entry: a fanout index gives the base document several entries
                var baseDocumentId = _documentIdReader.GetTermFor(baseDocId.Value);
                if (baseDocumentId != null)
                    ravenIds.Add(baseDocumentId);
            }

            long[] ids = QueryPool.Rent(pageSize);
            SortingDataTransfer sortingData = default;

            // an iterator skips anything after the loop when it exits early
            try
            {
                if (_index.Configuration.CoraxIncludeDocumentScore)
                {
                    // sized to the span Fill gets, so ids[i] and ScoresBuffer[i] line up
                    sortingData = new SortingDataTransfer { ScoresBuffer = ScorePool.Rent(ids.Length) };
                    switch (sortedQuery)
                    {
                        case SortingMatch sm:
                            sm.SetScoreAndDistanceBuffer(sortingData);
                            sortedQuery = sm;
                            break;
                        case SortingMultiMatch smm:
                            smm.SetSortingDataTransfer(sortingData);
                            sortedQuery = smm;
                            break;
                        default:
                            throw new InvalidOperationException($"A morelikethis() query has to be sorted to return the scores, but its match is a {sortedQuery.GetType().Name}.");
                    }
                }

                var read = 0;
                long returnedDocs = 0;
                long skippedDocs = 0;
                Page page = default;
                while ((read = sortedQuery.Fill(ids.AsSpan())) != 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        if (returnedDocs >= query.PageSize)
                            yield break;

                        var hit = ids[i];
                        token.ThrowIfCancellationRequested();

                        var id = _documentIdReader.GetTermFor(hit);
                        if (id == null)
                            continue;

                        if (ravenIds.Add(id) == false)
                            continue;

                        if (skippedDocs < query.Start)
                        {
                            skippedDocs++;
                            continue;
                        }

                        var termsReader = IndexSearcher.GetEntryTermsReader(hit, ref page, _entryTermsKey ??= new CompactKey());
                        var documentScore = sortingData.IncludeScores ? sortingData.ScoresBuffer[i] : (float?)null;
                        var retrieverInput = new RetrieverInput(IndexSearcher, _fieldMappings, termsReader, id, _index.IndexFieldsPersistence.HasTimeValues, documentScore);
                        var result = retriever.Get(ref retrieverInput, token);

                        if (result.Document != null)
                        {
                            returnedDocs++;
                            yield return new QueryResult { Result = result.Document };
                        }
                        else if (result.List != null)
                        {
                            foreach (Document item in result.List)
                            {
                                returnedDocs++;
                                yield return new QueryResult { Result = item };
                            }
                        }
                    }
                }
            }
            finally
            {
                QueryPool.Return(ids);
                if (sortingData.ScoresBuffer != null)
                    ScorePool.Return(sortingData.ScoresBuffer);
            }
        }

        private IQueryMatch PrepareMoreLikeThis(IndexQueryServerSide query, DocumentsOperationContext context, out RavenRavenMoreLikeThis mlt, out long? baseDocId, CancellationToken token)
        {
            IDisposable releaseServerContext = null;
            IDisposable closeServerTransaction = null;
            TransactionOperationContext serverContext = null;
            MoreLikeThisQuery moreLikeThisQuery;
            CoraxQueryBuilder.Parameters builderParameters;
            var hasJoins = query.Metadata.Query.Joins != null;
            IQueryMatch joinedQuery = null;
            mlt = null;
            baseDocId = null;

            // take counts entries, the loop counts documents: +1 for the base document, times fanout
            var maxOutputsPerDocument = Math.Max(1, _maxNumberOfOutputsPerDocument); // zero until the index has run
            long take = query.Start + query.PageSize + 1;

            take = take < 0 || take > int.MaxValue / maxOutputsPerDocument // query.Start is unbounded, and take is cast to int below
                ? CoraxConstants.IndexSearcher.TakeAll
                : take * maxOutputsPerDocument;

            if (take > IndexSearcher.NumberOfEntries)
                take = CoraxConstants.IndexSearcher.TakeAll;

            try
            {
                if (query.Metadata.HasCmpXchg)
                {
                    releaseServerContext = context.DocumentDatabase.ServerStore.ContextPool.AllocateOperationContext(out serverContext);
                    closeServerTransaction = serverContext.OpenReadTransaction();
                }

                using (closeServerTransaction)
                {
                    builderParameters = new(IndexSearcher, _allocator, serverContext, context, query, _index, query.QueryParameters, QueryBuilderFactories,
                        _fieldMappings, null, null /* allow highlighting? */, hasJoins ? (int)take : CoraxQueryBuilder.TakeAll, deduplicationDisabled: true, indexReadOperation: this, token: token);
                    moreLikeThisQuery = CoraxQueryBuilder.BuildMoreLikeThisQuery(builderParameters, query.Metadata.Query.Where, buildFilter: hasJoins == false);

                    // the where clause of a query with a JOIN is built with the server transaction open
                    if (hasJoins)
                        joinedQuery = BuildMoreLikeThisMatch(query, context, moreLikeThisQuery, builderParameters, take, out mlt, out baseDocId, token);
                }
            }
            finally
            {
                releaseServerContext?.Dispose();
            }

            return hasJoins
                ? joinedQuery
                : BuildMoreLikeThisMatch(query, context, moreLikeThisQuery, null, take, out mlt, out baseDocId, token);
        }

        private IQueryMatch BuildMoreLikeThisMatch(IndexQueryServerSide query, DocumentsOperationContext context, MoreLikeThisQuery moreLikeThisQuery,
            CoraxQueryBuilder.Parameters builderParameters, long take, out RavenRavenMoreLikeThis mlt, out long? baseDocId, CancellationToken token)
        {
            var joinBuilderParameters = builderParameters;
            var options = moreLikeThisQuery.Options != null ? JsonDeserializationServer.MoreLikeThisOptions(moreLikeThisQuery.Options) : MoreLikeThisOptions.Default;

            HashSet<string> stopWords = null;
            if (string.IsNullOrWhiteSpace(options.StopWordsDocumentId) == false)
            {
                var stopWordsDoc = context.DocumentDatabase.DocumentsStorage.Get(context, options.StopWordsDocumentId);
                if (stopWordsDoc == null)
                    throw new InvalidOperationException($"Stop words document {options.StopWordsDocumentId} could not be found");

                if (stopWordsDoc.Data.TryGet(nameof(MoreLikeThisStopWords.StopWords), out BlittableJsonReaderArray value) && value != null)
                {
                    stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < value.Length; i++)
                        stopWords.Add(value.GetStringByIndex(i));
                }
            }

            builderParameters = new(IndexSearcher, _allocator, null, context, query, _index, query.QueryParameters, QueryBuilderFactories,
                _fieldMappings, null, null /* allow highlighting? */, CoraxQueryBuilder.TakeAll, deduplicationDisabled:true, indexReadOperation: this, token: token);
            mlt = new RavenRavenMoreLikeThis(builderParameters, options);
            baseDocId = null;

            try
            {
                if (moreLikeThisQuery.BaseDocument == null)
                {
                    Span<long> docsIds = stackalloc long[16];

                    // the entry id of the given RavenDB doc ID
                    if (moreLikeThisQuery.BaseDocumentQuery.Fill(docsIds) == 0)
                        throw new InvalidOperationException("Given filtering expression did not yield any documents that could be used as a base of comparison");

                    baseDocId = docsIds[0];
                }

                if (stopWords != null)
                    mlt.SetStopWords(stopWords);

                string[] fieldNames;
                if (options.Fields != null && options.Fields.Length > 0)
                    fieldNames = options.Fields;
                else
                {
                    fieldNames = new string[_fieldMappings.Count];
                    var index = 0;
                    foreach (var binding in _fieldMappings)
                    {
                        if (binding.FieldNameAsString is Client.Constants.Documents.Indexing.Fields.DocumentIdFieldName or Client.Constants.Documents.Indexing.Fields.SourceDocumentIdFieldName or Client.Constants.Documents.Indexing.Fields.ReduceKeyHashFieldName)
                            continue;
                        fieldNames[index++] = binding.FieldNameAsString;

                    }

                    if (index < fieldNames.Length)
                        Array.Resize(ref fieldNames, index);
                }

                mlt.SetFieldNames(fieldNames);

                IQueryMatch mltQuery;
                if (baseDocId.HasValue)
                {
                    mltQuery = mlt.Like(baseDocId.Value);
                }
                else
                {
                    using (var blittableJson = ParseJsonStringIntoBlittable(moreLikeThisQuery.BaseDocument, context))
                        mltQuery = mlt.Like(blittableJson);
                }

                if (joinBuilderParameters != null)
                {
                    var taken = false;
                    joinBuilderParameters.MoreLikeThisMatch = () =>
                    {
                        if (taken)
                            throw new InvalidQueryException("A query that has a JOIN can contain only one morelikethis().", query.Query, query.QueryParameters);

                        taken = true;
                        return mltQuery;
                    };

                    var joined = CoraxQueryBuilder.BuildQuery(joinBuilderParameters, out var sortMetadata);
                    if (taken == false)
                        throw new InvalidQueryException("The morelikethis() of the query did not become part of the query that was built (a query with a JOIN builds it from the where clause), so it would have been silently ignored.", query.Query, query.QueryParameters);

                    // the query orders by something else than the score
                    return sortMetadata != null
                        ? joined
                        : IndexSearcher.OrderBy(joined, new OrderMetadata(hasBoost: true, MatchCompareFieldType.Score), _index.Configuration.NullsSortMode, (int)take, token);
                }

                if (moreLikeThisQuery.FilterQuery != null && moreLikeThisQuery.FilterQuery is AllEntriesMatch == false)
                {
                    mltQuery = IndexSearcher.And(mltQuery, moreLikeThisQuery.FilterQuery, token: token);
                }

                return IndexSearcher.OrderBy(mltQuery, new OrderMetadata(hasBoost: true, MatchCompareFieldType.Score), _index.Configuration.NullsSortMode, (int)take, token);
            }
            catch
            {
                mlt.Dispose();
                throw;
            }
        }

        public string GetDocumentIdFor(long entryId)
        {
            return _documentIdReader.GetTermFor(entryId);
        }
        
        public override IEnumerable<BlittableJsonReaderObject> IndexEntries(IndexQueryServerSide query, Reference<long> totalResults,
            DocumentsOperationContext documentsContext, Func<string, SpatialField> getSpatialField, bool ignoreLimit, CancellationToken token)
        {
            var pageSize = query.PageSize;
            var position = query.Start;

            if (query.Metadata.IsDistinct)
                throw new NotSupportedInCoraxException("We don't support Distinct in \"Show Raw Entry\" of Index.");
            if (query.Metadata.FilterScript != null)
                throw new NotSupportedInCoraxException(
                    "Filter isn't supported in Raw Index View.");

            var take = pageSize + position;
            if (take > IndexSearcher.NumberOfEntries)
                take = CoraxConstants.IndexSearcher.TakeAll;

            IQueryMatch queryMatch;
            var builderParameters = new CoraxQueryBuilder.Parameters(IndexSearcher, _allocator, null, documentsContext, query, _index, query.QueryParameters, QueryBuilderFactories, _fieldMappings, null, null, -1, deduplicationDisabled: false, indexReadOperation: this, token: token);
            if ((queryMatch = CoraxQueryBuilder.BuildQuery(builderParameters, out _)) is null)
                yield break;

            var ids = QueryPool.Rent(CoraxBufferSize(IndexSearcher, take, query));
            int docsToLoad = CoraxBufferSize(IndexSearcher, pageSize, query);
            using var coraxEntryReader = new CoraxIndexedEntriesReader(documentsContext, IndexSearcher);
            int read;
            long i = Skip();
            Page page = default;
            var alreadySeenDocuments = new HashSet<long>();
            
            while (true)
            {
                token.ThrowIfCancellationRequested();
                for (; docsToLoad != 0 && i < read; ++i, --docsToLoad)
                {
                    var coraxInternalEntryId = ids[i];
                    if (alreadySeenDocuments.Add(coraxInternalEntryId) == false)
                        continue;
                    
                    token.ThrowIfCancellationRequested();
                    var reader = IndexSearcher.GetEntryTermsReader(coraxInternalEntryId, ref page, _entryTermsKey ??= new CompactKey());
                    var id = _documentIdReader.GetTermFor(coraxInternalEntryId);
                    yield return documentsContext.ReadObject(coraxEntryReader.GetDocument(ref reader), id);
                }

                if ((read = queryMatch.Fill(ids)) == 0)
                    break;
                totalResults.Value += read;
            }

            QueryPool.Return(ids);
            long Skip()
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    read = queryMatch.Fill(ids);
                    totalResults.Value += read;

                    if (position > read)
                    {
                        position -= read;
                        continue;
                    }

                    if (position == read)
                    {
                        read = queryMatch.Fill(ids);
                        totalResults.Value += read;
                        return 0;
                    }

                    return position;
                }
            }
        }

        public override HashSet<FieldDebugInfo> GetEntriesFields(ICollection<string> unknownTypeStaticFields)
        {
            var fields = new HashSet<FieldDebugInfo>();
            foreach (var staticField in unknownTypeStaticFields)
            {
                var termType = IndexSearcher.IsVectorField(staticField) ? 
                    IndexedValueType.Vector 
                    : IndexedValueType.Term;
                
                fields.Add(new FieldDebugInfo(staticField, IndexFieldType.Static, termType));
            }
            
            var fieldsInIndex = IndexSearcher.GetFields();
            foreach (var fieldName in fieldsInIndex)
            {
                if (fields.Select(x => x.Name).Contains(fieldName))
                    continue;
                
                if (IsDynamicFieldKnownAsStatic(fieldName))
                    continue;
                
                var termType = IndexSearcher.IsVectorField(fieldName) ? 
                    IndexedValueType.Vector 
                    : IndexedValueType.Term;
                
                fields.Add(new (fieldName, IndexFieldType.Dynamic, termType));
            }

            return fields;
        }

        public override void Dispose()
        {
            base.Dispose();

            var exceptionAggregator = new ExceptionAggregator($"Could not dispose {nameof(CoraxIndexReadOperation)} of {_index.Name}");
            exceptionAggregator.Execute(() => IndexSearcher?.Dispose());
            exceptionAggregator.ThrowIfNeeded();
        }
        
        [DoesNotReturn]
        private static void ThrowDistinctOnBiggerCollectionThanInt32()
        {
            throw new NotSupportedInCoraxException($"Corax doesn't support 'Distinct' operation on collection bigger than int32 ({int.MaxValue}).");
        }

        [DoesNotReturn]
        private static void ThrowExplanationsIsNotImplementedInCorax()
        {
            throw new NotSupportedInCoraxException($"{nameof(Corax)} doesn't support {nameof(Explanations)} yet.");
        }
    }
}
