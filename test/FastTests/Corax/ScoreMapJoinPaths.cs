using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Exceptions.Corax;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// Execution paths of the JOIN that the other join tests do not cover
public class ScoreMapJoinPaths(ITestOutputHelper output) : RavenTestBase(output)
{
    private const double Tolerance = 1e-4;
    private const string Everything = "limit 0, 1000000";
    private const string Main = "from index 'Games' as g";
    private const string JoinStrong = "join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerId = id(u)";


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ManyGamesOfOneOwnerAllGetTheScoreOfThatOwnerAddedOnce()
    {
        using var store = CreateStore();
        using (var session = store.OpenSession())
        {
            session.Store(new User { Id = "users/strong", Name = "chris bob" });
            session.Store(new User { Id = "users/weak", Name = "chris" });
            for (var i = 0; i < 5; i++)
                session.Store(new Game { Id = $"games/weak-{i}", Name = "zelda", OwnerId = "users/weak", Rank = i });
            for (var i = 0; i < 5; i++)
                session.Store(new Game { Id = $"games/strong-{i}", Name = "zelda", OwnerId = "users/strong", Rank = 10 + i });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        var users = Fetch<User>(store, $"from index 'Users' where search(Name, 'chris bob') order by score() {Everything}").Rows.ToDictionary(x => x.Id, x => x.Score);
        var outer = Fetch<Game>(store, $"{Main} where search(g.Name, 'zelda') order by score() {Everything}").Rows.ToDictionary(x => x.Id, x => x.Score);
        Assert.True(users["users/strong"] > users["users/weak"] + 0.01);

        var joined = Fetch<Game>(store, $"{Main} {JoinStrong} where search(g.Name, 'zelda') order by score() {Everything}");
        Assert.Equal(10, joined.Total);
        Assert.Equal(10, joined.Rows.Count);

        foreach (var row in joined.Rows)
        {
            var owner = row.Entity.OwnerId;
            Assert.Equal(outer[row.Id] + users[owner], row.Score, Tolerance);
        }

        // the five games of the strong owner come first, whatever the order they were stored in
        Assert.All(joined.Rows.Take(5), x => Assert.Equal("users/strong", x.Entity.OwnerId));
        Assert.All(joined.Rows.Skip(5), x => Assert.Equal("users/weak", x.Entity.OwnerId));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AnEmptyJoinedQueryGivesNoRowsWhateverTheOrderAndThePage()
    {
        using var store = CreateStoreWithGames(10);

        foreach (var order in new[] { "", "order by score()", "order by score() desc", "order by g.Rank as long", "order by g.Rank as long desc" })
        {
            foreach (var page in new[] { "limit 0, 10", "limit 3, 5", Everything })
            {
                var result = Fetch<Game>(store, $"{Main} join (from index 'Users' where search(Name, 'nobody-has-this-name')) as u on g.OwnerId = id(u) {order} {page}");
                Assert.True(result.Rows.Count == 0, $"[{order}] [{page}] returned {result.Rows.Count} rows");
                Assert.True(result.Total == 0, $"[{order}] [{page}] total is {result.Total}");
            }
        }

        // a chain whose last link is empty empties the whole result
        var chain = Fetch<Game>(store, $"{Main} {JoinStrong.Replace("as u on", "as u on")} join (from index 'Users' where search(Name, 'nobody')) as c on u.Number = id(c) order by score() {Everything}");
        Assert.Empty(chain.Rows);
        Assert.Equal(0, chain.Total);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void SkipAndTakeWalkThroughTheWholeJoinedResultOnEveryOrder()
    {
        using var store = CreateStoreWithGames(23);

        var all = new[]
        {
            (Order: "order by score()", Key: (Func<Row<Game>, IComparable>)(x => -x.Score)),
            (Order: "order by g.Rank as long", Key: x => x.Entity.Rank),
            (Order: "order by g.Rank as long desc", Key: x => -x.Entity.Rank),
            (Order: "", Key: null)
        };

        foreach (var (order, key) in all)
        {
            var full = Fetch<Game>(store, $"{Main} {JoinStrong} {order} {Everything}");
            Assert.True(full.Total > 6, "the probe needs more than a few matches");
            Assert.Equal(full.Total, full.Rows.Count);

            var walked = new List<string>();
            const int pageSize = 4;
            for (var skip = 0; skip < full.Total + pageSize; skip += pageSize)
            {
                var page = Fetch<Game>(store, $"{Main} {JoinStrong} {order} limit {skip}, {pageSize}");
                Assert.True(page.Total == full.Total, $"[{order}] skip {skip}: total {page.Total} instead of {full.Total}");
                Assert.True(page.Rows.Count <= pageSize);
                if (skip >= full.Total)
                    Assert.Empty(page.Rows);

                walked.AddRange(page.Rows.Select(x => x.Id));
            }

            Assert.True(walked.Count == walked.Distinct().Count(), $"[{order}] a row is on two pages");
            Assert.True(full.Rows.Select(x => x.Id).OrderBy(x => x).SequenceEqual(walked.OrderBy(x => x)), $"[{order}] the pages do not add up to the whole result");

            // an explicit order has to be the same on every page, the score order only up to ties
            if (order.Contains("Rank"))
                Assert.Equal(full.Rows.Select(x => x.Id), walked);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByFieldWithAJoinReturnsEveryMatchingRowNotJustOne()
    {
        using var store = CreateStoreWithGames(12);

        foreach (var order in new[] { "order by g.Rank as long", "order by g.Rank as long desc", "order by g.Name", "order by g.Name desc", "order by g.Rank as long, score()", "order by score(), g.Rank as long" })
        {
            var plain = Fetch<Game>(store, $"{Main} where search(g.Name, 'zelda') {Everything}");
            var joined = Fetch<Game>(store, $"{Main} {JoinStrong} where search(g.Name, 'zelda') {order} {Everything}");
            var expected = plain.Rows.Where(x => x.Entity.OwnerId is "users/strong" or "users/weak").Select(x => x.Id).OrderBy(x => x).ToList();

            Assert.True(expected.Count > 1);
            Assert.True(expected.SequenceEqual(joined.Rows.Select(x => x.Id).OrderBy(x => x)), $"[{order}] wrong rows: {string.Join(",", joined.Rows.Select(x => x.Id))}");
            Assert.Equal(expected.Count, joined.Total);
        }
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void IncludesOfAJoinedQueryAreSentWithTheResult()
    {
        using var store = CreateStoreWithGames(6);

        using var session = store.OpenSession();
        var games = session.Advanced.RawQuery<Game>($"{Main} {JoinStrong} where search(g.Name, 'zelda') order by score() include g.OwnerId").ToList();
        Assert.NotEmpty(games);

        var requests = session.Advanced.NumberOfRequests;
        foreach (var game in games)
            Assert.NotNull(session.Load<User>(game.OwnerId));

        Assert.Equal(requests, session.Advanced.NumberOfRequests); // all served from the session: the included owners came with the query
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void HighlightingsOfAJoinedQueryAreReturnedForTheMatchingRows()
    {
        using var store = CreateStoreWithGames(6);

        using var session = store.OpenSession();
        var users = session.Advanced.DocumentQuery<User, UsersIndex>().Search(x => x.Name, "chris bob");
        var games = session.Advanced.DocumentQuery<Game, GamesIndex>()
            .Join(users, x => x.OwnerId)
            .Search(x => x.Name, "zelda")
            .Highlight("Name", 64, 2, out var highlightings)
            .ToList();

        Assert.NotEmpty(games);
        Assert.All(games, g => Assert.NotEmpty(highlightings.GetFragments(session.Advanced.GetDocumentId(g))));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ExplanationsOfAJoinedQueryFailWithTheSameClearMessageAsWithoutAJoin()
    {
        using var store = CreateStoreWithGames(6);

        using var session = store.OpenSession();
        var users = session.Advanced.DocumentQuery<User, UsersIndex>().Search(x => x.Name, "chris bob");
        var e = Assert.Throws<NotSupportedInCoraxException>(() => session.Advanced.DocumentQuery<Game, GamesIndex>()
            .Join(users, x => x.OwnerId)
            .Search(x => x.Name, "zelda")
            .IncludeExplanations(out _)
            .ToList());

        Assert.Contains("Explanations", e.Message);
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AMultiMapIndexCanBeTheJoinedSide()
    {
        using var store = CreateStore();
        new PeopleIndex().Execute(store);
        using (var session = store.OpenSession())
        {
            // 'chris bob' scores higher than 'chris'; the matches come from two collections
            session.Store(new User { Id = "users/strong", Name = "chris bob" });
            session.Store(new Bot { Id = "bots/weak", Name = "chris" });
            session.Store(new Bot { Id = "bots/other", Name = "alice" });
            session.Store(new Game { Id = "games/weak", Name = "zelda", OwnerId = "bots/weak", Rank = 1 });
            session.Store(new Game { Id = "games/strong", Name = "zelda", OwnerId = "users/strong", Rank = 2 });
            session.Store(new Game { Id = "games/other", Name = "zelda", OwnerId = "bots/other", Rank = 3 });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        var people = Fetch<object>(store, $"from index 'People' where search(Name, 'chris bob') order by score() {Everything}").Rows.ToDictionary(x => x.Id, x => x.Score);
        Assert.Equal(new[] { "bots/weak", "users/strong" }, people.Keys.OrderBy(x => x).ToArray());

        var outer = Fetch<Game>(store, $"{Main} where search(g.Name, 'zelda') order by score() {Everything}").Rows.ToDictionary(x => x.Id, x => x.Score);
        var joined = Fetch<Game>(store, $"{Main} join (from index 'People' where search(Name, 'chris bob')) as p on g.OwnerId = id(p) where search(g.Name, 'zelda') order by score() {Everything}");

        Assert.Equal(new[] { "games/strong", "games/weak" }, joined.Rows.Select(x => x.Id).ToArray());
        Assert.Equal(2, joined.Total);
        Assert.Equal(outer["games/strong"] + people["users/strong"], joined.Rows[0].Score, Tolerance);
        Assert.Equal(outer["games/weak"] + people["bots/weak"], joined.Rows[1].Score, Tolerance);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AMapReduceIndexCanBeTheMainSideAndTheJoinedSide()
    {
        using var store = CreateStore();
        new OwnerStatsIndex().Execute(store);
        using (var session = store.OpenSession())
        {
            session.Store(new User { Id = "users/strong", Name = "chris bob" });
            session.Store(new User { Id = "users/weak", Name = "chris" });
            session.Store(new User { Id = "users/other", Name = "alice" });
            for (var i = 0; i < 3; i++)
                session.Store(new Game { Id = $"games/strong-{i}", Name = "zelda", OwnerId = "users/strong", Rank = i });
            for (var i = 0; i < 2; i++)
                session.Store(new Game { Id = $"games/weak-{i}", Name = "zelda", OwnerId = "users/weak", Rank = 10 + i });
            session.Store(new Game { Id = "games/other-0", Name = "zelda", OwnerId = "users/other", Rank = 20 });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        // main side: the reduce results (one per owner) joined with the users
        var outer = Raw<OwnerStats>(store, $"from index 'OwnerStats' as s join (from index 'Users' where search(Name, 'chris bob')) as u on s.OwnerId = id(u) order by s.Count as long desc {Everything}");
        Assert.Equal(new[] { ("users/strong", 3L), ("users/weak", 2L) }, outer.Select(x => (x.OwnerId, x.Count)));

        using (var session = store.OpenSession())
        {
            session.Advanced.RawQuery<OwnerStats>($"from index 'OwnerStats' as s join (from index 'Users' where search(Name, 'chris bob')) as u on s.OwnerId = id(u) {Everything}")
                .Statistics(out var stats).ToList();
            Assert.Equal(2, stats.TotalResults);
        }

        // joined side: games whose owner has at least two games (field join on the reduce key)
        var joined = Fetch<Game>(store, $"{Main} join (from index 'OwnerStats' where Count >= 2) as s on g.OwnerId = s.OwnerId order by g.Rank as long {Everything}");
        Assert.Equal(5, joined.Total);
        Assert.Equal(new[] { "games/strong-0", "games/strong-1", "games/strong-2", "games/weak-0", "games/weak-1" }, joined.Rows.Select(x => x.Id).ToArray());
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ProjectingTheFieldsOfChainedAndFieldJoinedAliasesLoadsThemImplicitly()
    {
        using var store = CreateStore();
        new CompaniesIndex().Execute(store);
        using (var session = store.OpenSession())
        {
            session.Store(new Company { Id = "companies/1", Name = "acme", Country = "nl" });
            session.Store(new Company { Id = "companies/2", Name = "globex", Country = "be" });
            session.Store(new User { Id = "users/1", Name = "chris bob", CompanyId = "companies/1", City = "Utrecht" });
            session.Store(new User { Id = "users/2", Name = "chris bob", CompanyId = "companies/2", City = "Gent" });
            session.Store(new Game { Id = "games/1", Name = "zelda", OwnerId = "users/1", Country = "nl", Rank = 1 });
            session.Store(new Game { Id = "games/2", Name = "zelda", OwnerId = "users/2", Country = "be", Rank = 2 });
            session.Store(new Game { Id = "games/3", Name = "zelda", OwnerId = "users/missing", Country = "nl", Rank = 3 });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        const string join = "join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerId = id(u) join (from index 'Companies') as c on u.CompanyId = id(c)";

        // 'c' is only reachable through 'u', and 'u.City' is not indexed: the document has to be read
        var chain = Raw<Projected>(store, $"{Main} {join} order by g.Rank as long select g.Name as Game, u.City as City, c.Name as Company {Everything}");
        Assert.Equal(new[] { ("zelda", "Utrecht", "acme"), ("zelda", "Gent", "globex") }, chain.Select(x => (x.Game, x.City, x.Company)));

        // 'c' alone: the document of 'u' is not selected but is still the way to reach 'c'
        var chainOnly = Raw<Projected>(store, $"{Main} {join} order by g.Rank as long select c.Name as Company {Everything}");
        Assert.Equal(new[] { "acme", "globex" }, chainOnly.Select(x => x.Company));

        // a field-joined alias: the country of the game joins the companies of that country, which the games do not reference
        var byField = Raw<Projected>(store,
            $"{Main} join (from index 'Companies') as c on g.Country = c.Country order by g.Rank as long select g.Id as Id, c.Name as Company {Everything}");
        Assert.Equal(new[] { ("games/1", "acme"), ("games/2", "globex"), ("games/3", "acme") }, byField.Select(x => (x.Id, x.Company)));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ExplicitLoadsCanBeCombinedWithAJoinAndAChainedJoin()
    {
        using var store = CreateStore();
        new CompaniesIndex().Execute(store);
        using (var session = store.OpenSession())
        {
            session.Store(new Company { Id = "companies/1", Name = "acme" });
            session.Store(new Company { Id = "companies/2", Name = "globex" });
            session.Store(new User { Id = "users/1", Name = "chris bob", CompanyId = "companies/1" });
            session.Store(new Game { Id = "games/1", Name = "zelda", OwnerId = "users/1", PublisherId = "companies/2", Rank = 1 });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        // 'p' is loaded by an explicit load clause of the main document, 'u' comes from the join
        var rows = Raw<Projected>(store,
            $"{Main} {JoinStrong} load g.PublisherId as p select g.Id as Id, u.Name as Owner, p.Name as Publisher {Everything}");

        var row = Assert.Single(rows);
        Assert.Equal("games/1", row.Id);
        Assert.Equal("chris bob", row.Owner);
        Assert.Equal("globex", row.Publisher);

        // the document of a joined alias is reached with a chained join, not with a load clause
        var chained = Raw<Projected>(store,
            $"{Main} {JoinStrong} join (from index 'Companies') as c on u.CompanyId = id(c) load g.PublisherId as p select u.Name as Owner, p.Name as Publisher, c.Name as Company {Everything}");

        var chainedRow = Assert.Single(chained);
        Assert.Equal(("chris bob", "globex", "acme"), (chainedRow.Owner, chainedRow.Publisher, chainedRow.Company));
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void UpdatedAndDeletedDocumentsChangeTheJoinedResultAfterTheIndexesCaughtUp()
    {
        using var store = CreateStoreWithGames(0);
        using (var session = store.OpenSession())
        {
            session.Store(new User { Id = "users/a", Name = "chris bob" });
            session.Store(new User { Id = "users/b", Name = "chris bob" });
            session.Store(new User { Id = "users/c", Name = "chris bob" });
            session.Store(new Game { Id = "games/a", Name = "zelda", OwnerId = "users/a", Rank = 1 });
            session.Store(new Game { Id = "games/b", Name = "zelda", OwnerId = "users/b", Rank = 2 });
            session.Store(new Game { Id = "games/c", Name = "zelda", OwnerId = "users/c", Rank = 3 });
            session.Store(new Game { Id = "games/d", Name = "zelda", OwnerId = "users/c", Rank = 4 });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        string[] Ids() => Fetch<Game>(store, $"{Main} {JoinStrong} order by g.Rank as long {Everything}").Rows.Select(x => x.Id).ToArray();

        Assert.Equal(new[] { "games/a", "games/b", "games/c", "games/d" }, Ids());

        // a joined document stops matching the sub-query
        using (var session = store.OpenSession())
        {
            session.Load<User>("users/a").Name = "alice";
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
        Assert.Equal(new[] { "games/b", "games/c", "games/d" }, Ids());

        // a joined document is deleted
        using (var session = store.OpenSession())
        {
            session.Delete("users/b");
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
        Assert.Equal(new[] { "games/c", "games/d" }, Ids());

        // an outer document is deleted, and one is re-pointed at another owner
        using (var session = store.OpenSession())
        {
            session.Delete("games/c");
            session.Load<Game>("games/d").OwnerId = "users/a";
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
        Assert.Empty(Ids());
        Assert.Equal(0, Fetch<Game>(store, $"{Main} {JoinStrong} {Everything}").Total);

        // and a joined document that matches again brings its games back, including the re-pointed one
        using (var session = store.OpenSession())
        {
            session.Load<User>("users/a").Name = "chris bob";
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
        Assert.Equal(new[] { "games/a", "games/d" }, Ids()); // games/a still points at users/a
    }


    private IDocumentStore CreateStoreWithGames(int count)
    {
        var store = CreateStore();
        using var session = store.OpenSession();
        session.Store(new User { Id = "users/strong", Name = "chris bob" });
        session.Store(new User { Id = "users/weak", Name = "chris" });
        session.Store(new User { Id = "users/other", Name = "alice" });
        var owners = new[] { "users/weak", "users/other", "users/strong", "users/missing" };
        for (var i = 0; i < count; i++)
            session.Store(new Game { Id = $"games/{i:D3}", Name = "zelda adventure", OwnerId = owners[i % owners.Length], Rank = i });

        session.SaveChanges();
        Indexes.WaitForIndexing(store);
        return store;
    }

    private IDocumentStore CreateStore()
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();

        var store = GetDocumentStore(options);
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);
        return store;
    }

    private static List<T> Raw<T>(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<T>(rql).ToList();
    }

    private sealed class Row<T>
    {
        public string Id;
        public double Score;
        public T Entity;
    }

    private sealed class Page<T>
    {
        public List<Row<T>> Rows;
        public long Total;
    }

    private static Page<T> Fetch<T>(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        var entities = session.Advanced.RawQuery<T>(rql).Statistics(out var stats).ToList();
        var rows = new List<Row<T>>();
        foreach (var entity in entities)
        {
            var metadata = session.Advanced.GetMetadataFor(entity);
            rows.Add(new Row<T>
            {
                Id = (string)metadata[Constants.Documents.Metadata.Id],
                Score = metadata.ContainsKey(Constants.Documents.Metadata.IndexScore) ? Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore]) : double.NaN,
                Entity = entity
            });
        }

        return new Page<T> { Rows = rows, Total = stats.TotalResults };
    }

    private class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string City { get; set; }
        public string CompanyId { get; set; }
        public long Number { get; set; }
    }

    private class Bot
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    private class Company
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Country { get; set; }
    }

    private class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string PublisherId { get; set; }
        public string Country { get; set; }
        public long Rank { get; set; }
    }

    private class OwnerStats
    {
        public string OwnerId { get; set; }
        public long Count { get; set; }
    }

    private class Projected
    {
        public string Id { get; set; }
        public string Game { get; set; }
        public string Owner { get; set; }
        public string Publisher { get; set; }
        public string Company { get; set; }
        public string City { get; set; }
    }

    private class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Number, u.CompanyId };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    private class CompaniesIndex : AbstractIndexCreationTask<Company>
    {
        public CompaniesIndex()
        {
            Map = companies => from c in companies select new { c.Name, c.Country };
        }

        public override string IndexName => "Companies";
    }

    private class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.PublisherId, g.Country, g.Rank };
            Index(x => x.Name, FieldIndexing.Search);
            Store(x => x.Name, FieldStorage.Yes);
            TermVector(x => x.Name, FieldTermVector.WithPositionsAndOffsets);
        }

        public override string IndexName => "Games";
    }

    private class PeopleIndex : AbstractMultiMapIndexCreationTask
    {
        public PeopleIndex()
        {
            AddMap<User>(users => from u in users select new { u.Name });
            AddMap<Bot>(bots => from b in bots select new { b.Name });
            Index("Name", FieldIndexing.Search);
        }

        public override string IndexName => "People";
    }

    private class OwnerStatsIndex : AbstractIndexCreationTask<Game, OwnerStats>
    {
        public OwnerStatsIndex()
        {
            Map = games => from g in games select new { g.OwnerId, Count = 1L };
            Reduce = results => from r in results group r by r.OwnerId into g select new { OwnerId = g.Key, Count = g.Sum(x => x.Count) };
        }

        public override string IndexName => "OwnerStats";
    }
}
