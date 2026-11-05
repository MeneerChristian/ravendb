using Raven.Client.Documents.Indexes;
using System;
using System.Buffers.Text;
using Corax.Querying;
using Corax.Querying.Matches.Meta;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Server;
using Sparrow.Server.Collections;
using Voron.Data.CompactTrees;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>The union of the lists of the terms of a declared field that are in a range or start with a prefix, read in the order of the term dictionary of the join index.</summary>
internal static class JoinTermUnion
{
    public static IQueryMatch Create(IndexSearcher outer, JoinReader snapshot, JoinPredicate predicate)
    {
        var handles = new ContextBoundNativeList<long>(outer.Allocator);
        try
        {
            long ids = Walk(snapshot, predicate, ref handles);

            // a bitmap has one bit per entry id of the searcher: it is kept for the lists that fill it, within the budget of a memoization
            long bitmapBytes = (outer.LastEntryId + 1 + 63) / 64 * sizeof(ulong);
            if (ids * sizeof(long) < bitmapBytes || bitmapBytes > outer.MaxMemoizationSizeInBytes)
                return CreateSorted(outer, snapshot, handles.ToSpan());

            return CreateBitmap(outer, snapshot, handles.ToSpan());
        }
        finally
        {
            handles.Dispose();
        }
    }

    private static IQueryMatch CreateBitmap(IndexSearcher outer, JoinReader snapshot, ReadOnlySpan<long> handles)
    {
        var bitmap = new JoinBitmapSink(outer.Allocator, outer.LastEntryId);
        try
        {
            foreach (long handle in handles)
                bitmap.Add(snapshot, handle);

            return bitmap.ToMatch();
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static IQueryMatch CreateSorted(IndexSearcher outer, JoinReader snapshot, ReadOnlySpan<long> handles)
    {
        var sink = new JoinSortedSink(outer.Allocator);
        foreach (long handle in handles)
            sink.Add(snapshot, handle);

        return sink.ToMatch();
    }

    /// <summary>Collects the handles of the lists of the terms in the predicate and returns the total of their lengths.</summary>
    private static long Walk(JoinReader snapshot, JoinPredicate predicate, ref ContextBoundNativeList<long> handles)
    {
        long ids = 0;
        CompactTree terms = snapshot.Transaction.CompactTreeFor(JoinNames.Terms[predicate.Field]);
        using var scope = new CompactKeyCacheScope(snapshot.Transaction.LowLevelTransaction);
        var iterator = terms.Iterate();

        bool prefix = predicate.Kind == JoinPredicateKind.Prefix;
        if (prefix)
            iterator.Seek(predicate.Term);
        else if (predicate.IsLong == false && predicate.HasLower)
            iterator.Seek(predicate.LowerTerm);
        else
            iterator.Reset();

        Span<byte> copy = stackalloc byte[JoinLimits.MaxTermLength];
        while (iterator.MoveNext(scope.Key, out _, out _))
        {
            ReadOnlySpan<byte> term = scope.Key.Decoded();
            if (prefix)
            {
                if (term.StartsWith(predicate.Term) == false)
                    break;
            }
            else if (predicate.IsLong)
            {
                if (Utf8Parser.TryParse(term, out long number, out int consumed) == false || consumed != term.Length || InRange(predicate, number) == false)
                    continue;
            }
            else
            {
                if (predicate.HasUpper)
                {
                    int compared = term.SequenceCompareTo(predicate.UpperTerm);
                    if (compared > 0 || (compared == 0 && predicate.UpperInclusive == false))
                        break;
                }

                if (predicate.HasLower && predicate.LowerInclusive == false && term.SequenceEqual(predicate.LowerTerm))
                    continue;
            }

            // the term is looked up again: the iterator does not tell where its list is
            term.CopyTo(copy);
            if (snapshot.TryGetTermList(predicate.Field, copy.Slice(0, term.Length), out long handle) && handle != ListStore.EmptyHandle)
            {
                handles.Add(handle);
                ids += snapshot.CountOf(handle);
            }
        }

        return ids;
    }

    /// <summary>Sets the entries of every list in a bitmap: the lists need no copy, sort or dedupe, and the number of new bits is the exact count.</summary>
    private struct JoinBitmapSink : IDisposable
    {
        private GrowableBitArray _bitmap;
        private ContextBoundNativeList<long> _list;
        private long _count;

        public JoinBitmapSink(ByteStringContext allocator, long lastEntryId)
        {
            _bitmap = new GrowableBitArray(allocator, lastEntryId);
            _list = new ContextBoundNativeList<long>(allocator);
            _count = 0;
        }

        public void Add(JoinReader snapshot, long handle)
        {
            _list.Count = 0;
            snapshot.ReadList(handle, ref _list);
            foreach (long id in _list.ToSpan())
            {
                // an entry that the searcher does not have yet cannot match anything of it
                if (id < _bitmap.Capacity && _bitmap.Add(id))
                    _count++;
            }
        }

        public JoinBitmapMatch ToMatch()
        {
            _list.Dispose();
            return new JoinBitmapMatch(_bitmap, _count);
        }

        public void Dispose()
        {
            _list.Dispose();
            _bitmap.Dispose();
        }
    }

    /// <summary>Copies the lists into one array and sorts it: for lists that are small next to the bitmap of the ids space, or whose bitmap would not fit the budget of a memoization.</summary>
    private unsafe struct JoinSortedSink
    {
        private ContextBoundNativeList<long> _entries;

        public JoinSortedSink(ByteStringContext allocator)
        {
            _entries = new ContextBoundNativeList<long>(allocator);
        }

        public void Add(JoinReader snapshot, long handle) => snapshot.ReadList(handle, ref _entries);

        public JoinEntriesMatch ToMatch()
        {
            Span<long> all = _entries.ToSpan();
            all.Sort();
            int unique = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (unique == 0 || all[unique - 1] != all[i])
                    all[unique++] = all[i];
            }

            return new JoinEntriesMatch(_entries.RawItems, unique);
        }
    }

    private static bool InRange(JoinPredicate predicate, long number)
    {
        if (predicate.HasLower && (number < predicate.LowerNumber || (number == predicate.LowerNumber && predicate.LowerInclusive == false)))
            return false;

        return predicate.HasUpper == false || (number < predicate.UpperNumber || (number == predicate.UpperNumber && predicate.UpperInclusive));
    }
}
