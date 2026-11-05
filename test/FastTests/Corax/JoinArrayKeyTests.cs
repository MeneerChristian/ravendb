using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// JOIN, LEFT JOIN and BOOST when a key holds several values
public class JoinArrayKeyTests(ITestOutputHelper output, SharedJoinDatabase shared) : RavenTestBase(output), IClassFixture<SharedJoinDatabase>
{
    private const string Everything = "limit 0, 1000000";
    private const int Products = 200;
    private const int Games = 600;

    private const string Outer = "from index 'ArrGames' as g";
    private const string OuterWhere = "where search(g.Name, 'g1 g4 common')";

    private sealed record Row(string Id, double Score);

    internal sealed class Line
    {
        public string ProductId { get; set; }
        public int Quantity { get; set; }
    }

    internal sealed class ArrGame
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<Line> Lines { get; set; }
        public string[] ProductIds { get; set; }
        public string[] Tags { get; set; }
        public string Bucket { get; set; }
        public long Size { get; set; }
    }

    internal sealed class ArrProduct
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Bucket { get; set; }
        public string[] Codes { get; set; }
        public long Rank { get; set; }
    }

    internal sealed class ArrGamesIndex : AbstractIndexCreationTask<ArrGame>
    {
        public ArrGamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.ProductIds, g.Tags, g.Bucket, g.Size };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "ArrGames";
    }

    internal sealed class ArrProductsIndex : AbstractIndexCreationTask<ArrProduct>
    {
        public ArrProductsIndex()
        {
            Map = products => from p in products select new { p.Name, p.Bucket, p.Codes, p.Rank };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "ArrProducts";
    }

    internal static string[] Lines(int i)
    {
        if (i % 9 == 0)
            return [];

        var ids = Enumerable.Range(0, 1 + i % 4).Select(k => $"products/{(i * 7 + k * 13) % 260}").ToList();
        if (i % 5 == 0)
            ids.Add(ids[0]);
        return ids.ToArray();
    }

    internal static string[] GameTags(int i)
    {
        if (i % 8 == 0)
            return null;

        var tags = Enumerable.Range(0, 1 + i % 3).Select(k => $"t{(i + k * 4) % 14}").ToList();
        if (i % 6 == 0)
            tags.Add(tags[0]);
        return tags.ToArray();
    }

    internal static string GameGroup(int i) => i % 10 == 0 ? null : $"t{i % 12}";
    internal static string ProductGroup(int i) => $"t{i % 10}";
    internal static string[] ProductCodes(int i) => i % 7 == 0 ? [] : Enumerable.Range(0, 1 + i % 3).Select(k => $"t{(i * 3 + k * 5) % 16}").ToArray();
    internal static string ProductName(int i) => $"word{i % 40} word{i % 7} common";
    private static int GameNumber(string id) => int.Parse(id.Substring("games/".Length));
    private static int ProductNumber(string id) => int.Parse(id.Substring("products/".Length));

    private IDocumentStore Seed() => shared.Get("array-keys", Options.ForSearchEngine(RavenSearchEngineMode.Corax, includeScoresAndDistances: true), o => GetDocumentStore(o), store => SeedData(store, s => Indexes.WaitForIndexing(s)));

    internal static void SeedData(IDocumentStore store, Action<IDocumentStore> waitForIndexing)
    {
        new ArrProductsIndex().Execute(store);
        new ArrGamesIndex().Execute(store);

        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < Products; i++)
                bulk.Store(new ArrProduct { Id = $"products/{i}", Name = ProductName(i), Bucket = ProductGroup(i), Codes = ProductCodes(i), Rank = i });
            for (var i = 0; i < Games; i++)
            {
                bulk.Store(new ArrGame
                {
                    Id = $"games/{i}",
                    Name = $"game{i} g{i % 13} common",
                    Lines = Lines(i).Select(x => new Line { ProductId = x, Quantity = 1 }).ToList(),
                    ProductIds = Lines(i),
                    Tags = GameTags(i),
                    Bucket = GameGroup(i),
                    Size = i % 10
                });
            }
        }

        waitForIndexing(store);
    }

    internal sealed record Form(string Name, string On, string Sub, Func<int, IEnumerable<string>> OuterKeys, Func<int, IEnumerable<string>> JoinedKeys);

    internal static readonly Dictionary<string, Form> Forms = new[]
    {
        // the outer list holds ids of the joined documents
        new Form("id", "g.ProductIds = id(p)", "from index 'ArrProducts' where search(Name, 'word3 word5')",
            i => Lines(i), i => [$"products/{i}"]),
        // list outer, one value joined
        new Form("tags=group", "g.Tags = p.Bucket", "from index 'ArrProducts' where search(Name, 'word3 word5') and Rank < 120",
            i => GameTags(i) ?? [], i => [ProductGroup(i)]),
        // one value outer, list joined
        new Form("group=codes", "g.Bucket = p.Codes", "from index 'ArrProducts' where search(Name, 'word3 word5') and Rank < 120",
            i => GameGroup(i) is { } g ? [g] : [], i => ProductCodes(i)),
        // lists on both sides
        new Form("tags=codes", "g.Tags = p.Codes", "from index 'ArrProducts' where search(Name, 'word3 word5') and Rank < 120",
            i => GameTags(i) ?? [], i => ProductCodes(i)),
    }.ToDictionary(x => x.Name);

    public static IEnumerable<object[]> Cases()
    {
        foreach (var form in new[] { "id", "tags=group", "group=codes", "tags=codes" })
        foreach (var left in new[] { true, false })
        foreach (var boost in new[] { "", " boost 0", " boost 0.5", " boost 2" })
            yield return [form, left, boost];
    }

    private static double Factor(string boost) => boost.Length == 0 ? 1 : double.Parse(boost.Substring(" boost ".Length), CultureInfo.InvariantCulture);

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [MemberData(nameof(Cases))]
    public void RowsScoresAndTheProjectionMatchTheOracle(string formName, bool left, string boost)
    {
        using var store = Seed();
        var form = Forms[formName];
        var join = left ? "left join" : "join";
        var factor = Factor(boost);

        var outer = Fetch(store, $"{Outer} {OuterWhere} order by score() {Everything}");
        var matchedProducts = Fetch<ArrProduct>(store, $"{form.Sub} order by score() {Everything}");
        Assert.NotEmpty(matchedProducts);

        var keyScore = new Dictionary<string, double>();
        foreach (var product in matchedProducts)
        foreach (var key in form.JoinedKeys(ProductNumber(product.Id)))
            keyScore[key] = Math.Max(keyScore.GetValueOrDefault(key), product.Score);

        var expected = outer.Select(o =>
        {
            var keys = form.OuterKeys(GameNumber(o.Id)).Distinct().Where(keyScore.ContainsKey).ToList();
            return (o.Id, Matched: keys.Count > 0, Score: o.Score + factor * keys.Sum(k => keyScore[k]));
        }).Where(x => left || x.Matched).ToList();

        Assert.Contains(expected, x => x.Matched);
        Assert.Contains(expected, x => x.Matched && x.Score > 0);
        if (left)
            Assert.Contains(expected, x => x.Matched == false);
        else
            Assert.True(expected.Count < outer.Count, "the inner join must filter");

        var rows = Fetch(store, $"{Outer} {join} ({form.Sub}) as p on {form.On}{boost} {OuterWhere} order by score() {Everything}");
        AssertRows(expected.Select(x => new Row(x.Id, x.Score)).ToList(), rows);
        if (left)
            Assert.Equal(outer.Count, rows.Count);

        if (formName == "id")
            return;

        var projected = Project(store, $"{Outer} {join} ({form.Sub}) as p on {form.On}{boost} {OuterWhere} select g.Name as GameName, p.Name as ProductName {Everything}");
        Assert.Equal(expected.Count, projected.Count);
        var matchedById = expected.ToDictionary(x => x.Id, x => x.Matched);
        foreach (var row in projected)
        {
            var id = $"games/{int.Parse(row.GameName.Split(' ')[0].Substring("game".Length))}";
            Assert.True(matchedById[id] == (row.ProductName != null), $"{id}: matched {matchedById[id]}, projected '{row.ProductName}', outer keys {string.Join(',', form.OuterKeys(GameNumber(id)))}");
            if (row.ProductName == null)
                continue;

            var keys = form.OuterKeys(GameNumber(id)).ToHashSet();
            var candidates = matchedProducts.Where(p => form.JoinedKeys(ProductNumber(p.Id)).Any(keys.Contains)).Select(p => ProductName(ProductNumber(p.Id))).ToHashSet();
            Assert.Contains(row.ProductName, candidates);
        }

        Assert.Contains(projected, x => x.ProductName != null);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectionOfAListOfIdsNeedsAnArrayAlias(bool left)
    {
        using var store = Seed();
        var form = Forms["id"];
        var matchedIds = Fetch<ArrProduct>(store, $"{form.Sub} order by score() {Everything}").Select(x => x.Id).ToHashSet();

        // LOAD g.ProductIds as p[] makes the alias a list: the names of the joined documents of the row that matched
        var rows = Project(store, $"{Outer} {(left ? "left join" : "join")} ({form.Sub}) as p on {form.On} {OuterWhere} load g.ProductIds as p[] select g.Name as GameName, p.Name as ProductNames {Everything}");
        var several = 0;
        foreach (var row in rows)
        {
            var game = int.Parse(row.GameName.Split(' ')[0].Substring("game".Length));
            var expected = Lines(game).Distinct().Where(matchedIds.Contains).Select(k => ProductName(ProductNumber(k))).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(expected, (row.ProductNames ?? []).OrderBy(x => x, StringComparer.Ordinal).ToList());
            if (expected.Count > 1)
                several++;
        }

        Assert.True(several > 5, "rows that matched several keys");
        Assert.Equal(left ? Fetch(store, $"{Outer} {OuterWhere} {Everything}").Count : rows.Count, rows.Count);
        Assert.Contains(rows, x => x.ProductNames is { Length: > 0 });
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData("id", "")]
    [InlineData("tags=codes", "")]
    [InlineData("tags=codes", " boost 2")]
    public void RowsWithoutScoresAndWithPagingMatchTheOracle(string formName, string boost)
    {
        using var store = Seed();
        var form = Forms[formName];
        var matchedProducts = Fetch<ArrProduct>(store, $"{form.Sub} order by score() {Everything}");
        var keys = matchedProducts.SelectMany(p => form.JoinedKeys(ProductNumber(p.Id))).ToHashSet();
        var all = Enumerable.Range(0, Games).ToList();
        var inner = all.Where(i => form.OuterKeys(i).Any(keys.Contains)).Select(i => $"games/{i}").OrderBy(x => x, StringComparer.Ordinal).ToList();

        var rows = Fetch(store, $"{Outer} join ({form.Sub}) as p on {form.On}{boost} {Everything}").Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(inner, rows);

        var left = Fetch(store, $"{Outer} left join ({form.Sub}) as p on {form.On}{boost} {Everything}").Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(Games, left.Count);

        using var session = store.OpenSession();
        var page = session.Advanced.RawQuery<ArrGame>($"{Outer} join ({form.Sub}) as p on {form.On}{boost} order by score() limit 7, 9").Statistics(out var stats).ToList();
        Assert.Equal(inner.Count, stats.TotalResults);
        Assert.Equal(9, page.Count);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(true)]
    [InlineData(false)]
    public void ARowThatMatchesSeveralKeysScoresTheirSum(bool left)
    {
        using var store = Seed();
        var form = Forms["id"];
        var join = left ? "left join" : "join";
        var matched = Fetch<ArrProduct>(store, $"{form.Sub} order by score() {Everything}").ToDictionary(x => x.Id, x => x.Score);
        var outer = Fetch(store, $"{Outer} {OuterWhere} order by score() {Everything}").ToDictionary(x => x.Id, x => x.Score);

        var several = Enumerable.Range(0, Games).Where(i => outer.ContainsKey($"games/{i}") && Lines(i).Distinct().Count(matched.ContainsKey) >= 2).ToList();
        Assert.NotEmpty(several);

        var rows = Fetch(store, $"{Outer} {join} ({form.Sub}) as p on {form.On} {OuterWhere} order by score() {Everything}").ToDictionary(x => x.Id, x => x.Score);
        foreach (var i in several)
            Assert.True(Math.Abs(rows[$"games/{i}"] - (outer[$"games/{i}"] + Lines(i).Distinct().Where(matched.ContainsKey).Sum(k => matched[k]))) < 1e-3, $"games/{i}");
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    [InlineData(true, null)]
    [InlineData(true, 0.5)]
    [InlineData(false, 2.0)]
    public void ClientJoinOnAListFieldGivesTheRowsOfTheRql(bool left, double? boost)
    {
        using var store = Seed();
        var form = Forms["tags=codes"];
        var boostText = boost.HasValue ? $" boost {boost.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "";
        var expected = Fetch(store, $"{Outer} {(left ? "left join" : "join")} ({form.Sub}) as p on {form.On}{boostText} {OuterWhere} order by score() {Everything}");

        using var session = store.OpenSession();
        var products = session.Query<ArrProduct, ArrProductsIndex>().Search(x => x.Name, "word3 word5").Where(x => x.Rank < 120);
        var games = session.Query<ArrGame, ArrGamesIndex>().Search(x => x.Name, "g1 g4 common")
            .Join(products, x => x.Tags, p => p.Codes, left, boost).OrderByScore().Take(1000000).ToList();
        var actual = games.Select(x => new Row(session.Advanced.GetDocumentId(x), ScoreOf(session.Advanced.GetMetadataFor(x)))).ToList();
        AssertRows(expected, actual);
    }

    private static void AssertRows(List<Row> expected, List<Row> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal), actual.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));

        var byId = expected.ToDictionary(x => x.Id);
        foreach (var row in actual)
            Assert.True(Math.Abs(byId[row.Id].Score - row.Score) < 1e-3, $"{row.Id}: expected {byId[row.Id].Score}, got {row.Score}");

        for (var i = 1; i < actual.Count; i++)
            Assert.True(actual[i - 1].Score >= actual[i].Score - 1e-6, $"position {i}: {actual[i - 1].Score} before {actual[i].Score}");
    }

    private static double ScoreOf(IMetadataDictionary metadata) =>
        metadata.ContainsKey(Constants.Documents.Metadata.IndexScore) ? Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore]) : 0;

    private static List<Row> Fetch(IDocumentStore store, string rql) => Fetch<ArrGame>(store, rql);

    private static List<Row> Fetch<T>(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        var rows = new List<Row>();
        foreach (var entity in session.Advanced.RawQuery<T>(rql).ToList())
        {
            var metadata = session.Advanced.GetMetadataFor(entity);
            rows.Add(new Row((string)metadata[Constants.Documents.Metadata.Id], ScoreOf(metadata)));
        }

        return rows;
    }

    private static List<Projection> Project(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<Projection>(rql).ToList();
    }

    private sealed class Projection
    {
        public string GameName { get; set; }
        public string ProductName { get; set; }
        public string[] ProductNames { get; set; }
    }
}
