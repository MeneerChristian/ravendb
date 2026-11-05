using System;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>
/// A join index of a join on a field: after every refresh the lists, the key of every outer entry and of every inner document, the counts of the groups and the atoms of every key equal a scan of the two snapshots.
/// </summary>
public class JoinIndexFieldJoinRefreshTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static Task<JoinIndexHarness> CreateAsync(RavenTestBase test, bool putJoin = true) => JoinIndexHarness.CreateFieldJoinAsync(test, putJoin: putJoin);

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(30);
        while (condition() == false)
        {
            Assert.True(DateTime.UtcNow < until, $"timed out waiting for {what}");
            await Task.Delay(25);
        }
    }

    private static void Seed(JoinIndexHarness h)
    {
        h.PutCodedContext(1, "k1", "nl", "Alpha", 10, true, "red fox");
        h.PutCodedContext(2, "k1", "de", "Beta", 20, false, "blue whale");
        h.PutCodedContext(3, "k2", "nl", "Gamma", 30, true, "red panda");
        h.PutCodedContext(4, "k3", "be", "Delta", 40, false, "green");
        h.PutCodedContext(5, null, "nl", "Eps", 50, true, "nothing");
        h.PutCodedContext(6, "", "nl", "Zeta", 60, true, "empty");
        h.PutConversation(1, "k1");
        h.PutConversation(2, "K1");
        h.PutConversation(3, "k2");
        h.PutConversation(4, "k4");
        h.PutConversation(5, null);
        h.PutConversation(6, "");
        h.PutConversation(7, "k3");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task BuildMatchesAScanOfTheSnapshots()
    {
        using JoinIndexHarness h = await CreateAsync(this, putJoin: false);
        Seed(h);
        await h.PutJoinAsync(JoinIndexHarness.FieldJoinIndexDefinition());

        JoinIndexInvariantChecker.Result result = await h.CheckAsync();
        Assert.True(result.Ok, result.ToString());
        Assert.Equal(1, h.Stats().Join.Builds);
        Assert.Equal(IndexState.Normal, h.Stats().State);
        Assert.False(h.Stats().IsStale);
        Assert.Equal(5, result.Joined);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnInnerDocumentJoinsAGroupAndItsAtomsReachTheOuterEntries()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        h.PutCodedContext(7, "k1", "fr", "Eta", 70, true, "new");
        h.PutCodedContext(8, "k4", "it", "Theta", 80, false, "late");
        h.PutCodedContext(9, "k9", "es", "Iota", 90, true, "nobody");
        await h.AssertCurrentAsync();
        Assert.Equal(1, h.Stats().Join.Builds);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheLastHolderOfAnAtomLeavesAndTheOthersStay()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        // k1 has nl (context 1) and de (context 2): after the change nl is held by 7 only, and de by nobody
        h.PutCodedContext(7, "k1", "nl", "Eta", 70, true, "x");
        await h.AssertCurrentAsync();
        h.PutCodedContext(1, "k1", "es", "Alpha", 10, true, "red fox");
        await h.AssertCurrentAsync();
        h.PutCodedContext(2, "k1", "es", "Beta", 20, false, "blue whale");
        await h.AssertCurrentAsync();
        h.Delete("contexts/7");
        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TwoEntriesOfAGroupThatSwapAnAtomInOneBatchChangeNothing()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        h.Save(s =>
        {
            s.Store(new JoinIndexHarness.Context { Code = "k1", Country = "de", Name = "Alpha", Rank = 10, IsActive = true, Title = "red fox" }, "contexts/1");
            s.Store(new JoinIndexHarness.Context { Code = "k1", Country = "nl", Name = "Beta", Rank = 20, IsActive = false, Title = "blue whale" }, "contexts/2");
        });

        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnInnerDocumentThatChangesItsKeyMovesBetweenGroups()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        // out of a group of two into a group of one, into a group nobody asks for, into none, and back
        h.PutCodedContext(1, "k2", "nl", "Alpha", 10, true, "red fox");
        await h.AssertCurrentAsync();
        h.PutCodedContext(1, "k77", "nl", "Alpha", 10, true, "red fox");
        await h.AssertCurrentAsync();
        h.PutCodedContext(1, null, "nl", "Alpha", 10, true, "red fox");
        await h.AssertCurrentAsync();
        h.PutCodedContext(1, "", "nl", "Alpha", 10, true, "red fox");
        await h.AssertCurrentAsync();
        h.PutCodedContext(1, "K1", "nl", "Alpha", 10, true, "red fox");
        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AGroupThatLosesAllItsEntriesAndGetsOneAgainIsExact()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        h.Delete("contexts/1");
        h.Delete("contexts/2");
        await h.AssertCurrentAsync();
        h.PutCodedContext(2, "k1", "fr", "Beta", 20, true, "back");
        await h.AssertCurrentAsync();

        // deleted and created again in two batches, with the same terms
        h.Delete("contexts/2");
        await h.AssertCurrentAsync();
        h.PutCodedContext(2, "k1", "fr", "Beta", 20, true, "back");
        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task OuterEntriesThatChangeTheirKeyOrAreDeletedLeaveTheListsOfTheirGroup()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        h.PutConversation(1, "k2");
        h.PutConversation(2, null);
        h.PutConversation(3, "k77");
        h.PutConversation(4, "k1");
        await h.AssertCurrentAsync();
        h.Delete("conversations/1");
        h.Delete("conversations/7");
        await h.AssertCurrentAsync();
        h.PutConversation(1, "k1", "again");
        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AHubOnBothSidesIsCountedAndTransitionsOnce()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        for (int i = 0; i < 60; i++)
            h.PutConversation(i, "hub", i % 3 == 0 ? "a" : "b");

        for (int i = 0; i < 25; i++)
            h.PutCodedContext(i, "hub", i % 4 == 0 ? "nl" : "de", "n" + i % 3, i, i % 2 == 0, "t");

        await h.AssertCurrentAsync();

        // the group goes down to one entry, to none and up again: the any list and the lists of the atoms follow
        for (int i = 1; i < 25; i++)
            h.Delete($"contexts/{i}");

        await h.AssertCurrentAsync();
        h.PutCodedContext(0, "elsewhere", "nl", "n0", 0, true, "t");
        await h.AssertCurrentAsync();
        h.PutCodedContext(3, "hub", "fr", "n1", 3, true, "t");
        await h.AssertCurrentAsync();
        h.PutCodedContext(4, "hub", "fr", "n1", 3, true, "t");
        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task OuterEntriesThatArriveBeforeTheirGroupAreJoinedWhenItAppears()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        for (int i = 0; i < 6; i++)
            h.PutConversation(i, "late" + i % 2);

        await h.AssertCurrentAsync();
        h.PutCodedContext(1, "late0", "nl", "Alpha", 1, true, "x");
        await h.AssertCurrentAsync();
        h.PutCodedContext(2, "late1", "de", "Beta", 2, false, "y");
        h.PutCodedContext(3, "late1", "nl", "Beta", 3, true, "z");
        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ARefreshThatChangesBothSidesInOneBatchIsExact()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        h.Save(s =>
        {
            s.Store(new JoinIndexHarness.Conversation { ContextId = "k2", Title = "moved" }, "conversations/1");
            s.Store(new JoinIndexHarness.Conversation { ContextId = "k5", Title = "new" }, "conversations/20");
            s.Store(new JoinIndexHarness.Context { Code = "k5", Country = "fr", Name = "Eta", Rank = 5, IsActive = true, Title = "n" }, "contexts/20");
            s.Store(new JoinIndexHarness.Context { Code = "k2", Country = "fr", Name = "Eta", Rank = 5, IsActive = true, Title = "n" }, "contexts/3");
            s.Delete("contexts/4");
            s.Delete("conversations/7");
        });

        await h.AssertCurrentAsync();
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(21)]
    public async Task RandomRoundsKeepTheInvariant(int seed)
    {
        using JoinIndexHarness h = await CreateAsync(this);
        var random = new Random(seed);
        for (int round = 0; round < 10; round++)
        {
            h.RandomFieldRound(random, round == 0 ? 120 : 20);
            if (round % 4 == 3)
                await h.CleanTombstonesAsync();

            await h.AssertCurrentAsync();
        }

        Assert.Equal(IndexState.Normal, h.Stats().State);
        Assert.Equal(1, h.Stats().Join.Builds);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ABuildOverARandomWorkloadEqualsTheRefreshOfIt()
    {
        using JoinIndexHarness built = await CreateAsync(this, putJoin: false);
        built.RandomFieldRound(new Random(33), 250);
        await built.PutJoinAsync(JoinIndexHarness.FieldJoinIndexDefinition());
        await built.AssertCurrentAsync();
        built.RandomFieldRound(new Random(34), 60);
        await built.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnInnerDocumentWithSeveralKeysIsNoLongerAnError()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        h.Save(s =>
        {
            object doc = new { Code = new[] { "k1", "k2" }, Country = "nl", Name = "x", Rank = 1, IsActive = true, Title = "t" };
            s.Store(doc, "contexts/50");
            s.Advanced.GetMetadataFor(doc)[Constants.Documents.Metadata.Collection] = "Contexts";
        });

        await h.AssertCurrentAsync();
        Assert.Equal(IndexState.Normal, h.Stats().State);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnOuterDocumentWithSeveralKeysIsNoLongerAnError()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        h.Save(s =>
        {
            object doc = new { ContextId = new[] { "k1", "k2" }, Title = "two" };
            s.Store(doc, "conversations/50");
            s.Advanced.GetMetadataFor(doc)[Constants.Documents.Metadata.Collection] = "Conversations";
        });

        await h.AssertCurrentAsync();
        Assert.Equal(IndexState.Normal, h.Stats().State);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AKeyOverTheLimitOfTheTermsPutsTheJoinIndexInTheErrorState()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutCodedContext(1, new string('x', JoinLimits.MaxTermLength + 1), "nl", "x", 1, true, "t");
        await WaitForAsync(() => h.Stats().State == IndexState.Error, "error state");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnOuterKeyOverTheLimitOfTheTermsJoinsNothingAndIsNotAnError()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        h.PutConversation(30, new string('y', JoinLimits.MaxTermLength + 5));
        await h.AssertCurrentAsync();
        h.PutConversation(30, "k1");
        await h.AssertCurrentAsync();
        Assert.Equal(IndexState.Normal, h.Stats().State);
    }
}
