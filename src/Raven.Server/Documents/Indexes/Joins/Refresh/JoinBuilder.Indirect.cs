using System;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Voron.Data.CompactTrees;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>
/// The build of the atom lists of an Indirect file. The list of an atom holds the keys whose group has an inner entry with the atom, so it is the inverse of the atoms of the keys (<c>ka</c>) that the walk of
/// the inner key terms writes: the pairs of an atom and a key are noted while the groups are written, sorted once, and every list is written by one update. No outer entry takes part: the outer entries of a key are
/// read from the outer index when a query needs them.
/// </summary>
internal sealed partial class JoinBuilder
{
    private struct KeyAtomPair : IComparable<KeyAtomPair>
    {
        public long Code;
        public long KeyRef;

        public int CompareTo(KeyAtomPair other)
        {
            int result = Code.CompareTo(other.Code);
            return result != 0 ? result : KeyRef.CompareTo(other.KeyRef);
        }
    }

    private ContextBoundNativeList<KeyAtomPair> _keyPairs;
    private ContextBoundNativeList<long> _keyList;
    private bool _collectingKeys;

    private bool IsIndirect => _file.Schema.IsIndirect;

    private void InitializeIndirect()
    {
        _keyPairs = new ContextBoundNativeList<KeyAtomPair>(_allocator, 1024);
        _keyList = new ContextBoundNativeList<long>(_allocator, 1024);
    }

    private void DisposeIndirect()
    {
        _keyPairs.Dispose();
        _keyList.Dispose();
    }

    /// <summary>The key joins the lists of the atoms that its group has: noted for the end of the walk, or queued when more pairs than a queue holds are noted.</summary>
    private void NoteKeyAtoms(long keyRef, ReadOnlySpan<long> codes)
    {
        foreach (long code in codes)
        {
            if (_collectingKeys)
            {
                if (_keyPairs.Count < _flushThreshold)
                {
                    _keyPairs.Add(new KeyAtomPair { Code = code, KeyRef = keyRef });
                    continue;
                }

                // more pairs than a queue holds: the pairs so far are queued, the walk goes on with the queue alone
                PairsOverflowed = true;
                QueueKeyPairs(_keyPairs.ToSpan());
                _keyPairs.Count = 0;
                _collectingKeys = false;
            }

            _writer.AddKeyToAtom(JoinReader.AtomOfCode(code), keyRef);
        }
    }

    private void QueueKeyPairs(ReadOnlySpan<KeyAtomPair> pairs)
    {
        foreach (KeyAtomPair pair in pairs)
            _writer.AddKeyToAtom(JoinReader.AtomOfCode(pair.Code), pair.KeyRef);
    }

    /// <summary>Writes the list of every atom that was noted, ascending, by one update.</summary>
    private void WriteKeyLists()
    {
        _collectingKeys = false;
        Span<KeyAtomPair> pairs = _keyPairs.ToSpan();
        pairs.Sort();

        int index = 0;
        while (index < pairs.Length)
        {
            long code = pairs[index].Code;
            int end = index;
            while (end < pairs.Length && pairs[end].Code == code)
                end++;

            _keyList.Count = 0;
            _keyList.GrowFitting(end - index);
            for (int i = index; i < end; i++)
                _keyList.AddUnsafe(ListStore.Encode(pairs[i].KeyRef));

            _writer.UpdateAtomList(JoinReader.AtomOfCode(code), _keyList.ToSpan(), default);
            DirectAtoms++;
            index = end;
            ThrowIfCancelled();
        }

        _keyPairs.Count = 0;
    }

    /// <summary>
    /// Walks the terms of the key field of the outer index: the posting list of a term holds the outer entries with that key. The keys get a reference (a key that has no group yet is known to the file,
    /// so that the row of an outer entry names it) and every entry gets the row of its keys. No list of an atom is touched: the outer entries are not in the lists.
    /// </summary>
    private void BuildOuterKeysIndirect()
    {
        CompactTree keys = _outer.TermsOf(_plan.OuterKeyField);
        if (keys == null)
            return;

        using var scope = new CompactKeyCacheScope(_outer.Snapshot.Transaction.LowLevelTransaction);
        var iterator = keys.Iterate();
        iterator.Reset();
        while (iterator.MoveNext(scope.Key, out long value, out _))
        {
            _token.ThrowIfCancellationRequested();

            ReadOnlySpan<byte> term = scope.Key.Decoded();
            if (IsNoKey(term) || term.Length > JoinLimits.MaxTermLength)
                continue;

            _entries.Count = 0;
            _outerLists.Read(value, ref _entries);

            long keyRef = _writer.GetOrAddKey(term);
            ReadOnlySpan<long> outers = _entries.ToSpan();
            long termHash = _writer.Schema.HasOuterFingerprints ? OuterKeyFingerprint.HashOf(term) : 0;
            foreach (long outer in outers)
                NoteRowKey(outer, keyRef, ofInnerDocument: false, termHash);

            if (_writer.KeyHasGroup(keyRef))
                JoinedEntries += outers.Length;

            ThrowIfCancelled();
        }

        WriteRowKeys(ofInnerDocument: false);
    }
}
