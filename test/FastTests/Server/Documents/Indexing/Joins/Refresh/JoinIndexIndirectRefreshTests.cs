using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>What is particular to the refresh of an Indirect file: the outer side touches no list, a transition of a key is one list change, the inner side is discovered first and a cut on the outer side is not a partial state of the lists.</summary>
public class JoinIndexIndirectRefreshTests(ITestOutputHelper output) : RavenTestBase(output), IIndirectModeTest
{
    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 1000 && condition() == false; i++)
            await Task.Delay(20);

        Assert.True(condition(), what);
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(1_000, false)]
    public async Task TheRuleThatBuildsTheListsAgainCountsOneChangePerTransition(int limit, bool rebuilt)
    {
        // 3 outer entries with a key: 200 inner documents that change their country are 400 transitions, more than 1 for each of the 3 entries
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.MaxInnerListChangesPerOuterEntry)] = limit.ToString()
        });
        h.Save(s =>
        {
            for (int i = 0; i < 200; i++)
                s.Store(new JoinIndexHarness.Context { Code = "k" + i, Country = "a", Name = "n", Rank = 1, IsActive = true, Title = "t" }, $"contexts/{i}");

            for (int i = 0; i < 3; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + i, Title = "t" }, $"conversations/{i}");
        });
        await h.AssertCurrentAsync();
        JoinIndex before = h.Join();

        h.Save(s =>
        {
            for (int i = 0; i < 200; i++)
                s.Store(new JoinIndexHarness.Context { Code = "k" + i, Country = "b", Name = "n", Rank = 1, IsActive = true, Title = "t" }, $"contexts/{i}");
        });

        if (rebuilt)
        {
            await WaitForAsync(() => h.Join() is { } now && ReferenceEquals(now, before) == false, "the join index was reset");
            Assert.Contains(nameof(JoinRebuildReason.InnerChangesTooCostly), before.Counters.RebuildsByReason.Keys);
            await h.AssertCurrentAsync();
            Assert.Equal(JoinStorageMode.Indirect, h.Join().Plan.BuildMode);
        }
        else
        {
            await h.AssertCurrentAsync();
            Assert.True(ReferenceEquals(h.Join(), before), "the join index was reset");
            Assert.DoesNotContain(before.Counters.RebuildsByReason.Keys, x => x != nameof(JoinRebuildReason.Initial));
            Assert.True(before.Counters.KeyListChanges >= 400, $"{before.Counters.KeyListChanges} key list changes");
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheAtomsOfAKeyThatChangeAreWrittenToItsListOnceWhateverTheNumberOfTransitions()
    {
        const int keys = 30;
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this);
        h.Save(s =>
        {
            for (int i = 0; i < 10; i++)
                s.Store(new JoinIndexHarness.Context { Code = "k" + i, Country = "nl", Name = "n", Rank = i, IsActive = true, Title = "t" }, $"contexts/{i}");

            for (int i = 0; i < 40; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + i % 20, Title = "t" }, $"conversations/{i}");
        });
        await h.AssertCurrentAsync();
        JoinCounters counters = h.Join().Counters;
        long changes = counters.KeyListChanges;
        long writes = counters.KeyAtomUpdates;

        // every new key takes the any atom, the atoms of the terms of its fields, and the one of several inner documents when two documents share it: more than one transition for each key
        h.Save(s =>
        {
            for (int i = 0; i < keys; i++)
            {
                s.Store(new JoinIndexHarness.Context { Code = "new" + i, Country = "c" + i % 3, Name = "m" + i % 5, Rank = 100 + i, IsActive = i % 2 == 0, Title = "t" }, $"contexts/{100 + i}");
                s.Store(new JoinIndexHarness.Context { Code = "new" + i, Country = "d" + i % 3, Name = "m" + i % 5, Rank = 200 + i, IsActive = i % 2 == 1, Title = "t" }, $"contexts/{200 + i}");
            }
        });
        await h.AssertCurrentAsync();

        long transitions = counters.KeyListChanges - changes;
        Assert.True(transitions >= 3 * keys, $"{transitions} transitions");
        Assert.Equal(keys, counters.KeyAtomUpdates - writes);
        Assert.True(JoinFileConsistencyChecker.Check(h.Join(), 40, 230).Ok);
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(30)]
    [InlineData(0)]
    public async Task TheCountOfAPairIsLookedUpOnceByTheEstimateAndTheWriteTogether(int limit)
    {
        const int keys = 25;
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.MaxInnerListChangesPerOuterEntry)] = limit.ToString()
        });
        h.Save(s =>
        {
            for (int i = 0; i < 10; i++)
                s.Store(new JoinIndexHarness.Context { Code = "k" + i, Country = "nl", Name = "n", Rank = i, IsActive = true, Title = "t" }, $"contexts/{i}");

            for (int i = 0; i < 20; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + i % 15, Title = "t" }, $"conversations/{i}");
        });
        await h.AssertCurrentAsync();
        JoinCounters counters = h.Join().Counters;
        long changes = counters.KeyListChanges;
        long lookups = counters.PairCountLookups;

        // new keys: every pair of a key and an atom goes from no inner entry to one
        h.Save(s =>
        {
            for (int i = 0; i < keys; i++)
                s.Store(new JoinIndexHarness.Context { Code = "new" + i, Country = "c" + i % 3, Name = "m" + i % 5, Rank = 100 + i, IsActive = i % 2 == 0, Title = "t" }, $"contexts/{100 + i}");
        });
        await h.AssertCurrentAsync();

        long pairs = counters.KeyListChanges - changes;
        Assert.True(pairs >= 3 * keys, $"{pairs} pairs");

        // the estimate reads the count of every pair, and the write that follows uses what it read; without the estimate (a limit of 0) the write reads it itself
        Assert.Equal(pairs, counters.PairCountLookups - lookups);
        Assert.True(JoinFileConsistencyChecker.Check(h.Join(), 20, 125).Ok);

        // the counts are right: the keys leave when their documents do
        h.Save(s =>
        {
            for (int i = 0; i < keys; i++)
                s.Delete($"contexts/{100 + i}");
        });
        await h.AssertCurrentAsync();
        Assert.True(JoinFileConsistencyChecker.Check(h.Join(), 20, 125).Ok);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnOuterEntryThatKeepsItsIdAndChangesItsKeysIsReadOnceNotTwice()
    {
        const int moved = 40;
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this);
        h.Save(s =>
        {
            for (int i = 0; i < 10; i++)
                s.Store(new JoinIndexHarness.Context { Code = "k" + i, Country = "nl", Name = "n", Rank = i, IsActive = true, Title = "t" }, $"contexts/{i}");

            for (int i = 0; i < 100; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + i % 10, Title = "t" }, $"conversations/{i}");
        });
        await h.AssertCurrentAsync();
        JoinCounters counters = h.Join().Counters;
        long reads = counters.OuterKeyReads;

        // the documents are written again with another key that the file knows: the index keeps their entries, so the classification reads each entry and the row is added with what it read
        h.Save(s =>
        {
            for (int i = 0; i < moved; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + (i + 3) % 10, Title = "t" }, $"conversations/{i}");
        });
        await h.AssertCurrentAsync();
        Assert.Equal(moved, counters.OuterKeyReads - reads);
        Assert.True(JoinFileConsistencyChecker.Check(h.Join(), 100, 10).Ok);

        // a key that no document of the inner index has and the file does not know yet is read again, because it has to be added
        reads = counters.OuterKeyReads;
        h.Save(s =>
        {
            for (int i = 0; i < moved; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "unknown" + i, Title = "t" }, $"conversations/{i}");
        });
        await h.AssertCurrentAsync();
        Assert.Equal(2 * moved, counters.OuterKeyReads - reads);
        Assert.True(JoinFileConsistencyChecker.Check(h.Join(), 100, 10).Ok);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnOuterChangeMakesNoChangeOfAList()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this);
        h.Save(s =>
        {
            for (int i = 0; i < 30; i++)
                s.Store(new JoinIndexHarness.Context { Code = "k" + i % 10, Country = i % 2 == 0 ? "nl" : "de", Name = "n" + i % 3, Rank = i, IsActive = i % 2 == 0, Title = "red fox" }, $"contexts/{i}");

            for (int i = 0; i < 100; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + i % 12, Title = "t" }, $"conversations/{i}");
        });
        await h.AssertCurrentAsync();
        JoinCounters counters = h.Join().Counters;
        long updates = counters.ListUpdates;
        long changes = counters.KeyListChanges;
        long refreshes = counters.Refreshes;

        // inserts, deletes, a key that moves and a document that is written without a change of its key
        h.Save(s =>
        {
            for (int i = 0; i < 100; i += 2)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + (i + 5) % 12, Title = "u" }, $"conversations/{i}");

            for (int i = 1; i < 100; i += 4)
                s.Delete($"conversations/{i}");

            for (int i = 200; i < 230; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "new" + i % 4, Title = "t" }, $"conversations/{i}");
        });
        await h.AssertCurrentAsync();

        Assert.True(counters.Refreshes > refreshes);
        Assert.Equal(updates, counters.ListUpdates);
        Assert.Equal(changes, counters.KeyListChanges);
        Assert.True(JoinFileConsistencyChecker.Check(h.Join(), 230, 30).Ok);

        // a key of the new conversations that an inner document takes later: the lists change then, by one key
        h.PutCodedContext(500, "new1", "fr", "n", 1, true, "t");
        await h.AssertCurrentAsync();
        Assert.True(counters.KeyListChanges > changes);
        Assert.True(JoinFileConsistencyChecker.Check(h.Join(), 230, 600).Ok);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheInnerSideIsDiscoveredFirstAndACutOnTheOuterSideIsNotAPartialStateOfTheLists()
    {
        const int budget = 30;
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = budget.ToString() });
        h.Save(s =>
        {
            for (int i = 0; i < 20; i++)
                s.Store(new JoinIndexHarness.Context { Code = "k" + i, Country = "nl", Name = "n", Rank = 1, IsActive = true, Title = "t" }, $"contexts/{i}");

            for (int i = 0; i < 150; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + i % 25, Title = "t" }, $"conversations/{i}");
        });
        await h.AssertCurrentAsync();
        JoinIndex joinIndex = h.Join();

        await h.Store.Maintenance.SendAsync(new StopIndexOperation(JoinIndexHarness.JoinName));
        await WaitForAsync(() => h.Stats().Status == IndexRunningStatus.Paused, "the join index is paused");
        h.Save(s =>
        {
            for (int i = 0; i < 20; i++)
                s.Store(new JoinIndexHarness.Context { Code = "k" + i, Country = "de", Name = "n", Rank = 1, IsActive = true, Title = "t" }, $"contexts/{i}");

            for (int i = 0; i < 100; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + (i + 3) % 25, Title = "u" }, $"conversations/{i}");
        });
        await WaitForAsync(() => h.Store.Maintenance.Send(new GetIndexesStatisticsOperation()).Where(x => x.Name != JoinIndexHarness.JoinName).All(x => x.IsStale == false), "the upstream indexes are current");

        using var gate = new JoinIndexChunkTests.Gate(joinIndex);
        await h.Store.Maintenance.SendAsync(new StartIndexOperation(JoinIndexHarness.JoinName));

        int chunks = 0;
        bool sawOuterOnly = false;
        while (gate.WaitForChunk(TimeSpan.FromSeconds(5)))
        {
            chunks++;
            Assert.True(joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot file));
            using (file)
            {
                JoinIndexGeneration generation = file.Generation;
                Assert.Equal(JoinStorageMode.Indirect, generation.Mode);
                if (chunks >= 2)
                {
                    // the 20 inner documents fit the budget of the first commit: the inner side is complete from there on, whatever is left of the outer side
                    Assert.False(generation.InnerPartial, generation.ToString());
                    if (generation.OuterPartial)
                        sawOuterOnly = true;
                }

                JoinFileConsistencyChecker.Result check = JoinFileConsistencyChecker.Check(joinIndex, 150, 20);
                Assert.True(check.Ok, $"after {chunks - 1} commits: {check}");
            }

            gate.Resume();
        }

        gate.Open();
        await h.AssertCurrentAsync();
        Assert.True(sawOuterOnly, "no commit was cut on the outer side alone");
        Assert.True(chunks >= 3, $"{chunks} chunks");
        Assert.True(ReferenceEquals(h.Join(), joinIndex), "the join index was reset");
        Assert.DoesNotContain(joinIndex.Counters.RebuildsByReason.Keys, x => x != nameof(JoinRebuildReason.Initial));
        Assert.False(joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot last) == false);
        using (last)
        {
            Assert.False(last.Generation.IsPartial);
            Assert.False(last.Generation.OuterPartial);
        }
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 40)]
    [InlineData(4, 40)]
    public async Task ARandomWorkloadOfRowsWithSeveralKeysAndGroupsOfSeveralEntriesStaysExact(int seed, int budget)
    {
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.MaxInnerListChangesPerOuterEntry)] = "0" };
        if (budget > 0)
            settings[RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = budget.ToString();

        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, settings, arrays: true);
        var random = new Random(seed);
        const int Rows = 60;
        const int Docs = 20;

        string[] Codes(int max) => Enumerable.Range(0, random.Next(0, max)).Select(_ => JoinIndexHarness.RandomCode(random, 6)).Where(x => string.IsNullOrEmpty(x) == false).Distinct().ToArray();

        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < 40; i++)
            {
                int kind = random.Next(100);
                if (kind < 35)
                    h.PutConversationKeys(random.Next(Rows), Codes(4));
                else if (kind < 45)
                    h.Delete($"conversations/{random.Next(Rows)}");
                else if (kind < 80)
                    h.PutCodedContextKeys(random.Next(Docs), Codes(4), random.Next(3) == 0 ? "nl" : "c" + random.Next(3), "n" + random.Next(3), random.Next(20), random.Next(2) == 0, "w" + random.Next(4));
                else if (kind < 88)
                    h.Delete($"contexts/{random.Next(Docs)}");
                else
                {
                    // two group mates swap an atom in one batch: the group does not change
                    int a = random.Next(Docs), b = random.Next(Docs);
                    string[] keys = Codes(3);
                    h.Save(s =>
                    {
                        s.Store(new JoinIndexHarness.Context { Codes = keys, Country = "swap1", Name = "n", Rank = 1, IsActive = true, Title = "t" }, $"contexts/{a}");
                        s.Store(new JoinIndexHarness.Context { Codes = keys, Country = "swap2", Name = "n", Rank = 1, IsActive = true, Title = "t" }, $"contexts/{(b == a ? (a + 1) % Docs : b)}");
                    });
                }
            }

            await h.AssertCurrentAsync();
            JoinFileConsistencyChecker.Result check = JoinFileConsistencyChecker.Check(h.Join(), Rows, Docs);
            Assert.True(check.Ok, $"seed {seed} round {round}: {check}");
        }
    }
}
