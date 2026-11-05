using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes.Joins;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins;

public class JoinIndexUpstreamTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Join = "Conversations/WithContext";

    private class Conversation
    {
        public string ContextId { get; set; }
    }

    private class Context
    {
        public string Country { get; set; }
    }

    private static IndexDefinition Inner()
    {
        return new IndexDefinition { Name = "Contexts/Search", Maps = { "from c in docs.Contexts select new { c.Country }" } };
    }

    private async Task<DocumentStore> StoreAsync()
    {
        DocumentStore store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        await store.Maintenance.SendAsync(new PutIndexesOperation(
            new IndexDefinition { Name = "Conversations/ByContext", Maps = { "from c in docs.Conversations select new { c.ContextId }" } },
            Inner(),
            new IndexDefinition { Name = Join, Join = new JoinDefinition { OuterIndex = "Conversations/ByContext", OuterField = "ContextId", InnerIndex = "Contexts/Search" }.AddField("Country") }));
        return store;
    }

    private static IndexStats Stats(DocumentStore store) => store.Maintenance.Send(new GetIndexStatisticsOperation(Join));

    // a join index that is reset is not in the store until it is created again: its stats are not found in between
    private static IndexStats StatsOrNull(DocumentStore store)
    {
        try
        {
            return Stats(store);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<bool> WaitAsync(Func<bool> condition) => await WaitForValueAsync(() => Task.FromResult(condition()), true, timeout: 15_000);

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinThatReadsBothIndexesIsRunning()
    {
        using DocumentStore store = await StoreAsync();

        IndexStats stats = Stats(store);
        Assert.Null(stats.Join.PendingReason);
        Assert.Equal("Conversations/ByContext", stats.Join.OuterIndex);
        Assert.Equal("Contexts/Search", stats.Join.InnerIndex);
        Assert.Equal(IndexRunningStatus.Running, stats.Status);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnIndexOfAnotherTypeHasNoJoinSection()
    {
        using DocumentStore store = await StoreAsync();

        Assert.Null(store.Maintenance.Send(new GetIndexStatisticsOperation("Contexts/Search")).Join);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinIsPendingWhileAnIndexItJoinsIsMissingAndWakesWhenItIsBack()
    {
        using DocumentStore store = await StoreAsync();

        await store.Maintenance.SendAsync(new DeleteIndexOperation("Contexts/Search"));

        Assert.True(await WaitAsync(() => StatsOrNull(store)?.Join.PendingReason?.Contains("'Contexts/Search' does not exist") == true));
        Assert.Equal(IndexRunningStatus.Pending, Stats(store).Status);

        await store.Maintenance.SendAsync(new PutIndexesOperation(Inner()));

        Assert.True(await WaitAsync(() => StatsOrNull(store) is { Status: IndexRunningStatus.Running, Join.PendingReason: null }));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinThatIsToBeResetBecauseAnIndexItJoinsWasReplacedIsNotRunning()
    {
        using DocumentStore store = await StoreAsync();
        DocumentDatabase database = await GetDatabase(store.Database);
        Assert.True(await WaitAsync(() => StatsOrNull(store) is { IsStale: false, Join.Builds: > 0 }));

        var joinIndex = (JoinIndex)database.IndexStore.GetIndex(Join);
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        joinIndex.ForTestingPurposes = phase =>
        {
            if (phase != "BeforeReset")
                return;

            reached.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        try
        {
            await store.Maintenance.SendAsync(new DeleteIndexOperation("Contexts/Search"));
            Assert.True(await WaitAsync(() => Stats(store).Status == IndexRunningStatus.Pending));
            await store.Maintenance.SendAsync(new PutIndexesOperation(Inner()));

            Assert.True(reached.Wait(TimeSpan.FromSeconds(15)));
            IndexStats stats = Stats(store);
            Assert.Equal(IndexRunningStatus.Pending, stats.Status);
            Assert.Contains("is rebuilt", stats.Join.PendingReason);
        }
        finally
        {
            release.Set();
        }

        Assert.True(await WaitAsync(() => StatsOrNull(store) is { Status: IndexRunningStatus.Running, Join.PendingReason: null } && ReferenceEquals(database.IndexStore.GetIndex(Join), joinIndex) == false));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinIsPendingWhileAnIndexItJoinsIsDisabled()
    {
        using DocumentStore store = await StoreAsync();

        await store.Maintenance.SendAsync(new DisableIndexOperation("Conversations/ByContext"));
        Assert.True(await WaitAsync(() => Stats(store).Join.PendingReason?.Contains("is Disabled") == true));
        Assert.Equal(IndexRunningStatus.Pending, Stats(store).Status);

        await store.Maintenance.SendAsync(new EnableIndexOperation("Conversations/ByContext"));
        Assert.True(await WaitAsync(() => Stats(store) is { Status: IndexRunningStatus.Running, Join.PendingReason: null }));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ABatchOfAJoinedIndexThatChangedItWakesTheJoin()
    {
        using DocumentStore store = await StoreAsync();

        using (IAsyncDocumentSession session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Conversation { ContextId = "contexts/1" });
            await session.StoreAsync(new Context { Country = "nl" });
            await session.SaveChangesAsync();
        }

        Assert.True(await WaitAsync(() => Stats(store).Join.OuterBatches > 0 && Stats(store).Join.InnerBatches > 0));
    }
}
