using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Sparrow.Server;
using Voron;
using Voron.Data.CompactTrees;
using Voron.Debugging;
using Voron.Data.Containers;
using Voron.Data.Lookups;
using Voron.Impl;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Storage;

/// <summary>
/// The write side of a join index file: one Voron write transaction. Changes of lists are queued in a native buffer and applied per list, sorted,
/// when the buffer is full, before a list is read and at the commit. Nothing is visible to readers before <see cref="Commit"/>, a writer that is disposed without one changes nothing.
/// </summary>
public sealed unsafe class JoinWriter : JoinReader
{
    /// <summary>The number of queued changes at which they are applied: a queued change takes 24 bytes of native memory. A list that is rewritten by many small flushes leaves holes in the pages of the container, so a build should flush rarely.</summary>
    public const int DefaultFlushThreshold = 1024 * 1024;
    private const int QueueGrowthLimit = 256 * 1024;

    private readonly long _publishedSerial;
    private readonly ListStore _lists;
    private ContextBoundNativeList<ListOp> _ops;
    private ContextBoundNativeList<long> _additions;
    private ContextBoundNativeList<long> _removals;
    private ContextBoundNativeList<long> _keys;
    private ContextBoundNativeList<long> _encoded;
    private readonly bool[] _slotDirty;
    private long _severalDelta;
    private long _severalKeysDelta;
    private int _sequence;
    private long _queued;
    private bool _committed;
    private bool _disposed;

    public int FlushThreshold { get; set; } = DefaultFlushThreshold;

    /// <summary>How many times queued changes were applied to the lists.</summary>
    public int Flushes { get; private set; }

    /// <summary>How many lists were changed by the flushes.</summary>
    public int ListUpdates { get; private set; }

    /// <summary>Stopwatch ticks spent in the flushes, and the part of them spent in the list store.</summary>
    public long FlushTicks { get; private set; }

    public long ListStoreTicks { get; private set; }

    /// <summary>The pages this transaction has modified so far.</summary>
    public long ModifiedPages => Tx.LowLevelTransaction.NumberOfModifiedPages;

    /// <param name="tx">The write transaction that holds the structures: it belongs to the caller, who commits and disposes it (the write transaction of an index).</param>
    /// <param name="schema">The shape of the structures.</param>
    /// <param name="publishedSerial">The serial of the generation that the structures hold now; <see cref="Commit"/> writes the following one.</param>
    public JoinWriter(Transaction tx, JoinSchema schema, long publishedSerial) : base(tx, schema)
    {
        _publishedSerial = publishedSerial;
        _lists = new ListStore(tx.LowLevelTransaction, ListsContainer);
        _ops = new ContextBoundNativeList<ListOp>(tx.Allocator, 4096);
        _additions = new ContextBoundNativeList<long>(tx.Allocator, 256);
        _removals = new ContextBoundNativeList<long>(tx.Allocator, 256);
        _keys = new ContextBoundNativeList<long>(tx.Allocator, 32);
        _encoded = new ContextBoundNativeList<long>(tx.Allocator, 32);
        _slotDirty = new bool[SlotHandles.Length];
    }

    #region documents

    /// <summary>Remembers the entry of a document. Returns the entry that was remembered for the hash before, 0 when there was none.</summary>
    public long SetOuterDocument(long hash, long entryId) => Set(OuterDocuments, hash, entryId);

    public long SetInnerDocument(long hash, long entryId) => Set(InnerDocuments, hash, entryId);

    public bool RemoveOuterDocument(long hash) => Remove(OuterDocuments, hash);

    public bool RemoveInnerDocument(long hash) => Remove(InnerDocuments, hash);

    /// <summary>Remembers that the outer entry names one inner document. Returns the first key it named before, 0 when there was none.</summary>
    public long SetJoined(long outerEntryId, long innerKey)
    {
        bool had = TryGetJoined(outerEntryId, out long previous);
        Span<long> keys = [innerKey];
        SetOuterKeys(outerEntryId, keys);
        return had ? previous : 0;
    }

    public bool RemoveJoined(long outerEntryId)
    {
        bool had = TryGetJoined(outerEntryId, out _);
        SetOuterKeys(outerEntryId, default);
        return had;
    }

    /// <summary>
    /// Sets the keys of the inner documents that the key field of an outer entry names (ascending, without repeats; none removes the entry). The entries with two or more keys are kept in a list of their own.
    /// </summary>
    public void SetOuterKeys(long outerEntryId, ReadOnlySpan<long> innerKeys)
    {
        if (innerKeys.Length > JoinLimits.MaxKeysPerEntry)
            throw new ArgumentException($"An outer entry holds at most {JoinLimits.MaxKeysPerEntry} keys.", nameof(innerKeys));

        int oldCount = SetKeyList(OuterToInner, outerEntryId, innerKeys);
        if (innerKeys.Length >= 2 && oldCount < 2)
            Queue(SlotKeyBit | (long)MultiKeySlot, outerEntryId, isRemoval: false);
        else if (innerKeys.Length < 2 && oldCount >= 2)
            Queue(SlotKeyBit | (long)MultiKeySlot, outerEntryId, isRemoval: true);
    }

    /// <summary>Writes the values of a row (one shifted left, several in a container item with the lowest bit set) and returns how many it held before.</summary>
    private int SetKeyList(Lookup<Int64LookupKey> lookup, long id, ReadOnlySpan<long> values)
    {
        LowLevelTransaction llt = Tx.LowLevelTransaction;

        int oldCount = 0;
        bool had = lookup.TryGetValue(id, out long old);
        if (had)
        {
            if ((old & 1) == 0)
            {
                oldCount = 1;
            }
            else
            {
                var containerId = new ContainerEntryId(old & ~1L);
                Container.Get(llt, containerId, out Container.Item item);
                oldCount = (int)BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(item.Address, sizeof(long)));
                Container.Delete(llt, ListsContainer, containerId);
            }
        }

        if (values.Length == 0)
        {
            if (had)
                lookup.TryRemove(id);
        }
        else if (values.Length == 1)
        {
            lookup.Add(id, values[0] << 1);
        }
        else
        {
            ContainerEntryId containerId = Container.Allocate(llt, ListsContainer, sizeof(long) + values.Length * sizeof(long), out Span<byte> space);
            BinaryPrimitives.WriteInt64LittleEndian(space, values.Length);
            MemoryMarshal.AsBytes(values).CopyTo(space.Slice(sizeof(long)));
            lookup.Add(id, (long)containerId | 1);
        }

        return oldCount;
    }

    /// <summary>Adds one key to the keys of an outer entry (nothing changes when it has the key).</summary>
    public void AddOuterKey(long outerEntryId, long innerKey)
    {
        _keys.Count = 0;
        TryGetOuterKeys(outerEntryId, ref _keys);
        Span<long> keys = _keys.ToSpan();
        int at = keys.BinarySearch(innerKey);
        if (at >= 0)
            return;

        _keys.Add(0);
        keys = _keys.ToSpan();
        at = ~at;
        keys.Slice(at, keys.Length - at - 1).CopyTo(keys.Slice(at + 1));
        keys[at] = innerKey;
        SetOuterKeys(outerEntryId, keys);
    }

    /// <summary>Counts the outer entries that are joined with two or more inner documents; the number is written with the generation.</summary>
    public void AddRowsWithSeveralJoined(long delta) => _severalDelta += delta;

    private static long Set(Lookup<Int64LookupKey> lookup, long key, long value)
    {
        Int64LookupKey lookupKey = key;
        bool found = lookup.TryGetValue(ref lookupKey, out long previous);
        lookup.AddOrSetAfterGetNext(ref lookupKey, value);
        return found ? previous : 0;
    }

    private static bool Remove(Lookup<Int64LookupKey> lookup, long key) => lookup.TryRemove(key);

    #endregion

    #region field join

    /// <summary>Finds the key (the term of the key field of either side) or adds it. Returns its reference.</summary>
    public long GetOrAddKey(ReadOnlySpan<byte> term)
    {
        if (term.Length > JoinLimits.MaxTermLength)
            throw new ArgumentException($"A key is at most {JoinLimits.MaxTermLength} bytes, this one has {term.Length}.", nameof(term));

        CompactTree tree = KeyTerms;
        if (tree.TryGetValue(term, out ContainerEntryId id, out _))
            return (long)id;

        return (long)tree.Add(term, 0);
    }

    /// <summary>Sets the keys of an inner document (by its key): ascending references without repeats, none removes the row.</summary>
    public void SetInnerKeys(long innerDocumentKey, ReadOnlySpan<long> keyRefs)
    {
        if (keyRefs.Length > JoinLimits.MaxKeysPerEntry)
            throw new JoinUnsupportedException($"A document of the inner index holds {keyRefs.Length} keys in one document, a join index holds at most {JoinLimits.MaxKeysPerEntry}.");

        SetKeyList(InnerKeys, innerDocumentKey, keyRefs);
    }

    /// <summary>Adds one key to the keys of an inner document (nothing changes when it has the key).</summary>
    public void AddInnerKey(long innerDocumentKey, long keyRef)
    {
        _keys.Count = 0;
        TryGetInnerKeys(innerDocumentKey, ref _keys);
        Span<long> keys = _keys.ToSpan();
        int at = keys.BinarySearch(keyRef);
        if (at >= 0)
            return;

        _keys.Add(0);
        keys = _keys.ToSpan();
        at = ~at;
        keys.Slice(at, keys.Length - at - 1).CopyTo(keys.Slice(at + 1));
        keys[at] = keyRef;
        SetInnerKeys(innerDocumentKey, keys);
    }

    /// <summary>Counts the outer entries that hold two or more keys with an inner entry in their group; the number is written with the generation.</summary>
    public void AddRowsWithSeveralKeys(long delta) => _severalKeysDelta += delta;

    /// <summary>Changes the count of the inner entries of the group of the key that have the atom (by its code) and returns the new count; a count of 0 is not kept.</summary>
    public int AddPairCount(long keyRef, long code, int delta)
    {
        long key = PairKey(keyRef, code, out long check);
        Lookup<Int64LookupKey> counts = KeyCounts;
        int count = 0;
        if (counts.TryGetValue(key, out long value))
        {
            if ((value >> 32) != check)
                throw new JoinRebuildRequiredException(JoinRebuildReason.HashCollision, "two pairs of a key and an atom share a hash");

            count = (int)(value & uint.MaxValue);
        }

        int updated = checked(count + delta);
        if (updated < 0)
            throw new JoinRebuildRequiredException(JoinRebuildReason.Unsupported, "the count of a group went below zero");

        if (updated == 0)
        {
            if (count != 0)
                counts.TryRemove(key);
        }
        else
        {
            counts.Add(key, (check << 32) | (uint)updated);
        }

        return updated;
    }

    /// <summary>Adds an atom (by its code) to the atoms of a key. Not queued: the atoms of a key are read while the refresh goes on.</summary>
    public void AddKeyAtom(long keyRef, long code)
    {
        _encoded.Count = 0;
        _encoded.Add(ListStore.Encode(code));
        UpdateKeyAtoms(keyRef, _encoded.ToSpan(), default);
    }

    public void RemoveKeyAtom(long keyRef, long code)
    {
        _encoded.Count = 0;
        _encoded.Add(ListStore.Encode(code));
        UpdateKeyAtoms(keyRef, default, _encoded.ToSpan());
    }

    /// <summary>Sets the atoms of a key that has none yet (ascending codes without repeats).</summary>
    public void SetKeyAtoms(long keyRef, ReadOnlySpan<long> codes)
    {
        _encoded.Count = 0;
        foreach (long code in codes)
            _encoded.Add(ListStore.Encode(code));

        UpdateKeyAtoms(keyRef, _encoded.ToSpan(), default);
    }

    private void UpdateKeyAtoms(long keyRef, ReadOnlySpan<long> additions, ReadOnlySpan<long> removals)
    {
        Lookup<Int64LookupKey> handles = KeyAtomHandles;
        bool found = handles.TryGetValue(keyRef, out long handle);
        long updated = _lists.Update(found ? handle : ListStore.EmptyHandle, additions, removals);
        if (updated == ListStore.EmptyHandle)
        {
            if (found)
                handles.TryRemove(keyRef);
        }
        else if (found == false || updated != handle)
        {
            handles.Add(keyRef, updated);
        }
    }

    #endregion

    #region terms

    /// <summary>Finds the term or adds it with an empty list. Returns its reference.</summary>
    public long GetOrAddTerm(int field, ReadOnlySpan<byte> term)
    {
        if (term.Length > JoinLimits.MaxTermLength)
            throw new ArgumentException($"A term is at most {JoinLimits.MaxTermLength} bytes, this one has {term.Length}.", nameof(term));

        CompactTree tree = TermsOf(field);
        if (tree.TryGetValue(term, out ContainerEntryId id, out _))
            return (long)id;

        return (long)tree.Add(term, 0);
    }

    /// <summary>Sets the terms of an inner document (by its key) in a field. No terms removes the document from the field. Several terms are only possible for a multi-valued field.</summary>
    public void SetInnerTerms(int field, long innerKey, ReadOnlySpan<long> termRefs)
    {
        if (termRefs.Length > 1 && Schema.IsMultiValued(field) == false)
            throw new ArgumentException($"The field {field} has one term per entry.", nameof(termRefs));

        if (termRefs.Length > JoinLimits.MaxTermsPerEntry)
            throw new ArgumentException($"An entry has at most {JoinLimits.MaxTermsPerEntry} terms in a field.", nameof(termRefs));

        Lookup<Int64LookupKey> lookup = InnerTermsOf(field);
        LowLevelTransaction llt = Tx.LowLevelTransaction;

        bool had = lookup.TryGetValue(innerKey, out long old);
        if (had && (old & 1) != 0)
            Container.Delete(llt, ListsContainer, new ContainerEntryId(old & ~1L));

        if (termRefs.Length == 0)
        {
            if (had)
                lookup.TryRemove(innerKey);

            return;
        }

        if (termRefs.Length == 1)
        {
            lookup.Add(innerKey, termRefs[0]);
            return;
        }

        ContainerEntryId id = Container.Allocate(llt, ListsContainer, sizeof(long) + termRefs.Length * sizeof(long), out Span<byte> space);
        BinaryPrimitives.WriteInt64LittleEndian(space, termRefs.Length);
        MemoryMarshal.AsBytes(termRefs).CopyTo(space.Slice(sizeof(long)));
        lookup.Add(innerKey, (long)id | 1);
    }

    #endregion

    #region lists

    /// <summary>Queues the entry for a list of outer entries named by <see cref="JoinReader.AtomOfTerm"/>, <see cref="JoinReader.AtomOfAny"/> or <see cref="JoinReader.AtomOfGrouping"/>.</summary>
    public void AddToAtom(long atom, long outerEntryId) => Queue(atom, outerEntryId, isRemoval: false);

    public void RemoveFromAtom(long atom, long outerEntryId) => Queue(atom, outerEntryId, isRemoval: true);

    /// <summary>Queues the entry for the list of a term of a field.</summary>
    public void AddToTerm(int field, long termRef, long outerEntryId) => Queue(TermListKey(field, termRef), outerEntryId, isRemoval: false);

    public void RemoveFromTerm(int field, long termRef, long outerEntryId) => Queue(TermListKey(field, termRef), outerEntryId, isRemoval: true);

    /// <summary>The outer entries that have a joined entry.</summary>
    public void AddToAny(long outerEntryId) => Queue(SlotKeyBit | AnySlot, outerEntryId, isRemoval: false);

    public void RemoveFromAny(long outerEntryId) => Queue(SlotKeyBit | AnySlot, outerEntryId, isRemoval: true);

    /// <summary>The outer entries whose joined entry is in the grouping.</summary>
    public void AddToGrouping(int grouping, long outerEntryId) => Queue(SlotKeyBit | (long)GroupingListSlot(grouping), outerEntryId, isRemoval: false);

    public void RemoveFromGrouping(int grouping, long outerEntryId) => Queue(SlotKeyBit | (long)GroupingListSlot(grouping), outerEntryId, isRemoval: true);

    /// <summary>The keys of the inner documents that are in the grouping.</summary>
    public void AddGroupingMember(int grouping, long innerKey) => Queue(SlotKeyBit | (long)GroupingMembersSlot(grouping), innerKey, isRemoval: false);

    public void RemoveGroupingMember(int grouping, long innerKey) => Queue(SlotKeyBit | (long)GroupingMembersSlot(grouping), innerKey, isRemoval: true);

    /// <summary>Whether the inner document is in the grouping, as of the changes that were applied. Queued changes are applied first.</summary>
    public bool IsGroupingMember(int grouping, long innerKey)
    {
        Flush();
        long handle = SlotHandles[GroupingMembersSlot(grouping)];
        _additions.Count = 0;
        ReadList(handle, ref _additions);
        return _additions.ToSpan().BinarySearch(innerKey) >= 0;
    }

    private void Queue(long key, long entryId, bool isRemoval)
    {
        _queued++;

        // a queue that doubles leaves every smaller buffer held until the transaction ends: from a size on it grows to the size at which it is flushed
        if (_ops.Count + 1 >= _ops.Capacity && _ops.Capacity >= QueueGrowthLimit && FlushThreshold > _ops.Capacity)
            _ops.GrowFitting(FlushThreshold);

        _ops.Add(new ListOp { Key = key, Value = ListStore.Encode(entryId), Sequence = _sequence++, IsRemoval = isRemoval ? 1 : 0 });

        if (_ops.Count >= FlushThreshold)
            Flush();
    }

    /// <summary>Applies the queued changes to the lists. Of the changes of one entry in one list the last one counts.</summary>
    /// <summary>The changes of lists that this writer was asked for, flushed or not.</summary>
    public long ListChangesQueued => _queued;

    public void Flush()
    {
        if (_ops.Count == 0)
            return;

        Flushes++;
        long flushStart = Stopwatch.GetTimestamp();
        Span<ListOp> ops = _ops.ToSpan();
        ops.Sort();

        int index = 0;
        while (index < ops.Length)
        {
            long key = ops[index].Key;
            _additions.Count = 0;
            _removals.Count = 0;

            // the buffers of a long list grow once to what the list needs: a buffer that doubles leaves every smaller one held until the transaction ends
            int end = index;
            int removed = 0;
            while (end < ops.Length && ops[end].Key == key)
                removed += ops[end++].IsRemoval;

            _additions.GrowFitting(end - index - removed);
            _removals.GrowFitting(removed);

            while (index < ops.Length && ops[index].Key == key)
            {
                int last = index;
                while (last + 1 < ops.Length && ops[last + 1].Key == key && ops[last + 1].Value == ops[index].Value)
                    last++;

                if (ops[last].IsRemoval == 0)
                    _additions.Add(ops[last].Value);
                else
                    _removals.Add(ops[last].Value);

                index = last + 1;
            }

            Apply(key, _additions.ToSpan(), _removals.ToSpan());
        }

        _ops.Count = 0;
        _sequence = 0;
        FlushTicks += Stopwatch.GetTimestamp() - flushStart;
    }

    /// <summary>
    /// Changes the list of an atom at once, without queueing: the entries are encoded (<see cref="ListStore.Encode"/>), ascending and without repeats, and no entry is in both spans. The list is created by one
    /// update when the atom has none. Changes that are queued for the atom are applied after these, which is the same as before them when they are about other entries.
    /// </summary>
    public void UpdateAtomList(long atom, ReadOnlySpan<long> additions, ReadOnlySpan<long> removals)
    {
        if (additions.Length > 0 || removals.Length > 0)
            Apply(atom, additions, removals);
    }

    private void Apply(long key, ReadOnlySpan<long> additions, ReadOnlySpan<long> removals)
    {
        ListUpdates++;
        if ((key & SlotKeyBit) != 0)
        {
            int slot = (int)(key & ~SlotKeyBit);
            long slotStart = Stopwatch.GetTimestamp();
            long updated = _lists.Update(SlotHandles[slot], additions, removals);
            ListStoreTicks += Stopwatch.GetTimestamp() - slotStart;
            if (updated != SlotHandles[slot])
            {
                SlotHandles[slot] = updated;
                _slotDirty[slot] = true;
            }

            return;
        }

        int field = (int)(key >> 56);
        long termRef = key & ((1L << 56) - 1);

        Lookup<Int64LookupKey> handles = ListHandlesOf(field);
        Int64LookupKey lookupKey = termRef;
        bool found = handles.TryGetValue(ref lookupKey, out long handle);
        long storeStart = Stopwatch.GetTimestamp();
        long newHandle = _lists.Update(found ? handle : ListStore.EmptyHandle, additions, removals);
        ListStoreTicks += Stopwatch.GetTimestamp() - storeStart;

        if (newHandle == handle && found)
            return;

        if (newHandle == ListStore.EmptyHandle)
        {
            if (found)
                handles.TryRemove(termRef);

            return;
        }

        handles.Add(termRef, newHandle);
    }

    #endregion

    /// <summary>
    /// Applies the queued changes and writes the generation they reflect in the same transaction. The owner of the transaction commits it and makes the returned generation known to the readers.
    /// </summary>
    public JoinIndexGeneration Commit(JoinIndexGeneration generation)
    {
        if (_committed)
            throw new InvalidOperationException("The writer was committed already.");

        Flush();

        var tree = Tx.CreateTree(JoinNames.Meta);
        for (int slot = 0; slot < SlotHandles.Length; slot++)
        {
            if (_slotDirty[slot])
                tree.Add(JoinNames.Slots[slot], SlotHandles[slot]);
        }

        if (_severalDelta != 0)
        {
            long several = tree.TryRead(JoinNames.SeveralRow, out ValueReader reader) ? *(long*)reader.Base : 0;
            tree.Add(JoinNames.SeveralRow, several + _severalDelta);
            _severalDelta = 0;
        }

        if (_severalKeysDelta != 0)
        {
            long several = tree.TryRead(JoinNames.SeveralKeysRow, out ValueReader reader) ? *(long*)reader.Base : 0;
            tree.Add(JoinNames.SeveralKeysRow, several + _severalKeysDelta);
            _severalKeysDelta = 0;
        }

        JoinIndexGeneration published = generation.WithSerial(_publishedSerial + 1, DateTime.UtcNow.Ticks);
        tree.Add(JoinNames.GenerationRow, published.Serialize());

        _committed = true;
        return published;
    }

    public override void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lists.Dispose();
        _ops.Dispose();
        _additions.Dispose();
        _removals.Dispose();
        _keys.Dispose();
        _encoded.Dispose();
        base.Dispose();
    }
}
