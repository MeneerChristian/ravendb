using System.Linq;
using Raven.Client.Exceptions;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Queries.Parser;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

public class JoinParserLanguageTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    private static Query Parse(string q)
    {
        var parser = new QueryParser();
        parser.Init(q);
        return parser.Parse();
    }

    private static QueryMetadata Metadata(string q, QueryType type = QueryType.Select) => new(q, null, 1, queryType: type);

    [RavenFact(RavenTestCategory.Querying)]
    public void KeywordsAreCaseInsensitive()
    {
        var query = Parse("from index 'Games' as g join (from index 'Users' where Name = 'a') as u on g.OwnerId = id(u) where g.Name = 'z' order by score() limit 5");

        Assert.Equal("u", Assert.Single(query.Joins).Alias.Value);
        Assert.NotNull(query.Limit);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void AliasOfTheMainDocumentCanBeWrittenWithoutAs()
    {
        var query = Parse("FROM INDEX 'Games' g JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = id(u)");

        Assert.Equal("g", query.From.Alias.Value.Value);
        Assert.Single(query.Joins);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void JoinIsFollowedByEveryOtherClauseOfTheOuterQuery()
    {
        var query = Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users' WHERE Name = 'a') AS u ON g.OwnerId = id(u) " +
                          "WHERE g.Name = 'z' ORDER BY score(), g.Name DESC LOAD g.PublisherId AS p SELECT g.Name, p.Name AS Publisher, u.Name AS Owner LIMIT 10, 20 INCLUDE g.PublisherId");

        Assert.NotNull(query.Where);
        Assert.Equal(2, query.OrderBy.Count);
        Assert.Equal("p", Assert.Single(query.Load).Alias.Value.Value);
        Assert.Equal(3, query.Select.Count);
        Assert.NotNull(query.Limit);
        Assert.NotNull(query.Offset);
        Assert.NotNull(query.Include);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void JoinChainIsNullWithoutJoinAndEqualsJoinsForASingleJoin()
    {
        Assert.Null(Parse("FROM INDEX 'Games' AS g WHERE g.Name = 'z'").JoinChain);

        var query = Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = id(u)");
        Assert.Equal(query.Joins, query.JoinChain);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void OuterQueryWithoutJoinKeepsAJoinNamedFieldUsable()
    {
        var query = Parse("FROM INDEX 'Games' AS g WHERE g.Name = 'join'");

        Assert.Null(query.Joins);
        Assert.NotNull(query.Where);
    }

    [RavenTheory(RavenTestCategory.Querying)]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users' LIMIT 5) AS u ON g.OwnerId = id(u)", "LIMIT or OFFSET")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users' LIMIT 5, 10) AS u ON g.OwnerId = id(u)", "LIMIT or OFFSET")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users' OFFSET 5) AS u ON g.OwnerId = id(u)", "LIMIT or OFFSET")]
    [InlineData("FROM INDEX 'Games' AS g JOIN FROM INDEX 'Users') AS u ON g.OwnerId = id(u)", "Expected '('")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') u ON g.OwnerId = id(u)", "requires an alias")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u g.OwnerId = id(u)", "requires an ON clause")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.OwnerId > id(u)", "must be an equality")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = id(x)", "must be an equality")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS g ON g.OwnerId = id(g)", "ON clause of a JOIN")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.A = id(u) JOIN (FROM INDEX 'Users') AS u ON g.B = id(u)", "used more than once")]
    [InlineData("FROM Games JOIN (FROM INDEX 'Users') AS u ON OwnerId = id(u)", "requires an alias for the main document")]
    public void InvalidJoinsExplainWhatIsWrong(string q, string expectedMessage)
    {
        var e = Assert.ThrowsAny<System.Exception>(() => Parse(q));

        Assert.Contains(expectedMessage, e.Message);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void SubQueryTextThatLooksLikeJoinSyntaxInsideStringsIsNotParsed()
    {
        var query = Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users' WHERE Name = 'a) join (b' AND Bio = \"x) AS z ON\") AS u ON g.OwnerId = id(u) WHERE g.Name = ')'");

        var join = Assert.Single(query.Joins);
        Assert.Equal("u", join.Alias.Value);
        Assert.IsType<BinaryExpression>(join.Query.Where);
        Assert.NotNull(query.Where);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void EscapedQuotesInsideTheSubQueryAreKept()
    {
        var query = Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users' WHERE Name = 'it\\'s') AS u ON g.OwnerId = id(u)");

        var where = (BinaryExpression)Assert.Single(query.Joins).Query.Where;
        Assert.Equal("it's", ((ValueExpression)where.Right).Token.Value);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void IndexNameWithAnEscapedQuoteSurvivesTheRoundTrip()
    {
        var query = Parse("FROM INDEX 'Games\\'s' AS g JOIN (FROM INDEX 'Users\\'s') AS u ON g.OwnerId = id(u)");

        var reparsed = Parse(query.ToString());

        Assert.Equal("Games's", reparsed.From.From.FieldValue);
        Assert.Equal("Users's", reparsed.Joins[0].Query.From.From.FieldValue);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void QuotedFieldsInTheOnClauseAreAccepted()
    {
        var query = Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.'Owner Id' = u.'User Id'");

        var join = Assert.Single(query.Joins);
        Assert.Equal("User Id", join.InnerField);
        Assert.True(((FieldExpression)join.On.Left).IsQuoted || ((FieldExpression)join.On.Left).FieldValue.Contains("Owner Id"));
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void SubQueryParametersAreKeptAsParameters()
    {
        var query = Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users' WHERE Name = $name) AS u ON g.OwnerId = id(u) WHERE g.Name = $game");

        var where = (BinaryExpression)Assert.Single(query.Joins).Query.Where;
        Assert.Equal(ValueTokenType.Parameter, ((ValueExpression)where.Right).Value);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void SubQueryCanHaveOrderBySelectAndLoad()
    {
        var query = Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users' AS x WHERE Name = 'a' ORDER BY Name LOAD x.CompanyId AS c SELECT x.Name, c.Name) AS u ON g.OwnerId = id(u)");

        var inner = Assert.Single(query.Joins).Query;
        Assert.NotNull(inner.OrderBy);
        Assert.NotNull(inner.Load);
        Assert.Equal(2, inner.Select.Count);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void SubQueryWithItsOwnJoinIsNestedAndRoundTrips()
    {
        var query = Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users' AS u JOIN (FROM INDEX 'Companies') AS c ON u.CompanyId = id(c)) AS u ON g.OwnerId = id(u)");

        var reparsed = Parse(query.ToString());

        Assert.Equal("c", Assert.Single(Assert.Single(reparsed.Joins).Query.Joins).Alias.Value);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void FieldJoinInChainFromAFieldJoinOfTheMainDocument()
    {
        var query = Parse("FROM INDEX 'Games' AS g " +
                          "JOIN (FROM INDEX 'Users') AS u ON g.Country = u.Country " +
                          "JOIN (FROM INDEX 'Companies') AS c ON c.Code = u.CompanyCode");

        var u = Assert.Single(query.Joins);
        Assert.Equal("Country", u.InnerField);
        var c = Assert.Single(u.Query.Joins);
        Assert.Equal("Code", c.InnerField);
        Assert.Equal("u.CompanyCode", ((FieldExpression)c.On.Left).FieldValue);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void JoinedFieldPathWithSeveralSegmentsIsKept()
    {
        var query = Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.Address.City = u.Address.Location.City");

        Assert.Equal("Address.Location.City", Assert.Single(query.Joins).InnerField);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void ProjectsDocumentIsOffAfterParsing()
    {
        var query = Parse("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = id(u) SELECT u.Name");

        Assert.False(Assert.Single(query.Joins).ProjectsDocument);
    }

    [RavenTheory(RavenTestCategory.Querying)]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = id()")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = id(u, g)")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.OwnerId = count(u)")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON id(g) = id(g)")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON id(u) = u.Id")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.A = u.B OR g.C = u.D")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON g.A != u.B")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS u ON")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (FROM INDEX 'Users') AS")]
    [InlineData("FROM INDEX 'Games' AS g JOIN (")]
    [InlineData("FROM INDEX 'Games' AS g JOIN")]
    public void MoreInvalidOnClausesAreRejected(string q)
    {
        var e = Record.Exception(() => Parse(q));
        Assert.True(e != null, q);
    }


    [RavenTheory(RavenTestCategory.Querying)]
    [InlineData("from Games as g join (from index 'Users') as u on g.OwnerId = id(u)")]
    [InlineData("from Games as g join (from Users) as u on g.OwnerId = id(u)")]
    public void MetadataRejectsJoinOnACollectionQuery(string q)
    {
        var e = Assert.Throws<InvalidQueryException>(() => Metadata(q));

        Assert.Contains("JOIN is supported only for queries on an index", e.Message);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void MetadataRejectsASubQueryOnACollection()
    {
        var e = Assert.Throws<InvalidQueryException>(() => Metadata("from index 'Games' as g join (from Users where Name = 'a') as u on g.OwnerId = id(u)"));

        Assert.Contains("must query an index", e.Message);
        Assert.Contains("'u'", e.Message);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void MetadataRejectsACollectionSubQueryDeepInAChain()
    {
        var e = Assert.Throws<InvalidQueryException>(() => Metadata(
            "from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) join (from Companies) as c on u.CompanyId = id(c)"));

        Assert.Contains("'c'", e.Message);
    }

    [RavenTheory(RavenTestCategory.Querying)]
    [InlineData(QueryType.Update, "from Games as g join (from index 'Users') as u on g.OwnerId = id(u) update { this.A = 1; }")]
    public void MetadataRejectsJoinForPatchOnCollections(QueryType type, string q)
    {
        Assert.ThrowsAny<System.Exception>(() => Metadata(q, type));
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void MetadataAcceptsJoinOnIndexes()
    {
        var metadata = Metadata("from index 'Games' as g join (from index 'Users' where Name = 'a') as u on g.OwnerId = id(u)");

        Assert.Equal("Games", metadata.IndexName);
        Assert.False(metadata.IsDynamic);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void SelectingAJoinedFieldAddsAnImplicitLoadAndMarksTheJoinAsProjected()
    {
        var metadata = Metadata("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) select g.Name, u.Name as Owner");

        var load = Assert.Single(metadata.Query.Load);
        Assert.Equal("u", load.Alias.Value.Value);
        Assert.Equal("g.OwnerId", ((FieldExpression)load.Expression).FieldValue);
        Assert.True(Assert.Single(metadata.Query.Joins).ProjectsDocument);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void NotSelectingTheJoinedAliasAddsNoLoad()
    {
        var metadata = Metadata("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) select g.Name");

        Assert.Null(metadata.Query.Load);
        Assert.False(Assert.Single(metadata.Query.Joins).ProjectsDocument);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void WithoutSelectNoLoadIsAdded()
    {
        var metadata = Metadata("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u)");

        Assert.Null(metadata.Query.Load);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void ExplicitLoadOfTheJoinAliasIsNotDuplicated()
    {
        var metadata = Metadata("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) load g.OwnerId as u select u.Name");

        Assert.Single(metadata.Query.Load);
        Assert.True(Assert.Single(metadata.Query.Joins).ProjectsDocument);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void SelectingAMethodOverTheJoinedAliasCountsAsUsingIt()
    {
        var metadata = Metadata("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) select id(u) as OwnerId");

        Assert.Equal("u", Assert.Single(metadata.Query.Load).Alias.Value.Value);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void SelectingFromAJavascriptProjectionCountsAsUsingTheJoinedAlias()
    {
        var metadata = Metadata("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) select { Name: g.Name, Owner: u.Name }");

        Assert.Equal("u", Assert.Single(metadata.Query.Load).Alias.Value.Value);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void SelectingTheLastJoinOfAChainLoadsTheDocumentItIsJoinedThrough()
    {
        var metadata = Metadata("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) join (from index 'Companies') as c on u.CompanyId = id(c) select c.Name");

        Assert.Equal(new[] { "u", "c" }, metadata.Query.Load.Select(x => x.Alias.Value.Value));
        Assert.Equal("g.OwnerId", ((FieldExpression)metadata.Query.Load[0].Expression).FieldValue);
        Assert.Equal("u.CompanyId", ((FieldExpression)metadata.Query.Load[1].Expression).FieldValue);
        Assert.All(metadata.Query.JoinChain, x => Assert.True(x.ProjectsDocument));
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void SelectingOnlyTheFirstJoinOfAChainDoesNotLoadTheLast()
    {
        var metadata = Metadata("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) join (from index 'Companies') as c on u.CompanyId = id(c) select u.Name");

        Assert.Equal("u", Assert.Single(metadata.Query.Load).Alias.Value.Value);
        Assert.True(metadata.Query.JoinChain[0].ProjectsDocument);
        Assert.False(metadata.Query.JoinChain[1].ProjectsDocument);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void SelectingAFieldJoinedAliasLoadsByTheOuterFieldAndProjectsTheBestDocument()
    {
        var metadata = Metadata("from index 'Games' as g join (from index 'Users') as u on g.Country = u.Country select u.Name");

        var load = Assert.Single(metadata.Query.Load);
        Assert.Equal("g.Country", ((FieldExpression)load.Expression).FieldValue);
        Assert.True(Assert.Single(metadata.Query.Joins).ProjectsDocument);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void TwoJoinsSelectedBothGetALoad()
    {
        var metadata = Metadata("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) join (from index 'Publishers') as p on g.PublisherId = id(p) select u.Name, p.Name as Publisher");

        Assert.Equal(new[] { "u", "p" }, metadata.Query.Load.Select(x => x.Alias.Value.Value));
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void MetadataOfAJoinWithParametersAndOrderByScore()
    {
        var metadata = Metadata("from index 'Games' as g join (from index 'Users' where Name = $name) as u on g.OwnerId = id(u) where search(g.Name, $game) order by score() limit 20");

        Assert.NotNull(metadata.Query.Joins);
        Assert.True(metadata.OrderBy.Length == 1);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void MetadataDoesNotChangeTheJoinOfAQueryWithoutOne()
    {
        var metadata = Metadata("from index 'Games' as g where g.Name = 'a' select g.Name");

        Assert.Null(metadata.Query.Joins);
        Assert.Null(metadata.Query.Load);
    }
}
