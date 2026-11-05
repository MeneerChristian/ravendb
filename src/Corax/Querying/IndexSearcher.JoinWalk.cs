using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using Corax.Mappings;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;
using Sparrow.Server;
using Sparrow.Server.Utils.VxSort;
using Voron.Util;

namespace Corax.Querying;

// The keys of a join in the order the joined index sorts them
public interface IJoinWalkOrder
{
    int Count { get; }

    // The index (in the keys of the join) of the key at a position of the order
    int KeyAt(int position);

    // tied[i]: the entry at position from + i has all the sort values of the entry before it (the first entry is never tied)
    void FillTies(int from, Span<bool> tied);
}

public partial class IndexSearcher
{
    private const int JoinWalkChunk = 1024;

    // The first `take` entries of the join in the order of the joined index, and how many entries the join has
    public JoinOrderedWalkMatch JoinOrderedWalk(in FieldMetadata field, JoinTermScores keys, IJoinWalkOrder order, bool keysAreIndexedTerms, IQueryMatch where, int take)
    {
        var state = new JoinWalkState(this, keys, order, take);
        try
        {
            state.CountEntries(field, keysAreIndexedTerms, where);
        }
        catch
        {
            state.Dispose();
            throw;
        }

        return new JoinOrderedWalkMatch(state);
    }

    internal sealed class JoinWalkState : IDisposable
    {
        private readonly IndexSearcher _searcher;
        private readonly JoinTermScores _keys;
        private readonly IJoinWalkOrder _order;
        private readonly long _take;

        private ContextBoundNativeList<long> _containers; // per key: the term of the outer field it equals, -1 when no entry holds it (or another key owns the term)
        private ContextBoundNativeList<int> _counts; // per key: the entries under it that pass the where
        private ContextBoundNativeList<long> _where;
        private bool _hasWhere;
        private ContextBoundNativeList<long> _results;
        private int _alreadyRead;
        private bool _built;

        public long Total;

        public JoinWalkState(IndexSearcher searcher, JoinTermScores keys, IJoinWalkOrder order, int take)
        {
            _searcher = searcher;
            _keys = keys;
            _order = order;
            _take = take < 0 ? long.MaxValue : take;
            _results = new ContextBoundNativeList<long>(searcher.Allocator);
        }

        public void CountEntries(in FieldMetadata field, bool keysAreIndexedTerms, IQueryMatch where)
        {
            var allocator = _searcher.Allocator;
            var keyCount = _keys.Count;
            var tree = _searcher._fieldsTree?.CompactTreeFor(field.FieldName);
            if (tree == null || keyCount == 0)
                return;

            if (where != null)
                MaterializeWhere(where);

            _containers = new ContextBoundNativeList<long>(allocator, keyCount);
            _containers.Count = keyCount;
            _containers.ToSpan().Fill(-1);

            var termField = keysAreIndexedTerms ? field.ChangeAnalyzer(FieldIndexingMode.Exact) : field;
            using (var lookup = new JoinKeyLookup(_searcher, tree, termField, keysAreIndexedTerms, keyCount, sweepAllowed: true))
            {
                // the keys are looked up in the order of their bytes
                ReadOnlySpan<int> byBytes = _keys.SortedOrder();
                var containers = _containers.ToSpan();
                for (var i = 0; i < keyCount; i++)
                {
                    var key = byBytes[i];
                    if (lookup.TryGetContainerId(_keys.Term(key), out var containerId))
                        containers[key] = containerId;
                }
            }

            OwnEveryTermOnce(keyCount);

            _counts = new ContextBoundNativeList<int>(allocator, keyCount);
            _counts.Count = keyCount;
            var counts = _counts.ToSpan();
            counts.Clear();

            var buffer = ArrayPool<long>.Shared.Rent(JoinPostingBufferSize);
            var drain = new PostingDrain(_searcher, allocator, buffer);
            try
            {
                var found = _containers.ToSpan();
                for (var key = 0; key < keyCount; key++)
                {
                    var containerId = found[key];
                    if (containerId == -1)
                        continue;

                    long count;
                    if (_hasWhere)
                    {
                        var consumer = new CountConsumer(_where.ToSpan());
                        drain.Drain(containerId, ref consumer);
                        count = consumer.Count;
                    }
                    else
                    {
                        count = _searcher.NumberOfDocumentsUnderSpecificTerm(containerId);
                    }

                    counts[key] = (int)count;
                    Total += count;
                }
            }
            finally
            {
                drain.Dispose();
                ArrayPool<long>.Shared.Return(buffer);
            }
        }

        private void MaterializeWhere(IQueryMatch where)
        {
            _hasWhere = true;
            _where = MaterializeEntries(_searcher, where);

            // a where that holds every entry filters nothing
            if (_where.Count == _searcher.NumberOfEntries)
            {
                _where.Dispose();
                _hasWhere = false;
            }
        }

        // The entries of the match, ascending, once each
        internal static ContextBoundNativeList<long> MaterializeEntries(IndexSearcher searcher, IQueryMatch match)
        {
            var entries = new ContextBoundNativeList<long>(searcher.Allocator);
            var buffer = ArrayPool<long>.Shared.Rent(JoinPostingBufferSize);
            try
            {
                int read;
                while ((read = match.Fill(buffer)) > 0)
                {
                    JoinTermScores.Room(ref entries, read);
                    entries.AddRange(new ReadOnlySpan<long>(buffer, 0, read));
                }
            }
            finally
            {
                ArrayPool<long>.Shared.Return(buffer);
            }

            // the entries are searched for: they have to be in order, once each
            var span = entries.ToSpan();
            var sorted = true;
            for (var i = 1; i < span.Length && sorted; i++)
                sorted = span[i - 1] < span[i];

            if (sorted == false)
            {
                Sort.Run(span);
                var unique = 0;
                for (var i = 0; i < span.Length; i++)
                {
                    if (i == 0 || span[i] != span[i - 1])
                        span[unique++] = span[i];
                }

                entries.Shrink(unique);
            }

            return entries;
        }

        // Keys that equal the same term (they differ in case only) share its entries: the first of them in the order has them
        private void OwnEveryTermOnce(int keyCount)
        {
            var allocator = _searcher.Allocator;
            var terms = new ContextBoundNativeList<long>(allocator, keyCount);
            var owners = new ContextBoundNativeList<int>(allocator, keyCount);
            ContextBoundNativeList<int> positions = default;
            try
            {
                var containers = _containers.ToSpan();
                for (var key = 0; key < keyCount; key++)
                {
                    if (containers[key] == -1)
                        continue;

                    terms.Add(containers[key]);
                    owners.Add(key);
                }

                var termSpan = terms.ToSpan();
                var ownerSpan = owners.ToSpan();
                MemoryExtensions.Sort(termSpan, ownerSpan);

                for (var start = 0; start < termSpan.Length;)
                {
                    var end = start + 1;
                    while (end < termSpan.Length && termSpan[end] == termSpan[start])
                        end++;

                    if (end - start > 1)
                    {
                        if (positions.HasContext == false)
                        {
                            positions = new ContextBoundNativeList<int>(allocator, keyCount);
                            positions.Count = keyCount;
                            for (var position = 0; position < keyCount; position++)
                                positions[_order.KeyAt(position)] = position;
                        }

                        var first = start;
                        for (var i = start + 1; i < end; i++)
                        {
                            if (positions[ownerSpan[i]] < positions[ownerSpan[first]])
                                first = i;
                        }

                        for (var i = start; i < end; i++)
                        {
                            if (i != first)
                                containers[ownerSpan[i]] = -1;
                        }
                    }

                    start = end;
                }
            }
            finally
            {
                terms.Dispose();
                owners.Dispose();
                if (positions.HasContext)
                    positions.Dispose();
            }
        }

        public int Fill(Span<long> matches)
        {
            if (_results.HasContext == false)
                return 0;

            if (_built == false)
            {
                _built = true;
                if (_counts.HasContext)
                    Walk();
            }

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

        // The keys in the order of the joined index; the entries of the keys that tie are in the order of their ids
        private void Walk()
        {
            var allocator = _searcher.Allocator;
            var group = new ContextBoundNativeList<int>(allocator);
            var tied = ArrayPool<bool>.Shared.Rent(JoinWalkChunk);
            var buffer = ArrayPool<long>.Shared.Rent(JoinPostingBufferSize);
            var drain = new PostingDrain(_searcher, allocator, buffer);
            var collected = new ContextBoundNativeList<long>(allocator);
            try
            {
                var counts = _counts.ToSpan();
                long groupEntries = 0;
                var total = _order.Count;
                for (var from = 0; from < total && _results.Count < _take; from += JoinWalkChunk)
                {
                    var length = Math.Min(JoinWalkChunk, total - from);
                    _order.FillTies(from, tied.AsSpan(0, length));
                    for (var i = 0; i < length; i++)
                    {
                        if (tied[i] == false && group.Count > 0)
                        {
                            Flush(ref group, groupEntries, ref drain, ref collected);
                            groupEntries = 0;
                            if (_results.Count >= _take)
                                break;
                        }

                        var key = _order.KeyAt(from + i);
                        if (counts[key] == 0)
                            continue;

                        JoinTermScores.Room(ref group, 1);
                        group.Add(key);
                        groupEntries += counts[key];
                    }
                }

                if (group.Count > 0 && _results.Count < _take)
                    Flush(ref group, groupEntries, ref drain, ref collected);
            }
            finally
            {
                drain.Dispose();
                ArrayPool<long>.Shared.Return(buffer);
                ArrayPool<bool>.Shared.Return(tied);
                group.Dispose();
                collected.Dispose();
            }
        }

        // Adds the entries of the keys of a group that come first in the order of their ids
        private void Flush(ref ContextBoundNativeList<int> group, long groupEntries, ref PostingDrain drain, ref ContextBoundNativeList<long> collected)
        {
            var limit = (int)Math.Min(groupEntries, _take - _results.Count);
            collected.Clear();
            var containers = _containers.ToSpan();
            var keys = group.ToSpan();
            var consumer = new CollectConsumer(ref collected, _hasWhere ? _where.ToSpan() : default, _hasWhere, limit, single: keys.Length == 1);
            foreach (var key in keys)
            {
                consumer.RestartWhere();
                drain.Drain(containers[key], ref consumer);
            }

            var entries = collected.ToSpan();
            entries.Sort();
            JoinTermScores.Room(ref _results, limit);
            _results.AddRange(entries.Slice(0, Math.Min(limit, entries.Length)));
            group.Clear();
        }

        public void Dispose()
        {
            if (_containers.HasContext)
                _containers.Dispose();
            if (_counts.HasContext)
                _counts.Dispose();
            if (_hasWhere)
                _where.Dispose();
            if (_results.HasContext)
                _results.Dispose();
            _containers = default;
            _counts = default;
            _hasWhere = false;
            _results = default;
        }

        // Moves the position past the entry (or to where it would be), and tells whether the entry is in the list. The entries come in ascending order, mostly close to each other.
        private static bool Seek(ReadOnlySpan<long> where, ref int position, long entry)
        {
            var low = position;
            var high = position;
            var step = 1;
            while (high < where.Length && where[high] < entry)
            {
                low = high + 1;
                high += step;
                step <<= 1;
            }

            var start = Math.Min(low, where.Length);
            var at = where.Slice(start, Math.Min(high + 1, where.Length) - start).BinarySearch(entry);
            position = at >= 0 ? start + at + 1 : start + ~at;
            return at >= 0;
        }

        // The entries are ascending: the where is walked forward
        private ref struct CountConsumer(ReadOnlySpan<long> where) : IPostingConsumer
        {
            private readonly ReadOnlySpan<long> _where = where;
            private int _position;
            public long Count;

            public bool Consume(ReadOnlySpan<long> entries)
            {
                foreach (var entry in entries)
                {
                    if (Seek(_where, ref _position, entry))
                        Count++;

                    if (_position >= _where.Length)
                        return false;
                }

                return true;
            }
        }

        // Keeps the `limit` smallest entries that pass the where
        internal ref struct CollectConsumer : IPostingConsumer
        {
            private readonly ref ContextBoundNativeList<long> _list;
            private readonly ReadOnlySpan<long> _where;
            private readonly bool _hasWhere;
            private readonly int _limit;
            private readonly bool _single;
            private int _position;
            private bool _hasBound;
            private long _bound;

            public CollectConsumer(ref ContextBoundNativeList<long> list, ReadOnlySpan<long> where, bool hasWhere, int limit, bool single)
            {
                _list = ref list;
                _where = where;
                _hasWhere = hasWhere;
                _limit = limit;
                _single = single;
            }

            public void RestartWhere() => _position = 0;

            public bool Consume(ReadOnlySpan<long> entries)
            {
                foreach (var entry in entries)
                {
                    // the entries of a key are ascending: nothing after this one is among the smallest
                    if (_hasBound && entry > _bound)
                        return false;

                    if (_hasWhere)
                    {
                        if (Seek(_where, ref _position, entry) == false)
                        {
                            if (_position >= _where.Length)
                                return false;
                            continue;
                        }
                    }

                    JoinTermScores.Room(ref _list, 1);
                    _list.Add(entry);
                    if (_single && _list.Count >= _limit)
                        return false;

                    if (_list.Count >= 2L * _limit)
                    {
                        var span = _list.ToSpan();
                        span.Sort();
                        _list.Shrink(_limit);
                        _bound = span[_limit - 1];
                        _hasBound = true;
                    }
                }

                return true;
            }
        }
    }
}

public struct JoinOrderedWalkMatch : IQueryMatch
{
    private readonly IndexSearcher.JoinWalkState _state;

    internal JoinOrderedWalkMatch(IndexSearcher.JoinWalkState state)
    {
        _state = state;
    }

    // Every entry the join holds that passes the where, not only the ones that Fill returns
    public long TotalResults => _state.Total;

    public long Count => _state.Total;

    public SkipSortingResult AttemptToSkipSorting() => SkipSortingResult.SortingIsRequired;

    public QueryCountConfidence Confidence => QueryCountConfidence.High;

    public bool IsBoosting => false;

    public DuplicatesOccurrence DuplicatesOccurrenceStatus => DuplicatesOccurrence.NotPossible;

    public int Fill(Span<long> matches) => _state.Fill(matches);

    public int AndWith(Span<long> buffer, int matches) => throw new NotSupportedException($"{nameof(JoinOrderedWalkMatch)} does not support the operation of {nameof(AndWith)}.");

    public void Score(Span<long> matches, Span<float> scores, float boostFactor)
    {
    }

    public QueryInspectionNode Inspect()
    {
        return new QueryInspectionNode(nameof(JoinOrderedWalkMatch),
            parameters: new Dictionary<string, string>
            {
                { Constants.QueryInspectionNode.IsBoosting, IsBoosting.ToString() },
                { Constants.QueryInspectionNode.Count, Count.ToString(CultureInfo.InvariantCulture) },
            });
    }
}
