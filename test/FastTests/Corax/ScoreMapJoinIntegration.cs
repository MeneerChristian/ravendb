using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Subscriptions;
using Sparrow.Json;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Exceptions;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

public class ScoreMapJoinIntegration(ITestOutputHelper output) : RavenTestBase(output)
{
    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoinKeepsOnlyGamesOfMatchingUsers()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var names = session.Advanced.RawQuery<Game>(
                "from index 'Games' as g " +
                "join (from index 'Users' where search(Name, 'chris bob') select id()) as u on g.OwnerId = id(u) " +
                "order by g.Name")
            .ToList()
            .Select(x => x.Name)
            .ToList();

        Assert.Equal(new[] { "mario one", "zelda one", "zelda two" }, names);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoinWithNoMatchingUsersReturnsNothing()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var results = session.Advanced.RawQuery<Game>(
                "from index 'Games' as g " +
                "join (from index 'Users' where search(Name, 'nobody') select id()) as u on g.OwnerId = id(u)")
            .ToList();

        Assert.Empty(results);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoinCombinesWithTheOuterWhereClause()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var results = session.Advanced.RawQuery<Game>(
                "from index 'Games' as g " +
                "join (from index 'Users' where search(Name, 'chris bob') select id()) as u on g.OwnerId = id(u) " +
                "where search(g.Name, 'zelda') order by g.Name")
            .ToList();

        Assert.Equal(new[] { "zelda one", "zelda two" }, results.Select(x => x.Name));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByScoreUsesTheScoreOfTheSubQuery()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        // every game is named 'zelda', so only the owner's score tells them apart
        var results = session.Advanced.RawQuery<Game>(
                "from index 'Games' as g " +
                "join (from index 'Users' where search(Name, 'chris bob') select id()) as u on g.OwnerId = id(u) " +
                "where search(g.Name, 'zelda') order by score()")
            .ToList();

        Assert.Equal(new[] { "users/strong" }, results.Select(x => x.OwnerId).Take(1));
        Assert.Equal("users/strong", results[0].OwnerId);
        Assert.Equal("users/weak", results[^1].OwnerId);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void LinqJoinKeepsOnlyGamesOfMatchingUsersAndSortsByCombinedScore()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var users = session.Query<User, UsersIndex>().Search(x => x.Name, "chris bob");

        var results = session.Query<Game, GamesIndex>()
            .Join(users, x => x.OwnerId)
            .Search(x => x.Name, "zelda")
            .OrderByScore()
            .ToList();

        Assert.Equal(new[] { "users/strong", "users/weak" }, results.Select(x => x.OwnerId));
        Assert.DoesNotContain(results, x => x.OwnerId == "users/other");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task AsyncLinqJoinWorks()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenAsyncSession();

        var users = session.Query<User, UsersIndex>().Search(x => x.Name, "chris bob");

        var results = await session.Query<Game, GamesIndex>()
            .Join(users, x => x.OwnerId)
            .Search(x => x.Name, "zelda")
            .OrderByScore()
            .ToListAsync();

        Assert.Equal(new[] { "users/strong", "users/weak" }, results.Select(x => x.OwnerId));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void QuerySyntaxJoinWorks()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var users = session.Query<User, UsersIndex>().Search(x => x.Name, "chris bob");
        var games = session.Query<Game, GamesIndex>().Search(x => x.Name, "zelda");

        var results = (from g in games
                       join u in users on g.OwnerId equals u.Id
                       select g)
            .OrderByScore()
            .ToList();

        Assert.Equal(new[] { "users/strong", "users/weak" }, results.Select(x => x.OwnerId));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task AsyncQuerySyntaxJoinWorks()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenAsyncSession();

        var users = session.Query<User, UsersIndex>().Search(x => x.Name, "chris bob");
        var games = session.Query<Game, GamesIndex>().Search(x => x.Name, "zelda");

        var results = await (from g in games
                             join u in users on g.OwnerId equals u.Id
                             select g)
            .OrderByScore()
            .ToListAsync();

        Assert.Equal(new[] { "users/strong", "users/weak" }, results.Select(x => x.OwnerId));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void QuerySyntaxJoinProjectsTheFieldsOfTheJoinedDocuments()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var users = session.Query<User, UsersIndex>();

        var expected = session.Advanced.RawQuery<OwnerRow>("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) order by g.Name select g.Name, u.Name as Owner")
            .ToList().Select(x => (x.Name, x.Owner)).ToList();
        var actual = (from g in session.Query<Game, GamesIndex>()
                      join u in users on g.OwnerId equals u.Id
                      orderby g.Name
                      select new { g.Name, Owner = u.Name }).ToList().Select(x => (x.Name, x.Owner)).ToList();

        Assert.NotEmpty(expected);
        Assert.All(expected, x => Assert.NotNull(x.Owner));
        Assert.Equal(expected, actual);
    }

    private sealed class OwnerRow
    {
        public string Name { get; set; }
        public string Owner { get; set; }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void DocumentQueryJoinWorks()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var users = session.Advanced.DocumentQuery<User, UsersIndex>().Search(x => x.Name, "chris bob");

        var results = session.Advanced.DocumentQuery<Game, GamesIndex>()
            .Join(users, x => x.OwnerId)
            .Search(x => x.Name, "zelda")
            .OrderByScore()
            .ToList();

        Assert.Equal(new[] { "users/strong", "users/weak" }, results.Select(x => x.OwnerId));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void SourceParametersAreRenamedSoTheyDoNotCollideWithTheOuterQuery()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        // both queries start numbering their parameters at p0
        var users = session.Advanced.DocumentQuery<User, UsersIndex>().Search(x => x.Name, "chris bob");
        var query = session.Advanced.DocumentQuery<Game, GamesIndex>()
            .Search(x => x.Name, "mario")
            .Join(users, x => x.OwnerId);

        var results = query.ToList();

        var game = Assert.Single(results);
        Assert.Equal("users/weak", game.OwnerId);
        Assert.Equal(2, query.GetIndexQuery().QueryParameters.Count);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void WithoutOrderByTheJoinScoreStillDecidesTheOrderButIsNotReturned()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var results = session.Advanced.RawQuery<Game>(
                "from index 'Games' as g " +
                "join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerId = id(u) " +
                "where search(g.Name, 'zelda')")
            .ToList();

        Assert.Equal(new[] { "users/strong", "users/weak" }, results.Select(x => x.OwnerId));
        Assert.All(results, game => Assert.False(session.Advanced.GetMetadataFor(game).ContainsKey(Constants.Documents.Metadata.IndexScore)));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ReturnedScoreIncludesTheScoreOfTheJoinedQuery()
    {
        using var store = GetStoreWithData(includeScores: true);

        // separate sessions, otherwise both queries would hand back the same tracked entity
        Dictionary<string, double> ScoresOf(string rql)
        {
            using var session = store.OpenSession();
            return session.Advanced.RawQuery<Game>(rql).ToList()
                .ToDictionary(game => game.Name, game => (double)session.Advanced.GetMetadataFor(game)[Constants.Documents.Metadata.IndexScore]);
        }

        var withJoin = ScoresOf("from index 'Games' as g " +
                                "join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerId = id(u) " +
                                "where search(g.Name, 'zelda') order by score()");
        var withoutJoin = ScoresOf("from index 'Games' as g where search(g.Name, 'zelda') order by score()");

        Assert.Equal(2, withJoin.Count);
        foreach (var (name, score) in withJoin)
            Assert.True(score > withoutJoin[name], $"{name}: {score} should be higher than {withoutJoin[name]}");

        // the owner that matches both search terms adds more than the one matching a single term
        Assert.True(withJoin["zelda two"] - withoutJoin["zelda two"] > withJoin["zelda one"] - withoutJoin["zelda one"]);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoinScoreDecidesWhichGamesMakeThePage()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        // every game matches 'zelda' equally, so only the score of the joined user can pick the first one
        var first = session.Advanced.RawQuery<Game>(
                "from index 'Games' as g " +
                "join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerId = id(u) " +
                "where search(g.Name, 'zelda') order by score()")
            .Take(1)
            .Single();

        Assert.Equal("users/strong", first.OwnerId);
    }

    private class GameWithOwner
    {
        public string Name { get; set; }
        public string Owner { get; set; }
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData("from index 'Games' as g join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerId = id(u) " +
                "where search(g.Name, 'zelda') order by score() select g.Name, u.Name as Owner")]
    public void JoinedDocumentFieldsCanBeProjected(string rql)
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var rows = session.Advanced.RawQuery<GameWithOwner>(rql).ToList();

        Assert.Equal(new[] { "zelda two/chris bob", "zelda one/chris" }, rows.Select(x => x.Name + "/" + x.Owner));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ExplicitLoadOfTheJoinAliasIsRespected()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var rows = session.Advanced.RawQuery<GameWithOwner>(
                "from index 'Games' as g join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerId = id(u) " +
                "where search(g.Name, 'zelda') order by score() load g.OwnerId as u select g.Name, u.Name as Owner")
            .ToList();

        Assert.Equal(new[] { "zelda two/chris bob", "zelda one/chris" }, rows.Select(x => x.Name + "/" + x.Owner));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void EveryEntryOfTheJoinedIndexCountsEvenBeyondTheOldTenThousandKeyLimit()
    {
        const int users = 25_000;
        using var store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);

        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < users; i++)
            {
                // the last user is the only one matching both search terms, so it has the best score
                bulk.Store(new User { Name = i == users - 1 ? "chris bob" : "chris" }, $"users/{i}");
                bulk.Store(new Game { Name = "zelda", OwnerId = $"users/{i}" }, $"games/{i}");
            }
        }

        Indexes.WaitForIndexing(store);

        using var session = store.OpenSession();
        var best = session.Advanced.RawQuery<Game>(
                "from index 'Games' as g join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerId = id(u) " +
                "where search(g.Name, 'zelda') order by score()")
            .Statistics(out var stats)
            .Take(3)
            .ToList();

        Assert.Equal(users, stats.TotalResults);
        Assert.Equal($"users/{users - 1}", best[0].OwnerId);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoinShowsUpInTheQueryTimings()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var users = session.Advanced.DocumentQuery<User, UsersIndex>().Search(x => x.Name, "chris bob");
        var results = session.Advanced.DocumentQuery<Game, GamesIndex>()
            .Join(users, x => x.OwnerId)
            .Timings(out var timings)
            .ToList();

        Assert.NotEmpty(results);
        Assert.True(timings.Timings.TryGetValue("Join", out var join), "the join should have its own timing scope");

        // every join has a scope of its own (named after its alias) with the scan of the joined index inside it
        var joined = Assert.Single(join.Timings).Value;
        Assert.True(joined.Timings.ContainsKey("Scan"), "the scan of the joined index should be timed inside the join");

        Assert.Equal(2, joined.Counters["SourceMatches"]); // users/strong and users/weak
        Assert.Equal(2, joined.Counters["OuterMatchKeys"]);
        Assert.True(joined.Counters["ScanAllocatedManagedBytes"] > 0);
        Assert.True(joined.Counters["OuterMatchAllocatedManagedBytes"] > 0);
        Assert.True(join.Counters["OuterQueryAllocatedManagedBytes"] >= joined.Counters["OuterMatchAllocatedManagedBytes"]); // the match is part of the outer query
        Assert.Equal(joined.Counters["ScanAllocatedManagedBytes"] + join.Counters["OuterQueryAllocatedManagedBytes"], join.Counters["TotalAllocatedManagedBytes"]);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoinWorksWhenShowingRawIndexEntries()
    {
        using var store = GetStoreWithData();
        using var commands = store.Commands();

        var rql = "from index 'Games' as g join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerId = id(u) where search(g.Name, 'zelda')";

        var documents = commands.Query(new IndexQuery { Query = rql });
        var entries = commands.Query(new IndexQuery { Query = rql }, indexEntriesOnly: true);

        // the same games as the query for documents, as index entries
        Assert.Equal(documents.Results.Length, entries.Results.Length);
        Assert.Equal(2, entries.Results.Length);
        Assert.All(entries.Results.Cast<BlittableJsonReaderObject>(), entry => Assert.True(entry.TryGet("OwnerId", out string owner) && owner is "users/strong" or "users/weak"));
    }

    private static async Task<(HttpStatusCode Status, string ETag, string Body)> PostQuery(IDocumentStore store, string rql, string ifNoneMatch = null, string debug = null)
    {
        using var http = new System.Net.Http.HttpClient();
        var url = $"{store.Urls[0]}/databases/{store.Database}/queries?queryStats=true" + (debug != null ? $"&debug={debug}" : "");
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { Query = rql, QueryParameters = new { } }), Encoding.UTF8, "application/json")
        };

        if (ifNoneMatch != null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

        using var response = await http.SendAsync(request);

        // the server sends the etag as a bare number, which the typed ETag header property refuses
        var etag = response.Headers.NonValidated.TryGetValues("ETag", out var values) ? values.First() : null;
        return (response.StatusCode, etag, await response.Content.ReadAsStringAsync());
    }

    private class LuceneGamesIndex : AbstractIndexCreationTask<Game>
    {
        public LuceneGamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId };
            Index(x => x.Name, FieldIndexing.Search);
            Configuration["Indexing.Static.SearchEngineType"] = "Lucene";
        }

        public override string IndexName => "GamesLucene";
    }

    private const string ZeldaGamesOfChrisAndBob =
        "from index 'Games' as g join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerId = id(u) where search(g.Name, 'zelda')";

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void StreamingAJoinReturnsTheJoinedResult()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var streamed = new List<string>();
        using var stream = session.Advanced.Stream<Game>(session.Advanced.RawQuery<Game>(ZeldaGamesOfChrisAndBob));
        while (stream.MoveNext())
            streamed.Add(stream.Current.Document.Name);

        Assert.Equal(new[] { "zelda one", "zelda two" }, streamed.OrderBy(x => x));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void DeleteByQueryWithAJoinDeletesOnlyTheJoinedDocuments()
    {
        using var store = GetStoreWithData();

        store.Operations.Send(new DeleteByQueryOperation(new IndexQuery { Query = ZeldaGamesOfChrisAndBob })).WaitForCompletion();

        using var session = store.OpenSession();
        var left = session.Query<Game>().Customize(x => x.WaitForNonStaleResults()).Select(x => x.Name).ToList();
        Assert.Equal(new[] { "mario one", "zelda three" }, left.OrderBy(x => x));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoinOnALuceneIndexIsRejectedInsteadOfBeingIgnored()
    {
        using var store = GetStoreWithData();
        new LuceneGamesIndex().Execute(store);
        Indexes.WaitForIndexing(store);
        Assert.Equal(SearchEngineType.Lucene, store.Maintenance.Send(new GetIndexStatisticsOperation("GamesLucene")).SearchEngineType);

        using var session = store.OpenSession();

        // the same query without the join returns 3 games: silently dropping the join would answer that
        var e = Assert.ThrowsAny<System.Exception>(() => session.Advanced.RawQuery<Game>(ZeldaGamesOfChrisAndBob.Replace("'Games'", "'GamesLucene'")).ToList());
        Assert.Contains("JOIN is supported only for Corax indexes", e.ToString());
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.Sharding)]
    [InlineData("")]
    [InlineData(" order by g.Name")]
    [InlineData(" order by score()")]
    public void JoinIsRejectedInAShardedDatabaseInsteadOfJoiningEachShardOnItsOwn(string orderBy)
    {
        var options = Options.ForMode(RavenDatabaseMode.Sharded);
        var shardedSetup = options.ModifyDatabaseRecord;
        options.ModifyDatabaseRecord = record =>
        {
            shardedSetup?.Invoke(record);
            record.Settings[RavenConfiguration.GetKey(x => x.Indexing.StaticIndexingEngineType)] = "Corax";
            record.Settings[RavenConfiguration.GetKey(x => x.Indexing.AutoIndexingEngineType)] = "Corax";
        };

        using var store = GetDocumentStore(options);
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < 100; i++)
            {
                bulk.Store(new User { Name = i % 2 == 0 ? "chris bob" : "alice" }, $"users/{i}");
                bulk.Store(new Game { Name = "zelda", OwnerId = $"users/{(i * 7 + 3) % 100}" }, $"games/{i}");
            }
        }

        Indexes.WaitForIndexing(store);

        // every shard would join with the users it holds itself
        using var session = store.OpenSession();
        var e = Assert.ThrowsAny<System.Exception>(() => session.Advanced.RawQuery<Game>(ZeldaGamesOfChrisAndBob + orderBy + " limit 0, 1000").ToList());
        Assert.Contains("JOIN is not supported in sharding", e.ToString());
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoinOnACollectionQueryIsRejectedAndNothingIsDeletedOrPatched()
    {
        using var store = GetStoreWithData();
        const string collectionJoin = "from Games as g join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerId = id(u)";

        // a delete or patch by query must not drop the join: it would remove every game
        var delete = Assert.ThrowsAny<System.Exception>(() => store.Operations.Send(new DeleteByQueryOperation(new IndexQuery { Query = collectionJoin })).WaitForCompletion());
        Assert.Contains("JOIN is supported only for queries on an index", delete.ToString());

        var patch = Assert.ThrowsAny<System.Exception>(() => store.Operations.Send(new PatchByQueryOperation(new IndexQuery { Query = collectionJoin + " update { this.Patched = true; }" })).WaitForCompletion());
        Assert.Contains("JOIN is supported only for queries on an index", patch.ToString());

        using (var session = store.OpenSession())
        {
            var query = Assert.ThrowsAny<System.Exception>(() => session.Advanced.RawQuery<Game>(collectionJoin).ToList());
            Assert.Contains("JOIN is supported only for queries on an index", query.ToString());

            Assert.Equal(4, session.Query<Game>().Customize(x => x.WaitForNonStaleResults()).Count());
            Assert.DoesNotContain(session.Query<Game>().ToList(), x => session.Advanced.GetMetadataFor(x).ContainsKey("Patched"));
        }

        var subscription = Assert.ThrowsAny<System.Exception>(() => store.Subscriptions.Create(new SubscriptionCreationOptions { Query = collectionJoin }));
        Assert.Contains("Subscription cannot specify a join", subscription.ToString());
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheSubQueryOfAJoinMustReadAnIndex()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var e = Assert.ThrowsAny<System.Exception>(() => session.Advanced.RawQuery<Game>(
                "from index 'Games' as g join (from Users where Name = 'chris') as u on g.OwnerId = id(u)")
            .ToList());

        Assert.Contains("must query an index", e.ToString());
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void FacetsAreComputedOverTheJoinedResult()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        // 'zelda' games: zelda one, zelda two, zelda three
        var users = session.Query<User, UsersIndex>().Search(x => x.Name, "chris bob");
        var facets = session.Query<Game, GamesIndex>()
            .Search(x => x.Name, "zelda")
            .Join(users, x => x.OwnerId)
            .AggregateBy(builder => builder.ByField(x => x.OwnerId))
            .Execute();

        var counts = facets["OwnerId"].Values.ToDictionary(x => x.Range, x => x.Count);
        Assert.Equal(new Dictionary<string, int> { ["users/strong"] = 1, ["users/weak"] = 1 }, counts);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task FacetsOverAJoinAreCachedUntilSomethingTheyJoinWithChanges()
    {
        using var store = GetStoreWithData();
        const string rql = "from index 'Games' as g join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerId = id(u) where search(g.Name, 'zelda') select facet(OwnerId)";

        var first = await PostQuery(store, rql);
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.NotModified, (await PostQuery(store, rql, first.ETag)).Status);

        using (var session = store.OpenSession())
        {
            session.Load<User>("users/strong").Name = "someone else";
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        var afterJoinedChange = await PostQuery(store, rql, first.ETag);
        Assert.Equal(HttpStatusCode.OK, afterJoinedChange.Status);
        Assert.NotEqual(first.Body, afterJoinedChange.Body);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoinInASuggestionQueryFailsWithAClearMessage()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var e = Assert.ThrowsAny<System.Exception>(() => session.Advanced.RawQuery<Game>(
                "from index 'Games' as g join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerId = id(u) select suggest(Name, 'zelda')")
            .ToList());

        Assert.DoesNotContain("This is a bug", e.ToString());
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheResultIsStaleWhenAJoinedIndexIsStale()
    {
        using var store = GetStoreWithData();

        QueryStatistics StatisticsOfTheJoin()
        {
            using var session = store.OpenSession();
            var users = session.Advanced.DocumentQuery<User, UsersIndex>().Search(x => x.Name, "chris bob");
            session.Advanced.DocumentQuery<Game, GamesIndex>().Join(users, x => x.OwnerId).Statistics(out var stats).ToList();
            return stats;
        }

        Assert.False(StatisticsOfTheJoin().IsStale);

        // the joined index stops and falls behind, the main index (Games) is up to date
        store.Maintenance.Send(new StopIndexOperation("Users"));
        using (var session = store.OpenSession())
        {
            session.Load<User>("users/strong").Name = "someone else";
            session.SaveChanges();
        }

        Assert.True(StatisticsOfTheJoin().IsStale, "the result depends on the Users index, which is behind");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void WaitingForNonStaleResultsAlsoWaitsForTheJoinedIndex()
    {
        using var store = GetStoreWithData();
        store.Maintenance.Send(new StopIndexOperation("Users"));
        using (var session = store.OpenSession())
        {
            session.Load<User>("users/strong").Name = "someone else";
            session.SaveChanges();
        }

        IDocumentQuery<Game> Join(IDocumentSession session)
        {
            var users = session.Advanced.DocumentQuery<User, UsersIndex>().Search(x => x.Name, "chris bob");
            return session.Advanced.DocumentQuery<Game, GamesIndex>().Join(users, x => x.OwnerId);
        }

        // the joined index is behind and does not move: waiting for non-stale results times out
        using (var session = store.OpenSession())
        {
            Assert.Throws<System.TimeoutException>(() => Join(session).WaitForNonStaleResults(System.TimeSpan.FromMilliseconds(300)).ToList());
        }

        // ... and completes as soon as it has caught up, with the data it caught up with
        var restart = Task.Run(async () =>
        {
            await Task.Delay(700);
            store.Maintenance.Send(new StartIndexOperation("Users"));
        });

        using (var session = store.OpenSession())
        {
            var results = Join(session).WaitForNonStaleResults(System.TimeSpan.FromSeconds(30)).Statistics(out var stats).ToList();

            Assert.False(stats.IsStale);
            Assert.DoesNotContain(results, x => x.OwnerId == "users/strong"); // the rename is in the result
            Assert.NotEmpty(results);
        }

        restart.Wait();
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ClientRendersAnAliasForTheMainDocument()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var users = session.Advanced.DocumentQuery<User, UsersIndex>().Search(x => x.Name, "chris bob");
        var query = session.Advanced.DocumentQuery<Game, GamesIndex>()
            .Search(x => x.Name, "zelda")
            .Join(users, x => x.OwnerId)
            .OrderByScore();

        var rql = query.ToString();

        Assert.StartsWith("from index 'Games' as __alias_join join (", rql);
        Assert.Contains("on __alias_join.OwnerId = id(j)", rql);
        Assert.Contains("search(__alias_join.Name,", rql);
        Assert.Equal(rql, query.ToString()); // rendering does not change the query
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoinOnADynamicQueryIsRejected()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<Game>(
                "from Games as g join (from index 'Users' where search(Name, 'chris bob') select id()) as u on g.OwnerId = id(u)")
            .ToList());
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoiningOnTheIdPropertiesMatchesByIdInsteadOfByAField()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        var users = session.Advanced.DocumentQuery<User, UsersIndex>().Search(x => x.Name, "chris bob");
        var query = session.Advanced.DocumentQuery<Game, GamesIndex>()
            .Join(users, x => x.OwnerId, x => x.Id);

        Assert.Contains("on __alias_join.OwnerId = id(j)", query.ToString());

        var withIdAsOuterKey = session.Advanced.DocumentQuery<Game, GamesIndex>()
            .Join(users, x => x.Id, x => x.Id);

        Assert.Contains("on id(__alias_join) = id(j)", withIdAsOuterKey.ToString());
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheAliasOfTheJoinedQueryNeverCollidesWithTheAliasOfTheMainDocument()
    {
        using var store = GetStoreWithData();
        using var session = store.OpenSession();

        // the lambda parameter of the projection becomes the alias of the main document
        var users = session.Query<User, UsersIndex>().Search(x => x.Name, "chris bob");
        var names = session.Query<Game, GamesIndex>()
            .Join(users, g => g.OwnerId)
            .Select(j => new { j.Name })
            .ToList()
            .Select(x => x.Name)
            .OrderBy(x => x);

        Assert.Equal(new[] { "mario one", "zelda one", "zelda two" }, names);
    }

    private IDocumentStore GetStoreWithData(bool includeScores = false)
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        if (includeScores)
            options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();

        var store = GetDocumentStore(options);
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);

        using (var session = store.OpenSession())
        {
            session.Store(new User { Name = "chris bob" }, "users/strong"); // matches both search terms
            session.Store(new User { Name = "chris" }, "users/weak"); // matches one
            session.Store(new User { Name = "alice" }, "users/other");

            // the game of the lower scoring owner is stored first, so a tie would put it first
            session.Store(new Game { Name = "zelda one", OwnerId = "users/weak" });
            session.Store(new Game { Name = "zelda two", OwnerId = "users/strong" });
            session.Store(new Game { Name = "mario one", OwnerId = "users/weak" });
            session.Store(new Game { Name = "zelda three", OwnerId = "users/other" });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
        return store;
    }

    private class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    private class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
    }

    private class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name };
            Index(x => x.Name, FieldIndexing.Search);
            Analyzers.Add(x => x.Name, "StandardAnalyzer");
        }

        public override string IndexName => "Users";
    }

    private class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
