using Raven.Client.Documents.Indexes;
using System;
using System.Text;
using FastTests;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Serving;

public class JoinPredicateAnalyzerTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    private static JoinDefinition Definition()
    {
        return new JoinDefinition { OuterIndex = "Conversations/ByContext", OuterField = "ContextId", InnerIndex = "Contexts/Search" }
            .AddField("Country")
            .AddField("Name")
            .AddField("Rank", JoinFieldKind.Long)
            .AddField("Title", JoinFieldKind.Text)
            .AddGrouping("IsActive", "true");
    }

    // the joined index lowercases what it does not index as Exact; a text that it cannot make one term of (the analyzer of the field splits it or drops it) is a split
    private static EncodedTerm Encode(string field, string text) => field == "Title" && text is "the" or "of the"
        ? EncodedTerm.Split
        : new EncodedTerm(Encoding.UTF8.GetBytes(field == "Name" ? text : text.ToLowerInvariant()));

    private static bool Analyze(string sub, out JoinPredicate predicate, out string fallback, BlittableJsonReaderObject parameters = null, bool scoresMayMatter = true)
    {
        string rql = $"from index 'Conversations/ByContext' as m join (from index 'Contexts/Search'{sub}) as c on m.ContextId = id(c)";
        var metadata = new QueryMetadata(rql, parameters, 1);
        return JoinPredicateAnalyzer.TryAnalyze(metadata.Query.Joins[0], Definition(), parameters, Encode, scoresMayMatter, out predicate, out fallback);
    }

    [RavenTheory(RavenTestCategory.Querying)]
    [InlineData(" where Title = 'the'")]
    [InlineData(" where Title = 'of the'")]
    [InlineData(" where Title = 'ab' or Title = 'the'")]
    public void ATextThatTheAnalyzerOfTheFieldSplitsIsNamedAndNotAnException(string sub)
    {
        Assert.False(Analyze(sub, out JoinPredicate predicate, out string fallback, scoresMayMatter: false));
        Assert.Null(predicate);
        Assert.Equal(JoinFallbacks.AnalyzerSplitsValue, fallback);
        Assert.Equal("KindNotServed:AnalyzerSplitsValue", fallback);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void SubQueryWithoutConditionIsEveryJoinedEntry()
    {
        Assert.True(Analyze("", out JoinPredicate predicate, out string fallback));
        Assert.Equal(JoinPredicateKind.Any, predicate.Kind);
        Assert.Null(fallback);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void EqualityOnADeclaredFieldIsATerm()
    {
        Assert.True(Analyze(" where Country = 'NL'", out JoinPredicate predicate, out _));
        Assert.Equal(JoinPredicateKind.Term, predicate.Kind);
        Assert.Equal(0, predicate.Field);
        Assert.Equal("nl", Encoding.UTF8.GetString(predicate.Term));

        Assert.True(Analyze(" where Name = 'Alpha'", out predicate, out _));
        Assert.Equal(1, predicate.Field);
        Assert.Equal("Alpha", Encoding.UTF8.GetString(predicate.Term));

        Assert.True(Analyze(" where Rank = 10", out predicate, out _));
        Assert.Equal(2, predicate.Field);
        Assert.Equal("10", Encoding.UTF8.GetString(predicate.Term));
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void EqualityOnTheValueOfAGroupingIsTheGrouping()
    {
        Assert.True(Analyze(" where IsActive = true", out JoinPredicate predicate, out _));
        Assert.Equal(JoinPredicateKind.Grouping, predicate.Kind);
        Assert.Equal(0, predicate.Grouping);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void ConditionsThatAreAndedAndNegatedAreServed()
    {
        Assert.True(Analyze(" where Country = 'nl' and IsActive = true and Rank = 3", out JoinPredicate predicate, out _));
        Assert.Equal(JoinPredicateKind.And, predicate.Kind);

        Assert.True(Analyze(" where Country != 'nl'", out predicate, out _));
        Assert.Equal(JoinPredicateKind.Not, predicate.Kind);
        Assert.Equal(JoinPredicateKind.Term, predicate.Left.Kind);

        Assert.True(Analyze(" where not (Country = 'nl' and IsActive = true)", out predicate, out _));
        Assert.Equal(JoinPredicateKind.Not, predicate.Kind);
        Assert.Equal(JoinPredicateKind.And, predicate.Left.Kind);

        Assert.True(Analyze(" where IsActive != true", out predicate, out _));
        Assert.Equal(JoinPredicateKind.Not, predicate.Kind);
        Assert.Equal(JoinPredicateKind.Grouping, predicate.Left.Kind);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void ParametersAreReadLikeLiterals()
    {
        using var context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject parameters = context.ReadObject(new DynamicJsonValue { ["country"] = "NL", ["rank"] = 7L, ["active"] = true, ["nothing"] = null, ["decimal"] = 1.5 }, "parameters");

        Assert.True(Analyze(" where Country = $country and Rank = $rank and IsActive = $active", out JoinPredicate predicate, out string fallback, parameters), fallback);
        Assert.Equal(JoinPredicateKind.And, predicate.Kind);

        Assert.False(Analyze(" where Country = $nothing", out _, out fallback, parameters));
        Assert.Equal("KindNotServed:ValueType", fallback);
        Assert.False(Analyze(" where Rank = $decimal", out _, out fallback, parameters));
        Assert.Equal("KindNotServed:ValueType", fallback);
    }

    [RavenTheory(RavenTestCategory.Querying)]
    [InlineData(" where Country = 'nl' or Name = 'a'", "Or")]
    [InlineData(" where Rank > 5", "Range")]
    [InlineData(" where Rank between 1 and 5", "Between")]
    [InlineData(" where Country in ('nl', 'de')", "In")]
    [InlineData(" where search(Title, 'fox')", "Method:search")]
    [InlineData(" where startsWith(Name, 'A')", "Method:startsWith")]
    [InlineData(" where exists(Country)", "Method:exists")]
    [InlineData(" where Title = 'fox'", "Text")]
    [InlineData(" where Code = 'x'", "UndeclaredField:Code")]
    [InlineData(" where Rank = '5'", "ValueType")]
    [InlineData(" where Country = 5", "ValueType")]
    [InlineData(" where Country = null", "ValueType")]
    [InlineData(" where Country = 'nl' or IsActive = true", "Or")]
    [InlineData(" where not (Country = 'nl' or Name = 'a')", "Or")]
    public void EverythingElseIsReadByTheServer(string sub, string detail)
    {
        Assert.False(Analyze(sub, out JoinPredicate predicate, out string fallback));
        Assert.Null(predicate);
        Assert.Equal(JoinFallbacks.KindNotServed(detail), fallback);
    }

    [RavenTheory(RavenTestCategory.Querying)]
    [InlineData(" where Country = 'nl' or Name = 'a'", "Or")]
    [InlineData(" where Country in ('nl', 'de', 'c1')", "Or")]
    [InlineData(" where Rank > 5", "Range")]
    [InlineData(" where Rank <= 5", "Range")]
    [InlineData(" where Rank between 1 and 5", "Range")]
    [InlineData(" where Name >= 'Alpha'", "Range")]
    [InlineData(" where Name between 'A' and 'B'", "Range")]
    [InlineData(" where startsWith(Name, 'Al')", "Prefix")]
    [InlineData(" where search(Title, 'fox')", "Term")]
    [InlineData(" where Title = 'fox'", "Term")]
    [InlineData(" where Rank > 5 and Rank < 9 and Country = 'nl'", "And")]
    [InlineData(" where not (Country = 'nl' or Name = 'a')", "Not")]
    public void WhenNoScoreIsUsedTheKindsWithDifferentScoresAreServedToo(string sub, string kind)
    {
        Assert.False(Analyze(sub, out _, out _));
        Assert.True(Analyze(sub, out JoinPredicate predicate, out string fallback, scoresMayMatter: false), fallback);
        Assert.Equal(kind, predicate.Kind.ToString());
        Assert.True(predicate.ScoresDiffer);
    }

    [RavenTheory(RavenTestCategory.Querying)]
    [InlineData(" where search(Title, 'red fox')", "Method:search")]
    [InlineData(" where Title = 'red fox'", "Text")]
    [InlineData(" where startsWith(Title, 'r')", "Method:startsWith")]
    [InlineData(" where startsWith(Rank, 'r')", "Method:startsWith")]
    [InlineData(" where Rank > 5.5", "ValueType")]
    [InlineData(" where Rank > 'a'", "ValueType")]
    [InlineData(" where Title > 'a'", "UndeclaredField:Title")]
    [InlineData(" where Code > 1", "UndeclaredField:Code")]
    [InlineData(" where Country in ('nl') and IsActive > 1", "UndeclaredField:IsActive")]
    [InlineData(" where Country = 'nl' or exists(Name)", "Method:exists")]
    public void WhenNoScoreIsUsedWhatCannotBeAnsweredIsStillReadByTheServer(string sub, string detail)
    {
        Assert.False(Analyze(sub, out JoinPredicate predicate, out string fallback, scoresMayMatter: false));
        Assert.Null(predicate);
        Assert.Equal(JoinFallbacks.KindNotServed(detail), fallback);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void EqualityAndGroupingsKeepTheirUniformScores()
    {
        Assert.True(Analyze(" where Country = 'nl' and IsActive = true", out JoinPredicate predicate, out _));
        Assert.False(predicate.ScoresDiffer);
        Assert.True(Analyze(" where Country != 'nl'", out predicate, out _));
        Assert.False(predicate.ScoresDiffer);
    }
}
