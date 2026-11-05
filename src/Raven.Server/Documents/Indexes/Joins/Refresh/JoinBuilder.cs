using Raven.Client.Documents.Indexes;
using System;
using System.Threading;
using Corax.Indexing;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Server;
using Sparrow.Threading;
using Voron;
using Voron.Data.CompactTrees;
using Voron.Data.Containers;
using Voron.Util;
using CoraxConstants = Corax.Constants;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>
/// Fills an empty join index file from the snapshots of the two indexes. It walks the terms of the indexes instead of their entries:
/// the terms of the id field give the entries of the documents, the terms of the outer field give the entries that point to an inner document,
/// the terms of the declared fields give the terms of the inner entries. Nothing is allocated per entry, the buffers are native memory.
/// </summary>
internal sealed partial class JoinBuilder : IDisposable
{
    private struct InnerTerm : IComparable<InnerTerm>
    {
        /// <summary>The key of the inner document.</summary>
        public long Entry;
        public long TermRef;

        public int CompareTo(InnerTerm other)
        {
            int result = Entry.CompareTo(other.Entry);
            return result != 0 ? result : TermRef.CompareTo(other.TermRef);
        }
    }

    private struct EntryKey : IComparable<EntryKey>
    {
        public long Entry;
        public long Key;

        public int CompareTo(EntryKey other) => Entry.CompareTo(other.Entry);
    }

    private readonly int _flushThreshold;
    private readonly IJoinFile _file;
    private readonly JoinPlan _plan;
    private readonly IndexView _outer;
    private readonly IndexView _inner;
    private readonly CancellationToken _token;
    private readonly ByteStringContext _allocator = new(SharedMultipleUseFlag.None);
    private readonly ListStore _outerLists;
    private readonly ListStore _innerLists;

    private ContextBoundNativeList<InnerTerm> _innerTerms;
    private ContextBoundNativeList<EntryKey> _innerKeys;
    private ContextBoundNativeList<long>[] _members;
    private ContextBoundNativeList<long> _entries;
    private ContextBoundNativeList<long> _refs;
    private ContextBoundNativeList<long>[] _fieldRefs;
    private ContextBoundNativeList<long> _atoms;
    private ContextBoundNativeList<long> _codes;

    private JoinWriter _writer;

    public long OuterDocuments { get; private set; }

    public long InnerDocuments { get; private set; }

    public long JoinedEntries { get; private set; }

    /// <summary>How many times the queued list changes were applied in the build, and how many lists were changed by it.</summary>
    public int ListFlushes { get; private set; }

    public int ListUpdates { get; private set; }

    /// <param name="flushThreshold">The number of queued list changes at which they are applied, which is also the most pairs of an atom and a key that are noted.</param>
    /// <param name="minAtomEntries">The atoms with at least this many outer entries get their list written at once; zero queues every entry of every atom.</param>
    public JoinBuilder(IJoinFile file, JoinPlan plan, IndexView outer, IndexView inner, CancellationToken token, int flushThreshold = JoinWriter.DefaultFlushThreshold, int minAtomEntries = 0)
    {
        _flushThreshold = flushThreshold;
        _minAtomEntries = minAtomEntries;
        _file = file;
        _plan = plan;
        _outer = outer;
        _inner = inner;
        _token = token;
        _outerLists = new ListStore(outer.Snapshot.Transaction.LowLevelTransaction, default);
        _innerLists = new ListStore(inner.Snapshot.Transaction.LowLevelTransaction, default);
        _innerTerms = new ContextBoundNativeList<InnerTerm>(_allocator, 1024);
        _innerKeys = new ContextBoundNativeList<EntryKey>(_allocator, 1024);
        _entries = new ContextBoundNativeList<long>(_allocator, 1024);
        _refs = new ContextBoundNativeList<long>(_allocator, 32);
        _members = new ContextBoundNativeList<long>[plan.GroupingFields.Length];
        for (int g = 0; g < _members.Length; g++)
            _members[g] = new ContextBoundNativeList<long>(_allocator, 1024);

        InitializeAtoms();
        _atoms = new ContextBoundNativeList<long>(_allocator, 64);
        _codes = new ContextBoundNativeList<long>(_allocator, 64);
        _fieldRefs = new ContextBoundNativeList<long>[plan.Fields.Length];
        for (int f = 0; f < _fieldRefs.Length; f++)
            _fieldRefs[f] = new ContextBoundNativeList<long>(_allocator, 32);
    }

    /// <summary>Fills the lists and writes the generation that the two snapshots stand for. The lists are not served before the transaction of the index commits.</summary>
    public JoinIndexGeneration Run()
    {
        _writer = _file.BeginWrite();
        _writer.FlushThreshold = _flushThreshold;
        try
        {
            BuildInnerDocuments();
            BuildInnerFields();
            BuildGroupings();
            BuildOuterDocuments();
            if (_plan.IsFieldJoin)
            {
                BuildKeyGroups();
                BuildOuterKeys();
            }
            else
            {
                BuildJoinedEntries();
            }

            JoinIndexGeneration generation = new(0, _outer.Snapshot.EnvironmentId, JoinCursors.From(_outer.Snapshot.Consumed),
                _inner.Snapshot.EnvironmentId, JoinCursors.From(_inner.Snapshot.Consumed), _inner.Searcher.LastEntryId, 0);

            JoinIndexGeneration committed = _writer.Commit(generation);
            ListFlushes = _writer.Flushes;
            ListUpdates = _writer.ListUpdates;
            return committed;
        }
        finally
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void ThrowIfCancelled() => _token.ThrowIfCancellationRequested();

    private void BuildInnerDocuments()
    {
        CompactTree ids = _inner.TermsOf(_plan.IdField);
        if (ids == null)
            return;

        using var scope = new CompactKeyCacheScope(_inner.Snapshot.Transaction.LowLevelTransaction);
        var iterator = ids.Iterate();
        iterator.Reset();
        while (iterator.MoveNext(scope.Key, out long value, out _))
        {
            if (ListStore.KindOf(value) != TermIdMask.Single)
                throw new JoinUnsupportedException("A document of the inner index has several entries: a join index holds one entry per document.");

            long key = _file.InnerKeyOf(scope.Key.Decoded());
            long entry = ListStore.Decode(value);
            if (_writer.SetInnerDocument(key, entry) != 0)
                throw new JoinRebuildRequiredException(JoinRebuildReason.HashCollision);

            _innerKeys.Add(new EntryKey { Entry = entry, Key = key });
            InnerDocuments++;
            ThrowIfCancelled();
        }

        _innerKeys.ToSpan().Sort();
    }

    /// <summary>The key of the inner document that has the entry in the inner index.</summary>
    private long KeyOf(long innerEntry)
    {
        int at = _innerKeys.ToSpan().BinarySearch(new EntryKey { Entry = innerEntry });
        if (at < 0)
            throw new JoinUnsupportedException("The inner index has an entry that is not the entry of a document.");

        return _innerKeys[at].Key;
    }

    private void BuildInnerFields()
    {
        for (int f = 0; f < _plan.Fields.Length; f++)
        {
            _innerTerms.Count = 0;
            CompactTree terms = _inner.TermsOf(_plan.Fields[f]);
            if (terms == null)
                continue;

            bool isLong = _plan.IsLong(f);
            using var scope = new CompactKeyCacheScope(_inner.Snapshot.Transaction.LowLevelTransaction);
            var iterator = terms.Iterate();
            iterator.Reset();
            while (iterator.MoveNext(scope.Key, out long value, out _))
            {
                _token.ThrowIfCancellationRequested();

                ReadOnlySpan<byte> term = scope.Key.Decoded();
                InnerProjector.ValidateTerm(_plan, f, term, 0);

                if (isLong)
                    InnerProjector.ParseLong(_plan, f, term);

                long termRef = _writer.GetOrAddTerm(f, term);

                _entries.Count = 0;
                _innerLists.Read(value, ref _entries);
                foreach (long entry in _entries.ToSpan())
                    _innerTerms.Add(new InnerTerm { Entry = KeyOf(entry), TermRef = termRef });
            }

            WriteInnerTerms(f);
        }
    }

    private void WriteInnerTerms(int field)
    {
        Span<InnerTerm> pairs = _innerTerms.ToSpan();
        pairs.Sort();

        int index = 0;
        while (index < pairs.Length)
        {
            long entry = pairs[index].Entry;
            _refs.Count = 0;
            int end = index;
            while (end < pairs.Length && pairs[end].Entry == entry)
                _refs.Add(pairs[end++].TermRef);

            if (_refs.Count > 1 && _plan.Schema.IsMultiValued(field) == false)
                throw new JoinUnsupportedException($"The field '{_plan.Definition.Fields[field].Field}' of the inner index holds several values in one document, a field of the kind {_plan.Definition.Fields[field].Kind} holds one.");

            if (_refs.Count > JoinLimits.MaxTermsPerEntry)
                throw new JoinUnsupportedException($"The field '{_plan.Definition.Fields[field].Field}' of the inner index has more than {JoinLimits.MaxTermsPerEntry} terms in one document.");

            _writer.SetInnerTerms(field, entry, _refs.ToSpan());

            index = end;
            ThrowIfCancelled();
        }
    }

    private void BuildGroupings()
    {
        for (int g = 0; g < _members.Length; g++)
        {
            _members[g].Count = 0;
            CompactTree terms = _inner.TermsOf(_plan.GroupingFields[g]);
            if (terms == null)
                continue;

            byte[] term = InnerProjector.GroupingTerm(_inner, _plan, g);
            if (terms.TryGetValue(term, out long value) == false)
                continue;

            _innerLists.Read(value, ref _members[g]);
            Span<long> members = _members[g].ToSpan();
            for (int i = 0; i < members.Length; i++)
                members[i] = KeyOf(members[i]);

            members.Sort();
            foreach (long key in members)
            {
                _writer.AddGroupingMember(g, key);
                ThrowIfCancelled();
            }
        }
    }

    private void BuildOuterDocuments()
    {
        CompactTree ids = _outer.TermsOf(_plan.IdField);
        if (ids == null)
            return;

        using var scope = new CompactKeyCacheScope(_outer.Snapshot.Transaction.LowLevelTransaction);
        var iterator = ids.Iterate();
        iterator.Reset();
        while (iterator.MoveNext(scope.Key, out long value, out _))
        {
            if (ListStore.KindOf(value) != TermIdMask.Single)
                throw new JoinUnsupportedException("A document of the outer index has several entries: a join index holds one entry per document.");

            long hash = _file.HashDocumentId(scope.Key.Decoded());
            if (_writer.SetOuterDocument(hash, ListStore.Decode(value)) != 0)
                throw new JoinRebuildRequiredException(JoinRebuildReason.HashCollision);

            OuterDocuments++;
            ThrowIfCancelled();
        }
    }

    private void BuildJoinedEntries()
    {
        CompactTree keys = _outer.TermsOf(_plan.OuterKeyField);
        if (keys == null)
            return;

        _collecting = _minAtomEntries > 0;
        CompactTree innerIds = _inner.TermsOf(_plan.IdField);
        using var scope = new CompactKeyCacheScope(_outer.Snapshot.Transaction.LowLevelTransaction);
        var iterator = keys.Iterate();
        iterator.Reset();
        while (iterator.MoveNext(scope.Key, out long value, out _))
        {
            _token.ThrowIfCancellationRequested();

            ReadOnlySpan<byte> term = scope.Key.Decoded();
            if (term.SequenceEqual(CoraxConstants.NullValueSlice.AsReadOnlySpan()) || term.SequenceEqual(CoraxConstants.NonExistingValueSlice.AsReadOnlySpan()))
                continue;

            _entries.Count = 0;
            _outerLists.Read(value, ref _entries);

            long inner = _file.InnerKeyOf(term);
            bool joined = false;
            int membership = 0;
            if (innerIds != null && innerIds.TryGetValue(term, out long innerValue))
            {
                if (ListStore.KindOf(innerValue) != TermIdMask.Single)
                    throw new JoinUnsupportedException("A document of the inner index has several entries: a join index holds one entry per document.");

                LoadInner(inner, out membership);
                joined = true;
            }

            foreach (long outer in _entries.ToSpan())
                _writer.AddOuterKey(outer, inner);

            if (joined)
            {
                // an entry that names several documents is in the list of an atom when any of them has it: it is added for every document that has it, the list holds it once
                ReadOnlySpan<long> outers = _entries.ToSpan();
                for (int f = 0; f < _fieldRefs.Length; f++)
                {
                    foreach (long termRef in _fieldRefs[f].ToSpan())
                        AddAtom(JoinReader.AtomOfTerm(f, termRef), value, outers);
                }

                AddAtom(JoinReader.AtomOfAny(), value, outers);
                for (int g = 0; g < _members.Length; g++)
                {
                    if ((membership & (1 << g)) != 0)
                        AddAtom(JoinReader.AtomOfGrouping(g), value, outers);
                }

                JoinedEntries += outers.Length;
            }

            ThrowIfCancelled();
        }

        WriteAtoms();
        CountEntriesWithSeveralJoined();
    }

    /// <summary>The entries that name two or more documents are listed by the writer; those of them whose documents exist are the ones a score of the join is not a constant for.</summary>
    private void CountEntriesWithSeveralJoined()
    {
        _writer.Flush();
        _entries.Count = 0;
        _writer.ReadList(_writer.SlotHandle(_writer.MultiKeySlot), ref _entries);

        long several = 0;
        foreach (long outer in _entries.ToSpan())
        {
            _refs.Count = 0;
            _writer.TryGetOuterKeys(outer, ref _refs);
            int joined = 0;
            foreach (long key in _refs.ToSpan())
            {
                if (_writer.TryGetInnerDocument(key, out _) && ++joined == 2)
                {
                    several++;
                    break;
                }
            }

            ThrowIfCancelled();
        }

        _writer.AddRowsWithSeveralJoined(several);
    }

    /// <summary>The terms and the groupings of an inner document (by its key) as the file holds them.</summary>
    private void LoadInner(long inner, out int membership)
    {
        for (int f = 0; f < _fieldRefs.Length; f++)
        {
            _fieldRefs[f].Count = 0;
            _writer.TryGetInnerTerms(f, inner, ref _fieldRefs[f]);
        }

        membership = 0;
        for (int g = 0; g < _members.Length; g++)
        {
            if (_members[g].ToSpan().BinarySearch(inner) >= 0)
                membership |= 1 << g;
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _outerLists.Dispose();
        _innerLists.Dispose();
        _innerTerms.Dispose();
        _innerKeys.Dispose();
        _entries.Dispose();
        _refs.Dispose();
        _atoms.Dispose();
        _codes.Dispose();
        DisposeAtoms();
        foreach (ContextBoundNativeList<long> list in _members)
            list.Dispose();
        foreach (ContextBoundNativeList<long> list in _fieldRefs)
            list.Dispose();

        _allocator.Dispose();
    }
}
