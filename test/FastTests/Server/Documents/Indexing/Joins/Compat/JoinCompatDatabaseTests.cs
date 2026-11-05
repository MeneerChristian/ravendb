using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Smuggler;
using Raven.Client.ServerWide.Operations;
using Raven.Client.ServerWide;
using Raven.Server.Documents;
using Sparrow;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Compat.CompatDb;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>A database that has a join index: its backup, export and compaction, and what a client that does not know join indexes reads from it.</summary>
public class JoinCompatDatabaseTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Games = "from index 'Games' as g";
    private const string Join = $"{Games} {VipJoin} order by id() limit 0, 1000";
    private static readonly string[] Expected = ["games/000", "games/001", "games/004", "games/005", "games/008", "games/009"];

    private static string[] AutoJoins(IDocumentStore store) => store.Maintenance.Send(new GetIndexNamesOperation(0, 100)).Where(x => x.Contains("/Join/", StringComparison.Ordinal)).ToArray();

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 800 && condition() == false; i++)
            await Task.Delay(25);

        Assert.True(condition(), what);
    }

    private async Task AssertAutoJoinIsMadeAgainAsync(IDocumentStore store)
    {
        DocumentDatabase database = await GetDatabase(store.Database);
        database.IndexStore.AutoJoins.AvailableMemoryForTesting = () => new Size(64, SizeUnit.Gigabytes);

        // none was restored or imported, and the join is exact all the same
        Assert.Empty(AutoJoins(store));
        Assert.Equal(Expected, Run(store, Join));

        // the query read as usual asked for the auto join index of the new database; the next one is served by it
        await WaitForAsync(() => AutoJoins(store).Length == 1, "the auto join index was not made again");
        Assert.Equal(1, database.IndexStore.AutoJoins.Counters.Requested);
        Assert.Equal(Expected, Run(store, Join));
        Assert.Equal(Expected, Run(store, Join));
        Assert.Equal(IndexType.AutoJoin, store.Maintenance.Send(new GetIndexStatisticsOperation(AutoJoins(store)[0])).Type);
    }

    // the answer of a query that was cached by the client is not read from the server again
    private static List<string> Run(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession(new SessionOptions { NoCaching = true });
        return session.Advanced.RawQuery<CompatGame>(rql).ToList().Select(x => session.Advanced.GetDocumentId(x)).ToList();
    }

    private async Task<CompatDb> DatabaseWithAnAutoJoinAsync()
    {
        CompatDb db = await CompatDb.CreateAsync(this, CompatMode.Auto, runInMemory: false);
        await db.WaitForTheJoinAsync();
        Assert.Equal([AutoJoin], AutoJoins(db.Store));
        Assert.Equal(Expected, Run(db.Store, Join));
        return db;
    }

    private static RavenTestBase.Options TargetOptions() => CompatDb.Options(CompatMode.Auto);

    [RavenFact(RavenTestCategory.BackupExportImport | RavenTestCategory.Indexes)]
    public async Task AnAutoJoinIsNotRestoredFromABackupAndIsMadeAgainByTheQueries()
    {
        using CompatDb db = await DatabaseWithAnAutoJoinAsync();
        string backupPath = NewDataPath(suffix: "BackupFolder");
        var operation = await db.Store.Maintenance.SendAsync(new BackupOperation(new BackupConfiguration
        {
            BackupType = BackupType.Backup,
            LocalSettings = new LocalSettings { FolderPath = backupPath }
        }));
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(1));

        string restored = $"restored_{db.Store.Database}";
        using (Backup.RestoreDatabase(db.Store, new RestoreBackupConfiguration { BackupLocation = Directory.GetDirectories(backupPath).First(), DatabaseName = restored }))
        {
            using var target = new DocumentStore { Urls = db.Store.Urls, Database = restored }.Initialize();
            Indexes.WaitForIndexing(target);
            await AssertAutoJoinIsMadeAgainAsync(target);
        }
    }

    [RavenFact(RavenTestCategory.BackupExportImport | RavenTestCategory.Indexes)]
    public async Task AnAutoJoinIsNotExportedAndIsMadeAgainByTheQueriesOfTheTarget()
    {
        using CompatDb db = await DatabaseWithAnAutoJoinAsync();
        string file = Path.Combine(NewDataPath(), "auto-join.ravendbdump");
        using DocumentStore target = GetDocumentStore(TargetOptions());

        var export = await db.Store.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions(), file);
        await export.WaitForCompletionAsync(TimeSpan.FromMinutes(1));
        var import = await target.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions(), file);
        await import.WaitForCompletionAsync(TimeSpan.FromMinutes(1));
        Indexes.WaitForIndexing(target);

        await AssertAutoJoinIsMadeAgainAsync(target);
    }

    [RavenTheory(RavenTestCategory.Indexes | RavenTestCategory.Querying)]
    [InlineData(CompatMode.Expanded)]
    [InlineData(CompatMode.Indirect)]
    public async Task ACompactedDatabaseGivesTheSameJoinAndTheJoinBecomesCurrent(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode, runInMemory: false);
        await db.WaitForTheJoinAsync();
        Assert.Equal(Expected, Run(db.Store, Join));

        var compact = await db.Store.Maintenance.Server.SendAsync(new CompactDatabaseOperation(new CompactSettings { DatabaseName = db.Store.Database, Documents = true }));
        await compact.WaitForCompletionAsync(TimeSpan.FromMinutes(2));

        // after the compaction the documents are the ones of the Voron environment that was copied: the join index compares generations and either follows or builds again
        await WaitForAsync(() => db.Stats().Join.PendingReason == null && db.Stats().IsStale == false, "the join index is not current after the compaction");
        JoinIndexStats before = db.Join;
        IndexStats stats = db.Stats();
        Assert.Equal(Expected, Run(db.Store, Join));
        CompatDb.Outcome outcome = CompatDb.Since(before, db.Join);
        Assert.True(outcome.Served + outcome.Fallbacks == 1, $"the join index was not asked: {outcome.Describe()} (state {stats.State}, status {stats.Status}, pending {stats.Join.PendingReason}, builds {stats.Join.Builds})");

        // every fallback has a name, and once it is current the join is served again
        await WaitForAsync(() => CompatDb.Since(before, RunAndRead(db)).Served >= 1, $"the join is not served again after the compaction: {CompatDb.Since(before, db.Join).Describe()}");
    }

    private static JoinIndexStats RunAndRead(CompatDb db)
    {
        Run(db.Store, Join);
        return db.Join;
    }

    [RavenFact(RavenTestCategory.Indexes | RavenTestCategory.ClientApi)]
    public async Task TheListOfIndexesWithAJoinIsAdditiveAndAReaderThatDoesNotKnowTheTypeFailsOnTheWholeList()
    {
        using CompatDb db = await CompatDb.CreateAsync(this, CompatMode.Expanded);
        using var http = new HttpClient();
        string body = await http.GetStringAsync($"{db.Store.Urls[0]}/databases/{db.Store.Database}/indexes");
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement[] results = document.RootElement.GetProperty("Results").EnumerateArray().ToArray();

        // the type is written as its name, the join index is one more entry with the properties of every index and an empty list of maps
        JsonElement joinIndex = results.Single(x => x.GetProperty("Name").GetString() == ManualJoin);
        Assert.Equal("Join", joinIndex.GetProperty("Type").GetString());
        Assert.Equal(0, joinIndex.GetProperty("Maps").GetArrayLength());
        Assert.Equal(Users, joinIndex.GetProperty("Join").GetProperty("InnerIndex").GetString());
        Assert.All(results.Where(x => x.GetProperty("Name").GetString() != ManualJoin), x => Assert.Equal("Map", x.GetProperty("Type").GetString()));

        // what a reader that was written before the type existed does: it knows the names of the types it knows
        var known = Enum.GetNames<IndexType>().Where(x => x is not ("Join" or "AutoJoin")).ToHashSet();
        string[] unknown = results.Select(x => x.GetProperty("Type").GetString()).Where(x => known.Contains(x) == false).Distinct().ToArray();
        output.WriteLine("types an older client does not know: " + string.Join(", ", unknown));
        Assert.Equal(new[] { "Join" }, unknown);

        // the same JSON read by the enum of this client works: the reader fails by the name it does not know, not by a shape it does not
        foreach (JsonElement entry in results)
            Assert.True(Enum.TryParse<IndexType>(entry.GetProperty("Type").GetString(), out _));
    }
}
