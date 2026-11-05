using System;
using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace Corax.Querying;

public static class CompositeKey
{
    private const byte Zero = 0, Separator = 1, EscapedZero = 0xFF;

    internal static int MaxEscapedLength(int termLength) => termLength * 2;

    internal static void AppendPart(ref byte[] key, ref int written, ReadOnlySpan<byte> part, bool first)
    {
        Reserve(ref key, written, 2 + MaxEscapedLength(part.Length));
        if (first == false)
        {
            key[written++] = Zero;
            key[written++] = Separator;
        }

        for (var rest = part; rest.Length > 0;)
        {
            var zero = rest.IndexOf(Zero);
            if (zero == -1)
            {
                rest.CopyTo(key.AsSpan(written));
                written += rest.Length;
                break;
            }

            rest.Slice(0, zero).CopyTo(key.AsSpan(written));
            written += zero;
            key[written++] = Zero;
            key[written++] = EscapedZero;
            rest = rest.Slice(zero + 1);
        }
    }

    internal static void AppendEscapedPart(ref byte[] key, ref int written, ReadOnlySpan<byte> escapedPart, bool first)
    {
        Reserve(ref key, written, 2 + escapedPart.Length);
        if (first == false)
        {
            key[written++] = Zero;
            key[written++] = Separator;
        }

        escapedPart.CopyTo(key.AsSpan(written));
        written += escapedPart.Length;
    }

    internal static void Reserve(ref byte[] buffer, int written, int extra)
    {
        var needed = written + extra;
        if (needed <= buffer.Length)
            return;

        var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, buffer.Length * 2));
        buffer.AsSpan(0, written).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(buffer);
        buffer = bigger;
    }

    internal static bool TryReadPart(ReadOnlySpan<byte> key, ref int position, out ReadOnlySpan<byte> part)
    {
        part = default;
        if (position < 0)
            return false;

        var start = position;
        for (var i = start; i < key.Length; i++)
        {
            if (key[i] != Zero)
                continue;

            if (i + 1 < key.Length && key[i + 1] == Separator)
            {
                part = key.Slice(start, i - start);
                position = i + 2;
                return true;
            }

            i++;
        }

        part = key.Slice(start);
        position = -1;
        return true;
    }

    internal static int FirstTerm(ReadOnlySpan<byte> key, Span<byte> output)
    {
        var position = 0;
        TryReadPart(key, ref position, out var part);
        var written = 0;
        for (var i = 0; i < part.Length; i++)
        {
            output[written++] = part[i];
            if (part[i] == Zero)
                i++;
        }

        return written;
    }

    // Lowercases the UTF-8 text rune by rune, up to three times the input in size
    public static int Fold(ReadOnlySpan<byte> term, Span<byte> output)
    {
        var written = 0;
        for (var read = 0; read < term.Length;)
        {
            var b = term[read];
            if (b < 0x80)
            {
                output[written++] = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
                read++;
                continue;
            }

            if (Rune.DecodeFromUtf8(term.Slice(read), out var rune, out var consumed) != OperationStatus.Done)
            {
                output[written++] = b;
                read++;
                continue;
            }

            written += Rune.ToLowerInvariant(rune).EncodeToUtf8(output.Slice(written));
            read += consumed;
        }

        return written;
    }
}

// The values of the fields of one entry make more keys than CompositeKeyBuilder.MaxCombinations
public sealed class CompositeKeyLimitException(long combinations)
    : InvalidOperationException($"The fields of a JOIN on several fields hold at least {combinations} combinations of values in one entry, more than the {CompositeKeyBuilder.MaxCombinations} that are supported.")
{
    public long Combinations { get; } = combinations;
}

public sealed class CompositeKeyBuilder : IDisposable
{
    // the cross product is capped: lists in several fields would make millions of keys
    public const int MaxCombinations = 1 << 16;

    private readonly int _fields;
    private byte[] _values = ArrayPool<byte>.Shared.Rent(256);
    private int _valuesUsed;
    private int[] _valueEnds = ArrayPool<int>.Shared.Rent(16); // end of every value in _values, in the order they were added
    private int[] _valueFields = ArrayPool<int>.Shared.Rent(16);
    private int[] _byField = ArrayPool<int>.Shared.Rent(16); // the values grouped by field (the order of adding is kept inside a field)
    private int _valueCount;
    private readonly int[] _firstValue; // per field: its first position in _byField, one more at the end
    private byte[] _key = ArrayPool<byte>.Shared.Rent(256);
    private int _combinations = -1;

    public CompositeKeyBuilder(int fields)
    {
        _fields = fields;
        _firstValue = new int[fields + 1];
    }

    public void Reset()
    {
        _valuesUsed = 0;
        _valueCount = 0;
        _combinations = -1;
    }

    // Adds a value of a field; the values of different fields may come in any order
    public void Add(int field, ReadOnlySpan<byte> term)
    {
        Debug.Assert((uint)field < (uint)_fields, "the field is one of the fields of the builder");
        _combinations = -1;
        if (_valuesUsed + term.Length > _values.Length)
        {
            var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_values.Length * 2, _valuesUsed + term.Length));
            _values.AsSpan(0, _valuesUsed).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(_values);
            _values = bigger;
        }

        if (_valueCount == _valueEnds.Length)
        {
            Grow(ref _valueEnds);
            Grow(ref _valueFields);
            Grow(ref _byField);
        }

        term.CopyTo(_values.AsSpan(_valuesUsed));
        _valuesUsed += term.Length;
        _valueEnds[_valueCount] = _valuesUsed;
        _valueFields[_valueCount++] = field;
    }

    private void Grow(ref int[] array)
    {
        var bigger = ArrayPool<int>.Shared.Rent(_valueCount * 2);
        array.AsSpan(0, _valueCount).CopyTo(bigger);
        ArrayPool<int>.Shared.Return(array);
        array = bigger;
    }

    public int Combinations
    {
        get
        {
            if (_combinations == -1)
                _combinations = GroupByField();

            return _combinations;
        }
    }

    private int GroupByField()
    {
        Array.Clear(_firstValue);
        for (var v = 0; v < _valueCount; v++)
            _firstValue[_valueFields[v] + 1]++;

        for (var f = 0; f < _fields; f++)
        {
            if (_firstValue[f + 1] == 0)
                return 0;
        }

        long total = 1;
        for (var f = 0; f < _fields; f++)
        {
            total *= _firstValue[f + 1];
            if (total > MaxCombinations)
                throw new CompositeKeyLimitException(total);
        }

        for (var f = 0; f < _fields; f++)
            _firstValue[f + 1] += _firstValue[f];

        Span<int> next = stackalloc int[_fields];
        _firstValue.AsSpan(0, _fields).CopyTo(next);
        for (var v = 0; v < _valueCount; v++)
            _byField[next[_valueFields[v]]++] = v;

        return (int)total;
    }

    public ReadOnlySpan<byte> Combination(int combination)
    {
        Debug.Assert((uint)combination < (uint)Combinations, "the combination is one of the combinations");
        _ = Combinations; // groups the values on the first call
        var written = 0;
        // mixed radix: the value chosen per field is a digit of the combination, the last field changes fastest
        var radix = 1;
        Span<int> choice = stackalloc int[_fields];
        for (var f = _fields - 1; f >= 0; f--)
        {
            var count = _firstValue[f + 1] - _firstValue[f];
            choice[f] = combination / radix % count;
            radix *= count;
        }

        for (var f = 0; f < _fields; f++)
        {
            var value = _byField[_firstValue[f] + choice[f]];
            var start = value == 0 ? 0 : _valueEnds[value - 1];
            CompositeKey.AppendPart(ref _key, ref written, _values.AsSpan(start, _valueEnds[value] - start), first: f == 0);
        }

        return _key.AsSpan(0, written);
    }

    public void Dispose()
    {
        if (_values == null)
            return;

        ArrayPool<byte>.Shared.Return(_values);
        ArrayPool<int>.Shared.Return(_valueEnds);
        ArrayPool<int>.Shared.Return(_valueFields);
        ArrayPool<int>.Shared.Return(_byField);
        ArrayPool<byte>.Shared.Return(_key);
        _values = null;
    }
}
