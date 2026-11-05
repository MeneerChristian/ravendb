using System;
using System.Collections.Generic;
using Raven.Client.Documents.Indexes;
using Raven.Client.ServerWide;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Auto;
using Raven.Server.Rachis;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Commands.Indexes;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Auto;

public class JoinIndexAutoGateTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    private static AutoIndexDefinition Definition()
    {
        return new AutoIndexDefinition
        {
            Name = "Auto/Conversations/ByContext/Join/Contexts/Search/OnContextIdToId",
            Type = IndexType.AutoJoin,
            Collection = "Conversations",
            Join = new JoinDefinition { OuterIndex = "Conversations/ByContext", OuterField = "ContextId", InnerIndex = "Contexts/Search" }
                .AddField("Country").AddField("Rank", JoinFieldKind.Long)
        };
    }

    [RavenFact(RavenTestCategory.Cluster)]
    public void TheCommandHasTheVersionOfTheCommandOfAJoinAndIsNotSentToAnOlderNode()
    {
        int version = ClusterCommandsVersionManager.ClusterCommandsVersions[nameof(PutAutoJoinIndexCommand)];
        Assert.Equal(ClusterCommandsVersionManager.ClusterCommandsVersions[nameof(PutJoinIndexCommand)], version);
        Assert.True(version > ClusterCommandsVersionManager.ClusterCommandsVersions[nameof(PutAutoIndexCommand)]);

        var versions = new ClusterCommandsVersionManager();
        versions.SetClusterVersion(version - 1);
        Assert.False(versions.CanPutCommand(nameof(PutAutoJoinIndexCommand)));
        Assert.True(versions.CanPutCommand(nameof(PutAutoIndexCommand)));
        versions.SetClusterVersion(version);
        Assert.True(versions.CanPutCommand(nameof(PutAutoJoinIndexCommand)));

        var validator = new ClusterValidator(new ClusterCommandsVersionManager());
        using var context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject entry = context.ReadObject(new DynamicJsonValue
        {
            ["Flags"] = nameof(RachisEntryFlags.StateMachineCommand),
            ["Entry"] = new DynamicJsonValue { ["Type"] = nameof(PutAutoJoinIndexCommand) }
        }, "entry");

        Assert.ThrowsAny<Exception>(() => validator.AssertEntryBeforeSendToFollower(entry, version - 1, "B"));
        validator.AssertEntryBeforeSendToFollower(entry, version, "B");
    }

    [RavenFact(RavenTestCategory.Cluster)]
    public void TheCommandSurvivesTheJsonOfTheRaftLog()
    {
        AutoIndexDefinition definition = Definition();
        var command = new PutAutoJoinIndexCommand(definition, "db", "request", IndexDeploymentMode.Rolling, new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc));

        using JsonOperationContext context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject json = context.ReadObject(command.ToJson(context), "command");
        var read = (PutAutoJoinIndexCommand)JsonDeserializationCluster.Commands[nameof(PutAutoJoinIndexCommand)](json);

        Assert.Equal("db", read.DatabaseName);
        Assert.Equal("request", read.UniqueRequestId);
        Assert.Equal(IndexDeploymentMode.Rolling, read.DefaultAutoDeploymentMode);
        Assert.Equal(IndexType.AutoJoin, read.Definition.Type);
        Assert.Equal(definition.Name, read.Definition.Name);
        Assert.True(JoinDefinition.IsSameJoin(definition.Join, read.Definition.Join));
        Assert.Equal(IndexDefinitionCompareDifferences.None, definition.Compare(read.Definition));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheRecordAddsTheJoinOnceAndRefusesAnotherDefinitionOfTheName()
    {
        var record = new DatabaseRecord("db") { AutoIndexes = new Dictionary<string, AutoIndexDefinition>() };
        var command = new PutAutoJoinIndexCommand(Definition(), "db", "r1", IndexDeploymentMode.Parallel);
        command.UpdateDatabaseRecord(record, 5);
        Assert.Equal(IndexType.AutoJoin, record.AutoIndexes[Definition().Name].Type);

        // the same definition again (two nodes asked at once) changes nothing
        new PutAutoJoinIndexCommand(Definition(), "db", "r2", IndexDeploymentMode.Parallel).UpdateDatabaseRecord(record, 6);
        Assert.Single(record.AutoIndexes);
        Assert.Equal(5, record.AutoIndexes[Definition().Name].ClusterState.LastIndex);

        AutoIndexDefinition other = Definition();
        other.Join.AddField("Name");
        Assert.Throws<RachisApplyException>(() => new PutAutoJoinIndexCommand(other, "db", "r3", IndexDeploymentMode.Parallel).UpdateDatabaseRecord(record, 7));

        // a definition of something else than an auto join index is not put by this command
        AutoIndexDefinition map = Definition();
        map.Type = IndexType.AutoMap;
        Assert.Throws<RachisApplyException>(() => new PutAutoJoinIndexCommand(map, "db", "r4", IndexDeploymentMode.Parallel).UpdateDatabaseRecord(new DatabaseRecord("db") { AutoIndexes = new Dictionary<string, AutoIndexDefinition>() }, 8));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheNameTellsTheJoinAndNotTheFields()
    {
        var joinIndex = new JoinDefinition { OuterIndex = "Auto/Conversations/ByContextId", OuterField = "ContextId", InnerIndex = "Contexts/Search" }.AddField("Country");
        Assert.Equal("Auto/Auto/Conversations/ByContextId/Join/Contexts/Search/OnContextIdToId", AutoJoinShape.NameOf(joinIndex));

        joinIndex.AddField("Rank", JoinFieldKind.Long);
        Assert.Equal("Auto/Auto/Conversations/ByContextId/Join/Contexts/Search/OnContextIdToId", AutoJoinShape.NameOf(joinIndex));

        joinIndex.InnerField = "Code";
        Assert.EndsWith("/OnContextIdToCode", AutoJoinShape.NameOf(joinIndex));

        joinIndex.OuterField = "id()";
        Assert.EndsWith("/OnIdOfOuterToCode", AutoJoinShape.NameOf(joinIndex));
        Assert.True(IndexStore.IsValidIndexName(AutoJoinShape.NameOf(joinIndex), isStatic: false, out _));
        Assert.False(IndexStore.IsValidIndexName(AutoJoinShape.NameOf(joinIndex), isStatic: true, out _));
    }
}
