using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Corax;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>Indexes with a custom analyzer, a boosted outer field and a dotted field name on the two sides of a join index, and a join index that is throttled: the answer of the join index is the one of the join of the server.</summary>
public class JoinCompatShapeTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static void Report(string line) => JoinTestHelpers.AppendReport("COMPAT_REPORT", "join-compat-report.txt", line);

    private static Dictionary<string, string> ScoreSettings() => new() { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };

    private static IndexDefinition InnerWith(Action<IndexDefinition> change)
    {
        IndexDefinition definition = JoinIndexHarness.InnerIndex();
        change(definition);
        return definition;
    }

    private static void PutTitles(JoinIndexHarness h, params string[] titles)
    {
        for (int i = 0; i < titles.Length; i++)
        {
            h.PutContext(i, "nl", "n" + i, i, true, titles[i]);
            h.PutConversation(i, $"contexts/{i}", i % 2 == 0 ? "a" : "b");
            h.PutConversation(100 + i, $"contexts/{i}", "a");
        }
    }

    private static readonly string[] Titles = ["the red fox", "a red fox", "the the", "of the", "red", "fox the", "the quick brown fox", "THE Red Fox"];

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData("StopAnalyzer")]
    [InlineData("KeywordAnalyzer")]
    [InlineData("WhitespaceAnalyzer")]
    [InlineData("StandardAnalyzer")]
    public async Task ACustomAnalyzerOnADeclaredTextFieldGivesTheJoinOfTheServer(string analyzer)
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings(), putJoin: false,
            indexes: [JoinIndexHarness.OuterIndex(), InnerWith(x => x.Fields["Title"] = new IndexFieldOptions { Indexing = FieldIndexing.Search, Analyzer = analyzer })]);
        await h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition());
        PutTitles(h, Titles);
        Indexes.WaitForIndexing(h.Store, timeout: TimeSpan.FromSeconds(60));
        await h.AssertCurrentAsync();

        var oracle = new JoinIndexQueryOracle(h);
        string[] subs =
        [
            " where search(Title, 'the red')",
            " where search(Title, 'the')",
            " where search(Title, 'of the')",
            " where search(Title, 'red fox')",
            " where Title = 'the'",
            " where Title = 'red fox'",
            " where Title = 'the red fox'",
            " where Title = 'THE Red Fox'",
            " where Title = 'red' or Title = 'fox'",
            " where Title = 'zzz'"
        ];

        int notEmpty = 0;
        foreach (string sub in subs)
        {
            string rql = Join(sub, " order by m.Title, m.ContextId limit 0, 100");
            long servedBefore = oracle.Info.Served, fallbacksBefore = oracle.Info.FallbackTotal, splitsBefore = oracle.FallbacksOf(JoinFallbacks.AnalyzerSplitsValue);
            (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
            AssertSame(rql, served, scan);
            notEmpty += scan.Rows.Count > 0 ? 1 : 0;

            // the join index answered or counted why it did not
            Assert.True(oracle.Info.Served - servedBefore + oracle.Info.FallbackTotal - fallbacksBefore == 1, $"{analyzer}: {rql}: {string.Join(", ", oracle.Info.Fallbacks)}");

            // a text that the analyzer splits is a named fallback, not a failure of the join index
            if (analyzer != "KeywordAnalyzer" && sub is " where Title = 'red fox'" or " where Title = 'the red fox'")
                Assert.Equal(splitsBefore + 1, oracle.FallbacksOf(JoinFallbacks.AnalyzerSplitsValue));
            Report($"T15 {analyzer} | {sub} | rows {scan.Rows.Count} | served {oracle.Info.Served - servedBefore}, fallbacks {string.Join(",", oracle.Info.Fallbacks)}");
        }

        Assert.Equal(0, oracle.FallbacksOf(JoinFallbacks.Failed));
        Assert.True(notEmpty >= 3, "the data has to match some of the queries, or the test proves nothing");
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnIndexWithABoostedFieldCannotExistOnCoraxSoAJoinNeverSeesOne()
    {
        IndexDefinition boostedOuter = new() { Name = JoinIndexHarness.OuterName, Maps = { "from c in docs.Conversations select new { c.ContextId, Title = c.Title.Boost(3) }" } };
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings(), putJoin: false, indexes: [boostedOuter, JoinIndexHarness.InnerIndex()]);
        h.PutConversation(1, "contexts/1");
        Indexes.WaitForIndexing(h.Store, timeout: TimeSpan.FromSeconds(60), allowErrors: true);

        string[] errors = h.Store.Maintenance.Send(new GetIndexErrorsOperation([JoinIndexHarness.OuterName])).SelectMany(x => x.Errors).Select(x => x.Error).ToArray();
        Assert.Contains(errors, x => x.Contains("Boosting in index is not supported by Corax", StringComparison.Ordinal));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheBoostsOfTheQueryGiveTheSameScoresWhenAJoinServesTheJoin()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        h.RandomRound(new Random(11), 200);
        Indexes.WaitForIndexing(h.Store, timeout: TimeSpan.FromSeconds(60));
        await h.AssertCurrentAsync();

        var oracle = new JoinIndexQueryOracle(h);
        int scored = 0;
        string[] tails =
        [
            " order by score() limit 0, 50",
            " order by score() desc limit 0, 50",
            " where m.Title = 'a' order by score() limit 0, 50",
            " where boost(m.Title = 'b', 2) order by score() limit 0, 50",
            " where boost(m.Title = 'b', 2) or boost(m.Title = 'a', 5) order by score() limit 0, 50",
            " boost 2 order by score() limit 0, 50"
        ];

        foreach (string sub in new[] { "", " where Country = 'nl'", " where Country = 'c1' and IsActive = true" })
        {
            foreach (string tail in tails)
            {
                string rql = Join(sub, tail);
                (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
                AssertSame(rql, served, scan);
                scored += scan.Rows.Count(x => x.Score > 0) > 0 ? 1 : 0;
            }
        }

        Assert.True(scored >= 9, $"{scored} of the 18 queries have rows with a score: the data has to match, or the test proves nothing");
        Assert.True(oracle.Info.Served + oracle.Info.FallbackTotal == 18, oracle.Info.Served + " served, " + string.Join(", ", oracle.Info.Fallbacks));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AFieldWithADotInItsNameIsDeclaredAndServed()
    {
        IndexDefinition inner = new()
        {
            Name = JoinIndexHarness.InnerName,
            Maps = { "map('Contexts', function (c) { return { 'Address.City': c.Country, Name: c.Name, Rank: c.Rank, IsActive: c.IsActive, Title: c.Title }; })" }
        };
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings(), putJoin: false, indexes: [JoinIndexHarness.OuterIndex(), inner]);
        IndexDefinition joinIndex = new()
        {
            Name = JoinIndexHarness.JoinName,
            Join = new JoinDefinition { OuterIndex = JoinIndexHarness.OuterName, OuterField = "ContextId", InnerIndex = JoinIndexHarness.InnerName }.AddField("Address.City").AddField("Rank", JoinFieldKind.Long)
        };
        await h.PutJoinAsync(joinIndex);
        h.RandomRound(new Random(5), 150);
        Indexes.WaitForIndexing(h.Store, timeout: TimeSpan.FromSeconds(60));
        await h.AssertCurrentAsync();

        var oracle = new JoinIndexQueryOracle(h);
        foreach (string sub in new[] { " where 'Address.City' = 'nl'", " where 'Address.City' != 'nl'", " where 'Address.City' = 'c1' and Rank > 10", " where 'Address.City' = 'nobody'" })
        {
            string rql = Join(sub, " order by m.Title, m.ContextId limit 0, 100");
            long servedBefore = oracle.Info.Served, fallbacksBefore = oracle.Info.FallbackTotal;
            (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
            AssertSame(rql, served, scan);
            Report($"T15 dotted {sub} | rows {scan.Rows.Count} | served {oracle.Info.Served - servedBefore}, fallbacks {oracle.Info.FallbackTotal - fallbacksBefore} {string.Join(",", oracle.Info.Fallbacks)}");
            Assert.Equal(1, oracle.Info.Served - servedBefore);
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AThrottledJoinIsNotWaitedForLongerThanTheQueryWaits()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings(), putJoin: false);
        IndexDefinition definition = JoinIndexHarness.JoinIndexDefinition();
        definition.Configuration["Indexing.Throttling.TimeIntervalInMs"] = "3000";
        await h.PutJoinAsync(definition);
        h.PutContext(1, "nl", "Alpha", 10, true, "red fox");
        h.PutConversation(1, "contexts/1");
        Indexes.WaitForIndexing(h.Store, timeout: TimeSpan.FromSeconds(60));
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);

        // a change right after the last refresh is held back by the window of the throttling: the query does not wait for it for seconds
        h.PutConversation(2, "contexts/1");
        h.PutContext(2, "nl", "Beta", 20, true, "blue whale");
        h.PutConversation(3, "contexts/2");
        for (int i = 0; i < 400 && (h.Stats(JoinIndexHarness.OuterName).IsStale || h.Stats(JoinIndexHarness.InnerName).IsStale); i++)
            await Task.Delay(25);

        var clock = Stopwatch.StartNew();
        (QueryAnswer served, QueryAnswer scan) = oracle.Both(Join(" where Country = 'nl'", " order by m.Title, m.ContextId limit 0, 100"));
        TimeSpan elapsed = clock.Elapsed;
        AssertSame("throttled", served, scan);
        Assert.Equal(3, scan.Rows.Count);
        Assert.True(elapsed < TimeSpan.FromSeconds(2.5), $"the query waited for the window of the throttling: {elapsed}");

        // and the join index is current once its window is over
        await h.AssertCurrentAsync();
        oracle.Check(Join(" where Country = 'nl'", " order by m.Title, m.ContextId limit 0, 100"));
    }
}
