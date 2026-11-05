using System;
using System.Buffers;
using Sparrow.Utils;

namespace Raven.Server.Documents.Indexes.Joins.Serving.Resident;

/// <summary>
/// The changes of the outer rows that one refresh makes to the lists of the keys, collected in native memory while the refresh runs and handed to <see cref="ResidentKeyListCache.ApplyPatch"/> after the
/// join index transaction committed. A patch that is disposed without being applied (rollback) changes nothing. Removals are applied before additions whatever the order of the calls, so a (key, entry)
/// pair that one row loses and another row gets in the same batch ends up in the list.
/// </summary>
public sealed unsafe class ResidentPatch : IDisposable
{
    internal readonly struct Op(long keyRef, long entryId) : IComparable<Op>
    {
        public readonly long KeyRef = keyRef;
        public readonly long EntryId = entryId;

        public int CompareTo(Op other)
        {
            int result = KeyRef.CompareTo(other.KeyRef);
            return result != 0 ? result : EntryId.CompareTo(other.EntryId);
        }
    }

    private const int ManyKeys = 24;

    private OpBuffer _removes, _adds;

    /// <summary>The number of rows (documents) that were recorded.</summary>
    public int Documents { get; private set; }

    public int RemoveCount => _removes.Count;

    public int AddCount => _adds.Count;

    internal ReadOnlySpan<Op> Removes => _removes.AsSpan();

    internal ReadOnlySpan<Op> Adds => _adds.AsSpan();

    /// <summary>Sorts the changes by key and entry, so that the changes of one key are together and ascending. The order of the calls does not matter: removals are applied before additions anyway.</summary>
    internal void Sort()
    {
        _removes.AsMutableSpan().Sort();
        _adds.AsMutableSpan().Sort();
    }

    /// <summary>A new row (a document that was not in the outer index): its entry joins the list of each of its keys.</summary>
    public void AddRow(long entryId, ReadOnlySpan<long> keys)
    {
        Check(entryId);
        Documents++;
        foreach (long key in keys)
            _adds.Add(key, entryId);
    }

    /// <summary>A deleted row: its entry leaves the list of each of the keys it had.</summary>
    public void RemoveRow(long entryId, ReadOnlySpan<long> oldKeys)
    {
        Check(entryId);
        Documents++;
        foreach (long key in oldKeys)
            _removes.Add(key, entryId);
    }

    /// <summary>
    /// A document that was in the outer index and is in it after the refresh: with the same entry id only the keys that changed are touched (the list of a key the row keeps is not touched, a key it lost
    /// loses the entry, a key it got gains it); with another entry id the old entry leaves all old keys and the new one joins all new keys.
    /// </summary>
    public void ChangeRow(long oldEntryId, ReadOnlySpan<long> oldKeys, long newEntryId, ReadOnlySpan<long> newKeys)
    {
        Check(oldEntryId);
        Check(newEntryId);
        Documents++;
        if (oldEntryId != newEntryId)
        {
            foreach (long key in oldKeys)
                _removes.Add(key, oldEntryId);

            foreach (long key in newKeys)
                _adds.Add(key, newEntryId);

            return;
        }

        long[] oldSorted = null, newSorted = null;
        try
        {
            if (oldKeys.Length > ManyKeys)
                oldSorted = Sorted(oldKeys);

            if (newKeys.Length > ManyKeys)
                newSorted = Sorted(newKeys);

            foreach (long key in oldKeys)
            {
                if (Has(newKeys, newSorted, newKeys.Length, key) == false)
                    _removes.Add(key, oldEntryId);
            }

            foreach (long key in newKeys)
            {
                if (Has(oldKeys, oldSorted, oldKeys.Length, key) == false)
                    _adds.Add(key, newEntryId);
            }
        }
        finally
        {
            if (oldSorted != null)
                ArrayPool<long>.Shared.Return(oldSorted);

            if (newSorted != null)
                ArrayPool<long>.Shared.Return(newSorted);
        }
    }

    private static long[] Sorted(ReadOnlySpan<long> keys)
    {
        long[] copy = ArrayPool<long>.Shared.Rent(keys.Length);
        keys.CopyTo(copy);
        Array.Sort(copy, 0, keys.Length);
        return copy;
    }

    private static bool Has(ReadOnlySpan<long> keys, long[] sorted, int length, long key) => sorted != null ? Array.BinarySearch(sorted, 0, length, key) >= 0 : keys.Contains(key);

    private static void Check(long entryId)
    {
        if (entryId < 0)
            throw new ArgumentOutOfRangeException(nameof(entryId), entryId, "An entry id is not negative");
    }

    public void Dispose()
    {
        _removes.Dispose();
        _adds.Dispose();
    }

    private struct OpBuffer
    {
        private Op* _items;
        private int _capacity;

        public int Count { get; private set; }

        public ReadOnlySpan<Op> AsSpan() => new(_items, Count);

        public Span<Op> AsMutableSpan() => new(_items, Count);

        public void Add(long keyRef, long entryId)
        {
            if (Count == _capacity)
                Grow();

            _items[Count++] = new Op(keyRef, entryId);
        }

        private void Grow()
        {
            int capacity = _capacity == 0 ? 64 : _capacity * 2;
            var items = (Op*)NativeMemory.AllocateMemory((long)capacity * sizeof(Op));
            if (_items != null)
            {
                new ReadOnlySpan<Op>(_items, Count).CopyTo(new Span<Op>(items, capacity));
                NativeMemory.Free((byte*)_items, (long)_capacity * sizeof(Op));
            }

            _items = items;
            _capacity = capacity;
        }

        public void Dispose()
        {
            if (_items != null)
                NativeMemory.Free((byte*)_items, (long)_capacity * sizeof(Op));

            _items = null;
            _capacity = 0;
            Count = 0;
        }
    }
}
