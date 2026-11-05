using System;
using System.Numerics;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>
/// The lists of the atoms of a build. The outer entries of a key (the posting list of its term in the outer index) join every atom of the key, so the list of an atom is the union of the outer
/// lists of the keys that have it. The build notes the pairs of an atom and the outer list of a key, and writes the list of a large atom once, from the union of the lists, instead of queueing
/// every entry of every key for it: a list that is changed by many small flushes is rewritten by each of them. The atoms that are smaller than the limit are queued as before.
/// </summary>
internal sealed unsafe partial class JoinBuilder
{
    private struct AtomPair : IComparable<AtomPair>
    {
        public long Atom;

        /// <summary>The handle of the list of the key in the outer index.</summary>
        public long Handle;

        public long Count;

        public int CompareTo(AtomPair other)
        {
            int result = Atom.CompareTo(other.Atom);
            return result != 0 ? result : Handle.CompareTo(other.Handle);
        }
    }

    private readonly int _minAtomEntries;
    private ContextBoundNativeList<AtomPair> _pairs;
    private ContextBoundNativeList<ulong> _bits;
    private ContextBoundNativeList<long> _pairEntries;
    private ContextBoundNativeList<long> _union;
    private ContextBoundNativeList<long> _encodedEntries;
    private bool _collecting;

    /// <summary>The atoms whose list was written at once, and the atoms that were queued entry by entry.</summary>
    public long DirectAtoms { get; private set; }

    public long QueuedAtoms { get; private set; }

    /// <summary>The pairs of an atom and a key that were noted: when they would have been more than a queue holds, the build went on with the queue alone.</summary>
    public bool PairsOverflowed { get; private set; }

    private void InitializeAtoms()
    {
        _pairs = new ContextBoundNativeList<AtomPair>(_allocator, 1024);
        _bits = new ContextBoundNativeList<ulong>(_allocator);
        _pairEntries = new ContextBoundNativeList<long>(_allocator);
        _union = new ContextBoundNativeList<long>(_allocator);
        _encodedEntries = new ContextBoundNativeList<long>(_allocator);
    }

    private void DisposeAtoms()
    {
        _pairs.Dispose();
        _bits.Dispose();
        _pairEntries.Dispose();
        _union.Dispose();
        _encodedEntries.Dispose();
    }

    /// <summary>The outer entries of a key (its list in the outer index) join the atom: noted for the end of the walk, or queued.</summary>
    private void AddAtom(long atom, long handle, ReadOnlySpan<long> outerEntries)
    {
        if (_collecting)
        {
            if (_pairs.Count < _flushThreshold)
            {
                _pairs.Add(new AtomPair { Atom = atom, Handle = handle, Count = outerEntries.Length });
                return;
            }

            // more pairs than a queue holds: the pairs so far are queued, the walk goes on with the queue alone
            PairsOverflowed = true;
            QueuePairs(_pairs.ToSpan());
            _pairs.Count = 0;
            _collecting = false;
        }

        foreach (long outer in outerEntries)
            _writer.AddToAtom(atom, outer);
    }

    /// <summary>Writes the lists of the atoms that were noted: the large ones at once, the others by the queue.</summary>
    private void WriteAtoms()
    {
        _collecting = false;
        Span<AtomPair> pairs = _pairs.ToSpan();
        if (pairs.Length == 0)
            return;

        pairs.Sort();
        int index = 0;
        while (index < pairs.Length)
        {
            long atom = pairs[index].Atom;
            long total = 0;
            int end = index;
            while (end < pairs.Length && pairs[end].Atom == atom)
                total += pairs[end++].Count;

            ReadOnlySpan<AtomPair> group = pairs.Slice(index, end - index);
            if (total >= _minAtomEntries)
            {
                WriteAtom(atom, group, total);
                DirectAtoms++;
            }
            else
            {
                QueuePairs(group);
                QueuedAtoms++;
            }

            index = end;
            ThrowIfCancelled();
        }

        _pairs.Count = 0;
    }

    private void QueuePairs(ReadOnlySpan<AtomPair> pairs)
    {
        foreach (AtomPair pair in pairs)
        {
            _pairEntries.Count = 0;
            _outerLists.Read(pair.Handle, ref _pairEntries);
            foreach (long outer in _pairEntries.ToSpan())
                _writer.AddToAtom(pair.Atom, outer);
        }
    }

    /// <summary>One list of the atom from the union of the outer lists of the keys that have it.</summary>
    private void WriteAtom(long atom, ReadOnlySpan<AtomPair> keys, long total)
    {
        int capacity = checked((int)total);
        _encodedEntries.Count = 0;
        _encodedEntries.GrowFitting(capacity);

        if (keys.Length == 1)
        {
            _pairEntries.Count = 0;
            _outerLists.Read(keys[0].Handle, ref _pairEntries);
            foreach (long entry in _pairEntries.ToSpan())
                _encodedEntries.AddUnsafe(ListStore.Encode(entry));
        }
        else if (TryUnionInBitmap(keys, total) == false)
        {
            UnionBySorting(keys, capacity);
        }

        _writer.UpdateAtomList(atom, _encodedEntries.ToSpan(), default);
    }

    /// <summary>
    /// Sets the entries of the lists in a bitmap over the ids of the outer entries, which gives them ascending and without repeats (an entry with several keys is in several lists). The bitmap is
    /// zero between two atoms. It is not used when the ids are so far apart that the bitmap is more to walk than the entries are to sort, or when an id is beyond it.
    /// </summary>
    private bool TryUnionInBitmap(ReadOnlySpan<AtomPair> keys, long total)
    {
        long words = _outer.Searcher.LastEntryId / 64 + 1;
        if (words > total * 8 || words > int.MaxValue / 2)
            return false;

        if (_bits.Capacity < words)
        {
            _bits.GrowFitting((int)words);
            _bits.ToFullCapacitySpan().Clear();
        }

        ulong* bits = _bits.RawItems;
        long low = long.MaxValue;
        long high = -1;
        bool beyond = false;
        foreach (AtomPair key in keys)
        {
            _pairEntries.Count = 0;
            _outerLists.Read(key.Handle, ref _pairEntries);
            foreach (long entry in _pairEntries.ToSpan())
            {
                long word = entry >> 6;
                if ((ulong)word >= (ulong)words)
                {
                    beyond = true;
                    continue;
                }

                bits[word] |= 1UL << (int)(entry & 63);
                if (word < low)
                    low = word;

                if (word > high)
                    high = word;
            }
        }

        // the words that were set are cleared whether they are used or not
        for (long word = low; word <= high; word++)
        {
            ulong value = bits[word];
            if (value == 0)
                continue;

            bits[word] = 0;
            if (beyond)
                continue;

            while (value != 0)
            {
                _encodedEntries.AddUnsafe(ListStore.Encode(word * 64 + BitOperations.TrailingZeroCount(value)));
                value &= value - 1;
            }
        }

        if (beyond)
            _encodedEntries.Count = 0;

        return beyond == false;
    }

    private void UnionBySorting(ReadOnlySpan<AtomPair> keys, int capacity)
    {
        _union.Count = 0;
        _union.GrowFitting(capacity);
        foreach (AtomPair key in keys)
            _outerLists.Read(key.Handle, ref _union);

        _union.Sort();
        Span<long> all = _union.ToSpan();
        long previous = -1;
        foreach (long entry in all)
        {
            if (entry == previous)
                continue;

            previous = entry;
            _encodedEntries.AddUnsafe(ListStore.Encode(entry));
        }
    }
}
