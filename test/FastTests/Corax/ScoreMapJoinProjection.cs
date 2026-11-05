using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Queries.Timings;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

public class ScoreMapJoinProjection(ITestOutputHelper output) : RavenTestBase(output)
{
    private class User
    {
        public string Name { get; set; }
        public string City { get; set; }
    }

    private class Game
    {
        public string Name { get; set; }
        public string OwnerId { get; set; }
    }

    private class Row
    {
        public string Name { get; set; }
        public string Owner { get; set; }
        public string City { get; set; }
    }

    // Name is stored in the index entry, City only exists in the document
    private class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name };
            Store(x => x.Name, FieldStorage.Yes);
        }

        public override string IndexName => "Users";
    }

    private class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId };
        }

        public override string IndexName => "Games";
    }

    private const string Rql = "from index 'Games' as g join (from index 'Users' where Name = 'chris') as u on g.OwnerId = id(u) " +
                               "where g.Name = 'zelda' select g.Name, u.Name as Owner include timings()";

    private IDocumentStore GetStore()
    {
        var store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);

        using (var session = store.OpenSession())
        {
            session.Store(new User { Name = "chris", City = "Utrecht" }, "users/1");
            session.Store(new Game { Name = "zelda", OwnerId = "users/1" });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
        return store;
    }

    private static bool HasTiming(QueryTimings timings, string name)
    {
        Assert.NotNull(timings.Timings); // the timings must have come back, or "no Load timing" proves nothing
        return Has(timings, name);
    }

    private static bool Has(QueryTimings timings, string name) =>
        timings.Timings != null && timings.Timings.Any(x => x.Key == name || Has(x.Value, name));

    private static string Dump(QueryTimings t) => t.Timings == null ? "" : string.Join(",", t.Timings.Select(x => x.Key + "(" + Dump(x.Value) + ")"));

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void StoredFieldOfTheJoinedIndexIsProjectedWithoutLoadingTheDocument()
    {
        using var store = GetStore();
        using var session = store.OpenSession();

        var row = Assert.Single(session.Advanced.RawQuery<Row>(Rql).Timings(out var timings).ToList());

        Assert.Equal("zelda/chris", row.Name + "/" + row.Owner);
        Assert.False(HasTiming(timings, "Load"), Dump(timings) + " a stored field should come from the index entry, the joined document should not be loaded");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void FieldThatIsNotStoredFallsBackToTheDocument()
    {
        using var store = GetStore();
        using var session = store.OpenSession();

        var row = Assert.Single(session.Advanced.RawQuery<Row>(
            "from index 'Games' as g join (from index 'Users' where Name = 'chris') as u on g.OwnerId = id(u) " +
            "where g.Name = 'zelda' select g.Name, u.Name as Owner, u.City as City include timings()").Timings(out var timings).ToList());

        Assert.Equal("zelda/chris/Utrecht", row.Name + "/" + row.Owner + "/" + row.City);
        Assert.True(HasTiming(timings, "Load"), Dump(timings) + " the document is loaded for the field that is not in the index entry");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ProjectionFromDocumentOnlyLoadsTheDocument()
    {
        using var store = GetStore();
        using var session = store.OpenSession();

        var row = Assert.Single(session.Advanced.RawQuery<Row>(Rql).Projection(ProjectionBehavior.FromDocument).Timings(out var timings).ToList());

        Assert.Equal("zelda/chris", row.Name + "/" + row.Owner);
        Assert.True(HasTiming(timings, "Load"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void StoredFieldOfAJoinedDocumentThatNoLongerMatchesTheJoinIsNotReturned()
    {
        using var store = GetStore();
        using (var session = store.OpenSession())
        {
            session.Store(new Game { Name = "zelda", OwnerId = "users/missing" });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
        using var read = store.OpenSession();

        // the join is a filter: only the game of the matching user comes back, with the owner of that game
        var rows = read.Advanced.RawQuery<Row>(Rql).ToList();
        Assert.Equal(new[] { "zelda/chris" }, rows.Select(x => x.Name + "/" + x.Owner));
    }
}
