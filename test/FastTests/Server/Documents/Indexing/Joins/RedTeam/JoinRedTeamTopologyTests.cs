using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.RedTeam;

/// <summary>Category B of the red team review: which indexes are joined with which (the same collection on both sides, one index in two join indexes, many join indexes on one outer index, two join indexes that point at each other).</summary>
public class JoinRedTeamTopologyTests(ITestOutputHelper output) : JoinRedTeamBase(output)
{
    private static IndexDefinition JoinOf(string name, string outer, string outerField, string inner, Action<JoinDefinition> declare)
    {
        var joinIndex = new JoinDefinition { OuterIndex = outer, OuterField = outerField, InnerIndex = inner };
        declare(joinIndex);
        return new IndexDefinition { Name = name, Join = joinIndex };
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT09_AnIndexJoinedWithAnotherIndexOfTheSameCollectionIsExactWhileOneSideLags()
    {
        // contexts that name other contexts: the outer index and the inner index read the same collection
        var selfOuter = new IndexDefinition { Name = "Self/Outer", Maps = { "from c in docs.Contexts select new { ContextId = c.Name, c.Title }" } };
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings(), putJoin: false, indexes: [selfOuter, JoinIndexHarness.InnerIndex()]);
        await h.PutJoinAsync(JoinOf("SelfPairs", "Self/Outer", "ContextId", "Contexts/Search", j => j.AddField("Country").AddField("Rank", JoinFieldKind.Long).AddGrouping("IsActive", "true")));

        var rnd = new Random(7);
        for (int round = 0; round < 6; round++)
        {
            for (int i = 0; i < 40; i++)
                h.PutContext(rnd.Next(25), rnd.Next(3) == 0 ? "nl" : "de", rnd.Next(5) == 0 ? "nobody" : $"contexts/{rnd.Next(25)}", rnd.Next(50), rnd.Next(3) != 0);

            for (int i = 0; i < 5; i++)
                h.Delete($"contexts/{rnd.Next(25)}");

            await h.AssertCurrentAsync("SelfPairs");
        }

        var oracle = new JoinIndexQueryOracle(h, "SelfPairs", "Self/Outer", JoinIndexHarness.InnerName);
        bool served = false;
        foreach (string sub in Subs)
        {
            foreach (string tail in new[] { " order by score() limit 0, 1000", " order by m.Title limit 0, 1000" })
            {
                served |= Same(oracle, oracle.JoinOf(sub, tail));
                Same(oracle, oracle.JoinOf(sub, tail).Replace(" join ", " left join "));
            }
        }

        Assert.True(served, "the join index served nothing: " + string.Join(", ", oracle.Info.Fallbacks));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT10_OneInnerIndexInTwoJoinsOfTwoOuterIndexesFollowsBothAndServesBoth()
    {
        var secondOuter = new IndexDefinition { Name = "Conversations/ByContext2", Maps = { "from c in docs.Conversations select new { c.ContextId, c.Title }" } };
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings(), indexes: [JoinIndexHarness.OuterIndex(), JoinIndexHarness.InnerIndex(), secondOuter]);
        IndexDefinition second = JoinIndexHarness.JoinIndexDefinition("PairsTwo");
        second.Join.OuterIndex = "Conversations/ByContext2";
        await h.PutJoinAsync(second);

        var rnd = new Random(11);
        for (int round = 0; round < 5; round++)
        {
            h.RandomRound(rnd, 80);
            h.TrackJoin(JoinIndexHarness.JoinName);
            await h.AssertCurrentAsync();
            h.TrackJoin("PairsTwo");
            await h.AssertCurrentAsync("PairsTwo");
        }

        var one = new JoinIndexQueryOracle(h);
        var two = new JoinIndexQueryOracle(h, "PairsTwo", "Conversations/ByContext2", JoinIndexHarness.InnerName);
        // a range with a score is read by the server by design
        foreach (string sub in Subs.Where(x => x.Contains("Rank") == false))
        {
            Assert.True(Same(one, Join(sub, " order by score() limit 0, 1000")), "first join index: " + string.Join(", ", one.Info.Fallbacks));
            Assert.True(Same(two, two.JoinOf(sub, " order by score() limit 0, 1000")), "second join index: " + string.Join(", ", two.Info.Fallbacks));
        }

        Same(one, Join(" where Rank > 3", " order by score() limit 0, 1000"));
        Same(two, two.JoinOf(" where Rank > 3", " order by score() limit 0, 1000"));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT11_ANarrowJoinOfTheSamePairDoesNotShadowTheWideOne()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        await h.PutJoinAsync(JoinOf("Narrow", JoinIndexHarness.OuterName, "ContextId", JoinIndexHarness.InnerName, j => j.AddField("Rank", JoinFieldKind.Long)));
        h.RandomRound(new Random(5), 150);
        h.TrackJoin("Narrow");
        await h.AssertCurrentAsync("Narrow");
        h.TrackJoin(JoinIndexHarness.JoinName);
        await h.AssertCurrentAsync();

        var wide = new JoinIndexQueryOracle(h) { AlsoForced = ["Narrow"] };
        var narrow = new JoinIndexQueryOracle(h, "Narrow");
        foreach (string sub in Subs.Where(x => x.Contains("Rank") == false))
        {
            long before = wide.Info.Served + narrow.Info.Served;
            Same(wide, Join(sub, " order by score() limit 0, 1000"));
            Assert.True(wide.Info.Served + narrow.Info.Served > before, $"no join index served '{sub}': wide {string.Join(", ", wide.Info.Fallbacks)}; narrow {string.Join(", ", narrow.Info.Fallbacks)}");
        }

        // a query on a field that only the wide join index has: it is the wide one that has to serve it
        long wideServed = wide.Info.Served;
        Same(wide, Join(" where Country = 'nl'", " order by m.Title limit 0, 1000"));
        Assert.True(wide.Info.Served > wideServed, "the wide join index does not serve a field that only it declares: " + string.Join(", ", wide.Info.Fallbacks));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT12_ManyJoinsOnOneOuterIndexAreAllCurrentAndTheQueriesStayExact()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings(), putJoin: false);
        const int count = 24;
        var definitions = new List<IndexDefinition>();
        for (int i = 0; i < count; i++)
        {
            int k = i;
            definitions.Add(JoinOf($"J{i:00}", JoinIndexHarness.OuterName, "ContextId", JoinIndexHarness.InnerName, j =>
            {
                j.AddField("Country");
                if ((k & 1) != 0) j.AddField("Name");
                if ((k & 2) != 0) j.AddField("Rank", JoinFieldKind.Long);
                if ((k & 4) != 0) j.AddField("Title", JoinFieldKind.Text);
                j.AddGrouping("IsActive", (k & 8) != 0 ? "false" : "true");
            }));
        }

        await h.Store.Maintenance.SendAsync(new PutIndexesOperation(definitions.ToArray()));
        var rnd = new Random(3);
        for (int round = 0; round < 3; round++)
            h.RandomRound(rnd, 100);

        for (int i = 0; i < count; i++)
        {
            string name = $"J{i:00}";
            h.TrackJoin(name);
            await h.AssertCurrentAsync(name);
            Assert.Equal(IndexState.Normal, h.Stats(name).State);
        }

        var oracle = new JoinIndexQueryOracle(h, "J00");
        foreach (string sub in Subs)
            Same(oracle, Join(sub, " order by score() limit 0, 1000"));

        long served = Enumerable.Range(0, count).Sum(i => h.Stats($"J{i:00}").Join.Served);
        Assert.True(served > 0, "no join index served");
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT13_TwoJoinsThatPointAtEachOtherAreBothCurrentAndExact()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        // contexts name conversations in Country (a default-indexed field), conversations name contexts in ContextId
        await h.PutJoinAsync(JoinOf("Back", JoinIndexHarness.InnerName, "Country", JoinIndexHarness.OuterName, j => j.AddField("Title")));
        var rnd = new Random(9);
        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i < 40; i++)
            {
                h.PutConversation(rnd.Next(20), rnd.Next(6) == 0 ? null : $"contexts/{rnd.Next(20)}", rnd.Next(2) == 0 ? "a" : "b");
                h.PutContext(rnd.Next(20), rnd.Next(6) == 0 ? "nobody" : $"conversations/{rnd.Next(20)}", "n", rnd.Next(50), rnd.Next(3) != 0);
            }

            h.TrackJoin(JoinIndexHarness.JoinName);
            await h.AssertCurrentAsync();
            h.TrackJoin("Back");
            await h.AssertCurrentAsync("Back");
        }

        var forward = new JoinIndexQueryOracle(h);
        var back = new JoinIndexQueryOracle(h, "Back", JoinIndexHarness.InnerName, JoinIndexHarness.OuterName);
        Assert.True(Same(forward, Join("", " order by score() limit 0, 1000")));
        string backQuery = "from index 'Contexts/Search' as m join (from index 'Conversations/ByContext' where Title = 'a') as c on m.Country = id(c) order by score() limit 0, 1000";
        Same(back, backQuery);
        Same(back, backQuery.Replace(" join ", " left join "));
    }
}
