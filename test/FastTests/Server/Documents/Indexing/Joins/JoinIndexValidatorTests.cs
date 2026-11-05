using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins;

public class JoinIndexValidatorTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    private static JoinIndexFacts Outer(Action<JoinIndexFacts> change = null)
    {
        var facts = new JoinIndexFacts
        {
            Name = "Conversations/ByContext",
            Collections = ["Conversations"],
            Type = IndexType.Map,
            SourceType = IndexSourceType.Documents,
            SearchEngine = SearchEngineType.Corax,
            MaxNumberOfOutputsPerDocument = 1,
            Fields = { ["ContextId"] = new JoinIndexFieldFacts { Indexing = FieldIndexing.Default } }
        };
        change?.Invoke(facts);
        return facts;
    }

    private static JoinIndexFacts Inner(Action<JoinIndexFacts> change = null)
    {
        var facts = new JoinIndexFacts
        {
            Name = "Contexts/Search",
            Collections = ["Contexts"],
            Type = IndexType.Map,
            SourceType = IndexSourceType.Documents,
            SearchEngine = SearchEngineType.Corax,
            MaxNumberOfOutputsPerDocument = 1,
            Fields =
            {
                ["Country"] = new JoinIndexFieldFacts { Indexing = FieldIndexing.Default },
                ["Name"] = new JoinIndexFieldFacts { Indexing = FieldIndexing.Exact },
                ["Rank"] = new JoinIndexFieldFacts { Indexing = FieldIndexing.Default },
                ["Title"] = new JoinIndexFieldFacts { Indexing = FieldIndexing.Search },
                ["IsActive"] = new JoinIndexFieldFacts { Indexing = FieldIndexing.Default }
            }
        };
        change?.Invoke(facts);
        return facts;
    }

    private static JoinDefinition Definition()
    {
        return new JoinDefinition { OuterIndex = "Conversations/ByContext", OuterField = "ContextId", InnerIndex = "Contexts/Search" }
            .AddField("Country")
            .AddField("Name")
            .AddField("Rank", JoinFieldKind.Long)
            .AddField("Title", JoinFieldKind.Text)
            .AddGrouping("IsActive", "true");
    }

    private static List<JoinValidationError> Validate(JoinDefinition definition, JoinIndexFacts outer = null, JoinIndexFacts inner = null, bool sharded = false)
    {
        outer ??= Outer();
        inner ??= Inner();
        var indexes = new Dictionary<string, JoinIndexFacts>(StringComparer.OrdinalIgnoreCase) { [outer.Name] = outer, [inner.Name] = inner };
        return new JoinIndexValidator(name => indexes.GetValueOrDefault(name), sharded).Validate(definition);
    }

    private static void AssertSingle(List<JoinValidationError> errors, JoinValidationCode code, string contains = null)
    {
        JoinValidationError error = Assert.Single(errors);
        Assert.Equal(code, error.Code);
        if (contains != null)
            Assert.Contains(contains, error.Message);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ADefinitionOfTwoCoraxMapIndexesIsValid()
    {
        Assert.Empty(Validate(Definition()));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AMissingIndexIsReportedWithItsName()
    {
        var validator = new JoinIndexValidator(_ => null, isShardedDatabase: false);
        List<JoinValidationError> errors = validator.Validate(Definition());

        Assert.Equal(2, errors.Count);
        Assert.All(errors, x => Assert.Equal(JoinValidationCode.IndexMissing, x.Code));
        Assert.Contains(errors, x => x.Message.Contains("'Conversations/ByContext'"));
        Assert.Contains(errors, x => x.Message.Contains("'Contexts/Search'"));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheShapeIsCheckedBeforeTheIndexesAreLookedUp()
    {
        JoinDefinition definition = Definition();
        definition.OuterField = null;

        AssertSingle(Validate(definition), JoinValidationCode.InvalidDefinition, "OuterField");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AShardedDatabaseIsRefused()
    {
        AssertSingle(Validate(Definition(), sharded: true), JoinValidationCode.ShardedDatabase);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AnIndexJoinedWithItselfIsRefusedWhateverTheCase()
    {
        JoinDefinition definition = Definition();
        definition.InnerIndex = "conversations/bycontext";

        AssertSingle(Validate(definition), JoinValidationCode.SameIndex);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ASideBoundByCollectionIsNotSupportedByAJoinIndexYet()
    {
        JoinDefinition definition = Definition();
        definition.OuterCollection = "Conversations";
        definition.OuterIndex = "Auto/Conversations/ByContextId";

        AssertSingle(Validate(definition), JoinValidationCode.InvalidDefinition, "bound by collection");
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(SearchEngineType.Lucene, JoinValidationCode.UnsupportedSearchEngine)]
    [InlineData(SearchEngineType.None, JoinValidationCode.UnsupportedSearchEngine)]
    public void AnIndexThatIsNotCoraxIsRefused(SearchEngineType engine, JoinValidationCode code)
    {
        AssertSingle(Validate(Definition(), outer: Outer(x => x.SearchEngine = engine)), code, "'Conversations/ByContext'");
        AssertSingle(Validate(Definition(), inner: Inner(x => x.SearchEngine = engine)), code, "'Contexts/Search'");
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(IndexType.MapReduce)]
    [InlineData(IndexType.JavaScriptMapReduce)]
    [InlineData(IndexType.AutoMapReduce)]
    [InlineData(IndexType.Join)]
    public void OnlyMapIndexesCanBeJoined(IndexType type)
    {
        AssertSingle(Validate(Definition(), outer: Outer(x => x.Type = type)), JoinValidationCode.UnsupportedIndexKind);
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(IndexType.AutoMap)]
    [InlineData(IndexType.JavaScriptMap)]
    public void AutoMapAndJavaScriptMapIndexesCanBeJoined(IndexType type)
    {
        Assert.Empty(Validate(Definition(), outer: Outer(x => x.Type = type), inner: Inner(x => x.Type = type)));
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(IndexSourceType.Counters)]
    [InlineData(IndexSourceType.TimeSeries)]
    public void OnlyIndexesOfDocumentsCanBeJoined(IndexSourceType source)
    {
        AssertSingle(Validate(Definition(), inner: Inner(x => x.SourceType = source)), JoinValidationCode.UnsupportedSource);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void EntriesThatAreNotAFunctionOfTheirOwnDocumentAreRefused()
    {
        AssertSingle(Validate(Definition(), outer: Outer(x => x.HasReferencedCollections = true)), JoinValidationCode.UnsupportedSource, "LoadDocument");
        AssertSingle(Validate(Definition(), inner: Inner(x => x.HasCompareExchange = true)), JoinValidationCode.UnsupportedSource, "compare exchange");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void BoostedFieldsOnlyMatterOnTheInnerSide()
    {
        Assert.Empty(Validate(Definition(), outer: Outer(x => x.HasBoostedFields = true)));
        AssertSingle(Validate(Definition(), inner: Inner(x => x.HasBoostedFields = true)), JoinValidationCode.BoostedFields);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void OneEntryPerDocumentIsRequired()
    {
        AssertSingle(Validate(Definition(), outer: Outer(x => x.MaxNumberOfOutputsPerDocument = 2)), JoinValidationCode.MultipleOutputs, "2 entries");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheJoinKeyMustBeIndexedTheWayAnIdIsCompared()
    {
        Assert.Empty(Validate(Definition(), outer: Outer(x => x.Fields["ContextId"] = new JoinIndexFieldFacts { Indexing = FieldIndexing.Key })));
        AssertSingle(Validate(Definition(), outer: Outer(x => x.Fields["ContextId"] = new JoinIndexFieldFacts { Indexing = FieldIndexing.Exact })), JoinValidationCode.OuterFieldNotSupported);
        AssertSingle(Validate(Definition(), outer: Outer(x => x.Fields["ContextId"] = new JoinIndexFieldFacts { Indexing = FieldIndexing.Default, HasCustomAnalyzer = true, Analyzer = "A" })),
            JoinValidationCode.OuterFieldNotSupported, "'A'");
        AssertSingle(Validate(Definition(), outer: Outer(x => x.Fields.Remove("ContextId"))), JoinValidationCode.FieldNotIndexed);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ADeclaredFieldMustBeIndexed()
    {
        AssertSingle(Validate(Definition(), inner: Inner(x => x.Fields.Remove("Country"))), JoinValidationCode.FieldNotIndexed, "'Country'");
        AssertSingle(Validate(Definition(), inner: Inner(x => x.Fields["Country"] = new JoinIndexFieldFacts { Indexing = FieldIndexing.No })), JoinValidationCode.FieldNotIndexed, "not indexed");
        AssertSingle(Validate(Definition(), inner: Inner(x => { x.Fields.Remove("Country"); x.HasDynamicFields = true; })), JoinValidationCode.FieldNotIndexed, "dynamically");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheKindOfAFieldMustFitHowItIsIndexed()
    {
        // an analyzed field cannot be Exact or Long, a field of one term cannot be Text
        AssertSingle(Validate(Definition(), inner: Inner(x => x.Fields["Country"] = new JoinIndexFieldFacts { Indexing = FieldIndexing.Search })), JoinValidationCode.FieldKindMismatch, "declare it as Text");
        AssertSingle(Validate(Definition(), inner: Inner(x => x.Fields["Title"] = new JoinIndexFieldFacts { Indexing = FieldIndexing.Exact })), JoinValidationCode.FieldKindMismatch, "declare it as Exact");
        AssertSingle(Validate(Definition(), inner: Inner(x => x.Fields["Country"] = new JoinIndexFieldFacts { Indexing = FieldIndexing.Default, HasCustomAnalyzer = true, Analyzer = "Custom" })),
            JoinValidationCode.FieldKindMismatch, "'Custom'");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AGroupingNeedsAFieldOfOneTermPerEntry()
    {
        AssertSingle(Validate(Definition(), inner: Inner(x => x.Fields["IsActive"] = new JoinIndexFieldFacts { Indexing = FieldIndexing.Search })), JoinValidationCode.FieldKindMismatch, "grouping");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void SeveralErrorsAreReportedTogether()
    {
        List<JoinValidationError> errors = Validate(Definition(), outer: Outer(x => x.SearchEngine = SearchEngineType.Lucene), inner: Inner(x => x.Fields.Remove("Country")));

        Assert.Equal(new[] { JoinValidationCode.UnsupportedSearchEngine, JoinValidationCode.FieldNotIndexed }, errors.Select(x => x.Code));
    }

    private static JoinDefinition FieldDefinition()
    {
        JoinDefinition definition = Definition();
        definition.InnerField = "Code";
        return definition;
    }

    private static JoinIndexFacts InnerWithCode(FieldIndexing indexing = FieldIndexing.Default, string analyzer = null)
    {
        return Inner(x => x.Fields["Code"] = new JoinIndexFieldFacts { Indexing = indexing, HasCustomAnalyzer = analyzer != null, Analyzer = analyzer });
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AJoinOnAFieldNeedsBothKeysIndexedTheSameWayWithoutAnAnalyzer()
    {
        Assert.Empty(Validate(FieldDefinition(), inner: InnerWithCode()));
        Assert.Empty(Validate(FieldDefinition(), outer: Outer(x => x.Fields["ContextId"].Indexing = FieldIndexing.Exact), inner: InnerWithCode(FieldIndexing.Exact)));
        Assert.Empty(Validate(FieldDefinition(), outer: Outer(x => x.Fields["ContextId"].Indexing = FieldIndexing.Key), inner: InnerWithCode(FieldIndexing.Key)));
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(FieldIndexing.Exact)]
    [InlineData(FieldIndexing.Key)]
    public void AJoinOnAFieldRefusesKeysThatAreIndexedDifferently(FieldIndexing inner)
    {
        // the outer key is indexed with the default indexing
        AssertSingle(Validate(FieldDefinition(), inner: InnerWithCode(inner)), JoinValidationCode.InnerFieldNotSupported, "the same way");
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(FieldIndexing.Search)]
    [InlineData(FieldIndexing.No)]
    public void AJoinOnAFieldRefusesAKeyThatIsAnalyzedOrNotIndexed(FieldIndexing indexing)
    {
        List<JoinValidationError> errors = Validate(FieldDefinition(), inner: InnerWithCode(indexing));
        Assert.Contains(errors, x => x.Code is JoinValidationCode.InnerFieldNotSupported or JoinValidationCode.FieldNotIndexed);

        errors = Validate(FieldDefinition(), outer: Outer(x => x.Fields["ContextId"].Indexing = indexing), inner: InnerWithCode());
        Assert.Contains(errors, x => x.Code is JoinValidationCode.OuterFieldNotSupported or JoinValidationCode.FieldNotIndexed);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AJoinOnAFieldRefusesAKeyWithACustomAnalyzer()
    {
        AssertSingle(Validate(FieldDefinition(), inner: InnerWithCode(analyzer: "StandardAnalyzer")), JoinValidationCode.InnerFieldNotSupported, "StandardAnalyzer");
        AssertSingle(Validate(FieldDefinition(), outer: Outer(x => x.Fields["ContextId"].HasCustomAnalyzer = true), inner: InnerWithCode()), JoinValidationCode.OuterFieldNotSupported);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AMissingInnerKeyFieldIsNotIndexed()
    {
        AssertSingle(Validate(FieldDefinition()), JoinValidationCode.FieldNotIndexed, "'Code'");
    }

    private static JoinDefinition IdOfOuterDefinition()
    {
        JoinDefinition definition = Definition();
        definition.OuterField = "id()";
        definition.InnerField = "Code";
        return definition;
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AJoinOfTheIdOfTheOuterDocumentNeedsNoFieldOfTheOuterIndex()
    {
        // the outer index has no field for the key: the id of the document is the key
        Assert.Empty(Validate(IdOfOuterDefinition(), outer: Outer(x => x.Fields.Clear()), inner: InnerWithCode()));
        Assert.Empty(Validate(IdOfOuterDefinition(), inner: InnerWithCode()));
        Assert.Empty(Validate(IdOfOuterDefinition(), inner: InnerWithCode(FieldIndexing.Key)));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AJoinOfTheIdOfTheOuterDocumentRefusesAnInnerKeyThatKeepsTheCase()
    {
        // a join on id() lowercases the keys of such a field, the terms of the join index are the terms of the index
        AssertSingle(Validate(IdOfOuterDefinition(), inner: InnerWithCode(FieldIndexing.Exact)), JoinValidationCode.InnerFieldNotSupported, "lowercase");
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(FieldIndexing.Search)]
    [InlineData(FieldIndexing.No)]
    public void AJoinOfTheIdOfTheOuterDocumentRefusesAnAnalyzedOrUnindexedInnerKey(FieldIndexing indexing)
    {
        List<JoinValidationError> errors = Validate(IdOfOuterDefinition(), inner: InnerWithCode(indexing));
        Assert.Contains(errors, x => x.Code is JoinValidationCode.InnerFieldNotSupported or JoinValidationCode.FieldNotIndexed);
        Assert.Empty(Validate(IdOfOuterDefinition(), inner: InnerWithCode(FieldIndexing.Default)));
        AssertSingle(Validate(IdOfOuterDefinition(), inner: InnerWithCode(analyzer: "StandardAnalyzer")), JoinValidationCode.InnerFieldNotSupported, "StandardAnalyzer");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AMissingInnerKeyOfAJoinOfTheIdIsNotIndexed()
    {
        AssertSingle(Validate(IdOfOuterDefinition()), JoinValidationCode.FieldNotIndexed, "'Code'");
    }
}
