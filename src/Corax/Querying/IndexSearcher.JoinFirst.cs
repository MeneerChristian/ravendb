using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Collections.Generic;
using System.Globalization;
using Corax.Mappings;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;
using Corax.Utils;
using Sparrow.Server;
using Sparrow.Server.Utils.VxSort;
using Voron;
using Voron.Data.CompactTrees;
using Voron.Util;

namespace Corax.Querying;

public partial class IndexSearcher
{
    // The entries that are read for the first results of a join are at least as many as the keys, so a small join does not give up too early
    private const int JoinFirstMinimumBudget = 1024;
    private const int JoinFirstChunk = 256;
    private const int JoinFirstFirstBatch = 64;
    private const int JoinFirstMaximumBatch = 4096;

    // The first `take` entries (in the order of their ids) that hold one of the keys in the field, among the entries of `where` or among all entries.
    // The entries are tested one by one, so the lists of the keys are not merged. The number of entries of the join is exact when countTotal is set.
    // False when the join has to be answered another way: the match is not made then, and `where` is not read.
    // Every entry holds at most one term of the field. membershipByKey: the term of an entry is one of the keys exactly when it equals the key as it is
    public bool TryJoinFirstEntries(in FieldMetadata field, JoinTermScores keys, bool keysAreIndexedTerms, bool useEncodedKeys, bool membershipByKey, IQueryMatch where, int take, bool countTotal,
        out JoinFirstEntriesMatch match)
    {
        var state = new JoinFirstState(this, take);
        try
        {
            if (state.Run(field, keys, keysAreIndexedTerms, useEncodedKeys, membershipByKey, where, countTotal))
            {
                match = new JoinFirstEntriesMatch(state);
                return true;
            }
        }
        catch
        {
            state.Dispose();
            throw;
        }

        state.Dispose();
        match = default;
        return false;
    }

    // The term of the entry is held by a term of the tree that one of the keys was looked up as
    private readonly struct JoinTermTest(CompactTree tree, ContextBoundNativeList<long> found)
    {
        public bool Accepts(ref EntryTermsReader reader)
        {
            return tree.TryGetValue(reader.Current, out var containerId) && found.ToSpan().BinarySearch(containerId) >= 0;
        }
    }

    internal sealed class JoinFirstState : IDisposable
    {
        private readonly IndexSearcher _searcher;
        private readonly long _take;
        private ContextBoundNativeList<long> _results;
        private int _alreadyRead;
        private int _resultCount;

        // The number of entries the join has, when it is known: the read of the first entries may end before the others are seen
        public long Total;
        public bool TotalKnown;

        // More entries may follow the ones that are returned
        public bool HasMore;

        public long Scanned;
        public bool Probed;
        public bool Collected;

        public JoinFirstState(IndexSearcher searcher, int take)
        {
            _searcher = searcher;
            _take = take;
            _results = new ContextBoundNativeList<long>(searcher.Allocator);
        }

        public bool Run(in FieldMetadata field, JoinTermScores keys, bool keysAreIndexedTerms, bool useEncodedKeys, bool membershipByKey, IQueryMatch where, bool countTotal)
        {
            var tree = _searcher._fieldsTree?.CompactTreeFor(field.FieldName);
            if (tree == null || keys.Count == 0)
            {
                // the field does not exist: nothing matches
                TotalKnown = true;
                return true;
            }

            var rootPage = _searcher.FieldCache.GetLookupRootPage(field.FieldName);
            var encoded = _searcher.CanLookUpEncodedKeys(tree, keys, useEncodedKeys);
            var budget = Math.Max(keys.Count, JoinFirstMinimumBudget);

            // the empty key is stored as a marker in the tree, an entry does not hold it as itself
            if (countTotal == false && membershipByKey && _take > 0 && HoldsEmptyKey(keys) == false)
            {
                if (where != null)
                {
                    if (where.Count > 4 * budget)
                        return false;

                    var entries = JoinWalkState.MaterializeEntries(_searcher, where);
                    try
                    {
                        _lastEnd = ScanKeys(keys, encoded, rootPage, entries.ToSpan(), useAll: false, _take, long.MaxValue);
                    }
                    finally
                    {
                        entries.Dispose();
                    }

                    FinishWalk();
                    return true;
                }

                _lastEnd = ScanKeys(keys, encoded, rootPage, default, useAll: true, _take, budget);
                if (_lastEnd != WalkEnd.BudgetSpent)
                {
                    FinishWalk();
                    return true;
                }

                _results.Clear();
            }

            if (where != null)
                return false;

            Probe(tree, field, keys, keysAreIndexedTerms, encoded, rootPage);
            return true;
        }

        private static bool HoldsEmptyKey(JoinTermScores keys)
        {
            for (var i = 0; i < keys.Count; i++)
            {
                if (keys.IsEncoded ? keys.EncodedBits(i) == 0 : keys.Term(i).Length == 0)
                    return true;
            }

            return false;
        }

        private WalkEnd _lastEnd;

        // The walk ended without a count of the join: it knows the count only when it has seen every entry
        private void FinishWalk()
        {
            if (_lastEnd == WalkEnd.Found)
            {
                HasMore = true;
                return;
            }

            Total = _results.Count;
            TotalKnown = true;
        }

        private enum WalkEnd
        {
            Found,
            BudgetSpent,
            EntriesEnded
        }

        // Walks all entries in the order of their ids
        private WalkEnd WalkAll(ref JoinTermTest test, long rootPage, long limit, long budget)
        {
            var all = _searcher.AllEntries();
            var buffer = ArrayPool<long>.Shared.Rent(JoinFirstChunk);
            try
            {
                int read;
                while ((read = all.Fill(buffer.AsSpan(0, JoinFirstChunk))) > 0)
                {
                    var end = Walk(ref test, rootPage, buffer.AsSpan(0, read), limit, budget);
                    if (end != WalkEnd.EntriesEnded)
                    {
                        _lastEnd = end;
                        return end;
                    }
                }

                _lastEnd = WalkEnd.EntriesEnded;
                return WalkEnd.EntriesEnded;
            }
            finally
            {
                ArrayPool<long>.Shared.Return(buffer);
            }
        }

        // Tests the entries one by one until `limit` of them hold a key
        private WalkEnd Walk(ref JoinTermTest test, long rootPage, ReadOnlySpan<long> entries, long limit, long budget)
        {
            var llt = _searcher._transaction.LowLevelTransaction;
            using var scope = new CompactKeyCacheScope(llt);
            var key = scope.Key;

            // the reader of every entry initializes the key again, the scope releases an initialized one
            key.Initialize(llt);
            Page page = default;
            foreach (var entry in entries)
            {
                if (_results.Count >= limit)
                    return WalkEnd.Found;

                if (Scanned >= budget)
                    return WalkEnd.BudgetSpent;

                Scanned++;
                var reader = _searcher.GetEntryTermsReader(entry, ref page, key);
                if (reader.FindNext(rootPage) && reader.IsNull == false && reader.IsNonExisting == false && test.Accepts(ref reader))
                {
                    JoinTermScores.Room(ref _results, 1);
                    _results.Add(entry);
                }
            }

            return _results.Count >= limit ? WalkEnd.Found : WalkEnd.EntriesEnded;
        }

        // The first entries whose term is one of the keys, without sorting or looking up the keys: the entries are read in batches that grow, and the keys are
        // scanned once for each batch for the terms of its entries. `fixedEntries` are the entries to read, all entries when `useAll` is set.
        private WalkEnd ScanKeys(JoinTermScores keys, bool encoded, long rootPage, ReadOnlySpan<long> fixedEntries, bool useAll, long limit, long budget)
        {
            var llt = _searcher._transaction.LowLevelTransaction;
            var allocator = _searcher.Allocator;
            var all = useAll ? _searcher.AllEntries() : default;
            var position = 0;
            var batch = JoinFirstFirstBatch;
            var entryBuffer = ArrayPool<long>.Shared.Rent(JoinFirstMaximumBatch);
            var candidates = ArrayPool<long>.Shared.Rent(JoinFirstMaximumBatch);
            var ends = ArrayPool<int>.Shared.Rent(JoinFirstMaximumBatch);
            var bits = ArrayPool<int>.Shared.Rent(JoinFirstMaximumBatch);
            var next = ArrayPool<int>.Shared.Rent(JoinFirstMaximumBatch);
            var hits = ArrayPool<bool>.Shared.Rent(JoinFirstMaximumBatch);
            var heads = ArrayPool<int>.Shared.Rent(2 * JoinFirstMaximumBatch);
            var terms = new ContextBoundNativeList<byte>(allocator);
            using var scope = new CompactKeyCacheScope(llt);
            var key = scope.Key;
            key.Initialize(llt);
            Page page = default;
            try
            {
                while (true)
                {
                    int read;
                    if (useAll)
                        read = all.Fill(entryBuffer.AsSpan(0, batch));
                    else
                    {
                        read = Math.Min(batch, fixedEntries.Length - position);
                        fixedEntries.Slice(position, read).CopyTo(entryBuffer);
                        position += read;
                    }

                    if (read == 0)
                        return WalkEnd.EntriesEnded;

                    if (Scanned + read > budget)
                        return WalkEnd.BudgetSpent;

                    // the entries of the batch that hold a term of the field
                    var count = 0;
                    terms.Clear();
                    for (var i = 0; i < read; i++)
                    {
                        Scanned++;
                        var reader = _searcher.GetEntryTermsReader(entryBuffer[i], ref page, key);
                        if (reader.FindNext(rootPage) == false || reader.IsNull || reader.IsNonExisting)
                            continue;

                        // the dictionary of the entry is the one of the tree, which has the table that encoded the keys
                        var termBits = 0;
                        var term = encoded ? reader.Current.EncodedWithCurrent(out termBits) : reader.Current.Decoded();
                        bits[count] = termBits;
                        JoinTermScores.Room(ref terms, term.Length);
                        terms.AddRange(term);
                        ends[count] = terms.Count;
                        candidates[count++] = entryBuffer[i];
                    }

                    if (count > 0)
                        MarkCandidatesHeldByKeys(keys, encoded, terms, count, ends, bits, next, heads, hits);

                    for (var i = 0; i < count; i++)
                    {
                        if (hits[i] == false)
                            continue;

                        if (_results.Count >= limit)
                            return WalkEnd.Found;

                        JoinTermScores.Room(ref _results, 1);
                        _results.Add(candidates[i]);
                    }

                    if (_results.Count >= limit)
                        return WalkEnd.Found;

                    batch = Math.Min(batch * 2, JoinFirstMaximumBatch);
                }
            }
            finally
            {
                ArrayPool<long>.Shared.Return(entryBuffer);
                ArrayPool<long>.Shared.Return(candidates);
                ArrayPool<int>.Shared.Return(ends);
                ArrayPool<int>.Shared.Return(bits);
                ArrayPool<int>.Shared.Return(next);
                ArrayPool<bool>.Shared.Return(hits);
                ArrayPool<int>.Shared.Return(heads);
                terms.Dispose();
            }
        }

        // hits[i]: the term of candidate i is one of the keys. The candidates are put in a hash table, every key is looked up in it once
        private static unsafe void MarkCandidatesHeldByKeys(JoinTermScores keys, bool encoded, ContextBoundNativeList<byte> terms, int count, int[] ends, int[] bits, int[] next, int[] heads, bool[] hits)
        {
            var slots = 1 << (BitOperations.Log2((uint)count) + 1);
            var shift = 64 - BitOperations.Log2((uint)slots);
            heads.AsSpan(0, slots).Fill(-1);
            hits.AsSpan(0, count).Clear();
            for (var i = 0; i < count; i++)
            {
                var slot = (int)(HashOf(Candidate(terms, ends, i), bits[i]) >> shift);
                next[i] = heads[slot];
                heads[slot] = i;
            }

            var left = count;
            for (var k = 0; k < keys.Count && left > 0; k++)
            {
                var term = encoded ? keys.EncodedTerm(k) : keys.Term(k);
                var termBits = encoded ? keys.EncodedBits(k) : 0;
                for (var candidate = heads[(int)(HashOf(term, termBits) >> shift)]; candidate != -1; candidate = next[candidate])
                {
                    if (bits[candidate] != termBits || term.SequenceEqual(Candidate(terms, ends, candidate)) == false)
                        continue;

                    if (hits[candidate] == false)
                        left--;
                    hits[candidate] = true;
                }
            }
        }

        private static unsafe ReadOnlySpan<byte> Candidate(ContextBoundNativeList<byte> terms, int[] ends, int index)
        {
            var start = index == 0 ? 0 : ends[index - 1];
            return new ReadOnlySpan<byte>(terms.RawItems + start, ends[index] - start);
        }

        // The end of the term decides, ids share their beginning
        private static ulong HashOf(ReadOnlySpan<byte> term, int bits)
        {
            Span<byte> tail = stackalloc byte[sizeof(ulong)];
            tail.Clear();
            term.Slice(Math.Max(0, term.Length - tail.Length)).CopyTo(tail);
            return (BinaryPrimitives.ReadUInt64LittleEndian(tail) ^ ((ulong)term.Length << 32 | (uint)bits)) * 0x9E3779B97F4A7C15UL;
        }

        // The keys are looked up in the tree, the entries under the terms that hold them are counted (exact: an entry has one term in the field)
        private void Probe(CompactTree tree, in FieldMetadata field, JoinTermScores keys, bool keysAreIndexedTerms, bool encoded, long rootPage)
        {
            var allocator = _searcher.Allocator;
            var termField = keysAreIndexedTerms ? field.ChangeAnalyzer(FieldIndexingMode.Exact) : field;
            var found = new ContextBoundNativeList<long>(allocator, keys.Count);
            try
            {
                // the keys are looked up in the order of their bytes
                ReadOnlySpan<int> order = keys.SortedOrder();
                using (var lookup = new JoinKeyLookup(_searcher, tree, termField, keysAreIndexedTerms, keys.Count, sweepAllowed: true))
                {
                    for (var k = 0; k < keys.Count; k++)
                    {
                        var key = order[k];
                        var lookedUp = encoded
                            ? lookup.TryGetContainerIdEncoded(keys.EncodedTerm(key), keys.EncodedBits(key), out var containerId)
                            : lookup.TryGetContainerId(keys.Term(key), out containerId);
                        if (lookedUp)
                            found.Add(containerId);
                    }
                }

                if (found.Count == 0)
                {
                    Probed = true;
                    TotalKnown = true;
                    return;
                }

                // keys that differ in case only are looked up as the same term, which is counted once
                var terms = found.ToSpan();
                Sort.Run(terms);
                var unique = 0;
                for (var i = 0; i < terms.Length; i++)
                {
                    if (i == 0 || terms[i] != terms[i - 1])
                        terms[unique++] = terms[i];
                }

                found.Shrink(unique);

                Probed = true;
                TotalKnown = true;
                foreach (var term in found.ToSpan())
                    Total += _searcher.NumberOfDocumentsUnderSpecificTerm(term);

                if (Total == 0 || _take == 0)
                    return;

                var limit = Math.Min(_take, Total);
                HasMore = limit < Total;

                // the entries are tested in the order of their ids when the first of them come soon; else the lists of the terms are read
                var budget = Math.Max(Total, JoinFirstMinimumBudget);
                if ((double)limit * _searcher.NumberOfEntries / Total <= budget)
                {
                    var test = new JoinTermTest(tree, found);
                    if (WalkAll(ref test, rootPage, limit, budget) == WalkEnd.Found)
                        return;

                    _results.Clear();
                }

                Collected = true;
                CollectFirst(found, limit);
            }
            finally
            {
                found.Dispose();
            }
        }

        // The `limit` smallest entries of the lists of the terms
        private void CollectFirst(ContextBoundNativeList<long> terms, long limit)
        {
            var allocator = _searcher.Allocator;
            var buffer = ArrayPool<long>.Shared.Rent(JoinPostingBufferSize);
            var drain = new PostingDrain(_searcher, allocator, buffer);
            var collected = new ContextBoundNativeList<long>(allocator);
            try
            {
                var consumer = new JoinWalkState.CollectConsumer(ref collected, default, false, (int)limit, single: terms.Count == 1);
                foreach (var term in terms.ToSpan())
                    drain.Drain(term, ref consumer);

                var entries = collected.ToSpan();
                entries.Sort();
                JoinTermScores.Room(ref _results, (int)limit);
                _results.AddRange(entries.Slice(0, Math.Min((int)limit, entries.Length)));
            }
            finally
            {
                drain.Dispose();
                ArrayPool<long>.Shared.Return(buffer);
                collected.Dispose();
            }
        }

        public int Fill(Span<long> matches)
        {
            if (_results.HasContext == false)
                return 0;

            var read = _results.CopyTo(matches, _alreadyRead);
            if (read != 0)
            {
                _alreadyRead += read;
                return read;
            }

            _alreadyRead = 0;
            Dispose();
            return 0;
        }

        // The entries that were found, also after they were read
        public int ResultCount => _results.HasContext ? _results.Count : _resultCount;

        public void Dispose()
        {
            if (_results.HasContext)
            {
                _resultCount = _results.Count;
                _results.Dispose();
            }

            _results = default;
        }
    }
}

public struct JoinFirstEntriesMatch : IQueryMatch
{
    private readonly IndexSearcher.JoinFirstState _state;

    internal JoinFirstEntriesMatch(IndexSearcher.JoinFirstState state)
    {
        _state = state;
    }

    // The number of entries of the join when it was asked for, else the number of entries that were returned and one more when others may follow
    public long TotalResults => _state.TotalKnown ? _state.Total : _state.ResultCount + (_state.HasMore ? 1 : 0);

    public bool TotalIsKnown => _state.TotalKnown;

    // Entries tested, tells how far into the order of the ids the first entries were
    public long ScannedEntries => _state.Scanned;

    public bool ProbedKeys => _state.Probed;

    public bool CollectedFromLists => _state.Collected;

    public long Count => TotalResults;

    public SkipSortingResult AttemptToSkipSorting() => SkipSortingResult.ResultsNativelySorted;

    public QueryCountConfidence Confidence => QueryCountConfidence.High;

    public bool IsBoosting => false;

    public DuplicatesOccurrence DuplicatesOccurrenceStatus => DuplicatesOccurrence.NotPossible;

    public int Fill(Span<long> matches) => _state.Fill(matches);

    public int AndWith(Span<long> buffer, int matches) => throw new NotSupportedException($"{nameof(JoinFirstEntriesMatch)} does not support the operation of {nameof(AndWith)}.");

    public void Score(Span<long> matches, Span<float> scores, float boostFactor)
    {
    }

    public QueryInspectionNode Inspect()
    {
        return new QueryInspectionNode(nameof(JoinFirstEntriesMatch),
            parameters: new Dictionary<string, string>
            {
                { Constants.QueryInspectionNode.IsBoosting, IsBoosting.ToString() },
                { Constants.QueryInspectionNode.Count, Count.ToString(CultureInfo.InvariantCulture) },
            });
    }
}
