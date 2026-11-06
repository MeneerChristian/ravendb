using System;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Corax.Mappings;
using Corax.Querying;
using Corax.Querying.Matches;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Voron.Data.CompactTrees;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>
/// The posting lists of the key terms in the key field of ONE snapshot of the outer index, addressed by the key references of an Indirect file: what a union reads when a list is not resident, and what the
/// cache is filled from. The searcher decides the snapshot, so the cache is given this source only together with the transaction id of that same snapshot.
/// </summary>
internal unsafe struct JoinOuterKeyLists : IResidentListSource, IDisposable
{
    private readonly IndexSearcher _outer;
    private readonly FieldMetadata _metadata;
    private readonly JoinReader _file;
    private readonly CompactTree _tree;
    private ContextBoundNativeList<long> _ids;

    // the warm-up and the admission ask for the count of a key and then read its list: the term is looked up once for the pair
    private long _lastKeyRef;
    private long _lastTermId;
    private bool _lastFound;

    public JoinOuterKeyLists(IndexSearcher outer, FieldMetadata metadata, JoinReader file, CompactTree tree)
    {
        _outer = outer;
        _metadata = metadata;
        _file = file;
        _tree = tree;
        _ids = new ContextBoundNativeList<long>(outer.Allocator);
        _lastKeyRef = -1;
        _lastTermId = 0;
        _lastFound = false;
    }

    // the term of a key reference is the key of the file: its entry in the tree of the key field holds the id of the term, which is what a posting list is read by
    internal static bool TryFindTerm(JoinReader file, CompactTree tree, long keyRef, out long termId)
    {
        Span<byte> term = stackalloc byte[Raven.Client.Documents.Indexes.JoinLimits.MaxTermLength];
        int length = file.CopyKey(keyRef, term);
        return tree.TryGetValue(term.Slice(0, length), out termId);
    }

    private bool TryFindTerm(long keyRef, out long termId)
    {
        if (_lastKeyRef != keyRef)
        {
            _lastFound = TryFindTerm(_file, _tree, keyRef, out _lastTermId);
            _lastKeyRef = keyRef;
        }

        termId = _lastTermId;
        return _lastFound;
    }

    private static void ReadRowsOfTerm(IndexSearcher outer, long termId, ref ContextBoundNativeList<long> rows)
    {
        TermMatch match = outer.TermQueryOfTermId(termId);
        Span<long> buffer = stackalloc long[1024];
        int read;
        while ((read = match.Fill(buffer)) > 0)
            rows.AddRange(buffer.Slice(0, read));
    }

    /// <summary>The entries of the list of the key, appended to <paramref name="rows"/> in ascending order.</summary>
    public static void ReadRowsOfKey(IndexSearcher outer, FieldMetadata metadata, JoinReader file, CompactTree tree, long keyRef, ref ContextBoundNativeList<long> rows)
    {
        if (TryFindTerm(file, tree, keyRef, out long termId))
            ReadRowsOfTerm(outer, termId, ref rows);
    }

    public long CountOf(long keyRef)
    {
        return TryFindTerm(keyRef, out long termId) ? _outer.NumberOfEntriesUnderTermId(termId) : 0;
    }

    public ReadOnlySpan<long> Read(long keyRef)
    {
        _ids.Count = 0;
        if (TryFindTerm(keyRef, out long termId))
            ReadRowsOfTerm(_outer, termId, ref _ids);
        return _ids.ToSpan();
    }

    public void Dispose() => _ids.Dispose();
}
