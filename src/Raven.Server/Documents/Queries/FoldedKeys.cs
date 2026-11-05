using System;
using System.Buffers;
using System.Numerics;
using Corax.Querying;

namespace Raven.Server.Documents.Queries
{
    // The keys of a join on a field lowercased into one pooled buffer and looked up in an open-addressing table over that buffer
    internal sealed class FoldedKeys : IDisposable
    {
        private readonly JoinTermScores _keys;
        private byte[] _bytes;
        private int[] _ends;       // end of the folded form of every distinct folded key, in _bytes
        private int[] _best;       // the key of the original list that stands for it
        private int[] _slots;      // open addressing over the distinct folded keys: index + 1, 0 is empty
        private int _count;

        public FoldedKeys(JoinTermScores keys)
        {
            _keys = keys;
            _bytes = ArrayPool<byte>.Shared.Rent(Capacity(keys.Count, 16, 256));
            _ends = ArrayPool<int>.Shared.Rent(Capacity(keys.Count, 1, 16));
            _best = ArrayPool<int>.Shared.Rent(Capacity(keys.Count, 1, 16));
            var slotCount = (int)BitOperations.RoundUpToPowerOf2((uint)Capacity(keys.Count, 2, 16));
            _slots = ArrayPool<int>.Shared.Rent(slotCount);
            _slots.AsSpan(0, slotCount).Clear();
            _slotMask = slotCount - 1;

            Span<byte> folded = stackalloc byte[512];
            byte[] rented = null;
            var used = 0;
            for (var i = 0; i < keys.Count; i++)
            {
                var term = keys.Term(i);
                var maxBytes = term.Length * 3; // a lowercase form can be longer than the original
                Span<byte> scratch = folded;
                if (maxBytes > folded.Length)
                {
                    if (rented == null || rented.Length < maxBytes)
                        rented = new byte[maxBytes];

                    scratch = rented;
                }

                var length = Fold(term, scratch);
                Add(scratch.Slice(0, length), i, ref used);
            }
        }

        private readonly int _slotMask;

        private static int Capacity(int count, int perKey, int minimum)
        {
            var size = (long)count * perKey;
            if (size > Array.MaxLength / 2)
                throw new InvalidOperationException($"The JOIN returned {count:N0} keys, too many to match them case-insensitively against the field of the main query.");

            return (int)Math.Max(minimum, size);
        }

        private static int Fold(ReadOnlySpan<byte> term, Span<byte> output) => CompositeKey.Fold(term, output);

        private static int Hash(ReadOnlySpan<byte> bytes)
        {
            var hash = new HashCode();
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }

        private ReadOnlySpan<byte> Folded(int entry) => _bytes.AsSpan(entry == 0 ? 0 : _ends[entry - 1], _ends[entry] - (entry == 0 ? 0 : _ends[entry - 1]));

        private void Add(ReadOnlySpan<byte> folded, int key, ref int used)
        {
            var slot = Hash(folded) & _slotMask;
            while (_slots[slot] != 0)
            {
                var entry = _slots[slot] - 1;
                if (Folded(entry).SequenceEqual(folded))
                {
                    if (_keys.Beats(key, _best[entry]))
                        _best[entry] = key;

                    return;
                }

                slot = (slot + 1) & _slotMask;
            }

            if (used + folded.Length > _bytes.Length)
            {
                var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_bytes.Length * 2, used + folded.Length));
                _bytes.AsSpan(0, used).CopyTo(bigger);
                ArrayPool<byte>.Shared.Return(_bytes);
                _bytes = bigger;
            }

            folded.CopyTo(_bytes.AsSpan(used));
            used += folded.Length;
            _ends[_count] = used;
            _best[_count] = key;
            _slots[slot] = ++_count;
        }

        public bool TryFind(ReadOnlySpan<byte> term, out int index)
        {
            var slot = Hash(term) & _slotMask;
            while (_slots[slot] != 0)
            {
                var entry = _slots[slot] - 1;
                if (Folded(entry).SequenceEqual(term))
                {
                    index = _best[entry];
                    return true;
                }

                slot = (slot + 1) & _slotMask;
            }

            index = -1;
            return false;
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_bytes);
            ArrayPool<int>.Shared.Return(_ends);
            ArrayPool<int>.Shared.Return(_best);
            ArrayPool<int>.Shared.Return(_slots);
            _bytes = null;
        }
    }
}
