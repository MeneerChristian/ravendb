using Corax.Indexing;
using Corax.Querying;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;
using Corax.Utils;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Voron.Data.Containers;
using Voron.Data.PostingLists;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>Turns a predicate into the match of the outer entries that satisfy it, read from the lists of one snapshot of a join index.</summary>
internal static class JoinMatchFactory
{
    public static IQueryMatch Create(IndexSearcher outer, JoinReader snapshot, JoinPredicate predicate)
    {
        switch (predicate.Kind)
        {
            case JoinPredicateKind.Any:
                return Any(outer, snapshot);
            case JoinPredicateKind.Term:
                return snapshot.TryGetTermList(predicate.Field, predicate.Term, out long handle) ? OfList(outer, snapshot, handle) : outer.EmptyMatch();
            case JoinPredicateKind.Grouping:
                return OfList(outer, snapshot, snapshot.SlotHandle(JoinReader.GroupingListSlot(predicate.Grouping)));
            case JoinPredicateKind.And:
                return outer.And(Create(outer, snapshot, predicate.Left), Create(outer, snapshot, predicate.Right));
            case JoinPredicateKind.Or:
                return outer.Or(Create(outer, snapshot, predicate.Left), Create(outer, snapshot, predicate.Right));
            case JoinPredicateKind.Range:
            case JoinPredicateKind.Prefix:
                return JoinTermUnion.Create(outer, snapshot, predicate);
            case JoinPredicateKind.Not:
                return outer.AndNot(Any(outer, snapshot), Create(outer, snapshot, predicate.Left));
            default:
                throw new System.ArgumentOutOfRangeException(nameof(predicate));
        }
    }

    /// <summary>
    /// The outer entries that may satisfy the predicate through some inner entry of their key, a superset of the answer that the lists alone can name: an AND is the AND of what its operands may have,
    /// an OR the OR, and nothing narrower than every entry is known for a NOT.
    /// </summary>
    public static IQueryMatch CreateCandidates(IndexSearcher outer, JoinReader snapshot, JoinPredicate predicate)
    {
        switch (predicate.Kind)
        {
            case JoinPredicateKind.And:
                return outer.And(CreateCandidates(outer, snapshot, predicate.Left), CreateCandidates(outer, snapshot, predicate.Right));
            case JoinPredicateKind.Or:
                return outer.Or(CreateCandidates(outer, snapshot, predicate.Left), CreateCandidates(outer, snapshot, predicate.Right));
            case JoinPredicateKind.Not:
                return Any(outer, snapshot);
            default:
                return Create(outer, snapshot, predicate);
        }
    }

    private static IQueryMatch Any(IndexSearcher outer, JoinReader snapshot) => OfList(outer, snapshot, snapshot.SlotHandle(JoinReader.AnySlot));

    /// <summary>The entries of a list. The count of a posting list is its header, so it is exact without reading the list.</summary>
    public static IQueryMatch OfList(IndexSearcher outer, JoinReader snapshot, long handle)
    {
        if (handle == ListStore.EmptyHandle)
            return outer.EmptyMatch();

        switch (ListStore.KindOf(handle))
        {
            case TermIdMask.Single:
                return TermMatch.YieldOnce(outer, outer.Allocator, handle, 1, false);
            case TermIdMask.SmallPostingList:
                Container.Get(snapshot.Transaction.LowLevelTransaction, EntryIdEncodings.GetContainerId(handle), out Container.Item item);
                return TermMatch.YieldSmall(outer, outer.Allocator, item, 1, false);
            default:
                return TermMatch.YieldSet(outer, outer.Allocator, snapshot.OpenPostingList(handle), 1, false, outer.IsAccelerated);
        }
    }
}
