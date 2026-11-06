using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Auto;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Server;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes.Joins.Auto;
using Raven.Server.Rachis;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Commands;
using Raven.Server.ServerWide.Commands.Indexes;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Cluster.JoinClusterVersionTests;
using static FastTests.Server.Documents.Indexing.Joins.Auto.JoinIndexAutoQueryTests;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Cluster;

/// <summary>The command of an auto join index has the version of the command of a join index, for the same reason (see <see cref="JoinClusterVersionTests"/>): the versions are literals.</summary>
public class JoinAutoClusterVersionTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static AutoIndexDefinition AutoDefinition() => new()
    {
        Name = AutoName,
        Type = IndexType.AutoJoin,
        Collection = "Conversations",
        Join = new JoinDefinition { OuterIndex = JoinIndexHarness.OuterName, OuterField = "ContextId", InnerIndex = JoinIndexHarness.InnerName }
            .AddField("Country").AddField("Rank", JoinFieldKind.Long)
    };

    private static PutAutoJoinIndexCommand AutoJoinCommand() =>
        new(AutoDefinition(), "db", "request", IndexDeploymentMode.Parallel, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

    [RavenFact(RavenTestCategory.Cluster)]
    public void TheCommandIsAllowedFromTheVersionOfTheJoinsAndNotBelow()
    {
        Assert.Equal(JoinVersion, ClusterCommandsVersionManager.ClusterCommandsVersions[nameof(PutAutoJoinIndexCommand)]);

        Assert.False(ManagerAt(72_000).CanPutCommand(nameof(PutAutoJoinIndexCommand)));
        Assert.False(ManagerAt(LastVersionWithoutJoins).CanPutCommand(nameof(PutAutoJoinIndexCommand)));
        Assert.True(ManagerAt(JoinVersion).CanPutCommand(nameof(PutAutoJoinIndexCommand)));
        Assert.True(ManagerAt(ClusterCommandsVersionManager.MyCommandsVersion).CanPutCommand(nameof(PutAutoJoinIndexCommand)));

        // the command that an older build knows is not held back by the join indexes
        Assert.True(ManagerAt(LastVersionWithoutJoins).CanPutCommand(nameof(PutAutoIndexCommand)));
    }

    [RavenFact(RavenTestCategory.Cluster)]
    public void TheLeaderRefusesToProposeTheCommandToAClusterThatHasAnOlderNode()
    {
        var exception = Assert.Throws<RejectPutClusterCommandException>(() => new ClusterValidator(ManagerAt(LastVersionWithoutJoins)).AssertPutCommandToLeader(AutoJoinCommand()));

        Assert.Contains($"'{nameof(PutAutoJoinIndexCommand)}'", exception.Message);
        Assert.Contains($"cluster version is '{LastVersionWithoutJoins}'", exception.Message);
        Assert.Contains($"minimum version of {JoinVersion}", exception.Message);

        new ClusterValidator(ManagerAt(JoinVersion)).AssertPutCommandToLeader(AutoJoinCommand());
    }

    [RavenFact(RavenTestCategory.Cluster)]
    public void AnEntryWithTheCommandIsNotSentToAFollowerThatIsOlder()
    {
        var validator = new ClusterValidator(new ClusterCommandsVersionManager());
        using var context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject entry = context.ReadObject(new DynamicJsonValue
        {
            ["Flags"] = nameof(RachisEntryFlags.StateMachineCommand),
            ["Entry"] = new DynamicJsonValue { ["Type"] = nameof(PutAutoJoinIndexCommand) }
        }, "entry");

        var exception = Assert.Throws<RejectSendToFollowerException>(() => validator.AssertEntryBeforeSendToFollower(entry, LastVersionWithoutJoins, "B"));
        Assert.Contains(nameof(PutAutoJoinIndexCommand), exception.Message);
        Assert.Contains($"with the version {JoinVersion}", exception.Message);

        validator.AssertEntryBeforeSendToFollower(entry, JoinVersion, "B");
    }

    /// <summary>The auto join index is stored with the type 'AutoJoin', which a build without the value cannot parse (see <see cref="JoinClusterVersionTests"/>).</summary>
    [RavenFact(RavenTestCategory.Cluster)]
    public void TheRecordOfAnAutoJoinHasATypeThatAnOlderNodeDoesNotKnow()
    {
        Assert.Equal(9, (int)IndexType.AutoJoin);

        var record = new DatabaseRecord("db") { AutoIndexes = new Dictionary<string, AutoIndexDefinition>() };
        AutoJoinCommand().UpdateDatabaseRecord(record, 1);

        using JsonOperationContext context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject json = DocumentConventions.DefaultForServer.Serialization.DefaultConverter.ToBlittable(record, context);

        Assert.True(json.TryGet(nameof(DatabaseRecord.AutoIndexes), out BlittableJsonReaderObject autoIndexes));
        Assert.True(autoIndexes.TryGet(AutoName, out BlittableJsonReaderObject stored));
        Assert.True(stored.TryGet(nameof(AutoIndexDefinition.Type), out string type));
        Assert.Equal(nameof(IndexType.AutoJoin), type);
    }

    [RavenFact(RavenTestCategory.Cluster | RavenTestCategory.Indexes)]
    public async Task AnAutoJoinIsRefusedWithTheVersionsInTheMessageAndNothingIsCreatedWhileTheClusterHasAnOlderNode()
    {
        using RavenServer ownServer = GetNewServer();
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.Server = ownServer;
        using DocumentStore store = GetDocumentStore(options);
        await store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndexHarness.OuterIndex(), JoinIndexHarness.InnerIndex()));
        DocumentDatabase database = await GetDatabase(store.Database, ownServer);

        ClusterCommandsVersionManager versions = ownServer.ServerStore.Engine.CommandsVersionManager;
        int current = versions.CurrentClusterMinimalVersion;

        try
        {
            versions.SetClusterVersion(LastVersionWithoutJoins);

            var exception = await Assert.ThrowsAnyAsync<Exception>(async () => await database.IndexStore.Create.CreateAutoJoinAsync(AutoDefinition(), Guid.NewGuid().ToString()));
            string text = exception.ToString();
            Assert.Contains(nameof(PutAutoJoinIndexCommand), text);
            Assert.Contains($"cluster version is '{LastVersionWithoutJoins}'", text);
            Assert.Contains($"minimum version of {JoinVersion}", text);
        }
        finally
        {
            versions.SetClusterVersion(current);
        }

        Assert.Equal(0, database.IndexStore.AutoJoins.Counters.Created);
        Assert.Null(database.IndexStore.GetIndex(AutoName));
        DatabaseRecord record = store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database));
        Assert.True(record.AutoIndexes == null || record.AutoIndexes.Count == 0);

        await database.IndexStore.Create.CreateAutoJoinAsync(AutoDefinition(), Guid.NewGuid().ToString());
        Assert.NotNull(database.IndexStore.GetIndex(AutoName));
    }

    [RavenFact(RavenTestCategory.Cluster | RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AQueryDoesNotAskForAnAutoJoinWhileTheClusterHasAnOlderNodeAndTheAnswerIsTheSame()
    {
        using RavenServer ownServer = GetNewServer();
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.Server = ownServer;
        Action<DatabaseRecord> engine = options.ModifyDatabaseRecord;
        options.ModifyDatabaseRecord = record =>
        {
            engine?.Invoke(record);
            foreach ((string key, string value) in ScoreSettings())
                record.Settings[key] = value;
        };

        using DocumentStore store = GetDocumentStore(options);
        await store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndexHarness.OuterIndex(), JoinIndexHarness.InnerIndex()));
        DocumentDatabase database = await GetDatabase(store.Database, ownServer);
        database.IndexStore.AutoJoins.AvailableMemoryForTesting = () => new Size(64, SizeUnit.Gigabytes);
        var harness = JoinIndexHarness.ForExistingDatabase(this, store, database);
        harness.RandomRound(new Random(3), 100);
        Indexes.WaitForIndexing(store);

        var oracle = new JoinIndexQueryOracle(harness, AutoName);
        string rql = oracle.JoinOf(FirstSub, FirstTail);
        ClusterCommandsVersionManager versions = ownServer.ServerStore.Engine.CommandsVersionManager;
        int current = versions.CurrentClusterMinimalVersion;
        QueryAnswer skipped;

        try
        {
            versions.SetClusterVersion(LastVersionWithoutJoins);
            skipped = oracle.Run(rql);
            QueryAnswer again = oracle.Run(rql);

            Assert.True(skipped.Rows.Count > 0, "the data has to match the query, or the test proves nothing");
            AssertSame(rql, again, skipped);
            Assert.Equal(1, skipped.Counters.GetValueOrDefault(AutoJoinCounters.SkippedClusterVersion));
            Assert.Equal(0, skipped.Counters.GetValueOrDefault("Join/c/JoinIndexServed"));
            Assert.Equal(2, database.IndexStore.AutoJoins.Counters.SkippedForClusterVersion);
            Assert.Equal(0, database.IndexStore.AutoJoins.Counters.Requested);
            await Task.Delay(300);
            Assert.Empty(AutoJoinNames(harness));
        }
        finally
        {
            versions.SetClusterVersion(current);
        }

        // when the cluster is complete the same query asks for the join index, and the answer does not change
        _ = oracle.Run(rql);
        Assert.Equal(1, database.IndexStore.AutoJoins.Counters.Requested);
        await WaitForTheAutoJoinAsync(harness);
        AssertSame(rql, oracle.Run(rql), skipped);
    }
}
