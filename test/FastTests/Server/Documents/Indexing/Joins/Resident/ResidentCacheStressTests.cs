using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Sparrow.LowMemory;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

/// <summary>
/// A writer that patches, invalidates and re-warms the cache while two readers read it. A reader that gets the lease for the version it knows must see exactly the lists of that version: a list that is
/// half patched, or a list of another version, is a torn read.
/// </summary>
public unsafe class ResidentCacheStressTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    private const int Keys = 8, Universe = 12_000, Versions = 120, Readers = 2;
    private static readonly Guid Env = Guid.NewGuid();

    private static long KeyRefOf(int key) => 1_000 + key * 11L;

    private static ResidentEpoch EpochOf(int version) => new(Env, 1_000 + version, version);

    private static uint Mix(uint a, uint b, uint c)
    {
        uint h = a * 0x9E3779B1u ^ b * 0x85EBCA6Bu ^ c * 0xC2B2AE35u;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        return h;
    }

    // the keys of every entry in a version: a bit per key
    private static byte[][] BuildVersions()
    {
        var versions = new byte[Versions + 1][];
        versions[0] = new byte[Universe];
        for (int id = 0; id < Universe; id++)
        {
            for (int key = 0; key < Keys; key++)
            {
                if (Mix((uint)id, (uint)key, 0) % 2 == 0)
                    versions[0][id] |= (byte)(1 << key);
            }
        }

        for (int v = 1; v <= Versions; v++)
        {
            versions[v] = (byte[])versions[v - 1].Clone();
            for (int id = 0; id < Universe; id++)
            {
                for (int key = 0; key < Keys; key++)
                {
                    if (Mix((uint)id, (uint)key, (uint)v) % 12 == 0)
                        versions[v][id] ^= (byte)(1 << key);
                }
            }
        }

        return versions;
    }

    private static long[] ListOf(byte[] version, int key)
    {
        var list = new List<long>();
        for (int id = 0; id < Universe; id++)
        {
            if ((version[id] & (1 << key)) != 0)
                list.Add(id + 1); // entry ids start at 1
        }

        return list.ToArray();
    }

    private static long[] KeysOf(byte mask)
    {
        var keys = new List<long>();
        for (int key = 0; key < Keys; key++)
        {
            if ((mask & (1 << key)) != 0)
                keys.Add(KeyRefOf(key));
        }

        return keys.ToArray();
    }

    private sealed class VersionSource(long[][] lists) : IResidentListSource
    {
        public long CountOf(long keyRef) => lists[(keyRef - 1_000) / 11].Length;

        public ReadOnlySpan<long> Read(long keyRef) => lists[(keyRef - 1_000) / 11];
    }

    private readonly struct SourceBox(VersionSource source) : IResidentListSource
    {
        public long CountOf(long keyRef) => source.CountOf(keyRef);

        public ReadOnlySpan<long> Read(long keyRef) => source.Read(keyRef);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ReadersNeverSeeATornListWhilePatchesInvalidationsAndLowMemoryRun()
    {
        byte[][] versions = BuildVersions();
        long[][][] lists = new long[Versions + 1][][]; // lists by version, key
        for (int v = 0; v <= Versions; v++)
            lists[v] = Enumerable.Range(0, Keys).Select(k => ListOf(versions[v], k)).ToArray();

        var registry = new ResidentCacheRegistry(64L << 20, registerWithLowMemoryNotification: false);
        using var cache = new ResidentKeyListCache(new ResidentCacheOptions { MaxPatchDocuments = 1_000_000 }, registry);
        long[] allKeys = Enumerable.Range(0, Keys).Select(KeyRefOf).ToArray();
        Assert.True(cache.TryEstablish(EpochOf(0), Universe));
        var first = new SourceBox(new VersionSource(lists[0]));
        Assert.Equal(Keys, cache.WarmUp(EpochOf(0), allKeys, ref first, default).Loaded);

        int published = 0;
        bool done = false;
        long valid = 0, invalid = 0, unions = 0;
        string failure = null;
        var readers = new List<Thread>();
        for (int r = 0; r < Readers; r++)
        {
            int reader = r;
            readers.Add(new Thread(() =>
            {
                var words = new ulong[(Universe >> 6) + 2];
                var random = new Random(reader);
                try
                {
                    while (Volatile.Read(ref done) == false && failure == null)
                    {
                        int v = Volatile.Read(ref published);
                        if (random.Next(4) == 0)
                        {
                            // the whole protocol: a hit reads the cache, a miss reads the store of the same version
                            Array.Clear(words);
                            byte mask = (byte)(1 + random.Next(255));
                            long[] keys = KeysOf(mask);
                            var source = new SourceBox(new VersionSource(lists[v]));
                            fixed (ulong* w = words)
                                cache.UnionKeys(Env, 1_000 + v, mask, keys, ref source, w, words.Length);

                            if (Same(words, versions[v], mask) == false)
                                failure = $"reader {reader}: union of the keys {mask} at version {v} is not the version";

                            Interlocked.Increment(ref unions);
                            continue;
                        }

                        using ResidentKeyListCache.ReadLease lease = cache.TryBeginRead(Env, 1_000 + v);
                        if (lease.IsValid == false)
                        {
                            Interlocked.Increment(ref invalid);
                            continue;
                        }

                        for (int key = 0; key < Keys; key++)
                        {
                            Array.Clear(words);
                            bool hit;
                            fixed (ulong* w = words)
                                hit = lease.TryOrInto(KeyRefOf(key), w, words.Length, out _);

                            if (hit == false)
                                continue; // dropped by low memory or not warmed yet

                            if (Same(words, versions[v], (byte)(1 << key)) == false || lease.CountOf(KeyRefOf(key)) != lists[v][key].Length)
                                failure = $"reader {reader}: the list of key {key} at version {v} is torn";
                        }

                        Interlocked.Increment(ref valid);
                    }
                }
                catch (Exception e)
                {
                    failure = $"reader {reader}: {e}";
                }
            }));
        }

        foreach (Thread t in readers)
            t.Start();

        try
        {
            for (int v = 0; v < Versions && failure == null; v++)
            {
                using (var patch = new ResidentPatch())
                {
                    for (int id = 0; id < Universe; id++)
                    {
                        if (versions[v][id] != versions[v + 1][id])
                            patch.ChangeRow(id + 1, KeysOf(versions[v][id]), id + 1, KeysOf(versions[v + 1][id]));
                    }

                    Assert.Equal(ResidentPatchOutcome.Applied, cache.ApplyPatch(EpochOf(v), EpochOf(v + 1), Universe, patch));
                }

                Volatile.Write(ref published, v + 1);
                var source = new SourceBox(new VersionSource(lists[v + 1]));
                if (v % 30 == 29)
                {
                    // a partial refresh somewhere: no epoch, then the complete one establishes it again and warms up
                    cache.Invalidate();
                    Assert.True(cache.TryEstablish(EpochOf(v + 1), Universe));
                    cache.WarmUp(EpochOf(v + 1), allKeys, ref source, default);
                }
                else if (v % 45 == 44)
                {
                    registry.LowMemory(LowMemorySeverity.Low);
                    registry.LowMemoryOver();
                    cache.WarmUp(EpochOf(v + 1), allKeys, ref source, default);
                }
            }
        }
        finally
        {
            Volatile.Write(ref done, true);
            foreach (Thread t in readers)
                Assert.True(t.Join(TimeSpan.FromSeconds(120)), "a reader is stuck");
        }

        Output.WriteLine($"valid leases {valid}, epoch misses {invalid}, unions {unions}, stats {cache.GetStats()}");
        Assert.Null(failure);
        Assert.True(valid > 100 && unions > 100, $"the readers hardly ran: {valid} leases, {unions} unions");
    }

    private static bool Same(ulong[] words, byte[] version, byte mask)
    {
        int expected = 0;
        for (int id = 0; id < Universe; id++)
        {
            bool should = (version[id] & mask) != 0;
            bool is_ = (words[(id + 1) >> 6] & (1UL << ((id + 1) & 63))) != 0;
            if (should != is_)
                return false;

            if (should)
                expected++;
        }

        int count = 0;
        foreach (ulong word in words)
            count += BitOperations.PopCount(word);

        return count == expected;
    }
}
