using System;
using System.Runtime.InteropServices;
using Corax.Indexing;
using Corax.Utils;
using Sparrow.Server;
using Voron;
using Voron.Data.Containers;
using Voron.Data.PostingLists;
using Voron.Impl;
using Voron.Util;
using Voron.Util.PFor;

namespace Raven.Server.Documents.Indexes.Joins.Storage;

/// <summary>
/// Keeps sorted lists of entry ids in the way the lists of a Corax index are kept, so that a list can be read by the matches of Corax:
/// a list is a handle (a long) that is kept by whoever owns the list. The handle is 0 for an empty list, the encoded entry itself for a list of one entry,
/// the id of a container item with the entries compressed in it for a small list, and the id of a container item with the state of a posting list for a large one.
/// The values in the lists are entry ids encoded with <see cref="EntryIdEncodings"/>, see <see cref="Encode"/>.
/// </summary>
public sealed unsafe class ListStore : IDisposable
{
    public const long EmptyHandle = 0;

    private const int TailPadding = 256;

    private readonly LowLevelTransaction _llt;
    private readonly ContainerId _container;
    private readonly FastPForEncoder _encoder;
    private FastPForDecoder _decoder;
    private ContextBoundNativeList<long> _existing;
    private ContextBoundNativeList<long> _merged;
    private ContextBoundNativeList<long> _temp;
    private ContextBoundNativeList<long> _flaggedRemovals;
    private readonly ByteStringContext<ByteStringMemoryCache>.InternalScope _encodeScope;
    private readonly ByteString _encodeBuffer;

    public ListStore(LowLevelTransaction llt, ContainerId container)
    {
        _llt = llt;
        _container = container;
        _encoder = new FastPForEncoder(llt.Allocator);
        _decoder = new FastPForDecoder(llt.Allocator);
        _existing = new ContextBoundNativeList<long>(llt.Allocator, 256);
        _merged = new ContextBoundNativeList<long>(llt.Allocator, 256);
        _temp = new ContextBoundNativeList<long>(llt.Allocator, 256);
        _flaggedRemovals = new ContextBoundNativeList<long>(llt.Allocator, 256);
        _encodeScope = llt.Allocator.Allocate(Container.MaxSizeInsideContainerPage, out _encodeBuffer);
    }

    public static long Encode(long entryId) => EntryIdEncodings.Encode(entryId, 1, TermIdMask.Single);

    public static long Decode(long encoded) => (long)EntryIdEncodings.DecodeAndDiscardFrequency(encoded);

    public static TermIdMask KindOf(long handle) => (TermIdMask)(handle & (long)TermIdMask.EnsureIsSingleMask);

    /// <summary>
    /// Applies the changes to the list and returns its new handle, which the caller has to keep instead of the old one.
    /// Both spans hold encoded values (<see cref="Encode"/>), sorted ascending and without duplicates, and must not share a value.
    /// A removal of a value that is not in the list is ignored.
    /// </summary>
    public long Update(long handle, ReadOnlySpan<long> additions, ReadOnlySpan<long> removals)
    {
        if (handle == EmptyHandle)
            return additions.Length == 0 ? EmptyHandle : Create(additions);

        switch (KindOf(handle))
        {
            case TermIdMask.Single:
                _existing.Count = 0;
                _existing.Add(handle);
                return UpdateInline(handle, additions, removals);
            case TermIdMask.SmallPostingList:
                ContainerEntryId smallId = EntryIdEncodings.GetContainerId(handle);
                Container.Get(_llt, smallId, out Container.Item item);
                DecodeSmall(item);
                return UpdateInline(handle, additions, removals);
            default:
                return UpdateLarge(handle, additions, removals);
        }
    }

    private long UpdateInline(long handle, ReadOnlySpan<long> additions, ReadOnlySpan<long> removals)
    {
        Merge(additions, removals);

        if (_merged.Count == 0)
        {
            Release(handle);
            return EmptyHandle;
        }

        if (KindOf(handle) == TermIdMask.Single && _merged.Count == 1 && _merged[0] == handle)
            return handle;

        ReadOnlySpan<long> merged = _merged.ToSpan();
        if (KindOf(handle) == TermIdMask.SmallPostingList && merged.Length > 1 && TryEncodeSmall(merged, out int length))
        {
            ContainerEntryId id = EntryIdEncodings.GetContainerId(handle);
            Container.GetMutable(_llt, id, out Container.Item current);
            if (current.Length == length)
            {
                new Span<byte>(_encodeBuffer.Ptr, length).CopyTo(current.ToSpan());
                return handle;
            }
        }

        Release(handle);
        return Create(_merged.ToSpan());
    }

    private void Merge(ReadOnlySpan<long> additions, ReadOnlySpan<long> removals)
    {
        _merged.Count = 0;
        _merged.Inner.EnsureCapacityFor(_llt.Allocator, _existing.Count + additions.Length);

        int e = 0, a = 0, r = 0;
        long* existing = _existing.RawItems;
        int existingCount = _existing.Count;

        while (e < existingCount || a < additions.Length)
        {
            long next;
            if (a >= additions.Length || (e < existingCount && existing[e] <= additions[a]))
            {
                next = existing[e];
                if (a < additions.Length && additions[a] == next)
                    a++;

                e++;
            }
            else
            {
                next = additions[a++];
            }

            while (r < removals.Length && removals[r] < next)
                r++;

            if (r < removals.Length && removals[r] == next)
            {
                r++;
                continue;
            }

            _merged.AddUnsafe(next);
        }
    }

    private long Create(ReadOnlySpan<long> values)
    {
        if (values.Length == 1)
            return values[0];

        if (TryEncodeSmall(values, out int length))
        {
            ContainerEntryId id = Container.Allocate(_llt, _container, length, out Span<byte> space);
            new Span<byte>(_encodeBuffer.Ptr, length).CopyTo(space);
            return EntryIdEncodings.Encode((long)id, 0, TermIdMask.SmallPostingList);
        }

        ContainerEntryId setId = Container.Allocate(_llt, _container, sizeof(PostingListState), out Span<byte> setSpace);
        ref PostingListState state = ref MemoryMarshal.AsRef<PostingListState>(setSpace);
        state = default;
        PostingList.Create(_llt, ref state);

        fixed (long* additions = values)
            PostingList.Update(_llt, ref state, additions, values.Length, null, 0, _encoder, ref _temp, ref _decoder);

        return EntryIdEncodings.Encode((long)setId, 0, TermIdMask.PostingList);
    }

    private long UpdateLarge(long handle, ReadOnlySpan<long> additions, ReadOnlySpan<long> removals)
    {
        ContainerEntryId id = EntryIdEncodings.GetContainerId(handle);
        Span<byte> space = Container.GetMutable(_llt, id);
        ref PostingListState state = ref MemoryMarshal.AsRef<PostingListState>(space);

        // the posting list merges the removals with the entries and tells them apart by the lowest bit, which no entry has
        _flaggedRemovals.Count = 0;
        _flaggedRemovals.Inner.EnsureCapacityFor(_llt.Allocator, removals.Length);
        foreach (long removal in removals)
            _flaggedRemovals.AddUnsafe(removal | 1);

        long count;
        fixed (long* adds = additions)
            count = PostingList.Update(_llt, ref state, adds, additions.Length, _flaggedRemovals.RawItems, removals.Length, _encoder, ref _temp, ref _decoder);

        if (count > 0)
            return handle;

        Release(handle);
        return EmptyHandle;
    }

    /// <summary>Frees what a list uses. The handle must not be used afterwards.</summary>
    public void Release(long handle)
    {
        switch (KindOf(handle))
        {
            case TermIdMask.Single:
                break;
            case TermIdMask.SmallPostingList:
                Container.Delete(_llt, _container, EntryIdEncodings.GetContainerId(handle));
                break;
            default:
                ContainerEntryId id = EntryIdEncodings.GetContainerId(handle);
                Span<byte> space = Container.GetMutable(_llt, id);
                PostingListState state = MemoryMarshal.AsRef<PostingListState>(space);
                _llt.FreePage(state.RootPage);
                Container.Delete(_llt, _container, id);
                break;
        }
    }

    private bool TryEncodeSmall(ReadOnlySpan<long> values, out int length)
    {
        byte* output = _encodeBuffer.Ptr;
        int offset = WriteVarInt(output, values.Length);

        fixed (long* pValues = values)
        {
            int size = _encoder.Encode(pValues, values.Length);
            if (size >= _encodeBuffer.Length - offset)
            {
                length = 0;
                return false;
            }

            (int count, int sizeUsed) = _encoder.Write(output + offset, _encodeBuffer.Length - offset);
            if (count != values.Length || sizeUsed != size)
                throw new InvalidOperationException($"The list of {values.Length} entries was encoded as {count} entries and {sizeUsed} bytes instead of {size}.");

            length = size + offset;
            return true;
        }
    }

    private void DecodeSmall(Container.Item item)
    {
        int count = ReadVarInt(item.Address, out int offset);
        _existing.Count = 0;
        _existing.Inner.EnsureCapacityFor(_llt.Allocator, count + TailPadding);
        _decoder.Init(item.Address + offset, item.Length - offset);
        int read = _decoder.Read(_existing.RawItems, RoundUp(count + 1));
        if (read != count)
            throw new InvalidOperationException($"A small list of {count} entries decoded to {read}.");

        _existing.Count = read;
    }

    private static int RoundUp(int value) => (value + 255) / 256 * 256;

    internal static int WriteVarInt(byte* output, int value)
    {
        uint v = (uint)value;
        int pos = 0;
        while (v >= 0x80)
        {
            output[pos++] = (byte)(v | 0x80);
            v >>= 7;
        }

        output[pos++] = (byte)v;
        return pos;
    }

    internal static int ReadVarInt(byte* input, out int offset)
    {
        uint result = 0;
        int shift = 0;
        offset = 0;
        while (true)
        {
            byte b = input[offset++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return (int)result;

            shift += 7;
        }
    }

    public long Count(long handle) => Count(_llt, handle);

    public static long Count(LowLevelTransaction llt, long handle)
    {
        switch (KindOf(handle))
        {
            case TermIdMask.Single:
                return handle == EmptyHandle ? 0 : 1;
            case TermIdMask.SmallPostingList:
                Container.Get(llt, EntryIdEncodings.GetContainerId(handle), out Container.Item item);
                return ReadVarInt(item.Address, out _);
            default:
                Container.Get(llt, EntryIdEncodings.GetContainerId(handle), out Container.Item set);
                return ((PostingListState*)set.Address)->NumberOfEntries;
        }
    }

    /// <summary>Appends the entry ids of the list (decoded, ascending) to the output.</summary>
    public void Read(long handle, ref ContextBoundNativeList<long> output)
    {
        if (handle == EmptyHandle)
            return;

        int start = output.Count;

        switch (KindOf(handle))
        {
            case TermIdMask.Single:
                output.Add(handle);
                break;
            case TermIdMask.SmallPostingList:
                Container.Get(_llt, EntryIdEncodings.GetContainerId(handle), out Container.Item item);
                DecodeSmall(item);
                output.AddRange(_existing.ToSpan());
                break;
            default:
                Container.Get(_llt, EntryIdEncodings.GetContainerId(handle), out Container.Item set);
                var state = *(PostingListState*)set.Address;
                using (var list = new PostingList(_llt, Slices.Empty, state))
                {
                    PostingList.Iterator iterator = list.Iterate();
                    _temp.Count = 0;
                    _temp.Inner.EnsureCapacityFor(_llt.Allocator, 1024);
                    while (iterator.Fill(_temp.ToFullCapacitySpan().Slice(0, 1024), out int total))
                        output.AddRange(_temp.ToFullCapacitySpan().Slice(0, total));
                }

                break;
        }

        EntryIdEncodings.DecodeAndDiscardFrequencyClassic(output.ToSpan().Slice(start), output.Count - start);
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _existing.Dispose();
        _merged.Dispose();
        _temp.Dispose();
        _flaggedRemovals.Dispose();
        _encodeScope.Dispose();
    }
}
