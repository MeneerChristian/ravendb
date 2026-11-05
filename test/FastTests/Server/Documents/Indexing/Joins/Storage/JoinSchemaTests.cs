using Raven.Client.Documents.Indexes;
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
}
