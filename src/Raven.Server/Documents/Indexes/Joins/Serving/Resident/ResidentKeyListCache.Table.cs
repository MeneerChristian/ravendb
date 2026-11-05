using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using SparrowMemory = Sparrow.Utils.NativeMemory;

namespace Raven.Server.Documents.Indexes.Joins.Serving.Resident;

// The table of slots, the hash table of key references and the forms of a list. Everything here runs under the exclusive lock except Find and the reads of a list under the shared one.
public sealed unsafe partial class ResidentKeyListCache
{
    private Slot* _slots;
    private int _slotCapacity, _slotHigh, _freeHead = -1, _hand;
    private int* _table;
    private int _tableMask, _used, _tombstones;

    private void InitStructures()
    {
        _slotCapacity = InitialSlots;
        _slots = (Slot*)SparrowMemory.AllocateMemory((long)_slotCapacity * sizeof(Slot));
        _slotHigh = 0;
        _freeHead = -1;
        _hand = 0;
        _used = 0;
        _tombstones = 0;
        _tableMask = InitialTable - 1;
        _table = AllocateTable(InitialTable);
        _structBytes = (long)_slotCapacity * sizeof(Slot) + (long)InitialTable * sizeof(int);
    }

    private void FreeStructures()
    {
        SparrowMemory.Free((byte*)_slots, (long)_slotCapacity * sizeof(Slot));
        SparrowMemory.Free((byte*)_table, (long)(_tableMask + 1) * sizeof(int));
        _slots = null;
        _table = null;
        _structBytes = 0;
    }

    private static int* AllocateTable(int capacity)
    {
        var table = (int*)SparrowMemory.AllocateMemory((long)capacity * sizeof(int));
        new Span<int>(table, capacity).Fill(-1);
        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HashOf(long keyRef) => (int)(((ulong)keyRef * 0x9E3779B97F4A7C15UL) >> 32);

    private int Find(long keyRef)
    {
        int i = HashOf(keyRef) & _tableMask;
        while (true)
        {
            int s = _table[i];
            if (s == -1)
                return -1;

            if (s >= 0 && _slots[s].KeyRef == keyRef)
                return s;

            i = (i + 1) & _tableMask;
        }
    }

    // Allocates what one more list needs before anything else changes, so that a failed allocation leaves the cache as it was.
    private void EnsureRoomForOneMore()
    {
        if (_freeHead < 0 && _slotHigh == _slotCapacity)
        {
            int grown = _slotCapacity * 2;
            var bigger = (Slot*)SparrowMemory.AllocateMemory((long)grown * sizeof(Slot));
            new ReadOnlySpan<Slot>(_slots, _slotHigh).CopyTo(new Span<Slot>(bigger, grown));
            SparrowMemory.Free((byte*)_slots, (long)_slotCapacity * sizeof(Slot));
            _structBytes += (long)(grown - _slotCapacity) * sizeof(Slot);
            _slots = bigger;
            _slotCapacity = grown;
        }

        if ((_used + _tombstones + 1) * 4 > (_tableMask + 1) * 3)
        {
            int capacity = InitialTable;
            while (capacity < (_used + 1) * 3)
                capacity <<= 1;

            int* table = AllocateTable(capacity);
            for (int s = 0; s < _slotHigh; s++)
            {
                if (_slots[s].InUse == 0)
                    continue;

                int i = HashOf(_slots[s].KeyRef) & (capacity - 1);
                while (table[i] >= 0)
                    i = (i + 1) & (capacity - 1);

                table[i] = s;
            }

            SparrowMemory.Free((byte*)_table, (long)(_tableMask + 1) * sizeof(int));
            _structBytes += (long)(capacity - (_tableMask + 1)) * sizeof(int);
            _table = table;
            _tableMask = capacity - 1;
            _tombstones = 0;
        }
    }

    private int TakeSlot()
    {
        if (_freeHead >= 0)
        {
            int slot = _freeHead;
            _freeHead = _slots[slot].NextFree;
            return slot;
        }

        return _slotHigh++;
    }

    private void TableInsert(long keyRef, int slot)
    {
        int i = HashOf(keyRef) & _tableMask;
        while (_table[i] >= 0)
            i = (i + 1) & _tableMask;

        if (_table[i] == -2)
            _tombstones--;

        _table[i] = slot;
    }

    private void TableRemove(long keyRef, int slot)
    {
        int i = HashOf(keyRef) & _tableMask;
        while (_table[i] != slot)
            i = (i + 1) & _tableMask;

        _table[i] = -2;
        _tombstones++;
    }

    private static long DataBytes(int form, int capacity) => form == 0 ? (long)capacity * sizeof(uint) : (long)capacity * sizeof(ulong);

    // The form and the capacity of a list that is inserted: the bitmap only when it is smaller than the array, the array with a share of slack for the patches.
    private static void ChooseForm(ReadOnlySpan<long> ids, out bool bitmap, out int capacity)
    {
        int n = ids.Length;
        long words = (n > 0 ? ids[n - 1] >> 6 : 0) + 1;
        bitmap = n >= BitmapMinIds && words * sizeof(ulong) < (long)n * sizeof(uint);
        capacity = bitmap ? (int)words : n + n / 8 + 2;
    }

    private void AdjustBytes(long delta, bool registryCharged = false)
    {
        _bytes += delta;
        if (_bytes > _peakBytes)
            _peakBytes = _bytes;

        if (registryCharged == false)
            _registry?.Charge(delta);
    }

    private bool FitsRegistry(long need) => _registry == null || _registry.ChargedBytes + need <= _registry.BudgetBytes;

    private ResidentInsertResult InsertLocked(long keyRef, ReadOnlySpan<long> ids, bool noEvict)
    {
        if (_budget <= 0 || (_registry?.IsLowMemory ?? false))
            return ResidentInsertResult.Disabled;

        if (Find(keyRef) >= 0)
            return ResidentInsertResult.AlreadyResident;

        Debug.Assert(IsAscending(ids), "The ids of a list are ascending and unique");
        if (ids.Length > 0 && ids[^1] > uint.MaxValue)
        {
            _idTooLarge++;
            return ResidentInsertResult.IdTooLarge;
        }

        ChooseForm(ids, out bool bitmap, out int capacity);
        long need = DataBytes(bitmap ? 1 : 0, capacity) + ListOverheadBytes;
        if (need > _budget || (_registry != null && need > _registry.BudgetBytes))
        {
            _rejected++;
            return ResidentInsertResult.TooLarge;
        }

        if (noEvict && (_bytes + need > _budget || FitsRegistry(need) == false))
        {
            _rejected++;
            return ResidentInsertResult.NoRoom;
        }

        while (_bytes + need > _budget)
        {
            if (EvictOne(-1) == false)
            {
                _rejected++;
                return ResidentInsertResult.NoRoom;
            }
        }

        while (_registry != null && _registry.TryCharge(need) == false)
        {
            if (EvictOne(-1) == false)
            {
                _rejected++;
                return ResidentInsertResult.NoRoom;
            }
        }

        // the registry has been charged: undo it when the allocations fail
        byte* data = null;
        try
        {
            EnsureRoomForOneMore();
            data = SparrowMemory.AllocateMemory(DataBytes(bitmap ? 1 : 0, capacity));
        }
        catch
        {
            _registry?.Release(need);
            throw;
        }

        int slot = TakeSlot();
        ref Slot s = ref _slots[slot];
        s.KeyRef = keyRef;
        s.Form = (byte)(bitmap ? 1 : 0);
        s.Capacity = capacity;
        s.Count = ids.Length;
        s.Referenced = 1;
        s.InUse = 1;
        s.Data = data;
        if (bitmap)
        {
            new Span<byte>(data, capacity * sizeof(ulong)).Clear();
            var words = (ulong*)data;
            foreach (long id in ids)
                words[id >> 6] |= 1UL << (int)(id & 63);
        }
        else
        {
            var array = (uint*)data;
            for (int i = 0; i < ids.Length; i++)
                array[i] = (uint)ids[i];
        }

        _used++;
        TableInsert(keyRef, slot);
        _dataBytes += DataBytes(s.Form, capacity);
        AdjustBytes(need, registryCharged: true);
        _inserts++;
        return ResidentInsertResult.Inserted;
    }

    private static bool IsAscending(ReadOnlySpan<long> ids)
    {
        for (int i = 1; i < ids.Length; i++)
        {
            if (ids[i - 1] >= ids[i])
                return false;
        }

        return true;
    }

    // Second chance: a list that was used since the hand last passed is spared once.
    private bool EvictOne(int except)
    {
        if (_slotHigh == 0)
            return false;

        for (int step = 0; step < 2 * _slotHigh + 2; step++)
        {
            if (_hand >= _slotHigh)
                _hand = 0;

            int slot = _hand++;
            ref Slot s = ref _slots[slot];
            if (s.InUse == 0 || slot == except)
                continue;

            if (s.Referenced != 0)
            {
                s.Referenced = 0;
                continue;
            }

            FreeSlot(slot);
            _evictions++;
            return true;
        }

        return false;
    }

    private void FreeSlot(int slot)
    {
        ref Slot s = ref _slots[slot];
        TableRemove(s.KeyRef, slot);
        long bytes = DataBytes(s.Form, s.Capacity);
        SparrowMemory.Free(s.Data, bytes);
        _dataBytes -= bytes;
        AdjustBytes(-(bytes + ListOverheadBytes));
        s.Data = null;
        s.InUse = 0;
        s.NextFree = _freeHead;
        _freeHead = slot;
        _used--;
    }

    private void DropAllListsLocked()
    {
        if (_slots == null)
            return;

        for (int s = 0; s < _slotHigh; s++)
        {
            if (_slots[s].InUse != 0)
                SparrowMemory.Free(_slots[s].Data, DataBytes(_slots[s].Form, _slots[s].Capacity));
        }

        _registry?.Release(_bytes);
        _bytes = 0;
        _dataBytes = 0;
        _rates?.Clear();

        // the structures go back to their initial size so that a cleared cache holds next to nothing
        if (_slotCapacity != InitialSlots || _tableMask + 1 != InitialTable || _slotHigh != 0)
        {
            FreeStructures();
            InitStructures();
        }
    }

    // Makes room for the growth of a list before it grows, so that the bytes never pass the budget: evicts other lists, and drops the list itself when nothing else is left.
    private bool MakeRoom(int keep, long delta)
    {
        while (_bytes + delta > _budget || (_registry != null && _registry.ChargedBytes + delta > _registry.BudgetBytes))
        {
            if (EvictOne(keep))
                continue;

            FreeSlot(keep);
            _evictions++;
            return false;
        }

        return true;
    }

    private bool Resize(int slot, int capacity)
    {
        ref Slot s = ref _slots[slot];
        long oldBytes = DataBytes(s.Form, s.Capacity);
        long newBytes = DataBytes(s.Form, capacity);
        if (newBytes > oldBytes && MakeRoom(slot, newBytes - oldBytes) == false)
            return false;

        byte* data = SparrowMemory.AllocateMemory(newBytes);
        long copy = Math.Min(oldBytes, newBytes);
        Buffer.MemoryCopy(s.Data, data, newBytes, copy);
        if (newBytes > oldBytes)
            new Span<byte>(data + oldBytes, (int)(newBytes - oldBytes)).Clear();

        SparrowMemory.Free(s.Data, oldBytes);
        s.Data = data;
        s.Capacity = capacity;
        _dataBytes += newBytes - oldBytes;
        AdjustBytes(newBytes - oldBytes);
        return true;
    }

    private bool ConvertToArray(int slot, int extra)
    {
        ref Slot s = ref _slots[slot];
        int capacity = s.Count + extra + (s.Count + extra) / 8 + 2;
        long newBytes = DataBytes(0, capacity);
        long oldBytes = DataBytes(1, s.Capacity);
        if (newBytes > oldBytes && MakeRoom(slot, newBytes - oldBytes) == false)
            return false;

        var array = (uint*)SparrowMemory.AllocateMemory(newBytes);
        var words = (ulong*)s.Data;
        int n = 0;
        for (int w = 0; w < s.Capacity; w++)
        {
            ulong word = words[w];
            while (word != 0)
            {
                array[n++] = (uint)((w << 6) + BitOperations.TrailingZeroCount(word));
                word &= word - 1;
            }
        }

        Debug.Assert(n == s.Count);
        SparrowMemory.Free(s.Data, oldBytes);
        s.Data = (byte*)array;
        s.Capacity = capacity;
        s.Form = 0;
        _dataBytes += newBytes - oldBytes;
        AdjustBytes(newBytes - oldBytes);
        return true;
    }

    private static int BinarySearch(uint* ids, int count, uint value)
    {
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            uint v = ids[mid];
            if (v == value)
                return mid;

            if (v < value)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return ~lo;
    }

    // The patch of one (key, entry) pair: the entry joins the list of the key when the key is resident.
    private void PatchAdd(long keyRef, long id)
    {
        int slot = Find(keyRef);
        if (slot < 0)
            return;

        if (id > uint.MaxValue)
        {
            // the list cannot hold the entry and would be wrong without it
            _idTooLarge++;
            FreeSlot(slot);
            return;
        }

        ref Slot s = ref _slots[slot];
        if (s.Form == 0)
        {
            var ids = (uint*)s.Data;
            int at = BinarySearch(ids, s.Count, (uint)id);
            if (at >= 0)
                return;

            at = ~at;
            if (s.Count == s.Capacity && Resize(slot, s.Count + Math.Max(4, s.Count / 8)) == false)
                return;

            ids = (uint*)s.Data;
            Buffer.MemoryCopy(ids + at, ids + at + 1, (long)(s.Capacity - at - 1) * sizeof(uint), (long)(s.Count - at) * sizeof(uint));
            ids[at] = (uint)id;
            s.Count++;
        }
        else
        {
            int w = (int)(id >> 6);
            if (w >= s.Capacity)
            {
                // a bitmap that would grow beyond the array of the same ids becomes the array
                bool grown = (long)(w + 1) * sizeof(ulong) > (long)(s.Count + 1) * sizeof(uint) ? ConvertToArray(slot, 1) : Resize(slot, w + 1);
                if (grown == false)
                    return;

                if (_slots[slot].Form == 0)
                {
                    PatchAdd(keyRef, id);
                    return;
                }
            }

            var words = (ulong*)s.Data;
            ulong mask = 1UL << (int)(id & 63);
            if ((words[w] & mask) != 0)
                return;

            words[w] |= mask;
            s.Count++;
        }

        s.Referenced = 1;
        _patchAdds++;
    }

    // From this many changes of one key an array is rebuilt in one pass: every change on its own moves the tail of the array.
    private const int MergeFrom = 8;

    // The removals of one key, ascending by entry.
    private void PatchRemoveGroup(long keyRef, ReadOnlySpan<ResidentPatch.Op> ops)
    {
        int slot = ops.Length < MergeFrom ? -1 : Find(keyRef);
        if (slot < 0 || _slots[slot].Form != 0)
        {
            foreach (ResidentPatch.Op op in ops)
                PatchRemove(keyRef, op.EntryId);

            return;
        }

        ref Slot s = ref _slots[slot];
        var ids = (uint*)s.Data;
        int write = 0, o = 0;
        for (int read = 0; read < s.Count; read++)
        {
            uint id = ids[read];
            while (o < ops.Length && ops[o].EntryId < id)
                o++;

            if (o < ops.Length && ops[o].EntryId == id)
                continue;

            ids[write++] = id;
        }

        _patchRemoves += s.Count - write;
        s.Count = write;
        if (s.Capacity > 32 && s.Count < s.Capacity / 2)
            Resize(slot, s.Count + s.Count / 8 + 2);
    }

    // The additions of one key, ascending by entry.
    private void PatchAddGroup(long keyRef, ReadOnlySpan<ResidentPatch.Op> ops)
    {
        int slot = ops.Length < MergeFrom ? -1 : Find(keyRef);
        if (slot < 0 || _slots[slot].Form != 0)
        {
            foreach (ResidentPatch.Op op in ops)
                PatchAdd(keyRef, op.EntryId);

            return;
        }

        if (ops[^1].EntryId > uint.MaxValue)
        {
            // the list cannot hold the entry and would be wrong without it
            _idTooLarge++;
            FreeSlot(slot);
            return;
        }

        ref Slot s = ref _slots[slot];
        var ids = (uint*)s.Data;
        int fresh = 0, at = 0;
        for (int a = 0; a < ops.Length; a++)
        {
            if (a > 0 && ops[a - 1].EntryId == ops[a].EntryId)
                continue;

            uint id = (uint)ops[a].EntryId;
            while (at < s.Count && ids[at] < id)
                at++;

            if (at < s.Count && ids[at] == id)
                continue;

            fresh++;
        }

        if (fresh == 0)
            return;

        int needed = s.Count + fresh;
        if (needed > s.Capacity && Resize(slot, needed + needed / 8 + 2) == false)
            return;

        s = ref _slots[slot];
        ids = (uint*)s.Data;
        int read2 = s.Count - 1, write2 = needed - 1;
        for (int a = ops.Length - 1; a >= 0; a--)
        {
            if (a + 1 < ops.Length && ops[a + 1].EntryId == ops[a].EntryId)
                continue;

            uint id = (uint)ops[a].EntryId;
            while (read2 >= 0 && ids[read2] > id)
                ids[write2--] = ids[read2--];

            if (read2 >= 0 && ids[read2] == id)
                continue;

            ids[write2--] = id;
        }

        s.Count = needed;
        s.Referenced = 1;
        _patchAdds += fresh;
    }

    private void PatchRemove(long keyRef, long id)
    {
        int slot = Find(keyRef);
        if (slot < 0 || id > uint.MaxValue)
            return;

        ref Slot s = ref _slots[slot];
        if (s.Form == 0)
        {
            var ids = (uint*)s.Data;
            int at = BinarySearch(ids, s.Count, (uint)id);
            if (at < 0)
                return;

            Buffer.MemoryCopy(ids + at + 1, ids + at, (long)(s.Capacity - at) * sizeof(uint), (long)(s.Count - at - 1) * sizeof(uint));
            s.Count--;
            _patchRemoves++;
            if (s.Capacity > 32 && s.Count < s.Capacity / 2)
                Resize(slot, s.Count + s.Count / 8 + 2);
        }
        else
        {
            int w = (int)(id >> 6);
            if (w >= s.Capacity)
                return;

            var words = (ulong*)s.Data;
            ulong mask = 1UL << (int)(id & 63);
            if ((words[w] & mask) == 0)
                return;

            words[w] &= ~mask;
            s.Count--;
            _patchRemoves++;
        }
    }

    // The ids of a resident list, for the tests and the invariant checks.
    private long[] ListOf(int slot)
    {
        ref Slot s = ref _slots[slot];
        var result = new long[s.Count];
        int n = 0;
        if (s.Form == 0)
        {
            for (int i = 0; i < s.Count; i++)
                result[n++] = ((uint*)s.Data)[i];
        }
        else
        {
            var words = (ulong*)s.Data;
            for (int w = 0; w < s.Capacity; w++)
            {
                ulong word = words[w];
                while (word != 0)
                {
                    result[n++] = (w << 6) + BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                }
            }
        }

        Debug.Assert(n == s.Count);
        return result;
    }

    // Whether a resident list is a bitmap (1), an array (0) or absent (-1).
    internal int FormOf(long keyRef)
    {
        _lock.EnterReadLock();
        try
        {
            int slot = Find(keyRef);
            return slot < 0 ? -1 : _slots[slot].Form;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>The keys that have a resident list, for the checks of the tests.</summary>
    internal long[] GetResidentKeys()
    {
        _lock.EnterReadLock();
        try
        {
            var keys = new System.Collections.Generic.List<long>();
            for (int i = 0; i < _slotHigh; i++)
            {
                if (_slots[i].InUse != 0)
                    keys.Add(_slots[i].KeyRef);
            }

            return keys.ToArray();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    internal bool TryGetList(long keyRef, out long[] ids)
    {
        _lock.EnterReadLock();
        try
        {
            int slot = Find(keyRef);
            ids = slot < 0 ? null : ListOf(slot);
            return slot >= 0;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}
