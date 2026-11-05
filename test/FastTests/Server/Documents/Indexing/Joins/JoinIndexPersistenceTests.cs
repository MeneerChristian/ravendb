using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Smuggler;
using Raven.Client.ServerWide.Operations;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins;

public class JoinIndexPersistenceTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Join = "Conversations/WithContext";

    private static IndexDefinition[] Definitions()
    {
        return
        [
            new IndexDefinition { Name = "Conversations/ByContext", Maps = { "from c in docs.Conversations select new { c.ContextId }" } },
            new IndexDefinition { Name = "Contexts/Search", Maps = { "from c in docs.Contexts select new { c.Country, c.Rank }" } },
            new IndexDefinition
            {
                Name = Join,
                Join = new JoinDefinition { OuterIndex = "Conversations/ByContext", OuterField = "ContextId", InnerIndex = "Contexts/Search" }
                    .AddField("Country").AddField("Rank", JoinFieldKind.Long)
            }
        ];
    }

    private static void AssertJoin(IDocumentStore store, string database)
    {
        var maintenance = store.Maintenance.ForDatabase(database);
        IndexDefinition definition = maintenance.Send(new GetIndexOperation(Join));
        Assert.Equal(IndexType.Join, definition.Type);
        Assert.Equal(new[] { "Country", "Rank" }, definition.Join.Fields.Select(x => x.Field));

        IndexStats stats = maintenance.Send(new GetIndexStatisticsOperation(Join));
        Assert.Equal(IndexType.Join, stats.Type);
        Assert.Equal(new[] { "Contexts", "Conversations" }, stats.Collections.Keys.Order());
    }

    [RavenFact(RavenTestCategory.BackupExportImport | RavenTestCategory.Indexes)]
    public async Task ABackupRestoresTheJoin()
    {
        await BackupAndRestoreAsync(BackupType.Backup);
    }

    [RavenFact(RavenTestCategory.BackupExportImport | RavenTestCategory.Indexes, LicenseRequired = true)]
    public async Task ASnapshotRestoresTheJoinWithItsStorage()
    {
        await BackupAndRestoreAsync(BackupType.Snapshot);
    }

    private async Task BackupAndRestoreAsync(BackupType type)
    {
        string backupPath = NewDataPath(suffix: "BackupFolder");
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.RunInMemory = false;
        using DocumentStore store = GetDocumentStore(options);
        await store.Maintenance.SendAsync(new PutIndexesOperation(Definitions()));

        var operation = await store.Maintenance.SendAsync(new BackupOperation(new BackupConfiguration
        {
            BackupType = type,
            LocalSettings = new LocalSettings { FolderPath = backupPath }
        }));
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(1));

        string restored = $"restored_{store.Database}";
        using (Backup.RestoreDatabase(store, new RestoreBackupConfiguration { BackupLocation = Directory.GetDirectories(backupPath).First(), DatabaseName = restored }))
        {
            AssertJoin(store, restored);
        }
    }

    [RavenFact(RavenTestCategory.BackupExportImport | RavenTestCategory.Indexes)]
    public async Task ExportAndImportCarryTheDefinitionOfTheJoin()
    {
        string file = Path.Combine(NewDataPath(), "joinIndex.ravendbdump");
        using DocumentStore source = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        using DocumentStore target = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        await source.Maintenance.SendAsync(new PutIndexesOperation(Definitions()));

        var export = await source.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions { OperateOnTypes = DatabaseItemType.Indexes }, file);
        await export.WaitForCompletionAsync(TimeSpan.FromMinutes(1));
        var import = await target.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions { OperateOnTypes = DatabaseItemType.Indexes }, file);
        await import.WaitForCompletionAsync(TimeSpan.FromMinutes(1));

        AssertJoin(target, target.Database);
    }

    [RavenMultiplatformFact(RavenTestCategory.Voron | RavenTestCategory.Encryption | RavenTestCategory.Indexes, RavenArchitecture.AllX64, LicenseRequired = true)]
    public async Task AJoinOfAnEncryptedDatabaseIsEncryptedAndSurvivesALoad()
    {
        string databaseName = Encryption.SetupEncryptedDatabase(out var certificates, out byte[] _);
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.AdminCertificate = certificates.ServerCertificate.Value;
        options.ClientCertificate = certificates.ServerCertificate.Value;
        options.ModifyDatabaseName = _ => databaseName;
        options.ModifyDatabaseRecord += record => record.Encrypted = true;
        options.RunInMemory = false;

        using DocumentStore store = GetDocumentStore(options);
        await store.Maintenance.SendAsync(new PutIndexesOperation(Definitions()));
        AssertJoin(store, store.Database);

        await store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(store.Database, disable: true));
        await store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(store.Database, disable: false));

        AssertJoin(store, store.Database);
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData("/admin/indexes/optimize")]
    [InlineData("/admin/indexes/dump")]
    public async Task AnOperationThatNeedsASearchIndexFailsWithAMessageForAJoin(string path)
    {
        using DocumentStore store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        await store.Maintenance.SendAsync(new PutIndexesOperation(Definitions()));
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{store.Urls[0]}/databases/{store.Database}{path}?name={Uri.EscapeDataString(Join)}&path={Uri.EscapeDataString(NewDataPath())}");

        using HttpResponseMessage response = await store.GetRequestExecutor().HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("NullReferenceException", body);
        Assert.DoesNotContain("InvalidCastException", body);
    }
}
