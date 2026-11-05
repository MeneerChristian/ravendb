using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Serving;

/// <summary>A join index holds the join of an id or the join of one field of the inner index; a join is answered only by the join index that holds that very join.</summary>
public class JoinFieldJoinServingTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Join = "GamesWithUsers";

    private const string FieldJoin = "GamesWithUsersByCode";

    private sealed class User
    {
        public string Code { get; set; }

        public string Name { get; set; }
    }

    private sealed class Game
    {
        public string Id { get; set; }

        public string OwnerId { get; set; }
    }

    private async Task<DocumentStore> CreateStoreAsync(bool idJoin = true, string innerField = null)
    {
        DocumentStore store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        using (var session = store.OpenSession())
        {
            session.Store(new User { Code = "c2", Name = "u1" }, "users/1");
            session.Store(new User { Code = "c1", Name = "u2" }, "users/2");
            session.Store(new Game { OwnerId = "users/1" }, "games/1");
            session.Store(new Game { OwnerId = "c1" }, "games/2");
            session.Store(new Game { OwnerId = "c2" }, "games/3");
            session.Store(new Game { OwnerId = "users/9" }, "games/4");
            session.SaveChanges();
        }

        await store.Maintenance.SendAsync(new PutIndexesOperation(
            new IndexDefinition { Name = "Users", Maps = { "from u in docs.Users select new { u.Code, u.Name }" } },
            new IndexDefinition { Name = "Games", Maps = { "from g in docs.Games select new { g.OwnerId }" } },
            new IndexDefinition { Name = Join, Join = new JoinDefinition { OuterIndex = "Games", OuterField = "OwnerId", InnerIndex = "Users" }.AddField("Name") }));
        if (idJoin == false)
            await store.Maintenance.SendAsync(new DeleteIndexOperation(Join));

        if (innerField != null)
        {
            await store.Maintenance.SendAsync(new PutIndexesOperation(
                new IndexDefinition { Name = FieldJoin, Join = new JoinDefinition { OuterIndex = "Games", OuterField = "OwnerId", InnerField = innerField, InnerIndex = "Users" }.AddField("Name") }));
        }

        Indexes.WaitForIndexing(store);
        return store;
    }

    private static string[] Ids(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<Game>(rql).ToList().Select(x => x.Id).OrderBy(x => x).ToArray();
    }

    private static long Served(IDocumentStore store, string joinIndex = Join) => store.Maintenance.Send(new GetIndexStatisticsOperation(joinIndex)).Join.Served;

    private static long Fallbacks(IDocumentStore store, string joinIndex) => store.Maintenance.Send(new GetIndexStatisticsOperation(joinIndex)).Join.FallbackTotal;

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    public async Task AJoinOnAFieldIsNotServedByTheJoinOfTheSameIndexesOnTheId()
    {
        using DocumentStore store = await CreateStoreAsync();
        const string idJoin = "from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) limit 0, 100";
        const string fieldJoin = "from index 'Games' as g join (from index 'Users') as u on g.OwnerId = u.Code limit 0, 100";

        Assert.Equal(["games/1"], Ids(store, idJoin));
        long served = Served(store);
        Assert.True(served > 0);

        Assert.Equal(["games/2", "games/3"], Ids(store, fieldJoin));
        Assert.Equal(served, Served(store));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    public async Task AJoinOnAFieldIsCountedAsAKindThatTheJoinDoesNotServe()
    {
        using DocumentStore store = await CreateStoreAsync();
        const string fieldJoin = "from index 'Games' as g join (from index 'Users') as u on g.OwnerId = u.Code limit 0, 100";

        // the client caches the answer of a repeated query, so the second one differs
        Assert.Equal(["games/2", "games/3"], Ids(store, fieldJoin));
        Assert.Equal(["games/2", "games/3"], Ids(store, fieldJoin.Replace("100", "99")));

        var fallbacks = store.Maintenance.Send(new GetIndexStatisticsOperation(Join)).Join.Fallbacks;
        Assert.Equal(2, fallbacks.GetValueOrDefault("KindNotServed:FieldJoin"));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    public async Task AJoinOfTheIdOfTheMainDocumentOnAFieldIsNotServedByTheJoinOfTheSameIndexes()
    {
        using DocumentStore store = await CreateStoreAsync();
        using (var session = store.OpenSession())
        {
            session.Store(new User { Code = "GAMES/3", Name = "u3" }, "users/3");
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
        const string idJoin = "from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) limit 0, 100";
        const string idOfMainJoin = "from index 'Games' as g join (from index 'Users') as u on id(g) = u.Code limit 0, 100";
        const string reversed = "from index 'Games' as g join (from index 'Users') as u on u.Code = id(g) limit 0, 99";

        Assert.Equal(["games/1"], Ids(store, idJoin));
        long served = Served(store);
        Assert.True(served > 0);

        Assert.Equal(["games/3"], Ids(store, idOfMainJoin));
        Assert.Equal(["games/3"], Ids(store, reversed));
        Assert.Equal(served, Served(store));

        // the join index still answers the join on the id
        Assert.Equal(["games/1"], Ids(store, idJoin.Replace("100", "98")));
        Assert.True(Served(store) > served);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    public async Task AFieldJoinWithASubQueryIsNotServedByTheJoinOnTheId()
    {
        using DocumentStore store = await CreateStoreAsync();
        long served = Served(store);

        Assert.Equal(["games/2"], Ids(store, "from index 'Games' as g join (from index 'Users' where Name = 'u2') as u on g.OwnerId = u.Code limit 0, 100"));
        Assert.Equal(served, Served(store));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    public async Task AJoinOnAFieldIsServedByTheJoinOfThatField()
    {
        using DocumentStore store = await CreateStoreAsync(idJoin: false, innerField: "Code");
        const string fieldJoin = "from index 'Games' as g join (from index 'Users' where Name = 'u2') as u on g.OwnerId = u.Code limit 0, 100";

        Assert.Equal(["games/2"], Ids(store, fieldJoin));
        Assert.Equal(1, Served(store, FieldJoin));
        Assert.Equal(0, Fallbacks(store, FieldJoin));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    public async Task AJoinOnAFieldAndAJoinOnTheIdEachServeTheirOwnJoin()
    {
        using DocumentStore store = await CreateStoreAsync(innerField: "Code");
        const string idJoin = "from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) limit 0, 100";
        const string fieldJoin = "from index 'Games' as g join (from index 'Users') as u on g.OwnerId = u.Code limit 0, 100";

        Assert.Equal(["games/1"], Ids(store, idJoin));
        Assert.Equal(1, Served(store));
        Assert.Equal(0, Served(store, FieldJoin));

        Assert.Equal(["games/2", "games/3"], Ids(store, fieldJoin));
        Assert.Equal(1, Served(store));
        Assert.Equal(1, Served(store, FieldJoin));

        // neither of them counts the join of the other as a fallback
        Assert.Equal(0, Fallbacks(store, Join));
        Assert.Equal(0, Fallbacks(store, FieldJoin));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    public async Task AJoinOnAFieldNeverServesAJoinOnTheId()
    {
        using DocumentStore store = await CreateStoreAsync(idJoin: false, innerField: "Code");
        const string idJoin = "from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) limit 0, 100";

        Assert.Equal(["games/1"], Ids(store, idJoin));
        Assert.Equal(0, Served(store, FieldJoin));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    public async Task AJoinOnAnotherFieldIsCountedOnTheJoinOfTheSameIndexes()
    {
        using DocumentStore store = await CreateStoreAsync(idJoin: false, innerField: "Code");
        const string byName = "from index 'Games' as g join (from index 'Users') as u on g.OwnerId = u.Name limit 0, 100";

        Assert.Empty(Ids(store, byName));
        Assert.Equal(0, Served(store, FieldJoin));
        Assert.Equal(1, store.Maintenance.Send(new GetIndexStatisticsOperation(FieldJoin)).Join.Fallbacks.GetValueOrDefault("KindNotServed:FieldJoin"));
    }

    private const string IdOfOuterJoin = "GamesWithUsersOfTheirId";

    private async Task<DocumentStore> CreateIdOfOuterStoreAsync()
    {
        DocumentStore store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        using (var session = store.OpenSession())
        {
            session.Store(new User { Code = "games/3", Name = "u1" }, "users/1");
            session.Store(new User { Code = "GAMES/1", Name = "u2" }, "users/2");
            session.Store(new User { Code = "games/1", Name = "u3" }, "users/3");
            session.Store(new User { Code = "nobody", Name = "u4" }, "users/4");
            session.Store(new Game { OwnerId = "users/1" }, "games/1");
            session.Store(new Game { OwnerId = "users/2" }, "games/2");
            session.Store(new Game { OwnerId = "users/3" }, "games/3");
            session.SaveChanges();
        }

        await store.Maintenance.SendAsync(new PutIndexesOperation(
            new IndexDefinition { Name = "Users", Maps = { "from u in docs.Users select new { u.Code, u.Name }" } },
            new IndexDefinition { Name = "Games", Maps = { "from g in docs.Games select new { g.OwnerId }" } },
            new IndexDefinition { Name = IdOfOuterJoin, Join = new JoinDefinition { OuterIndex = "Games", OuterField = "id()", InnerField = "Code", InnerIndex = "Users" }.AddField("Name") }));
        Indexes.WaitForIndexing(store);
        return store;
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    public async Task AJoinOfTheIdOfTheMainDocumentOnAFieldIsServedByTheJoinOfThatKey()
    {
        using DocumentStore store = await CreateIdOfOuterStoreAsync();
        const string idOfMain = "from index 'Games' as g join (from index 'Users' where Name = 'u3') as u on id(g) = u.Code limit 0, 100";
        const string reversed = "from index 'Games' as g join (from index 'Users' where Name = 'u3') as u on u.Code = id(g) limit 0, 99";

        Assert.Equal(["games/1"], Ids(store, idOfMain));
        Assert.Equal(["games/1"], Ids(store, reversed));
        Assert.Equal(2, Served(store, IdOfOuterJoin));
        Assert.Equal(0, Fallbacks(store, IdOfOuterJoin));

        // the conversations that two users name
        Assert.Equal(["games/1", "games/3"], Ids(store, "from index 'Games' as g join (from index 'Users') as u on id(g) = u.Code limit 0, 98"));
        Assert.Equal(3, Served(store, IdOfOuterJoin));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    public async Task AJoinOfTheIdOfTheMainDocumentWithAProjectedAliasIsNotServed()
    {
        using DocumentStore store = await CreateIdOfOuterStoreAsync();
        long served = Served(store, IdOfOuterJoin);

        using (var session = store.OpenSession())
        {
            var rows = session.Advanced.RawQuery<Game>("from index 'Games' as g join (from index 'Users' where Name = 'u3') as u on id(g) = u.Code select u.Name limit 0, 100").ToList();
            Assert.Single(rows);
        }

        Assert.Equal(served, Served(store, IdOfOuterJoin));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    public async Task AJoinOnAFieldOfTheMainDocumentIsNeverServedByTheJoinOfTheId()
    {
        using DocumentStore store = await CreateIdOfOuterStoreAsync();

        Assert.Empty(Ids(store, "from index 'Games' as g join (from index 'Users') as u on g.OwnerId = u.Code limit 0, 100"));
        Assert.Equal(0, Served(store, IdOfOuterJoin));
        Assert.Equal(0, Fallbacks(store, IdOfOuterJoin));
    }
}
