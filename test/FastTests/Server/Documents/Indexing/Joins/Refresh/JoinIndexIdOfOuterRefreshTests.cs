using System;
using System.Threading.Tasks;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>
/// A join index of a join of the id of the outer document with a field of the inner index (ON id(m) = c.Code): the key of an outer entry is its own lowercased document id, so after every refresh the lists, the key of
/// every outer entry and of every inner document, the counts of the groups and the counters equal a scan of the two snapshots, whichever side is written first.
/// </summary>
public class JoinIndexIdOfOuterRefreshTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static Task<JoinIndexHarness> CreateAsync(RavenTestBase test, bool putJoin = true, bool arrays = true) => JoinIndexHarness.CreateFieldJoinAsync(test, putJoin: putJoin, arrays: arrays, outerId: true);

    private static (long GroupsOfSeveral, long RowsWithSeveralKeys) Counters(JoinIndexHarness h)
    {
        Assert.True(h.Join().TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
        using (snapshot)
            return (snapshot.RowsWithSeveralJoined, snapshot.RowsWithSeveralKeys);
    }

    private static void Seed(JoinIndexHarness h)
    {
        h.PutCodedContextKeys(1, ["conversations/1"], "nl", "Alpha", 10, true, "red fox");
        h.PutCodedContextKeys(2, ["conversations/1", "conversations/2"], "de", "Beta", 20, false, "blue whale");
        h.PutCodedContextKeys(3, ["Conversations/2", "conversations/3"], "nl", "Gamma", 30, true, "red panda");
        h.PutCodedContextKeys(4, ["conversations/3"], "be", "Delta", 40, false, "green");
        h.PutCodedContextKeys(5, null, "nl", "Eps", 50, true, "nothing");
        h.PutCodedContextKeys(6, [], "nl", "Zeta", 60, true, "empty");
        h.PutCodedContextKeys(7, ["", "CONVERSATIONS/1"], "nl", "Eta", 70, true, "mixed");
        h.PutCodedContextKeys(8, ["conversations/99"], "nl", "Theta", 80, true, "nobody home");
        for (int i = 1; i <= 5; i++)
            h.PutConversation(i, null, "t" + i);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task BuildMatchesAScanOfTheSnapshotsAndTheOuterIndexNeedsNoFieldForTheKey()
    {
        using JoinIndexHarness h = await CreateAsync(this, putJoin: false);
        Seed(h);
        await h.PutJoinAsync(JoinIndexHarness.FieldJoinIndexDefinition(outerId: true));

        JoinIndexInvariantChecker.Result result = await h.CheckAsync();
        Assert.True(result.Ok, result.ToString());
        Assert.Equal(1, h.Stats().Join.Builds);
        Assert.Equal(IndexState.Normal, h.Stats().State);
        Assert.False(h.Stats().IsStale);

        // conversations 1, 2, 3 are named; 4 and 5 are not (and conversation 99 does not exist)
        Assert.Equal(3, result.Joined);

        // conversation 1: contexts 1, 2, 7; conversation 2: contexts 2, 3; conversation 3: contexts 3, 4
        Assert.Equal((3, 0), Counters(h));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task DocumentsThatAreAddedAfterTheBuildMatchAScan()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        await h.AssertCurrentAsync();
        Seed(h);
        await h.AssertCurrentAsync();
        Assert.Equal(1, h.Stats().Join.Builds);
        Assert.Equal(IndexState.Normal, h.Stats().State);
        Assert.Equal((3, 0), Counters(h));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AConversationThatIsCreatedAfterTheContextsThatNameItJoinsThemAndOneThatIsDeletedLeavesThem()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutCodedContextKeys(1, ["conversations/7"], "nl", "Alpha", 1, true, "a");
        h.PutCodedContextKeys(2, ["conversations/7"], "de", "Beta", 2, false, "b");
        await h.AssertCurrentAsync();

        // the group of the key is a fact of the inner index: the conversation need not exist
        Assert.Equal((1, 0), Counters(h));

        h.PutConversation(7, null, "now");
        await h.AssertCurrentAsync();
        Assert.Equal((1, 0), Counters(h));

        // an update of the conversation keeps its entry and its key
        h.PutConversation(7, "other", "again");
        await h.AssertCurrentAsync();
        Assert.Equal((1, 0), Counters(h));

        h.Delete("conversations/7");
        await h.AssertCurrentAsync();
        Assert.Equal((1, 0), Counters(h));

        // it comes back, with the contexts still there
        h.PutConversation(7, null, "back");
        await h.AssertCurrentAsync();
        Assert.Equal((1, 0), Counters(h));

        // the same batch deletes and creates both sides
        h.Delete("contexts/1");
        h.Delete("conversations/7");
        h.PutConversation(7, null, "newer");
        h.PutCodedContextKeys(3, ["conversations/7"], "fr", "Gamma", 3, true, "c");
        await h.AssertCurrentAsync();
        Assert.Equal(IndexState.Normal, h.Stats().State);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AContextThatKeepsOneKeyDropsOneAndAddsOneMovesBetweenTheGroups()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        h.PutCodedContextKeys(2, ["conversations/1", "conversations/2", "conversations/5"], "de", "Beta", 20, false, "blue whale");
        await h.AssertCurrentAsync();
        h.PutCodedContextKeys(2, ["conversations/2", "conversations/5", "conversations/4"], "de", "Beta", 20, false, "blue whale");
        await h.AssertCurrentAsync();
        h.PutCodedContextKeys(2, ["conversations/4"], "nl", "Beta", 99, true, "blue whale");
        await h.AssertCurrentAsync();
        h.PutCodedContextKeys(2, [], "nl", "Beta", 99, true, "blue whale");
        await h.AssertCurrentAsync();
        h.PutCodedContextKeys(2, ["conversations/1", "conversations/2", "conversations/3", "conversations/4"], "de", "Beta", 20, false, "blue whale");
        await h.AssertCurrentAsync();
        h.Delete("contexts/2");
        await h.AssertCurrentAsync();
        Assert.Equal(IndexState.Normal, h.Stats().State);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AHubConversationLosesAndRegainsAllItsContexts()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        for (int i = 0; i < 60; i++)
            h.PutCodedContextKeys(i, i % 3 == 0 ? ["conversations/0"] : i % 3 == 1 ? ["conversations/0", "conversations/" + i % 5] : ["conversations/" + i % 5], i % 3 == 0 ? "nl" : "de", "n" + i % 4, i, true, "t");

        for (int i = 0; i < 6; i++)
            h.PutConversation(i, null, "t");

        await h.AssertCurrentAsync();

        for (int i = 0; i < 60; i++)
            h.Delete($"contexts/{i}");

        await h.AssertCurrentAsync();
        Assert.Equal((0, 0), Counters(h));

        for (int i = 0; i < 60; i++)
            h.PutCodedContextKeys(i, i % 3 == 0 ? ["conversations/0"] : ["conversations/" + i % 5], i % 3 == 0 ? "nl" : "de", "n" + i % 4, i, true, "t");

        await h.AssertCurrentAsync();
        h.Delete("conversations/0");
        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task IdsOfAnyCaseAndKeysThatAreNoKeysAreOneKeyEach()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutCodedContextKeys(1, ["conversations/1", "Conversations/1", "CONVERSATIONS/1"], "nl", "Alpha", 1, true, "a");
        h.PutCodedContextKeys(2, ["CONVERSATIONS/1"], "de", "Beta", 2, true, "b");
        h.PutCodedContextKeys(3, [null, ""], "de", "Beta", 2, true, "b");
        h.PutConversation(1, null, "t");
        await h.AssertCurrentAsync();

        // one conversation named by two contexts
        Assert.Equal((1, 0), Counters(h));
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(1, 1, true)]
    [InlineData(2, 4, true)]
    [InlineData(3, 1, false)]
    [InlineData(5, 4, true)]
    [InlineData(9, 3, true)]
    [InlineData(21, 5, true)]
    public async Task RandomRoundsKeepTheInvariant(int seed, int innerKeys, bool arrays)
    {
        using JoinIndexHarness h = await CreateAsync(this, arrays: arrays);
        var random = new Random(seed);
        for (int round = 0; round < 10; round++)
        {
            h.RandomIdRound(random, round == 0 ? 120 : 20, innerKeys, arrays);
            if (round % 4 == 3)
                await h.CleanTombstonesAsync();

            await h.AssertCurrentAsync();
        }

        Assert.Equal(IndexState.Normal, h.Stats().State);
        Assert.Equal(1, h.Stats().Join.Builds);
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(33, 4, true)]
    [InlineData(35, 1, false)]
    [InlineData(37, 3, true)]
    public async Task ABuildOverARandomWorkloadEqualsTheRefreshOfIt(int seed, int innerKeys, bool arrays)
    {
        using JoinIndexHarness built = await CreateAsync(this, putJoin: false, arrays: arrays);
        built.RandomIdRound(new Random(seed), 250, innerKeys, arrays);
        await built.PutJoinAsync(JoinIndexHarness.FieldJoinIndexDefinition(outerId: true));
        await built.AssertCurrentAsync();
        built.RandomIdRound(new Random(seed + 1), 60, innerKeys, arrays);
        await built.AssertCurrentAsync();
    }
}
