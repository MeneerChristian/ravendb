using System.Collections.Generic;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>What is particular to serving from an Indirect file: the budget counts keys, outer inserts never put the join index behind, and the union of the keys reads the outer snapshot of the query.</summary>
public class JoinIndexIndirectQueryTests(ITestOutputHelper output) : RavenTestBase(output), IIndirectModeTest
{
    private const string GroupResidual = "KindNotServed:GroupResidual";
    private const string BehindTimeout = "BehindTimeout";
    private const string Order = " order by m.Title limit 0, 50";

    private static string FieldJoin(string sub, string tail = "") => $"{JoinIndexQueryOracle.Outer} join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code{tail}";

    private static string FieldLeftJoin(string sub, string tail = "") => $"{JoinIndexQueryOracle.Outer} left join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code{tail}";

    private static Dictionary<string, string> Settings(int maxResidualKeys) => new()
    {
        [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString(),
        [RavenConfiguration.GetKey(x => x.Join.MaxResidualKeys)] = maxResidualKeys.ToString()
    };

    private static async Task SeedBudgetDataAsync(JoinIndexHarness h)
    {
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
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheBudgetOfAnIndirectJoinCountsTheKeysWithSeveralInnerEntries()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, Settings(maxResidualKeys: 1));
        await SeedBudgetDataAsync(h);
        var oracle = new JoinIndexQueryOracle(h);

        // a NOT can name every key: 2 keys have several inner entries (7 outer entries, which an Expanded join index would count), more than the budget of 1
        oracle.Check(FieldJoin(" where Country != 'nobody'", Order), expectServed: false);
        Assert.Equal(1, oracle.FallbacksOf(GroupResidual));

        // no key has the atom, so nothing is checked
        oracle.Check(FieldJoin(" where Country = 'nl' and Name = 'n9'", Order));

        // one key (small) has the atom Rank = 3: one key to check, within the budget, and it is checked against the inner index
        QueryAnswer served = oracle.Both(FieldJoin(" where Rank = 3 and Country = 'nl'", Order)).Served;
        Assert.True(served.Counters.GetValueOrDefault("Join/c/JoinVerifiedKeys") >= 1, string.Join(", ", served.Counters));
        oracle.Check(FieldJoin(" where Country = 'nl'", Order));
        Assert.Equal(1, oracle.FallbacksOf(GroupResidual));
        Assert.Equal(1, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task WithinTheBudgetOfKeysAnAndAndANotAreServedAndExact()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, Settings(maxResidualKeys: 2));
        await SeedBudgetDataAsync(h);
        var oracle = new JoinIndexQueryOracle(h);

        foreach (string sub in new[] { " where Country != 'nobody'", " where Country = 'nl' and Name = 'n2'", " where Country = 'nl' and Name = 'n1'", " where Country != 'nl'", " where IsActive = true and Name != 'n1'" })
        {
            oracle.Check(FieldJoin(sub, Order));
            oracle.Check(FieldLeftJoin(sub, Order));
        }

        Assert.Equal(0, oracle.FallbacksOf(GroupResidual));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task OuterInsertsNeverPutTheJoinBehindForAQueryThatDoesNotScore()
    {
        // the outer batches are coalesced for a minute: the rows of the new outer entries are not in the join index when the queries run
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.RefreshWindow)] = "60000" };
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, settings, putJoin: false);
        h.PutCodedContext(1, "k1", "nl", "n1", 1, true, "x");
        h.PutCodedContext(2, "k1", "de", "n2", 2, false, "y");
        h.PutCodedContext(3, "k2", "nl", "n2", 3, true, "z");
        h.PutConversation(1, "k1", "a");
        for (int i = 0; i < 1000 && h.Stats(JoinIndexHarness.OuterName).IsStale; i++)
            await Task.Delay(20);

        await h.PutJoinAsync(JoinIndexHarness.FieldJoinIndexDefinition());
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        oracle.Check(FieldJoin("", Order));

        long refreshes = h.Join().Counters.Refreshes;
        for (int i = 2; i < 40; i++)
            h.PutConversation(i, i % 3 == 0 ? "k2" : "k1", "b" + i);

        for (int i = 0; i < 1000 && h.Stats(JoinIndexHarness.OuterName).IsStale; i++)
            await Task.Delay(20);

        // a rewritten document and a deleted one too: the join index has the old rows of both
        h.PutConversation(1, "k2", "moved");
        h.Delete("conversations/5");
        for (int i = 0; i < 1000 && h.Stats(JoinIndexHarness.OuterName).IsStale; i++)
            await Task.Delay(20);

        Assert.Equal(refreshes, h.Join().Counters.Refreshes);
        foreach (string sub in new[] { "", " where Country = 'nl'", " where Country = 'de' or Name = 'n2'", " where Country = 'nl' and Name = 'n2'", " where Country != 'nl'" })
        {
            oracle.Check(FieldJoin(sub, Order));
            oracle.Check(FieldLeftJoin(sub, Order));
        }

        Assert.Equal(0, oracle.FallbacksOf(BehindTimeout));
        Assert.Equal(refreshes, h.Join().Counters.Refreshes);

        // a query that scores reads the rows with several keys of the file, which are not the rows of its snapshot: it is read by the server, and exact
        oracle.Check(FieldJoin("", " order by score() limit 0, 50"), expectServed: false);
        Assert.True(oracle.FallbacksOf(BehindTimeout) >= 1, string.Join(", ", oracle.Info.Fallbacks));
    }
}
