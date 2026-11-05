using System;
using System.Buffers;
using System.Numerics;
using Sparrow.Server;
using Voron;
using Voron.Data.CompactTrees;
using Voron.Data.Containers;
using Voron.Data.Lookups;

namespace Corax.Querying;

public interface IFieldTermScoreSink
{
    void EnsureCapacity(int keys);

    // One term of the field with the best score of the matched entries that have it
    void Add(ReadOnlySpan<byte> decodedTerm, float score, long winnerEntryId);
}

public partial class IndexSearcher
{
    public void ScoresByEntryTermMap(string field, ReadOnlySpan<long> sortedIds, ReadOnlySpan<float> scores, JoinTermScores output)
    {
        if (sortedIds.IsEmpty)
            return;

        var reader = TermsReaderFor(field);
        if (reader.HasMap == false)
            return;

        var table = new TermIdBestTable(JoinTermTableCapacity);
        try
        {
            for (var start = 0; start < sortedIds.Length;)
            {
                var read = reader.GetAllTermIdsFromSet(sortedIds.Slice(start), out var termIds);
                if (read == 0)
                    break;

                for (var i = 0; i < read; i++)
                {
                    if (termIds[i] != -1)
                        table.Update(termIds[i], scores[start + i], sortedIds[start + i]);
                }

                start += read;
            }

            Drain(ref table, output);
        }
        finally
        {
            table.Dispose();
            reader.Dispose();
        }
    }

    public void ScoresByEntryTerms(string field, ReadOnlySpan<long> sortedIds, ReadOnlySpan<float> scores, JoinTermScores output)
    {
        var rootPage = FieldCache.GetLookupRootPage(field);
        var table = new TermIdBestTable(JoinTermTableCapacity);
        var key = new CompactKey();
        Page page = default;
        try
        {
            for (var i = 0; i < sortedIds.Length; i++)
            {
                var reader = GetEntryTermsReader(sortedIds[i], ref page, key);
                while (reader.FindNext(rootPage))
                {
                    if (reader.IsNull || reader.IsNonExisting)
                        continue;

                    table.Update(reader.TermId, scores[i], sortedIds[i]);
                }

                key.Reset();
            }

            Drain(ref table, output);
        }
        finally
        {
            table.Dispose();
        }
    }

    private void Drain(ref TermIdBestTable table, JoinTermScores output)
    {
        var llt = _transaction.LowLevelTransaction;
        var key = llt.AcquireCompactKey();
        try
        {
            table.ForEach(this, key, output);
        }
        finally
        {
            llt.ReleaseCompactKey(ref key);
        }
    }

    internal struct TermIdBestTable : IDisposable
    {
        private long[] _keys;
        private float[] _scores;
        private long[] _winners;
        private int _capacity, _used;

        public TermIdBestTable(int capacity)
        {
            _capacity = capacity;
            _keys = ArrayPool<long>.Shared.Rent(capacity);
            _scores = ArrayPool<float>.Shared.Rent(capacity);
            _winners = ArrayPool<long>.Shared.Rent(capacity);
            _keys.AsSpan(0, capacity).Fill(-1);
            _used = 0;
        }

        // the top bits of the product, as many as the table has slots
        internal static int Slot(long termId, int mask) => (int)(((ulong)termId * 0x9E3779B97F4A7C15UL) >> (64 - BitOperations.Log2((uint)mask + 1)));

        public void Update(long termId, float score, long entryId)
        {
            var mask = _capacity - 1;
            var slot = Slot(termId, mask);
            while (true)
            {
                var current = _keys[slot];
                if (current == termId)
                {
                    if (score > _scores[slot]) // ids ascend: the first entry with the best score keeps the win
                    {
                        _scores[slot] = score;
                        _winners[slot] = entryId;
                    }

                    return;
                }

                if (current == -1)
                {
                    _keys[slot] = termId;
                    _scores[slot] = score;
                    _winners[slot] = entryId;
                    if (++_used * 2 > _capacity)
                        Grow();
                    return;
                }

                slot = (slot + 1) & mask;
            }
        }

        private void Grow()
        {
            var capacity = _capacity * 2;
            var keys = ArrayPool<long>.Shared.Rent(capacity);
            var scores = ArrayPool<float>.Shared.Rent(capacity);
            var winners = ArrayPool<long>.Shared.Rent(capacity);
            keys.AsSpan(0, capacity).Fill(-1);
            var mask = capacity - 1;
            for (var i = 0; i < _capacity; i++)
            {
                if (_keys[i] == -1)
                    continue;
                var slot = Slot(_keys[i], mask);
                while (keys[slot] != -1)
                    slot = (slot + 1) & mask;
                keys[slot] = _keys[i];
                scores[slot] = _scores[i];
                winners[slot] = _winners[i];
            }

            Dispose();
            _keys = keys;
            _scores = scores;
            _winners = winners;
            _capacity = capacity;
        }

        public void ForEach(IndexSearcher searcher, CompactKey key, JoinTermScores output)
        {
            output.EnsureCapacity(_used);
            for (var slot = 0; slot < _capacity; slot++)
            {
                var termId = _keys[slot];
                if (termId == -1 || searcher.IsNullOrNonExistingTerm(termId))
                    continue;

                Container.Get(searcher._transaction.LowLevelTransaction, new ContainerEntryId(termId), out var item);
                TermsReader.Set(key, item, searcher._dictionaryId);
                output.Add(key.Decoded(), _scores[slot], _winners[slot]);
            }
        }

        public void Dispose()
        {
            if (_keys == null)
                return;
            ArrayPool<long>.Shared.Return(_keys);
            ArrayPool<float>.Shared.Return(_scores);
            ArrayPool<long>.Shared.Return(_winners);
            _keys = null;
        }
    }

    public void ScoresByWalkingTermsOfField<TSink>(string field, ReadOnlySpan<long> sortedIds, ReadOnlySpan<float> scores, ref TSink sink)
        where TSink : IFieldTermScoreSink
    {
        if (sortedIds.IsEmpty)
            return;

        using var nameScope = Slice.From(Allocator, field, ByteStringType.Immutable, out var fieldName);
        var tree = _fieldsTree?.CompactTreeFor(fieldName);
        if (tree == null || tree.NumberOfEntries == 0)
            return;

        // every key is the winner of at least one of the sorted ids, and the tree holds each term once
        sink.EnsureCapacity((int)Math.Min(tree.NumberOfEntries, sortedIds.Length));
        var buffer = ArrayPool<long>.Shared.Rent(JoinPostingBufferSize);
        var drain = new PostingDrain(this, Allocator, buffer);
        var llt = _transaction.LowLevelTransaction;
        var key = llt.AcquireCompactKey();
        try
        {
            var iterator = tree.Iterate<Lookup<CompactTree.CompactKeyLookup>.ForwardIterator>();
            iterator.Reset();
            while (iterator.MoveNext(key, out var containerId, out _))
            {
                var intersection = new IntersectWithMatched { SortedIds = sortedIds, Scores = scores, Best = float.NegativeInfinity, Winner = -1L };
                drain.Drain(containerId, ref intersection);
                var best = intersection.Best;
                var winner = intersection.Winner;

                if (winner >= 0)
                    sink.Add(key.Decoded(), best, winner);
            }
        }
        finally
        {
            drain.Dispose();
            ArrayPool<long>.Shared.Return(buffer);
            llt.ReleaseCompactKey(ref key);
        }
    }

    private ref struct IntersectWithMatched : IPostingConsumer
    {
        public ReadOnlySpan<long> SortedIds;
        public ReadOnlySpan<float> Scores;
        public float Best;
        public long Winner;
        private int _from;

        public bool Consume(ReadOnlySpan<long> entries)
        {
            for (var i = 0; i < entries.Length; i++)
            {
                _from = LowerBound(SortedIds, _from, entries[i]);
                if (_from == SortedIds.Length)
                    return false;
                if (SortedIds[_from] != entries[i])
                    continue;

                // ids ascend: the first entry with the best score wins a tie
                if (Scores[_from] > Best)
                {
                    Best = Scores[_from];
                    Winner = entries[i];
                }
            }

            return true;
        }
    }

    private static int LowerBound(ReadOnlySpan<long> sorted, int from, long value)
    {
        if (from >= sorted.Length || sorted[from] >= value)
            return from;

        var low = from;
        var step = 1;
        var high = from + 1;
        while (high < sorted.Length && sorted[high] < value)
        {
            low = high;
            step <<= 1;
            high = low + step;
        }

        if (high > sorted.Length)
            high = sorted.Length;

        // sorted[low] < value, and either high is the end or sorted[high] >= value
        low++;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (sorted[middle] < value)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    public bool FieldHoldsOneTermPerEntry(string field) => HasMultipleTermsInField(field) == false;

    public bool EntryTermMapCoversPostings(string field)
    {
        using var nameScope = Slice.From(Allocator, field, ByteStringType.Immutable, out var name);
        var rows = EntriesToTermsReader(name)?.NumberOfEntries ?? 0;

        long postings = 0;
        var tree = _fieldsTree?.CompactTreeFor(name);
        if (tree != null)
        {
            var iterator = tree.Iterate<Lookup<CompactTree.CompactKeyLookup>.ForwardIterator>();
            iterator.Reset();
            while (iterator.MoveNext(out var containerId))
            {
                postings += NumberOfDocumentsUnderSpecificTerm(containerId);
                if (postings > rows)
                    return false; // more postings than rows is already the answer
            }
        }

        if (TryGetPostingListForNull(name, out var nullPostingListId, out _))
            postings += GetPostingList(nullPostingListId)?.State.NumberOfEntries ?? 0;

        if (TryGetPostingListForNonExisting(name, out var nonExistingPostingListId, out _))
            postings += GetPostingList(nonExistingPostingListId)?.State.NumberOfEntries ?? 0;

        return postings == rows;
    }

    internal bool IsNullOrNonExistingTerm(long termId)
    {
        InitializeSpecialTermsMarkers();
        return _nullTermsMarkers.Contains(termId) || _nonExistingTermsMarkers.Contains(termId);
    }
}
