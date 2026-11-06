using System;
using System.Collections.Generic;
using Corax.Mappings;
using Corax.Querying;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Server;
using Sparrow.Server.Collections;
using Voron;
using Voron.Data.CompactTrees;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>
/// The outer entries of a set of keys of an Indirect file: the entries of the posting list of every key term in the key field of the outer index of the query, as that snapshot has them. They are set in a bitmap over the
/// ids of the searcher (ascending, without repeats, an exact count), or sorted when the bitmap would not fit the budget of a memoization. It is never an InQuery of Corax, which sorts and merges the terms.
/// </summary>
internal static unsafe class JoinKeyUnion
{
    private const long SmallBitmapBytes = 64 * 1024;

    /// <summary>
    /// A match over the entries of <paramref name="match"/> that starts from the first one, sharing its memory, for the matches that this class makes and that only read it. False for any other match,
    /// which cannot be shared.
    /// </summary>
    public static bool TryRestart(IQueryMatch match, out IQueryMatch restarted)
    {
        switch (match)
        {
            case JoinBitmapMatch bitmap:
                restarted = bitmap.Restarted();
                return true;
            case JoinWordsMatch words:
                restarted = words.Restarted();
                return true;
            case JoinEntriesMatch entries:
                restarted = entries.Restarted();
                return true;
            case JoinLazyKeysMatch lazy:
                restarted = lazy.Restarted();
                return true;
            default:
                restarted = null;
                return false;
        }
    }

    public static IQueryMatch Create(IndexSearcher outer, JoinReader snapshot, ReadOnlySpan<long> keys, string outerField, JoinResidentRead resident = default, Action<ResidentUnion> onResident = null, bool disjoint = false)
    {
        if (keys.Length == 0)
            return outer.EmptyMatch();

        Slice.From(outer.Allocator, outerField, ByteStringType.Immutable, out Slice field);
        CompactTree tree = outer.GetTermsFor(field);
        if (tree == null)
            return outer.EmptyMatch();

        // the entries of one key are its posting list in the outer index: ascending, without repeats, counted by its header. It is streamed as it is, nothing is copied or set in a bitmap
        if (keys.Length == 1)
            return JoinOuterKeyLists.TryFindTerm(snapshot, tree, keys[0], out long termId) ? outer.TermQueryOfTermId(termId) : outer.EmptyMatch();

        if (disjoint && resident.IsUsable && keys.Length <= MaxLazyKeys)
        {
            IQueryMatch lazy = CreateLazy(outer, snapshot, keys, outerField, resident, onResident);
            if (lazy != null)
                return lazy;
        }

        FieldMetadata metadata = outer.FieldMetadataBuilder(outerField);
        long bitmapBytes = (outer.LastEntryId + 1 + 63) / 64 * sizeof(ulong);
        bool bitmap = bitmapBytes <= outer.MaxMemoizationSizeInBytes && (keys.Length > 16 || bitmapBytes <= SmallBitmapBytes);

        if (bitmap && resident.IsUsable)
            return CreateFromResident(outer, snapshot, keys, metadata, tree, resident, onResident);

        var scratch = new ContextBoundNativeList<long>(outer.Allocator);
        try
        {
            if (bitmap)
            {
                var bits = new GrowableBitArray(outer.Allocator, outer.LastEntryId);
                long count = 0;
                try
                {
                    foreach (long keyRef in keys)
                    {
                        scratch.Count = 0;
                        JoinOuterKeyLists.ReadRowsOfKey(outer, metadata, snapshot, tree, keyRef, ref scratch);
                        foreach (long id in scratch.ToSpan())
                        {
                            // an entry that the searcher does not have cannot be matched
                            if (id < bits.Capacity && bits.Add(id))
                                count++;
                        }
                    }

                    return new JoinBitmapMatch(bits, count);
                }
                catch
                {
                    bits.Dispose();
                    throw;
                }
            }

            var all = new ContextBoundNativeList<long>(outer.Allocator);
            foreach (long keyRef in keys)
                JoinOuterKeyLists.ReadRowsOfKey(outer, metadata, snapshot, tree, keyRef, ref all);

            Span<long> span = all.ToSpan();
            span.Sort();
            int unique = 0;
            for (int i = 0; i < span.Length; i++)
            {
                if (unique == 0 || span[unique - 1] != span[i])
                    span[unique++] = span[i];
            }

            // the entries belong to the allocator of the query, like the entries of the other matches of the answer
            return new JoinEntriesMatch(all.RawItems, unique);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    private const int MaxLazyKeys = 16 * 1024;

    /// <summary>The entries that a page reads are taken from the first entries of the lists, which are copied. Reading more is the union made the usual way.</summary>
    internal const int LazyEntriesPerList = 64;

    // Null when a list is not resident or the cache is not at the snapshot of the reader: the union is made the usual way
    private static IQueryMatch CreateLazy(IndexSearcher outer, JoinReader snapshot, ReadOnlySpan<long> keys, string outerField, JoinResidentRead resident, Action<ResidentUnion> onResident)
    {
        resident.Resident.Follow(resident, outer.LastEntryId);
        if (resident.Cache.TryReadHeads(resident.Scope, resident.OuterTransactionId, resident.AtomCode, keys, LazyEntriesPerList, outer.Allocator, out long* ids, out int* ends, out long total) == false)
            return null;

        onResident?.Invoke(new ResidentUnion { EpochHit = true, KeysFromCache = keys.Length });
        if (total == 0)
            return outer.EmptyMatch();

        // the keys outlive this call: the usual union is made from them when a reader takes more entries than the copies hold
        outer.Allocator.Allocate(keys.Length * sizeof(long), out ByteString keyMemory);
        keys.CopyTo(new Span<long>(keyMemory.Ptr, keys.Length));
        var keyCopy = (long*)keyMemory.Ptr;
        int keyCount = keys.Length;
        return new JoinLazyKeysMatch(outer.Allocator, ids, ends, keyCount, LazyEntriesPerList, total,
            () => Create(outer, snapshot, new ReadOnlySpan<long>(keyCopy, keyCount), outerField, resident, onResident, disjoint: false));
    }

    // The bitmap is the words that the cache ORs the lists into: the resident lists when the snapshot of the query is the epoch of the cache, the store (the outer index of this very snapshot) for the rest
    private static IQueryMatch CreateFromResident(IndexSearcher outer, JoinReader snapshot, ReadOnlySpan<long> keys, FieldMetadata metadata, CompactTree tree, JoinResidentRead resident,
        Action<ResidentUnion> onResident)
    {
        int wordCount = (int)((outer.LastEntryId + 1 + 63) / 64);
        outer.Allocator.Allocate(wordCount * sizeof(ulong), out ByteString memory);
        var words = (ulong*)memory.Ptr;
        new Span<ulong>(words, wordCount).Clear();

        // a reader of a newer transaction of the outer index with the same cursors as the epoch moves the epoch to its transaction
        resident.Resident.Follow(resident, outer.LastEntryId);

        var source = new JoinOuterKeyLists(outer, metadata, snapshot, tree);
        try
        {
            ResidentUnion union = resident.Cache.UnionKeys(resident.Scope, resident.OuterTransactionId, resident.AtomCode, keys, ref source, words, wordCount);
            onResident?.Invoke(union);
        }
        finally
        {
            source.Dispose();
        }

        return new JoinWordsMatch(words, wordCount);
    }
}
