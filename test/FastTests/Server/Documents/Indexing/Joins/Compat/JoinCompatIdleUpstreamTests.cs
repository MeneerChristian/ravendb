using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Corax;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>A join index over two auto indexes that go idle: the idle indexes stop consuming, the join stays exact, and a query that uses the outer index wakes them.</summary>
public class JoinCompatIdleUpstreamTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string AutoJoin = "IdlePairs";

    private static string FindAuto(IDocumentStore store, string prefix) => store.Maintenance.Send(new GetIndexNamesOperation(0, 100)).Single(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AJoinOverTwoIdleAutoIndexesStaysExactAndTheQueriesWakeTheUpstream()
    {
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, settings, putJoin: false, indexes: []);
        using (var session = h.Store.OpenSession())
        {
            _ = session.Advanced.RawQuery<JoinIndexHarness.Conversation>("from Conversations where ContextId = 'x' and Title = 'x'").ToList();
            _ = session.Advanced.RawQuery<JoinIndexHarness.Context>("from Contexts where Country = 'x' and IsActive = true and Name = 'x' and Rank = 1").ToList();
        }

        string outer = FindAuto(h.Store, "Auto/Conversations/");
        string inner = FindAuto(h.Store, "Auto/Contexts/");
        await h.PutJoinAsync(new IndexDefinition
        {
            Name = AutoJoin,
            Join = new JoinDefinition { OuterIndex = outer, OuterField = "ContextId", InnerIndex = inner }
                .AddField("Country").AddField("Name", JoinFieldKind.Exact).AddField("Rank", JoinFieldKind.Long).AddGrouping("IsActive", "true")
        });
        var oracle = new JoinIndexQueryOracle(h, AutoJoin, outer, inner);
        h.RandomRound(new Random(21), 150);
        await h.AssertCurrentAsync(AutoJoin);
        string rql = oracle.JoinOf(" where Country = 'nl'", " order by m.Title, m.ContextId limit 0, 1000");
        oracle.Check(rql);

        // both upstream indexes go idle, and documents change meanwhile: they are not indexed
        h.Database.IndexStore.GetIndex(outer).SetState(IndexState.Idle, inMemoryOnly: true);
        h.Database.IndexStore.GetIndex(inner).SetState(IndexState.Idle, inMemoryOnly: true);
        Assert.Equal(IndexState.Idle, h.Database.IndexStore.GetIndex(outer).State);
        h.RandomRound(new Random(22), 60);

        // the query of the join uses the outer index (it wakes it) and the inner one (the join reads it): the answer is the join of the server in any case
        (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
        AssertSame(rql, served, scan);
        Report($"T6a first query after idle: served {oracle.Info.Served}, fallbacks {string.Join(",", oracle.Info.Fallbacks)}; outer {h.Database.IndexStore.GetIndex(outer).State}, inner {h.Database.IndexStore.GetIndex(inner).State}");

        Assert.True(SpinWait.SpinUntil(() => h.Database.IndexStore.GetIndex(outer).State == IndexState.Normal, TimeSpan.FromSeconds(15)), "the outer index was not woken by the query");
        Indexes.WaitForIndexing(h.Store, timeout: TimeSpan.FromSeconds(60));
        await h.AssertCurrentAsync(AutoJoin);

        // waking an index changes its state while the join index looks at it: for a moment the join index cannot read the indexes and the join is read by the server, which is exact but not served
        WaitUntilJoinCanRead(h.Join(AutoJoin));
        oracle.Check(rql);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AJoinThatCannotReadAnUpstreamForAMomentFallsBackExactlyAndServesAgainOnceItLooksAgain()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        h.RandomRound(new Random(31), 60);
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        string rql = Join(" where Country = 'nl'", " order by m.Title, m.ContextId limit 0, 1000");
        oracle.Check(rql);

        JoinIndex joinIndex = h.Join();
        var inner = h.Database.IndexStore.GetIndex(JoinIndexHarness.InnerName);
        bool fellBack = false;
        for (int attempt = 0; attempt < 5 && fellBack == false; attempt++)
        {
            // an index in the middle of a change of its state cannot be read, and the join index is told about the change just then
            using (inner.DrainRunningQueries())
            {
                h.Database.Changes.RaiseNotifications(new IndexChange { Name = inner.Name, Type = IndexChangeTypes.IndexPromotedFromIdle });
                Assert.True(SpinWait.SpinUntil(() => joinIndex.Status == IndexRunningStatus.Pending, TimeSpan.FromSeconds(15)), "the join index did not turn pending while the inner index could not be read");
            }

            long servedBefore = oracle.Info.Served;
            (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
            AssertSame(rql, served, scan);
            fellBack = joinIndex.Status == IndexRunningStatus.Pending && oracle.Info.Served == servedBefore;
            WaitUntilJoinCanRead(joinIndex);
        }

        Assert.True(fellBack, "the query was served by a join index that is pending");
        Assert.True(oracle.FallbacksOf(JoinFallbacks.Unresolved) > 0, "the fallback of a pending join index was not counted");
        oracle.Check(rql);
    }

    // a join index that is pending looks at the indexes again from time to time, so this is a wait for a condition and not for a time
    private static void WaitUntilJoinCanRead(JoinIndex joinIndex)
    {
        Assert.True(SpinWait.SpinUntil(() => joinIndex.Status == IndexRunningStatus.Running, TimeSpan.FromSeconds(30)), $"the join index stayed {joinIndex.Status}: {joinIndex.GetStats().Join.PendingReason}");
    }

    private static void Report(string line) => JoinTestHelpers.AppendReport("COMPAT_REPORT", "join-compat-report.txt", line);
}
