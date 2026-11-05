using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// Edge cases of the JOIN, all proven against an independent oracle
public class ScoreMapJoinEdgeCases(ITestOutputHelper output) : RavenTestBase(output)
{
    private const double Tolerance = 1e-4;
    private const string Everything = "limit 0, 1000000";


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OuterDocumentsWithANullOrMissingJoinFieldNeverMatchOnTheId()
    {
        using var store = CreateStoreWithNullsAndMissingFields(out var users, out var games);

        const string query = "from index 'Games' as g join (from index 'Users' where search(Name, 'chris')) as u on g.OwnerId = id(u) where search(g.Name, 'zelda')";

        var userScores = Fetch<User>(store, $"from index 'Users' where search(Name, 'chris') order by score() {Everything}").Rows.ToDictionary(x => x.Id, x => x.Score);
        var outer = Fetch<Game>(store, $"from index 'Games' as g where search(g.Name, 'zelda') order by score() {Everything}").Rows;
        var expected = Combine(outer, x => new[] { x.Entity.OwnerId }, userScores);

        Assert.Equal(new[] { "games/ok" }, expected.Keys.ToArray()); // the probe itself: only one game has a usable owner
        AssertJoin(store, query, expected, RanksOf(outer), "null/missing OwnerId");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OuterDocumentsWithANullOrMissingJoinFieldNeverMatchOnAField()
    {
        using var store = CreateStoreWithNullsAndMissingFields(out var users, out var games);

        // users/nullcountry has no country at all: a null on both sides must not count as equal
        const string query = "from index 'Games' as g join (from index 'Users' where search(Name, 'chris')) as u on g.Country = u.Country where search(g.Name, 'zelda')";

        var userRows = Fetch<User>(store, $"from index 'Users' where search(Name, 'chris') order by score() {Everything}").Rows;
        var outer = Fetch<Game>(store, $"from index 'Games' as g where search(g.Name, 'zelda') order by score() {Everything}").Rows;
        var expected = Combine(outer, x => new[] { x.Entity.Country }, BestByValue(userRows, x => x.Entity.Countries));

        Assert.Equal(new[] { "games/ok" }, expected.Keys.ToArray());
        AssertJoin(store, query, expected, RanksOf(outer), "null/missing Country");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ProjectingAFieldJoinedAliasTakesTheBestScoringDocumentWithThatValue()
    {
        using var store = CreateStore();
        using (var session = store.OpenSession())
        {
            // two users share the country nl
            session.Store(new User { Id = "users/weak", Name = "chris", Number = 2, Countries = new List<string> { "nl" } });
            session.Store(new User { Id = "users/strong", Name = "chris bob", Number = 1, Countries = new List<string> { "nl" } });
            session.Store(new User { Id = "users/de", Name = "chris", Number = 3, Countries = new List<string> { "de" } });
            session.Store(new Game { Id = "games/nl", Name = "zelda", Country = "nl", Rank = 1 });
            session.Store(new Game { Id = "games/de", Name = "zelda", Country = "de", Rank = 2 });
            session.Store(new Game { Id = "games/none", Name = "zelda", Country = "xx", Rank = 3 });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        using (var session = store.OpenSession())
        {
            var rows = session.Advanced.RawQuery<JoinedNumber>(
                    "from index 'Games' as g join (from index 'Users' where search(Name, 'chris bob')) as u on g.Country = u.Country " +
                    "order by g.Rank as long select g.Id as Id, u.Number as Number")
                .ToList();

            Assert.Equal(new[] { ("games/nl", 1L), ("games/de", 3L) }, rows.Select(x => (x.Id, x.Number)));
        }
    }

    private class JoinedNumber
    {
        public string Id { get; set; }
        public long Number { get; set; }
    }

    private IDocumentStore CreateStoreWithNullsAndMissingFields(out List<User> users, out List<Game> games)
    {
        var store = CreateStore();

        users = new List<User>
        {
            new() { Id = "users/1", Name = "chris", Countries = new List<string> { "nl" } },
            new() { Id = "users/nullcountry", Name = "chris", Countries = null }
        };

        games = new List<Game>
        {
            new() { Id = "games/ok", Name = "zelda", OwnerId = "users/1", Country = "nl", Rank = 1 },
            new() { Id = "games/null", Name = "zelda", OwnerId = null, Country = null, Rank = 2 },
            new() { Id = "games/dangling", Name = "zelda", OwnerId = "users/does-not-exist", Country = "xx", Rank = 4 }
        };

        using (var session = store.OpenSession())
        {
            foreach (var user in users)
                session.Store(user);
            foreach (var game in games)
                session.Store(game);
            session.SaveChanges();
        }

        // a document that does not have the fields at all (not even as null)
        using (var commands = store.Commands())
            commands.Put("games/missing", null, new { Name = "zelda", Rank = 3L }, new Dictionary<string, object> { ["@collection"] = "Games" });

        Indexes.WaitForIndexing(store);
        return store;
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AnOuterFieldWithSeveralValuesMatchesWhenAnyValueMatchesAndGivesNoDuplicateRows()
    {
        using var store = CreateStoreWithMultiValuedOwners();

        const string query = "from index 'Games' as g join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerIds = id(u) where search(g.Name, 'zelda')";

        var outer = Fetch<Game>(store, $"from index 'Games' as g where search(g.Name, 'zelda') order by score() {Everything}").Rows;
        var userScores = Fetch<User>(store, $"from index 'Users' where search(Name, 'chris bob') order by score() {Everything}").Rows.ToDictionary(x => x.Id, x => x.Score);
        var expected = Combine(outer, x => x.Entity.OwnerIds ?? new List<string>(), userScores);

        Assert.Equal(new[] { "games/both", "games/dupes", "games/first", "games/last" }, expected.Keys.OrderBy(x => x).ToArray());

        // ids, no duplicates, total and every ordering
        var rows = Fetch<Game>(store, $"{query} order by score() {Everything}");
        Assert.Equal(expected.Count, rows.Total);
        Assert.Equal(expected.Count, rows.Rows.Count);
        Assert.Equal(expected.Count, rows.Rows.Select(x => x.Id).Distinct().Count());
        Assert.Equal(expected.Keys.OrderBy(x => x), rows.Rows.Select(x => x.Id).OrderBy(x => x));

        AssertJoinRowsOnly(store, query, expected, RanksOf(outer), "multi-valued OwnerIds");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AnOuterFieldWithSeveralValuesScoresWithTheSumOfTheDistinctMatchingValues()
    {
        using var store = CreateStoreWithMultiValuedOwners();

        const string query = "from index 'Games' as g join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerIds = id(u) where search(g.Name, 'zelda')";

        var outer = Fetch<Game>(store, $"from index 'Games' as g where search(g.Name, 'zelda') order by score() {Everything}").Rows;
        var userScores = Fetch<User>(store, $"from index 'Users' where search(Name, 'chris bob') order by score() {Everything}").Rows.ToDictionary(x => x.Id, x => x.Score);
        Assert.True(userScores["users/strong"] > userScores["users/weak"] + 0.01, "the probe needs owners with clearly different scores");

        // the score of every distinct matching owner is added to the score of the game
        var expected = Combine(outer, x => x.Entity.OwnerIds ?? new List<string>(), userScores);
        AssertJoin(store, query, expected, RanksOf(outer), "multi-valued OwnerIds: summed score");

        var best = Fetch<Game>(store, $"{query} order by score() {Everything}").Rows;
        Assert.Equal(new[] { "games/both", "games/dupes" }, best.Take(2).Select(x => x.Id).OrderBy(x => x).ToArray());
    }

    private IDocumentStore CreateStoreWithMultiValuedOwners()
    {
        var store = CreateStore();
        using (var session = store.OpenSession())
        {
            session.Store(new User { Id = "users/strong", Name = "chris bob" }); // matches both search terms
            session.Store(new User { Id = "users/weak", Name = "chris" }); // matches one
            session.Store(new User { Id = "users/other", Name = "alice" });

            // the games of the weaker owners are stored first, so a tie would put them first
            session.Store(new Game { Id = "games/first", Name = "zelda", OwnerIds = ["users/other", "users/weak"], Rank = 1 });
            session.Store(new Game { Id = "games/last", Name = "zelda", OwnerIds = ["users/missing", "users/weak"], Rank = 2 });
            session.Store(new Game { Id = "games/both", Name = "zelda", OwnerIds = ["users/weak", "users/strong"], Rank = 3 });
            session.Store(new Game { Id = "games/dupes", Name = "zelda", OwnerIds = ["users/strong", "users/weak", "users/strong"], Rank = 4 });
            session.Store(new Game { Id = "games/nomatch", Name = "zelda", OwnerIds = ["users/other"], Rank = 5 });
            session.Store(new Game { Id = "games/empty", Name = "zelda", OwnerIds = [], Rank = 6 });
            session.Store(new Game { Id = "games/null", Name = "zelda", OwnerIds = null, Rank = 7 });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
        return store;
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AFieldJoinOnJoinedEntriesWithSeveralOrDuplicateTermsTakesTheBestScore()
    {
        using var store = CreateStore();
        using (var session = store.OpenSession())
        {
            // the weaker users are stored first
            session.Store(new User { Id = "users/d1", Name = "chris", Countries = ["nl", "nl"] }); // the same term twice
            session.Store(new User { Id = "users/d2", Name = "chris bob", Countries = ["nl", "be"] }); // strong, two terms
            session.Store(new User { Id = "users/d3", Name = "chris", Countries = ["be"] });
            session.Store(new User { Id = "users/d4", Name = "chris bob", Countries = ["de", "de", "de"] }); // strong, one term three times
            session.Store(new User { Id = "users/d5", Name = "alice", Countries = ["fr", "nl"] }); // not in the sub-query

            session.Store(new Game { Id = "games/nl-1", Name = "zelda", Country = "nl", Rank = 1 }); // nl: d1 (weak) and d2 (strong), plus d5 which is not joined
            session.Store(new Game { Id = "games/nl-2", Name = "zelda", Country = "nl", Rank = 2 });
            session.Store(new Game { Id = "games/be", Name = "zelda", Country = "be", Rank = 3 }); // be: d2 (strong) and d3 (weak)
            session.Store(new Game { Id = "games/de", Name = "zelda", Country = "de", Rank = 4 }); // de: only d4
            session.Store(new Game { Id = "games/fr", Name = "zelda", Country = "fr", Rank = 5 }); // fr: only the user that is not joined
            session.Store(new Game { Id = "games/it", Name = "zelda", Country = "it", Rank = 6 }); // nobody
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        var userRows = Fetch<User>(store, $"from index 'Users' where search(Name, 'chris bob') order by score() {Everything}").Rows;
        var scoreOf = userRows.ToDictionary(x => x.Id, x => x.Score);
        Assert.True(scoreOf["users/d2"] > scoreOf["users/d1"] + 0.01, "the probe needs a strong and a weak user on the same country");
        Assert.DoesNotContain("users/d5", scoreOf.Keys);

        var outer = Fetch<Game>(store, $"from index 'Games' as g where search(g.Name, 'zelda') order by score() {Everything}").Rows;
        var expected = Combine(outer, x => new[] { x.Entity.Country }, BestByValue(userRows, x => x.Entity.Countries));
        Assert.Equal(new[] { "games/be", "games/de", "games/nl-1", "games/nl-2" }, expected.Keys.OrderBy(x => x).ToArray());

        const string query = "from index 'Games' as g join (from index 'Users' where search(Name, 'chris bob')) as u on g.Country = u.Country where search(g.Name, 'zelda')";
        AssertJoin(store, query, expected, RanksOf(outer), "field join, duplicate terms");

        // 'nl' is joined to a weak and a strong user
        var best = Fetch<Game>(store, $"{query} order by score() {Everything}").Rows.ToDictionary(x => x.Id, x => x.Score);
        var outerScores = outer.ToDictionary(x => x.Id, x => x.Score);
        Assert.Equal(outerScores["games/nl-1"] + scoreOf["users/d2"], best["games/nl-1"], Tolerance);
        Assert.Equal(outerScores["games/be"] + scoreOf["users/d2"], best["games/be"], Tolerance);
        Assert.Equal(outerScores["games/de"] + scoreOf["users/d4"], best["games/de"], Tolerance);
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoinOnTheIdWorksForAnyNumberOfJoinedKeys()
    {
        using var store = CreateStoreWithSixtyUsersAndTheirGames();
        var failures = new List<string>();

        foreach (var keys in new[] { 0, 1, 2, 3, 4, 5, 6, 50 })
        {
            var subQuery = $"from index 'Users' where search(Name, 'hot') and Number < {keys}";
            var userScores = Fetch<User>(store, $"{subQuery} order by score() {Everything}").Rows.ToDictionary(x => x.Id, x => x.Score);
            Assert.Equal(keys, userScores.Count); // the probe: the sub-query matches exactly this many users

            var outer = Fetch<Game>(store, $"from index 'Games' as g where search(g.Name, 'zelda') order by score() {Everything}").Rows;
            var expected = Combine(outer, x => new[] { x.Entity.OwnerId }, userScores);
            Assert.Equal(keys, expected.Count);

            Check(failures, $"{keys} keys on the id", () =>
                AssertJoin(store, $"from index 'Games' as g join ({subQuery}) as u on g.OwnerId = id(u) where search(g.Name, 'zelda')", expected, RanksOf(outer), $"{keys} keys on the id"));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void JoinOnAFieldWorksForAnyNumberOfJoinedKeys()
    {
        using var store = CreateStoreWithSixtyUsersAndTheirGames();
        var failures = new List<string>();

        foreach (var keys in new[] { 0, 1, 2, 3, 4, 5, 6, 50 })
        {
            var subQuery = $"from index 'Users' where search(Name, 'hot') and Number < {keys}";
            var userRows = Fetch<User>(store, $"{subQuery} order by score() {Everything}").Rows;
            Assert.Equal(keys, userRows.Count);

            var outer = Fetch<Game>(store, $"from index 'Games' as g where search(g.Name, 'zelda') order by score() {Everything}").Rows;
            var expected = Combine(outer, x => new[] { x.Entity.Country }, BestByValue(userRows, x => x.Entity.Countries));
            Assert.Equal(keys, expected.Count);

            Check(failures, $"{keys} keys on a field", () =>
                AssertJoin(store, $"from index 'Games' as g join ({subQuery}) as u on g.Country = u.Country where search(g.Name, 'zelda')", expected, RanksOf(outer), $"{keys} keys on a field"));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private IDocumentStore CreateStoreWithSixtyUsersAndTheirGames()
    {
        var store = CreateStore();
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < 60; i++)
            {
                // a different amount of padding words gives the users different scores
                bulk.Store(new User { Id = $"users/{i}", Name = "hot" + string.Concat(Enumerable.Repeat(" pad", i % 3)), Number = i, Countries = [$"c{i}"] }, $"users/{i}");
                bulk.Store(new Game { Id = $"games/{i}", Name = "zelda", OwnerId = $"users/{i}", Country = $"c{i}", Rank = 1000 - i }, $"games/{i}");
            }

            for (var i = 0; i < 5; i++)
                bulk.Store(new Game { Id = $"games/orphan{i}", Name = "zelda", OwnerId = $"users/none{i}", Country = $"cx{i}", Rank = 2000 + i }, $"games/orphan{i}");
        }

        Indexes.WaitForIndexing(store);
        return store;
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void PagingAJoinKeepsTheTotalAndTheOrderOnTheScoreAndOnThePlainPath()
    {
        using var store = CreateStore();
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < 50; i++)
            {
                // every fifth user does not match the joined query: 40 of the 50 games stay
                bulk.Store(new User { Id = $"users/{i}", Name = i % 5 == 0 ? "cold" : "hot" + string.Concat(Enumerable.Repeat(" pad", i % 4)), Number = i, Countries = [$"c{i}"] }, $"users/{i}");
                bulk.Store(new Game { Id = $"games/{i}", Name = "zelda", OwnerId = $"users/{i}", Country = $"c{i}", Rank = 100 - i }, $"games/{i}");
            }
        }

        Indexes.WaitForIndexing(store);

        var outer = Fetch<Game>(store, $"from index 'Games' as g where search(g.Name, 'zelda') order by score() {Everything}").Rows;
        var userRows = Fetch<User>(store, $"from index 'Users' where search(Name, 'hot') order by score() {Everything}").Rows;
        Assert.Equal(40, userRows.Count);

        var joins = new[]
        {
            ("id", "g.OwnerId = id(u)", Combine(outer, x => new[] { x.Entity.OwnerId }, userRows.ToDictionary(x => x.Id, x => x.Score))),
            ("field", "g.Country = u.Country", Combine(outer, x => new[] { x.Entity.Country }, BestByValue(userRows, x => x.Entity.Countries)))
        };

        var ranks = RanksOf(outer);
        var pages = new[] { (0, 10), (10, 10), (20, 10), (30, 10), (25, 10), (7, 5), (35, 100), (40, 10), (0, 1) };

        foreach (var (kind, on, expected) in joins)
        {
            Assert.Equal(40, expected.Count);
            var query = $"from index 'Games' as g join (from index 'Users' where search(Name, 'hot')) as u on {on} where search(g.Name, 'zelda')";

            var fullBest = expected.Values.OrderByDescending(x => x).ToList();
            var fullByRank = expected.Keys.OrderBy(x => ranks[x]).ToList();

            foreach (var (skip, take) in pages)
            {
                var label = $"{kind} join, page {skip},{take}";

                var byScore = Fetch<Game>(store, $"{query} order by score() limit {skip}, {take}");
                Assert.True(40 == byScore.Total, $"[{label}] order by score(): total should be 40 but is {byScore.Total}");
                AssertScoreSequence(fullBest.Skip(skip).Take(take).ToList(), byScore.Rows.Select(x => x.Score).ToList(), $"[{label}] order by score()");

                var byRank = Fetch<Game>(store, $"{query} order by g.Rank as long limit {skip}, {take}");
                Assert.True(40 == byRank.Total, $"[{label}] order by field: total should be 40 but is {byRank.Total}");
                Assert.Equal(fullByRank.Skip(skip).Take(take), byRank.Rows.Select(x => x.Id));

                var byRankDescending = Fetch<Game>(store, $"{query} order by g.Rank as long desc limit {skip}, {take}");
                Assert.True(40 == byRankDescending.Total, $"[{label}] order by field desc: total should be 40 but is {byRankDescending.Total}");
                Assert.Equal(Enumerable.Reverse(fullByRank).Skip(skip).Take(take), byRankDescending.Rows.Select(x => x.Id));

                var plain = Fetch<Game>(store, $"{query} limit {skip}, {take}");
                Assert.True(40 == plain.Total, $"[{label}] no order by: total should be 40 but is {plain.Total}");
                Assert.True(plain.Rows.Count == Math.Max(0, Math.Min(take, 40 - skip)), $"[{label}] no order by: unexpected page size {plain.Rows.Count}");
                Assert.All(plain.Rows, x => Assert.Contains(x.Id, expected.Keys));
            }

            // consecutive pages cover the joined result exactly once, with and without an order by
            foreach (var orderBy in new[] { "", " order by score()", " order by g.Rank as long" })
            {
                var seen = new List<string>();
                for (var skip = 0; skip < 40; skip += 10)
                    seen.AddRange(Fetch<Game>(store, $"{query}{orderBy} limit {skip}, 10").Rows.Select(x => x.Id));

                Assert.True(seen.Count == 40, $"[{kind} join{orderBy}] the pages hold {seen.Count} rows instead of 40");
                Assert.True(seen.Distinct().Count() == 40, $"[{kind} join{orderBy}] a row appears on two pages");
                Assert.Equal(expected.Keys.OrderBy(x => x), seen.OrderBy(x => x));
            }
        }
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AnIndexCanBeJoinedToItselfOnTheIdAndOnAField()
    {
        using var store = CreateStore();
        using (var session = store.OpenSession())
        {
            session.Store(new Game { Id = "games/1", Name = "zelda", Country = "nl", Rank = 1 });
            session.Store(new Game { Id = "games/2", Name = "mario", ParentId = "games/1", Country = "nl", Rank = 2 });
            session.Store(new Game { Id = "games/3", Name = "mario kart", ParentId = "games/2", Country = "be", Rank = 3 }); // the parent is a mario
            session.Store(new Game { Id = "games/4", Name = "mario", ParentId = "games/1", Country = "nl", Rank = 4 });
            session.Store(new Game { Id = "games/5", Name = "zelda zelda", ParentId = "games/1", Country = "be", Rank = 5 });
            session.Store(new Game { Id = "games/6", Name = "mario", ParentId = "games/missing", Country = "xx", Rank = 6 });
            session.Store(new Game { Id = "games/7", Name = "mario", ParentId = null, Country = null, Rank = 7 });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        var outer = Fetch<Game>(store, $"from index 'Games' as g where search(g.Name, 'mario') order by score() {Everything}").Rows;
        var ranks = RanksOf(outer);

        // joined documents: the zelda games
        var zelda = Fetch<Game>(store, $"from index 'Games' where search(Name, 'zelda') order by score() {Everything}").Rows;
        Assert.Equal(2, zelda.Count);

        var byId = Combine(outer, x => new[] { x.Entity.ParentId }, zelda.ToDictionary(x => x.Id, x => x.Score));
        Assert.Equal(new[] { "games/2", "games/4" }, byId.Keys.OrderBy(x => x).ToArray());
        AssertJoin(store, "from index 'Games' as g join (from index 'Games' where search(Name, 'zelda')) as o on g.ParentId = id(o) where search(g.Name, 'mario')", byId, ranks, "self join on the id");

        var byField = Combine(outer, x => new[] { x.Entity.Country }, BestByValue(zelda, x => new List<string> { x.Entity.Country }));
        Assert.Equal(new[] { "games/2", "games/3", "games/4" }, byField.Keys.OrderBy(x => x).ToArray());
        AssertJoin(store, "from index 'Games' as g join (from index 'Games' where search(Name, 'zelda')) as o on g.Country = o.Country where search(g.Name, 'mario')", byField, ranks, "self join on a field");

        // the same documents on both sides: a mario whose parent is also a mario
        var mario = Fetch<Game>(store, $"from index 'Games' where search(Name, 'mario') order by score() {Everything}").Rows;
        var marioParents = Combine(outer, x => new[] { x.Entity.ParentId }, mario.ToDictionary(x => x.Id, x => x.Score));
        Assert.Equal(new[] { "games/3" }, marioParents.Keys.OrderBy(x => x).ToArray());
        AssertJoin(store, "from index 'Games' as g join (from index 'Games' where search(Name, 'mario')) as o on g.ParentId = id(o) where search(g.Name, 'mario')", marioParents, ranks, "self join, same documents on both sides");
    }


    // Row set, no duplicates, scores and order for 'order by score()', field order, and the plain query without an order by
    private static void AssertJoin(IDocumentStore store, string query, IReadOnlyDictionary<string, double> expected, IReadOnlyDictionary<string, long> ranks, string label)
    {
        var byScore = Fetch<Game>(store, $"{query} order by score() {Everything}");
        Assert.True(expected.Count == byScore.Total, $"[{label}] order by score(): total should be {expected.Count} but is {byScore.Total}");
        AssertRows(byScore.Rows, expected, $"{label}, order by score()");

        foreach (var row in byScore.Rows)
            Assert.True(Math.Abs(expected[row.Id] - row.Score) < Tolerance, $"[{label}] {row.Id} should have score {expected[row.Id]} but has {row.Score}");

        AssertScoreSequence(expected.Values.OrderByDescending(x => x).ToList(), byScore.Rows.Select(x => x.Score).ToList(), $"[{label}] order by score()");

        var weakest = Fetch<Game>(store, $"{query} order by score() desc {Everything}");
        AssertRows(weakest.Rows, expected, $"{label}, order by score() desc");
        AssertScoreSequence(expected.Values.OrderBy(x => x).ToList(), weakest.Rows.Select(x => x.Score).ToList(), $"[{label}] order by score() desc");

        AssertJoinRowsOnly(store, query, expected, ranks, label);
    }

    // The checks that do not depend on the scores
    private static void AssertJoinRowsOnly(IDocumentStore store, string query, IReadOnlyDictionary<string, double> expected, IReadOnlyDictionary<string, long> ranks, string label)
    {
        var byRank = Fetch<Game>(store, $"{query} order by g.Rank as long {Everything}");
        Assert.True(expected.Count == byRank.Total, $"[{label}] order by field: total should be {expected.Count} but is {byRank.Total}");
        AssertRows(byRank.Rows, expected, $"{label}, order by field");
        Assert.Equal(expected.Keys.OrderBy(x => ranks[x]), byRank.Rows.Select(x => x.Id));

        var plain = Fetch<Game>(store, $"{query} {Everything}");
        Assert.True(expected.Count == plain.Total, $"[{label}] no order by: total should be {expected.Count} but is {plain.Total}");
        AssertRows(plain.Rows, expected, $"{label}, no order by");

        // paged, the total still counts everything
        var paged = Fetch<Game>(store, $"{query} limit 0, 1");
        Assert.True(expected.Count == paged.Total, $"[{label}] limit 0, 1: total should be {expected.Count} but is {paged.Total}");
        Assert.True(paged.Rows.Count == Math.Min(1, expected.Count), $"[{label}] limit 0, 1 returned {paged.Rows.Count} rows");
    }

    private static void AssertRows(List<Row<Game>> rows, IReadOnlyDictionary<string, double> expected, string label)
    {
        var ids = rows.Select(x => x.Id).ToList();
        Assert.True(ids.Count == ids.Distinct().Count(), $"[{label}] a row is returned more than once: {string.Join(", ", ids)}");
        Assert.True(expected.Keys.OrderBy(x => x).SequenceEqual(ids.OrderBy(x => x)),
            $"[{label}] expected rows [{string.Join(", ", expected.Keys.OrderBy(x => x))}] but got [{string.Join(", ", ids.OrderBy(x => x))}]");
    }

    private static void AssertScoreSequence(List<double> expected, List<double> actual, string label)
    {
        Assert.True(expected.Count == actual.Count, $"{label}: expected {expected.Count} rows but got {actual.Count}");
        for (var i = 0; i < expected.Count; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) < Tolerance, $"{label}: position {i} should have score {expected[i]} but has {actual[i]}");
    }

    private static void Check(List<string> failures, string label, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            failures.Add($"{label}: {e.Message}");
        }
    }


    // The joined result: every outer row with at least one join value that exists in the joined side
    private static Dictionary<string, double> Combine(IEnumerable<Row<Game>> outer, Func<Row<Game>, IEnumerable<string>> valuesOf, IReadOnlyDictionary<string, double> joinedByValue)
    {
        var result = new Dictionary<string, double>();
        foreach (var row in outer)
        {
            var found = false;
            var sum = 0d;
            foreach (var value in valuesOf(row).Where(x => x != null).Distinct())
            {
                if (joinedByValue.TryGetValue(value, out var score) == false)
                    continue;

                sum += score;
                found = true;
            }

            if (found)
                result[row.Id] = row.Score + sum;
        }

        return result;
    }

    private static Dictionary<string, double> BestByValue(IEnumerable<Row<User>> users, Func<Row<User>, List<string>> valuesOf) =>
        BestByValue(users, valuesOf, x => x.Score);

    private static Dictionary<string, double> BestByValue(IEnumerable<Row<Game>> games, Func<Row<Game>, List<string>> valuesOf) =>
        BestByValue(games, valuesOf, x => x.Score);

    private static Dictionary<string, double> BestByValue<T>(IEnumerable<Row<T>> rows, Func<Row<T>, List<string>> valuesOf, Func<Row<T>, double> scoreOf)
    {
        var best = new Dictionary<string, double>();
        foreach (var row in rows)
        foreach (var value in (valuesOf(row) ?? new List<string>()).Where(x => x != null))
            best[value] = best.TryGetValue(value, out var current) ? Math.Max(current, scoreOf(row)) : scoreOf(row);

        return best;
    }

    private static Dictionary<string, long> RanksOf(IEnumerable<Row<Game>> games) => games.ToDictionary(x => x.Id, x => x.Entity.Rank);

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

    // one session per query: a session hands the same tracked entity to every query that returns the same id
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


    private IDocumentStore CreateStore()
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();

        var store = GetDocumentStore(options);
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);
        return store;
    }

    private class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public long Number { get; set; }
        public List<string> Countries { get; set; }
    }

    private class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public List<string> OwnerIds { get; set; }
        public string ParentId { get; set; }
        public string Country { get; set; }
        public long Rank { get; set; }
    }

    private class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Number, Country = u.Countries };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    private class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.OwnerIds, g.ParentId, g.Country, g.Rank };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
