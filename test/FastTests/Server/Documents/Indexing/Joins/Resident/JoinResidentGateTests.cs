using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Sparrow.LowMemory;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

/// <summary>
/// The memory gate of the resident caches: the process budget, the real low memory notification, and the estimate that the auto join indexes add to their gate. A collection that runs alone: the notification is one of the process and drops the lists of every cache,
/// so a test that runs at the same time and holds lists would find them gone.
/// </summary>
[Collection(ResidentNativeMemoryCollection.Name)]
public class JoinResidentGateTests(ITestOutputHelper output) : RavenTestBase(output), IIndirectModeTest
{
    private static string FieldJoin(string sub) => $"{Outer} join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code order by m.Title limit 0, 100";

    private static JoinDefinition Definition(JoinStorageMode mode, string innerField = "Code") => new() { OuterIndex = "O", OuterField = "ContextId", InnerField = innerField, InnerIndex = "I", StorageMode = mode };

    [RavenTheory(RavenTestCategory.Configuration | RavenTestCategory.Indexes)]
    [InlineData(true, JoinStorageMode.Indirect, "Code", 1_000_000, 100_000_000, 4_900_000)]
    // a join on the id of the inner documents is Indirect by the same setting
    [InlineData(true, JoinStorageMode.Indirect, null, 1_000_000, 100_000_000, 11_600_000)]
    [InlineData(true, JoinStorageMode.Indirect, "Code", 1_000_000, 1_000_000, 1_000_000)]
    [InlineData(true, JoinStorageMode.Expanded, "Code", 1_000_000, 100_000_000, 0)]
    [InlineData(false, JoinStorageMode.Indirect, "Code", 1_000_000, 100_000_000, 0)]
    public void TheEstimateOfTheCacheOfAJoinThatIsBuiltNowIsBytesPerOuterEntryWithinWhatIsLeft(bool indirectEnabled, JoinStorageMode mode, string innerField, long outerEntries, long free, long expected)
    {
        var options = new JoinOptions { IndirectEnabled = indirectEnabled, StorageModeDefault = mode };
        JoinDefinition definition = Definition(JoinStorageMode.Auto, innerField);
        Assert.Equal(expected, options.ResidentEstimateBytes(definition, outerEntries, free));

        // a cache that is switched off needs nothing
        options.ResidentCacheMaxBytes = 0;
        Assert.Equal(0, options.ResidentEstimateBytes(definition, outerEntries, free));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheCachesOfARegistryNeverHoldMoreThanTheBudgetAndALowMemoryClearsThem()
    {
        // a registry of its own that no notification of the machine reaches: the one of the process is charged by the caches of the join indexes of the tests that run at the same time, and a machine that is short of memory would mark this one in the middle of the test
        var registry = new ResidentCacheRegistry(ResidentCacheRegistry.DefaultBudgetBytes, registerWithLowMemoryNotification: false);
        long original = registry.BudgetBytes;
        try
        {
            using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this);
            for (int i = 0; i < 8; i++)
                h.PutCodedContext(i, "k" + i, i % 2 == 0 ? "nl" : "de", "n" + i % 3, i, true, "w");

            for (int i = 0; i < 80; i++)
                h.PutConversation(i, "k" + i % 9, "a");

            await h.AssertCurrentAsync();
            JoinIndex joinIndex = h.Join();
            JoinIndexHarness.UseResident(joinIndex, ResidentMode.Default, registry);
            for (int i = 0; i < 500 && joinIndex.Resident?.Cache.ResidentKeys is null or 0; i++)
                await Task.Delay(20);

            Assert.True(joinIndex.Resident.Cache.ResidentKeys > 0);
            long bytes = joinIndex.Resident.Cache.ResidentBytes;
            Assert.True(registry.ChargedBytes >= bytes, $"charged {registry.ChargedBytes} < {bytes}");

            // a budget below what the cache holds: nothing more is kept, and what is kept is never above it
            registry.SetBudget(bytes / 2);
            var oracle = new JoinIndexQueryOracle(h);
            JoinIndexHarness.UseResident(joinIndex, ResidentMode.Default, registry);
            for (int round = 0; round < 3; round++)
            {
                foreach (string sub in new[] { "", " where Country = 'nl'", " where Country != 'nl'" })
                {
                    oracle.Check(FieldJoin(sub));
                    Assert.True(registry.ChargedBytes <= registry.BudgetBytes, $"{registry.ChargedBytes} bytes charged against a budget of {registry.BudgetBytes}");
                }
            }

            // a low memory: the lists of every cache are dropped, the epoch stays, the answers stay exact
            registry.SetBudget(original);
            JoinIndexHarness.UseResident(joinIndex, ResidentMode.Default, registry);
            for (int i = 0; i < 500 && joinIndex.Resident?.Cache.ResidentKeys is null or 0; i++)
                await Task.Delay(20);

            Assert.True(joinIndex.Resident.Cache.ResidentKeys > 0);
            ResidentKeyListCache cache = joinIndex.Resident.Cache;
            registry.LowMemory(LowMemorySeverity.ExtremelyLow);
            Assert.Equal(0, cache.ResidentKeys);
            Assert.False(cache.Epoch.IsNone);
            Assert.True(cache.GetStats().LowMemoryDrops > 0, "drops");
            registry.LowMemoryOver();
            foreach (string sub in new[] { "", " where Country = 'nl'", " where Country != 'nl'" })
                oracle.Check(FieldJoin(sub));
        }
        finally
        {
            registry.SetBudget(original);
            registry.LowMemoryOver();
        }
    }

    /// <summary>The real notification: a simulated one toggles the low memory state of the process, so it only means a start of low memory when the machine is not short of memory at the time (a container whose page cache fills its limit is, for minutes).</summary>
    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ALowMemoryNotificationOfTheProcessClearsTheCachesOfTheRegistriesThatRegisteredForIt()
    {
        var registry = new ResidentCacheRegistry(ResidentCacheRegistry.DefaultBudgetBytes, registerWithLowMemoryNotification: true);
        var epoch = new ResidentEpoch(Guid.NewGuid(), 100, 1);
        using var cache = new ResidentKeyListCache(new ResidentCacheOptions(), registry);
        Assert.True(cache.TryEstablish(epoch, 1_000_000));
        Assert.Equal(ResidentInsertResult.Inserted, cache.TryInsert(epoch.EnvironmentId, epoch.TransactionId, 1_000, new long[] { 1, 2, 3 }));
        Assert.Equal(1, cache.ResidentKeys);

        await WaitForNormalMemoryAsync();
        Assert.SkipWhen(LowMemoryNotification.Instance.LowMemoryState, "the machine is short of memory: a simulated notification would end the low memory state and not start one");
        try
        {
            LowMemoryNotification.Instance.SimulateLowMemoryNotification();
            for (int i = 0; i < 500 && cache.ResidentKeys > 0; i++)
                await Task.Delay(20);

            Assert.Equal(0, cache.ResidentKeys);
            Assert.False(cache.Epoch.IsNone);
            Assert.True(registry.IsLowMemory);
        }
        finally
        {
            registry.LowMemoryOver();
            ResidentCacheRegistry.Instance.LowMemoryOver();
            await WaitForNormalMemoryAsync();
            GC.KeepAlive(registry);
        }
    }

    /// <summary>The state of the process is the one of the machine (a container that is short of memory has it for minutes): a test that expects lists in the cache of its join index has a registry of its own and does not depend on it.</summary>
    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheCacheOfAJoinOfATestHoldsItsListsWhileTheProcessIsShortOfMemory()
    {
        ResidentCacheRegistry.Instance.LowMemory(LowMemorySeverity.ExtremelyLow);
        try
        {
            using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this);
            for (int i = 0; i < 8; i++)
                h.PutCodedContext(i, "k" + i, i % 2 == 0 ? "nl" : "de", "n" + i % 3, i, true, "w");

            for (int i = 0; i < 80; i++)
                h.PutConversation(i, "k" + i % 9, "a");

            await h.AssertCurrentAsync();
            for (int i = 0; i < 500 && h.Join().Resident?.Cache.Epoch.IsNone != false; i++)
                await Task.Delay(20);

            new JoinIndexQueryOracle(h).Check(FieldJoin(""));
            Assert.True(await h.AssertResidentAsync(atLeast: 1) > 0);
        }
        finally
        {
            ResidentCacheRegistry.Instance.LowMemoryOver();
        }
    }

    private static async Task WaitForNormalMemoryAsync()
    {
        for (int i = 0; i < 500 && LowMemoryNotification.Instance.LowMemoryState; i++)
            await Task.Delay(20);
    }
}
