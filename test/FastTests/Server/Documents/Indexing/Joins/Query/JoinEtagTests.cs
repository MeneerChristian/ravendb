using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>The etag of a join answer has to change whenever the state that the answer was read from changes, without any join index.</summary>
public class JoinEtagTests(ITestOutputHelper output) : RavenTestBase(output)
{
    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnswerReadFromANewerJoinedIndexDoesNotShareTheEtagOfTheOlderOne()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, putJoin: false);
        var oracle = new JoinIndexQueryOracle(h);
        h.PutContext(1, country: "nl");
        h.PutConversation(1, "contexts/1");
        Indexes.WaitForIndexing(h.Store);

        string rql = Join(" where Country = 'nl'", " limit 0, 100");
        QueryAnswer before = oracle.Run(rql);
        Assert.Equal(1, before.Total);

        var outer = h.Database.IndexStore.GetIndex(JoinIndexHarness.OuterName);
        bool done = false;
        outer.ForTestingPurposesOnly().BeforeJoinSourcesOpened = () =>
        {
            if (done)
                return;

            done = true;
            h.PutContext(1, country: "c1");
            Indexes.WaitForIndexing(h.Store);
        };

        QueryAnswer during = oracle.Run(rql);
        outer.ForTestingPurposesOnly().BeforeJoinSourcesOpened = null;
        QueryAnswer after = oracle.Run(rql);

        Output.WriteLine($"before etag {before.ResultEtag} total {before.Total}; during etag {during.ResultEtag} total {during.Total}; after etag {after.ResultEtag} total {after.Total}");
        Assert.Equal(0, after.Total);
        if (before.ResultEtag == during.ResultEtag)
            Assert.Equal(before.Total, during.Total);
        if (during.ResultEtag == after.ResultEtag)
            Assert.Equal(after.Total, during.Total);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task EtagOfAJoinIsTheSameWhetherOrNotAJoinServesIt()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        var oracle = new JoinIndexQueryOracle(h);
        h.PutContext(1, country: "nl");
        h.PutContext(2, country: "de");
        h.PutConversation(1, "contexts/1");
        h.PutConversation(2, "contexts/2");
        await h.AssertCurrentAsync();

        string rql = Join(" where Country = 'nl'", " limit 0, 100");
        QueryAnswer served = oracle.Run(rql);
        Assert.Equal(1, served.Counters.GetValueOrDefault("Join/c/JoinIndexServed"));

        // the join index is switched off: the join is read as usual, the answer and so its etag are the same
        await h.Store.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Indexes.DisableIndexOperation(JoinIndexHarness.JoinName));
        QueryAnswer scanned = oracle.Run(rql);
        Assert.Equal(0, scanned.Counters.GetValueOrDefault("Join/c/JoinIndexServed"));
        Assert.Equal(served.Total, scanned.Total);
        Assert.Equal(served.ResultEtag, scanned.ResultEtag);

        // the joined documents change: the etag changes
        h.PutContext(2, country: "nl");
        Indexes.WaitForIndexing(h.Store);
        Assert.NotEqual(served.ResultEtag, oracle.Run(rql).ResultEtag);
    }
}
