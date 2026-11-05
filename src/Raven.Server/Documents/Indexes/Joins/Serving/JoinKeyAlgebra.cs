using System;
using Corax.Querying;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Server;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>
/// The predicates of the sub-query of a join in the key domain of an Indirect file: a key set is the sorted references of the keys whose group has an inner entry that satisfies the predicate. OR, a range, a prefix,
/// an IN are unions of key lists; AND and NOT are exact for a key whose group has one inner entry and are decided by the inner index for the keys of the groups of several (<see cref="GoodKeys"/>). No outer entry takes part.
/// </summary>
internal static class JoinKeyAlgebra
{
    /// <summary>The keys that the lists name for the predicate: exact for every key with one inner entry.</summary>
    public static ContextBoundNativeList<long> Keys(ByteStringContext allocator, JoinReader snapshot, JoinPredicate predicate)
    {
        var result = new ContextBoundNativeList<long>(allocator);
        try
        {
            switch (predicate.Kind)
            {
                case JoinPredicateKind.Any:
                    snapshot.ReadAtomKeys(JoinReader.AtomOfAny(), ref result);
                    break;
                case JoinPredicateKind.Term:
                    if (snapshot.TryGetTermList(predicate.Field, predicate.Term, out long handle))
                        snapshot.ReadList(handle, ref result);

                    break;
                case JoinPredicateKind.Grouping:
                    snapshot.ReadAtomKeys(JoinReader.AtomOfGrouping(predicate.Grouping), ref result);
                    break;
                case JoinPredicateKind.And:
                    Combine(allocator, snapshot, predicate, CombineKind.Intersect, ref result, candidates: false);
                    break;
                case JoinPredicateKind.Or:
                    Combine(allocator, snapshot, predicate, CombineKind.Union, ref result, candidates: false);
                    break;
                case JoinPredicateKind.Not:
                    using (ContextBoundNativeList<long> operand = Keys(allocator, snapshot, predicate.Left))
                    {
                        var any = new ContextBoundNativeList<long>(allocator);
                        try
                        {
                            snapshot.ReadAtomKeys(JoinReader.AtomOfAny(), ref any);
                            Except(any.ToSpan(), operand.ToSpan(), ref result);
                        }
                        finally
                        {
                            any.Dispose();
                        }
                    }

                    break;
                case JoinPredicateKind.Range:
                case JoinPredicateKind.Prefix:
                    JoinTermUnion.CollectKeys(snapshot, predicate, ref result);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(predicate));
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>The keys that may satisfy the predicate through some inner entry of their group, a superset of what the lists alone can name: an AND is the AND of what its operands may have, an OR the OR, a NOT every key.</summary>
    public static ContextBoundNativeList<long> CandidateKeys(ByteStringContext allocator, JoinReader snapshot, JoinPredicate predicate)
    {
        var result = new ContextBoundNativeList<long>(allocator);
        try
        {
            switch (predicate.Kind)
            {
                case JoinPredicateKind.And:
                    Combine(allocator, snapshot, predicate, CombineKind.Intersect, ref result, candidates: true);
                    return result;
                case JoinPredicateKind.Or:
                    Combine(allocator, snapshot, predicate, CombineKind.Union, ref result, candidates: true);
                    return result;
                case JoinPredicateKind.Not:
                    snapshot.ReadAtomKeys(JoinReader.AtomOfAny(), ref result);
                    return result;
                default:
                    result.Dispose();
                    return Keys(allocator, snapshot, predicate);
            }
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The keys that have an inner entry that satisfies the predicate: the keys with one inner entry from the lists, the keys of the groups of several (the list of the keys with several inner entries) from the inner index,
    /// by the keys that the sub-query finds among them. The keys to check are counted against <see cref="Refresh.JoinOptions.MaxResidualKeys"/> by the caller.
    /// </summary>
    public static ContextBoundNativeList<long> GoodKeys(ByteStringContext allocator, JoinReader snapshot, JoinPredicate predicate, JoinServeRequest request, bool verify)
    {
        ContextBoundNativeList<long> listed = Keys(allocator, snapshot, predicate);
        if (verify == false || snapshot.AtomHandle(snapshot.AtomOfSeveralInner) == Storage.ListStore.EmptyHandle)
            return listed;

        var several = new ContextBoundNativeList<long>(allocator);
        var alone = new ContextBoundNativeList<long>(allocator);
        var residual = new ContextBoundNativeList<long>(allocator);
        var bytes = new ContextBoundNativeList<byte>(allocator);
        var ends = new ContextBoundNativeList<int>(allocator);
        var good = new ContextBoundNativeList<long>(allocator);
        var result = new ContextBoundNativeList<long>(allocator);
        JoinTermScores found = null;
        bool keepAlone = false;
        bool keepResult = false;
        try
        {
            snapshot.ReadAtomKeys(snapshot.AtomOfSeveralInner, ref several);
            Except(listed.ToSpan(), several.ToSpan(), ref alone);

            using (ContextBoundNativeList<long> candidates = CandidateKeys(allocator, snapshot, predicate))
                Intersect(candidates.ToSpan(), several.ToSpan(), ref residual);

            if (residual.Count == 0)
            {
                keepAlone = true;
                return alone;
            }

            Span<byte> term = stackalloc byte[JoinLimits.MaxTermLength];
            foreach (long keyRef in residual.ToSpan())
            {
                bytes.AddRange(term.Slice(0, snapshot.CopyKey(keyRef, term)));
                ends.Add(bytes.Count);
            }

            request.Timings?.AddCounter("JoinVerifiedKeys", residual.Count);
            found = request.ScanKeys(bytes.ToSpan(), ends.ToSpan());
            for (int i = 0; i < found.Count; i++)
            {
                if (snapshot.TryGetKey(found.Term(i), out long keyRef))
                    good.Add(keyRef);
            }

            good.ToSpan().Sort();
            Union(alone.ToSpan(), good.ToSpan(), ref result);
            keepResult = true;
            return result;
        }
        finally
        {
            listed.Dispose();
            several.Dispose();
            if (keepAlone == false)
                alone.Dispose();

            residual.Dispose();
            bytes.Dispose();
            ends.Dispose();
            good.Dispose();
            if (keepResult == false)
                result.Dispose();

            found?.Dispose();
        }
    }

    private enum CombineKind
    {
        Intersect,
        Union
    }

    private static void Combine(ByteStringContext allocator, JoinReader snapshot, JoinPredicate predicate, CombineKind kind, ref ContextBoundNativeList<long> result, bool candidates)
    {
        using ContextBoundNativeList<long> left = candidates ? CandidateKeys(allocator, snapshot, predicate.Left) : Keys(allocator, snapshot, predicate.Left);
        using ContextBoundNativeList<long> right = candidates ? CandidateKeys(allocator, snapshot, predicate.Right) : Keys(allocator, snapshot, predicate.Right);
        if (kind == CombineKind.Intersect)
            Intersect(left.ToSpan(), right.ToSpan(), ref result);
        else
            Union(left.ToSpan(), right.ToSpan(), ref result);
    }

    /// <summary>Appends the keys of both sorted sets, sorted and without repeats.</summary>
    public static void Union(ReadOnlySpan<long> left, ReadOnlySpan<long> right, ref ContextBoundNativeList<long> result)
    {
        result.GrowFitting(result.Count + left.Length + right.Length);
        int l = 0, r = 0;
        while (l < left.Length || r < right.Length)
        {
            if (r == right.Length || (l < left.Length && left[l] < right[r]))
                result.AddUnsafe(left[l++]);
            else if (l == left.Length || right[r] < left[l])
                result.AddUnsafe(right[r++]);
            else
            {
                result.AddUnsafe(left[l]);
                l++;
                r++;
            }
        }
    }

    /// <summary>Appends the keys that are in both sorted sets.</summary>
    public static void Intersect(ReadOnlySpan<long> left, ReadOnlySpan<long> right, ref ContextBoundNativeList<long> result)
    {
        int l = 0, r = 0;
        while (l < left.Length && r < right.Length)
        {
            if (left[l] < right[r])
                l++;
            else if (right[r] < left[l])
                r++;
            else
            {
                result.Add(left[l]);
                l++;
                r++;
            }
        }
    }

    /// <summary>Appends the keys of the first sorted set that are not in the second.</summary>
    public static void Except(ReadOnlySpan<long> left, ReadOnlySpan<long> right, ref ContextBoundNativeList<long> result)
    {
        int r = 0;
        foreach (long key in left)
        {
            while (r < right.Length && right[r] < key)
                r++;

            if (r < right.Length && right[r] == key)
                continue;

            result.Add(key);
        }
    }
}
