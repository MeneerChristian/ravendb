using System;
using System.Runtime.InteropServices;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow;
using Voron.Data.CompactTrees;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>
/// The refresh of a join on a field of the inner index. An outer entry is in the list of an atom when the group of the inner entries of its key has the atom, that is when the count of the pair
/// (key, atom) is above zero. A change of an inner entry changes counts; only a count that goes from or to zero changes a list, and then only for the outer entries of that key.
/// </summary>
internal sealed partial class JoinIncrementalRefresh
{
    /// <summary>What a change of an inner entry does to the count of a pair of a key and an atom (by its code).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PairDelta : IComparable<PairDelta>
    {
        public long KeyRef;
        public long Code;
        public int Delta;

        public int CompareTo(PairDelta other)
        {
            int result = KeyRef.CompareTo(other.KeyRef);
            return result != 0 ? result : Code.CompareTo(other.Code);
        }
    }

    /// <summary>An outer entry with several keys that sits in a group whose emptiness changes: whether it had two or more keys with an inner entry before this refresh.</summary>
    private struct SeveralKeysCheck : IComparable<SeveralKeysCheck>
    {
        public long Row;
        public int Was;

        public int CompareTo(SeveralKeysCheck other) => Row.CompareTo(other.Row);
    }

    /// <summary>A pair of a key and an atom whose count went from or to zero: the clean outer entries of the key join the list of the atom or leave it.</summary>
    private struct Move : IComparable<Move>
    {
        public long Atom;
        public long KeyRef;

        /// <summary>The outer entries of the key in the outer index, those of the dirty rows included.</summary>
        public long Rows;
        public int Add;
        public int GroupChanged;

        public int CompareTo(Move other)
        {
            int result = Atom.CompareTo(other.Atom);
            return result != 0 ? result : KeyRef.CompareTo(other.KeyRef);
        }
    }

    private ContextBoundNativeList<Move> _moves;
    private ContextBoundNativeList<long> _moveAdds;
    private ContextBoundNativeList<long> _moveRemovals;
    private ContextBoundNativeList<long> _moveEncoded;
    private ContextBoundNativeList<PairDelta> _deltas;
    private ContextBoundNativeList<long> _oldCodes;
    private ContextBoundNativeList<long> _newCodes;
    private ContextBoundNativeList<long> _oldKeyRefs;
    private ContextBoundNativeList<long> _newKeyRefs;
    private ContextBoundNativeList<SeveralKeysCheck> _recheck;
    private ContextBoundNativeList<long> _cachedCodes;
    private ContextBoundNativeList<long> _keyAtomAdds;
    private ContextBoundNativeList<long> _termHashes;
    private ContextBoundNativeList<long> _keyAtomRemovals;
    private ContextBoundNativeList<long> _cleanRows;
    private ContextBoundNativeList<long> _classifiedKeyRefs;
    private ContextBoundNativeList<KeySpan> _classifiedKeys;
    private ContextBoundNativeList<long> _classifiedFingerprints;
    private long _cachedKeyRef;

    private static void SortDistinct(ref ContextBoundNativeList<long> list)
    {
        Span<long> values = list.ToSpan();
        values.Sort();
        int write = 0;
        for (int read = 0; read < values.Length; read++)
        {
            if (write > 0 && values[write - 1] == values[read])
                continue;

            values[write++] = values[read];
        }

        list.Count = write;
    }

    /// <summary>
    /// Reads the keys of an outer entry into <see cref="_newKeyRefs"/> (ascending, without repeats). A key that is too long to equal a key of the file is none. When keys are not added, a key the file does not know
    /// sets <paramref name="unknown"/> and is left out. True when the entry has a key.
    /// </summary>
    private bool ReadOuterKeyRefs(JoinWriter writer, IndexView outer, long entry, bool add, out bool unknown, bool entryIsRead = false)
    {
        unknown = false;
        _newKeyRefs.Count = 0;
        if (entryIsRead == false)
        {
            _counters.Add(ref _counters.OuterKeyReads, 1);
            if (_keyReader.Read(outer, entry, _file) == false)
                return false;
        }

        int lookups = 0;
        for (int i = 0; i < _keyReader.TermCount; i++)
        {
            ReadOnlySpan<byte> term = _keyReader.TermAt(i);
            if (term.Length > JoinLimits.MaxTermLength)
                continue;

            long keyRef;
            lookups++;
            if (add)
            {
                keyRef = writer.GetOrAddKey(term);
            }
            else if (writer.TryGetKey(term, out keyRef) == false)
            {
                unknown = true;
                continue;
            }

            _newKeyRefs.Add(keyRef);
        }

        _counters.Add(ref _counters.KeyLookups, lookups);
        SortDistinct(ref _newKeyRefs);
        return _newKeyRefs.Count > 0 || unknown;
    }

    /// <summary>
    /// The fingerprint of the distinct keys that the outer entry the reader holds names (the terms that a file can have as a key): the hash of the sorted hashes of the terms, never 0. The number of those keys is
    /// the result. An Indirect file keeps the fingerprint with the keys of a row of two or more keys, so that a row whose keys did not change is known without looking up its keys in the dictionary of the file.
    /// </summary>
    private int FingerprintOfOuterKeys(out long fingerprint)
    {
        _termHashes.Count = 0;
        for (int i = 0; i < _keyReader.TermCount; i++)
        {
            ReadOnlySpan<byte> term = _keyReader.TermAt(i);
            if (term.Length <= JoinLimits.MaxTermLength)
                _termHashes.Add(OuterKeyFingerprint.HashOf(term));
        }

        return OuterKeyFingerprint.Of(_termHashes.ToSpan(), out fingerprint);
    }

    /// <summary>Whether the outer entry names now the keys that the file has for it (none for none).</summary>
    private bool HasTheSameFieldKey(JoinWriter writer, IndexView outer, long entry, int index)
    {
        _counters.Add(ref _counters.OuterKeyReads, 1);
        bool read = _keyReader.Read(outer, entry, _file);
        long fingerprint = 0;
        if (read && writer.Schema.HasOuterFingerprints)
        {
            int distinct = FingerprintOfOuterKeys(out fingerprint);
            if (distinct >= 1 && writer.TryGetOuterKeysFingerprint(entry, out long storedFingerprint))
            {
                if (storedFingerprint == fingerprint)
                    return true;

                // the keys are not the ones of the file; a row of one key is classified below, a row of several is read again when it is added
                if (distinct >= 2)
                    return false;
            }
        }

        ReadOuterKeyRefs(writer, outer, entry, add: false, out bool unknown, entryIsRead: read);
        _rowKeys.Count = 0;
        bool stored = writer.TryGetOuterKeys(entry, ref _rowKeys);

        // every key of the file is known: an entry with a key that is not known has another set
        if (unknown)
            return false;

        bool same = _newKeyRefs.Count == 0 ? stored == false : stored && _rowKeys.ToSpan().SequenceEqual(_newKeyRefs.ToSpan());
        if (same == false && (writer.Schema.IsIndirect == false || _newKeyRefs.Count < 2))
        {
            // the row is added again with these keys: the entry is not read a second time (the fingerprint of a row of several keys is made from the entry the reader holds, so such a row is read again)
            _classifiedKeys[index] = new KeySpan(_classifiedKeyRefs.Count, _newKeyRefs.Count);
            _classifiedKeyRefs.AddRange(_newKeyRefs.ToSpan());
            _classifiedFingerprints[index] = fingerprint;
        }

        return same;
    }

    /// <summary>The codes of the atoms that the group of the key has, as the file has them now. The answer is kept for the next call with the same key until the atoms change.</summary>
    private ReadOnlySpan<long> AtomsOfKey(JoinWriter writer, long keyRef)
    {
        if (_cachedKeyRef != keyRef)
        {
            _cachedCodes.Count = 0;
            writer.ReadKeyAtoms(keyRef, ref _cachedCodes);
            _cachedKeyRef = keyRef;
        }

        return _cachedCodes.ToSpan();
    }

    /// <summary>Takes an outer entry out of the lists of the atoms that the groups of its keys have.</summary>
    private void RemoveOuterFieldRow(JoinWriter writer, long entry, long hash, bool entryIsReplaced, int index)
    {
        if (writer.Schema.IsIndirect)
        {
            // the outer entries are in no list of the file: the row of the entry and the row of the document are all there is. The cache of the lists of the keys is told which keys the entry leaves
            if (Patch != null)
                RecordRemovedOuterRow(writer, entry, entryIsReplaced, index);

            writer.SetOuterKeys(entry, default);
            writer.RemoveOuterDocument(hash);
            return;
        }

        _rowKeys.Count = 0;
        if (writer.TryGetOuterKeys(entry, ref _rowKeys) && _rowKeys.Count > 0)
        {
            int withGroup = 0;
            foreach (long keyRef in _rowKeys.ToSpan())
            {
                ReadOnlySpan<long> codes = AtomsOfKey(writer, keyRef);
                if (codes.Length > 0)
                    withGroup++;

                foreach (long code in codes)
                    writer.RemoveFromAtom(JoinReader.AtomOfCode(code), entry);
            }

            if (withGroup >= 2)
            {
                writer.AddRowsWithSeveralKeys(-1);
                writer.RemoveFromAtom(JoinReader.AtomOfCode(writer.CodeOfSeveralInner), entry);
            }

            writer.SetOuterKeys(entry, default);
        }

        writer.RemoveOuterDocument(hash);
    }

    /// <summary>
    /// A row that leaves the outer index leaves the list of each of its keys: the old keys are the ones of the file. A row whose document gets another entry keeps them until the entry is added
    /// (<see cref="RecordAddedOuterRow"/>), where the change of the document is recorded as one.
    /// </summary>
    private void RecordRemovedOuterRow(JoinWriter writer, long entry, bool entryIsReplaced, int index)
    {
        _rowKeys.Count = 0;
        writer.TryGetOuterKeys(entry, ref _rowKeys);
        if (entryIsReplaced)
        {
            _patchOld[index] = new KeySpan(_patchOldKeys.Count, _rowKeys.Count);
            _patchOldKeys.AddRange(_rowKeys.ToSpan());
        }
        else
        {
            Patch.RemoveRow(entry, _rowKeys.ToSpan());
        }
    }

    private void RecordAddedOuterRow(long entry, long oldEntry, int index, ReadOnlySpan<long> newKeys)
    {
        if (oldEntry == 0)
        {
            if (newKeys.Length > 0)
                Patch.AddRow(entry, newKeys);

            return;
        }

        KeySpan old = _patchOld[index];
        Patch.ChangeRow(oldEntry, _patchOldKeys.ToSpan().Slice(old.Start, old.Length), entry, newKeys);
    }

    /// <summary>Remembers the new entry of an outer document and puts it in the lists of the atoms that the groups of its keys have.</summary>
    private void AddOuterFieldRow(JoinWriter writer, IndexView outer, long hash, long entry, long oldEntry, int index)
    {
        if (writer.SetOuterDocument(hash, entry) != 0)
            throw new JoinRebuildRequiredException(JoinRebuildReason.HashCollision);

        bool hasKeys;
        KeySpan classified = _classifiedKeys[index];
        if (classified.Start >= 0)
        {
            _newKeyRefs.Count = 0;
            _newKeyRefs.AddRange(_classifiedKeyRefs.ToSpan().Slice(classified.Start, classified.Length));
            hasKeys = _newKeyRefs.Count > 0;
        }
        else
        {
            hasKeys = ReadOuterKeyRefs(writer, outer, entry, add: true, out _);
        }

        if (hasKeys)
        {
            long fingerprint = 0;
            if (writer.Schema.HasOuterFingerprints)
            {
                if (classified.Start >= 0)
                    fingerprint = _classifiedFingerprints[index];
                else
                    FingerprintOfOuterKeys(out fingerprint);
            }

            writer.SetOuterKeys(entry, _newKeyRefs.ToSpan(), fingerprint);
        }

        if (writer.Schema.IsIndirect)
        {
            if (Patch != null)
                RecordAddedOuterRow(entry, oldEntry, index, hasKeys ? _newKeyRefs.ToSpan() : default);

            return;
        }

        if (hasKeys == false)
            return;

        int withGroup = 0;
        foreach (long keyRef in _newKeyRefs.ToSpan())
        {
            ReadOnlySpan<long> codes = AtomsOfKey(writer, keyRef);
            if (codes.Length > 0)
                withGroup++;

            foreach (long code in codes)
                writer.AddToAtom(JoinReader.AtomOfCode(code), entry);
        }

        if (withGroup >= 2)
        {
            writer.AddRowsWithSeveralKeys(1);
            writer.AddToAtom(JoinReader.AtomOfCode(writer.CodeOfSeveralInner), entry);
        }
    }

    /// <summary>
    /// Brings the rows of an inner document (by its key) to its new state and records what the change does to the counts of the groups of its old and its new keys; the counts are changed once all documents
    /// of the batch are in. A key that the document keeps and an atom it keeps change nothing.
    /// </summary>
    private void ApplyInnerFieldChange(JoinWriter writer, IndexView inner, long key, Change change, int index)
    {
        JoinPlan plan = _plan;
        long oldEntry = change.Old;
        long newEntry = change.New;
        bool hadOld = oldEntry != 0;
        bool hasNew = newEntry != 0;

        _oldKeyRefs.Count = 0;
        _newKeyRefs.Count = 0;
        int oldMask = 0;
        for (int f = 0; f < plan.Fields.Length; f++)
        {
            _oldRefs[f].Count = 0;
            _newRefs[f].Count = 0;
            if (hadOld)
                writer.TryGetInnerTerms(f, key, ref _oldRefs[f]);
        }

        if (hadOld)
        {
            oldMask = OldMembership(writer, key);
            writer.TryGetInnerKeys(key, ref _oldKeyRefs);
        }

        int newMask = 0;
        if (hasNew)
        {
            _projector.Project(inner, newEntry);
            for (int t = 0; t < _projector.Count; t++)
            {
                int f = _projector.FieldOf(t);
                _newRefs[f].Add(writer.GetOrAddTerm(f, _projector.TermOf(t)));
            }

            newMask = _projector.Membership;
            if (writer.Schema.KeysAreInnerIds && _oldKeyRefs.Count == 1 && _projector.KeyCount == 1)
            {
                // the key of a document is its id, which a document keeps: the key the file has for it is the key it has now
                _newKeyRefs.Add(_oldKeyRefs[0]);
            }
            else
            {
                for (int k = 0; k < _projector.KeyCount; k++)
                    _newKeyRefs.Add(writer.GetOrAddKey(_projector.KeyAt(k)));

                _counters.Add(ref _counters.KeyLookups, _projector.KeyCount);
            }

            SortDistinct(ref _newKeyRefs);
            if (_newKeyRefs.Count > JoinLimits.MaxKeysPerEntry)
                throw new JoinUnsupportedException($"The field '{plan.Definition.InnerField ?? "id()"}' of the inner index holds {_newKeyRefs.Count} keys in one document, a join index holds at most {JoinLimits.MaxKeysPerEntry}.");
        }

        _newMasks[index] = newMask;

        for (int f = 0; f < plan.Fields.Length; f++)
        {
            _oldRefs[f].ToSpan().Sort();
            _newRefs[f].ToSpan().Sort();
        }

        bool sameKeys = _oldKeyRefs.ToSpan().SequenceEqual(_newKeyRefs.ToSpan());
        if (hadOld == hasNew && oldMask == newMask && sameKeys)
        {
            bool same = true;
            for (int f = 0; f < plan.Fields.Length && same; f++)
                same = _oldRefs[f].ToSpan().SequenceEqual(_newRefs[f].ToSpan());

            if (same)
            {
                if (hasNew && oldEntry != newEntry)
                    SetInnerDocumentRow(writer, key, oldEntry, newEntry);

                return;
            }
        }

        AddKeyDeltas(oldMask, newMask);

        for (int f = 0; f < plan.Fields.Length; f++)
        {
            if (_oldRefs[f].Count > 0 || _newRefs[f].Count > 0)
                writer.SetInnerTerms(f, key, _newRefs[f].ToSpan());
        }

        for (int g = 0; g < plan.GroupingFields.Length; g++)
        {
            bool was = (oldMask & (1 << g)) != 0;
            bool now = (newMask & (1 << g)) != 0;
            if (was && now == false)
                writer.RemoveGroupingMember(g, key);
            else if (was == false && now)
                writer.AddGroupingMember(g, key);
        }

        if (sameKeys == false)
            writer.SetInnerKeys(key, _newKeyRefs.ToSpan());

        if (hasNew)
            SetInnerDocumentRow(writer, key, oldEntry, newEntry);
        else
            writer.RemoveInnerDocument(key);
    }

    /// <summary>The codes of the atoms of an inner entry (the terms of its declared fields, any, its groupings), ascending.</summary>
    private void FillCodes(ContextBoundNativeList<long>[] refs, int mask, ref ContextBoundNativeList<long> codes)
    {
        codes.Count = 0;
        for (int f = 0; f < refs.Length; f++)
        {
            foreach (long termRef in refs[f].ToSpan())
                codes.Add(JoinReader.CodeOfTerm(f, termRef));
        }

        codes.Add(JoinReader.CodeOfAny());
        for (int g = 0; g < _plan.GroupingFields.Length; g++)
        {
            if ((mask & (1 << g)) != 0)
                codes.Add(JoinReader.CodeOfGrouping(g));
        }
    }

    /// <summary>
    /// What the change of an inner entry does to the counts: every atom it had leaves the groups of the keys it no longer has, every atom it has joins the groups of the keys it did not have,
    /// and in a key it keeps only the atoms that differ count.
    /// </summary>
    private void AddKeyDeltas(int oldMask, int newMask)
    {
        _oldCodes.Count = 0;
        _newCodes.Count = 0;
        if (_oldKeyRefs.Count > 0)
            FillCodes(_oldRefs, oldMask, ref _oldCodes);

        if (_newKeyRefs.Count > 0)
            FillCodes(_newRefs, newMask, ref _newCodes);

        ReadOnlySpan<long> oldKeys = _oldKeyRefs.ToSpan();
        ReadOnlySpan<long> newKeys = _newKeyRefs.ToSpan();
        int o = 0;
        int n = 0;
        while (o < oldKeys.Length || n < newKeys.Length)
        {
            if (n == newKeys.Length || (o < oldKeys.Length && oldKeys[o] < newKeys[n]))
            {
                foreach (long code in _oldCodes.ToSpan())
                    _deltas.Add(new PairDelta { KeyRef = oldKeys[o], Code = code, Delta = -1 });

                o++;
            }
            else if (o == oldKeys.Length || newKeys[n] < oldKeys[o])
            {
                foreach (long code in _newCodes.ToSpan())
                    _deltas.Add(new PairDelta { KeyRef = newKeys[n], Code = code, Delta = 1 });

                n++;
            }
            else
            {
                AddCodeDifference(oldKeys[o], _oldCodes.ToSpan(), _newCodes.ToSpan());
                o++;
                n++;
            }
        }
    }

    private void AddCodeDifference(long keyRef, ReadOnlySpan<long> before, ReadOnlySpan<long> after)
    {
        int b = 0;
        int a = 0;
        while (b < before.Length || a < after.Length)
        {
            if (a == after.Length || (b < before.Length && before[b] < after[a]))
                _deltas.Add(new PairDelta { KeyRef = keyRef, Code = before[b++], Delta = -1 });
            else if (b == before.Length || after[a] < before[b])
                _deltas.Add(new PairDelta { KeyRef = keyRef, Code = after[a++], Delta = 1 });
            else
            {
                a++;
                b++;
            }
        }
    }

    /// <summary>
    /// Adds up the changes of the batch per pair of a key and an atom and applies each sum once, so that two entries of one group that swap an atom in one batch change nothing.
    /// A count that becomes positive puts the clean outer entries of the key into the list of the atom, a count that becomes zero takes them out. An entry that holds several keys may have the atom
    /// through another key: its lists are decided when every count is in, and whether it has two or more keys with an inner entry is counted again for the ones whose key got or lost its group.
    /// </summary>
    private void ApplyPairDeltas(JoinWriter writer, ListStore outerLists, CompactTree keyTree)
    {
        if (_deltas.Count == 0)
            return;

        Span<PairDelta> deltas = _deltas.ToSpan();
        deltas.Sort();

        CheckCostOfListChanges(writer, outerLists, keyTree, deltas);
        if (writer.Schema.IsIndirect)
        {
            ApplyPairDeltasToKeyLists(writer, deltas);
            _deltas.Count = 0;
            return;
        }

        LoadMultiRows(writer);
        bool anyMulti = _multiRows.Count > 0;
        if (anyMulti)
            RememberRowsOfChangedGroups(writer, outerLists, keyTree, deltas);

        _moves.Count = 0;
        int index = 0;
        int pairIndex = 0;
        while (index < deltas.Length)
        {
            long keyRef = deltas[index].KeyRef;
            long code = deltas[index].Code;
            int sum = 0;
            while (index < deltas.Length && deltas[index].KeyRef == keyRef && deltas[index].Code == code)
                sum += deltas[index++].Delta;

            if (sum == 0)
                continue;

            int now = CountAfter(writer, keyRef, code, sum, ref pairIndex);
            int before = now - sum;

            if (code == JoinReader.CodeOfAny() && (before >= 2) != (now >= 2))
            {
                writer.AddRowsWithSeveralJoined(now >= 2 ? 1 : -1);
                AddMove(writer, outerLists, keyTree, keyRef, writer.CodeOfSeveralInner, now >= 2);
            }

            if ((before == 0) == (now == 0))
                continue;

            AddMove(writer, outerLists, keyTree, keyRef, code, now > 0);
        }

        ApplyMoves(writer, outerLists, keyTree, anyMulti);
        CountRowsWithSeveralKeysAgain(writer);
        _deltas.Count = 0;
    }

    /// <summary>
    /// The counts of an Indirect file: a count that goes from or to zero puts the key into the list of the atom or takes it out, one list change for the transition, whatever the number of outer entries of
    /// the key. The count of the any atom crossing two moves the key in the list of the keys with several inner entries. The outer entries are not touched.
    /// </summary>
    /// <summary>
    /// The count of a pair after the batch changed it by <paramref name="sum"/>. A file whose keys are the ids of its inner documents keeps no counts: the group of a key is its one document, so the pair is
    /// there (1) or not (0), and the batch moves it by one from the other state.
    /// </summary>
    private int CountAfter(JoinWriter writer, long keyRef, long code, int sum, ref int pairIndex)
    {
        if (writer.Schema.KeysAreInnerIds)
        {
            CheckOneDocument(sum);
            return sum > 0 ? 1 : 0;
        }

        // the estimate of the cost read the count of every pair already, and nothing changed it since
        if (_pairsBeforeKnown == false)
        {
            _pairCountLookups++;
            return writer.AddPairCount(keyRef, code, sum);
        }

        return writer.SetPairCount(keyRef, code, _pairsBefore[pairIndex++], sum);
    }

    /// <summary>The count of a pair before the batch changed it by <paramref name="sum"/>, see <see cref="CountAfter"/>.</summary>
    private static int CountBefore(JoinWriter writer, long keyRef, long code, int sum)
    {
        if (writer.Schema.KeysAreInnerIds == false)
            return writer.GetPairCount(keyRef, code);

        CheckOneDocument(sum);
        return sum < 0 ? 1 : 0;
    }

    private static void CheckOneDocument(int sum)
    {
        if (sum is not (1 or -1))
            throw new JoinRebuildRequiredException(JoinRebuildReason.Unsupported, "a group of a key that is the id of a document changed by more than one document");
    }

    private void ApplyPairDeltasToKeyLists(JoinWriter writer, Span<PairDelta> deltas)
    {
        long several = JoinReader.AtomOfCode(writer.CodeOfSeveralInner);
        long gathered = -1;
        int index = 0;
        int pairIndex = 0;
        while (index < deltas.Length)
        {
            long keyRef = deltas[index].KeyRef;
            long code = deltas[index].Code;
            int sum = 0;
            while (index < deltas.Length && deltas[index].KeyRef == keyRef && deltas[index].Code == code)
                sum += deltas[index++].Delta;

            if (sum == 0)
                continue;

            if (keyRef != gathered)
            {
                UpdateAtomsOfKey(writer, gathered);
                gathered = keyRef;
            }

            int now = CountAfter(writer, keyRef, code, sum, ref pairIndex);
            int before = now - sum;

            if (code == JoinReader.CodeOfAny() && (before >= 2) != (now >= 2))
            {
                writer.AddRowsWithSeveralJoined(now >= 2 ? 1 : -1);
                if (now >= 2)
                {
                    _keyAtomAdds.Add(writer.CodeOfSeveralInner);
                    writer.AddKeyToAtom(several, keyRef);
                }
                else
                {
                    _keyAtomRemovals.Add(writer.CodeOfSeveralInner);
                    writer.RemoveKeyFromAtom(several, keyRef);
                }
            }

            if ((before == 0) == (now == 0))
                continue;

            long atom = JoinReader.AtomOfCode(code);
            if (now > 0)
            {
                _keyAtomAdds.Add(code);
                writer.AddKeyToAtom(atom, keyRef);
            }
            else
            {
                _keyAtomRemovals.Add(code);
                writer.RemoveKeyFromAtom(atom, keyRef);
            }

            _counters.Add(ref _counters.KeyListChanges, 1);
        }

        UpdateAtomsOfKey(writer, gathered);
    }

    /// <summary>Changes the atoms of a key by everything that this refresh did to its pairs: one update of its list, not one for each atom.</summary>
    private void UpdateAtomsOfKey(JoinWriter writer, long keyRef)
    {
        if (_keyAtomAdds.Count == 0 && _keyAtomRemovals.Count == 0)
            return;

        _keyAtomAdds.ToSpan().Sort();
        _keyAtomRemovals.ToSpan().Sort();
        writer.ChangeKeyAtoms(keyRef, _keyAtomAdds.ToSpan(), _keyAtomRemovals.ToSpan());
        _keyAtomAdds.Count = 0;
        _keyAtomRemovals.Count = 0;
        _counters.Add(ref _counters.KeyAtomUpdates, 1);
    }

    /// <summary>
    /// The list changes that the counts of this refresh make: for every pair of a key and an atom whose count goes from or to zero, one change of the list of the atom for each outer entry of the key.
    /// A change that is cut at the budget of a commit has the same changes per document in the commits that follow, so the whole of what is left is estimated. When that is more changes than building
    /// the lists again costs the lists are built again, before any of them is made.
    /// </summary>
    private void CheckCostOfListChanges(JoinWriter writer, ListStore outerLists, CompactTree keyTree, Span<PairDelta> deltas)
    {
        int limit = _options.MaxInnerListChangesPerOuterEntry;
        bool indirect = writer.Schema.IsIndirect;
        _pairsBefore.Count = 0;
        _pairsBeforeKnown = false;
        if (limit <= 0 || (keyTree == null && indirect == false) || _dirtyInner.Count == 0)
            return;

        long changes = 0;
        long rowsOfKey = 0;
        long cachedKey = 0;
        Span<byte> term = stackalloc byte[JoinLimits.MaxTermLength];
        int index = 0;
        while (index < deltas.Length)
        {
            long keyRef = deltas[index].KeyRef;
            long code = deltas[index].Code;
            int sum = 0;
            while (index < deltas.Length && deltas[index].KeyRef == keyRef && deltas[index].Code == code)
                sum += deltas[index++].Delta;

            if (sum == 0)
                continue;

            int before = CountBefore(writer, keyRef, code, sum);
            if (writer.Schema.KeysAreInnerIds == false)
            {
                _pairCountLookups++;
                _pairsBefore.Add(before);
            }
            int now = before + sum;
            bool several = code == JoinReader.CodeOfAny() && (before >= 2) != (now >= 2);
            if (several == false && (before == 0) == (now == 0))
                continue;

            if (indirect)
            {
                // a transition is one change of one list, whatever the number of outer entries of the key
                rowsOfKey = 1;
            }
            else if (cachedKey != keyRef)
            {
                int length = writer.CopyKey(keyRef, term);
                rowsOfKey = keyTree.TryGetValue(term.Slice(0, length), out long handle) ? outerLists.Count(handle) : 0;
                cachedKey = keyRef;
            }

            if (several)
                changes += rowsOfKey;

            if ((before == 0) != (now == 0))
                changes += rowsOfKey;
        }

        _pairsBeforeKnown = true;

        // what is left of the change has about the same cost for each document
        long documents = _dirtyInner.Count;
        double estimate = changes * (double)(documents + _innerRemaining) / documents;
        _counters.LastListChangeEstimate = (long)estimate;
        // the memory only sizes the queue: how many changes a refresh makes does not depend on it, so neither does the limit
        double allowed = (double)limit * Math.Max(1, writer.NumberOfJoined);
        if (estimate > allowed)
            throw new JoinRebuildRequiredException(JoinRebuildReason.InnerChangesTooCostly, $"about {estimate:N0} list changes for {writer.NumberOfJoined:N0} outer entries, at most {limit} for each");
    }

    /// <summary>
    /// Adds an atom to the atoms of a key, or takes it out, and notes that the clean outer entries of the key join the list of the atom or leave it: the entries are moved when every pair is noted,
    /// atom by atom (<see cref="ApplyMoves"/>).
    /// </summary>
    private void AddMove(JoinWriter writer, ListStore outerLists, CompactTree keyTree, long keyRef, long code, bool add)
    {
        if (add)
            writer.AddKeyAtom(keyRef, code);
        else
            writer.RemoveKeyAtom(keyRef, code);

        long rows = 0;
        if (keyTree != null)
        {
            Span<byte> term = stackalloc byte[JoinLimits.MaxTermLength];
            int length = writer.CopyKey(keyRef, term);
            rows = keyTree.TryGetValue(term.Slice(0, length), out long handle) ? outerLists.Count(handle) : 0;
        }

        // an entry that holds several keys may have the atom through another key: it is decided when every count is in. A key that gets or loses its group changes what several keys with a group mean
        // for the list of the entries that are joined with several inner entries, too
        _moves.Add(new Move { Atom = JoinReader.AtomOfCode(code), KeyRef = keyRef, Rows = rows, Add = add ? 1 : 0, GroupChanged = code == JoinReader.CodeOfAny() ? 1 : 0 });
    }

    /// <summary>
    /// Moves the clean outer entries of the keys whose pairs changed. The entries of an atom that is large (the sum over its keys, see <see cref="JoinOptions.MinAtomEntriesForDirectWrite"/>) are collected,
    /// sorted once and applied by one update of its list; the entries of the others are queued. A clean entry has one key, so it is moved into or out of an atom through one key only, and what is collected
    /// for an atom cannot hold an entry in both its additions and its removals.
    /// </summary>
    private void ApplyMoves(JoinWriter writer, ListStore outerLists, CompactTree keyTree, bool anyMulti)
    {
        Span<Move> moves = _moves.ToSpan();
        if (moves.Length == 0)
            return;

        moves.Sort();
        long severalAtom = JoinReader.AtomOfCode(writer.CodeOfSeveralInner);
        int minimum = _options.MinAtomEntriesForDirectWrite;
        int index = 0;
        while (index < moves.Length)
        {
            long atom = moves[index].Atom;
            long total = 0;
            int end = index;
            while (end < moves.Length && moves[end].Atom == atom)
                total += moves[end++].Rows;

            bool direct = minimum > 0 && total >= minimum;
            _moveAdds.Count = 0;
            _moveRemovals.Count = 0;
            for (int m = index; m < end; m++)
            {
                LoadCleanRows(writer, outerLists, keyTree, moves[m].KeyRef);
                bool add = moves[m].Add != 0;
                foreach (long row in _cleanRows.ToSpan())
                {
                    if (anyMulti && _multiRows.ToSpan().BinarySearch(row) >= 0)
                    {
                        _pending.Add(new Pending { Row = row, Atom = atom });
                        if (moves[m].GroupChanged != 0)
                            _pending.Add(new Pending { Row = row, Atom = severalAtom });
                    }
                    else if (direct)
                    {
                        if (add)
                            _moveAdds.Add(row);
                        else
                            _moveRemovals.Add(row);
                    }
                    else if (add)
                        writer.AddToAtom(atom, row);
                    else
                        writer.RemoveFromAtom(atom, row);
                }
            }

            if (direct)
            {
                UpdateAtomList(writer, atom);
                _counters.Add(ref _counters.AtomListsUpdatedAtOnce, 1);
            }
            else
            {
                _counters.Add(ref _counters.AtomListsQueued, 1);
            }

            index = end;
        }

        _moves.Count = 0;
    }

    private void UpdateAtomList(JoinWriter writer, long atom)
    {
        SortDistinct(ref _moveAdds);
        SortDistinct(ref _moveRemovals);

        // an entry that is moved both ways would make the result depend on the order: the lists are built again
        ReadOnlySpan<long> removals = _moveRemovals.ToSpan();
        foreach (long row in _moveAdds.ToSpan())
        {
            if (removals.BinarySearch(row) >= 0)
                throw new JoinRebuildRequiredException(JoinRebuildReason.Unsupported, "an outer entry joins an atom and leaves it in one refresh");
        }

        _moveEncoded.Count = 0;
        _moveEncoded.GrowFitting(_moveAdds.Count + _moveRemovals.Count);
        foreach (long row in _moveAdds.ToSpan())
            _moveEncoded.AddUnsafe(ListStore.Encode(row));

        int additions = _moveEncoded.Count;
        foreach (long row in removals)
            _moveEncoded.AddUnsafe(ListStore.Encode(row));

        Span<long> encoded = _moveEncoded.ToSpan();
        writer.UpdateAtomList(atom, encoded.Slice(0, additions), encoded.Slice(additions));
    }

    /// <summary>
    /// Before the counts change: the clean entries with several keys that have a key whose group becomes empty or stops being empty, and whether they have two or more keys with an inner entry now.
    /// </summary>
    private void RememberRowsOfChangedGroups(JoinWriter writer, ListStore outerLists, CompactTree keyTree, Span<PairDelta> deltas)
    {
        _recheck.Count = 0;
        int index = 0;
        while (index < deltas.Length)
        {
            long keyRef = deltas[index].KeyRef;
            long code = deltas[index].Code;
            int sum = 0;
            while (index < deltas.Length && deltas[index].KeyRef == keyRef && deltas[index].Code == code)
                sum += deltas[index++].Delta;

            if (sum == 0 || code != JoinReader.CodeOfAny())
                continue;

            int before = writer.GetPairCount(keyRef, code);
            if ((before == 0) == (before + sum == 0))
                continue;

            LoadCleanRows(writer, outerLists, keyTree, keyRef);
            foreach (long row in _cleanRows.ToSpan())
            {
                if (_multiRows.ToSpan().BinarySearch(row) >= 0)
                    _recheck.Add(new SeveralKeysCheck { Row = row });
            }
        }

        Span<SeveralKeysCheck> rows = _recheck.ToSpan();
        rows.Sort();
        int write = 0;
        for (int read = 0; read < rows.Length; read++)
        {
            if (write > 0 && rows[write - 1].Row == rows[read].Row)
                continue;

            rows[write++] = rows[read];
        }

        _recheck.Count = write;
        for (int i = 0; i < _recheck.Count; i++)
        {
            SeveralKeysCheck check = _recheck[i];
            check.Was = HasSeveralKeysWithGroup(writer, check.Row) ? 1 : 0;
            _recheck[i] = check;
        }

        // the loop that applies the counts reads the rows of its own key
        _cleanRows.Count = 0;
    }

    /// <summary>After the counts changed: the entries of <see cref="RememberRowsOfChangedGroups"/> that now have, or no longer have, two or more keys with an inner entry.</summary>
    private void CountRowsWithSeveralKeysAgain(JoinWriter writer)
    {
        for (int i = 0; i < _recheck.Count; i++)
        {
            SeveralKeysCheck check = _recheck[i];
            int now = HasSeveralKeysWithGroup(writer, check.Row) ? 1 : 0;
            if (now != check.Was)
                writer.AddRowsWithSeveralKeys(now - check.Was);
        }

        _recheck.Count = 0;
    }

    /// <summary>Whether two or more of the keys of the entry have an inner entry in their group, as the counts are now.</summary>
    private bool HasSeveralKeysWithGroup(JoinWriter writer, long row)
    {
        _rowKeys.Count = 0;
        writer.TryGetOuterKeys(row, ref _rowKeys);
        int withGroup = 0;
        foreach (long keyRef in _rowKeys.ToSpan())
        {
            if (writer.GetPairCount(keyRef, JoinReader.CodeOfAny()) > 0 && ++withGroup == 2)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the entry with the keys among <see cref="_rowKeys"/> is joined with several inner entries: the group of one of its keys has two or more, or two or more of its keys have a group (the entries
    /// of two keys may be one entry, which is not told apart: the answer of the lists is checked then, which is exact). The counts are as they are now.
    /// </summary>
    private bool RowIsJoinedWithSeveral(JoinWriter writer)
    {
        int withGroup = 0;
        foreach (long keyRef in _rowKeys.ToSpan())
        {
            int count = writer.GetPairCount(keyRef, JoinReader.CodeOfAny());
            if (count >= 2)
                return true;

            if (count > 0)
                withGroup++;
        }

        return withGroup >= 2;
    }

    /// <summary>Whether the group of one of the keys among <see cref="_rowKeys"/> has the atom, as the counts are now.</summary>
    private bool RowHasAtomOfKeys(JoinWriter writer, long atom)
    {
        if (atom == JoinReader.AtomOfCode(writer.CodeOfSeveralInner))
            return RowIsJoinedWithSeveral(writer);

        long code = JoinReader.CodeOfAtom(atom);
        foreach (long keyRef in _rowKeys.ToSpan())
        {
            if (writer.GetPairCount(keyRef, code) > 0)
                return true;
        }

        return false;
    }

    /// <summary>The outer entries with the key, as the outer snapshot has them, without the entries that are taken out and put in again by this refresh.</summary>
    private void LoadCleanRows(JoinWriter writer, ListStore outerLists, CompactTree keyTree, long keyRef)
    {
        _cleanRows.Count = 0;
        if (keyTree == null)
            return;

        Span<byte> term = stackalloc byte[JoinLimits.MaxTermLength];
        int length = writer.CopyKey(keyRef, term);
        if (keyTree.TryGetValue(term.Slice(0, length), out long value) == false)
            return;

        _affected.Count = 0;
        outerLists.Read(value, ref _affected);
        foreach (long entry in _affected.ToSpan())
        {
            if (_dirtyRows.Count > 0 && _dirtyRows.ToSpan().BinarySearch(entry) >= 0)
                continue;

            _cleanRows.Add(entry);
        }
    }
}
