using System;
using System.Buffers;
using Corax.Querying;
using Corax.Querying.Matches.Meta;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>
/// An AND or a NOT in the sub-query of a join on a field, exact for every group of inner entries. The lists of the atoms say which outer entries have an inner entry with the atom, which is the answer
/// for an entry whose key has one inner entry. For the outer entries whose key has several, an AND needs one inner entry that has every atom and a NOT one that lacks the atom, so those entries are
/// decided by the inner index: the sub-query is run restricted to their keys, the way the join of the server runs it, and an entry is in the answer when its key is found.
/// </summary>
internal static class JoinGroupVerify
{
    /// <summary>
    /// A bound, from the sizes of the lists, of the number of outer entries that <see cref="Create"/> has to check entry by entry: the entries that the lists may name and whose key has several inner entries.
    /// </summary>
    public static long ResidualBound(JoinReader snapshot, JoinPredicate predicate)
    {
        long several = snapshot.CountOf(snapshot.SlotHandle(snapshot.SeveralInnerSlot));
        return several == 0 ? 0 : Math.Min(several, CandidateBound(snapshot, predicate));
    }

    private static long CandidateBound(JoinReader snapshot, JoinPredicate predicate)
    {
        switch (predicate.Kind)
        {
            case JoinPredicateKind.Term:
                return snapshot.TryGetTermList(predicate.Field, predicate.Term, out long handle) ? snapshot.CountOf(handle) : 0;
            case JoinPredicateKind.Grouping:
                return snapshot.CountOf(snapshot.SlotHandle(JoinReader.GroupingListSlot(predicate.Grouping)));
            case JoinPredicateKind.And:
                return Math.Min(CandidateBound(snapshot, predicate.Left), CandidateBound(snapshot, predicate.Right));
            case JoinPredicateKind.Or:
                return CandidateBound(snapshot, predicate.Left) + CandidateBound(snapshot, predicate.Right);
            default:
                // every entry may be named by a NOT, and the terms of a range or a prefix are not counted here
                return snapshot.CountOf(snapshot.SlotHandle(JoinReader.AnySlot));
        }
    }

    /// <summary>The outer entries that have an inner entry of their key that satisfies the predicate: the entries whose key has one inner entry from the lists, the others from the inner index.</summary>
    public static IQueryMatch Create(IndexSearcher outer, JoinReader snapshot, JoinPredicate predicate, JoinServeRequest request)
    {
        long handle = snapshot.SlotHandle(snapshot.SeveralInnerSlot);
        if (handle == ListStore.EmptyHandle)
            return JoinMatchFactory.Create(outer, snapshot, predicate);

        IQueryMatch alone = outer.AndNot(JoinMatchFactory.Create(outer, snapshot, predicate), JoinMatchFactory.OfList(outer, snapshot, handle));
        IQueryMatch residual = outer.And(JoinMatchFactory.CreateCandidates(outer, snapshot, predicate), JoinMatchFactory.OfList(outer, snapshot, handle));
        IQueryMatch verified = Verify(outer, snapshot, residual, request);
        return verified == null ? alone : outer.Or(alone, verified);
    }

    /// <summary>The entries of the match whose key is a key of an inner entry that satisfies the sub-query; null when there are none.</summary>
    private static unsafe IQueryMatch Verify(IndexSearcher outer, JoinReader snapshot, IQueryMatch residual, JoinServeRequest request)
    {
        var rows = new ContextBoundNativeList<long>(outer.Allocator);
        var keys = new ContextBoundNativeList<long>(outer.Allocator);
        var good = new ContextBoundNativeList<long>(outer.Allocator);
        var bytes = new ContextBoundNativeList<byte>(outer.Allocator);
        var ends = new ContextBoundNativeList<int>(outer.Allocator);
        JoinTermScores found = null;
        bool keep = false;
        try
        {
            Drain(residual, ref rows);
            if (rows.Count == 0)
                return null;

            foreach (long row in rows.ToSpan())
                snapshot.TryGetOuterKeys(row, ref keys);

            Span<long> all = keys.ToSpan();
            all.Sort();
            int unique = 0;
            Span<byte> term = stackalloc byte[JoinLimits.MaxTermLength];
            foreach (long key in all)
            {
                if (unique > 0 && all[unique - 1] == key)
                    continue;

                all[unique++] = key;
                bytes.AddRange(term.Slice(0, snapshot.CopyKey(key, term)));
                ends.Add(bytes.Count);
            }

            request.Timings?.AddCounter("JoinVerifiedRows", rows.Count);
            request.Timings?.AddCounter("JoinVerifiedKeys", unique);

            found = request.ScanKeys(bytes.ToSpan(), ends.ToSpan());
            for (int i = 0; i < found.Count; i++)
            {
                if (snapshot.TryGetKey(found.Term(i), out long keyRef))
                    good.Add(keyRef);
            }

            Span<long> goodKeys = good.ToSpan();
            goodKeys.Sort();

            // the entries stay ascending: they are kept in place
            Span<long> rowSpan = rows.ToSpan();
            int kept = 0;
            keys.Count = 0;
            foreach (long row in rowSpan)
            {
                int before = keys.Count;
                snapshot.TryGetOuterKeys(row, ref keys);
                bool has = false;
                for (int k = before; k < keys.Count && has == false; k++)
                    has = goodKeys.BinarySearch(keys[k]) >= 0;

                keys.Count = before;
                if (has)
                    rowSpan[kept++] = row;
            }

            if (kept == 0)
                return null;

            keep = true;
            return new JoinEntriesMatch(rows.RawItems, kept);
        }
        finally
        {
            // the entries that are kept belong to the allocator of the query, like the entries of the other matches of the answer
            if (keep == false)
                rows.Dispose();

            keys.Dispose();
            good.Dispose();
            bytes.Dispose();
            ends.Dispose();
            found?.Dispose();
        }
    }

    /// <summary>Reads every entry of the match, ascending and without repeats.</summary>
    private static void Drain(IQueryMatch match, ref ContextBoundNativeList<long> entries)
    {
        const int bufferSize = 4096;
        long[] buffer = ArrayPool<long>.Shared.Rent(bufferSize);
        try
        {
            while (true)
            {
                int read = match.Fill(buffer.AsSpan(0, bufferSize));
                if (read == 0)
                    break;

                entries.AddRange(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(buffer);
        }

        Span<long> all = entries.ToSpan();
        all.Sort();
        int unique = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (unique == 0 || all[unique - 1] != all[i])
                all[unique++] = all[i];
        }

        entries.Count = unique;
    }
}
