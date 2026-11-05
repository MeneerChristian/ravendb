using Raven.Client.Documents.Indexes;
using System;
using Corax.Mappings;
using Raven.Server.Documents.Indexes.Joins.Storage;
using System.Text;
using Sparrow.Server;
using Voron;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>Reads, in one pass over the terms of an entry, what a join index keeps of an inner entry: the terms of its declared fields, its groupings and the value to sort by.</summary>
internal sealed class InnerProjector : IDisposable
{
    private struct TermRef
    {
        public int Field;
        public int Offset;
        public int Length;
    }

    private readonly JoinPlan _plan;
    private readonly long[] _fieldRoots;
    private readonly long[] _groupingRoots;
    private readonly byte[][] _groupingTerms;
    private readonly int[] _counts;
    private long _keyRoot = -2;
    private ContextBoundNativeList<byte> _arena;
    private ContextBoundNativeList<TermRef> _terms;
    private ContextBoundNativeList<TermRef> _keys;

    /// <summary>Bit g is set when the entry is in the grouping g.</summary>
    public int Membership { get; private set; }

    /// <summary>The number of terms of the key field of a join on a field (null and non-existing values left out); a term that the entry holds twice is counted twice.</summary>
    public int KeyCount => _keys.Count;

    public bool HasKey => _keys.Count > 0;

    public ReadOnlySpan<byte> KeyAt(int index)
    {
        TermRef key = _keys[index];
        return _arena.ToSpan().Slice(key.Offset, key.Length);
    }

    public int Count => _terms.Count;

    public InnerProjector(JoinPlan plan, ByteStringContext allocator)
    {
        _plan = plan;
        _fieldRoots = new long[plan.Fields.Length];
        _groupingRoots = new long[plan.GroupingFields.Length];
        _groupingTerms = new byte[plan.GroupingFields.Length][];
        _counts = new int[plan.Fields.Length];
        _arena = new ContextBoundNativeList<byte>(allocator, 1024);
        _terms = new ContextBoundNativeList<TermRef>(allocator, 64);
        _keys = new ContextBoundNativeList<TermRef>(allocator, 8);
    }

    /// <summary>Finds the fields in the snapshot of the index: a field that no document has had yet does not exist there.</summary>
    public void Bind(IndexView inner)
    {
        if (_plan.IsFieldJoin)
            _keyRoot = inner.RootPageOf(_plan.InnerKeyField);

        for (int f = 0; f < _fieldRoots.Length; f++)
            _fieldRoots[f] = inner.RootPageOf(_plan.Fields[f]);

        for (int g = 0; g < _groupingRoots.Length; g++)
        {
            _groupingRoots[g] = inner.RootPageOf(_plan.GroupingFields[g]);
            _groupingTerms[g] = GroupingTerm(inner, _plan, g);
        }
    }

    /// <summary>The term of the value of a grouping as the inner index stores it: encoded with the analyzer of the field, the way a query on the index encodes the value it looks for.</summary>
    public static byte[] GroupingTerm(IndexView inner, JoinPlan plan, int grouping)
    {
        JoinGrouping definition = plan.Definition.Groupings[grouping];
        if (inner.Snapshot.FieldsMapping.TryGetByFieldName(definition.Field, out IndexFieldBinding binding) == false)
            throw new JoinUnsupportedException($"The inner index has no field '{definition.Field}' to group by.");

        Slice term = inner.Searcher.EncodeAndApplyAnalyzer(binding.Metadata, definition.Value);
        return term.AsReadOnlySpan().ToArray();
    }

    public byte[] GroupingTermOf(int grouping) => _groupingTerms[grouping];

    public int FieldOf(int index) => _terms[index].Field;

    public ReadOnlySpan<byte> TermOf(int index)
    {
        TermRef term = _terms[index];
        return _arena.ToSpan().Slice(term.Offset, term.Length);
    }

    public void Project(IndexView inner, long entryId)
    {
        _arena.Count = 0;
        _terms.Count = 0;
        Array.Clear(_counts);
        Membership = 0;
        _keys.Count = 0;

        var reader = inner.ReadEntry(entryId);
        try
        {
            while (reader.MoveNext())
            {
                if (reader.IsNull || reader.IsNonExisting)
                    continue;

                long root = reader.FieldRootPage;
                ReadOnlySpan<byte> term = default;
                bool read = false;

                if (root == _keyRoot)
                {
                    term = reader.Current.Decoded();
                    read = true;
                    ReadKey(term);
                }

                for (int f = 0; f < _fieldRoots.Length; f++)
                {
                    if (_fieldRoots[f] != root)
                        continue;

                    if (read == false)
                    {
                        term = reader.Current.Decoded();
                        read = true;
                    }

                    _counts[f]++;
                    Validate(f, term, entryId);
                    _terms.Add(new TermRef { Field = f, Offset = _arena.Count, Length = term.Length });
                    _arena.AddRange(term);
                }

                for (int g = 0; g < _groupingRoots.Length; g++)
                {
                    if (_groupingRoots[g] != root)
                        continue;

                    if (read == false)
                    {
                        term = reader.Current.Decoded();
                        read = true;
                    }

                    if (term.SequenceEqual(_groupingTerms[g]))
                        Membership |= 1 << g;
                }
            }
        }
        finally
        {
            inner.EndEntry();
        }

        for (int f = 0; f < _counts.Length; f++)
        {
            if (_counts[f] > 1 && _plan.Schema.IsMultiValued(f) == false)
                throw new JoinUnsupportedException($"The field '{_plan.Definition.Fields[f].Field}' of the inner index holds several values in one document, a field of the kind {_plan.Definition.Fields[f].Kind} holds one.");
        }
    }

    private void ReadKey(ReadOnlySpan<byte> term)
    {
        if (term.Length > JoinLimits.MaxTermLength)
            throw new JoinUnsupportedException($"The field '{_plan.Definition.InnerField ?? "id()"}' of the inner index has a key longer than {JoinLimits.MaxTermLength} bytes.");

        _keys.Add(new TermRef { Offset = _arena.Count, Length = term.Length });
        _arena.AddRange(term);
    }

    internal void Validate(int field, ReadOnlySpan<byte> term, long entryId)
    {
        ValidateTerm(_plan, field, term, entryId);
        if (_plan.IsLong(field))
            ParseLong(field, term, entryId);
    }

    internal static void ValidateTerm(JoinPlan plan, int field, ReadOnlySpan<byte> term, long entryId)
    {
        if (term.Length > JoinLimits.MaxTermLength)
            throw new JoinUnsupportedException($"The field '{plan.Definition.Fields[field].Field}' of the inner index has a term longer than {JoinLimits.MaxTermLength} bytes.");
    }

    internal long ParseLong(int field, ReadOnlySpan<byte> term, long entryId) => ParseLong(_plan, field, term);

    internal static long ParseLong(JoinPlan plan, int field, ReadOnlySpan<byte> term)
    {
        if (JoinPlan.TryParseLong(term, out long value) == false)
            throw new JoinUnsupportedException($"The field '{plan.Definition.Fields[field].Field}' is declared Long, but the inner index has the term '{Encoding.UTF8.GetString(term)}' in it, which is not a whole number.");

        return value;
    }

    public void Dispose()
    {
        _arena.Dispose();
        _terms.Dispose();
        _keys.Dispose();
    }
}

/// <summary>Reads the keys of an outer entry: the terms of the outer field, null and non-existing values left out, as the keys of the inner documents they name.</summary>
internal sealed class OuterKeyReader : IDisposable
{
    private struct TermSpan
    {
        public int Offset;
        public int Length;
    }

    private readonly JoinPlan _plan;
    private ContextBoundNativeList<byte> _arena;
    private ContextBoundNativeList<TermSpan> _terms;
    private ContextBoundNativeList<long> _keys;
    private long _root;

    public OuterKeyReader(JoinPlan plan, ByteStringContext allocator)
    {
        _plan = plan;
        _arena = new ContextBoundNativeList<byte>(allocator, 256);
        _terms = new ContextBoundNativeList<TermSpan>(allocator, 8);
        _keys = new ContextBoundNativeList<long>(allocator, 8);
    }

    public void Bind(IndexView outer) => _root = outer.RootPageOf(_plan.OuterKeyField);

    /// <summary>The keys of the inner documents that the entry names, ascending and without repeats (an id that is written twice, or in another case, is one key).</summary>
    public ReadOnlySpan<long> Keys => _keys.ToSpan();

    /// <summary>The terms of the field as the entry has them, in the order of the entry.</summary>
    public int TermCount => _terms.Count;

    public ReadOnlySpan<byte> TermAt(int index)
    {
        TermSpan term = _terms[index];
        return _arena.ToSpan().Slice(term.Offset, term.Length);
    }

    /// <summary>Whether the entry has a key. A null value is none.</summary>
    public bool Read(IndexView outer, long entryId, IInnerKeys hasher)
    {
        _arena.Count = 0;
        _terms.Count = 0;
        _keys.Count = 0;
        if (_root < 0)
            return false;

        var reader = outer.ReadEntry(entryId);
        try
        {
            while (reader.MoveNext())
            {
                if (reader.FieldRootPage != _root || reader.IsNull || reader.IsNonExisting)
                    continue;

                ReadOnlySpan<byte> term = reader.Current.Decoded();
                _terms.Add(new TermSpan { Offset = _arena.Count, Length = term.Length });
                _arena.AddRange(term);
                _keys.Add(hasher.InnerKeyOf(term));
            }
        }
        finally
        {
            outer.EndEntry();
        }

        Span<long> keys = _keys.ToSpan();
        keys.Sort();
        int write = 0;
        for (int read = 0; read < keys.Length; read++)
        {
            if (write > 0 && keys[write - 1] == keys[read])
                continue;

            keys[write++] = keys[read];
        }

        _keys.Count = write;
        if (write > JoinLimits.MaxKeysPerEntry)
            throw new JoinUnsupportedException($"The field '{_plan.Definition.OuterField}' of the outer index holds {write} ids in one document, a join index holds at most {JoinLimits.MaxKeysPerEntry}.");

        return write > 0;
    }

    public void Dispose()
    {
        _arena.Dispose();
        _terms.Dispose();
        _keys.Dispose();
    }
}
