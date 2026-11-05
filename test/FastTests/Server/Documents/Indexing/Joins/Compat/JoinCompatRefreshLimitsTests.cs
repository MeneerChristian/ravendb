using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Raven.Server.Config.Categories;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Sparrow;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>What a refresh of a join index does at the edge of its limits: the documents that an upstream index has not indexed yet (Join.MaxUnindexedIdsPerCommit) and the queue of list changes when little memory is available.</summary>
public class JoinCompatRefreshLimitsTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const int Limit = 20;

    private static async Task WaitForAsync(System.Func<bool> condition, string what)
    {
        for (int i = 0; i < 1000 && condition() == false; i++)
            await Task.Delay(20);

        Assert.True(condition(), what);
    }

    private static void Seed(JoinIndexHarness h)
    {
        h.PutContext(1, "nl", "Alpha", 10, true, "red fox");
        h.PutContext(2, "de", "Beta", 20, false, "blue whale");
        h.PutContext(3, "nl", "Gamma", 30, true, "red panda red");
        h.PutConversation(1, "contexts/1");
        h.PutConversation(2, "contexts/2");
    }

    private static void StoreConversations(JoinIndexHarness h, int count)
    {
        h.Save(s =>
        {
            for (int i = 0; i < count; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{1 + i % 3}", Title = "t" }, $"conversations/{1000 + i}");
        });
    }

    private static void StoreContexts(JoinIndexHarness h, int count)
    {
        h.Save(s =>
        {
            for (int i = 0; i < count; i++)
                s.Store(new JoinIndexHarness.Context { Country = "nl", Name = "n" + i, Rank = i, IsActive = true, Title = "t" }, $"contexts/{1000 + i}");
        });
    }

    private async Task PauseAsync(JoinIndexHarness h, string index)
    {
        await h.Store.Maintenance.SendAsync(new StopIndexOperation(index));
        await WaitForAsync(() => h.Stats(index).Status == IndexRunningStatus.Paused, index + " is paused");
    }

    /// <summary>
    /// An upstream index is paused and <paramref name="unindexed"/> documents are written behind it, then a change on the other side wakes the join index. At the limit the refresh reads them (they are not applied, the index has not indexed them);
    /// one above it the refresh gives up and the join index is built again, counted as TooManyUnindexedChanges. Either way the join index is exact once the upstream index has caught up.
    /// </summary>
    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(true, Limit - 1, false)]
    [InlineData(true, Limit, false)]
    [InlineData(true, Limit + 1, true)]
    [InlineData(false, Limit - 1, false)]
    [InlineData(false, Limit, false)]
    [InlineData(false, Limit + 1, true)]
    public async Task TheDocumentsThatAnUpstreamIndexHasNotIndexedAreReadUpToTheLimitAndOneAboveItBuildTheListsAgain(bool outerSide, int unindexed, bool rebuilds)
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.MaxUnindexedIdsPerCommit)] = Limit.ToString()
        });
        Seed(h);
        await h.AssertCurrentAsync();
        JoinIndex before = h.Join();
        long refreshes = before.Counters.Refreshes;
        Assert.Equal(1, before.Counters.Builds);

        await PauseAsync(h, outerSide ? JoinIndexHarness.OuterName : JoinIndexHarness.InnerName);
        if (outerSide)
        {
            StoreConversations(h, unindexed);
            h.PutContext(2, "fr", "Beta", 20, true, "blue whale");
        }
        else
        {
            StoreContexts(h, unindexed);
            h.PutConversation(3, "contexts/3");
        }

        if (rebuilds)
        {
            await WaitForAsync(() => ReferenceEquals(h.Join(), before) == false, "the join index was not built again");
            Assert.Equal(1, before.Counters.RebuildsByReason.GetValueOrDefault(nameof(JoinRebuildReason.TooManyUnindexedChanges)));
        }
        else
        {
            await WaitForAsync(() => before.Counters.Refreshes > refreshes, "the join index did not refresh");
            Assert.True(ReferenceEquals(h.Join(), before), "the join index was built again at or below the limit");
            Assert.Equal(0, before.Counters.RebuildsByReason.GetValueOrDefault(nameof(JoinRebuildReason.TooManyUnindexedChanges)));
            Assert.Equal(1, before.Counters.RebuildsByReason.Values.Sum());
        }

        await h.Store.Maintenance.SendAsync(new StartIndexOperation(outerSide ? JoinIndexHarness.OuterName : JoinIndexHarness.InnerName));
        await h.AssertCurrentAsync();
        Assert.Equal(1, h.Join().Counters.Builds);
        if (rebuilds == false)
            Assert.True(ReferenceEquals(h.Join(), before), "the join index was built again while the upstream index caught up");
    }

    /// <summary>The queue for an amount of available memory, from the rule and not from the code: a quarter of it in changes of 128 bytes, but not shorter than 262,144 changes while the memory is at least twice as much as that holds, and not shorter than half of the memory in changes under that (never fewer than 1,024).</summary>
    private static long ExpectedQueue(long availableBytes, long limit)
    {
        long quarter = availableBytes / 4 / JoinConfiguration.BytesPerQueuedListChange;
        long shortest = Math.Max(1024, Math.Min(262_144, availableBytes / 2 / JoinConfiguration.BytesPerQueuedListChange));
        return Math.Clamp(quarter, Math.Min(limit, shortest), limit);
    }

    /// <summary>The queue follows the available memory and not the floor of the machine (1/128 of its memory): with 100 MB available it is the 262,144 changes that the queue is never shorter than, on every machine; with 40 MB it is half of the memory (163,840 changes) and with almost nothing it is the hard minimum.</summary>
    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(1024L, 65_536)]
    [InlineData(2048L, 131_072)]
    [InlineData(4096L, 262_144)]
    [InlineData(16_384L, 1_048_576)]
    [InlineData(65_536L, 1_048_576)]
    public async Task TheQueueLimitFollowsTheAvailableMemoryNotTheFloorOfTheMachine(long totalMb, int floor)
    {
        // the default floor of a machine of that size
        var configuration = new JoinConfiguration(new Size(totalMb, SizeUnit.Megabytes));
        Assert.Equal(floor, configuration.MinQueuedListChanges);
        Assert.True((long)floor * JoinConfiguration.BytesPerQueuedListChange <= totalMb * 1024 * 1024 / 128, "the floor is at most 1/128 of the memory");

        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.MinQueuedListChanges)] = floor.ToString()
        });
        Seed(h);
        await h.AssertCurrentAsync();
        JoinIndex joinIndex = h.Join();

        foreach (long available in new[] { 100L << 20, 40L << 20, 1L << 20, 100L << 10 })
        {
            long expected = ExpectedQueue(available, Join_MaxQueued);
            joinIndex.Engine.AvailableMemoryBytes = () => available;
            h.PutConversation(50 + (int)(available >> 10), "contexts/1");
            await h.AssertCurrentAsync();
            output.WriteLine($"{totalMb} MB machine: floor {floor:N0}, queue limit with {available >> 10} KiB available {joinIndex.Counters.LastQueueLimit:N0} ({joinIndex.Counters.LastQueueLimit * JoinConfiguration.BytesPerQueuedListChange / (1024 * 1024)} MiB of changes)");
            Assert.Equal(expected, joinIndex.Counters.LastQueueLimit);
            Assert.Equal(expected, joinIndex.Engine.QueueLimit(Join_MaxQueued));
        }

        joinIndex.Engine.AvailableMemoryBytes = () => 100L << 20;
        Assert.Equal(262_144, joinIndex.Engine.QueueLimit(Join_MaxQueued));
        joinIndex.Engine.AvailableMemoryBytes = () => 40L << 20;
        Assert.Equal(163_840, joinIndex.Engine.QueueLimit(Join_MaxQueued));
        joinIndex.Engine.AvailableMemoryBytes = () => 100L << 10;
        Assert.Equal(1024, joinIndex.Engine.QueueLimit(Join_MaxQueued));
    }

    /// <summary>Only when the available memory is not known the floor of the machine is the queue.</summary>
    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(1024L, 65_536)]
    [InlineData(4096L, 262_144)]
    [InlineData(65_536L, 1_048_576)]
    public async Task TheFloorOfTheMachineIsTheQueueLimitWhenTheAvailableMemoryIsNotKnown(long totalMb, int floor)
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.MinQueuedListChanges)] = new JoinConfiguration(new Size(totalMb, SizeUnit.Megabytes)).MinQueuedListChanges.ToString()
        });
        JoinIndex joinIndex = h.Join();

        joinIndex.Engine.AvailableMemoryBytes = null;
        Assert.Equal(floor, joinIndex.Engine.QueueLimit(Join_MaxQueued));

        // a limit below the floor is the limit
        Assert.Equal(4096, joinIndex.Engine.QueueLimit(4096));
    }

    private const int Join_MaxQueued = 4_194_304;
}
