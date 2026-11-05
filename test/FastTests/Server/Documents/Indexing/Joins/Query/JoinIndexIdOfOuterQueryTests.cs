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
/// A join index of a join of the id of the main document with a field of the joined index (ON id(m) = c.Code, ON c.Code = id(m)) is served like the join of the server: one outer row per document,
/// many inner entries per row, the key of the row is its lowercased document id. The score of a row is one constant (it has one key), AND and NOT are exact through the verification of the rows with
/// several inner entries, and what the lists cannot answer is read by the server and counted as for every join on a field.
/// </summary>
public class JoinIndexIdOfOuterQueryTests(ITestOutputHelper output) : RavenTestBase(output)
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

    private static string IdJoin(string sub, string tail = "") => $"{Outer} join (from index 'Contexts/Search'{sub}) as c on id(m) = c.Code{tail}";

    private static string IdJoinReversed(string sub, string tail = "") => $"{Outer} join (from index 'Contexts/Search'{sub}) as c on c.Code = id(m){tail}";

    private static string IdLeftJoin(string sub, string tail = "") => $"{Outer} left join (from index 'Contexts/Search'{sub}) as c on id(m) = c.Code{tail}";

    private static bool NeedsAScore(string sub) => sub.Contains(" or ") || sub.Contains(" in ") || sub.Contains("Rank >") || sub.Contains("Rank between") || sub.Contains("startsWith") || sub.Contains("Rank <");

    private static Task<JoinIndexHarness> CreateAsync(RavenTestBase test, bool arrays = true) => JoinIndexHarness.CreateFieldJoinAsync(test, ScoreSettings(), arrays: arrays, outerId: true);

    private async Task<JoinIndexHarness> HarnessWithDataAsync(int seed, int innerKeys, bool arrays = true, int operations = 250)
    {
        JoinIndexHarness h = await CreateAsync(this, arrays);
        h.RandomIdRound(new Random(seed), operations, innerKeys, arrays);
        await h.AssertCurrentAsync();
        return h;
    }

    private static long GroupsWithSeveralEntries(JoinIndexHarness h)
    {
        Assert.True(h.Join().TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
        using (snapshot)
            return snapshot.RowsWithSeveralJoined;
    }

    public static IEnumerable<object[]> Modes()
    {
        yield return [1, false];
        yield return [1, true];
        yield return [4, true];
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [MemberData(nameof(Modes))]
    public async Task EveryKindThatIsServedEqualsTheJoinOfTheServer(int innerKeys, bool arrays)
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(1, innerKeys, arrays);
        Assert.True(GroupsWithSeveralEntries(h) > 0, "the workload has no conversation with two or more contexts");
        var oracle = new JoinIndexQueryOracle(h);

        long expectedServed = 0;
        foreach (string sub in Subs)
        {
            foreach (string tail in UnscoredTails)
            {
                oracle.Check(IdJoin(sub, tail));
                oracle.Check(IdJoinReversed(sub, tail));
                expectedServed += 2;
            }
        }

        Assert.Equal(expectedServed, oracle.Info.Served);
        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));
        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [MemberData(nameof(Modes))]
    public async Task TheScoreOfARowIsOneConstantAndEqualsTheScoreOfTheServer(int innerKeys, bool arrays)
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(2, innerKeys, arrays);
        var oracle = new JoinIndexQueryOracle(h);

        // a row has one key, so its score is one constant however many inner entries name the document
        long served = 0;
        foreach (string sub in Subs.Where(x => NeedsAScore(x) == false))
        {
            foreach (string tail in ScoredTails)
            {
                oracle.Check(IdJoin(sub, tail));
                oracle.Check(IdJoinReversed(sub, tail));
                served += 2;
            }
        }

        Assert.Equal(served, oracle.Info.Served);
        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [MemberData(nameof(Modes))]
    public async Task LeftJoinsAreTheJoinOfTheServer(int innerKeys, bool arrays)
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(3, innerKeys, arrays);
        var oracle = new JoinIndexQueryOracle(h);

        foreach (string sub in new[] { Subs[0], Subs[1], Subs[4], Subs[6], Subs[12] })
        {
            foreach (string tail in UnscoredTails)
                oracle.Check(IdLeftJoin(sub, tail));

            foreach (string tail in ScoredTails.Take(3))
                oracle.Check(IdLeftJoin(sub, tail));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnAndAndANotOnAHubAreVerifiedAgainstTheInnerIndex()
    {
        using JoinIndexHarness h = await CreateAsync(this);

        // conversation 1 is named by an entry in nl and by an entry named n2 that is not in nl: no entry has both
        h.PutCodedContextKeys(1, ["conversations/1"], "nl", "n1", 1, true, "x");
        h.PutCodedContextKeys(2, ["conversations/1"], "de", "n2", 2, false, "y");
        h.PutCodedContextKeys(3, ["conversations/2"], "nl", "n2", 3, true, "z");
        h.PutCodedContextKeys(4, ["Conversations/3", "conversations/2"], "nl", "n2", 4, true, "w");
        h.PutConversation(1, null, "hub");
        h.PutConversation(2, null, "two");
        h.PutConversation(3, null, "three");
        h.PutConversation(4, null, "none");
        await h.AssertCurrentAsync();
        Assert.Equal(2, GroupsWithSeveralEntries(h));
        var oracle = new JoinIndexQueryOracle(h);

        string order = " order by m.Title limit 0, 10";
        long served = 0;
        foreach (string sub in new[] { " where Country = 'nl' and Name = 'n2'", " where Country != 'nl'", " where Country = 'nl' and IsActive != true", " where IsActive = true and Name != 'n1'" })
        {
            oracle.Check(IdJoin(sub, order));
            oracle.Check(IdJoinReversed(sub, order));
            oracle.Check(IdLeftJoin(sub, order));
            served += 3;
        }

        Assert.Equal(served, oracle.Info.Served);
        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));

        // every conversation has one context: the lists alone are exact
        h.Delete("contexts/2");
        h.Delete("contexts/3");
        await h.AssertCurrentAsync();
        Assert.Equal(0, GroupsWithSeveralEntries(h));
        foreach (string sub in new[] { " where Country = 'nl' and Name = 'n2'", " where Country != 'nl'", " where Country = 'nl' and IsActive != true", " where IsActive = true and Name != 'n1'" })
            oracle.Check(IdJoin(sub, order));

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AboveTheBudgetOfResidualRowsTheJoinIsReadByTheServerAndIsCounted()
    {
        Dictionary<string, string> settings = ScoreSettings();
        settings["Join.MaxResidualRows"] = "3";
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, settings, arrays: true, outerId: true);

        // 6 conversations are named by two contexts each, 1 by one context
        for (int i = 0; i < 6; i++)
        {
            h.PutConversation(i, null, "a");
            h.PutCodedContextKeys(2 * i, [$"conversations/{i}"], "nl", "n1", 1, true, "x");
            h.PutCodedContextKeys(2 * i + 1, [$"conversations/{i}"], "de", "n2", 2, false, "y");
        }

        h.PutConversation(10, null, "b");
        h.PutCodedContextKeys(20, ["conversations/10"], "nl", "n2", 5, true, "x");
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        string order = " order by m.Title limit 0, 50";

        // a NOT can match any conversation, and 6 of them have two contexts: more than the budget
        oracle.Check(IdJoin(" where Country != 'nobody'", order), expectServed: false);
        Assert.Equal(1, oracle.FallbacksOf(GroupResidual));

        // no context has the atom, so nothing is checked
        oracle.Check(IdJoin(" where Country = 'nl' and Name = 'n9'", order));
        Assert.Equal(1, oracle.FallbacksOf(GroupResidual));

        // a condition without AND or NOT is never limited by the budget
        oracle.Check(IdJoin(" where Country = 'nl'", order));
        Assert.Equal(1, oracle.FallbacksOf(GroupResidual));
        Assert.Equal(1, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ConversationsCreatedAfterAndBeforeTheContextsThatNameThemAreJoined()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        var oracle = new JoinIndexQueryOracle(h);
        string order = " order by m.Title limit 0, 50";
        string[] subs = ["", " where Country = 'nl'", " where Name = 'n2'", " where IsActive = true and Name != 'n1'", " where Country = 'nl' and Name = 'n2'"];

        // the contexts name conversations that do not exist yet
        for (int i = 0; i < 6; i++)
            h.PutCodedContextKeys(i, [$"conversations/{i % 4}", $"conversations/{(i + 1) % 4}"], i % 2 == 0 ? "nl" : "de", "n" + i % 3, i, i % 2 == 0, "t");

        await h.AssertCurrentAsync();
        foreach (string sub in subs)
        {
            oracle.Check(IdJoin(sub, order));
            oracle.Check(IdLeftJoin(sub, order));
        }

        // the conversations come into being one by one, in an order unrelated to the contexts
        foreach (int id in new[] { 2, 0, 3, 1 })
        {
            h.PutConversation(id, null, "c" + id);
            await h.AssertCurrentAsync();
            foreach (string sub in subs)
            {
                oracle.Check(IdJoin(sub, order));
                oracle.Check(IdJoinReversed(sub, order));
            }
        }

        // conversations that are created first and named afterwards
        h.PutConversation(10, null, "late");
        h.PutConversation(11, null, "later");
        await h.AssertCurrentAsync();
        h.PutCodedContextKeys(20, ["conversations/10", "conversations/11"], "nl", "n2", 5, true, "t");
        h.PutCodedContextKeys(21, ["CONVERSATIONS/10"], "de", "n1", 6, true, "t");
        await h.AssertCurrentAsync();
        foreach (string sub in subs)
            oracle.Check(IdJoin(sub, order));

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task DeletesUpdatesAndRekeysOfBothSidesAreServedExactly()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        for (int i = 0; i < 10; i++)
        {
            h.PutConversation(i, null, i % 2 == 0 ? "a" : "b");
            h.PutCodedContextKeys(i, [$"conversations/{i % 5}"], i % 2 == 0 ? "nl" : "de", "n" + i % 3, i, true, "t");
        }

        var oracle = new JoinIndexQueryOracle(h);
        string order = " order by m.Title limit 0, 50";
        string[] subs = ["", " where Country = 'nl'", " where Country = 'de'", " where Name = 'n1'", " where Country != 'nl' and Name = 'n1'"];
        var random = new Random(8);

        for (int round = 0; round < 25; round++)
        {
            for (int i = 0; i < 5; i++)
            {
                switch (random.Next(5))
                {
                    case 0:
                        h.Delete($"conversations/{random.Next(12)}");
                        break;
                    case 1:
                        h.PutConversation(random.Next(12), null, "t" + random.Next(4));
                        break;
                    case 2:
                        h.PutCodedContextKeys(random.Next(12), Enumerable.Range(0, random.Next(0, 4)).Select(_ => $"conversations/{random.Next(14)}").ToArray(), random.Next(2) == 0 ? "nl" : "de", "n" + random.Next(3), random.Next(20), true, "t");
                        break;
                    case 3:
                        h.Delete($"contexts/{random.Next(12)}");
                        break;
                    default:
                        // an update of the conversation that changes nothing about its key
                        h.PutConversation(random.Next(12), "x", "u" + random.Next(4));
                        break;
                }
            }

            await h.AssertCurrentAsync();
            foreach (string sub in subs)
            {
                (QueryAnswer served, QueryAnswer scan) = oracle.Both(IdJoin(sub, order));
                AssertSame(sub, served, scan);
            }
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
        Assert.Equal(subs.Length * 25, oracle.Info.Served);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task IdsInAnotherCaseAndKeysThatAreNoKeysJoinLikeTheServerJoins()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutCodedContextKeys(1, ["Conversations/1"], "nl", "n1", 1, true, "x");
        h.PutCodedContextKeys(2, ["CONVERSATIONS/2", "conversations/2"], "nl", "n2", 2, true, "y");
        h.PutCodedContextKeys(3, [""], "de", "n2", 3, true, "z");
        h.PutCodedContextKeys(4, null, "nl", "n3", 4, true, "w");
        h.PutCodedContextKeys(5, [null, "conversations/1"], "de", "n1", 5, false, "v");
        h.PutCodedContextKeys(6, [], "de", "n1", 6, false, "v");
        h.PutCodedContextKeys(7, ["nobody"], "de", "n1", 7, false, "v");
        for (int i = 1; i <= 4; i++)
            h.PutConversation(i, null, "t" + i);

        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);

        string order = " order by m.Title limit 0, 20";
        foreach (string sub in new[] { "", " where Country = 'nl'", " where Name = 'n2'", " where Name = 'n1'", " where IsActive = true" })
        {
            oracle.Check(IdJoin(sub, order));
            oracle.Check(IdJoinReversed(sub, order));
            oracle.Check(IdLeftJoin(sub, order));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnInnerKeyIndexedAsAKeyIsJoinedLikeTheServerJoins()
    {
        IndexDefinition inner = JoinIndexHarness.FieldMultiInnerIndex();
        inner.Fields["Code"] = new IndexFieldOptions { Indexing = FieldIndexing.Key };
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings(), putJoin: false, indexes: [JoinIndexHarness.OuterIndex(), inner]);
        await h.PutJoinAsync(JoinIndexHarness.FieldJoinIndexDefinition(outerId: true));
        h.RandomIdRound(new Random(11), 200, 4);
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);

        string order = " order by m.Title limit 0, 30";
        foreach (string sub in new[] { "", " where Country = 'nl'", " where Name = 'n1'", " where IsActive = true and Name != 'n1'", " where Country != 'nl'" })
        {
            oracle.Check(IdJoin(sub, order));
            oracle.Check(IdJoinReversed(sub, order));
            oracle.Check(IdLeftJoin(sub, order));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
        Assert.True(oracle.Info.Served > 0);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheOrderOfAJoinedFieldIsReadByTheServer()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(4, 4, true, 200);
        var oracle = new JoinIndexQueryOracle(h);

        oracle.Check(IdJoin("", " order by c.Name limit 0, 10"), expectServed: false);
        Assert.True(oracle.FallbacksOf(OrderBy) > 0, string.Join(", ", oracle.Info.Fallbacks));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheJoinedAliasThatIsProjectedIsReadByTheServer()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(5, 4, true, 120);
        var oracle = new JoinIndexQueryOracle(h);
        string select = " order by m.Title select m.Title, c.Name limit 0, 20";

        long servedBefore = oracle.Info.Served;
        QueryAnswer answer = oracle.Run(IdJoin("", select));
        Assert.Equal(servedBefore, oracle.Info.Served);
        Assert.True(answer.Total > 0);

        // the rows are the ones of the served join
        QueryAnswer plain = oracle.Run(IdJoin("", " order by m.Title limit 0, 20"));
        Assert.Equal(plain.Total, answer.Total);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AJoinOnAFieldOfTheMainDocumentIsNotAnsweredByTheJoinOfTheId()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(6, 4, true, 80);
        var oracle = new JoinIndexQueryOracle(h);

        // the join index holds id(m) = c.Code: a join of the field ContextId of the conversations with the same field of the contexts is another join
        long servedBefore = oracle.Info.Served;
        oracle.Run($"{Outer} join (from index 'Contexts/Search') as c on m.ContextId = c.Code order by m.Title limit 0, 10");
        Assert.Equal(servedBefore, oracle.Info.Served);
        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(1, 1, true)]
    [InlineData(3, 4, true)]
    [InlineData(7, 4, true)]
    [InlineData(12, 1, false)]
    [InlineData(15, 3, true)]
    public async Task RandomWorkloadEqualsTheJoinOfTheServer(int seed, int innerKeys, bool arrays)
    {
        using JoinIndexHarness h = await CreateAsync(this, arrays);
        var oracle = new JoinIndexQueryOracle(h);
        var random = new Random(seed);

        // what the join of the server reads for the kind of the condition whenever the score is used
        var allowed = new HashSet<string> { OrderBy, "KindNotServed:Or", "KindNotServed:Range", "KindNotServed:Between", "KindNotServed:In", "KindNotServed:Method:startsWith" };

        for (int round = 0; round < 8; round++)
        {
            h.RandomIdRound(random, round == 0 ? 90 : 16, innerKeys, arrays);
            if (round % 4 == 3)
                await h.CleanTombstonesAsync();

            await h.AssertCurrentAsync();
            for (int i = 0; i < 9; i++)
            {
                string sub = Subs[random.Next(Subs.Length)];
                string tail = random.Next(2) == 0 ? UnscoredTails[random.Next(UnscoredTails.Length)] : ScoredTails[random.Next(ScoredTails.Length)];
                string rql = random.Next(4) == 0 ? IdLeftJoin(sub, tail) : random.Next(2) == 0 ? IdJoin(sub, tail) : IdJoinReversed(sub, tail);
                (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
                AssertSame(rql, served, scan);
            }
        }

        foreach (string reason in oracle.Info.Fallbacks.Keys)
            Assert.True(allowed.Contains(reason), $"unexpected fallback {reason}: {string.Join(", ", oracle.Info.Fallbacks)}");

        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));
        Assert.True(oracle.Info.Served > 0, "nothing was served");
    }
}
