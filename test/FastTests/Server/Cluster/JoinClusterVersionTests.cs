using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Server;
using Raven.Server.Rachis;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Commands;
using Raven.Server.ServerWide.Commands.Indexes;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Cluster;

/// <summary>
/// The command of a join index needs a cluster of version 72_002: a node of an older build does not know it, and cannot read a database record that holds a join index
/// (see <see cref="TheRecordOfAJoinHasATypeThatAnOlderNodeDoesNotKnow"/>). The versions here are literals, so that a shift of the constant in <see cref="ClusterCommandsVersionManager"/> fails the tests.
/// </summary>
public class JoinClusterVersionTests(ITestOutputHelper output) : RavenTestBase(output)
{
    internal const int JoinVersion = 72_002;
    internal const int LastVersionWithoutJoins = 72_001;

    internal static ClusterCommandsVersionManager ManagerAt(int version)
    {
        var manager = new ClusterCommandsVersionManager();
        manager.SetClusterVersion(version);
        return manager;
    }

    private static PutJoinIndexCommand JoinCommand() =>
        new(JoinIndexHarness.JoinIndexDefinition(), "db", "source", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), "request", 5, IndexDeploymentMode.Parallel);

    [RavenFact(RavenTestCategory.Cluster)]
    public void TheCommandIsAllowedFromTheVersionOfTheJoinsAndNotBelow()
    {
        Assert.Equal(JoinVersion, ClusterCommandsVersionManager.ClusterCommandsVersions[nameof(PutJoinIndexCommand)]);

        Assert.False(ManagerAt(72_000).CanPutCommand(nameof(PutJoinIndexCommand)));
        Assert.False(ManagerAt(LastVersionWithoutJoins).CanPutCommand(nameof(PutJoinIndexCommand)));
        Assert.True(ManagerAt(JoinVersion).CanPutCommand(nameof(PutJoinIndexCommand)));

        // a build that has the join indexes is a build that can reach the version
        Assert.True(ClusterCommandsVersionManager.MyCommandsVersion >= JoinVersion);
        Assert.True(ManagerAt(ClusterCommandsVersionManager.MyCommandsVersion).CanPutCommand(nameof(PutJoinIndexCommand)));

        // the command that an older build knows is not held back by the join indexes
        Assert.True(ManagerAt(LastVersionWithoutJoins).CanPutCommand(nameof(PutIndexCommand)));
    }

    [RavenFact(RavenTestCategory.Cluster)]
    public void TheLeaderRefusesToProposeTheCommandToAClusterThatHasAnOlderNode()
    {
        var exception = Assert.Throws<RejectPutClusterCommandException>(() => new ClusterValidator(ManagerAt(LastVersionWithoutJoins)).AssertPutCommandToLeader(JoinCommand()));

        Assert.Contains($"'{nameof(PutJoinIndexCommand)}'", exception.Message);
        Assert.Contains($"cluster version is '{LastVersionWithoutJoins}'", exception.Message);
        Assert.Contains($"minimum version of {JoinVersion}", exception.Message);

        new ClusterValidator(ManagerAt(JoinVersion)).AssertPutCommandToLeader(JoinCommand());
    }

    [RavenFact(RavenTestCategory.Cluster)]
    public void AnEntryWithTheCommandIsNotSentToAFollowerThatIsOlder()
    {
        var validator = new ClusterValidator(new ClusterCommandsVersionManager());
        using var context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject entry = context.ReadObject(new DynamicJsonValue
        {
            ["Flags"] = nameof(RachisEntryFlags.StateMachineCommand),
            ["Entry"] = new DynamicJsonValue { ["Type"] = nameof(PutJoinIndexCommand) }
        }, "entry");

        var exception = Assert.Throws<RejectSendToFollowerException>(() => validator.AssertEntryBeforeSendToFollower(entry, LastVersionWithoutJoins, "B"));
        Assert.Contains(nameof(PutJoinIndexCommand), exception.Message);
        Assert.Contains($"with the version {JoinVersion}", exception.Message);
        Assert.Contains("is not supported on follower B", exception.Message);

        validator.AssertEntryBeforeSendToFollower(entry, JoinVersion, "B");
    }

    /// <summary>
    /// The reason for the version. The definition of a join index is stored in the database record with the type 'Join', and every node reads the record. A build that does not have the value parses
    /// the name of the IndexType with Enum.Parse (Sparrow JsonDeserializationBase), which throws for a name it does not have: such a node cannot read the record of the database.
    /// </summary>
    [RavenFact(RavenTestCategory.Cluster)]
    public void TheRecordOfAJoinHasATypeThatAnOlderNodeDoesNotKnow()
    {
        Assert.Equal(8, (int)IndexType.Join);

        var record = new DatabaseRecord("db") { Indexes = new Dictionary<string, IndexDefinition>() };
        JoinCommand().UpdateDatabaseRecord(record, 1);

        using JsonOperationContext context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject json = DocumentConventions.DefaultForServer.Serialization.DefaultConverter.ToBlittable(record, context);

        Assert.True(json.TryGet(nameof(DatabaseRecord.Indexes), out BlittableJsonReaderObject indexes));
        Assert.True(indexes.TryGet(JoinIndexHarness.JoinName, out BlittableJsonReaderObject stored));
        Assert.True(stored.TryGet(nameof(IndexDefinition.Type), out string type));
        Assert.Equal(nameof(IndexType.Join), type);
        Assert.True(stored.TryGet(nameof(IndexDefinition.Join), out BlittableJsonReaderObject _));
    }

    [RavenFact(RavenTestCategory.Cluster | RavenTestCategory.Indexes)]
    public async Task AJoinIsRefusedWithTheVersionsInTheMessageAndNothingIsCreatedWhileTheClusterHasAnOlderNode()
    {
        using RavenServer ownServer = GetNewServer();
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.Server = ownServer;
        using DocumentStore store = GetDocumentStore(options);
        await store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndexHarness.OuterIndex(), JoinIndexHarness.InnerIndex()));

        ClusterCommandsVersionManager versions = ownServer.ServerStore.Engine.CommandsVersionManager;
        int current = versions.CurrentClusterMinimalVersion;
        DatabaseRecord before = store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database));

        try
        {
            versions.SetClusterVersion(LastVersionWithoutJoins);

            var exception = await Assert.ThrowsAnyAsync<Exception>(() => store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndexHarness.JoinIndexDefinition())));
            string text = exception.ToString();
            Assert.Contains(nameof(PutJoinIndexCommand), text);
            Assert.Contains($"cluster version is '{LastVersionWithoutJoins}'", text);
            Assert.Contains($"minimum version of {JoinVersion}", text);

            // the other indexes are still created: only the join indexes are held back
            await store.Maintenance.SendAsync(new PutIndexesOperation(new IndexDefinition { Name = "Other/Index", Maps = { "from c in docs.Contexts select new { c.Country }" } }));
        }
        finally
        {
            versions.SetClusterVersion(current);
        }

        DatabaseRecord after = store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database));
        Assert.Equal(before.Indexes.Count + 1, after.Indexes.Count);
        Assert.False(after.Indexes.ContainsKey(JoinIndexHarness.JoinName));

        // the same join index is put when the cluster is complete again
        await store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndexHarness.JoinIndexDefinition()));
        Assert.True(store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database)).Indexes.ContainsKey(JoinIndexHarness.JoinName));
    }
}
