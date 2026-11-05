using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>A join that a join index serves next to joins that the server reads itself: the answers of the others and the whole answer stay what the join of the server gives.</summary>
public class JoinIndexJoinCombinationTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Join = "GamesWithUsers";
    private const string Games = "from index 'Games' as g";
    private const string ServedUsersJoin = "join (from index 'Users' where Name = 'u1') as u on g.OwnerId = id(u)";
    private const string OthersJoin = "join (from index 'Others') as o on g.OtherId = id(o)";

    private sealed class User
    {
        public string CountryId { get; set; }

        public string Name { get; set; }

        public int Level { get; set; }
    }

    private sealed class Country
    {
        public int Limit { get; set; }
    }

    private sealed class Other
    {
        public string Name { get; set; }
    }

    private sealed class Game
    {
        public string Id { get; set; }

        public string OwnerId { get; set; }

        public string OtherId { get; set; }

        public int Score { get; set; }
    }

    private sealed class NameProjection
    {
        public string Name { get; set; }
    }

    private static IndexDefinition[] Definitions()
    {
        return
        [
            new IndexDefinition { Name = "Users", Maps = { "from u in docs.Users select new { u.CountryId, u.Name, u.Level }" } },
            new IndexDefinition { Name = "Countries", Maps = { "from c in docs.Countries select new { c.Limit }" } },
            new IndexDefinition { Name = "Others", Maps = { "from o in docs.Others select new { o.Id, o.Name }" } },
            new IndexDefinition { Name = "Games", Maps = { "from g in docs.Games select new { g.OwnerId, g.OtherId, g.Score }" } },
            new IndexDefinition { Name = Join, Join = new JoinDefinition { OuterIndex = "Games", OuterField = "OwnerId", InnerIndex = "Users" }.AddField("Name") }
        ];
    }

    private async Task<DocumentStore> CreateStoreAsync()
    {
        DocumentStore store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        using (var session = store.OpenSession())
        {
            session.Store(new Country { Limit = 2 }, "countries/1");
            session.Store(new User { CountryId = "countries/1", Name = "u1", Level = 3 }, "users/1");
            session.Store(new User { CountryId = "countries/1", Name = "u2", Level = 1 }, "users/2");
            session.Store(new Other { Name = "o1" }, "others/1");
            session.Store(new Other { Name = "o2" }, "others/2");
            session.Store(new Game { OwnerId = "users/1", OtherId = "others/1", Score = 5 }, "games/1");
            session.Store(new Game { OwnerId = "users/2", OtherId = "others/2", Score = 1 }, "games/2");
            session.Store(new Game { OwnerId = "users/1", OtherId = "others/2", Score = 7 }, "games/3");
            session.Store(new Game { OwnerId = "users/3", OtherId = "others/1", Score = 9 }, "games/4");
            session.SaveChanges();
        }

        await store.Maintenance.SendAsync(new PutIndexesOperation(Definitions()));
        Indexes.WaitForIndexing(store);
        return store;
    }

    private static long Served(IDocumentStore store) => store.Maintenance.Send(new GetIndexStatisticsOperation(Join)).Join.Served;

    private static string[] Ids(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<Game>(rql).ToList().Select(x => x.Id).OrderBy(x => x).ToArray();
    }

    private static string[] Names(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<NameProjection>(rql).ToList().Select(x => x.Name).OrderBy(x => x).ToArray();
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    public async Task ServedJoinDoesNotBreakTheProjectionOfAnotherJoin()
    {
        using DocumentStore store = await CreateStoreAsync();
        long served = Served(store);

        Assert.Equal(["o1", "o2"], Names(store, $"{Games} {ServedUsersJoin} {OthersJoin} select o.Name limit 0, 100"));
        Assert.Equal(["games/1", "games/3"], Ids(store, $"{Games} {ServedUsersJoin} {OthersJoin} limit 0, 100"));
        Assert.True(Served(store) >= served + 2);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    public async Task ServedJoinDoesNotBreakTheProjectionOfAFieldJoin()
    {
        using DocumentStore store = await CreateStoreAsync();
        long served = Served(store);

        Assert.Equal(["o1", "o2"], Names(store, $"{Games} {ServedUsersJoin} join (from index 'Others') as o on g.OtherId = o.Id select o.Name limit 0, 100"));
        Assert.True(Served(store) > served);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    public async Task AJoinThatIsComparedAlongAChainIsReadByTheServer()
    {
        using DocumentStore store = await CreateStoreAsync();
        long served = Served(store);

        string query = $"{Games} join (from index 'Users') as u on g.OwnerId = id(u) join (from index 'Countries') as c on u.CountryId = id(c) where u.Level > c.Limit limit 0, 100";
        Assert.Equal(["games/1", "games/3"], Ids(store, query));
        Assert.Equal(served, Served(store));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    public async Task AJoinWithAChainBelowItIsReadByTheServer()
    {
        using DocumentStore store = await CreateStoreAsync();
        long served = Served(store);

        string chain = $"{Games} join (from index 'Users' as u join (from index 'Countries') as c on u.CountryId = id(c)) as u on g.OwnerId = id(u) limit 0, 100";
        Assert.Equal(["games/1", "games/2", "games/3"], Ids(store, chain));
        Assert.Equal(served, Served(store));
    }
}
