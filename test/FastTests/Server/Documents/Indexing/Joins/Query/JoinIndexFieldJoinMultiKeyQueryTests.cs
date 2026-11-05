using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>
/// A join index of a join on a field whose key fields hold arrays (ON m.ContextId = c.Code) is served like the join of the server: an outer row matches when one of its keys is a key of a matched inner entry,
/// it is returned once, and its score is the outer score plus boost times the sum of the best scores of its distinct matched keys. What the lists cannot answer exactly is read by the server and counted.
/// </summary>
public class JoinIndexFieldJoinMultiKeyQueryTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static readonly string[] Subs =
    [
        "",
        " where Country = 'nl'",
        " where Country = 'c1'",
        " where Country = 'C1'",
        " where Name = 'n1'",
        " where Rank = 10",
        " where IsActive = true",
        " where IsActive != true",
        " where Country != 'nl'",
        " where Country = 'nl' and IsActive = true",
        " where Country = 'c2' and Name = 'n3' and IsActive = true",
        " where Country != 'c1' and IsActive = true",
        " where Country = 'nobody'",
        " where Rank > 20",
        " where Rank between 5 and 15",
        " where startsWith(Name, 'n')",
        " where Country = 'nl' or Country = 'c1'",
        " where Country in ('nl', 'c2', 'nobody')",
        " where Rank < 7 or Rank > 45"
    ];

    // the score of the join is not used: the query is ordered by a field of the main documents
    private static readonly string[] UnscoredTails =
    [
        " order by m.Title limit 0, 10",
        " order by m.Title desc limit 3, 7",
        " where m.Title = 'a' order by m.Title limit 0, 1000",
        " order by m.Title limit 1000, 5",
        " where m.Title = 'b' order by m.Title desc limit 0, 20"
    ];

    private static readonly string[] ScoredTails =
    [
        " limit 0, 1000",
        " order by score() limit 0, 7",
        " order by score() desc limit 3, 7",
        " where m.Title = 'a' order by score() limit 0, 5",
        " boost 2 order by score() limit 0, 20"
    ];

    private const string GroupResidual = "KindNotServed:GroupResidual";
    private const string ScoresMultiKey = "KindNotServed:ScoresMultiKey";
    private const string OrderBy = "KindNotServed:OrderByMultiKey";

    /// <summary>The arrays of the workload: how many keys a conversation (outer) and a context (inner) hold at most.</summary>
    public static IEnumerable<object[]> Modes()
    {
        yield return [1, 4];
        yield return [4, 1];
        yield return [4, 4];
    }

    private static Dictionary<string, string> ScoreSettings() => new() { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };

    private static string FieldJoin(string sub, string tail = "") => $"{Outer} join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code{tail}";

    private static string FieldLeftJoin(string sub, string tail = "") => $"{Outer} left join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code{tail}";

    private static bool HasAndOrNot(string sub) => (sub.Contains(" and ") && sub.Contains("between") == false) || sub.Contains("!=");

    private static bool NeedsAScore(string sub) => sub.Contains(" or ") || sub.Contains(" in ") || sub.Contains("Rank >") || sub.Contains("Rank between") || sub.Contains("startsWith") || sub.Contains("Rank <");

    private static Task<JoinIndexHarness> CreateAsync(RavenTestBase test) => JoinIndexHarness.CreateFieldJoinAsync(test, ScoreSettings(), arrays: true);

    private async Task<JoinIndexHarness> HarnessWithDataAsync(int seed, int outerKeys, int innerKeys, int operations = 250)
    {
        JoinIndexHarness h = await CreateAsync(this);
        h.RandomFieldArrayRound(new Random(seed), operations, outerKeys, innerKeys);
        await h.AssertCurrentAsync();
        return h;
    }

    /// <summary>An Indirect file counts the rows with two or more keys whether the keys have a group or not: the invariant check compares that number with the rows, here only the groups are compared.</summary>
    private static void AssertCounters(JoinIndexHarness h, long groups, long rowsWithGroups)
    {
        (long actualGroups, long actualRows) = Counters(h);
        Assert.Equal(groups, actualGroups);
        if (h.IsIndirect == false)
            Assert.Equal(rowsWithGroups, actualRows);
    }

    private static (long GroupsOfSeveral, long RowsWithSeveralKeys) Counters(JoinIndexHarness h)
    {
        Assert.True(h.Join().TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
        using (snapshot)
            return (snapshot.RowsWithSeveralJoined, snapshot.RowsWithSeveralKeys);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [MemberData(nameof(Modes))]
    public async Task EveryKindThatIsServedEqualsTheJoinOfTheServer(int outerKeys, int innerKeys)
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(1, outerKeys, innerKeys);
        (long groups, long rows) = Counters(h);
        Assert.True(groups > 0, "the workload has no group of two or more entries");
        Assert.Equal(outerKeys > 1, rows > 0);
        var oracle = new JoinIndexQueryOracle(h);

        // an AND or a NOT is exact too: the rows that are joined with two inner entries are checked against the inner index
        long expectedServed = 0;
        foreach (string sub in Subs)
        {
            foreach (string tail in UnscoredTails)
            {
                oracle.Check(FieldJoin(sub, tail));
                expectedServed++;
            }
        }

        Assert.Equal(expectedServed, oracle.Info.Served);
        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [MemberData(nameof(Modes))]
    public async Task TheScoreIsServedWhileNoRowHoldsSeveralKeysWithAGroupAndReadByTheServerOtherwise(int outerKeys, int innerKeys)
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(2, outerKeys, innerKeys);
        (_, long rows) = Counters(h);
        var oracle = new JoinIndexQueryOracle(h);

        // a row that matches through two keys scores twice the score of a key; one key, however many inner entries carry it, scores once
        int checks = 0;
        foreach (string sub in Subs.Where(x => NeedsAScore(x) == false && HasAndOrNot(x) == false))
        {
            foreach (string tail in ScoredTails)
            {
                oracle.Check(FieldJoin(sub, tail), expectServed: rows == 0);
                checks++;
            }
        }

        if (rows == 0)
        {
            Assert.Equal(checks, oracle.Info.Served);
            Assert.Equal(0, oracle.Info.FallbackTotal);
        }
        else
        {
            Assert.Equal(checks, oracle.FallbacksOf(ScoresMultiKey));
            Assert.Equal(0, oracle.Info.Served);
        }
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [MemberData(nameof(Modes))]
    public async Task LeftJoinsAreTheJoinOfTheServer(int outerKeys, int innerKeys)
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(3, outerKeys, innerKeys);
        (_, long rows) = Counters(h);
        var oracle = new JoinIndexQueryOracle(h);

        foreach (string sub in new[] { Subs[0], Subs[1], Subs[4], Subs[6], Subs[12] })
        {
            foreach (string tail in UnscoredTails)
                oracle.Check(FieldLeftJoin(sub, tail));

            foreach (string tail in ScoredTails.Take(3))
                oracle.Check(FieldLeftJoin(sub, tail), expectServed: rows == 0);
        }

        Assert.Equal(rows > 0, oracle.FallbacksOf(ScoresMultiKey) > 0);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnInnerEntryInSeveralGroupsIsJoinedLikeTheServerAndNeedsNoResidualWhileEveryGroupHasOneEntry()
    {
        using JoinIndexHarness h = await CreateAsync(this);

        // the entry 1 is in the groups of k1 and k2, which have no other entry: no row is joined with two inner entries
        h.PutCodedContextKeys(1, ["k1", "k2"], "nl", "n1", 1, true, "x");
        h.PutCodedContextKeys(2, ["k3"], "de", "n2", 2, false, "y");
        h.PutCodedContextKeys(3, ["k4", "k5"], "nl", "n2", 3, true, "z");
        h.PutConversationKeys(1, ["k1"], "a");
        h.PutConversationKeys(2, ["k2"], "b");
        h.PutConversationKeys(3, ["k3"], "a");
        h.PutConversationKeys(4, ["k5"], "b");
        h.PutConversationKeys(5, ["k9"], "a");
        await h.AssertCurrentAsync();
        AssertCounters(h, 0, 0);
        var oracle = new JoinIndexQueryOracle(h);

        string order = " order by m.Title limit 0, 10";
        string[] subs = [" where Country = 'nl' and Name = 'n2'", " where Country != 'nl'", " where Country = 'nl' and IsActive != true", " where IsActive = true and Name != 'n1'", ""];
        foreach (string sub in subs)
        {
            oracle.Check(FieldJoin(sub, order));
            oracle.Check(FieldJoin(sub, " order by score() limit 0, 10"));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);

        // the entry 3 joins the group of k1: now a row is joined with two entries and an AND is checked against the inner index
        h.PutCodedContextKeys(3, ["k4", "k1"], "nl", "n2", 3, true, "z");
        await h.AssertCurrentAsync();
        AssertCounters(h, 1, 0);
        foreach (string sub in subs.Take(4))
            oracle.Check(FieldJoin(sub, order));

        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ARowWithTwoKeysIsJoinedWithEveryGroupAndScoresTwiceWhenBothKeysMatch()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutCodedContextKeys(1, ["k1"], "nl", "n1", 1, true, "x");
        h.PutCodedContextKeys(2, ["k2"], "de", "n2", 2, false, "y");
        h.PutCodedContextKeys(3, ["k3", "k2"], "nl", "n2", 3, true, "z");
        h.PutConversationKeys(1, ["k1", "k2"], "both");
        h.PutConversationKeys(2, ["k1"], "one");
        h.PutConversationKeys(3, ["k3", "k1", "k2"], "three");
        h.PutConversationKeys(4, ["k9", "k2"], "other");
        await h.AssertCurrentAsync();
        AssertCounters(h, 1, 2);
        var oracle = new JoinIndexQueryOracle(h);

        string order = " order by m.Title limit 0, 10";

        // lists: exact by union, served
        foreach (string sub in new[] { "", " where Country = 'nl'", " where Country = 'de'", " where Name = 'n2'", " where Country = 'nl' or Country = 'de'" })
        {
            oracle.Check(FieldJoin(sub, order), expectServed: true);
            oracle.Check(FieldLeftJoin(sub, order), expectServed: true);
        }

        // row 1 has the atom nl through k1 and the atom n2 through k2, and no single inner entry has both
        foreach (string sub in new[] { " where Country = 'nl' and Name = 'n2'", " where Country != 'nl'", " where IsActive = true and Name != 'n1'" })
            oracle.Check(FieldJoin(sub, order));

        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));

        // the score of a row is the sum over its matched keys: the server reads the join
        oracle.Check(FieldJoin("", " order by score() limit 0, 10"), expectServed: false);
        oracle.Check(FieldJoin(" where Country = 'nl'", " order by score() limit 0, 10"), expectServed: false);
        oracle.Check(FieldLeftJoin(" where Country = 'nl'", " boost 3 order by score() limit 0, 10"), expectServed: false);
        Assert.Equal(3, oracle.FallbacksOf(ScoresMultiKey));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ARowWithTwoKeysInGroupsOfOneEntryEachIsNotTheListsAlgebra()
    {
        using JoinIndexHarness h = await CreateAsync(this);

        // every group holds one entry: the row of two keys is the only one joined with two inner entries, and no single entry has both atoms
        h.PutCodedContextKeys(1, ["k1"], "nl", "n1", 1, true, "x");
        h.PutCodedContextKeys(2, ["k2"], "de", "n2", 2, false, "y");
        h.PutConversationKeys(1, ["k1", "k2"], "both");
        h.PutConversationKeys(2, ["k1"], "one");
        h.PutConversationKeys(3, ["k2"], "two");
        await h.AssertCurrentAsync();
        AssertCounters(h, 0, 1);
        var oracle = new JoinIndexQueryOracle(h);

        string order = " order by m.Title limit 0, 10";
        foreach (string sub in new[] { " where Country = 'nl' and Name = 'n2'", " where Country != 'nl'", " where Country = 'nl' and IsActive != true", " where IsActive = true and Name != 'n1'" })
            oracle.Check(FieldJoin(sub, order));

        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));

        // the row loses its second key: every row has one inner entry and the algebra is exact
        h.PutConversationKeys(1, ["k1"], "both");
        await h.AssertCurrentAsync();
        AssertCounters(h, 0, 0);
        foreach (string sub in new[] { " where Country = 'nl' and Name = 'n2'", " where Country != 'nl'", " where Country = 'nl' and IsActive != true", " where IsActive = true and Name != 'n1'" })
            oracle.Check(FieldJoin(sub, order));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheScoreOfAnInnerEntryInSeveralGroupsIsTheScoreOfTheOneKeyOfTheRow()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutCodedContextKeys(1, ["k1", "k2", "k3"], "nl", "n1", 1, true, "x");
        h.PutCodedContextKeys(2, ["k1"], "nl", "n2", 2, true, "y");
        h.PutCodedContextKeys(3, ["k3", "k4"], "de", "n3", 3, false, "z");
        for (int i = 0; i < 12; i++)
            h.PutConversationKeys(i, [$"k{1 + i % 5}"], i % 2 == 0 ? "a" : "b");

        await h.AssertCurrentAsync();
        AssertCounters(h, 2, 0);
        var oracle = new JoinIndexQueryOracle(h);

        foreach (string tail in ScoredTails.Concat(UnscoredTails))
        {
            foreach (string sub in new[] { "", " where Country = 'nl'", " where Name = 'n3'", " where IsActive = true", " where Country = 'nobody'" })
            {
                oracle.Check(FieldJoin(sub, tail));
                oracle.Check(FieldLeftJoin(sub, tail));
            }
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task KeysThatAreNoKeysRepeatOrDifferInCaseJoinOnce()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutCodedContextKeys(1, ["k1", "K1", "k1"], "nl", "n1", 1, true, "x");
        h.PutCodedContextKeys(2, [null, "k2"], "nl", "n2", 2, true, "y");
        h.PutCodedContextKeys(3, [], "de", "n2", 3, true, "z");
        h.PutCodedContextKeys(4, null, "nl", "n3", 4, true, "w");
        h.PutCodedContextKeys(5, ["", "k2"], "de", "n1", 5, false, "v");
        h.PutConversationKeys(1, ["K1", "k1"], "repeated");
        h.PutConversationKeys(2, [null, "k2"], "null inside");
        h.PutConversationKeys(3, [], "empty");
        h.PutConversationKeys(4, null, "null");
        h.PutConversationKeys(5, [""], "empty string");
        h.PutConversationKeys(6, ["nobody", "k2", "also nobody"], "orphans");
        h.PutConversationKeys(7, ["K2", "k1"], "mixed case");
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);

        string order = " order by m.Title limit 0, 20";
        foreach (string sub in new[] { "", " where Country = 'nl'", " where Name = 'n2'", " where Name = 'n1'", " where IsActive = true" })
        {
            oracle.Check(FieldJoin(sub, order));
            oracle.Check(FieldLeftJoin(sub, order));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheOrderOfAJoinedFieldIsReadByTheServer()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(4, 4, 4, 200);
        var oracle = new JoinIndexQueryOracle(h);

        oracle.Check(FieldJoin("", " order by c.Name limit 0, 10"), expectServed: false);
        Assert.True(oracle.FallbacksOf(OrderBy) > 0, string.Join(", ", oracle.Info.Fallbacks));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnEntryWithTheMostKeysIsJoinedWithEveryGroup()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        string[] keys = Enumerable.Range(0, JoinLimits.MaxKeysPerEntry).Select(i => "x" + i).ToArray();
        h.PutCodedContextKeys(1, keys, "nl", "n1", 1, true, "x");
        for (int i = 0; i < 30; i++)
            h.PutCodedContextKeys(2 + i, [keys[i * 7 % keys.Length]], i % 2 == 0 ? "nl" : "de", "n" + i % 3, i, true, "y");

        for (int i = 0; i < 40; i++)
            h.PutConversationKeys(i, i % 5 == 0 ? keys : [keys[i * 5 % keys.Length], "other"], i % 3 == 0 ? "a" : "b");

        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);

        string order = " order by m.Title limit 0, 50";
        foreach (string sub in new[] { "", " where Country = 'nl'", " where Country = 'de'", " where Name = 'n1'" })
        {
            oracle.Check(FieldJoin(sub, order));
            oracle.Check(FieldLeftJoin(sub, order));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(1, 1, 4)]
    [InlineData(3, 4, 1)]
    [InlineData(7, 4, 4)]
    [InlineData(12, 4, 4)]
    [InlineData(15, 3, 6)]
    public async Task RandomWorkloadEqualsTheJoinOfTheServer(int seed, int outerKeys, int innerKeys)
    {
        using JoinIndexHarness h = await CreateAsync(this);
        var oracle = new JoinIndexQueryOracle(h);
        var random = new Random(seed);

        // what the join of the server reads because of the groups and the keys, and what it reads for the kind of the condition whenever the score is used
        var allowed = new HashSet<string> { ScoresMultiKey, OrderBy, "KindNotServed:Or", "KindNotServed:Range", "KindNotServed:Between", "KindNotServed:In", "KindNotServed:Method:startsWith" };

        for (int round = 0; round < 8; round++)
        {
            h.RandomFieldArrayRound(random, round == 0 ? 90 : 16, outerKeys, innerKeys);
            if (round % 4 == 3)
                await h.CleanTombstonesAsync();

            await h.AssertCurrentAsync();
            for (int i = 0; i < 9; i++)
            {
                string sub = Subs[random.Next(Subs.Length)];
                string tail = random.Next(2) == 0 ? UnscoredTails[random.Next(UnscoredTails.Length)] : ScoredTails[random.Next(ScoredTails.Length)];
                string rql = random.Next(4) == 0 ? FieldLeftJoin(sub, tail) : FieldJoin(sub, tail);
                (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
                AssertSame(rql, served, scan);
            }
        }

        foreach (string reason in oracle.Info.Fallbacks.Keys)
            Assert.True(allowed.Contains(reason), $"unexpected fallback {reason}: {string.Join(", ", oracle.Info.Fallbacks)}");

        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));
        Assert.True(oracle.Info.Served > 0, "nothing was served");
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ArraysThatChangeBetweenRefreshesAreServedExactly()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        for (int i = 0; i < 8; i++)
            h.PutCodedContextKeys(i, ["k" + i % 4, "k" + (i + 1) % 4], i % 2 == 0 ? "nl" : "de", "n" + i % 3, i, true, "t");

        var oracle = new JoinIndexQueryOracle(h);
        string order = " order by m.Title limit 0, 50";
        string[] subs = ["", " where Country = 'nl'", " where Country = 'de'", " where Name = 'n1'", " where Rank >= 3"];
        var random = new Random(5);

        for (int round = 0; round < 25; round++)
        {
            for (int i = 0; i < 5; i++)
            {
                // the contexts gain, keep and lose keys and atoms, the conversations gain and lose keys
                h.PutCodedContextKeys(random.Next(10), Enumerable.Range(0, random.Next(0, 4)).Select(_ => "k" + random.Next(6)).ToArray(), random.Next(2) == 0 ? "nl" : "de", "n" + random.Next(3), random.Next(20), true, "t");
                h.PutConversationKeys(random.Next(14), Enumerable.Range(0, random.Next(0, 4)).Select(_ => "k" + random.Next(7)).ToArray(), "t" + random.Next(4));
            }

            await h.AssertCurrentAsync();
            foreach (string sub in subs)
            {
                (QueryAnswer served, QueryAnswer scan) = oracle.Both(FieldJoin(sub, order));
                AssertSame(sub, served, scan);
            }
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
        Assert.Equal(subs.Length * 25, oracle.Info.Served);
    }
}

/// <summary>The tests of the class with the join indexes on a field in the storage mode Indirect.</summary>
public class JoinIndexFieldJoinMultiKeyQueryTestsIndirect(ITestOutputHelper output) : JoinIndexFieldJoinMultiKeyQueryTests(output), IIndirectModeTest;
