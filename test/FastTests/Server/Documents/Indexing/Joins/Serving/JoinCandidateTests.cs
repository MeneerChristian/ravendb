using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.RedTeam;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Serving;

/// <summary>
/// Several join indexes of the same join keys: a query is answered by the first of them that declares what it needs and is not behind, whatever the others declare. Every answer is compared with the join of the
/// server, which reads the join only when every join index is forced to fall back.
/// </summary>
public class JoinCandidateTests(ITestOutputHelper output) : JoinRedTeamBase(output)
{
    private const string Tail = " order by m.Title limit 0, 1000";

    private static IndexDefinition JoinOf(string name, Action<JoinDefinition> declare)
    {
        var joinIndex = new JoinDefinition { OuterIndex = JoinIndexHarness.OuterName, OuterField = "ContextId", InnerIndex = JoinIndexHarness.InnerName };
        declare(joinIndex);
        return new IndexDefinition { Name = name, Join = joinIndex };
    }

    private static async Task<JoinIndexHarness> HarnessAsync(IReadOnlyList<IndexDefinition> joinIndexes, RavenTestBase test, int seed, Dictionary<string, string> settings = null)
    {
        JoinIndexHarness h = await JoinIndexHarness.CreateAsync(test, settings ?? ScoreSettings(), putJoin: false);
        await h.Store.Maintenance.SendAsync(new PutIndexesOperation(joinIndexes.ToArray()));
        h.RandomRound(new Random(seed), 150);
        foreach (IndexDefinition joinIndex in joinIndexes)
        {
            h.TrackJoin(joinIndex.Name);
            await h.AssertCurrentAsync(joinIndex.Name);
        }

        return h;
    }

    private static long[] ServedOf(JoinIndexHarness h, string[] names) => names.Select(x => h.Join(x).QueryCounters.ServedCount).ToArray();

    private static long FallbacksOf(JoinIndexHarness h, string[] names) => names.Sum(x => h.Join(x).QueryCounters.FallbackTotal);

    /// <summary>Runs the query served and as the join of the server; the answers are the same. Returns the join indexes that served it, as the difference of their counters.</summary>
    private static string[] Check(JoinIndexHarness h, string[] names, string sub, string tail = Tail)
    {
        var oracle = new JoinIndexQueryOracle(h, names[0]) { AlsoForced = names.Skip(1).ToArray() };
        long[] before = ServedOf(h, names);
        string rql = oracle.JoinOf(sub, tail);
        (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
        AssertSame(rql, served, scan);

        long[] after = ServedOf(h, names);
        return names.Where((_, i) => after[i] > before[i]).ToArray();
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ANarrowAWideAndTheWidestJoinOfTheSameKeysEachServeTheQueriesTheyDeclare()
    {
        string[] names = ["A_Narrow", "B_Wide", "C_Widest"];
        using JoinIndexHarness h = await HarnessAsync(
        [
            JoinOf(names[0], j => j.AddField("Rank", JoinFieldKind.Long)),
            JoinOf(names[1], j => j.AddField("Country").AddField("Rank", JoinFieldKind.Long)),
            JoinOf(names[2], j => j.AddField("Country").AddField("Name").AddField("Rank", JoinFieldKind.Long).AddField("Title", JoinFieldKind.Text).AddGrouping("IsActive", "true"))
        ], this, seed: 5);

        // the first join index in name order that declares what the query needs serves it, and no other
        Assert.Equal([names[0]], Check(h, names, ""));
        Assert.Equal([names[0]], Check(h, names, " where Rank = 3"));
        Assert.Equal([names[1]], Check(h, names, " where Country = 'nl'"));
        Assert.Equal([names[1]], Check(h, names, " where Country = 'nl' and Rank = 4"));
        Assert.Equal([names[2]], Check(h, names, " where Name = 'Alpha'"));
        Assert.Equal([names[2]], Check(h, names, " where Country = 'nl' and Name = 'Alpha'"));
        Assert.Equal([names[2]], Check(h, names, " where IsActive = true"));
        Assert.Equal([names[2]], Check(h, names, " where Country = 'de' and IsActive = true"));

        // a left join and an order by of the joined documents have the same candidates
        var oracle = new JoinIndexQueryOracle(h, names[0]) { AlsoForced = names.Skip(1).ToArray() };
        string left = oracle.JoinOf(" where Country = 'nl'", Tail).Replace(" join ", " left join ");
        (QueryAnswer a, QueryAnswer b) = oracle.Both(left);
        AssertSame(left, a, b);

        // a query that none declares is read by the server and is counted on exactly one join index, with a reason that says what is missing
        long fallbacks = FallbacksOf(h, names);
        string noneDeclares = oracle.JoinOf(" where Country = 'nl' and IsActive = false", Tail);
        (a, b) = oracle.Both(noneDeclares);
        AssertSame(noneDeclares, a, b);
        Assert.Equal(fallbacks + 1, FallbacksOf(h, names));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ASecondJoinServesWhileTheFirstOneIsBehindAndTheFirstServesAgainWhenItIsCurrent()
    {
        string[] names = ["A_First", "B_Second"];
        using JoinIndexHarness h = await HarnessAsync(
        [
            JoinOf(names[0], j => j.AddField("Country").AddField("Rank", JoinFieldKind.Long)),
            JoinOf(names[1], j => j.AddField("Country").AddField("Name").AddField("Rank", JoinFieldKind.Long))
        ], this, seed: 6);

        Assert.Equal([names[0]], Check(h, names, " where Country = 'nl'"));

        // the first join index stops: the indexes move on, it is behind them
        h.Store.Maintenance.Send(new StopIndexOperation(names[0]));
        h.RandomRound(new Random(61), 80);
        await WaitUntilCurrentAsync(h, names[0]);

        long fallbacks = FallbacksOf(h, names);
        Assert.Equal([names[1]], Check(h, names, " where Country = 'nl'"));
        Assert.Equal([names[1]], Check(h, names, " where Country = 'de' and Rank = 3"));
        Assert.Equal(fallbacks, FallbacksOf(h, names));

        // when no join index is current the server reads the join, and the counter that says why is the one of the join index that declares the fields and is behind
        h.Store.Maintenance.Send(new StopIndexOperation(names[1]));
        h.RandomRound(new Random(62), 40);
        await WaitUntilCurrentAsync(h, names);
        var oracle = new JoinIndexQueryOracle(h, names[0]) { AlsoForced = [names[1]] };
        string rql = oracle.JoinOf(" where Country = 'nl'", Tail);
        long servedBefore = ServedOf(h, names).Sum();
        (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
        AssertSame(rql, served, scan);
        Assert.Equal(servedBefore, ServedOf(h, names).Sum());
        Assert.Equal(1, BehindCount(h, names[0]) + BehindCount(h, names[1]));

        h.Store.Maintenance.Send(new StartIndexOperation(names[0]));
        h.Store.Maintenance.Send(new StartIndexOperation(names[1]));
        foreach (string name in names)
            await h.AssertCurrentAsync(name);

        Assert.Equal([names[0]], Check(h, names, " where Country = 'nl'"));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ABehindJoinThatDeclaresTheFieldsIsTheReasonWhenAnotherOneLacksThem()
    {
        string[] names = ["A_Has", "B_Lacks"];
        using JoinIndexHarness h = await HarnessAsync(
        [
            JoinOf(names[0], j => j.AddField("Country")),
            JoinOf(names[1], j => j.AddField("Rank", JoinFieldKind.Long))
        ], this, seed: 7);

        h.Store.Maintenance.Send(new StopIndexOperation(names[0]));
        h.RandomRound(new Random(71), 60);
        await WaitUntilCurrentAsync(h, names[0]);

        var oracle = new JoinIndexQueryOracle(h, names[1]) { AlsoForced = [names[0]] };
        string rql = oracle.JoinOf(" where Country = 'nl'", Tail);
        (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
        AssertSame(rql, served, scan);

        Assert.Equal(1, BehindCount(h, names[0]));
        Assert.Equal(0, h.Join(names[1]).QueryCounters.FallbackTotal);
    }

    private static long BehindCount(JoinIndexHarness h, string name) => h.Join(name).QueryCounters.Fallbacks.Where(x => x.Key == JoinFallbacks.BehindTimeout).Sum(x => x.Value);

    /// <summary>Waits until every index but the ones that were stopped is not stale.</summary>
    private static async Task WaitUntilCurrentAsync(JoinIndexHarness h, params string[] stopped)
    {
        for (int i = 0; i < 1000; i++)
        {
            if (h.Store.Maintenance.Send(new GetIndexesStatisticsOperation()).Where(x => stopped.Contains(x.Name) == false).All(x => x.IsStale == false))
                return;

            await Task.Delay(20);
        }

        Assert.Fail("the indexes stay stale");
    }
}
