using System;
using System.Buffers;
using Corax.Querying;
using Corax.Utils;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Persistence.Corax;

// The keys of a JOIN on the id in the order of the joined index. The sort values are read for the part of the order that is walked
internal sealed class JoinOrderedKeys : IJoinWalkOrder, IDisposable
{
    private readonly IndexSearcher _searcher;
    private readonly OrderMetadata[] _sortMetadata;
    private ContextBoundNativeList<long> _entries; // in the order of the joined index
    private ContextBoundNativeList<int> _keyAt; // per position: the index of the key

    public JoinOrderedKeys(IndexSearcher searcher, OrderMetadata[] sortMetadata, ContextBoundNativeList<long> entries, ContextBoundNativeList<int> keyAt)
    {
        _searcher = searcher;
        _sortMetadata = sortMetadata ?? [];
        _entries = entries;
        _keyAt = keyAt;
    }

    public int Count => _entries.Count;

    public int KeyAt(int position) => _keyAt[position];

    public void FillTies(int from, Span<bool> tied)
    {
        // the sort values of the entry before the first one are needed to compare it
        var start = Math.Max(from - 1, 0);
        var length = from + tied.Length - start;
        var keys = ArrayPool<long>.Shared.Rent(length);
        try
        {
            var entries = _entries.ToSpan().Slice(start, length);
            var keySpan = keys.AsSpan(0, length);
            var offset = from - start;
            tied.Fill(true);
            foreach (var order in _sortMetadata)
            {
                _searcher.ReadSortKeys(order, entries, keySpan);
                for (var i = 0; i < tied.Length; i++)
                {
                    var at = offset + i;
                    if (at > 0)
                        tied[i] &= keySpan[at] == keySpan[at - 1];
                }
            }

            if (from == 0)
                tied[0] = false;
        }
        finally
        {
            ArrayPool<long>.Shared.Return(keys);
        }
    }

    // The dense rank of every key, in step with the keys
    public void RankKeys(JoinTermScores keys)
    {
        var ranks = ArrayPool<float>.Shared.Rent(Count);
        var tied = ArrayPool<bool>.Shared.Rent(1024);
        try
        {
            var rank = 0;
            for (var from = 0; from < Count; from += 1024)
            {
                var length = Math.Min(1024, Count - from);
                FillTies(from, tied.AsSpan(0, length));
                for (var i = 0; i < length; i++)
                {
                    if (tied[i] == false)
                        rank++;

                    ranks[KeyAt(from + i)] = rank;
                }
            }

            keys.SetScoresAsRanks(ranks.AsSpan(0, Count));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(ranks);
            ArrayPool<bool>.Shared.Return(tied);
        }
    }

    public void Dispose()
    {
        _entries.Dispose();
        _keyAt.Dispose();
    }
}
