using System;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Voron.Data.CompactTrees;
using CoraxConstants = Corax.Constants;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>The build of a join on a field of the inner index: the groups of the inner entries by key, what each group has, and the key of every outer entry.</summary>
internal sealed partial class JoinBuilder
{
    private static bool IsNoKey(ReadOnlySpan<byte> term)
    {
        return term.SequenceEqual(CoraxConstants.NullValueSlice.AsReadOnlySpan()) || term.SequenceEqual(CoraxConstants.NonExistingValueSlice.AsReadOnlySpan());
    }

    /// <summary>
    /// Walks the terms of the key field of the inner index: the posting list of a term is the group of the inner entries of that key. For every group the number of its entries that have each atom
    /// (any, a term of a declared field, a grouping) is counted, and the atoms are kept in a list of the key.
    /// </summary>
    private void BuildKeyGroups()
    {
        CompactTree keys = _inner.TermsOf(_plan.InnerKeyField);
        if (keys == null)
            return;

        using var scope = new CompactKeyCacheScope(_inner.Snapshot.Transaction.LowLevelTransaction);
        var iterator = keys.Iterate();
        iterator.Reset();
        while (iterator.MoveNext(scope.Key, out long value, out _))
        {
            _token.ThrowIfCancellationRequested();

            ReadOnlySpan<byte> term = scope.Key.Decoded();
            if (IsNoKey(term))
                continue;

            if (term.Length > JoinLimits.MaxTermLength)
                throw new JoinUnsupportedException($"The field '{_plan.Definition.InnerField}' of the inner index has a key longer than {JoinLimits.MaxTermLength} bytes.");

            long keyRef = _writer.GetOrAddKey(term);
            _entries.Count = 0;
            _innerLists.Read(value, ref _entries);

            _atoms.Count = 0;
            foreach (long entry in _entries.ToSpan())
            {
                long inner = KeyOf(entry);

                // an inner entry that holds several keys is in the group of each of them
                _writer.AddInnerKey(inner, keyRef);
                LoadInner(inner, out int membership);

                _atoms.Add(JoinReader.CodeOfAny());
                for (int f = 0; f < _fieldRefs.Length; f++)
                {
                    foreach (long termRef in _fieldRefs[f].ToSpan())
                        _atoms.Add(JoinReader.CodeOfTerm(f, termRef));
                }

                for (int g = 0; g < _members.Length; g++)
                {
                    if ((membership & (1 << g)) != 0)
                        _atoms.Add(JoinReader.CodeOfGrouping(g));
                }
            }

            WriteGroup(keyRef, _entries.Count);
            ThrowIfCancelled();
        }
    }

    /// <summary>Counts the atoms collected for a group (sorted here), writes the counts and the list of the atoms of the key.</summary>
    private void WriteGroup(long keyRef, int size)
    {
        Span<long> atoms = _atoms.ToSpan();
        atoms.Sort();

        _codes.Count = 0;
        int index = 0;
        while (index < atoms.Length)
        {
            long code = atoms[index];
            int end = index;
            while (end < atoms.Length && atoms[end] == code)
                end++;

            _writer.AddPairCount(keyRef, code, end - index);
            _codes.Add(code);
            index = end;
        }

        // the groups of two or more entries are the ones an AND or a NOT over the lists is not exact for: their outer entries are in a list of their own
        if (size >= 2)
        {
            _codes.Add(_writer.CodeOfSeveralInner);
            _writer.AddRowsWithSeveralJoined(1);
        }

        _writer.SetKeyAtoms(keyRef, _codes.ToSpan());
    }

    /// <summary>Walks the terms of the key field of the outer index: the posting list of a term holds the outer entries with that key, which join the group of the key. An entry with several keys joins each group.</summary>
    private void BuildOuterKeys()
    {
        CompactTree keys = _outer.TermsOf(_plan.OuterKeyField);
        if (keys == null)
            return;

        _collecting = _minAtomEntries > 0;
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
            _atoms.Count = 0;
            _writer.ReadKeyAtoms(keyRef, ref _atoms);

            ReadOnlySpan<long> outers = _entries.ToSpan();
            foreach (long outer in outers)
                _writer.AddOuterKey(outer, keyRef);

            if (_atoms.Count > 0)
                JoinedEntries += outers.Length;

            foreach (long code in _atoms.ToSpan())
                AddAtom(JoinReader.AtomOfCode(code), value, outers);

            ThrowIfCancelled();
        }

        WriteAtoms();
        CountEntriesWithSeveralKeys();
    }

    /// <summary>
    /// The entries that hold two or more keys are listed by the writer; those of them with two or more keys that have an inner entry in their group are the ones a score of the join is not a constant for,
    /// and they are joined with several inner entries (the list of those, which an AND or a NOT checks entry by entry) even when no group has two.
    /// </summary>
    private void CountEntriesWithSeveralKeys()
    {
        _writer.Flush();
        _entries.Count = 0;
        _writer.ReadList(_writer.SlotHandle(_writer.MultiKeySlot), ref _entries);

        long several = 0;
        foreach (long outer in _entries.ToSpan())
        {
            _refs.Count = 0;
            _writer.TryGetOuterKeys(outer, ref _refs);
            int withGroup = 0;
            foreach (long keyRef in _refs.ToSpan())
            {
                if (_writer.GetPairCount(keyRef, JoinReader.CodeOfAny()) > 0 && ++withGroup == 2)
                {
                    several++;
                    _writer.AddToAtom(JoinReader.AtomOfCode(_writer.CodeOfSeveralInner), outer);
                    break;
                }
            }

            ThrowIfCancelled();
        }

        _writer.AddRowsWithSeveralKeys(several);
    }
}
