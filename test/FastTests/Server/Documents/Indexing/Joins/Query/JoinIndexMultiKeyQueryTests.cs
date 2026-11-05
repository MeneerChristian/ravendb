using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>
/// A join index whose key field of the outer index holds an array of ids is served like the join of the server: each outer row once, null, missing and empty keys are no keys,
/// the case of the ids does not matter, and the score of a row that matches through several keys is the sum of the scores of the keys. What the lists cannot answer exactly is read by the server and counted.
/// </summary>
public class JoinIndexMultiKeyQueryTests(ITestOutputHelper output) : RavenTestBase(output)
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

    private const string ScoresMultiKey = "KindNotServed:ScoresMultiKey";
    private const string GroupResidual = "KindNotServed:GroupResidual";

    private static Dictionary<string, string> ScoreSettings() => new() { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };

    private static string LeftJoin(string sub, string tail = "") => $"{Outer} left join (from index 'Contexts/Search'{sub}) as c on m.ContextId = id(c){tail}";

    private static bool HasAndOrNot(string sub) => (sub.Contains(" and ") && sub.Contains("between") == false) || sub.Contains("!=");

    private static bool NeedsAScore(string sub) => sub.Contains(" or ") || sub.Contains(" in ") || sub.Contains("Rank >") || sub.Contains("Rank between") || sub.Contains("startsWith") || sub.Contains("Rank <");

    private static Task<JoinIndexHarness> CreateAsync(RavenTestBase test, bool putJoin = true, Dictionary<string, string> settings = null)
    {
        return JoinIndexHarness.CreateAsync(test, settings ?? ScoreSettings(), putJoin, indexes: [JoinIndexHarness.MultiOuterIndex(), JoinIndexHarness.InnerIndex()]);
    }

    private async Task<JoinIndexHarness> HarnessWithDataAsync(int seed, int operations = 200)
    {
        JoinIndexHarness h = await CreateAsync(this);
        h.RandomMultiRound(new Random(seed), operations);
        await h.AssertCurrentAsync();
        return h;
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task EveryKindThatIsServedEqualsTheJoinOfTheServer()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(1, 250);
        var oracle = new JoinIndexQueryOracle(h);

        long expectedServed = 0;
        long expectedResidual = 0;
        foreach (string sub in Subs)
        {
            foreach (string tail in UnscoredTails)
            {
                // an AND or a NOT over the lists is not the join of the server when a row has two joined documents
                bool residual = HasAndOrNot(sub);
                oracle.Check(Join(sub, tail), expectServed: residual == false);
                if (residual)
                    expectedResidual++;
                else
                    expectedServed++;
            }
        }

        Assert.Equal(expectedServed, oracle.Info.Served);
        Assert.Equal(expectedResidual, oracle.FallbacksOf(GroupResidual));
        Assert.Equal(expectedResidual, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheScoreOfTheJoinIsReadByTheServerWhileARowHasSeveralJoinedDocuments()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(2, 250);
        var oracle = new JoinIndexQueryOracle(h);

        int checks = 0;
        foreach (string sub in Subs.Where(x => NeedsAScore(x) == false))
        {
            foreach (string tail in ScoredTails)
            {
                oracle.Check(Join(sub, tail), expectServed: false);
                checks++;
            }
        }

        Assert.Equal(checks, oracle.FallbacksOf(ScoresMultiKey));
        Assert.Equal(checks, oracle.Info.FallbackTotal);
        Assert.Equal(0, oracle.Info.Served);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task LeftJoinsAreTheJoinOfTheServer()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(3, 250);
        var oracle = new JoinIndexQueryOracle(h);

        foreach (string sub in new[] { Subs[0], Subs[1], Subs[4], Subs[6], Subs[9], Subs[12] })
        {
            foreach (string tail in UnscoredTails)
                oracle.Check(LeftJoin(sub, tail));

            foreach (string tail in ScoredTails.Take(3))
                oracle.Check(LeftJoin(sub, tail), expectServed: false);
        }

        Assert.True(oracle.FallbacksOf(ScoresMultiKey) > 0);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnAndAndANotOnARowWithTwoJoinedDocumentsAreNotTheListsAlgebra()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutContext(1, "nl", "n1", 1, true, "x");
        h.PutContext(2, "de", "n2", 2, false, "y");
        h.PutContext(3, "nl", "n2", 3, true, "z");
        h.PutConversationKeys(1, ["contexts/1", "contexts/2"], "both");
        h.PutConversationKeys(2, ["contexts/1"], "one");
        h.PutConversationKeys(3, ["contexts/2"], "two");
        h.PutConversationKeys(4, ["contexts/3", "contexts/2"], "three");
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);

        // conversation 1 is in the list of Country = nl and in the list of Name = n2, but no single context has both; not (Country = nl) is true for context 2
        string order = " order by m.Title limit 0, 10";
        long residual = oracle.FallbacksOf(GroupResidual);
        foreach (string sub in new[] { " where Country = 'nl' and Name = 'n2'", " where Country != 'nl'", " where Country = 'nl' and IsActive != true", " where IsActive = true and Name != 'n1'" })
        {
            oracle.Check(Join(sub, order), expectServed: false);
            Assert.True(oracle.FallbacksOf(GroupResidual) > residual, $"GroupResidual was not counted for{sub}: {string.Join(", ", oracle.Info.Fallbacks)}");
            residual = oracle.FallbacksOf(GroupResidual);
        }

        // without a row that has two joined documents the algebra is exact and the join is served
        h.PutConversationKeys(1, ["contexts/1"], "both");
        h.PutConversationKeys(4, ["contexts/3", "contexts/99"], "three");
        await h.AssertCurrentAsync();
        foreach (string sub in new[] { " where Country = 'nl' and Name = 'n2'", " where Country != 'nl'", " where Country = 'nl' and IsActive != true", " where IsActive = true and Name != 'n1'" })
            oracle.Check(Join(sub, order));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task KeysThatAreNoKeysOrRepeatOrDifferInCaseJoinOnce()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutContext(1, "nl", "n1", 1, true, "x");
        h.PutContext(2, "nl", "n2", 2, true, "y");
        h.PutConversationKeys(1, ["contexts/1", "contexts/1", "CONTEXTS/1"], "repeated");
        h.PutConversationKeys(2, [null, "contexts/2"], "null inside");
        h.PutConversationKeys(3, [], "empty");
        h.PutConversationKeys(4, null, "null");
        h.PutConversationKeys(5, [""], "empty string");
        h.PutConversationKeys(6, ["nobody", "contexts/2", "also nobody"], "orphans");
        h.PutConversationKeys(7, ["Contexts/2", "contexts/1"], "mixed case");
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);

        string order = " order by m.Title limit 0, 20";
        foreach (string sub in new[] { "", " where Country = 'nl'", " where Name = 'n2'", " where Name = 'n1'", " where IsActive = true" })
        {
            oracle.Check(Join(sub, order));
            oracle.Check(LeftJoin(sub, order));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ASingleKeyPerRowIsServedWithScoresAndWithTheOrderOfAJoinedField()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutContext(1, "nl", "n1", 1, true, "x");
        h.PutContext(2, "de", "n2", 2, true, "y");
        for (int i = 0; i < 20; i++)
            h.PutConversationKeys(i, i % 5 == 0 ? [] : [$"contexts/{1 + i % 2}", i % 4 == 0 ? "contexts/1" : $"contexts/{1 + i % 2}"], i % 3 == 0 ? "a" : "b");

        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);

        // every row has one distinct joined document at most (repeated keys are one key)
        foreach (string tail in ScoredTails.Concat(UnscoredTails))
        {
            foreach (string sub in new[] { "", " where Country = 'nl' and IsActive = true", " where Country != 'nl'", " where Name = 'n2'" })
                oracle.Check(Join(sub, tail), expectServed: true);
        }

        oracle.Check(Join("", " order by c.Name limit 0, 10"), expectServed: true);

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheOrderOfAJoinedFieldIsReadByTheServerWhileARowHasSeveralJoinedDocuments()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(4, 200);
        var oracle = new JoinIndexQueryOracle(h);

        oracle.Check(Join("", " order by c.Name limit 0, 10"), expectServed: false);
        Assert.True(oracle.FallbacksOf("KindNotServed:OrderByMultiKey") > 0, string.Join(", ", oracle.Info.Fallbacks));
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
        // what the join of the server reads because of the several keys, and what it reads for the kind of the condition whenever the score is used
        var allowed = new HashSet<string> { ScoresMultiKey, GroupResidual, "KindNotServed:OrderByMultiKey", "KindNotServed:Or", "KindNotServed:Range", "KindNotServed:Between", "KindNotServed:In", "KindNotServed:Method:startsWith" };

        for (int round = 0; round < 8; round++)
        {
            h.RandomMultiRound(random, round == 0 ? 80 : 14);
            if (round % 4 == 3)
                await h.CleanTombstonesAsync();

            await h.AssertCurrentAsync();
            for (int i = 0; i < 9; i++)
            {
                string sub = Subs[random.Next(Subs.Length)];
                string tail = random.Next(2) == 0 ? UnscoredTails[random.Next(UnscoredTails.Length)] : ScoredTails[random.Next(ScoredTails.Length)];
                string rql = random.Next(4) == 0 ? LeftJoin(sub, tail) : Join(sub, tail);
                (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
                AssertSame(rql, served, scan);
            }
        }

        foreach (string reason in oracle.Info.Fallbacks.Keys)
            Assert.True(allowed.Contains(reason), $"unexpected fallback {reason}: {string.Join(", ", oracle.Info.Fallbacks)}");

        Assert.True(oracle.Info.Served > 0, "nothing was served");
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ArraysThatChangeBetweenRefreshesAreServedExactly()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        for (int i = 0; i < 6; i++)
            h.PutContext(i, i % 2 == 0 ? "nl" : "de", "n" + i % 3, i, true, "t");

        var oracle = new JoinIndexQueryOracle(h);
        string order = " order by m.Title limit 0, 50";
        string[] subs = ["", " where Country = 'nl'", " where Country = 'de'", " where Name = 'n1'", " where Rank >= 3"];
        var random = new Random(5);

        for (int round = 0; round < 25; round++)
        {
            for (int i = 0; i < 5; i++)
                h.PutConversationKeys(random.Next(12), Enumerable.Range(0, random.Next(0, 5)).Select(_ => $"contexts/{random.Next(8)}").ToArray(), "t" + random.Next(4));

            await h.AssertCurrentAsync();
            foreach (string sub in subs)
            {
                (QueryAnswer served, QueryAnswer scan) = oracle.Both(Join(sub, order));
                AssertSame(sub, served, scan);
            }
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
        Assert.Equal(subs.Length * 25, oracle.Info.Served);
    }
}
