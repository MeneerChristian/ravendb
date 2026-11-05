using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Server.Config;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>Documents that an upstream index has not indexed yet are not changes of its snapshot: a bulk load that is ahead of the index must not make the join index build its lists again.</summary>
public class JoinIndexBulkLoadTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static async Task WaitForAsync(System.Func<bool> condition, string what)
    {
        for (int i = 0; i < 1000 && condition() == false; i++)
            await Task.Delay(20);

        Assert.True(condition(), what);
    }

    private static void StoreConversations(JoinIndexHarness h, int from, int count)
    {
        h.Save(s =>
        {
            for (int i = 0; i < count; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{1 + i % 3}", Title = "t" }, $"conversations/{from + i}");
        });
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ADocumentThatTheOuterIndexHasNotIndexedYetIsNotCountedAgainstTheBudget()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = "1000",
            ["Indexing.MapBatchSize"] = "500"
        });
        Seed(h);
        await h.AssertCurrentAsync();
        JoinIndex before = h.Join();
        long refreshes = before.Counters.Refreshes;

        await h.Store.Maintenance.SendAsync(new StopIndexOperation(JoinIndexHarness.OuterName));
        await WaitForAsync(() => h.Stats(JoinIndexHarness.OuterName).Status == Raven.Client.Documents.Indexes.IndexRunningStatus.Paused, "the outer index is paused");

        StoreConversations(h, 1000, 1500);

        // a change on the inner side wakes the join index while 1,500 documents are written and not indexed
        h.PutContext(2, "fr", "Beta", 20, true, "blue whale");
        await WaitForAsync(() => before.Counters.Refreshes > refreshes || ReferenceEquals(h.Join(), before) == false, "the join index refreshed or was reset");
        Assert.True(ReferenceEquals(h.Join(), before), "the join index was reset because of documents that were not indexed yet");

        await h.Store.Maintenance.SendAsync(new StartIndexOperation(JoinIndexHarness.OuterName));
        await h.AssertCurrentAsync();
        Assert.True(ReferenceEquals(h.Join(), before), "the join index was reset while the outer index caught up");
        Assert.DoesNotContain(before.Counters.RebuildsByReason.Keys, x => x == nameof(JoinRebuildReason.TooManyUnindexedChanges));
        Assert.Equal(1, before.Counters.Builds);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ManyDocumentsThatTheOuterIndexHasNotIndexedYetStillEndTheRefresh()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.MaxUnindexedIdsPerCommit)] = "100"
        });
        Seed(h);
        await h.AssertCurrentAsync();
        JoinIndex before = h.Join();

        await h.Store.Maintenance.SendAsync(new StopIndexOperation(JoinIndexHarness.OuterName));
        await WaitForAsync(() => h.Stats(JoinIndexHarness.OuterName).Status == Raven.Client.Documents.Indexes.IndexRunningStatus.Paused, "the outer index is paused");
        StoreConversations(h, 1000, 300);
        h.PutContext(2, "fr", "Beta", 20, true, "blue whale");

        await WaitForAsync(() => ReferenceEquals(h.Join(), before) == false, "the join index was reset");
        await h.Store.Maintenance.SendAsync(new StartIndexOperation(JoinIndexHarness.OuterName));
        await h.AssertCurrentAsync();
    }

    private static void Seed(JoinIndexHarness h)
    {
        h.PutContext(1, "nl", "Alpha", 10, true, "red fox");
        h.PutContext(2, "de", "Beta", 20, false, "blue whale");
        h.PutContext(3, "nl", "Gamma", 30, true, "red panda red");
        h.PutConversation(1, "contexts/1");
        h.PutConversation(2, "contexts/2");
    }
}
