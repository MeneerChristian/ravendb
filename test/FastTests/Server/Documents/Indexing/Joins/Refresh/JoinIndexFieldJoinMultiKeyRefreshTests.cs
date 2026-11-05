using System;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>
/// A join index of a join on a field whose key fields hold arrays, on either side or on both: after every refresh the lists (an outer entry is in the list of an atom when a group of one of its keys has the atom),
/// the keys of every outer entry and of every inner document, the counts of the groups, the entries with several keys and the two counters equal a scan of the two snapshots.
/// </summary>
public class JoinIndexFieldJoinMultiKeyRefreshTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static Task<JoinIndexHarness> CreateAsync(RavenTestBase test, bool putJoin = true) => JoinIndexHarness.CreateFieldJoinAsync(test, putJoin: putJoin, arrays: true);

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(30);
        while (condition() == false)
        {
            Assert.True(DateTime.UtcNow < until, $"timed out waiting for {what}");
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// The number of keys with a group of two or more inner entries, and of the outer entries with two or more keys that have an inner entry in their group. An Indirect file counts the rows with two or more
    /// keys whether the keys have a group or not: the second number is not compared (-1), the invariant check compares it with the rows.
    /// </summary>
    private static (long GroupsOfSeveral, long RowsWithSeveralKeys) Counters(JoinIndexHarness h)
    {
        Assert.True(h.Join().TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
        using (snapshot)
            return (snapshot.RowsWithSeveralJoined, h.IsIndirect ? -1 : snapshot.RowsWithSeveralKeys);
    }

    private static string[] Keys(string prefix, int count) => Enumerable.Range(0, count).Select(i => prefix + i).ToArray();

    private static void Seed(JoinIndexHarness h)
    {
        h.PutCodedContextKeys(1, ["k1"], "nl", "Alpha", 10, true, "red fox");
        h.PutCodedContextKeys(2, ["k1", "k2"], "de", "Beta", 20, false, "blue whale");
        h.PutCodedContextKeys(3, ["k2", "k3"], "nl", "Gamma", 30, true, "red panda");
        h.PutCodedContextKeys(4, ["k3"], "be", "Delta", 40, false, "green");
        h.PutCodedContextKeys(5, null, "nl", "Eps", 50, true, "nothing");
        h.PutCodedContextKeys(6, [], "nl", "Zeta", 60, true, "empty");
        h.PutCodedContextKeys(7, ["", "K1"], "nl", "Eta", 70, true, "mixed");
        h.PutConversationKeys(1, ["k1"]);
        h.PutConversationKeys(2, ["k1", "k2"]);
        h.PutConversationKeys(3, ["k2", "k3", "k4"]);
        h.PutConversationKeys(4, ["k4"]);
        h.PutConversationKeys(5, ["K1", "k1", "k1"]);
        h.PutConversationKeys(6, [null, "k3"]);
        h.PutConversationKeys(7, []);
        h.PutConversationKeys(8, null);
        h.PutConversationKeys(9, [""]);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task BuildWithArraysOnBothSidesMatchesAScanOfTheSnapshots()
    {
        using JoinIndexHarness h = await CreateAsync(this, putJoin: false);
        Seed(h);
        await h.PutJoinAsync(JoinIndexHarness.FieldJoinIndexDefinition());

        JoinIndexInvariantChecker.Result result = await h.CheckAsync();
        Assert.True(result.Ok, result.ToString());
        Assert.Equal(1, h.Stats().Join.Builds);
        Assert.Equal(IndexState.Normal, h.Stats().State);
        Assert.False(h.Stats().IsStale);

        // conversations 1, 2, 3, 5, 6, 9 join something (4 names k4 only)
        Assert.Equal(6, result.Joined);
        (long groups, long rows) = Counters(h);

        // k1: contexts 1, 2, 7; k2: 2, 3; k3: 3, 4; "": 7. Conversations 2 (k1, k2) and 3 (k2, k3) have two keys with a group
        Assert.Equal(3, groups);
        Assert.Equal(h.IsIndirect ? -1 : 2, rows);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ArraysThatAreAddedToTheIndexesAfterTheBuildMatchAScan()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        await h.AssertCurrentAsync();
        Seed(h);
        await h.AssertCurrentAsync();
        Assert.Equal(1, h.Stats().Join.Builds);
        Assert.Equal(IndexState.Normal, h.Stats().State);
        Assert.Equal(h.IsIndirect ? (3, -1L) : (3, 2L), Counters(h));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnInnerDocumentThatKeepsOneKeyDropsOneAndAddsOneMovesBetweenTheGroups()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        h.PutCodedContextKeys(2, ["k1", "k2", "k5"], "de", "Beta", 20, false, "blue whale");
        await h.AssertCurrentAsync();
        h.PutCodedContextKeys(2, ["k2", "k5", "k6"], "de", "Beta", 20, false, "blue whale");
        await h.AssertCurrentAsync();
        h.PutCodedContextKeys(2, ["k6"], "nl", "Beta", 99, true, "blue whale");
        await h.AssertCurrentAsync();
        h.PutCodedContextKeys(2, [], "nl", "Beta", 99, true, "blue whale");
        await h.AssertCurrentAsync();
        h.PutCodedContextKeys(2, ["k1", "k2", "k3", "k4"], "de", "Beta", 20, false, "blue whale");
        await h.AssertCurrentAsync();
        h.Delete("contexts/2");
        await h.AssertCurrentAsync();
        Assert.Equal(IndexState.Normal, h.Stats().State);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnOuterEntryThatKeepsOneKeyDropsOneAndAddsOneStaysInTheListsOfTheKeysItKeeps()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        h.PutConversationKeys(3, ["k2", "k3", "k4", "k1"]);
        await h.AssertCurrentAsync();
        h.PutConversationKeys(3, ["k3", "k4", "k1"]);
        await h.AssertCurrentAsync();
        h.PutConversationKeys(3, ["k4"]);
        await h.AssertCurrentAsync();
        h.PutConversationKeys(3, []);
        await h.AssertCurrentAsync();
        h.PutConversationKeys(3, ["k2", "k3"]);
        await h.AssertCurrentAsync();
        h.Delete("conversations/3");
        await h.AssertCurrentAsync();
        Assert.Equal(IndexState.Normal, h.Stats().State);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task KeysThatAreRepeatedOrDifferOnlyInCaseAreOneKey()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutCodedContextKeys(1, ["k1", "K1", "k1"], "nl", "Alpha", 1, true, "a");
        h.PutCodedContextKeys(2, ["k1"], "de", "Beta", 2, true, "b");
        h.PutConversationKeys(1, ["k1", "K1", "k1"]);
        h.PutConversationKeys(2, ["K1"]);
        await h.AssertCurrentAsync();

        // one key each: the group of k1 holds two entries and no conversation has two keys
        Assert.Equal(h.IsIndirect ? (1, -1L) : (1, 0L), Counters(h));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ARowWithTwoKeysStaysInAListWhenOneKeyLosesTheAtomAndLeavesWhenBothDo()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutCodedContextKeys(1, ["a"], "nl", "x1", 1, true, "t");
        h.PutCodedContextKeys(2, ["b"], "nl", "x2", 2, true, "t");
        h.PutConversationKeys(1, ["a", "b"]);
        await h.AssertCurrentAsync();
        Assert.Equal(h.IsIndirect ? (0, -1L) : (0, 1L), Counters(h));

        // the group of a has no other entry with the atom Country = nl: the row stays in the list through b
        h.PutCodedContextKeys(1, ["a"], "de", "x1", 1, true, "t");
        await h.AssertCurrentAsync();

        // both groups lose it in one batch
        h.PutCodedContextKeys(1, ["a"], "fr", "x1", 1, true, "t");
        h.PutCodedContextKeys(2, ["b"], "fr", "x2", 2, true, "t");
        await h.AssertCurrentAsync();

        // the groups swap the atom in one batch
        h.PutCodedContextKeys(1, ["a"], "nl", "x1", 1, true, "t");
        h.PutCodedContextKeys(2, ["b"], "de", "x2", 2, true, "t");
        await h.AssertCurrentAsync();
        h.PutCodedContextKeys(1, ["a"], "de", "x1", 1, true, "t");
        h.PutCodedContextKeys(2, ["b"], "nl", "x2", 2, true, "t");
        await h.AssertCurrentAsync();
        Assert.Equal(h.IsIndirect ? (0, -1L) : (0, 1L), Counters(h));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheRowsWithSeveralKeysAreCountedWhenTheGroupsOfTheirKeysAppearAndVanish()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutConversationKeys(1, ["k1", "k2"]);
        h.PutConversationKeys(2, ["k1", "k2", "k3"]);
        h.PutConversationKeys(3, ["k3"]);
        await h.AssertCurrentAsync();
        Assert.Equal(h.IsIndirect ? (0, -1L) : (0, 0L), Counters(h));

        h.PutCodedContextKeys(1, ["k1"]);
        await h.AssertCurrentAsync();
        Assert.Equal(h.IsIndirect ? (0, -1L) : (0, 0L), Counters(h));

        h.PutCodedContextKeys(2, ["k2"]);
        await h.AssertCurrentAsync();
        Assert.Equal(h.IsIndirect ? (0, -1L) : (0, 2L), Counters(h));

        h.PutCodedContextKeys(3, ["k3"]);
        await h.AssertCurrentAsync();
        Assert.Equal(h.IsIndirect ? (0, -1L) : (0, 2L), Counters(h));

        // one entry holds two keys: it joins the groups of both and makes conversation 1 and 2 hold two keys with a group through one entry
        h.PutCodedContextKeys(2, ["k2", "k1"]);
        await h.AssertCurrentAsync();
        Assert.Equal(h.IsIndirect ? (1, -1L) : (1, 2L), Counters(h));

        h.Delete("contexts/1");
        h.Delete("contexts/2");
        await h.AssertCurrentAsync();
        Assert.Equal(h.IsIndirect ? (0, -1L) : (0, 0L), Counters(h));

        h.PutCodedContextKeys(1, ["k1", "k2", "k3"]);
        await h.AssertCurrentAsync();
        Assert.Equal(h.IsIndirect ? (1, -1L) : (1, 2L), Counters(h));

        h.PutConversationKeys(1, ["k1"]);
        h.PutConversationKeys(2, []);
        await h.AssertCurrentAsync();
        Assert.Equal(h.IsIndirect ? (1, -1L) : (1, 0L), Counters(h));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AHubOnBothSidesWithManyKeysTransitionsOnce()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        for (int i = 0; i < 60; i++)
            h.PutConversationKeys(i, i % 3 == 0 ? ["hub"] : i % 3 == 1 ? ["hub", "k" + i % 5] : ["k" + i % 5, "k" + (i + 1) % 5]);

        for (int i = 0; i < 12; i++)
            h.PutCodedContextKeys(i, i % 2 == 0 ? ["hub", "k" + i % 5] : ["hub"], i % 3 == 0 ? "nl" : "de", "n" + i % 4, i, true, "t");

        await h.AssertCurrentAsync();

        // the entries that hold the hub leave and come back in two batches
        for (int i = 0; i < 12; i++)
            h.Delete($"contexts/{i}");

        await h.AssertCurrentAsync();
        Assert.Equal(h.IsIndirect ? (0, -1L) : (0, 0L), Counters(h));

        for (int i = 0; i < 12; i++)
            h.PutCodedContextKeys(i, i % 2 == 0 ? ["hub", "k" + i % 5] : ["hub"], i % 3 == 0 ? "nl" : "de", "n" + i % 4, i, true, "t");

        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnEntryWithTheMostKeysOfAnEntryJoinsAllOfThem()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        string[] keys = Keys("x", JoinLimits.MaxKeysPerEntry);
        h.PutCodedContextKeys(1, keys, "nl", "Alpha", 1, true, "t");
        for (int i = 0; i < 40; i++)
            h.PutConversationKeys(i, i % 4 == 0 ? keys : [keys[i * 5 % keys.Length], "other"]);

        await h.AssertCurrentAsync();
        Assert.Equal(IndexState.Normal, h.Stats().State);

        h.PutCodedContextKeys(1, keys.Skip(100).ToArray(), "de", "Alpha", 1, true, "t");
        await h.AssertCurrentAsync();
        h.PutCodedContextKeys(1, keys, "nl", "Alpha", 1, true, "t");
        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task MoreInnerKeysThanTheLimitPutTheJoinIndexInTheErrorState()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        h.PutCodedContextKeys(50, Keys("x", JoinLimits.MaxKeysPerEntry + 1), "nl", "x", 1, true, "t");
        await WaitForAsync(() => h.Stats().State == IndexState.Error, "error state");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task MoreOuterKeysThanTheLimitPutTheJoinIndexInTheErrorState()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        h.PutConversationKeys(50, Keys("x", JoinLimits.MaxKeysPerEntry + 1));
        await WaitForAsync(() => h.Stats().State == IndexState.Error, "error state");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AKeyOverTheLimitOfTheTermsInAnArrayPutsTheJoinIndexInTheErrorStateAndOnTheOuterSideIsNoKey()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        h.PutConversationKeys(30, ["k1", new string('y', JoinLimits.MaxTermLength + 5)]);
        await h.AssertCurrentAsync();
        Assert.Equal(IndexState.Normal, h.Stats().State);

        h.PutCodedContextKeys(50, ["k1", new string('x', JoinLimits.MaxTermLength + 1)], "nl", "x", 1, true, "t");
        await WaitForAsync(() => h.Stats().State == IndexState.Error, "error state");
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(1, 1, 4)]
    [InlineData(2, 4, 1)]
    [InlineData(3, 4, 4)]
    [InlineData(5, 4, 4)]
    [InlineData(9, 3, 3)]
    [InlineData(21, 5, 5)]
    public async Task RandomRoundsKeepTheInvariant(int seed, int outerKeys, int innerKeys)
    {
        using JoinIndexHarness h = await CreateAsync(this);
        var random = new Random(seed);
        for (int round = 0; round < 10; round++)
        {
            h.RandomFieldArrayRound(random, round == 0 ? 120 : 20, outerKeys, innerKeys);
            if (round % 4 == 3)
                await h.CleanTombstonesAsync();

            await h.AssertCurrentAsync();
        }

        Assert.Equal(IndexState.Normal, h.Stats().State);
        Assert.Equal(1, h.Stats().Join.Builds);
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(33, 4, 4)]
    [InlineData(35, 1, 4)]
    [InlineData(37, 4, 1)]
    public async Task ABuildOverARandomWorkloadEqualsTheRefreshOfIt(int seed, int outerKeys, int innerKeys)
    {
        using JoinIndexHarness built = await CreateAsync(this, putJoin: false);
        built.RandomFieldArrayRound(new Random(seed), 250, outerKeys, innerKeys);
        await built.PutJoinAsync(JoinIndexHarness.FieldJoinIndexDefinition());
        await built.AssertCurrentAsync();
        built.RandomFieldArrayRound(new Random(seed + 1), 60, outerKeys, innerKeys);
        await built.AssertCurrentAsync();
    }
}

/// <summary>The tests of the class with the join indexes on a field in the storage mode Indirect.</summary>
public class JoinIndexFieldJoinMultiKeyRefreshTestsIndirect(ITestOutputHelper output) : JoinIndexFieldJoinMultiKeyRefreshTests(output), IIndirectModeTest;
