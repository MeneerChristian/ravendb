using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Resident.ResidentOuterModel;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

/// <summary>
/// The model of the outer rows against the resident lists after every random write: rows with no, one or two keys (a quarter reachable through two), a hub key with many rows, outer delete, re-key, re-key to
/// and from the hub, the three behaviours of entry ids, eviction in the middle of a sequence, restarts, partial refreshes, a reader of an older snapshot, and the invariant that every resident list equals the
/// posting list of its key in the snapshot of the epoch after every step. AND and NOT of atoms are not this class's job: the contents of the unions are compared.
/// </summary>
public class ResidentCacheOracleTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    public enum Mode { Resident, ReadEveryThird, Tiny, Restart, AtomRate, FillHold, AtomRateFillHoldTiny }

    private const int Keys = 16, Docs = 400, Preloaded = 300, Steps = 140, Atoms = 6;

    public static IEnumerable<object[]> Seeds()
    {
        for (int seed = 1; seed <= 28; seed++)
            yield return [seed, (Mode)((seed - 1) % 7), (IdPolicy)(((seed - 1) / 7) % 3)];
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [MemberData(nameof(Seeds))]
    public void RandomWritesAgainstTheModel(int seed, Mode mode, IdPolicy policy)
    {
        var random = new Random(seed);
        var model = new ResidentOuterModel(policy, firstEntry: seed % 2 == 0 ? 1 : 5_000);
        var options = mode switch
        {
            Mode.Tiny => new ResidentCacheOptions { BudgetBytes = seed % 2 == 0 ? 300 : 700 },
            Mode.AtomRate => new ResidentCacheOptions { Admission = ResidentAdmission.AtomRate, AtomRateWindow = 10, AtomRateMinReads = 3 },
            Mode.FillHold => new ResidentCacheOptions { Admission = ResidentAdmission.FillHold, BudgetBytes = 1_200 },
            Mode.AtomRateFillHoldTiny => new ResidentCacheOptions { Admission = ResidentAdmission.AtomRate | ResidentAdmission.FillHold, AtomRateWindow = 10, AtomRateMinReads = 3, BudgetBytes = 2_500 },
            _ => new ResidentCacheOptions()
        };

        long[] allKeys = Enumerable.Range(0, Keys).Select(KeyRef).ToArray();
        string Doc(int i) => "d" + i;
        long[] RandomKeys()
        {
            if (random.Next(10) == 0)
                return [];

            int First() => random.Next(3) == 0 ? 0 : 1 + random.Next(Keys - 2); // key 0 is the hub, key 15 never has a row

            int first = First();
            if (random.Next(4) != 0)
                return KeyRefs(first);

            return KeyRefs(first, First());
        }

        model.Write(Enumerable.Range(0, Preloaded).Select(i => (Doc(i), RandomKeys())), []);
        bool readThrough = mode is Mode.AtomRate or Mode.FillHold or Mode.AtomRateFillHoldTiny;
        using var rig = new ResidentCacheRig(model, options, establish: false, warmUp: readThrough == false) { AllKeys = allKeys };
        rig.Establish();

        var atomKeys = new long[Atoms][];
        void DrawAtom(int a) => atomKeys[a] = a == 0 ? allKeys : allKeys.Where(_ => random.Next(3) == 0).ToArray();
        for (int a = 0; a < Atoms; a++)
            DrawAtom(a);

        int checks = 0, restarts = 0, partials = 0, severalSeen = 0, applied = 0, mismatches = 0;
        for (int step = 0; step < Steps; step++)
        {
            Snapshot previous = model.Current;
            int kind = random.Next(12);
            if (kind < 6)
            {
                var upserts = new List<(string, long[])>();
                var deletes = new List<string>();
                var taken = new HashSet<string>();
                for (int n = random.Next(1, 6); n > 0; n--)
                {
                    string doc = Doc(random.Next(Docs));
                    if (taken.Add(doc) == false)
                        continue;

                    if (model.Has(doc) && random.Next(4) == 0)
                        deletes.Add(doc);
                    else
                        upserts.Add((doc, RandomKeys()));
                }

                if (rig.Refresh(upserts, deletes) == ResidentPatchOutcome.Applied)
                    applied++;
                else
                    mismatches++;
            }
            else if (kind == 6)
            {
                model.Touch(); // a transaction that changes nothing: the readers' snapshot is not the epoch until the next refresh
            }
            else if (kind == 7 && mode == Mode.Restart)
            {
                rig.Restart();
                restarts++;
            }
            else if (kind == 8)
            {
                // a refresh cut in chunks: the first chunk is a prefix of the changes of a newer snapshot, the epoch goes, the readers use the store
                rig.Cache.Invalidate();
                model.Write(Enumerable.Range(0, 3).Select(_ => (Doc(random.Next(Docs)), RandomKeys())).DistinctBy(x => x.Item1), []);
                Assert.Equal(ResidentEpoch.None, rig.Cache.Epoch);
                rig.AssertUnion(model.Current, 1, atomKeys[1], $"seed {seed} step {step} between chunks");
                model.Write(Enumerable.Range(0, 3).Select(_ => (Doc(random.Next(Docs)), RandomKeys())).DistinctBy(x => x.Item1), []);
                rig.Establish();
                partials++;
            }

            if (random.Next(10) == 0)
                DrawAtom(1 + random.Next(Atoms - 1));

            if (mode == Mode.ReadEveryThird && step % 3 != 0)
                continue;

            string context = $"seed {seed} {mode} {policy} step {step}";
            for (int read = 0; read < 10; read++)
            {
                int atom = Math.Min(random.Next(Atoms), random.Next(Atoms));
                rig.AssertUnion(model.Current, atom, atomKeys[atom], context);
            }

            // a reader that opened its snapshot before the write
            rig.AssertUnion(previous, 2, atomKeys[2], context + " (reader of the previous snapshot)");
            rig.AssertUnion(model.Current, 7, allKeys, context + " (all keys)");

            rig.AssertUnion(model.Current, 8, [], context + " (no keys)");
            if (model.Current.AnyRowWithSeveralOf(allKeys))
                severalSeen++;

            checks += rig.AssertInvariant(context);
        }

        ResidentCacheStats stats = rig.Cache.GetStats();
        Output.WriteLine($"seed {seed} {mode} {policy}: applied {applied}, patch not applied {mismatches}, restarts {restarts}, partial refreshes {partials}, hits {stats.Hits}, misses {stats.Misses}, epoch misses {stats.EpochMisses}, inserts {stats.Inserts}, evictions {stats.Evictions}, rejected {stats.Rejected}, not admitted {stats.NotAdmitted}, patch add/remove {stats.PatchAdds}/{stats.PatchRemoves}, lists checked {checks}, peak {stats.PeakResidentBytes}");
        Assert.True(severalSeen > 0, "no row was ever reachable through two keys");
        Assert.True(stats.PatchAdds > 0 || mode == Mode.Restart, "no patch ever changed a resident list");
        Assert.True(stats.ResidentBytes <= rig.Cache.BudgetBytes);
        Assert.True(stats.PeakResidentBytes <= rig.Cache.BudgetBytes, $"peak {stats.PeakResidentBytes} above the budget {rig.Cache.BudgetBytes}");
        switch (mode)
        {
            case Mode.Resident:
            case Mode.ReadEveryThird:
                Assert.True(stats.Hits > 0 && checks > 0, "the cache was never read or never held a list");
                break;
            case Mode.Restart:
                Assert.True(checks > 0 && restarts > 0, "the cache never held a list or never restarted");
                break;
            case Mode.Tiny:
                Assert.True(stats.Evictions > 0 || stats.Rejected > 0, "the tiny budget never evicted");
                break;
            case Mode.AtomRate:
                Assert.True(stats.NotAdmitted > 0 && stats.Inserts > 0, $"atom rate admission never said both yes and no: not admitted {stats.NotAdmitted}, inserts {stats.Inserts}");
                break;
            case Mode.FillHold:
                Assert.True(stats.Inserts > 0 && stats.Rejected + stats.NotAdmitted > 0, $"fill-hold: inserts {stats.Inserts}, rejected {stats.Rejected}, not admitted {stats.NotAdmitted}");
                break;
        }
    }
}
