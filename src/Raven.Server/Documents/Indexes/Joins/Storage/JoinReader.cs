using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using Corax.Indexing;
using Corax.Utils;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Sparrow;
using Sparrow.Server;
using Voron;
using Voron.Data.BTrees;
using Voron.Data.CompactTrees;
using Voron.Data.Containers;
using Voron.Data.Lookups;
using Voron.Data.PostingLists;
using Voron.Impl;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Storage;

/// <summary>The read side of the structures of a join index file within one Voron transaction.</summary>
public abstract unsafe class JoinReader : IDisposable
{
    protected const long SlotKeyBit = 1L << 62;

    private const int FieldShift = 56;

    private Lookup<Int64LookupKey> _outerDocuments;
    private Lookup<Int64LookupKey> _innerDocuments;
    private Lookup<Int64LookupKey> _outerToInner;
    private Lookup<Int64LookupKey> _outerFingerprints;
    private CompactTree[] _terms;
    private Lookup<Int64LookupKey>[] _innerTerms;
    private Lookup<Int64LookupKey>[] _listHandles;
    private CompactTree _keyTerms;
    private Lookup<Int64LookupKey> _innerKeys;
    private Lookup<Int64LookupKey> _keyAtoms;
    private Lookup<Int64LookupKey> _keyCounts;
    private long? _pairSeed;
    private ListStore _listReader;
    private ContainerId? _listsContainer;
    private long[] _slotHandles;

    protected readonly Transaction Tx;

    public JoinSchema Schema { get; }

    public Transaction Transaction => Tx;

    protected JoinReader(Transaction tx, JoinSchema schema)
    {
        Tx = tx;
        Schema = schema;
    }

    protected ContainerId ListsContainer => _listsContainer ??= Tx.OpenContainer(JoinNames.Lists);

    /// <summary>The handles of the lists that have no term: the any list, the lists of the groupings and the members of the groupings.</summary>
    protected long[] SlotHandles => _slotHandles ?? LoadSlots();

    public static int SlotCount(JoinSchema schema) => 2 + 2 * schema.GroupingCount + (schema.IsFieldJoin ? 1 : 0);

    public const int AnySlot = 0;

    public static int GroupingListSlot(int grouping) => 1 + grouping;

    public int GroupingMembersSlot(int grouping) => 1 + Schema.GroupingCount + grouping;

    /// <summary>The outer entries that hold two or more keys.</summary>
    public int MultiKeySlot => 1 + 2 * Schema.GroupingCount;

    /// <summary>
    /// The outer entries whose key has two or more inner entries (a join on a field only). The other entries are joined with one inner entry, for which the lists of the atoms are exact
    /// under an AND and a NOT; the entries of this list are checked entry by entry.
    /// </summary>
    public int SeveralInnerSlot => 2 + 2 * Schema.GroupingCount;

    /// <summary>The key of the list of a term of a declared field, as the queue of changes of a writer names it.</summary>
    public static long AtomOfTerm(int field, long termRef) => TermListKey(field, termRef);

    public static long AtomOfAny() => SlotKeyBit | AnySlot;

    public static long AtomOfGrouping(int grouping) => SlotKeyBit | (long)GroupingListSlot(grouping);

    /// <summary>The atom of the list of <see cref="SeveralInnerSlot"/>: in an Indirect file the keys whose group has two or more inner entries.</summary>
    public long AtomOfSeveralInner => SlotKeyBit | (long)SeveralInnerSlot;

    public static bool IsTermAtom(long atom) => (atom & SlotKeyBit) == 0;

    public static int FieldOfAtom(long atom) => (int)(atom >> FieldShift);

    public static long TermOfAtom(long atom) => atom & ((1L << FieldShift) - 1);

    public static int SlotOfAtom(long atom) => (int)(atom & ~SlotKeyBit);

    private const int CodeShift = 48;

    private const long CodeMask = (1L << CodeShift) - 1;

    /// <summary>The code of an atom as the lists of the atoms of a key and the counts of a key hold it: 52 bits (a value of a list has 53), the field (or 8 for a slot) above the reference of the term (or the slot).</summary>
    public static long CodeOfTerm(int field, long termRef)
    {
        if ((termRef & ~CodeMask) != 0)
            throw new JoinUnsupportedException("The terms of the join index file are too many to count the groups of a join on a field.");

        return ((long)field << CodeShift) | termRef;
    }

    public static long CodeOfAny() => (8L << CodeShift) | AnySlot;

    /// <summary>
    /// The code of the list of <see cref="SeveralInnerSlot"/> among the atoms of a key. It is in the atoms of a key whose group has two or more entries and has no count: it is the largest code, so it ends the list.
    /// </summary>
    public long CodeOfSeveralInner => (8L << CodeShift) | (long)SeveralInnerSlot;

    public static long CodeOfGrouping(int grouping) => (8L << CodeShift) | (long)GroupingListSlot(grouping);

    /// <summary>The code of an atom (the key of the queue of a writer), the inverse of <see cref="AtomOfCode"/>.</summary>
    public static long CodeOfAtom(long atom) => IsTermAtom(atom) ? CodeOfTerm(FieldOfAtom(atom), TermOfAtom(atom)) : (8L << CodeShift) | (long)SlotOfAtom(atom);

    /// <summary>The atom (the key of the queue of a writer, see <see cref="AtomOfTerm"/>) of a code.</summary>
    public static long AtomOfCode(long code)
    {
        int tag = (int)(code >> CodeShift);
        return tag == 8 ? SlotKeyBit | (code & CodeMask) : TermListKey(tag, code & CodeMask);
    }

    /// <summary>The most a key reference can be in an Indirect file: the atoms of a key are coded with 48 bits for the reference of a term, and a key is addressed the same way.</summary>
    public const long MaxKeyRef = CodeMask;

    /// <summary>Refuses a key reference that the lists of an Indirect file cannot hold.</summary>
    public static void CheckKeyRef(long keyRef)
    {
        if ((ulong)keyRef > (ulong)MaxKeyRef)
            throw new JoinUnsupportedException("The keys of the join index file are too many.");
    }

    private long[] LoadSlots()
    {
        var handles = new long[SlotCount(Schema)];
        Tree tree = Tx.ReadTree(JoinNames.Meta);
        for (int slot = 0; slot < handles.Length; slot++)
        {
            if (tree.TryRead(JoinNames.Slots[slot], out ValueReader reader))
                handles[slot] = *(long*)reader.Base;
        }

        return _slotHandles = handles;
    }

    public long SlotHandle(int slot) => SlotHandles[slot];

    protected static long TermListKey(int field, long termRef) => ((long)field << FieldShift) | termRef;

    public bool TryGetOuterDocument(long hash, out long entryId) => OuterDocuments.TryGetValue(hash, out entryId);

    public bool TryGetInnerDocument(long hash, out long entryId) => InnerDocuments.TryGetValue(hash, out entryId);

    /// <summary>The first key of the inner document (see JoinStorage.InnerKeyOf) that the outer entry names, for an entry that names one.</summary>
    public bool TryGetJoined(long outerEntryId, out long innerKey)
    {
        if (OuterToInner.TryGetValue(outerEntryId, out long value) == false)
        {
            innerKey = 0;
            return false;
        }

        if ((value & 1) == 0)
        {
            innerKey = value >> 1;
            return true;
        }

        Container.Get(Tx.LowLevelTransaction, new ContainerEntryId(value & ~1L), out Container.Item item);
        innerKey = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(item.Address + sizeof(long), sizeof(long)));
        return true;
    }

    /// <summary>Appends the keys of the inner documents that the key field of the outer entry names, ascending and without repeats, whether the documents exist or not.</summary>
    public bool TryGetOuterKeys(long outerEntryId, ref ContextBoundNativeList<long> keys) => TryReadKeyList(OuterToInner, outerEntryId, ref keys);

    /// <summary>
    /// The fingerprint of the keys of an outer entry of an Indirect file that joins on a field (see <see cref="JoinWriter.SetOuterKeys"/>). False for an entry without keys and for one whose fingerprint
    /// is not known (a row that the build wrote in two parts).
    /// </summary>
    public bool TryGetOuterKeysFingerprint(long outerEntryId, out long fingerprint) => OuterFingerprints.TryGetValue(outerEntryId, out fingerprint);

    /// <summary>Appends the values of a row of a lookup that holds one value shifted left or the container item of several, see <see cref="JoinStorageFormat.OuterToInner"/>.</summary>
    private bool TryReadKeyList(Lookup<Int64LookupKey> lookup, long id, ref ContextBoundNativeList<long> keys)
    {
        if (lookup.TryGetValue(id, out long value) == false)
            return false;

        if ((value & 1) == 0)
        {
            keys.Add(value >> 1);
            return true;
        }

        Container.Get(Tx.LowLevelTransaction, new ContainerEntryId(value & ~1L), out Container.Item item);
        int count = (int)BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(item.Address, sizeof(long)));
        keys.AddRange(new ReadOnlySpan<long>(item.Address + sizeof(long), count));
        return true;
    }

    /// <summary>The number of outer entries that are joined with two or more inner documents.</summary>
    public long RowsWithSeveralJoined
    {
        get
        {
            Tree meta = Tx.ReadTree(JoinNames.Meta);
            return meta != null && meta.TryRead(JoinNames.SeveralRow, out ValueReader reader) ? *(long*)reader.Base : 0;
        }
    }

    public long NumberOfOuterDocuments => OuterDocuments.NumberOfEntries;

    public long NumberOfInnerDocuments => InnerDocuments.NumberOfEntries;

    public long NumberOfJoined => OuterToInner.NumberOfEntries;

    /// <summary>The number of outer entries whose fingerprint the file keeps (see <see cref="TryGetOuterKeysFingerprint"/>).</summary>
    public long NumberOfOuterFingerprints => Schema.HasOuterFingerprints ? OuterFingerprints.NumberOfEntries : 0;

    public long NumberOfTerms(int field) => TermsOf(field).NumberOfEntries;

    /// <summary>The number of terms of the field that have a list. A term that lost its last entry stays in the dictionary but has no list.</summary>
    public long NumberOfTermsWithList(int field) => ListHandlesOf(field).NumberOfEntries;

    public long NumberOfInnerEntriesWithTerms(int field) => InnerTermsOf(field).NumberOfEntries;

    protected CompactTree TermsOf(int field)
    {
        _terms ??= new CompactTree[Schema.FieldCount];
        return _terms[field] ??= Tx.CompactTreeFor(JoinNames.Terms[field]);
    }

    protected Lookup<Int64LookupKey> InnerTermsOf(int field)
    {
        _innerTerms ??= new Lookup<Int64LookupKey>[Schema.FieldCount];
        return _innerTerms[field] ??= Tx.LookupFor<Int64LookupKey>(JoinNames.InnerTerms[field]);
    }

    protected Lookup<Int64LookupKey> ListHandlesOf(int field)
    {
        _listHandles ??= new Lookup<Int64LookupKey>[Schema.FieldCount];
        return _listHandles[field] ??= Tx.LookupFor<Int64LookupKey>(JoinNames.ListHandles[field]);
    }

    protected Lookup<Int64LookupKey> OuterDocuments => _outerDocuments ??= Tx.LookupFor<Int64LookupKey>(JoinNames.OuterDocuments);

    protected Lookup<Int64LookupKey> InnerDocuments => _innerDocuments ??= Tx.LookupFor<Int64LookupKey>(JoinNames.InnerDocuments);

    protected Lookup<Int64LookupKey> OuterToInner => _outerToInner ??= Tx.LookupFor<Int64LookupKey>(JoinNames.OuterToInner);

    protected Lookup<Int64LookupKey> OuterFingerprints => _outerFingerprints ??= Tx.LookupFor<Int64LookupKey>(JoinNames.OuterFingerprints);

    #region field join

    protected CompactTree KeyTerms => _keyTerms ??= Tx.CompactTreeFor(JoinNames.KeyTerms);

    protected Lookup<Int64LookupKey> InnerKeys => _innerKeys ??= Tx.LookupFor<Int64LookupKey>(JoinNames.InnerKeys);

    protected Lookup<Int64LookupKey> KeyAtomHandles => _keyAtoms ??= Tx.LookupFor<Int64LookupKey>(JoinNames.KeyAtoms);

    protected Lookup<Int64LookupKey> KeyCounts => _keyCounts ??= Tx.LookupFor<Int64LookupKey>(JoinNames.KeyCounts);

    /// <summary>The seed that keys the hash of the pairs of the counts.</summary>
    protected long PairSeed => _pairSeed ??= JoinFileRows.ReadInt64(Tx.ReadTree(JoinNames.Meta), JoinNames.SeedRow);

    /// <summary>Finds a key of a join on a field. The reference is stable as long as the file lives.</summary>
    public bool TryGetKey(ReadOnlySpan<byte> term, out long keyRef)
    {
        if (KeyTerms.TryGetValue(term, out ContainerEntryId id, out _) == false)
        {
            keyRef = 0;
            return false;
        }

        keyRef = (long)id;
        return true;
    }

    /// <summary>Copies the bytes of a key and returns their number.</summary>
    public int CopyKey(long keyRef, Span<byte> destination)
    {
        if (KeyTerms.TryGetValue(new ContainerEntryId(keyRef), out _, out ReadOnlySpan<byte> term) == false)
            throw new InvalidOperationException($"The key {keyRef} is not a key of the join index file.");

        term.CopyTo(destination);
        return term.Length;
    }

    /// <summary>Appends the references of the keys of the inner document (by its key), ascending and without repeats.</summary>
    public bool TryGetInnerKeys(long innerDocumentKey, ref ContextBoundNativeList<long> keyRefs) => TryReadKeyList(InnerKeys, innerDocumentKey, ref keyRefs);

    public long NumberOfKeys => KeyTerms.NumberOfEntries;

    public long NumberOfInnerDocumentsWithKey => InnerKeys.NumberOfEntries;

    /// <summary>The number of outer entries that hold two or more keys whose group has an inner entry (a join on a field).</summary>
    public long RowsWithSeveralKeys
    {
        get
        {
            Tree meta = Tx.ReadTree(JoinNames.Meta);
            return meta != null && meta.TryRead(JoinNames.SeveralKeysRow, out ValueReader reader) ? *(long*)reader.Base : 0;
        }
    }

    /// <summary>The number of pairs of a key and an atom that have a count.</summary>
    public long NumberOfPairCounts => KeyCounts.NumberOfEntries;

    /// <summary>Appends the codes of the atoms that the inner entries of the group of the key have, ascending.</summary>
    public void ReadKeyAtoms(long keyRef, ref ContextBoundNativeList<long> codes)
    {
        if (KeyAtomHandles.TryGetValue(keyRef, out long handle) == false)
            return;

        Lists.Read(handle, ref codes);
    }

    /// <summary>Whether the key has a group: some inner entry has it. A file whose keys are the ids of its inner documents has a group for the key of every document that has atoms.</summary>
    public bool KeyHasGroup(long keyRef)
    {
        if (Schema.KeysAreInnerIds)
            return KeyAtomHandles.TryGetValue(keyRef, out _);

        return GetPairCount(keyRef, CodeOfAny()) > 0;
    }

    /// <summary>The number of the inner entries of the group of the key that have the atom (by its code), 0 when none has. A file whose keys are the ids of its inner documents keeps no counts.</summary>
    public int GetPairCount(long keyRef, long code)
    {
        if (Schema.KeysAreInnerIds)
            throw new InvalidOperationException("A file whose keys are the ids of its inner documents keeps no counts of pairs of a key and an atom: a group is one document.");

        long key = PairKey(keyRef, code, out long check);
        if (KeyCounts.TryGetValue(key, out long value) == false)
            return 0;

        if ((value >> 32) != check)
            throw new JoinRebuildRequiredException(JoinRebuildReason.HashCollision, "two pairs of a key and an atom share a hash");

        return (int)(value & uint.MaxValue);
    }

    protected long PairKey(long keyRef, long code, out long check)
    {
        Span<byte> bytes = stackalloc byte[2 * sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, keyRef);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.Slice(sizeof(long)), code);
        ulong seed = (ulong)PairSeed;
        check = (long)(Hashing.XXHash64.CalculateInline(bytes, ~seed) & 0x7FFFFFFF);
        return (long)Hashing.XXHash64.CalculateInline(bytes, seed);
    }

    #endregion

    /// <summary>Finds a term of a field. The reference is stable for the term as long as the file lives and identifies the term in the lists and in the terms of the inner entries.</summary>
    public bool TryGetTerm(int field, ReadOnlySpan<byte> term, out long termRef, out long listHandle)
    {
        if (TermsOf(field).TryGetValue(term, out ContainerEntryId id, out _) == false)
        {
            termRef = 0;
            listHandle = ListStore.EmptyHandle;
            return false;
        }

        termRef = (long)id;
        listHandle = GetListHandle(field, termRef);
        return true;
    }

    public bool TryGetTermList(int field, ReadOnlySpan<byte> term, out long listHandle) => TryGetTerm(field, term, out _, out listHandle);

    /// <summary>The handle of the list of a term, EmptyHandle for a term without entries.</summary>
    public long GetListHandle(int field, long termRef) => ListHandlesOf(field).TryGetValue(termRef, out long handle) ? handle : ListStore.EmptyHandle;

    /// <summary>Copies the bytes of a term and returns their number.</summary>
    public int CopyTerm(int field, long termRef, Span<byte> destination)
    {
        if (TermsOf(field).TryGetValue(new ContainerEntryId(termRef), out _, out ReadOnlySpan<byte> term) == false)
            throw new InvalidOperationException($"The term {termRef} is not a term of the field {field}.");

        term.CopyTo(destination);
        return term.Length;
    }

    /// <summary>Appends the references of the terms that the inner document has in the field.</summary>
    public bool TryGetInnerTerms(int field, long innerKey, ref ContextBoundNativeList<long> termRefs)
    {
        if (InnerTermsOf(field).TryGetValue(innerKey, out long value) == false)
            return false;

        if ((value & 1) == 0)
        {
            termRefs.Add(value);
            return true;
        }

        Container.Get(Tx.LowLevelTransaction, new ContainerEntryId(value & ~1L), out Container.Item item);
        int count = (int)BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(item.Address, sizeof(long)));
        var refs = new ReadOnlySpan<long>(item.Address + sizeof(long), count);
        termRefs.AddRange(refs);
        return true;
    }

    private ListStore Lists => _listReader ??= new ListStore(Tx.LowLevelTransaction, ListsContainer);

    /// <summary>Appends the entry ids of a list, ascending.</summary>
    public void ReadList(long handle, ref ContextBoundNativeList<long> output) => Lists.Read(handle, ref output);

    /// <summary>The handle of the list of an atom (see <see cref="AtomOfTerm"/>), <see cref="ListStore.EmptyHandle"/> when it has none.</summary>
    public long AtomHandle(long atom) => IsTermAtom(atom) ? GetListHandle(FieldOfAtom(atom), TermOfAtom(atom)) : SlotHandles[SlotOfAtom(atom)];

    /// <summary>Appends the references of the keys in the list of an atom, ascending (an Indirect file). Changes that a writer has queued are not in the list until it flushes.</summary>
    public void ReadAtomKeys(long atom, ref ContextBoundNativeList<long> keyRefs)
    {
        if (Schema.IsIndirect == false)
            throw new InvalidOperationException("The atom lists of an Expanded file hold outer entries, not keys.");

        Lists.Read(AtomHandle(atom), ref keyRefs);
    }

    /// <summary>The number of the keys in the list of an atom (an Indirect file).</summary>
    public long CountOfAtomKeys(long atom)
    {
        if (Schema.IsIndirect == false)
            throw new InvalidOperationException("The atom lists of an Expanded file hold outer entries, not keys.");

        return CountOf(AtomHandle(atom));
    }

    public long CountOf(long handle) => ListStore.Count(Tx.LowLevelTransaction, handle);

    /// <summary>Opens a large list for the matches of Corax. The handle must be of a large list.</summary>
    public PostingList OpenPostingList(long handle)
    {
        if (ListStore.KindOf(handle) != TermIdMask.PostingList)
            throw new ArgumentException("The list is not a posting list.", nameof(handle));

        Container.Get(Tx.LowLevelTransaction, EntryIdEncodings.GetContainerId(handle), out Container.Item item);
        return new PostingList(Tx.LowLevelTransaction, Slices.Empty, *(PostingListState*)item.Address);
    }

    /// <summary>The pages (of 8 KB) that each structure uses, by the name of the structure. The pages of the container hold all lists, including the free ones.</summary>
    public Dictionary<string, long> GetPagesByStructure()
    {
        var pages = new Dictionary<string, long>
        {
            [JoinStorageFormat.OuterDocuments] = OuterDocuments.State.PageCount,
            [JoinStorageFormat.InnerDocuments] = InnerDocuments.State.PageCount,
            [JoinStorageFormat.OuterToInner] = OuterToInner.State.PageCount
        };

        if (Schema.HasOuterFingerprints)
            pages[JoinStorageFormat.OuterFingerprints] = OuterFingerprints.State.PageCount;

        for (int field = 0; field < Schema.FieldCount; field++)
        {
            pages[JoinStorageFormat.Terms(field)] = TermsOf(field).LeafPages + TermsOf(field).BranchPages;
            pages[JoinStorageFormat.ListHandles(field)] = ListHandlesOf(field).State.PageCount;
            pages[JoinStorageFormat.InnerTerms(field)] = InnerTermsOf(field).State.PageCount;
        }

        if (Schema.IsFieldJoin)
        {
            pages[JoinStorageFormat.KeyTerms] = KeyTerms.LeafPages + KeyTerms.BranchPages;
            pages[JoinStorageFormat.InnerKeys] = InnerKeys.State.PageCount;
            pages[JoinStorageFormat.KeyAtoms] = KeyAtomHandles.State.PageCount;
            pages[JoinStorageFormat.KeyCounts] = KeyCounts.State.PageCount;
        }

        (Lookup<Int64LookupKey> all, Lookup<Int64LookupKey> free) = Container.GetPagesFor(Tx.LowLevelTransaction, ListsContainer);
        pages[JoinStorageFormat.Lists] = all.NumberOfEntries;
        pages[JoinStorageFormat.Lists + " (free)"] = free.NumberOfEntries;
        return pages;
    }

    public virtual void Dispose()
    {
        _listReader?.Dispose();
    }
}
