using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes;
using Tests.Infrastructure;
using Index = Raven.Server.Documents.Indexes.Index;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins;

// Opt-in: RAVEN_JOIN_WAKES=<number of wakes, for example 5000>
// What a wake of an index costs when it finds nothing to do, for a join index and for a map index of an empty collection: the base loop opens a write transaction, an index context and a stats scope.
public class JoinIndexWakeBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Join = "Conversations/WithContext";

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AWakeThatFindsNothingToDo()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("RAVEN_JOIN_WAKES"), out int wakes) == false)
            return;

        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.RunInMemory = false;
        using DocumentStore store = GetDocumentStore(options);
        await store.Maintenance.SendAsync(new PutIndexesOperation(
            new IndexDefinition { Name = "Conversations/ByContext", Maps = { "from c in docs.Conversations select new { c.ContextId }" } },
            new IndexDefinition { Name = "Contexts/Search", Maps = { "from c in docs.Contexts select new { c.Country }" } },
            new IndexDefinition { Name = Join, Join = new JoinDefinition { OuterIndex = "Conversations/ByContext", OuterField = "ContextId", InnerIndex = "Contexts/Search" }.AddField("Country") }));
        DocumentDatabase database = await GetDatabase(store.Database);

        foreach (string name in new[] { "Contexts/Search", Join, "Contexts/Search", Join })
        {
            Index index = database.IndexStore.GetIndex(name);
            for (int i = 0; i < 200; i++)
                await WakeAsync(index);

            GC.Collect();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < wakes; i++)
                await WakeAsync(index);

            sw.Stop();
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            Output.WriteLine($"{name}: {wakes} wakes, {sw.Elapsed.TotalMilliseconds * 1000 / wakes:F1} us per wake, {allocated / wakes:N0} B allocated per wake (whole process)");
        }
    }

    private static async Task WakeAsync(Index index)
    {
        var awaiter = index.GetIndexingBatchAwaiter();
        index.ScheduleIndexingRun();
        await awaiter.WaitAsync();
    }
}
