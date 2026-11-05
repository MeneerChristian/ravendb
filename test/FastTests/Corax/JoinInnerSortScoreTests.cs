using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// A JOIN that orders by a field of the joined index sorts by the position of the joined key in that order
public class JoinInnerSortScoreTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Everything = "limit 0, 1000000";
    private const string InnerSearch = "w2 w6 w15 w40 w120 w400";
    private const string OuterSearch = "w1 w4 w12 w30 w90 w300";
    private const string Games = "from index 'Games' as g";
    private const string IdSub = $"from index 'Users' where search(Name, '{InnerSearch}')";
    // a join on a field has many games per joined document
    private const string FieldSub = $"from index 'Users' where Rank < 6 and search(Name, '{InnerSearch}')";
    private const string Join = $"join ({IdSub}) as u on g.OwnerId = id(u)";

    public static IEnumerable<object[]> Cases()
    {
        foreach (var order in new[] { "u.Tag", "u.Tag desc", "u.Tag, g.Rank as long" })
        foreach (var outer in new[] { false, true })
            yield return [order, outer];
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [MemberData(nameof(Cases))]
    public void TheReturnedScoreIsTheRelevanceNotTheRank(string order, bool withOuterSearch)
    {
        using var store = SeedOwn();
        var where = withOuterSearch ? $"where search(g.Name, '{OuterSearch}')" : "";

        var byScore = Fetch(store, $"{Games} {Join} {where} order by score() {Everything}");
        // the oracle is made of the plain queries: the score of the game alone plus the score of its owner alone
        var innerScore = Fetch(store, $"from index 'Users' where search(Name, '{InnerSearch}') order by score() {Everything}").ToDictionary(x => x.Id, x => x.Score);
        var outerScore = Fetch(store, $"{Games} {where} order by score() {Everything}").ToDictionary(x => x.Id, x => x.Score);
        foreach (var row in byScore)
            Assert.True(Math.Abs(row.Score - (outerScore[row.Id] + innerScore[row.OwnerId])) <= 1e-4, $"{row.Id}: order by score() gives {row.Score}, the plain queries give {outerScore[row.Id] + innerScore[row.OwnerId]}");

        var scoreOf = byScore.ToDictionary(x => x.Id, x => outerScore[x.Id] + innerScore[x.OwnerId]);
        Assert.True(byScore.Count > 500, $"the check needs at least 500 rows, there are {byScore.Count}");

        var ordered = Fetch(store, $"{Games} {Join} {where} order by {order} {Everything}");
        Assert.Equal(byScore.Count, ordered.Count);
        Assert.Equal(byScore.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal), ordered.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));

        // the order is the order of the joined index
        var inner = Fetch(store, $"from index 'Users' where search(Name, '{InnerSearch}') order by Tag {(order.Contains("u.Tag desc") ? "desc" : "")} {Everything}", expectScore: false);
        var position = new Dictionary<string, int>();
        string previous = null;
        var rank = 0;
        foreach (var user in inner)
        {
            if (rank == 0 || user.Tag != previous)
                rank++;
            previous = user.Tag;
            position[user.Id] = rank;
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            var a = position[ordered[i - 1].OwnerId];
            var b = position[ordered[i].OwnerId];
            Assert.True(a <= b, $"row {i}: the owner of {ordered[i - 1].Id} is at position {a} but the owner of {ordered[i].Id} is at {b}");
            if (a == b && order.Contains("g.Rank"))
                Assert.True(ordered[i - 1].Rank <= ordered[i].Rank, $"row {i}: rank {ordered[i - 1].Rank} before {ordered[i].Rank}");
        }

        var wrong = 0;
        foreach (var row in ordered)
        {
            if (Math.Abs(row.Score - scoreOf[row.Id]) > 1e-4)
            {
                if (wrong++ < 5)
                    output.WriteLine($"{row.Id}: returned {row.Score}, order by score() gives {scoreOf[row.Id]}");
            }
        }

        Assert.Equal(0, wrong);
        Assert.True(ordered.Select(x => x.Score).Distinct().Count() > 20, "the scores are relevance scores, not a handful of ranks");

        var page = Fetch(store, $"{Games} {Join} {where} order by {order} limit 20, 20", out var stats);
        Assert.Equal(20, page.Count);
        Assert.Equal(byScore.Count, stats.TotalResults);
        foreach (var row in page)
            Assert.True(Math.Abs(row.Score - scoreOf[row.Id]) <= 1e-4, $"{row.Id}: returned {row.Score}, order by score() gives {scoreOf[row.Id]}");
    }


    private static readonly string[] ScoreAndFieldOrders =
    [
        "score(), u.Tag",
        "score() desc, u.Tag",
        "score(), u.Tag desc",
        "u.Tag, score()",
        "u.Tag desc, score() desc",
        "score(), u.Tag, g.Rank as long",
        "g.Rank as long, score(), u.Tag",
        "u.Tag, g.Rank as long, score()",
        "g.Rank as long desc, u.Tag desc, score() desc",
    ];

    // The oracle is made of the plain queries
    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ScoreAndAJoinedFieldAreBothSortKeys()
    {
        using var store = SeedOwn();
        foreach (var (withOuterSearch, left, onField) in new[]
                 { (false, false, false), (true, false, false), (false, true, false), (true, true, false), (false, false, true), (true, false, true), (false, true, true), (true, true, true) })
        {
            // the rows of a left join that have no joined document sort after the others, and have the score of the outer query alone
            var sub = onField ? FieldSub : IdSub;
            var join = $"{(left ? "left " : "")}join ({sub}) as u on {(onField ? "g.PublisherId = u.CompanyId" : "g.OwnerId = id(u)")}";
            // a join on a field: the key is the company, its score the best of its users, its position the first of them in the order
            string KeyOf(Row game) => onField ? game.PublisherId : game.OwnerId;
            var where = withOuterSearch ? $"where search(g.Name, '{OuterSearch}')" : "";
            var innerScore = new Dictionary<string, double>();
            foreach (var user in Fetch(store, $"{sub} order by score() {Everything}"))
            {
                var key = onField ? user.CompanyId : user.Id;
                innerScore[key] = innerScore.TryGetValue(key, out var best) ? Math.Max(best, user.Score) : user.Score;
            }

            var outerScore = withOuterSearch
                ? Fetch(store, $"{Games} {where} order by score() {Everything}").ToDictionary(x => x.Id, x => x.Score)
                : null;
            var rows = Fetch(store, $"{Games} {join} {where} order by score() {Everything}");
            Assert.True(rows.Count > 500, $"the check needs at least 500 rows, there are {rows.Count}");
            if (left)
                Assert.Contains(rows, x => innerScore.ContainsKey(KeyOf(x)) == false);

            // the relevance of a row is the score of the outer query plus the score of its owner
            double offset = double.NaN;
            foreach (var row in rows)
            {
                var expected = (outerScore?[row.Id] ?? 0) + innerScore.GetValueOrDefault(KeyOf(row));
                if (withOuterSearch)
                    Assert.True(Math.Abs(row.Score - expected) <= 1e-4, $"{row.Id}: the join scores {row.Score}, the plain queries {expected}");
                else if (double.IsNaN(offset))
                    offset = row.Score - expected;
                else
                    Assert.True(Math.Abs(row.Score - expected - offset) <= 1e-4, $"{row.Id}: the join scores {row.Score}, the sub-query alone {expected} (offset {offset})");
            }

            var tagPosition = Positions(store, desc: false, sub, onField);
            var tagPositionDesc = Positions(store, desc: true, sub, onField);
            var scoreOf = rows.ToDictionary(x => x.Id, x => x.Score);
            foreach (var order in ScoreAndFieldOrders)
            {
                var parts = order.Split(", ").Select(x => (Kind: x.Split(' ')[0] switch { "score()" => 's', "u.Tag" => 't', _ => 'r' }, Desc: x.EndsWith(" desc"))).ToArray();

                string[] Key(Row row)
                    => parts.Select(p => p.Kind switch
                    {
                        's' => row.Score.ToString("R"),
                        't' => (p.Desc ? tagPositionDesc : tagPosition).GetValueOrDefault(KeyOf(row), int.MaxValue).ToString(),
                        _ => row.Rank.ToString()
                    }).ToArray();

                int Compare(string[] x, string[] y, out int decidedBy)
                {
                    for (var i = 0; i < parts.Length; i++)
                    {
                        decidedBy = i;
                        var c = parts[i].Kind == 's' ? double.Parse(x[i]).CompareTo(double.Parse(y[i])) : int.Parse(x[i]).CompareTo(int.Parse(y[i]));
                        if (parts[i].Kind == 's')
                            c = -c; // the best first
                        if (parts[i].Kind != 't' && parts[i].Desc)
                            c = -c;
                        if (c != 0)
                            return c;
                    }

                    decidedBy = -1;
                    return 0;
                }

                var ordered = Fetch(store, $"{Games} {join} {where} order by {order} {Everything}");
                Assert.Equal(rows.Count, ordered.Count);
                Assert.Equal(rows.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal), ordered.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));

                var laterKeyDecided = 0;
                var keys = ordered.Select(x => Key(x)).ToList();
                foreach (var row in ordered)
                    Assert.True(Math.Abs(row.Score - scoreOf[row.Id]) <= 1e-4, $"[{order}] {row.Id}: returned {row.Score}, order by score() gives {scoreOf[row.Id]}");

                for (var i = 1; i < keys.Count; i++)
                {
                    var c = Compare(keys[i - 1], keys[i], out var decidedBy);
                    Assert.True(c <= 0, $"[{order}] row {i}: {string.Join("|", keys[i - 1])} is before {string.Join("|", keys[i])}, with the score of the join {ordered[i - 1].Score} and {ordered[i].Score}");
                    if (decidedBy > 0)
                        laterKeyDecided++;
                }

                output.WriteLine($"[{order}] outer search {withOuterSearch}, left {left}, on a field {onField}: {ordered.Count} rows, the keys after the first decided {laterKeyDecided} neighbours");
                Assert.True(laterKeyDecided > (onField ? 10 : 50), $"[{order}] the check needs rows that tie on the first key, {laterKeyDecided} neighbours were decided by a later key");

                // pages: the same keys at the same positions
                foreach (var (skip, take) in new[] { (0, 20), (20, 20), (500, 10) })
                {
                    var page = Fetch(store, $"{Games} {join} {where} order by {order} limit {skip}, {take}", out var stats);
                    Assert.Equal(take, page.Count);
                    Assert.Equal(rows.Count, stats.TotalResults);
                    for (var i = 0; i < take; i++)
                        Assert.True(keys[skip + i].SequenceEqual(Key(page[i])), $"[{order}] page {skip},{take} position {i}: {string.Join("|", keys[skip + i])} against {string.Join("|", Key(page[i]))}");
                }
            }
        }
    }

    // BOOST 0 adds nothing to the score but the join still orders
    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByAJoinedFieldWithBoostZeroAndWithAnotherJoin()
    {
        using var store = SeedOwn();
        var position = Positions(store, desc: false);
        const string other = "join (from index 'Users' where search(Name, 'w1 w3 w5 w7')) as p on g.OwnerId = id(p)";

        void AssertOrdered(string rql, bool leftJoin)
        {
            var rows = Fetch(store, rql);
            Assert.True(rows.Count > 500, $"[{rql}] the check needs at least 500 rows, there are {rows.Count}");
            var last = 0;
            var unmatched = 0;
            foreach (var row in rows)
            {
                var at = position.GetValueOrDefault(row.OwnerId, int.MaxValue);
                Assert.True(at >= last, $"[{rql}] the owner of {row.Id} is at {at} after {last}");
                last = at;
                if (at == int.MaxValue)
                    unmatched++;
            }

            Assert.Equal(leftJoin, unmatched > 0);
        }

        AssertOrdered($"{Games} {Join} boost 0 order by u.Tag {Everything}", false);
        AssertOrdered($"{Games} left {Join} boost 0 order by u.Tag {Everything}", true);
        AssertOrdered($"{Games} {Join} boost 0 where search(g.Name, '{OuterSearch}') order by u.Tag, g.Rank as long {Everything}", false);
        AssertOrdered($"{Games} {Join} {other} order by u.Tag {Everything}", false);
        AssertOrdered($"{Games} left {Join} {other} order by u.Tag {Everything}", true);
        AssertOrdered($"{Games} {other} {Join} order by u.Tag {Everything}", false);
        AssertOrdered($"{Games} left {Join} left join (from index 'Users' where search(Name, 'w1 w3 w5 w7')) as p on g.OwnerId = id(p) order by u.Tag {Everything}", true);
    }

    // the position of every user of the sub-query in the order of its Tags (users with the same Tag share one)
    private Dictionary<string, int> Positions(IDocumentStore store, bool desc, string sub = IdSub, bool onField = false)
    {
        var users = Fetch(store, $"{sub} order by Tag {(desc ? "desc" : "")} {Everything}", expectScore: false);
        var position = new Dictionary<string, int>();
        string previous = null;
        var rank = 0;
        foreach (var user in users)
        {
            if (rank == 0 || user.Tag != previous)
                rank++;
            previous = user.Tag;
            position.TryAdd(onField ? user.CompanyId : user.Id, rank);
        }

        return position;
    }

    private static List<Row> Fetch(IDocumentStore store, string rql, bool expectScore = true) => Fetch(store, rql, out _, expectScore);

    private static List<Row> Fetch(IDocumentStore store, string rql, out QueryStatistics statistics, bool expectScore = true)
    {
        using var session = store.OpenSession();
        var rows = session.Advanced.RawQuery<Row>(rql).Statistics(out statistics).ToList();
        foreach (var row in rows)
        {
            var metadata = session.Advanced.GetMetadataFor(row);
            row.Id = (string)metadata[Constants.Documents.Metadata.Id];
            if (expectScore)
            {
                Assert.True(metadata.ContainsKey(Constants.Documents.Metadata.IndexScore), "the score must be returned");
                row.Score = Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore]);
            }
        }

        return rows;
    }

    // a database of its own for the long test
    private IDocumentStore SeedOwn()
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        var store = GetDocumentStore(options);
        Populate(store, users: 20_000, games: 30_000, seed: 20);
        return store;
    }

    private void Populate(IDocumentStore store, int users, int games, int seed)
    {
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);

        var random = new Random(seed);

        string Text()
        {
            var length = 1 + (int)(11 * Math.Pow(random.NextDouble(), 1.7));
            return string.Join(' ', Enumerable.Range(0, length).Select(_ => $"w{1 + (int)(5000 * Math.Pow(random.NextDouble(), 3))}"));
        }

        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < users; i++)
                bulk.Store(new Row { Name = Text(), Tag = random.Next(100) < 5 ? null : $"t{(int)(300 * Math.Pow(random.NextDouble(), 2))}", Rank = random.Next(60), CompanyId = $"companies/{random.Next(150)}" }, $"users/{i}");
            for (var i = 0; i < games; i++)
                bulk.Store(new Row { Name = Text(), OwnerId = $"users/{random.Next(users)}", PublisherId = $"companies/{random.Next(150)}", Rank = random.Next(1000) }, $"games/{i}");
        }

        Indexes.WaitForIndexing(store, timeout: TimeSpan.FromMinutes(30));
    }

    private sealed class Row
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Tag { get; set; }
        public string OwnerId { get; set; }
        public string CompanyId { get; set; }
        public string PublisherId { get; set; }
        public int Rank { get; set; }
        public double Score { get; set; }
    }

    private sealed class UsersIndex : AbstractIndexCreationTask<Row>
    {
        public UsersIndex()
        {
            Map = users => from u in users where u.OwnerId == null select new { u.Name, u.Tag, u.Rank, u.CompanyId };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    private sealed class GamesIndex : AbstractIndexCreationTask<Row>
    {
        public GamesIndex()
        {
            Map = games => from g in games where g.OwnerId != null select new { g.Name, g.OwnerId, g.PublisherId, g.Rank };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
