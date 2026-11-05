using System;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Tests.Infrastructure;
using Xunit;
using EngineType = Raven.Client.Documents.Indexes.SearchEngineType;

namespace FastTests.Corax;

// The query text the client builds for a JOIN: the parameters of the source query
public class JoinClientHardeningTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private sealed class Game
    {
        public string Id { get; set; }
        public string Country { get; set; }
        public string OwnerId { get; set; }
    }

    private sealed class Tagged
    {
        public string Id { get; set; }
        public string OwnerId { get; set; }
        public string J { get; set; }
    }

    private sealed class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    private sealed class Games : AbstractIndexCreationTask<Game>
    {
        public Games()
        {
            Map = games => from g in games select new { g.Country, g.OwnerId };
            SearchEngineType = EngineType.Corax;
        }
    }

    private sealed class Users : AbstractIndexCreationTask<User>
    {
        public Users()
        {
            Map = users => from u in users select new { u.Name };
            SearchEngineType = EngineType.Corax;
        }
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    [InlineData("/* it's */")]
    [InlineData("/[']/.test(u.Name) ? 1 : 0, /* x */")]
    [InlineData("\"it's\"")]
    public void ParametersOfTheSourceQueryAreRenamedWhenItsProjectionHoldsAnApostrophe(string snippet)
    {
        using var store = GetDocumentStore();
        using var session = store.OpenSession();

        // a declared function comes before the where clause, so its text is scanned before the parameter
        var source = new IndexQuery
        {
            Query = $"declare function f(u) {{ return {{ Name: {snippet} u.Name }}; }} from index 'Users' as u where Name = $p0 select f(u)",
            QueryParameters = new Parameters { ["p0"] = "ann" }
        };
        var query = session.Advanced.DocumentQuery<Game, Games>().WhereEquals("Country", "nl");
        ((IAbstractDocumentQuery<Game>)query).Join(source, "OwnerId");

        var text = query.ToString();

        Assert.Contains("$p1", text);
        Assert.DoesNotContain("Name = $p0", text);
        Assert.Contains(snippet.Split(',')[0], text);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    public void DefaultAliasThatIsAlsoAMemberOfTheMainTypeAsksForAnExplicitAlias()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenSession();

        var e = Assert.Throws<InvalidOperationException>(() => session.Advanced.DocumentQuery<Tagged>()
            .Join(session.Advanced.DocumentQuery<User, Users>().WhereEquals("Name", "ann"), x => x.OwnerId)
            .ToString());
        Assert.Contains("JoinAlias", e.Message);
        Assert.Contains("'j'", e.Message);

        var text = session.Advanced.DocumentQuery<Tagged>()
            .Join(session.Advanced.DocumentQuery<User, Users>().WhereEquals("Name", "ann"), x => x.OwnerId).JoinAlias("u")
            .WhereEquals("J", "x")
            .ToString();
        Assert.Contains(" as u on ", text);
    }
}
