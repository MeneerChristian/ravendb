using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>
/// The outer key field holds an array of ids. The lists of the join index (the terms, the groupings and the any list hold an outer entry when ANY of the documents it points to has the atom),
/// the keys of each entry and the number of entries with several joined documents equal a scan of the two snapshots after every refresh.
/// </summary>
public class JoinIndexMultiKeyRefreshTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static Task<JoinIndexHarness> CreateAsync(RavenTestBase test, bool putJoin = true)
    {
        return JoinIndexHarness.CreateAsync(test, putJoin: putJoin, indexes: [JoinIndexHarness.MultiOuterIndex(), JoinIndexHarness.InnerIndex()]);
    }

    private static void SeedContexts(JoinIndexHarness h)
    {
        h.PutContext(1, "nl", "Alpha", 10, true, "red fox");
        h.PutContext(2, "nl", "Beta", 20, false, "blue whale");
        h.PutContext(3, "de", "Gamma", 30, true, "red panda");
        h.PutContext(4, "be", "Delta", 40, false, "green");
    }

    private static void SeedConversations(JoinIndexHarness h)
    {
        h.PutConversationKeys(1, ["contexts/1", "contexts/2"]);
        h.PutConversationKeys(2, ["contexts/3"]);
        h.PutConversationKeys(3, ["contexts/1", "contexts/3", "contexts/4"]);
        h.PutConversationKeys(4, ["contexts/99", "contexts/2"]);
        h.PutConversationKeys(5, ["contexts/1", "CONTEXTS/1", "contexts/1"]);
        h.PutConversationKeys(6, [null, "contexts/4"]);
        h.PutConversationKeys(7, []);
        h.PutConversationKeys(8, null);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task BuildWithArraysOfKeysMatchesAScanOfTheSnapshots()
    {
        using JoinIndexHarness h = await CreateAsync(this, putJoin: false);
        SeedContexts(h);
        SeedConversations(h);
        await h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition());

        JoinIndexInvariantChecker.Result result = await h.CheckAsync();
        Assert.True(result.Ok, result.ToString());
        Assert.Equal(1, h.Stats().Join.Builds);
        Assert.Equal(IndexState.Normal, h.Stats().State);
        Assert.False(h.Stats().IsStale);
        Assert.Equal(6, result.Joined);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ArraysOfKeysThatAreAddedToTheIndexAfterTheBuildMatchAScan()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        SeedContexts(h);
        await h.AssertCurrentAsync();

        SeedConversations(h);
        await h.AssertCurrentAsync();
        Assert.Equal(1, h.Stats().Join.Builds);
        Assert.Equal(IndexState.Normal, h.Stats().State);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AKeyIsAddedToAndRemovedFromAnArrayWithoutDisturbingTheOthers()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        SeedContexts(h);
        SeedConversations(h);
        await h.AssertCurrentAsync();

        // grow: one key, two keys, several, then drop them one at a time
        string[][] steps =
        [
            ["contexts/1"],
            ["contexts/1", "contexts/2"],
            ["contexts/1", "contexts/2", "contexts/3"],
            ["contexts/1", "contexts/3"],
            ["contexts/3"],
            ["contexts/3", "contexts/99"],
            ["contexts/4", "contexts/3"],
            [],
            ["contexts/2", "contexts/2"],
            null,
            ["contexts/2", "contexts/4"]
        ];

        foreach (string[] keys in steps)
        {
            h.PutConversationKeys(2, keys);
            await h.AssertCurrentAsync();
        }

        Assert.Equal(1, h.Stats().Join.Builds);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnEntryStaysInAListWhileAnotherJoinedDocumentHasTheAtom()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        // contexts 1 and 2 are both nl, both active, both have the name n
        h.PutContext(1, "nl", "n", 1, true, "a");
        h.PutContext(2, "nl", "n", 2, true, "b");
        h.PutContext(3, "de", "m", 3, false, "c");
        h.PutConversationKeys(1, ["contexts/1", "contexts/2"]);
        h.PutConversationKeys(2, ["contexts/1", "contexts/2", "contexts/3"]);
        await h.AssertCurrentAsync();

        // the first of the two leaves nl and the grouping: the entries stay in both through the other
        h.PutContext(1, "be", "other", 1, false, "a");
        await h.AssertCurrentAsync();

        // the other one leaves too: now they go
        h.PutContext(2, "fr", "n", 2, false, "b");
        await h.AssertCurrentAsync();

        // one comes back, the other one is deleted
        h.PutContext(1, "nl", "n", 1, true, "a");
        h.Delete("contexts/2");
        await h.AssertCurrentAsync();

        // the key that is dropped from the array takes the atoms with it, the key that stays keeps its own
        h.PutContext(2, "nl", "n", 2, true, "b");
        await h.AssertCurrentAsync();
        h.PutConversationKeys(1, ["contexts/2"]);
        await h.AssertCurrentAsync();
        h.PutConversationKeys(2, ["contexts/3", "contexts/2"]);
        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ADocumentThatIsCreatedForAKeyJoinsTheEntriesThatWaitedForIt()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutContext(1, "nl", "n", 1, true, "a");
        h.PutConversationKeys(1, ["contexts/1", "contexts/7", "contexts/8"]);
        h.PutConversationKeys(2, ["contexts/7", "contexts/8"]);
        h.PutConversationKeys(3, ["contexts/7"]);
        await h.AssertCurrentAsync();

        // both documents that entry 1 and 2 wait for appear in one batch, and one more appears later
        h.PutContext(7, "nl", "s", 7, true, "x");
        h.PutContext(8, "de", "t", 8, false, "y");
        await h.AssertCurrentAsync();
        h.Delete("contexts/7");
        await h.AssertCurrentAsync();
        h.PutContext(7, "be", "u", 7, false, "z");
        h.Delete("contexts/8");
        await h.AssertCurrentAsync();
        h.PutContext(8, "nl", "s", 7, true, "x");
        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ADocumentThatIsDeletedAndCreatedAgainInOneBatchKeepsItsEntries()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        SeedContexts(h);
        SeedConversations(h);
        await h.AssertCurrentAsync();

        h.Save(s =>
        {
            s.Delete("contexts/1");
            s.Delete("contexts/3");
        });
        h.PutContext(1, "nl", "Alpha", 10, true, "red fox");
        h.PutContext(3, "fr", "Gamma", 31, false, "back");
        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ManyChangesOfBothSidesInOneBatchMatchAScan()
    {
        using JoinIndexHarness h = await CreateAsync(this, putJoin: false);
        SeedContexts(h);
        SeedConversations(h);
        await h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition());
        await h.AssertCurrentAsync();

        var random = new Random(11);
        for (int round = 0; round < 6; round++)
        {
            // many documents of both sides change between two refreshes
            h.Save(s =>
            {
                foreach (int id in Enumerable.Range(0, 50).OrderBy(_ => random.Next()).Take(40))
                    s.Store(new JoinIndexHarness.Conversation { ContextIds = JoinIndexHarness.RandomKeys(random, 10), Title = "t" }, $"conversations/{id}");

                foreach (int id in Enumerable.Range(0, 10).OrderBy(_ => random.Next()).Take(7))
                    s.Store(new JoinIndexHarness.Context { Country = "c" + random.Next(3), Name = "n" + random.Next(3), Rank = random.Next(5), IsActive = random.Next(2) == 0, Title = "w" + random.Next(3) }, $"contexts/{id}");
            });

            await h.AssertCurrentAsync();
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AHubWithManyEntriesThatHaveSeveralKeysStaysExact()
    {
        using JoinIndexHarness h = await CreateAsync(this, putJoin: false);
        h.PutContext(0, "nl", "Hub", 1, true, "hub");
        h.PutContext(1, "nl", "Side", 1, true, "side");
        h.PutContext(2, "de", "Other", 1, false, "other");
        h.Save(s =>
        {
            for (int i = 0; i < 300; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextIds = i % 3 == 0 ? ["contexts/0"] : i % 3 == 1 ? ["contexts/0", "contexts/1"] : ["contexts/2", "contexts/0", "contexts/1"], Title = "t" }, $"conversations/{i}");
        });
        await h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition());
        await h.AssertCurrentAsync();

        for (int round = 0; round < 6; round++)
        {
            h.PutContext(0, round % 2 == 0 ? "de" : "nl", "Hub", round, round % 2 == 1, "hub " + round);
            await h.AssertCurrentAsync();
            h.PutContext(1, round % 3 == 0 ? "be" : "nl", "Side", round, round % 3 != 0, "side");
            await h.AssertCurrentAsync();
            if (round == 3)
            {
                h.Delete("contexts/0");
                await h.AssertCurrentAsync();
                h.PutContext(0, "nl", "Hub", 1, true, "hub");
                await h.AssertCurrentAsync();
            }
        }
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    public async Task RandomWorkloadWithArraysKeepsTheJoinEqualToAScan(int seed)
    {
        using JoinIndexHarness h = await CreateAsync(this);
        var random = new Random(seed);

        for (int round = 0; round < 10; round++)
        {
            h.RandomMultiRound(random, round == 0 ? 100 : 12);
            if (round % 4 == 3)
                await h.CleanTombstonesAsync();

            await h.AssertCurrentAsync();
        }

        Assert.Equal(1, h.Stats().Join.Builds);
        Assert.Equal(IndexState.Normal, h.Stats().State);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task RandomWorkloadWithManyDocumentsPerRefreshWithArraysMatchesAScan()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        var random = new Random(21);

        for (int round = 0; round < 5; round++)
        {
            h.RandomMultiRound(random, 150);
            await h.AssertCurrentAsync();
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnArrayWithMoreKeysThanAJoinHoldsPutsTheJoinIndexInTheErrorState()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        SeedContexts(h);
        await h.AssertCurrentAsync();

        h.PutConversationKeys(1, Enumerable.Range(0, JoinLimits.MaxKeysPerEntry + 1).Select(x => $"contexts/{x}").ToArray());
        for (int i = 0; i < 1000 && h.Stats().State != IndexState.Error; i++)
            await Task.Delay(20);

        Assert.Equal(IndexState.Error, h.Stats().State);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnArrayWithAsManyKeysAsAJoinHoldsIsJoined()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        SeedContexts(h);
        h.PutConversationKeys(1, Enumerable.Range(0, JoinLimits.MaxKeysPerEntry).Select(x => $"contexts/{x}").ToArray());
        await h.AssertCurrentAsync();
        h.PutContext(7, "nl", "Seven", 7, true, "seven");
        await h.AssertCurrentAsync();
        h.PutContext(2, "de", "Beta", 20, true, "blue whale");
        await h.AssertCurrentAsync();
        Assert.Equal(IndexState.Normal, h.Stats().State);
    }
}
