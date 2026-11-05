using System;
using System.Runtime.InteropServices;
using Sparrow.Server;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>The ids of the documents that changed, in native memory: no object per id. The buffers are kept between refreshes.</summary>
public sealed class DirtyIds : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Item : IComparable<Item>
    {
        public long Hash;
        public int Offset;
        public int Length;

        public int CompareTo(Item other)
        {
            int result = Hash.CompareTo(other.Hash);
            return result != 0 ? result : Offset.CompareTo(other.Offset);
        }
    }

    private ContextBoundNativeList<byte> _bytes;
    private ContextBoundNativeList<Item> _items;

    public DirtyIds(ByteStringContext allocator)
    {
        _bytes = new ContextBoundNativeList<byte>(allocator, 4096);
        _items = new ContextBoundNativeList<Item>(allocator, 256);
    }

    public int Count => _items.Count;

    public void Clear()
    {
        _bytes.Count = 0;
        _items.Count = 0;
    }

    public void Add(ReadOnlySpan<byte> lowerId, long hash)
    {
        _items.Add(new Item { Hash = hash, Offset = _bytes.Count, Length = lowerId.Length });
        _bytes.AddRange(lowerId);
    }

    /// <summary>Orders the ids by hash and drops the repeated ones. Returns false when two different ids have the same hash.</summary>
    public bool SortAndDedupe()
    {
        Span<Item> items = _items.ToSpan();
        items.Sort();

        int write = 0;
        for (int read = 0; read < items.Length; read++)
        {
            if (write > 0 && items[write - 1].Hash == items[read].Hash)
            {
                if (IdOf(items[write - 1]).SequenceEqual(IdOf(items[read])) == false)
                    return false;

                continue;
            }

            items[write++] = items[read];
        }

        _items.Count = write;
        return true;
    }

    public long HashAt(int index) => _items[index].Hash;

    /// <summary>The position of the id with this hash after <see cref="SortAndDedupe"/>, -1 when no changed id has it.</summary>
    public int IndexOfHash(long hash)
    {
        Span<Item> items = _items.ToSpan();
        int low = 0, high = items.Length - 1;
        while (low <= high)
        {
            int mid = (low + high) >>> 1;
            long found = items[mid].Hash;
            if (found == hash)
                return mid;

            if (found < hash)
                low = mid + 1;
            else
                high = mid - 1;
        }

        return -1;
    }

    public ReadOnlySpan<byte> IdAt(int index) => IdOf(_items[index]);

    private ReadOnlySpan<byte> IdOf(in Item item) => _bytes.ToSpan().Slice(item.Offset, item.Length);

    public void Dispose()
    {
        _bytes.Dispose();
        _items.Dispose();
    }
}
