using System;
using Sparrow.Utils;

namespace Raven.Server.Documents.Indexes.Joins.Serving.Resident;

/// <summary>
/// How often each atom was read lately, for the admission of its key lists: the reads of the current and of the previous window of <c>window</c> atom reads. The table is native, holds only the atoms
/// of the last two windows, and is rebuilt when a window ends, so it never fills up.
/// </summary>
internal sealed unsafe class ResidentAtomRates : IDisposable
{
    private struct Entry
    {
        public long AtomPlusOne; // 0 = empty
        public int Current;
        public int Previous;
    }

    private readonly object _gate = new();
    private readonly int _window, _minReads, _mask;
    private Entry* _entries, _spare;
    private int _readsInWindow;

    public ResidentAtomRates(int window, int minReads)
    {
        _window = Math.Max(1, window);
        _minReads = minReads;
        int capacity = 16;
        while (capacity < _window * 8)
            capacity <<= 1;

        _mask = capacity - 1;
        _entries = Allocate(capacity);
        _spare = Allocate(capacity);
    }

    public long NativeBytes => 2L * (_mask + 1) * sizeof(Entry);

    private static Entry* Allocate(int capacity)
    {
        var entries = (Entry*)NativeMemory.AllocateMemory((long)capacity * sizeof(Entry));
        new Span<Entry>(entries, capacity).Clear();
        return entries;
    }

    private static int Slot(long atom, int mask) => (int)(((ulong)atom * 0x9E3779B97F4A7C15UL) >> 32) & mask;

    public void RecordRead(long atom)
    {
        lock (_gate)
        {
            if (_entries == null)
                return;

            int i = Slot(atom, _mask);
            while (_entries[i].AtomPlusOne != 0 && _entries[i].AtomPlusOne != atom + 1)
                i = (i + 1) & _mask;

            if (_entries[i].AtomPlusOne == 0)
                _entries[i].AtomPlusOne = atom + 1;

            _entries[i].Current++;
            if (++_readsInWindow >= _window)
                Age();
        }
    }

    public bool IsHot(long atom)
    {
        lock (_gate)
        {
            if (_entries == null)
                return false;

            int i = Slot(atom, _mask);
            while (_entries[i].AtomPlusOne != 0)
            {
                if (_entries[i].AtomPlusOne == atom + 1)
                    return _entries[i].Current + _entries[i].Previous >= _minReads;

                i = (i + 1) & _mask;
            }

            return false;
        }
    }

    private void Age()
    {
        int capacity = _mask + 1;
        new Span<Entry>(_spare, capacity).Clear();
        for (int e = 0; e < capacity; e++)
        {
            if (_entries[e].AtomPlusOne == 0 || _entries[e].Current == 0)
                continue;

            int i = Slot(_entries[e].AtomPlusOne - 1, _mask);
            while (_spare[i].AtomPlusOne != 0)
                i = (i + 1) & _mask;

            _spare[i].AtomPlusOne = _entries[e].AtomPlusOne;
            _spare[i].Previous = _entries[e].Current;
        }

        Entry* swap = _entries;
        _entries = _spare;
        _spare = swap;
        _readsInWindow = 0;
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_entries == null)
                return;

            new Span<Entry>(_entries, _mask + 1).Clear();
            _readsInWindow = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_entries == null)
                return;

            long bytes = (long)(_mask + 1) * sizeof(Entry);
            NativeMemory.Free((byte*)_entries, bytes);
            NativeMemory.Free((byte*)_spare, bytes);
            _entries = _spare = null;
        }
    }
}
