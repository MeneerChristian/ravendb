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
/// A join index of a join on a field of the inner index (ON m.ContextId = c.Code) is served like the join of the server: many conversations and many contexts share a key, a conversation is returned once,
/// null, empty and missing keys are no keys (an empty string is one), and the score of a row is the score of its key. What the lists cannot answer exactly is read by the server and counted.
/// </summary>
public class JoinIndexFieldJoinQueryTests(ITestOutputHelper output) : RavenTestBase(output)
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
    private const string OrderBy = "KindNotServed:OrderByMultiKey";

    private static Dictionary<string, string> ScoreSettings() => new() { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };

    private static string FieldJoin(string sub, string tail = "") => $"{Outer} join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code{tail}";

    private static string FieldLeftJoin(string sub, string tail = "") => $"{Outer} left join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code{tail}";

    private static bool NeedsAScore(string sub) => sub.Contains(" or ") || sub.Contains(" in ") || sub.Contains("Rank >") || sub.Contains("Rank between") || sub.Contains("startsWith") || sub.Contains("Rank <");

    private static Task<JoinIndexHarness> CreateAsync(RavenTestBase test) => JoinIndexHarness.CreateFieldJoinAsync(test, ScoreSettings());

    private async Task<JoinIndexHarness> HarnessWithDataAsync(int seed, int operations = 200)
    {
        JoinIndexHarness h = await CreateAsync(this);
        h.RandomFieldRound(new Random(seed), operations);
        await h.AssertCurrentAsync();
        return h;
    }

    private static long GroupsWithSeveralEntries(JoinIndexHarness h)
    {
        Assert.True(h.Join().TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
        using (snapshot)
            return snapshot.RowsWithSeveralJoined;
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task EveryKindThatIsServedEqualsTheJoinOfTheServer()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(1, 250);
        Assert.True(GroupsWithSeveralEntries(h) > 0, "the workload has no group of two or more entries");
        var oracle = new JoinIndexQueryOracle(h);

        // an AND or a NOT is verified per inner entry for the keys that have two inner entries, so every kind is served
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
        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));
        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheScoreOfAKeyIsServedAndEqualsTheScoreOfTheServer()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(2, 250);
        var oracle = new JoinIndexQueryOracle(h);

        // an outer entry has one key, so its score is one constant even when the group of the key has many inner entries
        long served = 0;
        foreach (string sub in Subs.Where(x => NeedsAScore(x) == false))
        {
            foreach (string tail in ScoredTails)
            {
                oracle.Check(FieldJoin(sub, tail), expectServed: true);
                served++;
            }
        }

        Assert.Equal(served, oracle.Info.Served);
        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task LeftJoinsAreTheJoinOfTheServer()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(3, 250);
        var oracle = new JoinIndexQueryOracle(h);

        foreach (string sub in new[] { Subs[0], Subs[1], Subs[4], Subs[6], Subs[12] })
        {
            foreach (string tail in UnscoredTails)
                oracle.Check(FieldLeftJoin(sub, tail));

            foreach (string tail in ScoredTails.Take(3))
                oracle.Check(FieldLeftJoin(sub, tail));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnAndAndANotOnAGroupAreNotTheListsAlgebra()
    {
        using JoinIndexHarness h = await CreateAsync(this);

        // the group of k1 has an entry in nl and an entry named n2 that is not in nl: no entry has both
        h.PutCodedContext(1, "k1", "nl", "n1", 1, true, "x");
        h.PutCodedContext(2, "k1", "de", "n2", 2, false, "y");
        h.PutCodedContext(3, "k2", "nl", "n2", 3, true, "z");
        h.PutConversation(1, "k1", "both");
        h.PutConversation(2, "k2", "one");
        h.PutConversation(3, "K1", "three");
        h.PutConversation(4, "k9", "none");
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        Assert.Equal(1, GroupsWithSeveralEntries(h));

        string order = " order by m.Title limit 0, 10";
        foreach (string sub in new[] { " where Country = 'nl' and Name = 'n2'", " where Country != 'nl'", " where Country = 'nl' and IsActive != true", " where IsActive = true and Name != 'n1'" })
        {
            oracle.Check(FieldJoin(sub, order));
            Assert.Equal(0, oracle.FallbacksOf(GroupResidual));
        }

        // every group has one entry: the lists alone are exact
        h.Delete("contexts/2");
        await h.AssertCurrentAsync();
        Assert.Equal(0, GroupsWithSeveralEntries(h));
        foreach (string sub in new[] { " where Country = 'nl' and Name = 'n2'", " where Country != 'nl'", " where Country = 'nl' and IsActive != true", " where IsActive = true and Name != 'n1'" })
            oracle.Check(FieldJoin(sub, order));

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task KeysThatAreNoKeysOrDifferInCaseJoinLikeTheServerJoins()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutCodedContext(1, "k1", "nl", "n1", 1, true, "x");
        h.PutCodedContext(2, "K2", "nl", "n2", 2, true, "y");
        h.PutCodedContext(3, "", "de", "n2", 3, true, "z");
        h.PutCodedContext(4, null, "nl", "n3", 4, true, "w");
        h.PutCodedContext(5, "k2", "de", "n1", 5, false, "v");
        h.PutConversation(1, "K1", "upper");
        h.PutConversation(2, "k2", "lower");
        h.PutConversation(3, "", "empty string");
        h.PutConversation(4, null, "null");
        h.PutConversation(5, "nobody", "orphan");
        h.PutConversation(6, "k1", "plain");
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
        using JoinIndexHarness h = await HarnessWithDataAsync(4, 200);
        var oracle = new JoinIndexQueryOracle(h);

        oracle.Check(FieldJoin("", " order by c.Name limit 0, 10"), expectServed: false);
        Assert.True(oracle.FallbacksOf(OrderBy) > 0, string.Join(", ", oracle.Info.Fallbacks));
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(12)]
    public async Task RandomWorkloadEqualsTheJoinOfTheServer(int seed)
    {
        using JoinIndexHarness h = await CreateAsync(this);
        var oracle = new JoinIndexQueryOracle(h);
        var random = new Random(seed);

        // what the join of the server reads for the kind of the condition whenever the score is used
        var allowed = new HashSet<string> { OrderBy, "KindNotServed:Or", "KindNotServed:Range", "KindNotServed:Between", "KindNotServed:In", "KindNotServed:Method:startsWith" };

        for (int round = 0; round < 8; round++)
        {
            h.RandomFieldRound(random, round == 0 ? 90 : 16);
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

        Assert.True(oracle.Info.Served > 0, "nothing was served");
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task KeysThatMoveBetweenRefreshesAreServedExactly()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        for (int i = 0; i < 8; i++)
            h.PutCodedContext(i, "k" + i % 4, i % 2 == 0 ? "nl" : "de", "n" + i % 3, i, true, "t");

        var oracle = new JoinIndexQueryOracle(h);
        string order = " order by m.Title limit 0, 50";
        string[] subs = ["", " where Country = 'nl'", " where Country = 'de'", " where Name = 'n1'", " where Rank >= 3"];
        var random = new Random(5);

        for (int round = 0; round < 25; round++)
        {
            for (int i = 0; i < 5; i++)
            {
                // the contexts change their key and their atoms, the conversations change their key
                h.PutCodedContext(random.Next(10), "k" + random.Next(6), random.Next(2) == 0 ? "nl" : "de", "n" + random.Next(3), random.Next(20), true, "t");
                h.PutConversation(random.Next(14), "k" + random.Next(7), "t" + random.Next(4));
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
public class JoinIndexFieldJoinQueryTestsIndirect(ITestOutputHelper output) : JoinIndexFieldJoinQueryTests(output), IIndirectModeTest;
