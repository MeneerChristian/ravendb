using System;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// Generates RQL on the client only: the store is never contacted.
public class JoinClientLanguageTests : NoDisposalNeeded, IDisposable
{
    private readonly IDocumentStore _store;

    public JoinClientLanguageTests(ITestOutputHelper output) : base(output)
    {
        _store = new DocumentStore { Urls = new[] { "http://localhost:1" }, Database = "JoinLanguage" }.Initialize();
    }

    public void Dispose() => _store.Dispose();

    private class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string PublisherId { get; set; }
        public string Country { get; set; }
    }

    private class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Country { get; set; }
        public Address Address { get; set; }
    }

    private class Address
    {
        public string City { get; set; }
    }

    private class Publisher
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    private IDocumentSession Session() => _store.OpenSession();

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqJoinOnIdAddsAnAliasToTheMainDocument()
    {
        using var session = Session();
        var users = session.Query<User>("Users", isMapReduce: false).Search(x => x.Name, "chris");

        var rql = session.Query<Game>("Games", isMapReduce: false)
            .Search(x => x.Name, "zelda")
            .Join(users, x => x.OwnerId)
            .OrderByScore()
            .ToString();

        Assert.StartsWith("from index 'Games' as __alias_join join (from index 'Users' where search(Name, $p1)) as j on __alias_join.OwnerId = id(j)", rql);
        Assert.Contains("where search(__alias_join.Name,", rql);
        Assert.Contains("order by score()", rql);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqJoinDoesNotMutateTheQueryWhenRenderedTwice()
    {
        using var session = Session();
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");
        var query = session.Query<Game>("Games", isMapReduce: false).Where(x => x.Name == "zelda").Join(users, x => x.OwnerId);

        Assert.Equal(query.ToString(), query.ToString());
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqJoinOnAFieldUsesTheFieldOfTheJoinedDocuments()
    {
        using var session = Session();
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");

        var rql = session.Query<Game>("Games", isMapReduce: false).Join(users, x => x.Country, u => u.Country).ToString();

        Assert.Contains(" as j on __alias_join.Country = j.Country", rql);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqJoinOnANestedJoinedFieldUsesThePath()
    {
        using var session = Session();
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");

        var rql = session.Query<Game>("Games", isMapReduce: false).Join(users, x => x.Country, u => u.Address.City).ToString();

        Assert.Contains("on __alias_join.Country = j.Address.City", rql);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqJoinWithTheIdentityPropertyOfTheJoinedDocumentsJoinsOnTheId()
    {
        using var session = Session();
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");

        var rql = session.Query<Game>("Games", isMapReduce: false).Join(users, x => x.OwnerId, u => u.Id).ToString();

        Assert.Contains("on __alias_join.OwnerId = id(j)", rql);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqJoinOnTheIdOfTheMainDocumentUsesIdFunction()
    {
        using var session = Session();
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");

        var rql = session.Query<Game>("Games", isMapReduce: false).Join(users, x => x.Id).ToString();

        Assert.Contains("on id(__alias_join) = id(j)", rql);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqSeveralJoinsGetDistinctAliases()
    {
        using var session = Session();
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");
        var publishers = session.Query<Publisher>("Publishers", isMapReduce: false).Where(x => x.Name == "nintendo");

        var rql = session.Query<Game>("Games", isMapReduce: false)
            .Join(users, x => x.OwnerId)
            .Join(publishers, x => x.PublisherId)
            .ToString();

        Assert.Contains(" as j on __alias_join.OwnerId = id(j)", rql);
        Assert.Contains(" as j1 on __alias_join.PublisherId = id(j1)", rql);
        Assert.True(rql.IndexOf(" as j on ", StringComparison.Ordinal) < rql.IndexOf(" as j1 on ", StringComparison.Ordinal));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqJoinParametersOfBothQueriesDoNotCollide()
    {
        using var session = Session();
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");

        var query = session.Query<Game>("Games", isMapReduce: false).Where(x => x.Name == "zelda").Join(users, x => x.OwnerId);
        var indexQuery = ((IRavenQueryInspector)query).GetIndexQuery(isAsync: false);

        Assert.Equal(2, indexQuery.QueryParameters.Count);
        Assert.Contains(indexQuery.QueryParameters, x => Equals(x.Value, "zelda"));
        Assert.Contains(indexQuery.QueryParameters, x => Equals(x.Value, "chris"));
        foreach (var name in indexQuery.QueryParameters.Keys)
            Assert.Contains("$" + name, indexQuery.Query);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqJoinWithAProjectionKeepsTheAliasOfTheProjection()
    {
        using var session = Session();
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");

        var rql = session.Query<Game>("Games", isMapReduce: false)
            .Join(users, x => x.OwnerId)
            .Select(g => new { g.Name })
            .ToString();

        Assert.EndsWith(" as j on __alias_join.OwnerId = id(j) select Name", rql);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqJoinKeepsPagingAndOrdering()
    {
        using var session = Session();
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");

        var rql = session.Query<Game>("Games", isMapReduce: false)
            .Join(users, x => x.OwnerId)
            .OrderBy(x => x.Name)
            .Skip(5)
            .Take(10)
            .ToString();

        Assert.Contains("order by __alias_join.Name", rql);
        Assert.Contains("limit $", rql);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void QuerySyntaxJoinOnIdIsRenderedLikeTheExtensionMethod()
    {
        using var session = Session();
        var games = session.Query<Game>("Games", isMapReduce: false);
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");

        var rql = (from g in games join u in users on g.OwnerId equals u.Id select g).ToString();

        Assert.StartsWith("from index 'Games' as __alias_join join (from index 'Users' where Name = $p0) as u on __alias_join.OwnerId = id(u)", rql);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void QuerySyntaxJoinOnAFieldJoinsOnThatField()
    {
        using var session = Session();
        var games = session.Query<Game>("Games", isMapReduce: false);
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");

        var rql = (from g in games join u in users on g.Country equals u.Country select g).ToString();

        Assert.Contains("on __alias_join.Country = u.Country", rql);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void QuerySyntaxWithTwoJoinsRendersBoth()
    {
        using var session = Session();
        var games = session.Query<Game>("Games", isMapReduce: false);
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");
        var publishers = session.Query<Publisher>("Publishers", isMapReduce: false).Where(x => x.Name == "nintendo");

        var rql = (from g in games
                   join u in users on g.OwnerId equals u.Id
                   join p in publishers on g.PublisherId equals p.Id
                   select g).ToString();

        Assert.Contains(" as u on __alias_join.OwnerId = id(u)", rql);
        Assert.Contains(" as p on __alias_join.PublisherId = id(p)", rql);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void QuerySyntaxCannotSelectTheJoinedDocument()
    {
        using var session = Session();
        var games = session.Query<Game>("Games", isMapReduce: false);
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");

        var e = Assert.ThrowsAny<Exception>(() => (from g in games join u in users on g.OwnerId equals u.Id select u).ToString());

        Assert.Contains("as a whole", e.ToString());
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void QuerySyntaxProjectsTheFieldsOfTheJoinedDocument()
    {
        using var session = Session();
        var games = session.Query<Game>("Games", isMapReduce: false);
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");

        var rql = (from g in games join u in users on g.OwnerId equals u.Id select new { g.Name, u.Country }).ToString();

        Assert.Contains("select Name, u.Country as Country", rql);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqJoinWithAComputedJoinedKeyIsRejected()
    {
        using var session = Session();
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");

        var e = Assert.ThrowsAny<Exception>(() => session.Query<Game>("Games", isMapReduce: false).Join(users, x => x.Country, u => u.Name.ToLower()).ToString());

        Assert.Contains("has to be a property or a path of properties", e.ToString());
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqJoinWithASourceThatIsNotARavenQueryIsRejected()
    {
        using var session = Session();
        var notRaven = new[] { new User() }.AsQueryable();

        var e = Assert.ThrowsAny<Exception>(() => session.Query<Game>("Games", isMapReduce: false).Join(notRaven, x => x.OwnerId).ToString());

        Assert.Contains("must be a Raven query", e.ToString());
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQueryJoinByExpressionAndByNameRenderTheSame()
    {
        using var session = Session();
        var byExpression = session.Advanced.DocumentQuery<Game>("Games").Join(session.Advanced.DocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris"), x => x.OwnerId).ToString();
        var byName = session.Advanced.DocumentQuery<Game>("Games").Join(session.Advanced.DocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris"), "OwnerId").ToString();

        Assert.Equal(byExpression, byName);
        Assert.StartsWith("from index 'Games' as __alias_join join (from index 'Users' where Name = $p0) as j on __alias_join.OwnerId = id(j)", byName);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQueryJoinOnAFieldRendersTheJoinedField()
    {
        using var session = Session();
        var users = session.Advanced.DocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris");

        var byExpression = session.Advanced.DocumentQuery<Game>("Games").Join(users, x => x.Country, u => u.Country).ToString();
        var byName = session.Advanced.DocumentQuery<Game>("Games").Join(session.Advanced.DocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris"), "Country", "Country").ToString();

        Assert.Contains("on __alias_join.Country = j.Country", byExpression);
        Assert.Equal(byExpression, byName);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQueryJoinedFieldWithSpecialCharactersIsEscaped()
    {
        using var session = Session();
        var users = session.Advanced.DocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris");

        var rql = session.Advanced.DocumentQuery<Game>("Games").Join(users, "Country", "First Name").ToString();

        Assert.Contains("j.'First Name'", rql);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQueryAsyncSourceIsAccepted()
    {
        using var session = _store.OpenAsyncSession();
        var users = session.Advanced.AsyncDocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris");

        var rql = session.Advanced.AsyncDocumentQuery<Game>("Games").Join(users, x => x.OwnerId).ToString();

        Assert.Contains(" join (from index 'Users' where Name = $p0) as j on __alias_join.OwnerId = id(j)", rql);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQueryJoinKeepsAnExplicitAliasOfTheMainDocument()
    {
        using var session = Session();
        var users = session.Advanced.DocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris");

        var rql = session.Advanced.DocumentQuery<Game>("Games").Join(users, x => x.OwnerId).SelectFields<Game>("Name").ToString();

        Assert.Contains(" join (from index 'Users'", rql);
        Assert.Contains(" as j on ", rql);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQueryJoinRejectsEmptyOuterFieldAndNullSource()
    {
        using var session = Session();
        var users = session.Advanced.DocumentQuery<User>("Users");

        Assert.Throws<ArgumentException>(() => session.Advanced.DocumentQuery<Game>("Games").Join(users, " "));
        Assert.Throws<ArgumentException>(() => session.Advanced.DocumentQuery<Game>("Games").Join(users, (string)null));
        Assert.Throws<ArgumentNullException>(() => session.Advanced.DocumentQuery<Game>("Games").Join((IDocumentQuery<User>)null, "OwnerId"));
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQueryJoinsSurviveCloningIntoAnotherType()
    {
        using var session = Session();
        var users = session.Advanced.DocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris");

        var rql = session.Advanced.DocumentQuery<Game>("Games").Join(users, x => x.OwnerId).OfType<Game>().SelectFields<Game>("Name").ToString();

        Assert.Contains(" join (from index 'Users'", rql);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQuerySourceParametersAreRenamedWhenTheOuterQueryAlreadyHasParameters()
    {
        using var session = Session();
        var users = session.Advanced.DocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris").WhereEquals(x => x.Country, "NL");

        var query = session.Advanced.DocumentQuery<Game>("Games").WhereEquals(x => x.Name, "zelda").Join(users, x => x.OwnerId).WhereEquals(x => x.Country, "JP");
        var indexQuery = query.GetIndexQuery();

        Assert.Equal(new object[] { "chris", "JP", "NL", "zelda" }.OrderBy(x => x), indexQuery.QueryParameters.Values.OrderBy(x => x));
        Assert.Equal(4, indexQuery.QueryParameters.Count);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQueryJoinSourceStringValuesThatLookLikeParametersAreNotRewritten()
    {
        using var session = Session();
        var users = session.Advanced.DocumentQuery<User>("Users").WhereEquals(x => x.Name, "$p0 and $p1");

        var query = session.Advanced.DocumentQuery<Game>("Games").WhereEquals(x => x.Name, "zelda").Join(users, x => x.OwnerId);
        var indexQuery = query.GetIndexQuery();

        Assert.Contains(indexQuery.QueryParameters, x => Equals(x.Value, "$p0 and $p1"));
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQueryLeftJoinRendersLeftJoin()
    {
        using var session = Session();
        var users = session.Advanced.DocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris");

        var query = session.Advanced.DocumentQuery<Game>("Games").Join(users, x => x.OwnerId, left: true);

        Assert.Equal("from index 'Games' as __alias_join left join (from index 'Users' where Name = $p0) as j on __alias_join.OwnerId = id(j)", query.ToString());
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQueryBoostIsAQueryParameter()
    {
        using var session = Session();
        var users = session.Advanced.DocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris");

        var query = session.Advanced.DocumentQuery<Game>("Games").Join(users, x => x.OwnerId, boost: 0.5);

        Assert.Equal("from index 'Games' as __alias_join join (from index 'Users' where Name = $p0) as j on __alias_join.OwnerId = id(j) boost $p1", query.ToString());
        var parameters = query.GetIndexQuery().QueryParameters;
        Assert.Equal("chris", parameters["p0"]);
        Assert.Equal(0.5, parameters["p1"]);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQueryLeftJoinWithBoostZeroAndAFieldJoin()
    {
        using var session = Session();
        var byExpression = session.Advanced.DocumentQuery<Game>("Games")
            .Join(session.Advanced.DocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris"), x => x.Country, u => u.Country, left: true, boost: 0).ToString();
        var byName = session.Advanced.DocumentQuery<Game>("Games")
            .Join(session.Advanced.DocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris"), "Country", "Country", true, 0).ToString();

        Assert.Equal("from index 'Games' as __alias_join left join (from index 'Users' where Name = $p0) as j on __alias_join.Country = j.Country boost $p1", byExpression);
        Assert.Equal(byExpression, byName);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQueryJoinWithoutOptionsIsUnchanged()
    {
        using var session = Session();
        var users = session.Advanced.DocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris");

        var rql = session.Advanced.DocumentQuery<Game>("Games").Join(users, x => x.OwnerId).ToString();

        Assert.Equal("from index 'Games' as __alias_join join (from index 'Users' where Name = $p0) as j on __alias_join.OwnerId = id(j)", rql);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQueryJoinParametersOfBothQueriesAndTheBoostDoNotCollide()
    {
        using var session = Session();
        var users = session.Advanced.DocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris");

        var query = session.Advanced.DocumentQuery<Game>("Games").WhereEquals(x => x.Name, "zelda").Join(users, x => x.OwnerId, left: true, boost: 2);

        Assert.Equal("from index 'Games' as __alias_join left join (from index 'Users' where Name = $p1) as j on __alias_join.OwnerId = id(j) boost $p2 where __alias_join.Name = $p0", query.ToString());
        var parameters = query.GetIndexQuery().QueryParameters;
        Assert.Equal("zelda", parameters["p0"]);
        Assert.Equal("chris", parameters["p1"]);
        Assert.Equal(2.0, parameters["p2"]);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void AsyncDocumentQueryLeftJoinWithBoost()
    {
        using var session = _store.OpenAsyncSession();
        var users = session.Advanced.AsyncDocumentQuery<User>("Users").WhereEquals(x => x.Name, "chris");

        var query = session.Advanced.AsyncDocumentQuery<Game>("Games").Join(users, x => x.OwnerId, left: true, boost: 0.25);

        Assert.Equal("from index 'Games' as __alias_join left join (from index 'Users' where Name = $p0) as j on __alias_join.OwnerId = id(j) boost $p1", query.ToString());
        Assert.Equal(0.25, query.GetIndexQuery().QueryParameters["p1"]);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQueryJoinRejectsANegativeOrNonFiniteBoost()
    {
        using var session = Session();
        var users = session.Advanced.DocumentQuery<User>("Users");

        Assert.Throws<ArgumentOutOfRangeException>(() => session.Advanced.DocumentQuery<Game>("Games").Join(users, "OwnerId", boost: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Advanced.DocumentQuery<Game>("Games").Join(users, "OwnerId", boost: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Advanced.DocumentQuery<Game>("Games").Join(users, "OwnerId", boost: double.PositiveInfinity));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqLeftJoinWithBoostOnId()
    {
        using var session = Session();
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");

        var query = session.Query<Game>("Games", isMapReduce: false).Join(users, x => x.OwnerId, left: true, boost: 0);

        Assert.Equal("from index 'Games' as __alias_join left join (from index 'Users' where Name = $p0) as j on __alias_join.OwnerId = id(j) boost $p1", query.ToString());
        Assert.Equal(0.0, ((IRavenQueryInspector)query).GetIndexQuery(isAsync: false).QueryParameters["p1"]);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqLeftJoinWithBoostOnAField()
    {
        using var session = Session();
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");

        var query = session.Query<Game>("Games", isMapReduce: false).Join(users, x => x.Country, u => u.Country, left: true, boost: 1.5);

        Assert.Equal("from index 'Games' as __alias_join left join (from index 'Users' where Name = $p0) as j on __alias_join.Country = j.Country boost $p1", query.ToString());
        Assert.Equal(1.5, ((IRavenQueryInspector)query).GetIndexQuery(isAsync: false).QueryParameters["p1"]);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void LinqJoinWithBoostOnlyIsAnInnerJoin()
    {
        using var session = Session();
        var users = session.Query<User>("Users", isMapReduce: false).Where(x => x.Name == "chris");

        var rql = session.Query<Game>("Games", isMapReduce: false).Join(users, x => x.OwnerId, boost: 2).OrderByScore().ToString();

        Assert.Equal("from index 'Games' as __alias_join join (from index 'Users' where Name = $p0) as j on __alias_join.OwnerId = id(j) boost $p1 order by score()", rql);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void DocumentQuerySourceParametersAreRenamedByTokenNotInsideStringLiterals()
    {
        using var session = Session();
        var source = new IndexQuery { Query = "from index 'Users' where Name = 'cost $p0' and Country = $p0", QueryParameters = new Parameters { ["p0"] = "NL" } };

        var query = (DocumentQuery<Game>)session.Advanced.DocumentQuery<Game>("Games").WhereEquals(x => x.Name, "zelda");
        query.Join(source, "OwnerId");

        var rql = query.ToString();
        Assert.Contains("Name = 'cost $p0'", rql);
        Assert.Contains("Country = $p1", rql);
    }
}
