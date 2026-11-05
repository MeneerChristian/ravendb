using System;
using System.Runtime.InteropServices;
using Corax.Mappings;
using Sparrow.Server;
using Voron.Data.CompactTrees;
using Voron.Data.Lookups;
using Voron.Impl;

namespace Corax.Querying;

internal enum JoinKeyLookupStrategy
{
    // The sweep when the keys are dense in the tree, the cursor reuse otherwise
    Auto,

    // One search from the root of the tree for every key
    PerKey,

    // The search of a key goes on from the leaf of the previous key
    Cursor,

    // One pass over the terms of the tree, merged with the keys
    Sweep
}

internal static class JoinKeyLookupSettings
{
    // crossover: a pass over all the terms beats a cursor lookup per key once there is a key per SweepDensity terms
    public const int SweepDensity = 8;

    [ThreadStatic] private static JoinKeyLookupStrategy _forced;

    // replaces the strategy chosen from the density of the keys, on this thread
    public static JoinKeyLookupStrategy Forced
    {
        get => _forced;
        set => _forced = value;
    }

    internal static JoinKeyLookupStrategy Current => _forced;

    // The strategy for this many keys against a tree of this many terms
    public static JoinKeyLookupStrategy Choose(long keyCount, long termsInTree) => keyCount * SweepDensity >= termsInTree ? JoinKeyLookupStrategy.Sweep : JoinKeyLookupStrategy.Cursor;
}

internal sealed unsafe class JoinKeyLookup : IDisposable
{
    private readonly IndexSearcher _searcher;
    private readonly CompactTree _tree;
    private readonly FieldMetadata _field;
    private readonly bool _keysAreIndexedTerms;
    private readonly LowLevelTransaction _llt;
    private readonly long _dictionaryId;
    private readonly JoinKeyLookupStrategy _strategy;

    private ByteString _previous; // from the allocator of the transaction: released with it, shared by the copies of this struct
    private int _previousLength;
    private int _previousBits; // the length of the previous key in bits, when the keys are encoded

    private CompactKeyCacheScope _sweepKey;
    private CompactTree.Iterator<Lookup<CompactTree.CompactKeyLookup>.ForwardIterator> _sweep;
    private bool _sweepOpen;
    private bool _sweepHasTerm;
    private long _sweepValue;

    public JoinKeyLookup(IndexSearcher searcher, CompactTree tree, in FieldMetadata field, bool keysAreIndexedTerms, int keyCount, bool sweepAllowed, bool cursorAllowed = true)
    {
        _searcher = searcher;
        _tree = tree;
        _field = field;
        _keysAreIndexedTerms = keysAreIndexedTerms;
        _llt = searcher.Transaction.LowLevelTransaction;
        _dictionaryId = tree.DictionaryId;
        _previousLength = -1;

        var strategy = JoinKeyLookupSettings.Current;
        if (strategy == JoinKeyLookupStrategy.Auto)
            strategy = JoinKeyLookupSettings.Choose(keyCount, tree.NumberOfEntries);
        if (strategy == JoinKeyLookupStrategy.Sweep && sweepAllowed == false)
            strategy = JoinKeyLookupStrategy.Cursor;
        if (strategy == JoinKeyLookupStrategy.Cursor && cursorAllowed == false)
            strategy = JoinKeyLookupStrategy.PerKey;

        _strategy = strategy;
        if (strategy == JoinKeyLookupStrategy.Sweep)
        {
            _sweepKey = new CompactKeyCacheScope(_llt);
            _sweepOpen = true;
            _sweep = tree.Iterate();
        }
    }

    public void Restart() => _previousLength = -1;

    public bool TryGetContainerId(ReadOnlySpan<byte> key, out long containerId)
    {
        if (_strategy == JoinKeyLookupStrategy.PerKey)
            return _searcher.TryGetContainerIdOfJoinKey(_tree, _field, key, _keysAreIndexedTerms, out containerId);

        // the empty string and the null are stored as markers
        if (key.Length == 0 || key.SequenceEqual(Constants.EmptyStringByteSpan))
            return Find(Constants.EmptyStringByteSpan, out containerId);

        if (key.SequenceEqual(Constants.NullValueSpan))
            return Find(Constants.NullValueSpan, out containerId);

        if (_keysAreIndexedTerms)
            return Find(key, out containerId);

        _searcher.ApplyAnalyzer(_field, _field.Analyzer, key, out var analyzed);
        try
        {
            containerId = -1;
            return analyzed.Size != 0 && Find(analyzed.AsReadOnlySpan(), out containerId);
        }
        finally
        {
            analyzed.Release(_searcher.Allocator);
        }
    }

    // The key is encoded with a dictionary that has the table of the dictionary of the tree, and the tree holds the key as it is
    public bool TryGetContainerIdEncoded(ReadOnlySpan<byte> encoded, int bits, out long containerId)
    {
        using var scope = new CompactKeyCacheScope(_llt);
        scope.Key.Set(bits, encoded, _dictionaryId);

        if (_strategy == JoinKeyLookupStrategy.PerKey)
            return _tree.TryGetValue(scope.Key, out containerId);

        // an unpositioned lookup, or a key that is smaller than the previous one, starts again from the root
        var restart = _previousLength < 0 || JoinTermScores.CompareEncoded(encoded, bits, new ReadOnlySpan<byte>(_previous.Ptr, _previousLength), _previousBits) < 0;
        Remember(encoded);
        _previousBits = bits;
        return Probe(scope.Key, restart, out containerId);
    }

    private bool Find(ReadOnlySpan<byte> term, out long containerId)
    {
        // an unpositioned lookup, or a term that is smaller than the previous one, starts again from the root
        var restart = _previousLength < 0 || term.SequenceCompareTo(new ReadOnlySpan<byte>(_previous.Ptr, _previousLength)) < 0;
        Remember(term);

        using var scope = new CompactKeyCacheScope(_llt, term, _dictionaryId);
        return Probe(scope.Key, restart, out containerId);
    }

    private void Remember(ReadOnlySpan<byte> term)
    {
        if (_previous.HasValue == false || term.Length > _previous.Length)
        {
            if (_previous.HasValue)
                _searcher.Allocator.Release(ref _previous);
            _searcher.Allocator.Allocate(Math.Max(term.Length * 2, 256), out _previous);
        }

        term.CopyTo(new Span<byte>(_previous.Ptr, term.Length));
        _previousLength = term.Length;
    }

    private bool Probe(CompactKey key, bool restart, out long containerId)
    {
        if (_strategy == JoinKeyLookupStrategy.Cursor)
        {
            if (restart)
                _tree.InitializeStateForTryGetNextValue();
            return _tree.TryGetNextValue(key, out _, out containerId, out _);
        }

        key.EncodedWithCurrent(out _);
        if (restart)
        {
            _sweep.Seek(key);
            _sweepHasTerm = _sweep.MoveNext(_sweepKey.Key, out _sweepValue, out _);
        }

        var match = -1;
        while (_sweepHasTerm)
        {
            var treeTerm = _sweepKey.Key.EncodedWithCurrent(out var treeTermBits);
            match = key.CompareEncodedWithCurrent(ref MemoryMarshal.GetReference(treeTerm), treeTermBits);
            if (match <= 0)
                break;

            _sweepHasTerm = _sweep.MoveNext(_sweepKey.Key, out _sweepValue, out _);
        }

        containerId = _sweepValue;
        return _sweepHasTerm && match == 0;
    }

    public void Dispose()
    {
        if (_previous.HasValue)
            _searcher.Allocator.Release(ref _previous);

        if (_sweepOpen)
        {
            _sweepOpen = false;
            _sweepKey.Dispose();
        }

        _previousLength = -1;
    }
}
