using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;

namespace FastTests.Corax;

// ORDER BY a field of the joined index of a JOIN on the id or on a field, inner or left
public class JoinInnerSortTests(ITestOutputHelper output, SharedJoinDatabase shared) : RavenTestBase(output), IClassFixture<SharedJoinDatabase>
{
    private const string Everything = "limit 0, 1000000";
    private const int Vocabulary = 5000;
    private const int Tags = 300;
    private static readonly string[] Countries = ["nl", "de", "fr", "be", "uk", "es", "it"];

    private const string InnerSearch = "w2 w6 w15 w40 w120 w400";
    private const string OuterSearch = "w1 w4 w12 w30 w90 w300";

    private const string Games = "from index 'Games' as g";
    private const string InnerQuery = $"from index 'Users' where search(Name, '{InnerSearch}')";
    private const string Join = $"join ({InnerQuery}) as u on g.OwnerId = id(u)";

    // a join on a field matches many games to one user
    private const string FieldInnerQuery = $"from index 'Users' where Rank < 12 and search(Name, '{InnerSearch}')";

    private enum Shape { Id, Field, FieldList, LeftId, LeftField }

    private static string JoinOf(Shape shape) => shape switch
    {
        Shape.Id => Join,
        Shape.Field => $"join ({FieldInnerQuery}) as u on g.PublisherId = u.CompanyId",
        Shape.FieldList => $"join ({FieldInnerQuery}) as u on g.PublisherIds = u.CompanyId",
        Shape.LeftId => $"left join ({InnerQuery}) as u on g.OwnerId = id(u)",
        Shape.LeftField => $"left join ({FieldInnerQuery}) as u on g.PublisherId = u.CompanyId",
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    private sealed record Part(string Alias, string Field, string Type = null, bool Desc = false)
    {
        public bool IsInner => Alias == "u";
        public string Rql(bool withAlias = true) => $"{(withAlias ? Alias + "." : "")}{Field}{(Type != null ? " as " + Type : "")}{(Desc ? " desc" : "")}";
    }

    private static Part U(string field, string type = null, bool desc = false) => new("u", field, type, desc);
    private static Part G(string field, string type = null, bool desc = false) => new("g", field, type, desc);

    private sealed record Case(string Name, Part[] Order, string OuterWhere = null, Shape Shape = Shape.Id, bool Project = false)
    {
        public string Sub => Shape is Shape.Id or Shape.LeftId ? InnerQuery : FieldInnerQuery;
        public string Outer => Shape == Shape.FieldList ? "from index 'GamesLists' as g" : Games;
        public bool IsLeft => Shape is Shape.LeftId or Shape.LeftField;
    }

    private static readonly Case[] Cases =
    [
        new("tag", [U("Tag")]),
        new("tag desc", [U("Tag", desc: true)]),
        new("tag then rank", [U("Tag"), G("Rank", "long")]),
        new("tag desc then rank desc", [U("Tag", desc: true), G("Rank", "long", true)]),
        new("country then tag", [G("Country"), U("Tag")]),
        new("country desc, tag desc, rank", [G("Country", desc: true), U("Tag", desc: true), G("Rank", "long")]),
        new("two joined fields", [U("Tag"), U("Rank", "long", true)]),
        new("two joined fields then one of the games", [U("Tag"), U("Rank", "long"), G("Rank", "long", true)]),
        new("joined number", [U("Rank", "long")]),
        new("joined number desc then country", [U("Rank", "long", true), G("Country")]),
        new("joined double", [U("Rank", "double")]),
        new("joined field that is analyzed (one term per value)", [U("TagText"), G("Rank", "long")]),
        new("joined field that is analyzed desc", [U("TagText", desc: true), G("Country")]),
        new("games that match a search (the relevance must not leak into the order)", [U("Tag"), G("Rank", "long")], $"search(g.Name, '{OuterSearch}')"),
        new("games that match a range", [U("Tag", desc: true), G("Country"), G("Rank", "long")], "g.Rank > 500"),
    ];

    private static readonly Case[] OtherShapes =
    [
        new("field join: tag", [U("Tag")], Shape: Shape.Field, Project: true),
        new("field join: tag desc", [U("Tag", desc: true)], Shape: Shape.Field, Project: true),
        new("field join: tag then rank", [U("Tag"), G("Rank", "long")], Shape: Shape.Field),
        new("field join: country then tag desc", [G("Country"), U("Tag", desc: true)], Shape: Shape.Field),
        new("field join: two joined fields", [U("Tag"), U("Rank", "long", true)], Shape: Shape.Field),
        new("field join: joined number desc then rank", [U("Rank", "long", true), G("Rank", "long")], Shape: Shape.Field),
        new("field join: games that match a search", [U("Tag"), G("Rank", "long")], $"search(g.Name, '{OuterSearch}')", Shape.Field),
        new("field join over a list: the smallest rank of the values", [U("Tag"), G("Rank", "long")], Shape: Shape.FieldList),
        new("field join over a list desc", [U("Tag", desc: true), G("Rank", "long", true)], Shape: Shape.FieldList),
        new("left join: tag (unmatched last)", [U("Tag")], Shape: Shape.LeftId, Project: true),
        new("left join: tag desc (unmatched last)", [U("Tag", desc: true)], Shape: Shape.LeftId, Project: true),
        new("left join: tag then rank", [U("Tag"), G("Rank", "long")], Shape: Shape.LeftId),
        new("left join: country then tag desc", [G("Country"), U("Tag", desc: true)], Shape: Shape.LeftId),
        new("left join: joined number", [U("Rank", "long")], Shape: Shape.LeftId),
        new("left join: games that match a search", [U("Tag", desc: true), G("Rank", "long")], $"search(g.Name, '{OuterSearch}')", Shape.LeftId),
        new("left join on a field: tag", [U("Tag")], Shape: Shape.LeftField, Project: true),
        new("left join on a field: tag desc then rank", [U("Tag", desc: true), G("Rank", "long")], Shape: Shape.LeftField),
        new("left join on a field: country then tag", [G("Country"), U("Tag")], Shape: Shape.LeftField),
    ];

    public static IEnumerable<object[]> CaseNames => Cases.Select(x => new object[] { x.Name });

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByTheJoinedIndexAt20k()
    {
        using var store = SeedShared();
        foreach (var c in Cases)
            Verify(store, c);
    }

    // a database of its own: the tests that share one are short
    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByTheJoinedIndexOfAJoinOnAFieldAndOfALeftJoinAt20k()
    {
        using var store = Seed(users: 6_000, games: 20_000, seed: 20);
        foreach (var c in OtherShapes)
            Verify(store, c);
    }

    [JoinBenchmarkFact("RAVEN_JOIN_INNER_SORT_BIG", RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByTheJoinedIndexAt200k()
    {
        using var store = Seed(users: 200_000, games: 200_000, seed: 200);
        foreach (var c in Cases.Where(x => x.Name is "tag" or "tag desc" or "country then tag" or "two joined fields" or "joined number desc then country"))
            Verify(store, c);

        Measure(store);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByAnAnalyzedFieldOfTheJoinedIndex()
    {
        using var store = SeedShared();
        var inner = Fetch<User>(store, $"{InnerQuery} order by Name {Everything}");
        output.WriteLine("inner order by Name: " + string.Join(" | ", inner.Take(40).Select(x => x.Name)));

        var joined = Fetch<Game>(store, $"{Games} {Join} order by u.Name {Everything}");
        var users = inner.Select((x, i) => (x.Id, Position: i)).ToDictionary(x => x.Id, x => x.Position);
        var owners = joined.Select(x => users[x.OwnerId]).ToList();
        var inversions = 0;
        for (var i = 1; i < owners.Count; i++)
            if (owners[i] < owners[i - 1])
                inversions++;
        output.WriteLine($"join rows {joined.Count}, owner positions going backwards: {inversions}");

        // The key an analyzed field sorts by is the one term the index keeps per entry, which no client can read
        var unordered = Fetch<Game>(store, $"{Games} {Join} {Everything}");
        Assert.Equal(unordered.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal), joined.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));
        var descending = Fetch<Game>(store, $"{Games} {Join} order by u.Name desc {Everything}");
        Assert.Equal(unordered.Count, descending.Count);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void WhatIsNotSupportedIsRejected()
    {
        using var store = SeedShared();

        void Rejects(string rql, string message)
        {
            var e = Assert.Throws<InvalidQueryException>(() => Fetch<Game>(store, rql));
            output.WriteLine(e.Message.Split('\n')[0]);
            Assert.Contains(message, e.Message);
        }

        Rejects($"{Games} {Join} order by u.Tag, g.Rank as long, u.Rank as long", "have to follow each other");
        Rejects($"{Games} {Join} join (from index 'Companies' where search(Name, 'a')) as p on g.PublisherId = id(p) order by u.Tag, p.Tag", "two joins");
        Rejects($"{Games} join (from index 'Users' as u join (from index 'Companies' where search(Name, 'a')) as c on u.CompanyId = id(c) where search(Name, '{InnerSearch}')) as u on g.OwnerId = id(u) order by u.Tag",
            "chain of joins");
        Rejects($"{Games} {Join} join (from index 'Companies' where search(Name, 'a')) as c on u.CompanyId = id(c) order by c.Tag", "chain of joins");
        Rejects($"{Games} {Join} join (from index 'Companies' where search(Name, 'a')) as c on u.CompanyId = id(c) order by u.Tag", "chain of joins");
        Rejects($"{Games} {Join} order by u.Nothing", "is not indexed");
        Rejects($"{Games} {Join} order by id(u)", "only the fields of a joined index");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByScoreIsUnchangedAndAnotherJoinCanStayUnordered()
    {
        using var store = SeedShared();
        var plain = Fetch<Game>(store, $"{Games} {Join} order by g.Rank as long, id() {Everything}");
        var withSecondJoin = Fetch<Game>(store,
            $"{Games} {Join} join (from index 'Companies' where search(Name, 'w1 w2 w3 w4')) as p on g.PublisherId = id(p) order by u.Tag {Everything}");
        var withoutOrder = Fetch<Game>(store,
            $"{Games} {Join} join (from index 'Companies' where search(Name, 'w1 w2 w3 w4')) as p on g.PublisherId = id(p) {Everything}");
        Assert.Equal(withoutOrder.Select(x => x.Id).OrderBy(x => x), withSecondJoin.Select(x => x.Id).OrderBy(x => x));
        Assert.True(withSecondJoin.Count > 0 && withSecondJoin.Count < plain.Count);

        var byScore = Fetch<Game>(store, $"{Games} {Join} where search(g.Name, '{OuterSearch}') order by score() {Everything}");
        Assert.True(byScore.Count > 0);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByAJoinedFieldWorksWhenTheJoinedFieldIsAlsoProjected()
    {
        using var store = SeedShared();
        var inner = Fetch<User>(store, $"{InnerQuery} order by Tag desc {Everything}");
        var innerTags = inner.Select(x => x.Tag).Distinct().ToList();

        using var session = store.OpenSession();
        var rows = session.Advanced.RawQuery<OwnerTagRow>($"{Games} {Join} order by u.Tag desc select id() as Id, u.Tag as OwnerTag {Everything}").ToList();
        Assert.True(rows.Count > 1000);
        var tags = new List<string>();
        foreach (var row in rows)
            if (tags.Count == 0 || tags[^1] != row.OwnerTag)
                tags.Add(row.OwnerTag);

        Assert.Equal(tags.Count, tags.Distinct().Count()); // every tag in one run
        Assert.Equal(innerTags.Where(tags.Contains), tags); // in the order of the joined index (tags no game points to are not there)
    }

    private sealed class OwnerTagRow
    {
        public string Id { get; set; }
        public string OwnerTag { get; set; }
    }


    private void Verify(IDocumentStore store, Case c)
    {
        var innerOrder = string.Join(", ", c.Order.Where(x => x.IsInner).Select(x => x.Rql(withAlias: false)));
        var order = string.Join(", ", c.Order.Select(x => x.Rql()));
        var where = c.OuterWhere == null ? "" : $"where {c.OuterWhere}";
        var join = JoinOf(c.Shape);

        // step 1: the joined index alone, with the same where and order by
        var inner = Fetch<User>(store, $"{c.Sub} order by {innerOrder} {Everything}");
        var userRank = new Dictionary<string, int>();
        var keyRank = new Dictionary<string, int>(); // a join on a field: the rank of the first user in the order that has the value
        var keyTag = new Dictionary<string, string>();
        string previous = null;
        var rank = 0;
        foreach (var user in inner)
        {
            var signature = string.Join("|", c.Order.Where(x => x.IsInner).Select(x => user.ValueOf(x.Field)));
            if (previous == null || signature != previous)
                rank++;
            previous = signature;
            userRank[user.Id] = rank;
            if (keyRank.TryAdd(user.CompanyId, rank))
                keyTag[user.CompanyId] = user.Tag;
        }

        int? RankOf(Game game)
        {
            switch (c.Shape)
            {
                case Shape.Id or Shape.LeftId:
                    return userRank.TryGetValue(game.OwnerId, out var owner) ? owner : null;
                case Shape.Field or Shape.LeftField:
                    return keyRank.TryGetValue(game.PublisherId, out var key) ? key : null;
                default:
                    int? best = null;
                    foreach (var publisher in game.PublisherIds)
                        if (keyRank.TryGetValue(publisher, out var value) && (best == null || value < best))
                            best = value;
                    return best;
            }
        }

        // a row of a left join without a joined document goes last, whatever the direction
        int Rank(Game game) => RankOf(game) ?? int.MaxValue;

        // step 2: the games alone: those that have a joined document (all of them for a left join)
        var everyGame = Fetch<Game>(store, $"{c.Outer} {where} {Everything}");
        var matching = c.IsLeft ? everyGame : everyGame.Where(x => RankOf(x) != null).ToList();
        Assert.True(matching.Count >= 2000, $"[{c.Name}] the check needs at least 2000 rows, the oracle has {matching.Count}");

        var distinctRanks = matching.Select(x => Rank(x)).Distinct().Count();
        output.WriteLine($"[{c.Name}] {inner.Count} users, {distinctRanks} distinct ranks among {matching.Count} games");
        Assert.True(distinctRanks >= 5, $"[{c.Name}] the check needs several distinct ranks, there are {distinctRanks}");
        if (c.IsLeft)
            Assert.True(matching.Any(x => RankOf(x) == null) && matching.Any(x => RankOf(x) != null), $"[{c.Name}] the check needs rows with and without a joined document");

        string Key(Game game)
        {
            var key = new StringBuilder();
            var ownerAdded = false;
            foreach (var part in c.Order)
            {
                if (part.IsInner)
                {
                    if (ownerAdded)
                        continue;
                    ownerAdded = true;
                    key.Append(Rank(game)).Append('|');
                }
                else
                {
                    key.Append(game.ValueOf(part.Field)).Append('|');
                }
            }

            return key.ToString();
        }

        int Compare(Game x, Game y)
        {
            var ownerCompared = false;
            foreach (var part in c.Order)
            {
                int result;
                if (part.IsInner)
                {
                    if (ownerCompared)
                        continue;
                    ownerCompared = true;
                    result = Rank(x).CompareTo(Rank(y));
                }
                else
                {
                    result = part.Field == "Country" ? string.CompareOrdinal(x.Country, y.Country) : x.Rank.CompareTo(y.Rank);
                    if (part.Desc)
                        result = -result;
                }

                if (result != 0)
                    return result;
            }

            return string.CompareOrdinal(x.Id, y.Id);
        }

        var expected = matching.ToList();
        expected.Sort(Compare);
        var expectedKeys = expected.Select(Key).ToList();

        var all = Fetch<Game>(store, $"{c.Outer} {join} {where} order by {order} {Everything}");
        Assert.Equal(expected.Count, all.Count);
        Assert.Equal(all.Count, all.Select(x => x.Id).Distinct().Count());
        Assert.Equal(expected.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal), all.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));
        var actualKeys = all.Select(Key).ToList();
        for (var i = 0; i < expectedKeys.Count; i++)
            Assert.True(expectedKeys[i] == actualKeys[i], $"[{c.Name}] position {i}: the oracle has {expectedKeys[i]} but the join has {actualKeys[i]} ({all[i].Id}, owner {all[i].OwnerId})");

        if (c.Project)
        {
            // the projected joined document is the one the row is ordered by (null for a row without one)
            var users = inner.ToDictionary(x => x.Id);
            string ExpectedTag(Game game) => c.Shape switch
            {
                Shape.Id or Shape.LeftId => users.TryGetValue(game.OwnerId, out var user) ? user.Tag : null,
                _ => keyTag.GetValueOrDefault(game.PublisherId)
            };

            var byId = matching.ToDictionary(x => x.Id);
            using var session = store.OpenSession();
            var rows = session.Advanced.RawQuery<OwnerTagRow>($"{c.Outer} {join} {where} order by {order} select id() as Id, u.Tag as OwnerTag {Everything}").ToList();
            Assert.Equal(expected.Count, rows.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                var game = byId[rows[i].Id];
                Assert.True(expectedKeys[i] == Key(game), $"[{c.Name}] projection, position {i}: the oracle has {expectedKeys[i]} but the join has {Key(game)}");
                Assert.True(ExpectedTag(game) == rows[i].OwnerTag, $"[{c.Name}] projection of {game.Id}: the joined document has the tag '{ExpectedTag(game)}' but the row has '{rows[i].OwnerTag}'");
            }
        }

        foreach (var (skip, take) in new[] { (0, 20), (20, 20), (1000, 10) })
        {
            var page = Fetch<Game>(store, $"{c.Outer} {join} {where} order by {order} limit {skip}, {take}", statistics: out var stats);
            Assert.Equal(take, page.Count);
            Assert.Equal(expected.Count, stats.TotalResults);
            for (var i = 0; i < take; i++)
                Assert.True(expectedKeys[skip + i] == Key(page[i]), $"[{c.Name}] page {skip},{take} position {i}: the oracle has {expectedKeys[skip + i]} but the join has {Key(page[i])}");

            if (page.Select(x => x.Id).SequenceEqual(all.Skip(skip).Take(take).Select(x => x.Id)) == false)
                output.WriteLine($"[{c.Name}] note: page {skip},{take} holds other rows than the same positions of the full result (rows that tie on every sort field)");
        }
    }

    private static List<T> Fetch<T>(IDocumentStore store, string rql, (string Name, object Value)? parameter = null) => Fetch<T>(store, rql, parameter, out _);

    private static List<T> Fetch<T>(IDocumentStore store, string rql, out QueryStatistics statistics) => Fetch<T>(store, rql, null, out statistics);

    // one session per query: a session hands the same tracked entity to every query that returns the same id
    private static List<T> Fetch<T>(IDocumentStore store, string rql, (string Name, object Value)? parameter, out QueryStatistics statistics)
    {
        using var session = store.OpenSession();
        var query = session.Advanced.RawQuery<T>(rql).Statistics(out statistics);
        if (parameter is { } p)
            query.AddParameter(p.Name, p.Value);
        return query.ToList();
    }


    private void Measure(IDocumentStore store)
    {
        const int warmup = 3;
        const int runs = 9;
        var report = new StringBuilder();
        report.AppendLine($"## {DateTime.Now:yyyy-MM-dd HH:mm} 200k users, 200k games, page 20, median of {runs} runs after {warmup} warm-up (server ms; managed KB of the whole process, server and client)");
        report.AppendLine("| join | outer where | order by | total | server ms | scan ms | sub-query sort+fill ms | sort keys ms | ids ms | managed KB | join scan KB | join outer match KB |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");

        // the id join with every order, then the other joins that can order, without a where
        var cases = new List<(string Join, string Where, string Order)>();
        foreach (var where in new[] { $"where search(g.Name, '{OuterSearch}')", "" })
        foreach (var order in new[] { "u.Tag", "u.Tag desc", "u.Tag, g.Rank as long", "score()", "score(), g.Rank as long", "score(), u.Tag", "u.Tag, score()", "g.Country, g.Rank as long", "g.Rank as long" })
            cases.Add((Join, where, order));
        foreach (var shape in new[] { Shape.LeftId, Shape.Field, Shape.LeftField })
        foreach (var order in new[] { "u.Tag", "score(), u.Tag" })
            cases.Add((JoinOf(shape), "", order));

        foreach (var (join, where, order) in cases)
        {
            var rql = $"{Games} {join} {where} order by {order} limit 0, 20";
            var ms = new List<double>();
            var kb = new List<double>();
            long total = 0;
            for (var i = 0; i < warmup + runs; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var before = GC.GetTotalAllocatedBytes(precise: true);
                using var session = store.OpenSession();
                session.Advanced.RawQuery<Game>(rql).NoCaching().Statistics(out var stats).ToList();
                var after = GC.GetTotalAllocatedBytes(precise: true);
                total = stats.TotalResults;
                if (i < warmup)
                    continue;
                ms.Add(stats.DurationInMs);
                kb.Add((after - before) / 1024.0);
            }

            var scan = new List<double>();
            var fill = new List<double>();
            var keys = new List<double>();
            var ids = new List<double>();
            var scanKb = new List<double>();
            var matchKb = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                using var session = store.OpenSession();
                session.Advanced.RawQuery<Game>(rql + " include timings()").NoCaching().Timings(out var timings).ToList();
                var joined = timings.Timings["Join"].Timings["u"];
                scan.Add(joined.Timings["Scan"].DurationInMs);
                scanKb.Add(joined.Counters["ScanAllocatedManagedBytes"] / 1024.0);
                matchKb.Add(joined.Counters["OuterMatchAllocatedManagedBytes"] / 1024.0);
                fill.Add(Counter(joined, "RankSortAndFillMicroseconds") / 1000.0);
                keys.Add(Counter(joined, "RankSortKeysMicroseconds") / 1000.0);
                ids.Add(Counter(joined, "RankDocumentIdsMicroseconds") / 1000.0);
            }

            report.AppendLine($"| {(join == Join ? "id" : join.StartsWith("left") ? (join.Contains("id(u)") ? "left id" : "left field") : "field")} | {(where == "" ? "none" : "search")} | {order} | {total:N0} | {Median(ms):N0} | {Median(scan):N0} | {Median(fill):N0} | {Median(keys):N0} | {Median(ids):N0} | {Median(kb):N0} | {Median(scanKb):N0} | {Median(matchKb):N0} |");
        }

        output.WriteLine(report.ToString());
        JoinTestHelpers.AppendReport("RAVEN_JOIN_INNER_SORT_REPORT", "join-inner-sort-report.txt", report);
    }

    private static double Counter(QueryTimings scope, string name) => scope.Counters.TryGetValue(name, out var value) ? value : 0;


    // the tests only read: they share one database (see SharedJoinDatabase)
    private IDocumentStore SeedShared() => shared.Get("seed", CoraxOptions(), o => GetDocumentStore(o), store => Populate(store, users: 6_000, games: 20_000, seed: 20));

    private DocumentStore Seed(int users, int games, int seed)
    {
        var store = GetDocumentStore(CoraxOptions());
        Populate(store, users, games, seed);
        return store;
    }

    private static Options CoraxOptions()
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        if (Environment.GetEnvironmentVariable("RAVEN_JOIN_INNER_SORT_SCORES") == "1") // the cost of returning the scores
            options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        return options;
    }

    private void Populate(IDocumentStore store, int users, int games, int seed)
    {
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);
        new GamesListsIndex().Execute(store);
        new CompaniesIndex().Execute(store);

        var generator = new Generator(seed);
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < 200; i++)
                bulk.Store(generator.Company(i), $"companies/{i}");
            foreach (var user in generator.Users(users))
                bulk.Store(user, user.Id);
            foreach (var game in generator.Games(games, users))
                bulk.Store(game, game.Id);
        }

        Indexes.WaitForIndexing(store, timeout: TimeSpan.FromMinutes(30));
    }

    private sealed class Generator
    {
        private readonly Random _random;
        private readonly double[] _words = Cumulative(Vocabulary);
        private readonly double[] _tags = Cumulative(Tags);

        public Generator(int seed) => _random = new Random(seed);

        private static double[] Cumulative(int size)
        {
            var result = new double[size];
            var sum = 0.0;
            for (var i = 0; i < size; i++)
            {
                sum += 1.0 / (i + 1);
                result[i] = sum;
            }

            return result;
        }

        private int Zipf(double[] cumulative)
        {
            var target = _random.NextDouble() * cumulative[^1];
            var index = Array.BinarySearch(cumulative, target);
            if (index < 0)
                index = ~index;
            return Math.Min(index, cumulative.Length - 1);
        }

        // 1-12 words, skewed to short values; some values repeat one of their words
        private string Text()
        {
            var length = 1 + (int)(11 * Math.Pow(_random.NextDouble(), 1.7));
            var words = new List<string>();
            for (var i = 0; i < length; i++)
                words.Add($"w{Zipf(_words) + 1}");
            if (length >= 2 && _random.Next(100) < 30)
                words[_random.Next(length)] = words[_random.Next(length)];
            if (length >= 3 && _random.Next(100) < 10)
                words.Add(words[0]);
            return string.Join(' ', words);
        }

        // a few values are very common, 5% have none: many keys tie on it
        private string Tag() => _random.Next(100) < 5 ? null : $"t{Zipf(_tags)}";

        public Company Company(int i) => new() { Id = $"companies/{i}", Name = Text(), Tag = Tag() };

        public IEnumerable<User> Users(int count) =>
            Enumerable.Range(0, count).Select(i => new User
            {
                Id = $"users/{i}", Name = Text(), Tag = Tag(), Rank = _random.Next(60), Country = Countries[_random.Next(Countries.Length)], CompanyId = $"companies/{_random.Next(200)}"
            });

        public IEnumerable<Game> Games(int count, int users) =>
            Enumerable.Range(0, count).Select(i => new Game
            {
                Id = $"games/{i}",
                Name = Text(),
                // some games point to a user that does not exist
                OwnerId = _random.Next(25) == 0 ? $"users/missing{i}" : $"users/{_random.Next(users)}",
                PublisherId = $"companies/{_random.Next(200)}",
                PublisherIds = [$"companies/{i % 200}", $"companies/{(i * 7 + 13) % 200}"], // no random numbers: the games of the other tests stay what they were
                Country = Countries[_random.Next(Countries.Length)],
                Rank = _random.Next(1000)
            });
    }

    private class Doc
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Tag { get; set; }
        public string OwnerId { get; set; }
        public string PublisherId { get; set; }
        public string CompanyId { get; set; }
        public string[] PublisherIds { get; set; }
        public string Country { get; set; }
        public int Rank { get; set; }

        public string ValueOf(string field) => field switch { "Tag" or "TagText" => Tag, "Rank" => Rank.ToString(), "Name" => Name, "Country" => Country, _ => throw new ArgumentOutOfRangeException(field) };
    }

    private sealed class User : Doc;

    private sealed class Company : Doc;

    private sealed class Game : Doc;

    private sealed class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Tag, TagText = u.Tag, u.Country, u.Rank, u.CompanyId };
            Index(x => x.Name, FieldIndexing.Search);
            Index("TagText", FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    // the games with the list of publishers: a join on a list of keys
    private sealed class GamesListsIndex : AbstractIndexCreationTask<Game>
    {
        public GamesListsIndex()
        {
            Map = games => from g in games select new { g.Name, g.PublisherIds, g.Country, g.Rank };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "GamesLists";
    }

    private sealed class CompaniesIndex : AbstractIndexCreationTask<Company>
    {
        public CompaniesIndex()
        {
            Map = companies => from c in companies select new { c.Name, c.Tag };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Companies";
    }

    private sealed class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.PublisherId, g.Country, g.Rank };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
