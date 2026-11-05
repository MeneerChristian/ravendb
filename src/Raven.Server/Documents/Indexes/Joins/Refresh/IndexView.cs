using System;
using System.Text;
using Corax.Indexing;
using Corax.Querying;
using Corax.Utils;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow;
using Sparrow.Server;
using Voron;
using Voron.Data.CompactTrees;
using Voron.Data.Containers;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>What the refresher reads from one index snapshot: the entry of a document, the terms of an entry and the terms of a field. One snapshot, one thread.</summary>
internal sealed class IndexView : IDisposable
{
    private readonly CompactTree _idTree;
    private readonly CompactKey _key = new();
    private TermsReader _idReader;
    private readonly bool _hasIdReader;
    private Page _page;

    public IndexSnapshot Snapshot { get; }

    public IndexSearcher Searcher => Snapshot.Searcher;

    public IndexView(IndexSnapshot snapshot, Slice idField)
    {
        Snapshot = snapshot;
        _idTree = snapshot.Searcher.GetTermsFor(idField);
        _hasIdReader = _idTree != null && snapshot.Searcher.EntriesToTermsReader(idField) != null;
        if (_hasIdReader)
            _idReader = snapshot.Searcher.TermsReaderFor(idField);
    }

    /// <summary>The entry of the document with this (lowercased) id. <paramref name="multiple"/> is set when the document has several entries.</summary>
    public bool TryFindEntry(ReadOnlySpan<byte> lowerId, out long entryId, out bool multiple)
    {
        entryId = 0;
        multiple = false;

        if (_idTree == null || _idTree.TryGetValue(lowerId, out long value) == false)
            return false;

        if (ListStore.KindOf(value) != TermIdMask.Single)
        {
            multiple = true;
            return false;
        }

        entryId = ListStore.Decode(value);
        return true;
    }

    /// <summary>Whether the entry exists and belongs to another document than the one with this id. A hash that is shared by two ids shows up like this.</summary>
    public bool EntryBelongsToAnotherDocument(long entryId, ReadOnlySpan<byte> lowerId)
    {
        if (_hasIdReader == false)
            return false;

        UnmanagedSpan raw = _idReader.GetTerm(entryId);
        if (raw.Length == 0)
            return false;

        return _idReader.GetDecodedTerm(raw).SequenceEqual(lowerId) == false;
    }

    /// <summary>The id of the document of the entry, for messages. Null when the entry does not exist.</summary>
    public string DescribeEntry(long entryId)
    {
        if (_hasIdReader == false)
            return null;

        UnmanagedSpan raw = _idReader.GetTerm(entryId);
        return raw.Length == 0 ? null : Encoding.UTF8.GetString(_idReader.GetDecodedTerm(raw));
    }

    public long RootPageOf(Slice field) => Searcher.FieldCache.GetLookupRootPage(field);

    public CompactTree TermsOf(Slice field) => Searcher.GetTermsFor(field);

    /// <summary>Starts reading the terms of an entry. Dispose the key scope with <see cref="EndEntry"/>.</summary>
    public EntryTermsReader ReadEntry(long entryId) => Searcher.GetEntryTermsReader(entryId, ref _page, _key);

    public void EndEntry() => _key.Reset();

    public void Dispose()
    {
        if (_hasIdReader)
            _idReader.Dispose();

        Snapshot.Dispose();
    }
}
