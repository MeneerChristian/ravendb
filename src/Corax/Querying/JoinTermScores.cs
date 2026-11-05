using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Corax.Utils;
using Sparrow.Server;
using Voron.Data.CompactTrees;
using Voron.Impl;
using Voron.Util;

namespace Corax.Querying;

/// <summary>The keys of a join hold more native memory than they may.</summary>
public sealed class JoinKeysMemoryExceededException(long maxHeldBytes, int keys)
    : InvalidOperationException($"The keys of a join hold more than {maxHeldBytes} bytes after {keys} keys.")
{
    public long MaxHeldBytes { get; } = maxHeldBytes;

    public int Keys { get; } = keys;
}

public sealed unsafe class JoinTermScores : IFieldTermScoreSink, IDisposable
{
    private const int MaxReservedKeys = 1 << 26;
    private const int MaxReservedBytes = 1 << 29;
    private const int InsertionSortLimit = 16;

    private ContextBoundNativeList<byte> _bytes;
    private ContextBoundNativeList<int> _ends;
    private ContextBoundNativeList<float> _scores;
    private ContextBoundNativeList<long> _winners;
    private ContextBoundNativeList<float> _ranks;
    private ContextBoundNativeList<int> _bits; // keys kept encoded: how many bits of the bytes of a key are its encoding
    private readonly ByteStringContext _allocator;
    private readonly bool _encoded;
    private readonly long _dictionaryId;
    private CompactKeyCacheScope _decoder;
    private readonly object _decodeGate;
    private bool _decoded; // the keys of an encoded list are decoded the first time a reader needs them in their decoded form
    private ContextBoundNativeList<byte> _decodedBytes;
    private ContextBoundNativeList<int> _decodedEnds;
    private int[] _order; // built once by the first reader that needs it (readers may be on different threads): published with a compare-exchange

    public JoinTermScores(ByteStringContext allocator, bool keepWinners = false, bool keepRanks = false)
    {
        if (keepRanks)
            _ranks = new ContextBoundNativeList<float>(allocator);

        _allocator = allocator;
        _bytes = new ContextBoundNativeList<byte>(allocator);
        _ends = new ContextBoundNativeList<int>(allocator);
        _scores = new ContextBoundNativeList<float>(allocator);
        _winners = new ContextBoundNativeList<long>(allocator);
        HasWinners = keepWinners;
    }

    private JoinTermScores(ByteStringContext allocator, LowLevelTransaction transaction, long dictionaryId) : this(allocator)
    {
        _encoded = true;
        _decodeGate = new object();
        _dictionaryId = dictionaryId;
        _decoder = new CompactKeyCacheScope(transaction);
        _bits = new ContextBoundNativeList<int>(allocator);
        TableHash = PersistentDictionary.GetTableHash(transaction, dictionaryId);
    }

    // The keys are added as the dictionary of the transaction encoded them, without decoding them
    public static JoinTermScores CreateEncoded(ByteStringContext allocator, LowLevelTransaction transaction, long dictionaryId)
        => new(allocator, transaction, dictionaryId);

    public bool IsEncoded => _encoded;

    // The table of the dictionary that encoded the keys: another dictionary with the same table encodes them to the same bits
    public ulong TableHash { get; }

    public int Count => _ends.Count;

    public bool HasWinners { get; }

    public bool HasRanks => _ranks.HasContext;

    public bool ScoresAreRanks { get; set; }

    public float Rank(int index) => _ranks[index];

    // The key as it was stored in the index: an encoded list decodes its keys when this is first asked for
    public ReadOnlySpan<byte> Term(int index)
    {
        if (_encoded == false)
            return StoredTerm(index);

        EnsureDecoded();
        var start = index == 0 ? 0 : _decodedEnds[index - 1];
        return new ReadOnlySpan<byte>(_decodedBytes.RawItems + start, _decodedEnds[index] - start);
    }

    public ReadOnlySpan<byte> EncodedTerm(int index)
    {
        Debug.Assert(_encoded, "only an encoded list holds the encoded form");
        return StoredTerm(index);
    }

    public int EncodedBits(int index) => _bits[index];

    private ReadOnlySpan<byte> StoredTerm(int index)
    {
        var start = index == 0 ? 0 : _ends[index - 1];
        return new ReadOnlySpan<byte>(_bytes.RawItems + start, _ends[index] - start);
    }

    private void EnsureDecoded()
    {
        if (Volatile.Read(ref _decoded))
            return;

        lock (_decodeGate)
        {
            if (_decoded)
                return;

            _decodedBytes = new ContextBoundNativeList<byte>(_allocator);
            _decodedEnds = new ContextBoundNativeList<int>(_allocator);
            Reserve(ref _decodedEnds, Count);
            var key = _decoder.Key;
            for (var i = 0; i < Count; i++)
            {
                var encoded = StoredTerm(i);
                key.Set(_bits[i], encoded, _dictionaryId);
                var term = key.Decoded();
                Room(ref _decodedBytes, term.Length);
                Room(ref _decodedEnds, 1);
                _decodedBytes.AddRange(term);
                _decodedEnds.Add(_decodedBytes.Count);
            }

            Volatile.Write(ref _decoded, true);
        }
    }

    // The order of two keys as encoded bits: the bytes (a key that is the start of another is smaller), then the length in bits
    internal static int CompareEncoded(ReadOnlySpan<byte> left, int leftBits, ReadOnlySpan<byte> right, int rightBits)
    {
        var compare = left.SequenceCompareTo(right);
        return compare != 0 ? compare : leftBits.CompareTo(rightBits);
    }

    public float Score(int index) => _scores[index];

    public long WinnerEntryId(int index) => _winners[index];

    public bool Beats(int index, int other)
    {
        Debug.Assert(HasWinners, "the tie between two keys is decided by the entries that won them");
        float mine, theirs;
        if (HasRanks)
            (mine, theirs) = (-Rank(index), -Rank(other));
        else if (ScoresAreRanks)
            (mine, theirs) = (-Score(index), -Score(other));
        else
            (mine, theirs) = (Score(index), Score(other));

        return mine > theirs || (mine == theirs && WinnerEntryId(index) < WinnerEntryId(other));
    }

    // Turns the negated ranks that a scan of a field kept as the scores into the ranks of the keys
    public void TurnScoresIntoRanks(JoinTermScores relevance)
    {
        var scores = _scores.ToSpan();
        if (relevance == null)
        {
            ScoresAreRanks = true;
            for (var i = 0; i < scores.Length; i++)
                scores[i] = -scores[i];
            return;
        }

        if (HasRanks == false || _ranks.Count != 0)
            throw new InvalidOperationException("The ranks of the keys are taken from their scores, so the list needs room for ranks and no rank added before.");

        for (var i = 0; i < scores.Length; i++)
        {
            _ranks.Add(-scores[i]);
            scores[i] = relevance.TryFind(Term(i), out var at) ? relevance.Score(at) : Bm25Relevance.InitialScoreValue;
        }

        if (_ranks.Count != _ends.Count)
            throw new InvalidOperationException($"{_ranks.Count} ranks for {_ends.Count} keys.");
    }

    // The score of every key becomes its rank
    public void SetScoresAsRanks(ReadOnlySpan<float> ranksByKey)
    {
        if (ranksByKey.Length != _ends.Count)
            throw new InvalidOperationException($"{ranksByKey.Length} ranks for {_ends.Count} keys.");

        ranksByKey.CopyTo(_scores.ToSpan());
        ScoresAreRanks = true;
    }

    public void AddEncoded(ReadOnlySpan<byte> encoded, int bits, float score)
    {
        Debug.Assert(_encoded, "the keys of a list that is not encoded are decoded");
        Debug.Assert(bits > 0 && encoded.Length == (bits + 7) / 8, "the bytes hold exactly the bits");
        Room(ref _bits, 1);
        Add(encoded, score);
        _bits.Add(bits);

        // the bytes of two keys that are equal compare equal: the bits after the last one are zero
        var unused = encoded.Length * 8 - bits;
        if (unused != 0)
            _bytes.RawItems[_bytes.Count - 1] &= (byte)(0xFF << unused);
    }

    // The most that the keys may hold in native memory (the bytes of the keys and what is kept for each of them); long.MaxValue is no limit
    public long MaxHeldBytes { get; set; } = long.MaxValue;

    // The native memory that the keys and what is kept for each of them hold now
    public long HeldBytes => _bytes.Count + (long)_ends.Count * BytesKeptPerKey;

    private int BytesKeptPerKey => sizeof(int) + sizeof(float) + (HasWinners ? sizeof(long) : 0) + (HasRanks ? sizeof(float) : 0) + (_encoded ? sizeof(int) : 0);

    public void Add(ReadOnlySpan<byte> term, float score)
    {
        if ((long)_bytes.Count + term.Length > int.MaxValue)
            throw new InvalidOperationException("The keys of a join hold more than 2 GB.");

        if (HeldBytes + term.Length + BytesKeptPerKey > MaxHeldBytes)
            throw new JoinKeysMemoryExceededException(MaxHeldBytes, _ends.Count);

        Room(ref _bytes, term.Length);
        Room(ref _ends, 1);
        Room(ref _scores, 1);
        _bytes.AddRange(term);
        _ends.Add(_bytes.Count);
        _scores.Add(score);
        ReleaseOrder();
    }

    public void Add(ReadOnlySpan<byte> term, float score, float rank)
    {
        Debug.Assert(HasRanks && _ranks.Count == _ends.Count, "every key of a list with ranks needs a rank");
        Room(ref _ranks, 1);
        Add(term, score);
        _ranks.Add(rank);
    }

    public void Add(ReadOnlySpan<byte> decodedTerm, float score, long winnerEntryId)
    {
        Add(decodedTerm, score);
        if (HasWinners)
        {
            Room(ref _winners, 1);
            _winners.Add(winnerEntryId);
            Debug.Assert(_winners.Count == _ends.Count, "every key of a list with winners needs a winner");
        }
    }

    public void EnsureCapacity(int keys) => EnsureCapacity(keys, 0);

    // Reserves room for more keys and their bytes, both counted from the current end
    public void EnsureCapacity(int keys, int bytes)
    {
        var keyRoom = MaxReservedKeys - _ends.Count;
        if (keys > 0 && keyRoom > 0)
        {
            var total = _ends.Count + Math.Min(keys, keyRoom);
            Reserve(ref _ends, total);
            Reserve(ref _scores, total);
            if (HasRanks)
                Reserve(ref _ranks, total);
            if (HasWinners)
                Reserve(ref _winners, total);
            if (_encoded)
                Reserve(ref _bits, total);
        }

        var byteRoom = MaxReservedBytes - _bytes.Count;
        if (bytes > 0 && byteRoom > 0)
            Reserve(ref _bytes, _bytes.Count + Math.Min(bytes, byteRoom));
    }

    private static void Reserve<T>(ref ContextBoundNativeList<T> list, int total) where T : unmanaged
    {
        if (total >= list.Capacity)
            list.GrowFitting(total + 1);
    }

    // Makes room for more items, doubling like the list does but in blocks that fill what the allocator keeps for them
    internal static void Room<T>(ref ContextBoundNativeList<T> list, int extra) where T : unmanaged
    {
        var needed = list.Count + extra;
        if (needed >= list.Capacity)
            list.GrowFitting(Math.Max(needed + 1, (int)Math.Min(list.Capacity * 2L, (int.MaxValue - 64) / sizeof(T))));
    }

    internal ReadOnlySpan<int> SortedOrder()
    {
        var count = Count;
        var published = Volatile.Read(ref _order);
        if (published != null)
            return published.AsSpan(0, count);

        var order = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
        var span = order.AsSpan(0, count);
        for (var i = 0; i < span.Length; i++)
            span[i] = i;

        SortByPrefixWindow(span);

        // another reader may have been faster: it owns the published array, this one goes back
        published = Interlocked.CompareExchange(ref _order, order, null);
        if (published == null)
            return span;

        ArrayPool<int>.Shared.Return(order);
        return published.AsSpan(0, count);
    }

    public bool TryFind(ReadOnlySpan<byte> term, out int index)
    {
        var order = SortedOrder();
        var low = 0;
        var high = order.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            var compare = Term(order[middle]).SequenceCompareTo(term);
            if (compare == 0)
            {
                index = order[middle];
                return true;
            }

            if (compare < 0)
                low = middle + 1;
            else
                high = middle - 1;
        }

        index = -1;
        return false;
    }

    public void Dispose()
    {
        _bytes.Dispose();
        _ends.Dispose();
        _scores.Dispose();
        _winners.Dispose();
        if (HasRanks)
            _ranks.Dispose();
        if (_encoded)
        {
            _bits.Dispose();
            _decoder.Dispose();
            if (_decoded)
            {
                _decodedBytes.Dispose();
                _decodedEnds.Dispose();
            }
        }

        ReleaseOrder();
    }

    private void SortByPrefixWindow(Span<int> order)
    {
        var count = order.Length;
        if (count < 2)
            return;

        var comparer = new KeyComparer(_bytes.RawItems, _ends.RawItems, _encoded ? _bits.RawItems : null);
        var first = StoredTerm(0);
        var prefix = first.Length;
        for (var i = 1; i < count && prefix > 0; i++)
            prefix = Math.Min(prefix, first.Slice(0, prefix).CommonPrefixLength(StoredTerm(i)));

        // the start of the key after the common prefix as a number: sorting numbers is cheaper than comparing the keys
        var windows = ArrayPool<ulong>.Shared.Rent(count);
        Comparison<int> compare = null;
        try
        {
            var windowSpan = windows.AsSpan(0, count);
            Span<byte> window = stackalloc byte[sizeof(ulong)];
            for (var i = 0; i < count; i++)
            {
                var rest = StoredTerm(i).Slice(prefix);
                window.Clear();
                rest.Slice(0, Math.Min(rest.Length, window.Length)).CopyTo(window);
                windowSpan[i] = BinaryPrimitives.ReadUInt64BigEndian(window);
            }

            MemoryExtensions.Sort(windowSpan, order);

            // keys with equal windows (longer than the window, or equal after it) are put in order by the whole key
            for (var start = 0; start < count;)
            {
                var end = start + 1;
                while (end < count && windowSpan[end] == windowSpan[start])
                    end++;

                if (end - start > 1)
                    SortRun(order.Slice(start, end - start), comparer, ref compare);
                start = end;
            }
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(windows);
        }
    }

    // Sorting with a comparer that is a struct boxes it and wraps it in a delegate on every call, and keys that tie on the window are many and mostly in pairs:
    // short runs are put in order in place and the long ones share one delegate
    private static void SortRun(Span<int> run, KeyComparer comparer, ref Comparison<int> compare)
    {
        if (run.Length > InsertionSortLimit)
        {
            compare ??= comparer.Compare;
            run.Sort(compare);
            return;
        }

        for (var i = 1; i < run.Length; i++)
        {
            var item = run[i];
            var at = i;
            while (at > 0 && comparer.Compare(run[at - 1], item) > 0)
            {
                run[at] = run[at - 1];
                at--;
            }

            run[at] = item;
        }
    }

    private void ReleaseOrder()
    {
        var order = Interlocked.Exchange(ref _order, null);
        if (order != null)
            ArrayPool<int>.Shared.Return(order);
    }

    // bits is null for keys that are not encoded
    private readonly struct KeyComparer(byte* bytes, int* ends, int* bits) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            var left = new ReadOnlySpan<byte>(bytes + (x == 0 ? 0 : ends[x - 1]), ends[x] - (x == 0 ? 0 : ends[x - 1]));
            var right = new ReadOnlySpan<byte>(bytes + (y == 0 ? 0 : ends[y - 1]), ends[y] - (y == 0 ? 0 : ends[y - 1]));
            return bits == null ? left.SequenceCompareTo(right) : CompareEncoded(left, bits[x], right, bits[y]);
        }
    }
}
