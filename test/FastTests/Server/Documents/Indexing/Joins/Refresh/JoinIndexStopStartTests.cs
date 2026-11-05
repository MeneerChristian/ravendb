using System;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>A join index that was stopped does not hear the batches of the two indexes: when it is started it has to look at them again.</summary>
public class JoinIndexStopStartTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 1000 && condition() == false; i++)
            await Task.Delay(20);

        Assert.True(condition(), what);
    }

    private static async Task WaitUpstreamAsync(JoinIndexHarness h)
    {
        await WaitForAsync(() => h.Store.Maintenance.Send(new GetIndexesStatisticsOperation())
            .Where(x => x.Name != JoinIndexHarness.JoinName).All(x => x.IsStale == false), "the upstream indexes are current");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinThatIsStartedAfterBothIndexesChangedBecomesCurrentAndExact()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        h.PutContext(1, "nl", "Alpha", 10, true, "red fox");
        h.PutContext(2, "de", "Beta", 20, false, "blue whale");
        h.PutConversation(1, "contexts/1");
        h.PutConversation(2, "contexts/2");
        await h.AssertCurrentAsync();
        JoinIndex joinIndex = h.Join();

        await h.Store.Maintenance.SendAsync(new StopIndexOperation(JoinIndexHarness.JoinName));
        await WaitForAsync(() => h.Stats().Status == IndexRunningStatus.Paused, "the join index is paused");

        h.PutContext(2, "fr", "Beta", 99, true, "green frog");
        h.PutContext(3, "nl", "Gamma", 5, true, "red panda");
        h.PutConversation(3, "contexts/3");
        h.PutConversation(1, "contexts/2");
        h.Delete("conversations/2");
        await WaitUpstreamAsync(h);

        await h.Store.Maintenance.SendAsync(new StartIndexOperation(JoinIndexHarness.JoinName));
        await h.AssertCurrentAsync();
        Assert.True(ReferenceEquals(h.Join(), joinIndex), "the join index was reset");
        Assert.Equal(1, joinIndex.Counters.Builds);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinThatIsStartedAfterOneIndexChangedBecomesCurrentAndExact()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        h.PutContext(1, "nl", "Alpha", 10, true, "red fox");
        h.PutConversation(1, "contexts/1");
        await h.AssertCurrentAsync();

        await h.Store.Maintenance.SendAsync(new StopIndexOperation(JoinIndexHarness.JoinName));
        await WaitForAsync(() => h.Stats().Status == IndexRunningStatus.Paused, "the join index is paused");

        h.PutConversation(2, "contexts/1");
        await WaitUpstreamAsync(h);

        await h.Store.Maintenance.SendAsync(new StartIndexOperation(JoinIndexHarness.JoinName));
        await h.AssertCurrentAsync();
    }
}

/// <summary>The tests of the class with the join indexes on a field in the storage mode Indirect.</summary>
public class JoinIndexStopStartTestsIndirect(ITestOutputHelper output) : JoinIndexStopStartTests(output), IIndirectModeTest;
