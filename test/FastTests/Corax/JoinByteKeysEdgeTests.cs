using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Corax.Querying;
using Newtonsoft.Json;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Sparrow.Server;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;
using JoinFieldScan = Raven.Server.Documents.Indexes.Persistence.Corax.CoraxIndexReadOperation.JoinFieldScan;
using ScanSession = FastTests.Corax.JoinFieldScanTests.ScanSession;

namespace FastTests.Corax;

// The byte keys of a JOIN against what the plain query and the stored documents say
public class JoinByteKeysEdgeTests(ITestOutputHelper output, SharedJoinDatabase shared) : RavenTestBase(output), IClassFixture<SharedJoinDatabase>
{
    private const string Everything = "limit 0, 1000000";
    private static readonly string[] Countries = ["NL", "nl", "De", "fr", "BE"];

    // ids longer than 256 characters, and longer than the 512 bytes of the stack buffer of the id checks

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task LongIdsJoinLikeThePlainQuery()
    {
        var users = LongUsers();
        var games = LongGames(users);
        using var store = SeedLong();
        var database = await Databases.GetDocumentDatabaseInstanceFor(store);

        const string rql = "from index 'LongUsers' where Tag in ('t1', 't2')";
        using (var session = new ScanSession(database, "LongUsers", rql))
        {
            var expected = JoinScanOracle.IdScores(store, rql);
            using var actual = session.Reader.ScanIdsAndScores(session.Query, session.DocumentsContext, CancellationToken.None);
            AssertSameKeys(rql, expected, actual);
            Assert.Contains(expected, x => x.Key.Length > 256);
            Assert.Contains(expected, x => Encoding.UTF8.GetByteCount(x.Key) > 400);
        }

        var matched = users.Where(u => u.Tag is "t1" or "t2").ToDictionary(u => Lower(u.Id), u => u);
        var expectedOwner = games.Where(g => g.OwnerId != null && matched.ContainsKey(Lower(g.OwnerId))).Select(g => g.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(expectedOwner);

        const string join = "join (from index 'LongUsers' where Tag in ('t1', 't2')) as u on g.OwnerId = id(u)";
        Assert.Equal(expectedOwner, Ids(store, $"from index 'LongGames' as g {join} {Everything}"));
        Assert.Equal(expectedOwner, Ids(store, $"from index 'LongGames' as g {join} order by score() {Everything}"));

        // the OR looks each owner id up again
        using var projection = store.OpenSession();
        var rows = projection.Advanced.RawQuery<LongProjection>(
                $"from index 'LongGames' as g {join} where matches(u) or g.Name = 'game 3' or g.Name = 'game 4' or g.Name = 'game 7' or g.Name = 'game 9' or g.Name = 'game 11' " +
                $"select g.Name as Name, u.Name as UserName {Everything}")
            .ToList()
            .ToDictionary(x => x.Name);

        var byLowerId = users.ToDictionary(u => Lower(u.Id));
        var longOwners = 0;
        foreach (var game in games)
        {
            if (game.OwnerId != null && Encoding.UTF8.GetByteCount(game.OwnerId) > 512)
                longOwners++;

            var user = game.OwnerId != null && byLowerId.TryGetValue(Lower(game.OwnerId), out var found) && found.Tag is ("t1" or "t2") ? found : null;
            if (user == null && game.Name is not ("game 3" or "game 4" or "game 7" or "game 9" or "game 11"))
            {
                Assert.False(rows.ContainsKey(game.Name), game.Name);
                continue;
            }

            Assert.True(rows.TryGetValue(game.Name, out var row), game.Name);
            Assert.Equal(user?.Name, row.UserName);
        }

        Assert.True(longOwners > 0, "some owner ids are longer than 512 bytes");
    }

    private static string Lower(string id) => id.ToLowerInvariant();

    private static List<EdgeUser> LongUsers() => Enumerable.Range(0, 90).Select(i => new EdgeUser
    {
        Id = (i % 3) switch
        {
            0 => "users/" + new string((char)('a' + i % 26), 300) + "-" + i, // more than 256 ASCII characters
            1 => "users/" + new string('É', 200) + "-" + i, // more than 400 bytes of UTF-8 (a document id is at most 512 bytes)
            _ => "users/" + i
        },
        Name = $"user {i}",
        Tag = $"t{i % 5}",
        Country = Countries[i % Countries.Length]
    }).ToList();

    private static List<EdgeGame> LongGames(List<EdgeUser> users)
    {
        var random = new Random(12);
        return Enumerable.Range(0, 1500).Select(i =>
        {
            var owner = users[random.Next(users.Count)].Id;
            return new EdgeGame
            {
                Id = $"games/{i}",
                Name = $"game {i}",
                // the owner as the game holds it, in another case or missing
                OwnerId = i % 50 == 7 ? "users/" + new string('É', 300) + i // a value over 512 bytes: never a document id, so never one of the keys
                    : i % 50 == 9 ? "users/" + new string('a', 600) + i // ASCII over 512 bytes: the ids are lowered in a pooled buffer
                    : i % 50 == 11 ? "USERS/" + new string('A', 600) + i
                    : random.Next(8) switch { 0 => null, 1 => "users/missing" + i, 2 => owner.ToUpperInvariant(), 3 => owner.ToLowerInvariant(), _ => owner },
                Country = Countries[random.Next(Countries.Length)]
            };
        }).ToList();
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task AJoinedIndexWithSeveralEntriesPerDocumentJoinsLikeTheDocuments()
    {
        var users = FanUsers();
        var games = FanGames(users);
        using var store = SeedFan();
        var database = await Databases.GetDocumentDatabaseInstanceFor(store);

        const string rql = "from index 'FanUsers' where Tag in ('t1', 't2', 't3')";
        using (var session = new ScanSession(database, "FanUsers", rql))
        {
            // one key per entry: a document is as many times in the keys as it has entries that match
            var scoreOfTag = new Dictionary<(string Tag, string Id), float>();
            foreach (var tag in new[] { "t1", "t2", "t3" })
            {
                foreach (var (documentId, score) in JoinScanOracle.Matches(store, $"from index 'FanUsers' where Tag = '{tag}'"))
                    scoreOfTag[(tag, Lower(documentId))] = score;
            }

            var expected = users.SelectMany(u => u.Tags.Where(tag => tag is "t1" or "t2" or "t3").Select(tag => (Key: Lower(u.Id), Score: scoreOfTag[(tag, Lower(u.Id))]))).ToList();
            using var actual = session.Reader.ScanIdsAndScores(session.Query, session.DocumentsContext, CancellationToken.None);
            AssertSameKeysAsMultiset(rql, expected, actual);
            Assert.True(expected.Count > expected.Select(x => x.Key).Distinct().Count(), "the joined index has several entries for some documents");
        }

        var matched = users.Where(u => u.Tags.Any(t => t is "t1" or "t2" or "t3")).Select(u => Lower(u.Id)).ToHashSet();
        var expectedGames = games.Where(g => g.OwnerId != null && matched.Contains(Lower(g.OwnerId))).Select(g => g.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(expectedGames);

        const string join = "join (from index 'FanUsers' where Tag in ('t1', 't2', 't3')) as u on g.OwnerId = id(u)";
        Assert.Equal(expectedGames, Ids(store, $"from index 'FanGames' as g {join} {Everything}"));
        Assert.Equal(expectedGames, Ids(store, $"from index 'FanGames' as g {join} order by score() {Everything}"));
    }

    private static List<EdgeFanUser> FanUsers()
    {
        var random = new Random(21);
        return Enumerable.Range(0, 300).Select(i => new EdgeFanUser
        {
            Id = i % 4 == 0 ? $"users/Ärger-{i}" : $"users/{i}",
            Name = $"fan {i}",
            Tags = random.Next(8) == 0 ? [] : Enumerable.Range(0, 1 + random.Next(4)).Select(_ => "t" + random.Next(12)).ToArray()
        }).ToList();
    }

    private static List<EdgeGame> FanGames(List<EdgeFanUser> users)
    {
        var random = new Random(22);
        return Enumerable.Range(0, 1200).Select(i =>
        {
            var owner = users[random.Next(users.Count)].Id;
            return new EdgeGame
            {
                Id = $"games/{i}",
                Name = $"game {i}",
                OwnerId = random.Next(10) switch { 0 => null, 1 => "users/missing" + i, 2 => owner.ToUpperInvariant(), _ => owner },
                Country = Countries[random.Next(Countries.Length)]
            };
        }).ToList();
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task AMapReduceJoinedIndexJoinsLikeThePlainQuery()
    {
        var users = LongUsers();
        var games = LongGames(users);
        using var store = SeedLong();
        var database = await Databases.GetDocumentDatabaseInstanceFor(store);

        const string rql = "from index 'ByCountry' where Country in ('NL', 'BE', 'De')";
        List<(string Key, float Score)> expected;
        using (var session = new ScanSession(database, "ByCountry", rql))
        {
            expected = JoinScanOracle.BestPerKey(JoinScanOracle.MatchesOf<ByCountryResult>(store, rql).Select(x => (Lower(x.Document.Country), x.Score)));
            using var actual = session.Reader.ScanTermScores(session.Query, session.DocumentsContext, "Country", JoinFieldScan.Auto, false, CancellationToken.None);
            AssertSameKeysAsMultiset(rql, expected, actual);
            Assert.NotEmpty(expected);
        }

        var keys = users.Where(u => Lower(u.Country) is "nl" or "be" or "de").Select(u => Lower(u.Country)).ToHashSet();
        Assert.Equal(keys, expected.Select(x => Lower(x.Key)).ToHashSet());
        var expectedGames = games.Where(g => keys.Contains(Lower(g.Country))).Select(g => g.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(expectedGames);

        const string join = "join (from index 'ByCountry' where Country in ('NL', 'BE', 'De')) as c on g.Country = c.Country";
        Assert.Equal(expectedGames, Ids(store, $"from index 'LongGames' as g {join} {Everything}"));
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task ADynamicFieldAsTheJoinFieldIsRejectedAndReadLikeThePlainQuery()
    {
        var users = LongUsers();
        var games = LongGames(users);
        using var store = SeedLong();
        var database = await Databases.GetDocumentDatabaseInstanceFor(store);

        // the field is read as the terms of the plain query's documents
        const string rql = "from index 'DynamicUsers' where Dynamic in ('t1', 't2')";
        using (var session = new ScanSession(database, "DynamicUsers", rql))
        {
            var expected = JoinScanOracle.BestPerKey(JoinScanOracle.MatchesOf<EdgeUser>(store, rql).Select(x => (Lower(x.Document.Tag), x.Score)));
            using var actual = session.Reader.ScanTermScores(session.Query, session.DocumentsContext, "Dynamic", JoinFieldScan.Auto, false, CancellationToken.None);
            AssertSameKeysAsMultiset(rql, expected, actual);
            Assert.NotEmpty(expected);
        }

        // the join itself refuses a field that the index does not declare
        using var rejected = store.OpenSession();
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            rejected.Advanced.RawQuery<EdgeGame>(
                "from index 'LongGames' as g join (from index 'DynamicUsers' where Dynamic in ('t1', 't2')) as u on g.Country = u.Dynamic " + Everything).ToList();
        });
        Assert.Contains("is not indexed in the index 'DynamicUsers'", ex.Message);
    }


    // the tests on the long ids read the same data
    private IDocumentStore SeedLong() => shared.Get("long", CoraxOptions(), o => GetDocumentStore(o), s =>
    {
        var users = LongUsers();
        var games = LongGames(users);
        new LongUsersIndex().Execute(s);
        new ByCountryIndex().Execute(s);
        new DynamicUsersIndex().Execute(s);
        new LongGamesIndex().Execute(s);
        using (var bulk = s.BulkInsert())
        {
            foreach (var user in users)
                bulk.Store(user, user.Id);
            foreach (var game in games)
                bulk.Store(game, game.Id);
        }

        Indexes.WaitForIndexing(s);
    });

    private IDocumentStore SeedFan()
    {
        var users = FanUsers();
        var games = FanGames(users);
        var store = GetDocumentStore(CoraxOptions());
        new FanUsersIndex().Execute(store);
        new FanGamesIndex().Execute(store);
        using (var bulk = store.BulkInsert())
        {
            foreach (var user in users)
                bulk.Store(user, user.Id);
            foreach (var game in games)
                bulk.Store(game, game.Id);
        }

        Indexes.WaitForIndexing(store);
        return store;
    }

    private static List<string> Ids(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<EdgeGame>(rql).ToList().Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    // The expected keys and the keys of the bytes, as the same multiset
    private static void AssertSameKeysAsMultiset(string label, List<(string Key, float Score)> expected, JoinTermScores actual)
    {
        var left = expected.Select(x => (x.Key, Bits: BitConverter.SingleToInt32Bits(x.Score)))
            .OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Bits).ToList();
        var right = Enumerable.Range(0, actual.Count).Select(i => (Key: Encoding.UTF8.GetString(actual.Term(i)), Bits: BitConverter.SingleToInt32Bits(actual.Score(i))))
            .OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Bits).ToList();
        Assert.True(left.SequenceEqual(right), $"[{label}] {left.Count} keys expected, {right.Count} found");
    }

    private static void AssertSameKeys(string label, List<(string Key, float Score)> expected, JoinTermScores actual)
    {
        Assert.True(expected.Count == actual.Count, $"[{label}] {expected.Count} keys expected, {actual.Count} found");
        var byKey = expected.ToDictionary(x => x.Key, x => x.Score, StringComparer.Ordinal);
        for (var i = 0; i < actual.Count; i++)
        {
            var key = Encoding.UTF8.GetString(actual.Term(i));
            Assert.True(byKey.TryGetValue(key, out var score), $"[{label}] unexpected key '{key}'");
            Assert.Equal(BitConverter.SingleToInt32Bits(score), BitConverter.SingleToInt32Bits(actual.Score(i)));
            Assert.True(actual.TryFind(Encoding.UTF8.GetBytes(key), out var found) && found == i, $"[{label}] '{key}' is not found by its bytes");
        }
    }


    private sealed class EdgeUser
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Tag { get; set; }
        public string Country { get; set; }
    }

    private sealed class EdgeFanUser
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string[] Tags { get; set; }
    }

    private sealed class EdgeGame
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string Country { get; set; }
    }

    private sealed class LongProjection
    {
        public string Name { get; set; }
        public string UserName { get; set; }
        public string OtherName { get; set; }
    }

    private sealed class ByCountryResult : JoinScanOracle.IHasMetadata
    {
        [JsonProperty("@metadata")]
        public Dictionary<string, object> Metadata { get; set; }

        public string Country { get; set; }
        public int Count { get; set; }
    }

    private sealed class LongUsersIndex : AbstractIndexCreationTask<EdgeUser>
    {
        public LongUsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Tag, u.Country };
        }

        public override string IndexName => "LongUsers";
    }

    private sealed class LongGamesIndex : AbstractIndexCreationTask<EdgeGame>
    {
        public LongGamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.Country };
        }

        public override string IndexName => "LongGames";
    }

    private sealed class FanUsersIndex : AbstractIndexCreationTask<EdgeFanUser>
    {
        public FanUsersIndex()
        {
            Map = users => from u in users from t in u.Tags select new { u.Name, Tag = t };
        }

        public override string IndexName => "FanUsers";
    }

    private sealed class FanGamesIndex : AbstractIndexCreationTask<EdgeGame>
    {
        public FanGamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.Country };
        }

        public override string IndexName => "FanGames";
    }

    private sealed class ByCountryIndex : AbstractIndexCreationTask<EdgeUser, ByCountryResult>
    {
        public ByCountryIndex()
        {
            Map = users => from u in users select new { u.Country, Count = 1 };
            Reduce = results => from r in results group r by r.Country into g select new { Country = g.Key, Count = g.Sum(x => x.Count) };
        }

        public override string IndexName => "ByCountry";
    }

    private sealed class DynamicUsersIndex : AbstractIndexCreationTask<EdgeUser>
    {
        public DynamicUsersIndex()
        {
            Map = users => users.Select(u => new { u.Name, _ = CreateField("Dynamic", u.Tag) });
        }

        public override string IndexName => "DynamicUsers";
    }
}
