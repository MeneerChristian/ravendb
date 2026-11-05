using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Indexes;
using Raven.Server.Extensions;
using Raven.Server.Json;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Client.Indexing;

public class JoinIndexDefinitionTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    public class OuterEntry
    {
        public string ContextId { get; set; }

        public string Title { get; set; }
    }

    public class InnerEntry
    {
        public string Country { get; set; }

        public int Rank { get; set; }

        public bool IsActive { get; set; }

        public string Title { get; set; }
    }

    public class Conversations_ByContext : AbstractIndexCreationTask<object, OuterEntry>
    {
        public Conversations_ByContext()
        {
            Map = docs => from d in docs select new OuterEntry { ContextId = "x", Title = "y" };
        }
    }

    public class Contexts_Search : AbstractIndexCreationTask<object, InnerEntry>
    {
        public Contexts_Search()
        {
            Map = docs => from d in docs select new InnerEntry { Country = "x" };
        }
    }

    public class Conversations_WithContext : AbstractJoinCreationTask<OuterEntry, InnerEntry>
    {
        public Conversations_WithContext()
        {
            UseOuterIndex<Conversations_ByContext>();
            UseInnerIndex<Contexts_Search>();
            JoinOn(x => x.ContextId);
            AddField(x => x.Country);
            AddField(x => x.Rank, JoinFieldKind.Long);
            AddField(x => x.Title, JoinFieldKind.Text);
            AddGrouping(x => x.IsActive, true);
        }
    }

    private sealed class Declared<TOuter, TInner> : AbstractJoinCreationTask<TOuter, TInner>
    {
        public Declared(Action<Declared<TOuter, TInner>> declare)
        {
            declare(this);
        }

        public void Outer(string name) => UseOuterIndex(name);

        public void Inner(string name) => UseInnerIndex(name);

        public void Key(Expression<Func<TOuter, object>> key) => JoinOn(key);

        public void Field(Expression<Func<TInner, object>> field, JoinFieldKind kind = JoinFieldKind.Exact) => AddField(field, kind);

        public void Grouping(Expression<Func<TInner, object>> field, object value) => AddGrouping(field, value);
    }

    internal static JoinDefinition Valid()
    {
        return new JoinDefinition { OuterIndex = "Conversations/ByContext", OuterField = "ContextId", InnerIndex = "Contexts/Search" }
            .AddField("Country")
            .AddField("Name")
            .AddField("Rank", JoinFieldKind.Long)
            .AddField("Title", JoinFieldKind.Text)
            .AddGrouping("IsActive", "true");
    }

    private static IndexDefinition JoinIndex(string name = "SearchPairs")
    {
        return new IndexDefinition { Name = name, Join = Valid() };
    }

    private static void AssertSingleError(JoinDefinition definition, JoinValidationCode code)
    {
        List<JoinValidationError> errors = definition.ValidateShape();
        Assert.Equal(code, Assert.Single(errors).Code);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void SectionSurvivesTheJsonRoundTrip()
    {
        using JsonOperationContext context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject json = context.ReadObject(Valid().ToJson(), "join");
        JoinDefinition read = JoinDefinition.FromJson(json);

        Assert.True(JoinDefinition.IsSameJoin(Valid(), read));
        Assert.Equal("Conversations/ByContext", read.OuterIndex);
        Assert.Equal("ContextId", read.OuterField);
        Assert.Equal("Contexts/Search", read.InnerIndex);
        Assert.Equal(new[] { "Country", "Name", "Rank", "Title" }, read.Fields.Select(x => x.Field));
        Assert.Equal(new[] { JoinFieldKind.Exact, JoinFieldKind.Exact, JoinFieldKind.Long, JoinFieldKind.Text }, read.Fields.Select(x => x.Kind));
        Assert.Equal(("IsActive", "true"), (read.Groupings.Single().Field, read.Groupings.Single().Value));
        Assert.Empty(read.ValidateShape());
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void UnknownFieldKindIsRefused()
    {
        using JsonOperationContext context = JsonOperationContext.ShortTermSingleUse();
        var json = new DynamicJsonValue
        {
            ["Fields"] = new DynamicJsonArray { new DynamicJsonValue { ["Field"] = "Country", ["Kind"] = "Bitmap" } }
        };

        using BlittableJsonReaderObject blittable = context.ReadObject(json, "join");
        JoinValidationException exception = Assert.Throws<JoinValidationException>(() => JoinDefinition.FromJson(blittable));
        Assert.Equal(JoinValidationCode.InvalidDefinition, Assert.Single(exception.Errors).Code);
        Assert.Contains("'Country'", exception.Message);
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData("OuterIndex")]
    [InlineData("OuterField")]
    [InlineData("InnerIndex")]
    public void RequiredPropertiesAreChecked(string property)
    {
        JoinDefinition definition = Valid();
        switch (property)
        {
            case "OuterIndex":
                definition.OuterIndex = " ";
                break;
            case "OuterField":
                definition.OuterField = null;
                break;
            default:
                definition.InnerIndex = "";
                break;
        }

        JoinValidationError error = Assert.Single(definition.ValidateShape());
        Assert.Equal(JoinValidationCode.InvalidDefinition, error.Code);
        Assert.Contains(property, error.Message);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void FieldAndGroupingLimitsAreEnforced()
    {
        JoinDefinition definition = Valid();
        definition.Fields.Clear();
        for (int i = 0; i < JoinLimits.MaxFields; i++)
            definition.AddField("F" + i);
        Assert.Empty(definition.ValidateShape());

        definition.AddField("OneTooMany");
        AssertSingleError(definition, JoinValidationCode.LimitExceeded);

        definition = Valid();
        definition.Groupings.Clear();
        for (int i = 0; i < JoinLimits.MaxGroupings; i++)
            definition.AddGrouping("G" + i, "v");
        Assert.Empty(definition.ValidateShape());

        definition.AddGrouping("OneTooMany", "v");
        AssertSingleError(definition, JoinValidationCode.LimitExceeded);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void JsonOfAnOlderVersionWithAStoreSortPropertyIsReadAndTheUnknownPropertyIsNotWrittenAgain()
    {
        var old = new DynamicJsonValue
        {
            ["OuterIndex"] = "Conversations/ByContext",
            ["OuterField"] = "ContextId",
            ["InnerIndex"] = "Contexts/Search",
            ["Fields"] = new DynamicJsonArray
            {
                new DynamicJsonValue { ["Field"] = "Country", ["Kind"] = "Exact", ["StoreSort"] = false },
                new DynamicJsonValue { ["Field"] = "Rank", ["Kind"] = "Long", ["StoreSort"] = true }
            },
            ["Groupings"] = new DynamicJsonArray()
        };

        using JsonOperationContext context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject json = context.ReadObject(old, "join");
        JoinDefinition read = JoinDefinition.FromJson(json);

        Assert.Equal(new[] { "Country", "Rank" }, read.Fields.Select(x => x.Field));
        Assert.Equal(new[] { JoinFieldKind.Exact, JoinFieldKind.Long }, read.Fields.Select(x => x.Kind));
        Assert.Empty(read.ValidateShape());

        using BlittableJsonReaderObject written = context.ReadObject(read.ToJson(), "join");
        foreach (object field in (BlittableJsonReaderArray)written["Fields"])
            Assert.False(((BlittableJsonReaderObject)field).TryGet("StoreSort", out bool _));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void DuplicateFieldsAndGroupingsAreRefusedAndAnotherValueIsAnotherGrouping()
    {
        JoinDefinition definition = Valid();
        definition.AddField("Country", JoinFieldKind.Text);
        AssertSingleError(definition, JoinValidationCode.InvalidDefinition);

        definition = Valid();
        definition.AddGrouping("IsActive", "true");
        AssertSingleError(definition, JoinValidationCode.InvalidDefinition);

        definition = Valid();
        definition.AddGrouping("IsActive", "false");
        Assert.Empty(definition.ValidateShape());
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void GroupingValueIsBoundedInBytes()
    {
        JoinDefinition definition = Valid();
        definition.Groupings.Clear();
        definition.AddGrouping("G", new string('a', JoinLimits.MaxTermLength));
        Assert.Empty(definition.ValidateShape());

        definition.Groupings.Clear();
        definition.AddGrouping("G", new string('ü', JoinLimits.MaxTermLength / 2 + 1));
        AssertSingleError(definition, JoinValidationCode.LimitExceeded);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void DefinitionWithASectionIsAJoinWithoutMaps()
    {
        IndexDefinition definition = JoinIndex();

        Assert.Equal(IndexType.Join, definition.Type);
        Assert.Equal(IndexSourceType.Documents, definition.SourceType);
        Assert.Empty(definition.Maps);
        Assert.Empty(definition.Fields);
        Assert.True(definition.Type.IsStatic());
        Assert.False(definition.Type.IsMap());
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheJoinIsPartOfCompareEqualsAndTheHash()
    {
        IndexDefinition left = JoinIndex();
        IndexDefinition same = JoinIndex();
        same.Join.OuterIndex = "conversations/bycontext";

        Assert.Equal(IndexDefinitionCompareDifferences.None, left.Compare(same));
        Assert.Equal(left, same);
        Assert.Equal(left.GetHashCode(), same.GetHashCode());

        IndexDefinition otherField = JoinIndex();
        otherField.Join.AddField("More");
        Assert.Equal(IndexDefinitionCompareDifferences.Join, left.Compare(otherField));
        Assert.NotEqual(left, otherField);

        IndexDefinition otherGrouping = JoinIndex();
        otherGrouping.Join.Groupings[0].Value = "false";
        Assert.Equal(IndexDefinitionCompareDifferences.Join, left.Compare(otherGrouping));

        IndexDefinition otherInner = JoinIndex();
        otherInner.Join.InnerIndex = "Contexts/Other";
        Assert.Equal(IndexDefinitionCompareDifferences.Join, left.Compare(otherInner));

        IndexDefinition otherOrder = JoinIndex();
        otherOrder.Join.Fields.Reverse();
        Assert.Equal(IndexDefinitionCompareDifferences.Join, left.Compare(otherOrder));

        IndexDefinition otherPriority = JoinIndex();
        otherPriority.Priority = IndexPriority.High;
        Assert.Equal(IndexDefinitionCompareDifferences.Priority, left.Compare(otherPriority));

        var withoutSection = new IndexDefinition { Name = "SearchPairs", Maps = { "from d in docs select new { d.Name }" } };
        Assert.True(left.Compare(withoutSection).HasFlag(IndexDefinitionCompareDifferences.Join));
        Assert.True(IndexDefinition.ReIndexRequiredMask.HasFlag(IndexDefinitionCompareDifferences.Join));
        Assert.True(IndexDefinitionCompareDifferences.All.HasFlag(IndexDefinitionCompareDifferences.Join));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void HashOfADefinitionWithoutASectionDoesNotChange()
    {
        var map = new IndexDefinition { Name = "Users/ByName", Maps = { "from u in docs.Users select new { u.Name }" } };
        int before = map.GetHashCode();
        map.Join = null;

        Assert.Equal(before, map.GetHashCode());
        Assert.Equal(IndexType.Map, map.Type);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void CopyToCopiesTheSection()
    {
        IndexDefinition source = JoinIndex();
        var copy = new IndexDefinition();
        source.CopyTo(copy);

        Assert.NotSame(source.Join, copy.Join);
        Assert.True(JoinDefinition.IsSameJoin(source.Join, copy.Join));

        copy.Join.AddField("Changed");
        Assert.Equal(4, source.Join.Fields.Count);
        Assert.Equal(IndexType.Join, copy.Type);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void DefinitionSurvivesTheClientSerializationAndTheServerDeserialization()
    {
        using JsonOperationContext context = JsonOperationContext.ShortTermSingleUse();
        IndexDefinition sent = JoinIndex();
        sent.Priority = IndexPriority.Low;

        using BlittableJsonReaderObject json = DocumentConventions.Default.Serialization.DefaultConverter.ToBlittable(sent, context);
        IndexDefinition received = JsonDeserializationServer.IndexDefinition(json);

        Assert.Equal("SearchPairs", received.Name);
        Assert.Equal(IndexType.Join, received.Type);
        Assert.Equal(IndexPriority.Low, received.Priority);
        Assert.Equal(IndexDefinitionCompareDifferences.None, sent.Compare(received));
        Assert.True(JoinDefinition.IsSameJoin(sent.Join, received.Join));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void DefinitionSurvivesTheServerJsonOfTheRecordAndTheCommand()
    {
        using JsonOperationContext context = JsonOperationContext.ShortTermSingleUse();
        IndexDefinition stored = JoinIndex();

        using BlittableJsonReaderObject json = context.ReadObject(stored.ToJson(), "index/def");
        IndexDefinition read = JsonDeserializationServer.IndexDefinition(json);

        Assert.Equal(IndexType.Join, read.Type);
        Assert.Equal(IndexDefinitionCompareDifferences.None, stored.Compare(read));
        Assert.True(JoinDefinition.IsSameJoin(stored.Join, read.Join));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ATypeNameThatTheReaderDoesNotKnowFailsTheDeserializationOfTheDefinition()
    {
        // what a node without IndexType.Join does with the name of the type of a join index: the whole record is unreadable, so a join index must never reach such a node
        using JsonOperationContext context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject json = context.ReadObject(
            new DynamicJsonValue { ["Name"] = "SearchPairs", ["Type"] = "NotAKnownType", ["Maps"] = new DynamicJsonArray() }, "index/def");

        Assert.Throws<FormatException>(() => JsonDeserializationServer.IndexDefinition(json));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ADefinitionWithoutMapsAndWithoutASectionCannotDetectItsType()
    {
        // what a node that reads the section of a join index as an unknown property would see
        var withoutSection = new IndexDefinition { Name = "SearchPairs" };

        Assert.Throws<ArgumentNullException>(() => withoutSection.Type);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheClassNameIsTheNameOfTheIndexAndTheDeclarationIsTheSection()
    {
        var task = new Conversations_WithContext();
        IndexDefinition definition = task.CreateIndexDefinition();

        Assert.Equal("Conversations/WithContext", task.IndexName);
        Assert.Equal("Conversations/WithContext", definition.Name);
        Assert.Equal(IndexType.Join, definition.Type);
        Assert.Equal("Conversations/ByContext", definition.Join.OuterIndex);
        Assert.Equal("Contexts/Search", definition.Join.InnerIndex);
        Assert.Equal("ContextId", definition.Join.OuterField);
        Assert.Equal(new[] { "Country", "Rank", "Title" }, definition.Join.Fields.Select(x => x.Field));
        Assert.Equal(new[] { JoinFieldKind.Exact, JoinFieldKind.Long, JoinFieldKind.Text }, definition.Join.Fields.Select(x => x.Kind));
        Assert.Equal(("IsActive", "true"), (definition.Join.Groupings.Single().Field, definition.Join.Groupings.Single().Value));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheSettingsOfTheTaskAreTheSettingsOfTheIndex()
    {
        var task = new Conversations_WithContext { Priority = IndexPriority.Low, LockMode = IndexLockMode.LockedIgnore, DeploymentMode = IndexDeploymentMode.Rolling, State = IndexState.Disabled };
        IndexDefinition definition = task.CreateIndexDefinition();

        Assert.Equal(IndexPriority.Low, definition.Priority);
        Assert.Equal(IndexLockMode.LockedIgnore, definition.LockMode);
        Assert.Equal(IndexDeploymentMode.Rolling, definition.DeploymentMode);
        Assert.Equal(IndexState.Disabled, definition.State);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheTaskIsFoundByTheScanOfAnAssembly()
    {
        Assert.Contains(typeof(Conversations_WithContext), typeof(Conversations_WithContext).Assembly.GetTypes().Where(x => typeof(IAbstractIndexCreationTask).IsAssignableFrom(x)));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ExpressionsAndStringsMixInTheOrderOfDeclaration()
    {
        IndexDefinition definition = new Declared<OuterEntry, InnerEntry>(j =>
        {
            j.Outer("Outer/Index");
            j.Inner("Inner/Index");
            j.Key(x => x.ContextId);
            j.Field(x => x.Country);
            j.Field(x => x.Title, JoinFieldKind.Text);
            j.Grouping(x => x.IsActive, false);
            j.Grouping(x => x.Rank, 7);
        }).CreateIndexDefinition();

        Assert.Equal(new[] { "Country", "Title" }, definition.Join.Fields.Select(x => x.Field));
        Assert.Equal(new[] { "IsActive=false", "Rank=7" }, definition.Join.Groupings.Select(x => $"{x.Field}={x.Value}"));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ADeclarationThatIsNotValidIsRefusedWhileTheDefinitionIsCreated()
    {
        JoinValidationException noKey = Assert.Throws<JoinValidationException>(() => new Declared<OuterEntry, InnerEntry>(j =>
        {
            j.Outer("Outer/Index");
            j.Inner("Inner/Index");
        }).CreateIndexDefinition());
        Assert.Contains(noKey.Errors, x => x.Code == JoinValidationCode.InvalidDefinition && x.Message.Contains("OuterField"));

        JoinValidationException nested = Assert.Throws<JoinValidationException>(() => new Declared<OuterEntry, InnerEntry>(j =>
        {
            j.Outer("Outer/Index");
            j.Inner("Inner/Index");
            j.Key(x => x.ContextId.Length);
        }).CreateIndexDefinition());
        Assert.Contains(nested.Errors, x => x.Code == JoinValidationCode.UnsupportedExpression);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheInnerFieldOfAJoinOnAFieldSurvivesTheJsonRoundTripAndIsPartOfTheJoin()
    {
        JoinDefinition field = Valid();
        field.InnerField = "Code";

        using JsonOperationContext context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject json = context.ReadObject(field.ToJson(), "join");
        JoinDefinition read = JoinDefinition.FromJson(json);

        Assert.Equal("Code", read.InnerField);
        Assert.True(JoinDefinition.IsSameJoin(field, read));
        Assert.Equal(field.GetJoinHashCode(), read.GetJoinHashCode());
        Assert.Equal("Code", field.Clone().InnerField);

        // a join on the id and a join on a field are two joins, and so are two fields
        Assert.False(JoinDefinition.IsSameJoin(field, Valid()));
        Assert.NotEqual(field.GetJoinHashCode(), Valid().GetJoinHashCode());
        JoinDefinition other = field.Clone();
        other.InnerField = "Name";
        Assert.False(JoinDefinition.IsSameJoin(field, other));
        Assert.Empty(field.ValidateShape());
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AJoinOnTheIdWritesNoInnerFieldSoItsJsonIsTheOneOfBefore()
    {
        using JsonOperationContext context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject json = context.ReadObject(Valid().ToJson(), "join");

        Assert.False(json.TryGet("InnerField", out string _));
        Assert.Null(JoinDefinition.FromJson(json).InnerField);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AnEmptyInnerFieldIsRefused()
    {
        JoinDefinition definition = Valid();
        definition.InnerField = " ";

        JoinValidationError error = Assert.Single(definition.ValidateShape());
        Assert.Equal(JoinValidationCode.InvalidDefinition, error.Code);
        Assert.Contains("InnerField", error.Message);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheIdOfTheOuterDocumentIsTheKeyNamedIdAndIsPartOfTheJoin()
    {
        JoinDefinition byId = Valid();
        byId.OuterField = "id()";
        byId.InnerField = "Code";
        Assert.True(byId.JoinsOnOuterId);
        Assert.False(Valid().JoinsOnOuterId);

        using JsonOperationContext context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject json = context.ReadObject(byId.ToJson(), "join");
        JoinDefinition read = JoinDefinition.FromJson(json);

        Assert.True(read.JoinsOnOuterId);
        Assert.Equal("id()", read.OuterField);
        Assert.True(JoinDefinition.IsSameJoin(byId, read));
        Assert.Equal(byId.GetJoinHashCode(), read.GetJoinHashCode());
        Assert.True(byId.Clone().JoinsOnOuterId);
        Assert.Empty(byId.ValidateShape());

        // a field named like a document is another join
        JoinDefinition field = byId.Clone();
        field.OuterField = "Id";
        Assert.False(JoinDefinition.IsSameJoin(byId, field));
        Assert.NotEqual(byId.GetJoinHashCode(), field.GetJoinHashCode());
        Assert.False(field.JoinsOnOuterId);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheIdOfTheOuterDocumentNeedsAnInnerField()
    {
        JoinDefinition definition = Valid();
        definition.OuterField = "id()";

        JoinValidationError error = Assert.Single(definition.ValidateShape());
        Assert.Equal(JoinValidationCode.InvalidDefinition, error.Code);
        Assert.Contains("InnerField", error.Message);
    }
}
