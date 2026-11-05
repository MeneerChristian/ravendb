using Raven.Client.Documents.Indexes;
using System;
using Corax.Querying;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;
using Corax.Utils;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Voron.Data.CompactTrees;
using Voron.Data.Lookups;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>
/// The first entries of a join that orders by a declared Exact field of the joined index, read from the lists of the join index: the terms of the field are walked in the order of the term dictionary, the list of
/// a term holds the outer entries whose joined document has the term, ascending. Entries with the same term come in the order of their entry ids, which is what ranking the joined documents does.
/// </summary>
internal static class JoinOrderedPage
{
    private const int ReadBuffer = 1 << 16;

    /// <param name="joinScore">What the join adds to the score of an entry (the score of the sub-query times the boost of the join).</param>
    /// <param name="whereHoldsEveryEntry">The where clause matches every entry of the outer index: it is not read, the entries of the join are the entries that have a joined document.</param>
    /// <param name="whereScore">What the where clause adds to the score of every entry when it holds every entry.</param>
    public static unsafe IPresortedMatch Create(IndexSearcher outer, JoinReader snapshot, int field, bool ascending, IQueryMatch where, bool whereHoldsEveryEntry, float whereScore, int take,
        bool scoresNeeded, float joinScore)
    {
        // the entries of the where clause and their scores, ascending
        Span<long> allowed = default;
        long total;
        MemoizationMatch memoized = default;
        long joined = snapshot.CountOf(snapshot.SlotHandle(JoinReader.AnySlot));
        bool filters = where != null && whereHoldsEveryEntry == false;
        if (filters == false)
        {
            total = joined;
        }
        else
        {
            memoized = (MemoizationMatch)where;
            allowed = memoized.FillAndRetrieve();
            allowed = Ascending(allowed, outer);

            // when every entry of the outer index has a joined document, the entries of the where clause are the entries of the join
            total = joined == outer.NumberOfEntries ? allowed.Length : CountJoined(outer, snapshot, allowed);
        }

        var entries = new ContextBoundNativeList<long>(outer.Allocator);
        long wanted = take < 0 ? long.MaxValue : take;
        if (total > 0 && wanted > 0)
        {
            CompactTree terms = snapshot.Transaction.CompactTreeFor(JoinNames.Terms[field]);
            if (ascending)
                Walk(terms.Iterate(), snapshot, field, filters, allowed, wanted, ref entries);
            else
                Walk(terms.Iterate<Lookup<CompactTree.CompactKeyLookup>.BackwardIterator>(), snapshot, field, filters, allowed, wanted, ref entries);
        }

        if (scoresNeeded == false)
            return new PresortedPageMatch(entries.ToSpan(), default, total);

        if (filters == false)
        {
            // every entry has the score of the where clause, which holds every entry, and the one of the join
            float[] scores = new float[entries.Count];
            Array.Fill(scores, Bm25Relevance.InitialScoreValue + (where != null ? whereScore : 0f) + joinScore);
            return new PresortedPageMatch(entries.ToSpan(), scores, total);
        }

        return new PresortedPageMatch(entries.ToSpan(), ScoresOf(outer, memoized, entries.ToSpan(), joinScore), total);
    }

    private static void Walk<TDirection>(CompactTree.Iterator<TDirection> iterator, JoinReader snapshot, int field, bool hasWhere, ReadOnlySpan<long> allowed, long wanted,
        ref ContextBoundNativeList<long> entries)
        where TDirection : struct, ILookupIterator
    {
        using var scope = new CompactKeyCacheScope(snapshot.Transaction.LowLevelTransaction);
        Span<byte> copy = stackalloc byte[JoinLimits.MaxTermLength];
        var list = new ContextBoundNativeList<long>(snapshot.Transaction.Allocator);
        iterator.Reset();
        while (entries.Count < wanted && iterator.MoveNext(scope.Key, out _, out _))
        {
            ReadOnlySpan<byte> term = scope.Key.Decoded();
            term.CopyTo(copy);
            if (snapshot.TryGetTermList(field, copy.Slice(0, term.Length), out long handle) == false || handle == ListStore.EmptyHandle)
                continue;

            list.Count = 0;
            snapshot.ReadList(handle, ref list);
            foreach (long entry in list.ToSpan())
            {
                if (hasWhere && allowed.BinarySearch(entry) < 0)
                    continue;

                entries.Add(entry);
                if (entries.Count >= wanted)
                    break;
            }
        }

        list.Dispose();
    }

    // The matches of a memoized match are ascending as a rule; when they are not they are sorted in a copy
    private static Span<long> Ascending(Span<long> ids, IndexSearcher outer)
    {
        bool ascending = true;
        for (int i = 1; i < ids.Length && ascending; i++)
            ascending = ids[i - 1] < ids[i];

        if (ascending)
            return ids;

        var copy = new ContextBoundNativeList<long>(outer.Allocator);
        copy.AddRange(ids);
        Span<long> sorted = copy.ToSpan();
        sorted.Sort();
        return sorted;
    }

    // The score of the entries of the page: what the where clause gives them (scored for the entries of the page only, they are ascending for it), and what the join adds
    private static float[] ScoresOf(IndexSearcher outer, MemoizationMatch where, ReadOnlySpan<long> page, float joinScore)
    {
        var scores = new float[page.Length];
        Array.Fill(scores, Bm25Relevance.InitialScoreValue + joinScore);
        if (page.Length == 0)
            return scores;

        var ascending = new long[page.Length];
        page.CopyTo(ascending);
        var whereScores = new float[page.Length];
        Array.Fill(whereScores, Bm25Relevance.InitialScoreValue);
        Array.Sort(ascending);
        where.Score(ascending, whereScores, 1f);
        for (int i = 0; i < page.Length; i++)
            scores[i] = whereScores[Array.BinarySearch(ascending, page[i])] + joinScore;

        return scores;
    }

    /// <summary>How many of the entries have a joined document: the entries of the any list that are among them.</summary>
    private static long CountJoined(IndexSearcher outer, JoinReader snapshot, ReadOnlySpan<long> allowed)
    {
        IQueryMatch any = JoinMatchFactory.Create(outer, snapshot, JoinPredicate.Any());
        long count = 0;
        var buffer = new long[ReadBuffer];
        for (int from = 0; from < allowed.Length; from += buffer.Length)
        {
            int length = Math.Min(buffer.Length, allowed.Length - from);
            allowed.Slice(from, length).CopyTo(buffer);
            count += any.AndWith(buffer.AsSpan(0, length), length);
        }

        return count;
    }
}
