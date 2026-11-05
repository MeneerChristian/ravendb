using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Corax;
using Corax.Querying;
using Index = Raven.Server.Documents.Indexes.Index;
using Raven.Server.Documents.Indexes.Persistence.Corax;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Queries.Timings;
using Raven.Server.ServerWide.Context;
using Sparrow.Server;

namespace Raven.Server.Documents.Queries
{
    internal sealed class JoinResult : IDisposable
    {
        public string Alias { get; init; }

        // The alias of the join of the main document this result was read for (matches(f) of a chained alias reads the sub-query of its root cut to the path of f)
        public string RootAlias { get; init; }

        public List<JoinResult> Nested { get; internal set; }

        // For a join on the id: the id and score of every document the sub-query matched
        public JoinTermScores Keys { get; internal set; }

        // The score of every key is its dense rank in the order of the joined index, 1 for the first
        public bool RankedByInnerOrder { get; init; }

        // The order of the keys in the joined index, while the keys have not been given their ranks: the outer query may read only the first entries of that order
        public JoinOrderedKeys OrderedKeys { get; internal set; }

        private bool _ranked;

        // Gives the keys their ranks when they have only the order
        public void EnsureRanked()
        {
            if (OrderedKeys == null || _ranked)
                return;

            OrderedKeys.RankKeys(Keys);
            _ranked = true;
        }

        // The keys are terms as stored in the joined index, they must not be analyzed again when matched
        public bool KeysAreTerms { get; init; }

        // How the joined index normalizes the keys
        public string KeyProbe { get; init; }

        // The outer field lowercases and the joined field keeps the case
        public bool FoldCase { get; internal set; }

        private FoldedKeys _foldedKeys;

        // FoldCase: the keys that differ in case only as one key, so that a row is scored once for it
        public JoinTermScores FoldedDistinctKeys { get; internal set; }

        // A join on several fields: the distinct terms of the first field among the keys
        public JoinTermScores CompositeFirstTerms { get; internal set; }

        // A join on several fields: the keys with the parts lowercased that the outer field lowercases
        public JoinTermScores CompositeProbe { get; internal set; }

        // A join on several fields: per equality, the probe key and indexing mode of the joined field
        public string[] PairKeyProbes { get; init; }
        public FieldIndexingMode[] PairInnerModes { get; init; }
        public string[] PairOuterPaths { get; init; }

        public ByteStringContext InnerAllocator { get; init; }

        private JoinTermScores ProbeKeys => CompositeProbe ?? Keys;

        // Join on several fields whose joined document is projected
        public bool OfferCompositeKey(ReadOnlySpan<byte> key, ref int best)
        {
            var keys = ProbeKeys;
            if (keys == null || keys.TryFind(key, out var index) == false)
                return false;

            if (best == -1 || keys.Beats(index, best))
                best = index;

            return true;
        }

        public string InnerIndexName { get; init; }
        public FieldIndexingMode InnerIndexingMode { get; init; }

        public QueryTimingsScope Timings { get; init; }

        // The reader of the joined index, used to project stored fields of a joined document without loading it
        public CoraxIndexReadOperation Reader { get; init; }

        public string OuterPath { get; init; }

        // Allocated while reading the sub-query, including the joins of the sub-query itself
        public long ScanAllocatedManagedBytes { get; internal set; }

        // A join on the id that the where clause combines with matches(alias), or a LEFT join on the id
        public bool IsOptional { get; init; }

        // LEFT JOIN: the join scores the rows it matches but never filters them (unless the where clause refers to it with matches(alias))
        public bool IsLeft { get; init; }

        // The comparison of fields of different documents this join stands for
        public JoinCompare Compare { get; init; }

        // The index field the join matches on, in the index of the query that holds the join
        public string OnFieldName { get; init; }

        public Index InnerIndex { get; init; }

        // ON id(main) = alias.field: the joined documents of a row are the ones whose field holds its id
        public bool IsOnIdOfMain { get; init; }

        public DocumentsOperationContext DocumentsContext { get; init; }

        // The ids of the joined documents of a row (ON id(main) = alias.field): the documents the sub-query matches whose field holds the id of the row, in the order of their ids
        public void AddDocumentIdsOfMainId(string mainId, List<string> ids, CancellationToken token)
        {
            if (Keys == null || Reader == null)
                throw new InvalidOperationException($"The join '{Alias}' has no joined documents to look the id '{mainId}' up in.");

            var maxBytes = Encoding.UTF8.GetMaxByteCount(mainId.Length);
            var rented = ArrayPool<byte>.Shared.Rent(maxBytes * 4);
            try
            {
                // the ids of the entries are lowercased, and a field that lowercases holds them that way
                var length = Encoding.UTF8.GetBytes(mainId, rented);
                var folded = rented.AsSpan(maxBytes, maxBytes * 3);
                var term = folded.Slice(0, CompositeKey.Fold(rented.AsSpan(0, length), folded));

                // only an id that is a key of the matched documents has joined documents: the sub-query is not read for the others
                if (Keys.TryFind(term, out _) == false)
                    return;

                Reader.AddDocumentIdsOfTerm(SubQuery, DocumentsContext, InnerField, term, ids, token);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        // The field of the joined documents that holds the key
        public string InnerField { get; init; }

        public IndexQueryServerSide SubQuery { get; init; }

        // The score of the joined documents is multiplied by this before it is added to the score of the outer query
        public float Boost { get; init; } = 1f;

        public bool MatchedDocument(string id)
        {
            if (Keys == null)
                throw new InvalidOperationException($"The join '{Alias}' has no keys to look the document '{id}' up in.");

            var maxBytes = Encoding.UTF8.GetMaxByteCount(id.Length);
            var rented = maxBytes <= 512 ? null : ArrayPool<byte>.Shared.Rent(maxBytes);
            try
            {
                Span<byte> buffer = rented ?? stackalloc byte[512];
                var length = Encoding.UTF8.GetBytes(id, buffer);
                return Reader.IsDocumentIdAmong(Keys, buffer.Slice(0, length));
            }
            finally
            {
                if (rented != null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        // Join on a field whose joined document is projected
        public bool OfferJoinedKey(ReadOnlySpan<byte> term, ref int best)
        {
            if (Keys == null)
                return false;

            int index;
            if (FoldCase)
            {
                if (TryFindFolded(term, out index) == false)
                    return false;
            }
            else if (Keys.TryFind(term, out index) == false)
            {
                return false;
            }

            if (best == -1 || Keys.Beats(index, best))
                best = index;

            return true;
        }

        // The id of the joined document that won the key that OfferJoinedKey kept
        public string JoinedDocumentIdOf(int key) => Reader.GetDocumentIdOfEntry(ProbeKeys.WinnerEntryId(key));

        // The terms of the outer field are lowercase
        private bool TryFindFolded(ReadOnlySpan<byte> term, out int index)
        {
            var folded = _foldedKeys;
            if (folded == null)
            {
                var created = new FoldedKeys(Keys);
                folded = Interlocked.CompareExchange(ref _foldedKeys, created, null) ?? created;
                if (ReferenceEquals(folded, created) == false)
                    created.Dispose();
            }

            return folded.TryFind(term, out index);
        }

        public void Dispose()
        {
            _foldedKeys?.Dispose();
            _foldedKeys = null;
            CompositeFirstTerms?.Dispose();
            CompositeFirstTerms = null;
            CompositeProbe?.Dispose();
            CompositeProbe = null;
            FoldedDistinctKeys?.Dispose();
            FoldedDistinctKeys = null;
            OrderedKeys?.Dispose();
            OrderedKeys = null;
            Keys?.Dispose();
            Keys = null;
        }
    }
}
