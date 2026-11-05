using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Corax.Querying.Matches.SortingMatches.Meta;
using Corax.Utils;
using Sparrow;

namespace Corax.Querying.Matches.SortingMatches;

public unsafe partial struct SortingMultiMatch<TInner>
{
    private static delegate*<ref SortingMultiMatch<TInner>, Span<long>, int> SortBy(OrderMetadata[] orderMetadata)
    {
        return (orderMetadata[0].FieldType, orderMetadata[0].Ascending) switch
        {
            (MatchCompareFieldType.Integer, true) => SortBy<EntryComparerByLong>(orderMetadata),
            (MatchCompareFieldType.Integer, false) => SortBy<Descending<EntryComparerByLong>>(orderMetadata),
            (MatchCompareFieldType.Floating, true) => SortBy<EntryComparerByDouble>(orderMetadata),
            (MatchCompareFieldType.Floating, false) => SortBy<Descending<EntryComparerByDouble>>(orderMetadata),
            (MatchCompareFieldType.Sequence, true) => SortBy<EntryComparerByTerm>(orderMetadata),
            (MatchCompareFieldType.Sequence, false) => SortBy<Descending<EntryComparerByTerm>>(orderMetadata),
            (MatchCompareFieldType.Score, true) => SortBy<EntryComparerByScore>(orderMetadata),
            (MatchCompareFieldType.Score, false) => SortBy<Descending<EntryComparerByScore>>(orderMetadata),
            (MatchCompareFieldType.Rank, true) => SortBy<EntryComparerByRank>(orderMetadata),
            (MatchCompareFieldType.Rank, false) => SortBy<Descending<EntryComparerByRank>>(orderMetadata),
            (MatchCompareFieldType.Spatial, true) => SortBy<EntryComparerBySpatial>(orderMetadata),
            (MatchCompareFieldType.Spatial, false) => SortBy<Descending<EntryComparerBySpatial>>(orderMetadata),
            (MatchCompareFieldType.Alphanumeric, true) => SortBy<EntryComparerByTermAlphaNumeric>(orderMetadata),
            (MatchCompareFieldType.Alphanumeric, false) => SortBy<Descending<EntryComparerByTermAlphaNumeric>>(orderMetadata),
            _ => &Fill<NullComparer, NullComparer, NullComparer>
        };
    }

    private static delegate*<ref SortingMultiMatch<TInner>, Span<long>, int> SortBy<TComparer1>(OrderMetadata[] orderMetadata)
        where TComparer1 : struct, IEntryComparer, IComparer<UnmanagedSpan>
    {
        if (orderMetadata.Length == 1)
            return &Fill<TComparer1, NullComparer, NullComparer>;

        return (orderMetadata[1].FieldType, orderMetadata[1].Ascending) switch
        {
            (MatchCompareFieldType.Integer, true) => SortBy<TComparer1, EntryComparerByLong>(orderMetadata),
            (MatchCompareFieldType.Integer, false) => SortBy<TComparer1, Descending<EntryComparerByLong>>(orderMetadata),
            (MatchCompareFieldType.Floating, true) => SortBy<TComparer1, EntryComparerByDouble>(orderMetadata),
            (MatchCompareFieldType.Floating, false) => SortBy<TComparer1, Descending<EntryComparerByDouble>>(orderMetadata),
            (MatchCompareFieldType.Sequence, true) => SortBy<TComparer1, EntryComparerByTerm>(orderMetadata),
            (MatchCompareFieldType.Sequence, false) => SortBy<TComparer1, Descending<EntryComparerByTerm>>(orderMetadata),
            (MatchCompareFieldType.Spatial, true) => SortBy<TComparer1,EntryComparerBySpatial>(orderMetadata),
            (MatchCompareFieldType.Spatial, false) => SortBy<TComparer1, Descending<EntryComparerBySpatial>>(orderMetadata),
            (MatchCompareFieldType.Alphanumeric, true) => SortBy<TComparer1, EntryComparerByTermAlphaNumeric>(orderMetadata),
            (MatchCompareFieldType.Alphanumeric, false) => SortBy<TComparer1, Descending<EntryComparerByTermAlphaNumeric>>(orderMetadata),
            
            (MatchCompareFieldType.Score, true) => SortBy<TComparer1, EntryComparerByScore>(orderMetadata),
            (MatchCompareFieldType.Score, false) => SortBy<TComparer1, Descending<EntryComparerByScore>>(orderMetadata),
            (MatchCompareFieldType.Rank, true) => SortBy<TComparer1, EntryComparerByRank>(orderMetadata),
            (MatchCompareFieldType.Rank, false) => SortBy<TComparer1, Descending<EntryComparerByRank>>(orderMetadata),
            
            _ => &Fill<TComparer1, NullComparer, NullComparer>
        };
    }

    private static delegate*<ref SortingMultiMatch<TInner>, Span<long>, int> SortBy<TComparer1, TComparer2>(OrderMetadata[] orderMetadata)
        where TComparer1 : struct, IEntryComparer, IComparer<UnmanagedSpan>
        where TComparer2 : struct, IEntryComparer, IComparer<int>, IComparer<UnmanagedSpan>
    {
        if (orderMetadata.Length == 2)
            return &Fill<TComparer1, TComparer2, NullComparer>;

        return (orderMetadata[2].FieldType, orderMetadata[2].Ascending) switch
        {
            (MatchCompareFieldType.Integer, true) => &Fill<TComparer1, TComparer2, EntryComparerByLong>,
            (MatchCompareFieldType.Integer, false) => &Fill<TComparer1, TComparer2, Descending<EntryComparerByLong>>,
            (MatchCompareFieldType.Floating, true) => &Fill<TComparer1, TComparer2, EntryComparerByDouble>,
            (MatchCompareFieldType.Floating, false) => &Fill<TComparer1, TComparer2, Descending<EntryComparerByDouble>>,
            (MatchCompareFieldType.Sequence, true) => &Fill<TComparer1, TComparer2, EntryComparerByTerm>,
            (MatchCompareFieldType.Sequence, false) => &Fill<TComparer1, TComparer2, Descending<EntryComparerByTerm>>,
            (MatchCompareFieldType.Spatial, true) => &Fill<TComparer1, TComparer2, EntryComparerBySpatial>,
            (MatchCompareFieldType.Spatial, false) => &Fill<TComparer1, TComparer2, Descending<EntryComparerBySpatial>>,
            (MatchCompareFieldType.Alphanumeric, true) => &Fill<TComparer1, TComparer2, EntryComparerByTermAlphaNumeric>,
            (MatchCompareFieldType.Alphanumeric, false) => &Fill<TComparer1, TComparer2, Descending<EntryComparerByTermAlphaNumeric>>,
            (MatchCompareFieldType.Score, true) => &Fill<TComparer1, TComparer2, EntryComparerByScore>,
            (MatchCompareFieldType.Score, false) => &Fill<TComparer1, TComparer2, Descending<EntryComparerByScore>>,
            (MatchCompareFieldType.Rank, true) => &Fill<TComparer1, TComparer2, EntryComparerByRank>,
            (MatchCompareFieldType.Rank, false) => &Fill<TComparer1, TComparer2, Descending<EntryComparerByRank>>,
            
            _ => &Fill<TComparer1, TComparer2, NullComparer>
        };
    }
    
    private struct EntryComparerHelper
    {
        public static Span<int> NumericSortBatch<TComparer1, TComparer2, TComparer3>(ref SortingMultiMatch<TInner> match, Span<long> batchTermIds, UnmanagedSpan* batchTerms, TComparer1 comparer1, TComparer2 comparer2, TComparer3 comparer3)
            where TComparer1 : struct, IComparer<UnmanagedSpan>, IComparer<int>, IEntryComparer
            where TComparer2 : struct, IComparer<UnmanagedSpan>, IComparer<int>, IEntryComparer
            where TComparer3 : struct, IComparer<UnmanagedSpan>, IComparer<int>, IEntryComparer
        {
            var indexes = MemoryMarshal.Cast<long, int>(batchTermIds)[..(batchTermIds.Length)];
            for (int i = 0; i < batchTermIds.Length; i++)
            {
                batchTerms[i] = new UnmanagedSpan(batchTermIds[i]);
                indexes[i] = i;
            }

            IndirectSort(ref match, indexes, batchTerms, comparer1, comparer2, comparer3);
            
            return indexes;
        }

        // The top-N of a sort that starts with a key that ties heavily
        internal const int TopSortMinimumRatio = 4;

        public static void IndirectTopSort<TComparer1, TComparer2, TComparer3>(ref SortingMultiMatch<TInner> match, Span<int> indexes,
            UnmanagedSpan* batchTerms, int take, TComparer1 comparer1, TComparer2 comparer2, TComparer3 comparer3)
            where TComparer1 : struct, IComparer<UnmanagedSpan>, IEntryComparer
            where TComparer2 : struct, IComparer<UnmanagedSpan>, IComparer<int>, IEntryComparer
            where TComparer3 : struct, IComparer<UnmanagedSpan>, IComparer<int>, IEntryComparer
        {
            if (match._orderMetadata[0].Ascending)
                TopSort(indexes, take, new IndirectComparer<TComparer1, TComparer2, TComparer3>(ref match, batchTerms, comparer1, comparer2, comparer3));
            else
                TopSort(indexes, take, new IndirectComparer<Descending<TComparer1>, TComparer2, TComparer3>(ref match, batchTerms, new(comparer1), comparer2, comparer3));
        }

        // Leaves the best 'take' entries, best first, in indexes[..take]. The heap keeps the worst of them at its root.
        private static void TopSort<TComparer>(Span<int> indexes, int take, TComparer comparer)
            where TComparer : struct, IComparer<int>
        {
            if (take == 0)
                return;

            var n = indexes.Length;
            var heap = indexes[..take];
            var size = 0;
            for (var i = 0; i < n; i++)
            {
                if (size < take)
                {
                    heap[size] = i;
                    SiftUp(heap, size, comparer);
                    size++;
                }
                else if (comparer.Compare(i, heap[0]) < 0) // i sorts before the worst kept entry: a tie is not better
                {
                    heap[0] = i;
                    SiftDown(heap, 0, size, comparer);
                }
            }

            for (var end = size - 1; end > 0; end--)
            {
                (heap[0], heap[end]) = (heap[end], heap[0]);
                SiftDown(heap, 0, end, comparer);
            }
        }

        // true when x is worse than y, equal entries by their position
        private static bool Worse<TComparer>(int x, int y, TComparer comparer) where TComparer : struct, IComparer<int>
        {
            var cmp = comparer.Compare(x, y);
            return cmp > 0 || (cmp == 0 && x > y);
        }

        private static void SiftUp<TComparer>(Span<int> heap, int position, TComparer comparer) where TComparer : struct, IComparer<int>
        {
            var item = heap[position];
            while (position > 0)
            {
                var parent = (position - 1) / 2;
                if (Worse(item, heap[parent], comparer) == false)
                    break;
                heap[position] = heap[parent];
                position = parent;
            }

            heap[position] = item;
        }

        private static void SiftDown<TComparer>(Span<int> heap, int position, int size, TComparer comparer) where TComparer : struct, IComparer<int>
        {
            var item = heap[position];
            while (true)
            {
                var child = 2 * position + 1;
                if (child >= size)
                    break;
                if (child + 1 < size && Worse(heap[child + 1], heap[child], comparer))
                    child++;
                if (Worse(heap[child], item, comparer) == false)
                    break;
                heap[position] = heap[child];
                position = child;
            }

            heap[position] = item;
        }

        public static void IndirectSort<TComparer1, TComparer2, TComparer3>(ref SortingMultiMatch<TInner> match, Span<int> indexes,
            UnmanagedSpan* batchTerms, TComparer1 comparer1, TComparer2 comparer2, TComparer3 comparer3)
            where TComparer1 : struct, IComparer<UnmanagedSpan>, IEntryComparer
            where TComparer2 : struct, IComparer<UnmanagedSpan>, IComparer<int>, IEntryComparer
            where TComparer3 : struct, IComparer<UnmanagedSpan>, IComparer<int>, IEntryComparer
        {
            if (match._orderMetadata[0].Ascending)
                indexes.Sort(new IndirectComparer<TComparer1, TComparer2, TComparer3>(ref match, batchTerms, comparer1,
                comparer2, comparer3));
            else
                indexes.Sort(new IndirectComparer<Descending<TComparer1>, TComparer2, TComparer3>(ref match, batchTerms, new(comparer1), comparer2, comparer3));
            
            // Support for including scores in the projection in case the score comparer is not first. 
            // We only have one chance to copy in the right order before pagination happens
            // (because we'll lose reference to the buffer holder (the comparer)). 
            if (typeof(TComparer1) != typeof(EntryComparerByScore) || typeof(TComparer1) != typeof(Descending<EntryComparerByScore>))
            {
                if (match._sortingDataTransfer.IncludeScores && match._scoreBufferHandler != null)
                {
                    match._scoresResults.EnsureCapacityFor(indexes.Length);
                    foreach (var t in indexes)
                        match._scoresResults.AddByRefUnsafe() = match._secondaryScoreBuffer[t];

                    match._secondaryScoreBuffer = default;
                    match._scoreBufferHandler.Dispose();
                    match._scoreBufferHandler = null;
                }
                
            }
        }
    }
}
