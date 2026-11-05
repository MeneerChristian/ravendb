using System;
using System.Collections.Generic;
using Corax.Mappings;
using Corax.Querying.Matches.Meta;
using Voron;
using Voron.Data.CompactTrees;

namespace Corax.Querying.Matches.TermProviders
{
    // ordered by the bytes of the keys
    internal struct JoinKeysTermProvider : ITermProvider
    {
        private readonly IndexSearcher _searcher;
        private readonly FieldMetadata _field;
        private readonly JoinTermScores _keys;
        private readonly bool _encoded;
        private readonly JoinKeyLookup _lookup; // shared by the copies of this struct; it continues from the previous key only when that is the smaller one
        private int _termIndex;

        public JoinKeysTermProvider(IndexSearcher searcher, in FieldMetadata field, CompactTree tree, JoinTermScores keys, bool keysAreIndexedTerms, bool useEncodedKeys = false)
        {
            _searcher = searcher;
            _field = keysAreIndexedTerms ? field.ChangeAnalyzer(FieldIndexingMode.Exact) : field;
            _keys = keys;
            _encoded = searcher.CanLookUpEncodedKeys(tree, keys, useEncodedKeys);
            _termIndex = -1;

            // the terms are handed out one by one, and the query does other lookups on the tree of the field in between
            var readOnly = searcher.Transaction.LowLevelTransaction.Flags == TransactionFlags.Read;
            _lookup = new JoinKeyLookup(searcher, readOnly ? tree.OpenReaderWithOwnCursor() : tree, _field, keysAreIndexedTerms, keys.Count, sweepAllowed: false, cursorAllowed: readOnly);
        }

        public bool IsFillSupported => false;

        public int Fill(Span<long> containers) => throw new NotImplementedException();

        public void Reset()
        {
            _termIndex = -1;
            _lookup.Restart();
        }

        public bool Next(out long termId, out double termRatioToWholeCollection)
        {
            // the keys are looked up in the order of their bytes
            while (++_termIndex < _keys.Count)
            {
                var key = _keys.SortedOrder()[_termIndex];
                var lookedUp = _encoded
                    ? _lookup.TryGetContainerIdEncoded(_keys.EncodedTerm(key), _keys.EncodedBits(key), out termId)
                    : _lookup.TryGetContainerId(_keys.Term(key), out termId);
                if (lookedUp == false)
                    continue;

                termRatioToWholeCollection = 1D;
                return true;
            }

            _lookup.Dispose();
            termId = 0;
            termRatioToWholeCollection = 0;
            return false;
        }

        public QueryInspectionNode Inspect()
        {
            return new QueryInspectionNode(nameof(JoinKeysTermProvider), parameters: new Dictionary<string, string>
            {
                { Constants.QueryInspectionNode.FieldName, _field.ToString() },
                { Constants.QueryInspectionNode.Term, $"{_keys.Count} join keys" }
            });
        }

        string DebugView => Inspect().ToString();
    }
}
