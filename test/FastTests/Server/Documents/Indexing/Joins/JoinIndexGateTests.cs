using System;
using Raven.Client.Documents.Indexes;
using Raven.Server.Rachis;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Commands.Indexes;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins;

public class JoinIndexGateTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    [RavenFact(RavenTestCategory.Cluster)]
    public void TheCommandHasAVersionThatIsHigherThanTheOneOfTheCommandOfAnIndex()
    {
        var versions = new ClusterCommandsVersionManager();
        int version = ClusterCommandsVersionManager.ClusterCommandsVersions[nameof(PutJoinIndexCommand)];

        Assert.True(version > ClusterCommandsVersionManager.ClusterCommandsVersions[nameof(PutIndexCommand)]);
        versions.SetClusterVersion(version - 1);
        Assert.False(versions.CanPutCommand(nameof(PutJoinIndexCommand)));
        Assert.True(versions.CanPutCommand(nameof(PutIndexCommand)));
        versions.SetClusterVersion(version);
        Assert.True(versions.CanPutCommand(nameof(PutJoinIndexCommand)));
    }

    [RavenFact(RavenTestCategory.Cluster)]
    public void AFollowerThatDoesNotKnowTheCommandIsNotSentTheEntry()
    {
        var validator = new ClusterValidator(new ClusterCommandsVersionManager());
        int version = ClusterCommandsVersionManager.ClusterCommandsVersions[nameof(PutJoinIndexCommand)];
        using var context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject entry = context.ReadObject(new DynamicJsonValue
        {
            ["Flags"] = nameof(RachisEntryFlags.StateMachineCommand),
            ["Entry"] = new DynamicJsonValue { ["Type"] = nameof(PutJoinIndexCommand) }
        }, "entry");

        Assert.ThrowsAny<Exception>(() => validator.AssertEntryBeforeSendToFollower(entry, version - 1, "B"));
        validator.AssertEntryBeforeSendToFollower(entry, version, "B");
    }

    [RavenFact(RavenTestCategory.Cluster)]
    public void TheCommandSurvivesTheJsonOfTheRaftLog()
    {
        var definition = new IndexDefinition
        {
            Name = "Conversations/WithContext",
            Join = new JoinDefinition { OuterIndex = "Conversations/ByContext", OuterField = "ContextId", InnerIndex = "Contexts/Search" }.AddField("Country").AddGrouping("IsActive", "true")
        };
        var command = new PutJoinIndexCommand(definition, "db", "source", new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc), "request", 5, IndexDeploymentMode.Rolling);

        using JsonOperationContext context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject json = context.ReadObject(command.ToJson(context), "command");
        var read = (PutJoinIndexCommand)JsonDeserializationCluster.Commands[nameof(PutJoinIndexCommand)](json);

        Assert.Equal("db", read.DatabaseName);
        Assert.Equal("request", read.UniqueRequestId);
        Assert.Equal(5, read.RevisionsToKeep);
        Assert.Equal(IndexDeploymentMode.Rolling, read.DefaultDeploymentMode);
        Assert.Equal(IndexType.Join, read.Definition.Type);
        Assert.True(JoinDefinition.IsSameJoin(definition.Join, read.Definition.Join));
    }
}
