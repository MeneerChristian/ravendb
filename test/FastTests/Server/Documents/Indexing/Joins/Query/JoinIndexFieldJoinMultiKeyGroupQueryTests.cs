using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>
/// An AND or a NOT in the sub-query of a join on a field whose key fields hold arrays: an outer entry is joined with several inner entries when its key has a group of two or more or when it holds several keys
/// that have a group. Both are answered exactly: the lists answer the entries that are joined with one inner entry, the others are checked against the inner index (one of their keys has an inner entry that
/// satisfies the whole sub-query), and none of it is read by the server.
/// </summary>
public class JoinIndexFieldJoinMultiKeyGroupQueryTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string GroupResidual = "KindNotServed:GroupResidual";
    private const string VerifiedRows = "Join/c/JoinVerifiedRows";
    private const string Order = " order by m.Title limit 0, 50";

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
        " where startsWith(Name, 'n') and Country != 'nl'",
        " where Country in ('nl', 'c2') and IsActive = true",
        " where Name != 'zz' and not (Country = 'nl' or Name = 'n3')"
    ];

    private static readonly string[] UnscoredTails =
    [
        " order by m.Title limit 0, 10",
        " order by m.Title desc limit 3, 7",
        " where m.Title = 'a' order by m.Title limit 0, 1000",
        " order by m.Title limit 1000, 5"
    ];

    private static Dictionary<string, string> Settings(int? maxResidualRows = null)
    {
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };
        if (maxResidualRows != null)
            settings[RavenConfiguration.GetKey(x => x.Join.MaxResidualRows)] = maxResidualRows.Value.ToString();

        return settings;
    }

    private static string FieldJoin(string sub, string tail = Order) => $"{JoinIndexQueryOracle.Outer} join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code{tail}";

    private static string FieldLeftJoin(string sub, string tail = Order) => $"{JoinIndexQueryOracle.Outer} left join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code{tail}";

    private static Task<JoinIndexHarness> CreateAsync(RavenTestBase test, int? maxResidualRows = null) => JoinIndexHarness.CreateFieldJoinAsync(test, Settings(maxResidualRows), arrays: true);

    private static (long GroupsOfSeveral, long RowsWithSeveralKeys) Counters(JoinIndexHarness h)
    {
        Assert.True(h.Join().TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
        using (snapshot)
            return (snapshot.RowsWithSeveralJoined, snapshot.RowsWithSeveralKeys);
    }

    private static string[] Ids(QueryAnswer answer) => answer.Rows.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>Runs the query on the join index and on the scan of the server, asserts the same answer, that the join index served it, and the documents of the answer.</summary>
    private static long Expect(JoinIndexQueryOracle oracle, string rql, string[] ids)
    {
        (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
        AssertSame(rql, served, scan);
        Assert.True(ids.SequenceEqual(Ids(scan)), $"the join of the server returned {string.Join(",", Ids(scan))} instead of {string.Join(",", ids)}: {rql}");
        Assert.True(ids.SequenceEqual(Ids(served)), $"the join index returned {string.Join(",", Ids(served))} instead of {string.Join(",", ids)}: {rql}");
        Assert.True(served.Counters.GetValueOrDefault("Join/c/JoinIndexServed") > 0, $"not served by the join index: {rql}. Fallbacks: {string.Join(", ", oracle.Info.Fallbacks)}");
        return served.Counters.GetValueOrDefault(VerifiedRows);
    }

    private static string[] Conversations(params int[] ids) => ids.Select(x => $"conversations/{x}").OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static void CheckAll(JoinIndexQueryOracle oracle)
    {
        foreach (string sub in AndNotSubs)
        {
            oracle.Check(FieldJoin(sub));
            oracle.Check(FieldLeftJoin(sub));
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnAndOfAtomsThatDifferentEntriesOfOneKeyHaveDoesNotMatchWithArraysOnBothSides()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutCodedContextKeys(1, ["k1", "k2"], "nl", "n1", 1, true, "x");
        h.PutCodedContextKeys(2, ["k1"], "de", "n2", 2, false, "y");
        h.PutCodedContextKeys(3, ["k3"], "nl", "n2", 3, true, "z");
        h.PutCodedContextKeys(4, ["k4", "k5"], "de", "n1", 4, true, "w");
        h.PutConversationKeys(1, ["k1"], "a");
        h.PutConversationKeys(2, ["k2"], "b");
        h.PutConversationKeys(3, ["k3"], "a");
        h.PutConversationKeys(4, ["k4"], "b");
        h.PutConversationKeys(5, ["k5", "k3"], "a");
        h.PutConversationKeys(6, ["k9"], "b");
        h.PutConversationKeys(7, ["k1", "k2"], "a");
        await h.AssertCurrentAsync();
        Assert.Equal((1, 2), Counters(h));
        var oracle = new JoinIndexQueryOracle(h);

        // k1 holds an entry in nl (named n1) and an entry named n2 (in de): no entry has both; k3 has one that has both
        long verified = Expect(oracle, FieldJoin(" where Country = 'nl' and Name = 'n2'"), Conversations(3, 5));
        Assert.True(verified > 0, "nothing was verified");

        // the entries that are not in nl: the second entry of k1, the entry of k4 and k5
        Expect(oracle, FieldJoin(" where Country != 'nl'"), Conversations(1, 4, 5, 7));

        // the first entry has both atoms and is in the groups of k1 and k2; the row 7 holds both keys, which name the same entry
        Expect(oracle, FieldJoin(" where Country = 'nl' and Name = 'n1'"), Conversations(1, 2, 7));
        Expect(oracle, FieldJoin(" where Country = 'de' and Name = 'n1'"), Conversations(4, 5));
        Expect(oracle, FieldJoin(" where Country = 'nl' and Country = 'de'"), []);
        Expect(oracle, FieldJoin(" where Country = 'nl' and Name != 'n1'"), Conversations(3, 5));
        Expect(oracle, FieldJoin(" where Name != 'zz' and not (Country = 'nl' and Name = 'n1')"), Conversations(1, 3, 4, 5, 7));

        // left joins keep every row; the unmatched ones have no joined document
        (QueryAnswer served, QueryAnswer scan) = oracle.Both(FieldLeftJoin(" where Country = 'nl' and Name = 'n2'"));
        AssertSame("left", served, scan);
        Assert.Equal(7, scan.Total);
        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ARowWithTwoKeysNamingTwoGroupsOfOneEntryEachIsCheckedAgainstTheInnerEntries()
    {
        using JoinIndexHarness h = await CreateAsync(this);

        // no group has two entries: the only reason for a check is the row with two keys, whose atoms come from two different inner entries
        h.PutCodedContextKeys(1, ["k1"], "nl", "n1", 1, true, "x");
        h.PutCodedContextKeys(2, ["k2"], "de", "n2", 2, false, "y");
        h.PutConversationKeys(1, ["k1", "k2"], "both");
        h.PutConversationKeys(2, ["k1"], "one");
        h.PutConversationKeys(3, ["k2"], "two");
        h.PutConversationKeys(4, ["k9", "k2"], "orphan and two");
        await h.AssertCurrentAsync();
        Assert.Equal((0, 1), Counters(h));
        var oracle = new JoinIndexQueryOracle(h);

        // the row 1 has nl through k1 and n2 through k2 and no entry has both
        long verified = Expect(oracle, FieldJoin(" where Country = 'nl' and Name = 'n2'"), []);
        Assert.True(verified > 0, "the row with two keys was not checked");
        Expect(oracle, FieldJoin(" where Country != 'nl'"), Conversations(1, 3, 4));
        Expect(oracle, FieldJoin(" where Country != 'nl' and Name = 'n1'"), []);
        Expect(oracle, FieldJoin(" where Country = 'nl' and IsActive != true"), []);
        Expect(oracle, FieldJoin(" where IsActive = true and Name != 'n1'"), []);
        Expect(oracle, FieldJoin(" where IsActive != true and Name != 'n1'"), Conversations(1, 3, 4));
        Expect(oracle, FieldJoin(" where Country = 'nl' and Name = 'n1'"), Conversations(1, 2));
        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));

        // the second key goes: every row is joined with one inner entry and the lists are exact without a check
        h.PutConversationKeys(1, ["k1"], "both");
        await h.AssertCurrentAsync();
        Assert.Equal((0, 0), Counters(h));
        Assert.Equal(0, Expect(oracle, FieldJoin(" where Country = 'nl' and Name = 'n2'"), []));
        Assert.Equal(0, Expect(oracle, FieldJoin(" where Country != 'nl'"), Conversations(3, 4)));

        // the key comes back, and so does the check
        h.PutConversationKeys(1, ["k2", "k1"], "both");
        await h.AssertCurrentAsync();
        Assert.Equal((0, 1), Counters(h));
        Assert.True(Expect(oracle, FieldJoin(" where Country = 'nl' and Name = 'n2'"), []) > 0);
        Expect(oracle, FieldJoin(" where Country != 'nl'"), Conversations(1, 3, 4));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ARowWithTwoKeysNamingOneEntryMatchesWhatThatEntryHas()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutCodedContextKeys(1, ["k1", "k2"], "nl", "n1", 1, true, "x");
        h.PutConversationKeys(1, ["k1", "k2"], "both");
        h.PutConversationKeys(2, ["k1"], "one");
        h.PutConversationKeys(3, ["k2", "k2"], "repeated");
        await h.AssertCurrentAsync();

        // the row 1 has two keys that have a group, but they name the same entry: it is checked (the lists cannot tell), and the answer is the one of the entry
        Assert.Equal((0, 1), Counters(h));
        var oracle = new JoinIndexQueryOracle(h);
        Expect(oracle, FieldJoin(" where Country = 'nl' and Name = 'n1'"), Conversations(1, 2, 3));
        Expect(oracle, FieldJoin(" where Country != 'nl'"), []);
        Expect(oracle, FieldJoin(" where Country = 'nl' and Name != 'n1'"), []);
        Expect(oracle, FieldJoin(" where Country = 'nl' and Name != 'n2'"), Conversations(1, 2, 3));
        Expect(oracle, FieldJoin(" where Country = 'de' and Name = 'n1'"), []);
        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));

        // a second entry joins the key k2: the row 1 and the row 3 are now joined with two entries
        h.PutCodedContextKeys(2, ["k2"], "de", "n2", 2, false, "y");
        await h.AssertCurrentAsync();
        Expect(oracle, FieldJoin(" where Country = 'nl' and Name = 'n2'"), []);
        Expect(oracle, FieldJoin(" where Country != 'nl'"), Conversations(1, 3));
        Expect(oracle, FieldJoin(" where Country = 'nl' and Name = 'n1'"), Conversations(1, 2, 3));
        Expect(oracle, FieldJoin(" where Name = 'n2' and IsActive != true"), Conversations(1, 3));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task UpdatesAndReKeysBetweenRefreshesKeepTheAnswerExact()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        h.PutCodedContextKeys(1, ["k1"], "nl", "n1", 1, true, "x");
        h.PutCodedContextKeys(2, ["k2"], "de", "n2", 2, false, "y");
        h.PutCodedContextKeys(3, ["k3", "k4"], "nl", "n2", 30, true, "z");
        h.PutConversationKeys(1, ["k1", "k2"], "a");
        h.PutConversationKeys(2, ["k1"], "b");
        h.PutConversationKeys(3, ["k3"], "a");
        h.PutConversationKeys(4, ["k4", "k2"], "b");
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        CheckAll(oracle);

        // an inner entry gains a key of a group with an entry: that group has two
        h.PutCodedContextKeys(2, ["k2", "k1"], "de", "n2", 2, false, "y");
        await h.AssertCurrentAsync();
        CheckAll(oracle);

        // the other entry of that group loses the key, the group of k3 gains an entry that is not like the first one
        h.PutCodedContextKeys(1, ["k5"], "nl", "n1", 1, true, "x");
        h.PutCodedContextKeys(4, ["k3"], "c1", "n3", 40, false, "w");
        await h.AssertCurrentAsync();
        CheckAll(oracle);

        // the rows change their keys: from two keys to one, from one to two, from a key that has a group to one that has none, in the same batch as an inner change
        h.PutConversationKeys(1, ["k2"], "a");
        h.PutConversationKeys(2, ["k1", "k3"], "b");
        h.PutConversationKeys(5, ["k5", "k4", "k3"], "a");
        h.PutCodedContextKeys(3, ["k4"], "nl", "n2", 30, true, "z");
        await h.AssertCurrentAsync();
        CheckAll(oracle);

        // the atoms of an entry change while its keys stay
        h.PutCodedContextKeys(2, ["k2", "k1"], "nl", "n3", 2, true, "y");
        await h.AssertCurrentAsync();
        CheckAll(oracle);

        // deletes of rows and of entries, in one batch
        h.Delete("contexts/4");
        h.Delete("conversations/5");
        h.Delete("contexts/2");
        await h.AssertCurrentAsync();
        CheckAll(oracle);

        // and they are put again
        h.PutCodedContextKeys(2, ["k1", "k2", "k3"], "de", "n1", 2, false, "y");
        h.PutConversationKeys(5, ["k1", "k3"], "a");
        await h.AssertCurrentAsync();
        CheckAll(oracle);
        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task BothReasonsForACheckCountAgainstTheBudgetAndAreServedWithinIt()
    {
        using JoinIndexHarness h = await CreateAsync(this, maxResidualRows: 1);

        // the group of big has two entries; the rows 1 and 2 have the key big; the row 3 has two keys of one entry each
        h.PutCodedContextKeys(1, ["big"], "nl", "n1", 1, true, "x");
        h.PutCodedContextKeys(2, ["big"], "de", "n2", 2, false, "y");
        h.PutCodedContextKeys(3, ["k1"], "nl", "n1", 3, true, "x");
        h.PutCodedContextKeys(4, ["k2"], "de", "n2", 4, false, "y");
        h.PutConversationKeys(1, ["big"], "a");
        h.PutConversationKeys(2, ["big"], "a");
        h.PutConversationKeys(3, ["k1", "k2"], "a");
        h.PutConversationKeys(4, ["k1"], "a");
        await h.AssertCurrentAsync();
        Assert.Equal((1, 1), Counters(h));
        var oracle = new JoinIndexQueryOracle(h);

        // a NOT can match any row, and three of them have to be checked: more than the budget of one
        oracle.Check(FieldJoin(" where Country != 'nobody'"), expectServed: false);
        Assert.Equal(1, oracle.FallbacksOf(GroupResidual));

        // the atom Rank = 3 is in the lists of the rows 3 and 4: two rows may be named, one has to be checked at most, three have to be checked at most for the bound
        oracle.Check(FieldJoin(" where Rank = 3 and Country = 'nl'"), expectServed: false);
        Assert.Equal(2, oracle.FallbacksOf(GroupResidual));

        // the atom Rank = 4 is in the list of the row 3 only, and no row has Rank = 9
        oracle.Check(FieldJoin(" where Rank = 4 and Country = 'de'"));
        oracle.Check(FieldJoin(" where Rank = 9 and Country = 'nl'"));
        Assert.Equal(2, oracle.FallbacksOf(GroupResidual));

        // only the rows with two keys are left over (the group of big has no row), and they are two
        h.PutConversationKeys(1, ["k1"], "a");
        h.PutConversationKeys(2, ["k2"], "a");
        h.PutConversationKeys(5, ["k2", "k1"], "a");
        await h.AssertCurrentAsync();
        Assert.Equal((1, 2), Counters(h));
        oracle.Check(FieldJoin(" where Rank = 4 and Country = 'de'"), expectServed: false);
        oracle.Check(FieldJoin(" where Country != 'nobody'"), expectServed: false);
        oracle.Check(FieldJoin(" where Rank = 9 and Country = 'nl'"));
        Assert.Equal(4, oracle.FallbacksOf(GroupResidual));
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(1, 1, 4)]
    [InlineData(4, 4, 1)]
    [InlineData(9, 4, 4)]
    [InlineData(15, 3, 6)]
    [InlineData(21, 4, 4)]
    public async Task RandomWorkloadWithAndNotOverArraysEqualsTheJoinOfTheServer(int seed, int outerKeys, int innerKeys)
    {
        using JoinIndexHarness h = await CreateAsync(this);
        var oracle = new JoinIndexQueryOracle(h);
        var random = new Random(seed);

        // what the join of the server reads for the kind of the condition whenever the score is used, and for the rows with several keys when the score is used; the checks are never a reason
        var allowed = new HashSet<string> { "KindNotServed:ScoresMultiKey", "KindNotServed:OrderByMultiKey", "KindNotServed:Or", "KindNotServed:Range", "KindNotServed:Between", "KindNotServed:In", "KindNotServed:Method:startsWith" };
        string[] scoredTails = [" limit 0, 1000", " order by score() limit 0, 7", " boost 2 order by score() limit 0, 20"];

        long verified = 0;
        for (int round = 0; round < 8; round++)
        {
            h.RandomFieldArrayRound(random, round == 0 ? 90 : 16, outerKeys, innerKeys);
            if (round % 4 == 3)
                await h.CleanTombstonesAsync();

            await h.AssertCurrentAsync();
            for (int i = 0; i < 9; i++)
            {
                string sub = AndNotSubs[random.Next(AndNotSubs.Length)];
                string tail = random.Next(3) != 0 ? UnscoredTails[random.Next(UnscoredTails.Length)] : scoredTails[random.Next(scoredTails.Length)];
                string rql = random.Next(4) == 0 ? FieldLeftJoin(sub, tail) : FieldJoin(sub, tail);
                (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
                AssertSame(rql, served, scan);
                verified += served.Counters.GetValueOrDefault(VerifiedRows);
            }
        }

        foreach (string reason in oracle.Info.Fallbacks.Keys)
            Assert.True(allowed.Contains(reason), $"unexpected fallback {reason}: {string.Join(", ", oracle.Info.Fallbacks)}");

        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));
        Assert.True(oracle.Info.Served > 0, "nothing was served");
        Assert.True(verified > 0, "no outer entry was checked against its keys");
    }
}
