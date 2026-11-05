using System;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Storage;

public class JoinSchemaTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    [RavenFact(RavenTestCategory.Voron)]
    public void SchemaOfADefinitionDependsOnWhatTheListsHold()
    {
        var definition = new JoinDefinition { OuterIndex = "O", OuterField = "F", InnerIndex = "I" }
            .AddField("Country")
            .AddField("Title", JoinFieldKind.Text)
            .AddField("Rank", JoinFieldKind.Long)
            .AddGrouping("IsActive", "true");

        JoinSchema schema = JoinSchema.From(definition);

        Assert.Equal(3, schema.FieldCount);
        Assert.Equal(1, schema.GroupingCount);
        Assert.False(schema.IsMultiValued(0));
        Assert.True(schema.IsMultiValued(1));
        Assert.Equal(schema.Hash, JoinSchema.From(definition).Hash);

        // the names of the indexes do not change what the lists hold
        var renamed = JoinSchema.From(new JoinDefinition { OuterIndex = "o", OuterField = "F", InnerIndex = "i", Fields = definition.Fields, Groupings = definition.Groupings });
        Assert.Equal(schema.Hash, renamed.Hash);

        definition.AddField("Name");
        Assert.NotEqual(schema.Hash, JoinSchema.From(definition).Hash);

        definition.Fields.RemoveAt(3);
        definition.Groupings[0].Value = "false";
        Assert.NotEqual(schema.Hash, JoinSchema.From(definition).Hash);
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void AJoinOnAFieldIsAnotherSchemaAndAJoinOnTheIdKeepsItsHash()
    {
        var id = new JoinDefinition { OuterIndex = "O", OuterField = "F", InnerIndex = "I" }.AddField("Country");
        var field = id.Clone();
        field.InnerField = "Code";
        var otherField = id.Clone();
        otherField.InnerField = "Name";

        JoinSchema idSchema = JoinSchema.From(id);
        JoinSchema fieldSchema = JoinSchema.From(field);

        Assert.False(idSchema.IsFieldJoin);
        Assert.True(fieldSchema.IsFieldJoin);
        Assert.NotEqual(idSchema.Hash, fieldSchema.Hash);
        Assert.NotEqual(fieldSchema.Hash, JoinSchema.From(otherField).Hash);
        Assert.Equal(fieldSchema.Hash, JoinSchema.From(field.Clone()).Hash);
    }

    private static JoinDefinition Shaped(string innerField)
    {
        return new JoinDefinition { OuterIndex = "Conversations/ByContext", OuterField = "ContextId", InnerField = innerField, InnerIndex = "Contexts/Search" }
            .AddField("Country")
            .AddField("Title", JoinFieldKind.Text)
            .AddField("Rank", JoinFieldKind.Long)
            .AddGrouping("IsActive", "true");
    }

    // The values were computed outside of this code base, from the text that the schema hashes (XXH64 of the lines of the definition), before the storage modes existed:
    // a file that an older build wrote has these hashes, and a build with the modes must open it as it is.
    [RavenFact(RavenTestCategory.Voron)]
    public void TheHashesOfExpandedSchemasAreTheOnesOfTheBuildsBeforeTheModes()
    {
        Assert.Equal(4373563743899629396L, JoinSchema.From(Shaped(null)).Hash);
        Assert.Equal(4009865409269581155L, JoinSchema.From(Shaped("Code")).Hash);
        Assert.Equal(4009865409269581155L, JoinSchema.From(Shaped("Code"), JoinStorageMode.Expanded).Hash);
        Assert.Equal(JoinStorageMode.Expanded, JoinSchema.From(Shaped("Code")).Mode);
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void TheHashOfAnIndirectSchemaIsAnotherOne()
    {
        JoinSchema indirect = JoinSchema.From(Shaped("Code"), JoinStorageMode.Indirect);

        Assert.Equal(-324854691065380478L, indirect.Hash);
        Assert.NotEqual(JoinSchema.From(Shaped("Code")).Hash, indirect.Hash);
        Assert.Equal(JoinStorageMode.Indirect, indirect.Mode);
        Assert.True(indirect.IsIndirect);
        Assert.True(indirect.IsFieldJoin);
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void AJoinOnTheIdOfTheInnerDocumentsHasAnIndirectSchemaWithKeysAndAFileHasAResolvedMode()
    {
        // computed outside of this code base (XXH64 of the text of the schema, which says that the keys are the ids of the inner documents; the same program gives the three values above for their texts)
        JoinSchema idIndirect = JoinSchema.From(Shaped(null), JoinStorageMode.Indirect);
        Assert.Equal(4250470275487495937L, idIndirect.Hash);
        Assert.True(idIndirect.IsIndirect);
        Assert.True(idIndirect.IsFieldJoin);
        Assert.True(idIndirect.KeysAreInnerIds);
        Assert.False(JoinSchema.From(Shaped("Code"), JoinStorageMode.Indirect).KeysAreInnerIds);
        Assert.False(JoinSchema.From(Shaped(null)).KeysAreInnerIds);
        Assert.Throws<ArgumentException>(() => JoinSchema.Create(3, 1, isFieldJoin: true, keysAreInnerIds: true));
        Assert.NotEqual(JoinSchema.From(Shaped(null)).Hash, idIndirect.Hash);
        Assert.NotEqual(JoinSchema.From(Shaped("Code"), JoinStorageMode.Indirect).Hash, idIndirect.Hash);
        Assert.False(JoinSchema.From(Shaped(null)).IsFieldJoin);

        Assert.Throws<ArgumentOutOfRangeException>(() => JoinSchema.From(Shaped("Code"), JoinStorageMode.Auto));
        Assert.Throws<ArgumentException>(() => JoinSchema.Create(3, 1, isFieldJoin: false, mode: JoinStorageMode.Indirect));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void ThePlanKeepsTheModeOfAFileThatExistsWhenTheDefinitionLeavesItToTheServer()
    {
        using var plan = new JoinPlan(Shaped("Code"), JoinStorageMode.Indirect);

        Assert.Equal(JoinStorageMode.Indirect, plan.Schema.Mode);
        Assert.Equal(JoinStorageMode.Expanded, plan.SchemaOf(JoinStorageMode.Expanded).Mode);
        Assert.Equal(JoinStorageMode.Indirect, plan.SchemaOf(JoinStorageMode.Indirect).Mode);
        Assert.Null(plan.SchemaOf(JoinStorageMode.Auto));

        // a join on the id of the inner documents keeps the groups of the inner documents by their id in an Indirect file, and none in an Expanded one
        using var idPlan = new JoinPlan(Shaped(null));
        Assert.False(idPlan.IsFieldJoin);
        Assert.True(idPlan.SchemaOf(JoinStorageMode.Indirect).IsFieldJoin);
        using var idIndirectPlan = new JoinPlan(Shaped(null), JoinStorageMode.Indirect);
        Assert.True(idIndirectPlan.IsFieldJoin);
        Assert.Equal(idIndirectPlan.IdField.ToString(), idIndirectPlan.InnerKeyField.ToString());

        // a join on the id of the outer document has no Indirect file
        JoinDefinition onOuterId = Shaped("Code");
        onOuterId.OuterField = JoinDefinition.OuterIdKey;
        using var outerIdPlan = new JoinPlan(onOuterId);
        Assert.Null(outerIdPlan.SchemaOf(JoinStorageMode.Indirect));
        Assert.Throws<ArgumentException>(() => new JoinPlan(onOuterId, JoinStorageMode.Indirect));
    }
}
