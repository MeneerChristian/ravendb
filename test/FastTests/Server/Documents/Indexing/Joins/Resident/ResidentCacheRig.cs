using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Sparrow.Utils;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Resident.ResidentOuterModel;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

/// <summary>The store of the cache over one snapshot of the model: counts what was read from it.</summary>
internal readonly struct SnapshotSource(Snapshot snapshot, long[] reads) : IResidentListSource
{
    public long CountOf(long keyRef) => snapshot.ListOf(keyRef).Length;

    public ReadOnlySpan<long> Read(long keyRef)
    {
        reads[0]++;
        return snapshot.ListOf(keyRef);
    }
}

/// <summary>
/// What step 7 will do around the cache, in the order of the design: collect the patch while the refresh runs, apply it after the commit with the epoch the refresh started from, establish the epoch and warm
/// up when the patch does not apply, and read with the snapshot of the reader.
/// </summary>
internal sealed unsafe class ResidentCacheRig : IDisposable
{
    private readonly ResidentCacheOptions _options;
    private readonly ResidentCacheRegistry _registry;
    private long _generation;

    public ResidentCacheRig(ResidentOuterModel model, ResidentCacheOptions options, ResidentCacheRegistry registry = null, bool establish = true, bool warmUp = true)
    {
        Model = model;
        WarmUpOnEstablish = warmUp;
        _options = options;
        _registry = registry;
        Cache = new ResidentKeyListCache(options, registry);
        if (establish)
            Establish();
    }

    public ResidentOuterModel Model { get; }

    public ResidentKeyListCache Cache { get; private set; }

    public long[] AllKeys { get; set; } = [];

    public bool WarmUpOnEstablish { get; set; }

    private readonly long[] _reads = new long[1];

    public ResidentEpoch CurrentEpoch => Cache.Epoch;

    public ResidentEpoch EpochOf(Snapshot snapshot) => new(snapshot.EnvironmentId, snapshot.TransactionId, _generation);

    /// <summary>The complete refresh that ends the outer side: the epoch is the snapshot, the lists are loaded in the order of the keys until the budget is full.</summary>
    public ResidentWarmUpResult Establish()
    {
        Snapshot snapshot = Model.Current;
        _generation++;
        if (Cache.TryEstablish(EpochOf(snapshot), snapshot.LastEntryId) == false)
            return default;

        if (WarmUpOnEstablish == false)
            return default;

        var source = new SnapshotSource(snapshot, _reads);
        return Cache.WarmUp(EpochOf(snapshot), AllKeys, ref source, CancellationToken.None);
    }

    /// <summary>The write of a refresh: the model changes, the patch of the changes is applied after the commit; when it does not apply the cache is established again at the new snapshot.</summary>
    public ResidentPatchOutcome Refresh(IEnumerable<(string Doc, long[] Keys)> upserts, IEnumerable<string> deletes)
    {
        ResidentEpoch before = Cache.Epoch;
        List<RowChange> changes = Model.Write(upserts, deletes);
        using ResidentPatch patch = PatchOf(changes);
        return Apply(before, patch);
    }

    public ResidentPatchOutcome Apply(ResidentEpoch before, ResidentPatch patch)
    {
        Snapshot snapshot = Model.Current;
        _generation++;
        ResidentPatchOutcome outcome = Cache.ApplyPatch(before, EpochOf(snapshot), snapshot.LastEntryId, patch);
        if (outcome != ResidentPatchOutcome.Applied)
            Establish();

        return outcome;
    }

    /// <summary>Restart: nothing is durable, the cache is empty and has no epoch.</summary>
    public void Restart()
    {
        Cache.Dispose();
        Cache = new ResidentKeyListCache(_options, _registry);
    }

    /// <summary>The union of the lists of the keys of an atom as a reader of the snapshot reads it, in the sorted entry ids.</summary>
    public long[] Union(Snapshot snapshot, long atomCode, long[] keys, out ResidentUnion union)
    {
        int wordCount = (int)((snapshot.LastEntryId >> 6) + 2);
        var words = (ulong*)NativeMemory.AllocateMemory((long)wordCount * sizeof(ulong));
        try
        {
            new Span<ulong>(words, wordCount).Clear();
            var source = new SnapshotSource(snapshot, _reads);
            union = Cache.UnionKeys(snapshot.EnvironmentId, snapshot.TransactionId, atomCode, keys, ref source, words, wordCount);
            var ids = new List<long>();
            for (int w = 0; w < wordCount; w++)
            {
                ulong word = words[w];
                while (word != 0)
                {
                    ids.Add((w << 6) + System.Numerics.BitOperations.TrailingZeroCount(word));
                    word &= word - 1;
                }
            }

            return ids.ToArray();
        }
        finally
        {
            NativeMemory.Free((byte*)words, (long)wordCount * sizeof(ulong));
        }
    }

    public long ReadsFromStore => _reads[0];

    /// <summary>The union against the model, and the flag of the row that is reachable through two of the keys.</summary>
    public void AssertUnion(Snapshot snapshot, long atomCode, long[] keys, string context)
    {
        long[] actual = Union(snapshot, atomCode, keys, out ResidentUnion union);
        long[] expected = snapshot.RowsOfAny(keys);
        Assert.True(expected.SequenceEqual(actual), $"{context}: union of [{string.Join(",", keys)}] expected [{string.Join(",", expected)}] actual [{string.Join(",", actual)}] epochHit {union.EpochHit}");
        bool several = snapshot.AnyRowWithSeveralOf(keys);
        Assert.True(several == union.EntryInSeveralKeys, $"{context}: entry in several keys expected {several} actual {union.EntryInSeveralKeys}");
        Assert.Equal(keys.Length, union.KeysFromCache + union.KeysFromStore);
    }

    /// <summary>The invariant: with an epoch every resident list equals the posting list of its key in the snapshot of the epoch; without one nothing is resident. Returns the number of lists checked.</summary>
    public int AssertInvariant(string context)
    {
        ResidentEpoch epoch = Cache.Epoch;
        if (epoch.IsNone)
        {
            Assert.True(Cache.ResidentKeys == 0, $"{context}: no epoch but {Cache.ResidentKeys} resident lists");
            return 0;
        }

        Snapshot snapshot = Model.History[epoch.TransactionId];
        int checks = 0;
        foreach (long key in AllKeys)
        {
            if (Cache.TryGetList(key, out long[] list) == false)
                continue;

            long[] expected = snapshot.ListOf(key);
            Assert.True(expected.SequenceEqual(list), $"{context}: key {key} resident [{string.Join(",", list)}] expected [{string.Join(",", expected)}] at transaction {epoch.TransactionId}");
            checks++;
        }

        Assert.Equal(checks, Cache.ResidentKeys);
        return checks;
    }

    public void Dispose() => Cache.Dispose();
}
