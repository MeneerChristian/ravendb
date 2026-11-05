using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Indexes;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Client.Indexing;

// What a class that declares a join index in code accepts and refuses: the translation of the expressions and the rules that need no server
public class JoinCreationTaskTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    public class ProbeOuter
    {
        public string ContextId { get; set; }

        public int Number { get; set; }

        public string[] Ids { get; set; }

        public List<string> IdList { get; set; }

        public int[] Numbers { get; set; }
    }

    public class ProbeInner
    {
        public string Country { get; set; }

        public string Name { get; set; }

        public int Rank { get; set; }

        public long? Weight { get; set; }

        public bool IsActive { get; set; }

        public string Title { get; set; }

        public string[] Tags { get; set; }

        public DateTime Created { get; set; }

        public ProbeAddress Address { get; set; }

        public string Field;
    }

    public class ProbeAddress
    {
        public string City { get; set; }
    }

    public class Conversations_ByContext : AbstractIndexCreationTask<object, ProbeOuter>
    {
        public Conversations_ByContext()
        {
            Map = docs => from d in docs select new ProbeOuter { ContextId = "x" };
        }
    }

    public class Contexts_Search : AbstractIndexCreationTask<object, ProbeInner>
    {
        public Contexts_Search()
        {
            Map = docs => from d in docs select new ProbeInner { Country = "x" };
        }
    }

    public class Conversations_WithContexts : AbstractJoinCreationTask<ProbeOuter, ProbeInner>
    {
        public Conversations_WithContexts()
        {
            UseOuterIndex<Conversations_ByContext>();
            UseInnerIndex<Contexts_Search>();
            JoinOn(x => x.ContextId);
            AddField(x => x.Country);
            AddField(x => x.Name);
            AddField(x => x.Rank, JoinFieldKind.Long);
            AddField(x => x.Title, JoinFieldKind.Text);
            AddGrouping(x => x.IsActive, true);
        }
    }

    private sealed class Probe : AbstractJoinCreationTask<ProbeOuter, ProbeInner>
    {
        public Probe(Action<Probe> declare)
        {
            declare(this);
        }

        public void Outer<TIndex>() where TIndex : AbstractIndexCreationTask, new() => UseOuterIndex<TIndex>();

        public void Inner<TIndex>() where TIndex : AbstractIndexCreationTask, new() => UseInnerIndex<TIndex>();

        public void Outer(string name) => UseOuterIndex(name);

        public void Inner(string name) => UseInnerIndex(name);

        public void Key(Expression<Func<ProbeOuter, object>> key) => JoinOn(key);

        public void Key(string key) => JoinOn(key);

        public void Join(Expression<Func<ProbeOuter, object>> outerKey, Expression<Func<ProbeInner, object>> innerKey) => JoinOn(outerKey, innerKey);

        public void Join(string outerKey, string innerKey) => JoinOn(outerKey, innerKey);

        public void JoinById(Expression<Func<ProbeInner, object>> innerKey) => JoinOnId(innerKey);

        public void JoinById(string innerKey) => JoinOnId(innerKey);

        public void Field(Expression<Func<ProbeInner, object>> field, JoinFieldKind kind = JoinFieldKind.Exact) => AddField(field, kind);

        public void Field(string field, JoinFieldKind kind = JoinFieldKind.Exact) => AddField(field, kind);

        public void Grouping(Expression<Func<ProbeInner, object>> field, object value) => AddGrouping(field, value);

        public void Grouping(string field, object value) => AddGrouping(field, value);
    }

    private static void Basics(Probe j)
    {
        j.Outer("Outer/Index");
        j.Inner("Inner/Index");
        j.Key(x => x.ContextId);
    }

    private static JoinDefinition Declare(Action<Probe> declare, DocumentConventions conventions = null)
    {
        var task = new Probe(declare) { Conventions = conventions };
        return task.CreateIndexDefinition().Join;
    }

    private static JoinValidationException Refused(Action<Probe> declare, JoinValidationCode? code = null)
    {
        var exception = Assert.Throws<JoinValidationException>(() => Declare(declare));
        if (code != null)
            Assert.Contains(exception.Errors, x => x.Code == code.Value);

        return exception;
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ExpressionsBuildTheSameDefinitionAsTheStringApi()
    {
        JoinDefinition declared = new Conversations_WithContexts().CreateIndexDefinition().Join;

        var byHand = new JoinDefinition { OuterIndex = "Conversations/ByContext", OuterField = "ContextId", InnerIndex = "Contexts/Search" }
            .AddField("Country").AddField("Name").AddField("Rank", JoinFieldKind.Long).AddField("Title", JoinFieldKind.Text).AddGrouping("IsActive", "true");

        Assert.True(JoinDefinition.IsSameJoin(declared, byHand));
        Assert.Equal(byHand.GetJoinHashCode(), declared.GetJoinHashCode());
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void IndexesCanBeDeclaredByTypeOrByName()
    {
        JoinDefinition byType = Declare(j =>
        {
            j.Outer<Conversations_ByContext>();
            j.Inner<Contexts_Search>();
            j.Key("ContextId");
        });

        Assert.Equal("Conversations/ByContext", byType.OuterIndex);
        Assert.Equal("Contexts/Search", byType.InnerIndex);

        JoinDefinition byName = Declare(j =>
        {
            j.Outer("Conversations/ByContext");
            j.Inner("Contexts/Search");
            j.Key(x => x.ContextId);
        });

        Assert.True(JoinDefinition.IsSameJoin(byType, byName));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ExpressionsAndStringsMixInTheOrderOfDeclaration()
    {
        JoinDefinition definition = Declare(j =>
        {
            Basics(j);
            j.Field("Zeta");
            j.Field(x => x.Country);
            j.Field("Alpha", JoinFieldKind.Text);
            j.Field(x => x.Weight, JoinFieldKind.Long);
            j.Grouping("Mode", "fast");
            j.Grouping(x => x.IsActive, false);
            j.Grouping(x => x.Rank, 7);
            j.Grouping(x => x.Weight, 7L);
        });

        Assert.Equal(new[] { "Zeta", "Country", "Alpha", "Weight" }, definition.Fields.Select(x => x.Field));
        Assert.Equal(new[] { "Mode=fast", "IsActive=false", "Rank=7", "Weight=7" }, definition.Groupings.Select(x => $"{x.Field}={x.Value}"));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void FieldsOfAClassAndNullableTypesAreSupported()
    {
        JoinDefinition definition = Declare(j =>
        {
            Basics(j);
            j.Field(x => x.Field);
            j.Field(x => x.Tags, JoinFieldKind.Text);
            j.Field(x => x.Weight, JoinFieldKind.Long);
        });

        Assert.Equal(new[] { "Field", "Tags", "Weight" }, definition.Fields.Select(x => x.Field));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheJoinIsComparedByEveryPartOfTheDefinition()
    {
        JoinDefinition baseline = new Conversations_WithContexts().CreateIndexDefinition().Join;

        // what does not change the join
        JoinDefinition sameWithOtherCase = baseline.Clone();
        sameWithOtherCase.OuterIndex = "CONVERSATIONS/BYCONTEXT";
        sameWithOtherCase.InnerIndex = "contexts/search";
        Assert.True(JoinDefinition.IsSameJoin(baseline, sameWithOtherCase));
        Assert.Equal(baseline.GetJoinHashCode(), sameWithOtherCase.GetJoinHashCode());

        var changes = new Dictionary<string, Action<JoinDefinition>>
        {
            ["outer index"] = d => d.OuterIndex = "Other/Index",
            ["inner index"] = d => d.InnerIndex = "Other/Index",
            ["outer field"] = d => d.OuterField = "contextid",
            ["field name"] = d => d.Fields[0].Field = "Other",
            ["field kind"] = d => d.Fields[1].Kind = JoinFieldKind.Text,
            ["field removed"] = d => d.Fields.RemoveAt(3),
            ["field order"] = d => (d.Fields[0], d.Fields[1]) = (d.Fields[1], d.Fields[0]),
            ["field added"] = d => d.AddField("Extra"),
            ["grouping value"] = d => d.Groupings[0].Value = "false",
            ["grouping field"] = d => d.Groupings[0].Field = "Other",
            ["grouping removed"] = d => d.Groupings.Clear(),
            ["grouping added"] = d => d.AddGrouping("Extra", "x")
        };

        foreach ((string what, Action<JoinDefinition> change) in changes)
        {
            JoinDefinition changed = baseline.Clone();
            change(changed);

            Assert.False(JoinDefinition.IsSameJoin(baseline, changed), what);
            Assert.False(JoinDefinition.IsSameJoin(changed, baseline), what);
        }

        Assert.False(JoinDefinition.IsSameJoin(baseline, null));
        Assert.True(JoinDefinition.IsSameJoin(baseline, baseline));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void CloneIsIndependentOfTheOriginal()
    {
        JoinDefinition original = new Conversations_WithContexts().CreateIndexDefinition().Join;
        JoinDefinition copy = original.Clone();

        copy.Fields[0].Field = "Changed";
        copy.Groupings[0].Value = "false";
        copy.Fields.Add(new JoinField { Field = "More" });

        Assert.Equal("Country", original.Fields[0].Field);
        Assert.Equal("true", original.Groupings[0].Value);
        Assert.Equal(4, original.Fields.Count);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void PropertyNameConverterOfTheConventionsIsApplied()
    {
        var conventions = new DocumentConventions { PropertyNameConverter = member => char.ToLowerInvariant(member.Name[0]) + member.Name.Substring(1) };

        JoinDefinition definition = Declare(j =>
        {
            Basics(j);
            j.Field(x => x.Country);
            j.Grouping(x => x.IsActive, true);
        }, conventions);

        Assert.Equal("contextId", definition.OuterField);
        Assert.Equal("country", Assert.Single(definition.Fields).Field);
        Assert.Equal("isActive", Assert.Single(definition.Groupings).Field);

        // without the convention the names are the names of the members
        Assert.Equal("ContextId", Declare(j => { Basics(j); j.Field(x => x.Country); }).OuterField);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ExpressionsThatCannotBeTranslatedAreRefusedWithTheReason()
    {
        var cases = new (Expression<Func<ProbeInner, object>> Expression, string Reason)[]
        {
            (x => x.Address.City, "nested path"),
            (x => x.Country.ToUpper(), "calls the method 'ToUpper'"),
            (x => x.Rank + 1, "computes a value"),
            (x => x.Tags[0], "uses an indexer"),
            (x => "Country", "constant"),
            (x => x, "the entry itself"),
            (x => x.Title ?? x.Name, "computes a value")
        };

        foreach ((Expression<Func<ProbeInner, object>> expression, string reason) in cases)
        {
            JoinValidationException field = Refused(j => { Basics(j); j.Field(expression); });
            Assert.Contains(field.Errors, x => x.Code == JoinValidationCode.UnsupportedExpression);
            Assert.Contains(reason, field.Message);
            Assert.Contains(expression.ToString(), field.Message);
            Assert.Contains("cannot declare a field", field.Message);

            JoinValidationException grouping = Refused(j => { Basics(j); j.Grouping(expression, "x"); });
            Assert.Contains("cannot declare a grouping", grouping.Message);
        }

        JoinValidationException key = Refused(j => { j.Outer("O"); j.Inner("I"); j.Key(x => x.ContextId.Trim()); }, code: JoinValidationCode.UnsupportedExpression);
        Assert.Contains("the join key", key.Message);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void KindMustFitTheTypeOfTheProperty()
    {
        Assert.Contains("a Long field is an integer", Refused(j => { Basics(j); j.Field(x => x.Country, JoinFieldKind.Long); }, code: JoinValidationCode.FieldKindMismatch).Message);
        Assert.Contains("a Text field is a string", Refused(j => { Basics(j); j.Field(x => x.Rank, JoinFieldKind.Text); }, code: JoinValidationCode.FieldKindMismatch).Message);
        Assert.Contains("a Text field is a string", Refused(j => { Basics(j); j.Field(x => x.Created, JoinFieldKind.Text); }, code: JoinValidationCode.FieldKindMismatch).Message);
        Assert.Contains("one term per entry", Refused(j => { Basics(j); j.Field(x => x.Tags); }, code: JoinValidationCode.FieldKindMismatch).Message);
        Assert.Contains("one term per entry", Refused(j => { Basics(j); j.Grouping(x => x.Tags, "a"); }, code: JoinValidationCode.FieldKindMismatch).Message);
        Assert.Contains("integer", Refused(j => { Basics(j); j.Field(x => x.IsActive, JoinFieldKind.Long); }, code: JoinValidationCode.FieldKindMismatch).Message);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void KeyIsAStringOrACollectionOfStrings()
    {
        Assert.Contains("a string", Refused(j => { j.Outer("O"); j.Inner("I"); j.Key(x => x.Number); }, code: JoinValidationCode.OuterFieldNotSupported).Message);
        Assert.Contains("a string", Refused(j => { j.Outer("O"); j.Inner("I"); j.Key(x => x.Numbers); }, code: JoinValidationCode.OuterFieldNotSupported).Message);

        Assert.Equal("Ids", Declare(j => { j.Outer("O"); j.Inner("I"); j.Key(x => x.Ids); }).OuterField);
        Assert.Equal("IdList", Declare(j => { j.Outer("O"); j.Inner("I"); j.Key(x => x.IdList); }).OuterField);
        Assert.Contains("declared twice", Refused(j => { Basics(j); j.Key("ContextId"); }, code: JoinValidationCode.InvalidDefinition).Message);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void GroupingValuesAreTheTermsTheIndexWrites()
    {
        Assert.Contains("a DateTime", Refused(j => { Basics(j); j.Grouping(x => x.Created, DateTime.UtcNow); }, code: JoinValidationCode.UnsupportedExpression).Message);
        Assert.Contains("a decimal", Refused(j => { Basics(j); j.Grouping(x => x.Rank, 1.5m); }, code: JoinValidationCode.UnsupportedExpression).Message.Replace("Decimal", "decimal"));
        Assert.Contains("has no value", Refused(j => { Basics(j); j.Grouping(x => x.Country, null); }, code: JoinValidationCode.UnsupportedExpression).Message);
        Assert.Contains("DayOfWeek", Refused(j => { Basics(j); j.Grouping(x => x.Country, DayOfWeek.Monday); }, code: JoinValidationCode.UnsupportedExpression).Message);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void RulesThatNeedNoServerAreCheckedByTheClient()
    {
        Refused(j => { j.Inner("I"); j.Key("K"); }, code: JoinValidationCode.InvalidDefinition);
        Refused(j => { Basics(j); for (int i = 0; i <= JoinLimits.MaxFields; i++) j.Field("F" + i); }, code: JoinValidationCode.LimitExceeded);
        Refused(j => { Basics(j); for (int i = 0; i <= JoinLimits.MaxGroupings; i++) j.Grouping("G" + i, "v"); }, code: JoinValidationCode.LimitExceeded);
        Refused(j => { Basics(j); j.Field("A"); j.Field(x => x.Country); j.Field("Country"); }, code: JoinValidationCode.InvalidDefinition);
        Refused(j => { Basics(j); j.Grouping("A", "x"); j.Grouping("A", "x"); }, code: JoinValidationCode.InvalidDefinition);
        Refused(j => { Basics(j); j.Grouping("A", new string('x', JoinLimits.MaxTermLength + 1)); }, code: JoinValidationCode.LimitExceeded);

        JoinValidationException several = Refused(j => { j.Field("A"); j.Field("A"); });
        Assert.True(several.Errors.Count >= 3, several.Message);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AJoinWithoutFieldsIsAllowed()
    {
        JoinDefinition definition = Declare(Basics);

        Assert.Empty(definition.Fields);
        Assert.Empty(definition.ValidateShape());
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void JoinOnTwoKeysDeclaresAJoinOnAFieldOfTheInnerIndex()
    {
        JoinDefinition byExpression = Declare(j =>
        {
            j.Outer("Outer/Index");
            j.Inner("Inner/Index");
            j.Join(o => o.ContextId, i => i.Country);
        });

        JoinDefinition byName = Declare(j =>
        {
            j.Outer("Outer/Index");
            j.Inner("Inner/Index");
            j.Join("ContextId", "Country");
        });

        Assert.Equal("ContextId", byExpression.OuterField);
        Assert.Equal("Country", byExpression.InnerField);
        Assert.True(JoinDefinition.IsSameJoin(byExpression, byName));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheKeysOfAJoinOnAFieldHoldOneStringEach()
    {
        Refused(j =>
        {
            j.Outer("O");
            j.Inner("I");
            j.Join(o => o.ContextId, i => i.Rank);
        }, JoinValidationCode.InnerFieldNotSupported);

        Refused(j =>
        {
            j.Outer("O");
            j.Inner("I");
            j.Join(o => o.ContextId, i => i.Tags);
        }, JoinValidationCode.InnerFieldNotSupported);

        Refused(j =>
        {
            j.Outer("O");
            j.Inner("I");
            j.Join(o => o.Ids, i => i.Country);
        }, JoinValidationCode.OuterFieldNotSupported);

        Refused(j =>
        {
            j.Outer("O");
            j.Inner("I");
            j.Join(o => o.ContextId, i => i.Country);
            j.Join("ContextId", "Name");
        }, JoinValidationCode.InvalidDefinition);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void JoinOnIdDeclaresAJoinOfTheIdOfTheOuterDocumentWithAFieldOfTheInnerIndex()
    {
        JoinDefinition byExpression = Declare(j =>
        {
            j.Outer("Outer/Index");
            j.Inner("Inner/Index");
            j.JoinById(i => i.Country);
        });

        JoinDefinition byName = Declare(j =>
        {
            j.Outer("Outer/Index");
            j.Inner("Inner/Index");
            j.JoinById("Country");
        });

        JoinDefinition byKeyNames = Declare(j =>
        {
            j.Outer("Outer/Index");
            j.Inner("Inner/Index");
            j.Join("id()", "Country");
        });

        Assert.Equal("id()", byExpression.OuterField);
        Assert.Equal("Country", byExpression.InnerField);
        Assert.True(byExpression.JoinsOnOuterId);
        Assert.True(JoinDefinition.IsSameJoin(byExpression, byName));
        Assert.True(JoinDefinition.IsSameJoin(byExpression, byKeyNames));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void JoinOnIdRefusesAnInnerKeyThatIsNotAString()
    {
        Refused(j =>
        {
            j.Outer("O");
            j.Inner("I");
            j.JoinById(i => i.Rank);
        }, JoinValidationCode.InnerFieldNotSupported);

        Refused(j =>
        {
            j.Outer("O");
            j.Inner("I");
            j.JoinById(i => i.Country);
            j.Key(x => x.ContextId);
        });
    }
}
