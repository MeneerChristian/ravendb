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
/// An AND or a NOT in the sub-query of a join on a field is exact for every group of inner entries: the lists answer the outer entries whose key has one inner entry, and the outer entries whose key has
/// several are checked entry by entry against the inner index. The answer is the answer of the join of the server, and it is served, not read by the server.
/// </summary>
public class JoinIndexFieldJoinGroupQueryTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string GroupResidual = "KindNotServed:GroupResidual";
    private const string MaxResidualRowsKey = "Join.MaxResidualRows";
    // the outer entries that were checked against the inner index, and for an Indirect file the keys with several inner entries that were
    private static long Verified(QueryAnswer answer) => answer.Counters.GetValueOrDefault("Join/c/JoinVerifiedRows") + answer.Counters.GetValueOrDefault("Join/c/JoinVerifiedKeys");

    private static readonly string[] AndNotSubs =
    [
        " where Country = 'nl' and Name = 'n2'",
        " where Country = 'nl' and Name = 'n1' and IsActive = true",
        " where Country = 'nl' and Country = 'c1'",
        " where Country != 'nl'",
        " where Country != 'nl' and Name != 'n1'",
        " where Country = 'nl' and IsActive != true",
        " where IsActive = true and Name != 'n1'",
        " where Name != 'zz' and not (Country = 'nl' and IsActive = true)",
        " where (Country = 'nl' or Country = 'c1') and Name != 'n2'",
        " where Country = 'nl' and (Name = 'n1' or Name = 'n2')",
        " where (Country = 'nl' and Name = 'n1') or (Country = 'c1' and IsActive = true)",
        " where Rank > 10 and Country = 'nl'",
        " where Rank >= 5 and Rank <= 30 and IsActive = true",
        " where startsWith(Name, 'n') and Country != 'nl'",
        " where Country in ('nl', 'c2') and IsActive = true",
        " where Name != 'zz' and not (Country = 'nl' or Name = 'n3')"
    ];

    // the score of the join is not used
    private static readonly string[] UnscoredTails =
    [
        " order by m.Title limit 0, 10",
        " order by m.Title desc limit 3, 7",
        " where m.Title = 'a' order by m.Title limit 0, 1000",
        " order by m.Title limit 1000, 5"
    ];

    private static readonly string[] ScoredTails =
    [
        " limit 0, 1000",
        " order by score() limit 0, 7",
        " order by score() desc limit 3, 7",
        " boost 2 order by score() limit 0, 20"
    ];

    private static Dictionary<string, string> Settings(int? maxResidualRows = null)
    {
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };
        if (maxResidualRows != null)
        {
            settings[MaxResidualRowsKey] = maxResidualRows.Value.ToString();
            settings[RavenConfiguration.GetKey(x => x.Join.MaxResidualKeys)] = maxResidualRows.Value.ToString();
        }

        return settings;
    }

    private static string FieldJoin(string sub, string tail = "") => $"{Outer} join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code{tail}";

    private static string FieldLeftJoin(string sub, string tail = "") => $"{Outer} left join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code{tail}";

    private static bool NeedsAScore(string sub) => sub.Contains(" or ") || sub.Contains(" in ") || sub.Contains("Rank >") || sub.Contains("Rank <") || sub.Contains("startsWith");

    private static long GroupsWithSeveralEntries(JoinIndexHarness h)
    {
        Assert.True(h.Join().TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
        using (snapshot)
            return snapshot.RowsWithSeveralJoined;
    }

    private static string[] Ids(QueryAnswer answer) => answer.Rows.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>Contexts and conversations of every group shape: groups of one, two and three entries, entries of a group that share every atom and entries that share none, case, null and a key without a context.</summary>
    private static void PutGroups(JoinIndexHarness h)
    {
        h.PutCodedContext(1, "k1", "nl", "n1", 1, true, "x");
        h.PutCodedContext(2, "k1", "de", "n2", 2, false, "y");
        h.PutCodedContext(3, "k2", "nl", "n2", 3, true, "z");
        h.PutCodedContext(4, "k3", "nl", "n1", 4, true, "x");
        h.PutCodedContext(5, "k3", "nl", "n1", 4, true, "x");
        h.PutCodedContext(6, "k4", "de", "n3", 20, false, "x");
        h.PutCodedContext(7, "k4", "de", "n3", 21, false, "x");
        h.PutCodedContext(8, "k4", "c9", "n3", 22, true, "x");
        h.PutCodedContext(9, "k5", "c1", "n1", 40, true, "x");
        h.PutCodedContext(10, "k5", "c1", "n4", 41, false, "x");
        h.PutConversation(1, "k1", "a");
        h.PutConversation(2, "k2", "b");
        h.PutConversation(3, "K1", "a");
        h.PutConversation(4, "k3", "b");
        h.PutConversation(5, "k4", "a");
        h.PutConversation(6, "k9", "b");
        h.PutConversation(7, "k4", "a");
        h.PutConversation(8, null, "b");
        h.PutConversation(9, "k5", "a");
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnAndOfAtomsThatDifferentEntriesOfOneKeyHaveDoesNotMatchTheKey()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, Settings());
        PutGroups(h);
        await h.AssertCurrentAsync();
        Assert.Equal(4, GroupsWithSeveralEntries(h));
        var oracle = new JoinIndexQueryOracle(h);
        string order = " order by m.Title limit 0, 20";

        // k1 has an entry in nl (named n1) and an entry named n2 (in de): no entry of k1 has both, k2 has one entry with both
        (QueryAnswer served, QueryAnswer scan) = oracle.Both(FieldJoin(" where Country = 'nl' and Name = 'n2'", order));
        AssertSame("nl and n2", served, scan);
        Assert.Equal(["conversations/2"], Ids(served));
        Assert.Equal(["conversations/2"], Ids(scan));
        Assert.True(Verified(served) > 0, "the group of k1 was not verified: " + string.Join(", ", served.Counters));

        // two values of one single-valued field: only an entry of its own could have both, none does
        (served, scan) = oracle.Both(FieldJoin(" where Country = 'nl' and Country = 'de'", order));
        AssertSame("nl and de", served, scan);
        Assert.Empty(Ids(served));

        // a NOT is true for a key when one of its entries is not in nl: k1 (the entry in de), k4 and k5, not k2 and k3
        (served, scan) = oracle.Both(FieldJoin(" where Country != 'nl'", order));
        AssertSame("not nl", served, scan);
        Assert.Equal(["conversations/1", "conversations/3", "conversations/5", "conversations/7", "conversations/9"], Ids(served));

        (served, scan) = oracle.Both(FieldJoin(" where Name != 'zz' and not (Country = 'nl' and IsActive = true)", order));
        AssertSame("not (nl and active)", served, scan);

        Assert.Equal(4, oracle.Info.Served);
        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));
        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task EveryAndNotAndNestingEqualsTheJoinOfTheServerOnEveryGroupShape()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, Settings());
        PutGroups(h);
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);

        long served = 0;
        foreach (string sub in AndNotSubs)
        {
            foreach (string tail in UnscoredTails)
            {
                oracle.Check(FieldJoin(sub, tail));
                oracle.Check(FieldLeftJoin(sub, tail));
                served += 2;
            }
        }

        Assert.Equal(served, oracle.Info.Served);
        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));
        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ScoredJoinsAndLeftJoinsWithAnAndOrANotEqualTheJoinOfTheServer()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, Settings());
        PutGroups(h);
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);

        long served = 0;
        foreach (string sub in AndNotSubs.Where(x => NeedsAScore(x) == false))
        {
            foreach (string tail in ScoredTails)
            {
                oracle.Check(FieldJoin(sub, tail));
                oracle.Check(FieldLeftJoin(sub, tail));
                served += 2;
            }
        }

        Assert.Equal(served, oracle.Info.Served);
        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AHubKeyOnBothSidesIsVerifiedExactly()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, Settings());

        // one key with 40 inner entries of all shapes and 50 outer entries, next to keys of one entry
        for (int i = 0; i < 40; i++)
            h.PutCodedContext(i, "hub", i % 3 == 0 ? "nl" : "de", "n" + i % 4, i, i % 2 == 0, "t");

        h.PutCodedContext(100, "solo1", "nl", "n1", 5, true, "t");
        h.PutCodedContext(101, "solo2", "de", "n2", 6, false, "t");
        for (int i = 0; i < 60; i++)
            h.PutConversation(i, i < 50 ? "hub" : "solo" + (i % 2 + 1), i % 2 == 0 ? "a" : "b");

        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        string order = " order by m.Title limit 0, 100";

        foreach (string sub in new[] { " where Country = 'nl' and Name = 'n1'", " where Country = 'nl' and Country = 'de'", " where Country != 'nl'", " where Country = 'nl' and IsActive != true and Name != 'n9'", " where Rank > 100 and Country = 'nl'" })
            oracle.Check(FieldJoin(sub, order));

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AGroupThatShrinksToOneEntryAndGrowsAgainKeepsEveryAndExact()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, Settings());
        h.PutCodedContext(1, "k1", "nl", "n1", 1, true, "x");
        h.PutCodedContext(2, "k1", "de", "n2", 2, false, "y");
        h.PutConversation(1, "k1", "a");
        h.PutConversation(2, "k2", "b");
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        string order = " order by m.Title limit 0, 20";
        string[] subs = [" where Country = 'nl' and Name = 'n2'", " where Country != 'nl'", " where Country = 'de' and IsActive != true", " where Country = 'nl' and Name = 'n1'"];

        void CheckAll()
        {
            foreach (string sub in subs)
                oracle.Check(FieldJoin(sub, order));
        }

        CheckAll();

        // an entry of k1 moves to k2: k1 has one entry, k2 has two
        h.PutCodedContext(3, "k2", "nl", "n2", 3, true, "z");
        h.PutCodedContext(2, "k2", "de", "n2", 2, false, "y");
        await h.AssertCurrentAsync();
        Assert.Equal(1, GroupsWithSeveralEntries(h));
        CheckAll();

        // k1 has no entry now
        h.Delete("contexts/1");
        await h.AssertCurrentAsync();
        CheckAll();

        // a group of two again, in the same batch as a change of the outer entries
        h.PutCodedContext(1, "k1", "nl", "n1", 1, true, "x");
        h.PutCodedContext(4, "k1", "de", "n2", 9, true, "w");
        h.PutConversation(3, "K1", "c");
        h.Delete("conversations/1");
        await h.AssertCurrentAsync();
        CheckAll();

        h.Delete("contexts/4");
        h.PutCodedContext(2, "k1", "nl", "n2", 2, false, "y");
        await h.AssertCurrentAsync();
        CheckAll();

        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));
        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AboveTheBudgetOfResidualRowsTheJoinIsReadByTheServerAndIsCounted()
    {
        // an Indirect file counts keys against the budget (JoinIndexIndirectQueryTests)
        if (this is IIndirectModeTest)
            return;

        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, Settings(maxResidualRows: 3));

        // 6 outer entries have the key big and 1 has the key small (both keys have two inner entries); the key one has one inner entry
        h.PutCodedContext(1, "big", "nl", "n1", 1, true, "x");
        h.PutCodedContext(2, "big", "de", "n2", 2, false, "y");
        h.PutCodedContext(3, "small", "nl", "n1", 3, true, "x");
        h.PutCodedContext(4, "small", "de", "n2", 4, false, "y");
        h.PutCodedContext(5, "one", "nl", "n2", 5, true, "x");
        for (int i = 0; i < 6; i++)
            h.PutConversation(i, "big", "a");

        h.PutConversation(10, "small", "b");
        h.PutConversation(11, "one", "b");
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        string order = " order by m.Title limit 0, 50";

        // a NOT can match any outer entry, and 7 of them have a key with several inner entries: more than the budget
        oracle.Check(FieldJoin(" where Country != 'nobody'", order), expectServed: false);
        Assert.Equal(1, oracle.FallbacksOf(GroupResidual));

        // no entry has the atom, so nothing is checked
        oracle.Check(FieldJoin(" where Country = 'nl' and Name = 'n9'", order));
        Assert.Equal(1, oracle.FallbacksOf(GroupResidual));

        // one outer entry (of the key small) has the atom Rank = 3
        oracle.Check(FieldJoin(" where Rank = 3 and Country = 'nl'", order));
        Assert.Equal(1, oracle.FallbacksOf(GroupResidual));

        // a condition without AND or NOT is never limited by the budget
        oracle.Check(FieldJoin(" where Country = 'nl'", order));
        Assert.Equal(1, oracle.FallbacksOf(GroupResidual));
        Assert.Equal(1, oracle.Info.FallbackTotal);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(21)]
    public async Task RandomWorkloadWithAndNotEqualsTheJoinOfTheServer(int seed)
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, Settings());
        var oracle = new JoinIndexQueryOracle(h);
        var random = new Random(seed);

        // what the join of the server reads for the kind of the condition whenever the score is used; the groups are never a reason
        var allowed = new HashSet<string> { "KindNotServed:OrderByMultiKey", "KindNotServed:Or", "KindNotServed:Range", "KindNotServed:Between", "KindNotServed:In", "KindNotServed:Method:startsWith" };

        long verified = 0;
        for (int round = 0; round < 8; round++)
        {
            h.RandomFieldRound(random, round == 0 ? 90 : 16);
            if (round % 4 == 3)
                await h.CleanTombstonesAsync();

            await h.AssertCurrentAsync();
            for (int i = 0; i < 9; i++)
            {
                string sub = AndNotSubs[random.Next(AndNotSubs.Length)];
                string tail = random.Next(2) == 0 ? UnscoredTails[random.Next(UnscoredTails.Length)] : ScoredTails[random.Next(ScoredTails.Length)];
                string rql = random.Next(4) == 0 ? FieldLeftJoin(sub, tail) : FieldJoin(sub, tail);
                (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
                AssertSame(rql, served, scan);
                verified += Verified(served);
            }
        }

        foreach (string reason in oracle.Info.Fallbacks.Keys)
            Assert.True(allowed.Contains(reason), $"unexpected fallback {reason}: {string.Join(", ", oracle.Info.Fallbacks)}");

        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));
        Assert.True(oracle.Info.Served > 0, "nothing was served");
        Assert.True(verified > 0, "no outer entry was verified against its group");
    }
}

/// <summary>The tests of the class with the join indexes on a field in the storage mode Indirect.</summary>
public class JoinIndexFieldJoinGroupQueryTestsIndirect(ITestOutputHelper output) : JoinIndexFieldJoinGroupQueryTests(output), IIndirectModeTest;
