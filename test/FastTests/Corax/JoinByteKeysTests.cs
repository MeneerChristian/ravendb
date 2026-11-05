using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Corax.Querying;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Indexes;
using Sparrow.Server;
using Sparrow.Threading;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;
using FieldIndexingMode = Corax.FieldIndexingMode;
using JoinFieldScan = Raven.Server.Documents.Indexes.Persistence.Corax.CoraxIndexReadOperation.JoinFieldScan;

namespace FastTests.Corax;

// The keys of a JOIN are the bytes of the index terms in one native buffer, not a string per key
public class JoinByteKeysTests(ITestOutputHelper output, SharedJoinDatabase shared) : RavenTestBase(output), IClassFixture<SharedJoinDatabase>
{
    private const string Everything = "limit 0, 1000000";
    private const int Users = 2_500;
    private const int Depts = 240;
    private const int Games = 5_000;
    private static readonly string[] Countries = ["NL", "nl", "De", "fr", "BE", "uk", "es"];
    private static readonly string[] LongValues = Enumerable.Range(0, 5).Select(n => new string((char)('a' + n), 700 + n) + "-" + n).ToArray();

    private const string GamesFrom = "from index 'Games' as g";


    [RavenFact(RavenTestCategory.Corax)]
    public void TheKeyStoreFindsTheKeysByTheirBytes()
    {
        using var allocator = new ByteStringContext(SharedMultipleUseFlag.None);
        var keys = new JoinTermScores(allocator, keepWinners: true);
        try
        {
            var random = new Random(3);
            var expected = new Dictionary<string, (float Score, long Winner)>(StringComparer.Ordinal);
            var strings = new List<string> { "a", "b", "ab", "users/Ärger", "users/ärger", "ñandú", "\u0003", new string('x', 700), new string('x', 699) + "y", "é", "e", "z", "😀" };
            for (var i = 0; i < 2000; i++)
                strings.Add($"users/{random.Next(100_000)}");

            foreach (var key in strings.Distinct())
            {
                var score = (float)random.NextDouble();
                var winner = random.Next(1_000_000);
                expected[key] = (score, winner);
                keys.Add(Encoding.UTF8.GetBytes(key), score, winner);
            }

            Assert.Equal(expected.Count, keys.Count);

            void CheckEverything()
            {
                var order = keys.SortedOrder();
                Assert.Equal(keys.Count, order.Length);
                for (var i = 1; i < order.Length; i++)
                    Assert.True(keys.Term(order[i - 1]).SequenceCompareTo(keys.Term(order[i])) < 0, "the order is by the bytes, without duplicates");

                foreach (var (key, (score, winner)) in expected)
                {
                    Assert.True(keys.TryFind(Encoding.UTF8.GetBytes(key), out var index), key);
                    Assert.Equal(key, Encoding.UTF8.GetString(keys.Term(index)));
                    Assert.Equal(BitConverter.SingleToInt32Bits(score), BitConverter.SingleToInt32Bits(keys.Score(index)));
                    Assert.Equal(winner, keys.WinnerEntryId(index));
                }

                Assert.False(keys.TryFind("users/none"u8, out _));
                Assert.False(keys.TryFind(""u8, out _));
                Assert.False(keys.TryFind(new string('x', 698).Select(x => (byte)x).ToArray(), out _));
            }

            CheckEverything();

            // a key added later is found too (the order is rebuilt), the buffers grow
            keys.Add("zzz-late"u8, 1f, 5);
            expected["zzz-late"] = (1f, 5);
            CheckEverything();
        }
        finally
        {
            keys.Dispose();
            keys.Dispose(); // twice is fine
        }

        using var empty = new JoinTermScores(allocator);
        Assert.Equal(0, empty.Count);
        Assert.False(empty.TryFind("a"u8, out _));
        Assert.True(empty.SortedOrder().IsEmpty);
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void TheKeyStoreKeepsNoWinnersWhenNotAsked()
    {
        using var allocator = new ByteStringContext(SharedMultipleUseFlag.None);
        using var keys = new JoinTermScores(allocator);
        keys.Add("a"u8, 2f, 7);
        keys.Add("b"u8, 3f);
        Assert.False(keys.HasWinners);
        Assert.Equal(2, keys.Count);
        Assert.Equal(3f, keys.Score(1));
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task TheIdScansGiveTheKeysAndScoresOfThePlainQuery()
    {
        using var store = Seed();
        var database = await Databases.GetDocumentDatabaseInstanceFor(store);

        foreach (var rql in new[]
                 {
                     "from index 'Users' where search(Name, 'w2 w6 w15 w40 w120 w400')",
                     "from index 'Users' where Tag = 't3'",
                     "from index 'Users' where Tag = 't3' or search(Name, 'w1 w2 w3')",
                     "from index 'Users'",
                     "from index 'Users' where Tag = 'nothing'"
                 })
        {
            using var session = new JoinFieldScanTests.ScanSession(database, "Users", rql);
            var expected = JoinScanOracle.IdScores(store, rql);
            using var actual = session.Reader.ScanIdsAndScores(session.Query, session.DocumentsContext, CancellationToken.None);
            AssertSameKeys(rql, expected, actual);
            if (rql.Contains("nothing") == false)
                Assert.NotEmpty(expected);
            Assert.False(actual.HasWinners);
        }

        // the order of the plain query, and the values of the order by fields as the index sorts by them
        foreach (var (orderBy, values, exact) in new (string OrderBy, Func<User, object[]> Values, bool Exact)[]
                 {
                     ("order by Name", u => [u.Name.Split(' ').Min(StringComparer.Ordinal)], false),
                     ("order by Rank as long desc", u => [u.Rank], true),
                     ("order by Tag, Rank as long", u => [u.Tag.ToLowerInvariant(), u.Rank], true),
                     ("order by Country desc, Tag", u => [u.Country.ToLowerInvariant(), u.Tag.ToLowerInvariant()], true)
                 })
        {
            var rql = $"from index 'Users' where Tag in ('t1', 't2', 't3', 't4') {orderBy}";
            using var session = new JoinFieldScanTests.ScanSession(database, "Users", rql);
            var expected = JoinScanOracle.IdRanks(store, rql, (User u) => u.Id, values);
            using var actual = session.Reader.ScanIdsAndRanks(session.Query, session.DocumentsContext, CancellationToken.None);
            if (exact)
                AssertSameKeys(rql, expected, actual);
            else if (JoinTestHelpers.IndexedInOneBatch(store, "Users"))
                AssertRanksFollowTheOrder(rql, expected, actual);
            else
                // the index cut the batch of the bulk insert (low memory): the stored sort term of a multi-term entry is not its smallest term
                Assert.True(expected.Count == actual.Count, $"[{rql}] {expected.Count} keys expected, {actual.Count} found");
            Assert.NotEmpty(expected);
            Assert.True(expected.Select(x => x.Score).Distinct().Count() > 1, "the ranks differ");
            Assert.True(expected.Select(x => x.Score).GroupBy(x => x).Any(x => x.Count() > 1), "and some are shared (ties)");
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task TheScoreMapAndTheInOverTheKeyStoreMatchTheEntriesOfTheKeys()
    {
        using var store = Seed();
        var database = await Databases.GetDocumentDatabaseInstanceFor(store);

        // the key stores of an id join and of field joins, matched in the games index
        using var gamesSession = new JoinFieldScanTests.ScanSession(database, "Games", "from index 'Games'");
        var searcher = gamesSession.Searcher;

        foreach (var (field, inner) in new[] { ("OwnerId", (string)null), ("Country", "Country"), ("Postcode", "Postcode"), ("Tags", "Tags"), ("Long", "Long"), ("Nullable", "Nullable") })
        {
            using var usersSession = new JoinFieldScanTests.ScanSession(database, "Users", "from index 'Users' where search(Name, 'w2 w6 w15 w40 w120 w400') or Tag = 't7'");
            using var keys = inner == null
                ? usersSession.Reader.ScanIdsAndScores(usersSession.Query, usersSession.DocumentsContext, CancellationToken.None)
                : usersSession.Reader.ScanTermScores(usersSession.Query, usersSession.DocumentsContext, inner, JoinFieldScan.Auto, false, CancellationToken.None);
            Assert.True(keys.Count > 0, field);

            var strings = new List<(string Term, float Score)>();
            for (var i = 0; i < keys.Count; i++)
                strings.Add((Encoding.UTF8.GetString(keys.Term(i)), keys.Score(i)));

            var metadata = searcher.FieldMetadataBuilder(field);
            // the term query of every key, in the order of the bytes of the keys, adds the score of the key to the entries under it
            var termField = inner != null ? metadata.ChangeAnalyzer(FieldIndexingMode.Exact) : metadata;
            var viaTermQueries = new Dictionary<long, float>();
            foreach (var (term, score) in strings.OrderBy(x => Encoding.UTF8.GetBytes(x.Term), Comparer<byte[]>.Create((x, y) => x.AsSpan().SequenceCompareTo(y))))
            {
                foreach (var entry in Entries(searcher.TermQuery(termField, term)))
                    viaTermQueries[entry] = viaTermQueries.GetValueOrDefault(entry) + score;
            }

            var viaBytes = Drain(searcher.ScoreMap(metadata, keys, inner != null));
            Assert.True(viaTermQueries.Count > 0, field);
            Assert.Equal(viaTermQueries.Count, viaBytes.Count);
            foreach (var (entry, score) in viaTermQueries)
            {
                Assert.True(viaBytes.TryGetValue(entry, out var other), $"{field}: entry {entry} is missing");
                // an entry under several keys adds up their scores
                Assert.True(Math.Abs(score - other) <= 1e-5f * Math.Max(1f, Math.Abs(score)), $"{field}: entry {entry} {score} vs {other}");
            }

            var inStrings = new List<(string Term, bool Exact)>(strings.Select(x => (x.Term, inner != null)));
            var plainStrings = Entries(searcher.InQuery(metadata, inStrings));
            var plainBytes = Entries(searcher.InQuery(metadata, keys, inner != null));
            Assert.Equal(plainStrings.OrderBy(x => x), plainBytes.OrderBy(x => x));
            Assert.Equal(viaTermQueries.Keys.OrderBy(x => x), plainBytes.OrderBy(x => x));
        }
    }

    private static Dictionary<long, float> Drain(ScoreMapMatch match)
    {
        var result = new Dictionary<long, float>();
        var ids = new long[4096];
        var scores = new float[4096];
        int read;
        while ((read = match.Fill(ids)) > 0)
        {
            var span = ids.AsSpan(0, read).ToArray();
            scores.AsSpan(0, read).Clear();
            match.Score(span, scores.AsSpan(0, read), 1f);
            for (var i = 0; i < read; i++)
                result[span[i]] = scores[i];
        }

        return result;
    }

    private static List<long> Entries(IQueryMatch match)
    {
        var result = new List<long>();
        var ids = new long[4096];
        int read;
        while ((read = match.Fill(ids)) > 0)
            result.AddRange(ids.AsSpan(0, read).ToArray());
        return result.Distinct().ToList();
    }

    // The same keys, dense ranks, and a rank that never goes down along the expected order
    private static void AssertRanksFollowTheOrder(string label, List<(string Key, float Score)> expected, JoinTermScores actual)
    {
        Assert.True(expected.Count == actual.Count, $"[{label}] {expected.Count} keys expected, {actual.Count} found");
        var actualByKey = new Dictionary<string, float>(StringComparer.Ordinal);
        for (var i = 0; i < actual.Count; i++)
            actualByKey[Encoding.UTF8.GetString(actual.Term(i))] = actual.Score(i);

        var previous = 0f;
        foreach (var group in expected.GroupBy(x => x.Score).OrderBy(x => x.Key))
        {
            var ranks = group.Select(x => actualByKey[x.Key]).Distinct().ToList();
            Assert.True(ranks.Count == 1, $"[{label}] the keys of the rank {group.Key} are not tied: {string.Join(", ", ranks)}");
            Assert.True(ranks[0] >= previous, $"[{label}] the rank {ranks[0]} of the keys of the rank {group.Key} is lower than {previous}");
            previous = ranks[0];
        }

        var distinct = actualByKey.Values.Distinct().OrderBy(x => x).ToList();
        Assert.True(distinct.SequenceEqual(Enumerable.Range(1, distinct.Count).Select(x => (float)x)), $"[{label}] the ranks are not dense");
    }

    private static void AssertSameKeys(string label, List<(string Key, float Score)> expected, JoinTermScores actual)
    {
        Assert.True(expected.Count == actual.Count, $"[{label}] {expected.Count} keys expected, {actual.Count} found");
        var byKey = expected.ToDictionary(x => x.Key, x => x.Score, StringComparer.Ordinal);
        Assert.Equal(expected.Count, byKey.Count);
        for (var i = 0; i < actual.Count; i++)
        {
            var key = Encoding.UTF8.GetString(actual.Term(i));
            Assert.True(byKey.TryGetValue(key, out var score), $"[{label}] unexpected key '{key}'");
            Assert.True(BitConverter.SingleToInt32Bits(score) == BitConverter.SingleToInt32Bits(actual.Score(i)), $"[{label}] '{key}': expected {score}, got {actual.Score(i)}");
            Assert.True(actual.TryFind(Encoding.UTF8.GetBytes(key), out var found) && found == i, $"[{label}] '{key}' is not found by its bytes");
        }
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void IdsThatAreNotAsciiAndDifferInCaseJoinLikeAsciiOnes()
    {
        using var store = Seed();
        var users = UserData();
        var depts = DeptData();
        var tagged = users.Where(u => u.Tag is "t1" or "t2" or "t3").Select(u => Lower(u.Id)).ToHashSet();
        var taggedDepts = depts.Where(d => d.Tag is "d4" or "d9").Select(d => Lower(d.Id)).ToHashSet();
        var games = GameData();

        Assert.Contains(users, u => u.Id.Any(c => c > 127) && u.Tag is "t1" or "t2" or "t3");
        Assert.Contains(games, g => g.OwnerId != null && g.OwnerId.Any(c => c > 127) && g.OwnerId != Lower(g.OwnerId) && tagged.Contains(Lower(g.OwnerId)));

        const string usersJoin = "join (from index 'Users' where Tag in ('t1', 't2', 't3')) as u on g.OwnerId = id(u)";
        const string deptsJoin = "join (from index 'Depts' where Tag in ('d4', 'd9')) as d on g.DeptId = id(d)";

        // a join on the id: scores not needed (a plain IN), scores needed (the score map), the same rows
        var expectedOwner = games.Where(g => g.OwnerId != null && tagged.Contains(Lower(g.OwnerId))).Select(g => g.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(expectedOwner);
        Assert.Equal(expectedOwner, Ids(store, $"{GamesFrom} {usersJoin} {Everything}"));
        Assert.Equal(expectedOwner, Ids(store, $"{GamesFrom} {usersJoin} order by score() {Everything}"));
        Assert.Equal(expectedOwner, Ids(store, $"{GamesFrom} {usersJoin} order by u.Rank as long {Everything}"));

        // the OR: a row is returned when either join matched it, the alias is the joined document only when that join did
        var expectedOr = games.Where(g => (g.OwnerId != null && tagged.Contains(Lower(g.OwnerId))) || (g.DeptId != null && taggedDepts.Contains(Lower(g.DeptId)))).Select(g => g.Id)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedOr, Ids(store, $"{GamesFrom} {usersJoin} {deptsJoin} where matches(u) or matches(d) order by score() {Everything}"));

        using var session = store.OpenSession();
        var rows = session.Advanced.RawQuery<Projection>($"{GamesFrom} {usersJoin} {deptsJoin} where matches(u) or matches(d) select g.Name as Name, u.Name as UserName, d.Name as DeptName {Everything}").ToList()
            .ToDictionary(x => x.Name.Split(' ')[0]);
        var userByLowerId = users.ToDictionary(u => Lower(u.Id));
        var deptByLowerId = depts.ToDictionary(d => Lower(d.Id));
        var withNonAscii = 0;
        foreach (var g in games)
        {
            var key = g.Id.Replace("games/", "G");
            var ownerMatched = g.OwnerId != null && tagged.Contains(Lower(g.OwnerId));
            var deptMatched = g.DeptId != null && taggedDepts.Contains(Lower(g.DeptId));
            if (ownerMatched == false && deptMatched == false)
            {
                Assert.False(rows.ContainsKey(key), key);
                continue;
            }

            Assert.True(rows.TryGetValue(key, out var row), key);
            Assert.Equal(ownerMatched ? userByLowerId[Lower(g.OwnerId)].Name : null, row.UserName);
            Assert.Equal(deptMatched ? deptByLowerId[Lower(g.DeptId)].Name : null, row.DeptName);
            if (ownerMatched && g.OwnerId.Any(c => c > 127) && g.OwnerId != Lower(g.OwnerId))
                withNonAscii++;
        }

        Assert.True(withNonAscii > 0, "the data has a row that only the analyzed spelling of a non-ASCII id can find");
        output.WriteLine($"{rows.Count} rows, {withNonAscii} with an upper-case non-ASCII owner id");
    }

    private static string Lower(string id) => id.ToLowerInvariant();

    private static List<string> Ids(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<Game>(rql).ToList().Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }


    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task TheMapGuardAgreesWithTheFlagOnTheData()
    {
        using var store = Seed();
        var database = await Databases.GetDocumentDatabaseInstanceFor(store);
        using var session = new JoinFieldScanTests.ScanSession(database, "Users", "from index 'Users'");
        var searcher = session.Searcher;

        foreach (var field in new[] { "Country", "Postcode", "Long", "Nullable", "Tag", "Rank" })
        {
            Assert.True(searcher.FieldHoldsOneTermPerEntry(field), field);
            Assert.True(searcher.EntryTermMapCoversPostings(field), field);
        }

        // the flag says several for these, and the data says the same
        foreach (var field in new[] { "Tags", "Text", "Name" })
        {
            Assert.False(searcher.FieldHoldsOneTermPerEntry(field), field);
            Assert.False(searcher.EntryTermMapCoversPostings(field), field);
        }

        Assert.True(searcher.EntryTermMapCoversPostings("NoSuchField")); // nothing in the map, nothing in the postings
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task AnIndexWrittenBeforeTheFlagExistedIsNotReadThroughTheMap()
    {
        using var store = SeedUsersOnly();
        var database = await Databases.GetDocumentDatabaseInstanceFor(store);
        var index = database.IndexStore.GetIndex("Users");

        using (var tx = index._environment.WriteTransaction())
        {
            var tree = tx.ReadTree(global::Corax.Constants.IndexWriter.MultipleTermsInField);
            Assert.NotNull(tree);
            foreach (var field in new[] { "Tags", "Text", "Name" })
            {
                using var scope = global::Voron.Slice.From(tx.Allocator, field, out var name);
                tree.Delete(name);
            }

            tx.Commit();
        }

        foreach (var field in new[] { "Tags", "Text", "Name" })
        {
            foreach (var rql in new[] { "from index 'Users' where search(Name, 'w2 w6 w15 w40 w120 w400')", "from index 'Users' where Tag = 't3'", "from index 'Users'" })
            {
                using var session = new JoinFieldScanTests.ScanSession(database, "Users", rql);
                Assert.True(session.Searcher.FieldHoldsOneTermPerEntry(field), $"{field}: the record is gone, the field looks single-valued");
                Assert.False(session.Searcher.EntryTermMapCoversPostings(field), $"{field}: the data says it is not");

                var expected = JoinScanOracle.TermScores(store, session, rql, field, true, out var expectedWinners);
                Assert.NotEmpty(expected);
                foreach (var strategy in new[] { JoinFieldScan.Auto, JoinFieldScan.BulkMap })
                {
                    using var actual = session.Reader.ScanTermScores(session.Query, session.DocumentsContext, field, strategy, true, CancellationToken.None);
                    AssertSameKeys($"{field} / {strategy} / {rql}", expected, actual);
                    for (var i = 0; i < actual.Count; i++)
                        Assert.Equal(expectedWinners[Encoding.UTF8.GetString(actual.Term(i))], session.Reader.GetDocumentIdOfEntry(actual.WinnerEntryId(i)));
                }
            }
        }

        // single-valued fields are still read through the map
        using var check = new JoinFieldScanTests.ScanSession(database, "Users", "from index 'Users'");
        Assert.True(check.Searcher.EntryTermMapCoversPostings("Country"));
    }


    private const string UsersJoin = "join (from index 'Users' where Tag in ('t1', 't2', 't3')) as u on g.OwnerId = id(u)";
    private const string DeptsJoin = "join (from index 'Depts' where Tag in ('d4', 'd9')) as d on g.DeptId = id(d)";

    private static string FieldJoin(string field, string where = "search(Name, 'w2 w6 w15 w40 w120 w400')") =>
        $"join (from index 'Users' where {where}) as u on g.{field} = u.{field}";

    private static readonly (string Name, string Rql)[] Battery =
    [
        ("id join", $"{GamesFrom} {UsersJoin} {Everything}"),
        ("id join, scores", $"{GamesFrom} join (from index 'Users' where search(Name, 'w2 w6 w15 w40 w120 w400')) as u on g.OwnerId = id(u) where search(g.Name, 'w1 w4 w12 w30') order by score() {Everything}"),
        ("id join, projection", $"{GamesFrom} {UsersJoin} select g.Name as Name, u.Name as UserName {Everything}"),
        ("id join, inner sort", $"{GamesFrom} {UsersJoin} order by u.Rank as long, g.Country {Everything}"),
        ("id join, inner sort by text desc", $"{GamesFrom} {UsersJoin} order by u.Name desc, g.Name {Everything}"),
        ("two joins, OR", $"{GamesFrom} {UsersJoin} {DeptsJoin} where matches(u) or matches(d) order by score() {Everything}"),
        ("two joins, OR, projection", $"{GamesFrom} {UsersJoin} {DeptsJoin} where matches(u) or matches(d) select g.Name as Name, u.Name as UserName, d.Name as DeptName {Everything}"),
        ("two joins, AND", $"{GamesFrom} {UsersJoin} {DeptsJoin} order by score() {Everything}"),
        ("field join Country", $"{GamesFrom} {FieldJoin("Country")} order by score() {Everything}"),
        ("field join Country, projection", $"{GamesFrom} {FieldJoin("Country")} select g.Name as Name, u.Name as UserName {Everything}"),
        ("field join Postcode", $"{GamesFrom} {FieldJoin("Postcode")} order by score() {Everything}"),
        ("field join Postcode, projection", $"{GamesFrom} {FieldJoin("Postcode")} select g.Name as Name, u.Name as UserName {Everything}"),
        ("field join Postcode, no scores", $"{GamesFrom} {FieldJoin("Postcode", "Tag = 't5'")} where g.Country = 'NL' {Everything}"),
        ("field join Tags (arrays)", $"{GamesFrom} {FieldJoin("Tags")} order by score() {Everything}"),
        ("field join Tags (arrays), no scores", $"{GamesFrom} {FieldJoin("Tags", "Tag = 't5'")} {Everything}"),
        ("field join Long", $"{GamesFrom} {FieldJoin("Long")} order by score() {Everything}"),
        ("field join Long, projection", $"{GamesFrom} {FieldJoin("Long")} select g.Name as Name, u.Name as UserName {Everything}"),
        ("field join Nullable", $"{GamesFrom} {FieldJoin("Nullable")} order by score() {Everything}"),
        ("field join Nullable, projection", $"{GamesFrom} {FieldJoin("Nullable")} select g.Name as Name, u.Name as UserName {Everything}"),
        ("field join Nullable, no scores", $"{GamesFrom} {FieldJoin("Nullable", "Tag = 't2'")} {Everything}"),
        ("field join and id join", $"{GamesFrom} {FieldJoin("Postcode", "Tag = 't5'")} {DeptsJoin} order by score() {Everything}"),
        ("nothing matches", $"{GamesFrom} join (from index 'Users' where Tag = 'none') as u on g.OwnerId = id(u) {Everything}"),
        ("nothing matches, field", $"{GamesFrom} join (from index 'Users' where Tag = 'none') as u on g.Country = u.Country {Everything}"),
    ];

    [JoinBenchmarkFact("RAVEN_JOIN_DUMP", RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void DumpTheBatteryForAComparisonBetweenBuilds()
    {
        var path = Environment.GetEnvironmentVariable("RAVEN_JOIN_DUMP");

        using var store = Seed();
        var text = new StringBuilder();
        foreach (var (name, rql) in Battery)
        {
            text.AppendLine($"=== {name}");
            using var session = store.OpenSession();
            if (rql.Contains(" select "))
            {
                var rows = session.Advanced.RawQuery<Projection>(rql).ToList();
                foreach (var row in rows.OrderBy(x => x.Name, StringComparer.Ordinal))
                    text.AppendLine($"{row.Name}\t{row.UserName}\t{row.DeptName}");
            }
            else
            {
                var rows = new List<(string Id, double Score)>();
                foreach (var entity in session.Advanced.RawQuery<Game>(rql).ToList())
                {
                    var metadata = session.Advanced.GetMetadataFor(entity);
                    rows.Add((entity.Id, metadata.ContainsKey(Constants.Documents.Metadata.IndexScore) ? Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore]) : 0));
                }

                foreach (var row in rows.OrderBy(x => x.Id, StringComparer.Ordinal))
                    text.AppendLine($"{row.Id}\t{row.Score.ToString("R", CultureInfo.InvariantCulture)}");
            }
        }

        File.WriteAllText(path, text.ToString());
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheKeyStoresOfAJoinDoNotOutliveTheirTransaction()
    {
        using var store = Seed();

        var baseline = Battery.ToDictionary(x => x.Name, x => Fetch(store, x.Rql));
        Assert.True(baseline.Values.Count(x => x.Count > 0) >= Battery.Length - 2, "the battery returns rows");

        for (var round = 0; round < 3; round++)
        {
            var round1 = round;
            var failures = new List<string>();
            Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, worker =>
            {
                var shift = (worker * 5 + round1) % Battery.Length;
                foreach (var (name, rql) in Battery.Skip(shift).Concat(Battery.Take(shift)))
                {
                    var rows = Fetch(store, rql);
                    if (rows.SequenceEqual(baseline[name]) == false)
                    {
                        lock (failures)
                            failures.Add($"round {round1}, worker {worker}, '{name}': {rows.Count} rows instead of {baseline[name].Count}");
                    }
                }
            });

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

            // the memory of the finished transactions is handed out again
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AStreamKeepsTheKeyStoresOfItsJoinsWhileOtherQueriesRun()
    {
        using var store = Seed();
        var baseline = Battery.ToDictionary(x => x.Name, x => Fetch(store, x.Rql));

        foreach (var rql in new[]
                 {
                     $"{GamesFrom} {UsersJoin} {DeptsJoin} where matches(u) or matches(d) {Everything}",
                     $"{GamesFrom} {FieldJoin("Tags")} {Everything}",
                     $"{GamesFrom} {FieldJoin("Postcode")} {Everything}",
                     $"{GamesFrom} {UsersJoin} order by u.Rank as long, g.Country {Everything}"
                 })
        {
            List<string> Stream()
            {
                using var session = store.OpenSession();
                var ids = new List<string>();
                using var stream = session.Advanced.Stream<Game>(session.Advanced.RawQuery<Game>(rql));
                while (stream.MoveNext())
                    ids.Add(stream.Current.Id);
                return ids;
            }

            var expected = Stream();
            Assert.NotEmpty(expected);

            using var session = store.OpenSession();
            var ids = new List<string>();
            using var open = session.Advanced.Stream<Game>(session.Advanced.RawQuery<Game>(rql));
            while (open.MoveNext())
            {
                ids.Add(open.Current.Id);
                if (ids.Count % 700 != 1)
                    continue;

                // other transactions (and their key stores) come and go while this one is open
                foreach (var (name, other) in Battery.Where((_, i) => i % 3 == ids.Count / 700 % 3))
                    Assert.True(Fetch(store, other).SequenceEqual(baseline[name]), name);

                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            }

            Assert.Equal(expected, ids);
        }
    }

    private static List<(string Id, double Score, string Projection)> Fetch(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        if (rql.Contains(" select "))
        {
            return session.Advanced.RawQuery<Projection>(rql).ToList()
                .Select(x => (x.Name, 0d, $"{x.UserName}|{x.DeptName}")).OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
        }

        var rows = new List<(string Id, double Score, string Projection)>();
        foreach (var entity in session.Advanced.RawQuery<Game>(rql).ToList())
        {
            var metadata = session.Advanced.GetMetadataFor(entity);
            rows.Add((entity.Id, metadata.ContainsKey(Constants.Documents.Metadata.IndexScore) ? Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore]) : 0, null));
        }

        // ties of a score or of a rank come back in the same order every time
        return rows.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
    }


    private sealed class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Tag { get; set; }
        public long Rank { get; set; }
        public string Country { get; set; }
        public string Postcode { get; set; }
        public string[] Tags { get; set; }
        public string Text { get; set; }
        public string Long { get; set; }
        public string Nullable { get; set; }
    }

    private sealed class Dept
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Tag { get; set; }
    }

    private sealed class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string DeptId { get; set; }
        public string Country { get; set; }
        public string Postcode { get; set; }
        public string[] Tags { get; set; }
        public string Long { get; set; }
        public string Nullable { get; set; }
    }

    private sealed class Projection
    {
        public string Name { get; set; }
        public string UserName { get; set; }
        public string DeptName { get; set; }
    }

    private static readonly string[] Spellings = ["Ärger", "ÉCOLE", "ñandú", "Ünï", "Ωmega", "straße", "Ölçü", "Çelik", "plain"];

    private static List<User> UserData()
    {
        var generator = new JoinScoreOrderTests.Generator(41);
        var names = generator.Users(Users);
        var random = new Random(8);
        return Enumerable.Range(0, Users).Select(i => new User
        {
            // some ids hold letters that are not ASCII, in upper and in lower case
            Id = i % 4 == 0 ? $"users/{Spellings[i / 4 % Spellings.Length]}-{i}" : $"users/{i}",
            Name = names[i].Name,
            Tag = $"t{i % 17}",
            Rank = i % 50, // many ties
            Country = Countries[random.Next(Countries.Length)],
            Postcode = $"pc{random.Next(300)}",
            Tags = random.Next(10) == 0 ? [] : Enumerable.Range(0, 1 + random.Next(4)).Select(_ => "t" + random.Next(60)).ToArray(),
            Text = string.Join(' ', Enumerable.Range(0, 1 + random.Next(3)).Select(_ => "x" + random.Next(50))),
            Long = random.Next(6) == 0 ? null : LongValues[random.Next(LongValues.Length)],
            Nullable = random.Next(5) switch { 0 => null, 1 => "", _ => "v" + random.Next(40) }
        }).ToList();
    }

    private static List<Dept> DeptData() => Enumerable.Range(0, Depts).Select(i => new Dept
    {
        Id = i % 3 == 0 ? $"depts/{Spellings[i / 3 % Spellings.Length]}-{i}" : $"depts/{i}",
        Name = $"dept{i % 30} common",
        Tag = $"d{i % 15}"
    }).ToList();

    private static List<Game> GameData()
    {
        var generator = new JoinScoreOrderTests.Generator(42);
        var names = generator.Games(Games, Users);
        var users = UserData();
        var depts = DeptData();
        var random = new Random(9);
        return Enumerable.Range(0, Games).Select(i =>
        {
            var owner = users[random.Next(Users)].Id;
            var dept = depts[random.Next(Depts)].Id;
            return new Game
            {
                Id = $"games/{i}",
                Name = $"G{i} {names[i].Name}",
                // the spelling of the id that the game holds differs from the one of the document in case
                OwnerId = random.Next(15) switch { 0 => null, 1 => "users/missing" + i, 2 => owner.ToUpperInvariant(), 3 => owner.ToLowerInvariant(), _ => owner },
                DeptId = random.Next(15) switch { 0 => null, 1 => "depts/missing" + i, 2 => dept.ToUpperInvariant(), _ => dept },
                Country = Countries[random.Next(Countries.Length)],
                Postcode = $"pc{random.Next(300)}",
                Tags = random.Next(10) == 0 ? [] : Enumerable.Range(0, 1 + random.Next(4)).Select(_ => "t" + random.Next(60)).ToArray(),
                Long = random.Next(6) == 0 ? null : LongValues[random.Next(LongValues.Length)],
                Nullable = random.Next(5) switch { 0 => null, 1 => "", _ => "v" + random.Next(40) }
            };
        }).ToList();
    }

    // the tests that only read share one database (see SharedJoinDatabase); the one that edits the index has its own
    private IDocumentStore Seed() => shared.Get("seed", CoraxOptions(), o => GetDocumentStore(o), Populate);

    private IDocumentStore SeedUsersOnly()
    {
        var store = GetDocumentStore(CoraxOptions());
        new UsersIndex().Execute(store);
        // indexing is stopped while the documents are stored, so that the load of the machine does not cut them into several batches
        store.Maintenance.Send(new StopIndexingOperation());
        using (var bulk = store.BulkInsert())
        {
            foreach (var user in UserData())
                    bulk.Store(user, user.Id);
        }

        JoinTestHelpers.StartIndexingAndWait(this, store);
        return store;
    }

    private void Populate(IDocumentStore store)
    {
        new UsersIndex().Execute(store);
        new DeptsIndex().Execute(store);
        new GamesIndex().Execute(store);

        // indexing is stopped while the documents are stored, so that the load of the machine does not cut them into several batches
        store.Maintenance.Send(new StopIndexingOperation());
        using (var bulk = store.BulkInsert())
        {
            foreach (var user in UserData())
                    bulk.Store(user, user.Id);
            foreach (var dept in DeptData())
                bulk.Store(dept, dept.Id);
            foreach (var game in GameData())
                bulk.Store(game, game.Id);
        }

        JoinTestHelpers.StartIndexingAndWait(this, store);
        Indexes.WaitForIndexing(store);
    }

    private sealed class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Tag, u.Rank, u.Country, u.Postcode, u.Tags, u.Text, u.Long, u.Nullable };
            Index(x => x.Name, FieldIndexing.Search);
            Index(x => x.Text, FieldIndexing.Search);
            Index(x => x.Long, FieldIndexing.Exact);
            Index(x => x.Nullable, FieldIndexing.Exact);
        }

        public override string IndexName => "Users";
    }

    private sealed class DeptsIndex : AbstractIndexCreationTask<Dept>
    {
        public DeptsIndex()
        {
            Map = depts => from d in depts select new { d.Name, d.Tag };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Depts";
    }

    private sealed class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.DeptId, g.Country, g.Postcode, g.Tags, g.Long, g.Nullable };
            Index(x => x.Name, FieldIndexing.Search);
            Index(x => x.Long, FieldIndexing.Exact);
            Index(x => x.Nullable, FieldIndexing.Exact);
        }

        public override string IndexName => "Games";
    }
}
