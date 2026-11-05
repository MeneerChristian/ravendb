using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries.Facets;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// Facets over a JOIN written the ways a client writes them
public class JoinFacetVariantTests(ITestOutputHelper output, SharedJoinDatabase shared) : RavenTestBase(output), IClassFixture<SharedJoinDatabase>
{
    private const string Everything = "limit 0, 1000000";
    private const string Outer = "from index 'ArrGames' as g";
    private const string SetupId = "facets/ArrGames";
    private const int Games = 600;

    private IDocumentStore Seed() => shared.Get("array-facets", Options.ForSearchEngine(RavenSearchEngineMode.Corax, includeScoresAndDistances: true), o => GetDocumentStore(o), store =>
    {
        JoinArrayKeyTests.SeedData(store, s => Indexes.WaitForIndexing(s));
        using var session = store.OpenSession();
        session.Store(new FacetSetup
        {
            Id = SetupId,
            Facets = [new Facet { FieldName = "Tags" }, new Facet { FieldName = "Bucket" }],
            RangeFacets = [new RangeFacet { Ranges = ["Size < 3", "Size >= 3 and Size < 7", "Size >= 7"] }]
        });
        session.SaveChanges();
    });

    // the games the join keeps
    private static List<int> Joined(IDocumentStore store, string formName, bool left)
    {
        var form = JoinArrayKeyTests.Forms[formName];
        using var session = store.OpenSession();
        var keys = session.Advanced.RawQuery<JoinArrayKeyTests.ArrProduct>($"{form.Sub} {Everything}").ToList()
            .SelectMany(p => form.JoinedKeys(int.Parse(session.Advanced.GetDocumentId(p).Substring("products/".Length)))).ToHashSet();
        return Enumerable.Range(0, Games).Where(i => left || form.OuterKeys(i).Any(keys.Contains)).ToList();
    }

    private static Dictionary<string, int> Counts(Dictionary<string, FacetResult> facets, string name)
        => facets[name].Values.Where(x => x.Count > 0 && x.Range != "NULL_VALUE").ToDictionary(x => x.Range, x => x.Count);

    private static void AssertSame(Dictionary<string, int> expected, Dictionary<string, int> actual, string what) =>
        Assert.True(expected.OrderBy(x => x.Key).SequenceEqual(actual.OrderBy(x => x.Key)),
            $"[{what}] expected {expected.Count} terms summing to {expected.Values.Sum()} but got {actual.Count} terms summing to {actual.Values.Sum()}");

    private static Dictionary<string, int> TagCounts(IEnumerable<int> games) =>
        games.SelectMany(i => (JoinArrayKeyTests.GameTags(i) ?? []).Distinct()).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());

    private static Dictionary<string, int> GroupCounts(IEnumerable<int> games) =>
        games.Select(JoinArrayKeyTests.GameGroup).Where(x => x != null).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());

    private static Dictionary<string, int> ProductCounts(IEnumerable<int> games) =>
        games.SelectMany(i => JoinArrayKeyTests.Lines(i).Distinct()).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData("id", false)]
    [InlineData("id", true)]
    [InlineData("tags=group", false)]
    [InlineData("group=codes", false)]
    [InlineData("tags=codes", false)]
    [InlineData("tags=codes", true)]
    public void FacetsOnMultiValuedFieldsCountTheRowsOfTheJoin(string formName, bool left)
    {
        using var store = Seed();
        var form = JoinArrayKeyTests.Forms[formName];
        var joined = Joined(store, formName, left);
        Assert.True(joined.Count > 20 && (left || joined.Count < Games), $"{joined.Count} rows");

        using var session = store.OpenSession();
        var facets = session.Advanced.RawQuery<JoinArrayKeyTests.ArrGame>(
            $"{Outer} {(left ? "left join" : "join")} ({form.Sub}) as p on {form.On} select facet(Tags), facet(Bucket), facet(ProductIds)").ExecuteAggregation();

        AssertSame(TagCounts(joined), Counts(facets, "Tags"), "facet(Tags)");
        AssertSame(GroupCounts(joined), Counts(facets, "Bucket"), "facet(Bucket)");
        AssertSame(ProductCounts(joined), Counts(facets, "ProductIds"), "facet(ProductIds)");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void FacetsWithAWhereAndABoostOverAListJoin()
    {
        using var store = Seed();
        var form = JoinArrayKeyTests.Forms["tags=codes"];
        var joined = Joined(store, "tags=codes", left: false).Where(i => i % 2 == 0).ToList();

        using var session = store.OpenSession();
        var facets = session.Advanced.RawQuery<JoinArrayKeyTests.ArrGame>(
            $"{Outer} join ({form.Sub}) as p on {form.On} boost 2 where Size in (0, 2, 4, 6, 8) select facet(Tags)").ExecuteAggregation();
        AssertSame(TagCounts(joined), Counts(facets, "Tags"), "facet(Tags)");
    }

    private static IRavenQueryable<JoinArrayKeyTests.ArrGame> LinqJoin(IDocumentSession session, bool left)
    {
        var products = session.Query<JoinArrayKeyTests.ArrProduct, JoinArrayKeyTests.ArrProductsIndex>().Search(x => x.Name, "word3 word5").Where(x => x.Rank < 120);
        return session.Query<JoinArrayKeyTests.ArrGame, JoinArrayKeyTests.ArrGamesIndex>().Join(products, x => x.Tags, p => p.Codes, left);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    [InlineData(false)]
    [InlineData(true)]
    public void LinqAggregateByFieldAndRangesOverAJoin(bool left)
    {
        using var store = Seed();
        var joined = Joined(store, "tags=codes", left);
        Assert.True(left || joined.Count < Games);

        using var session = store.OpenSession();
        var byField = LinqJoin(session, left).AggregateBy(x => x.ByField(y => y.Tags)).AndAggregateBy(x => x.ByField(y => y.Bucket)).Execute();
        AssertSame(TagCounts(joined), Counts(byField, "Tags"), "ByField(Tags)");
        AssertSame(GroupCounts(joined), Counts(byField, "Bucket"), "ByField(Bucket)");

        var withSum = LinqJoin(session, left).AggregateBy(x => x.ByField(y => y.Bucket).SumOn(y => y.Size).MaxOn(y => y.Size)).Execute();
        var bySize = joined.Where(i => JoinArrayKeyTests.GameGroup(i) != null).GroupBy(JoinArrayKeyTests.GameGroup).ToDictionary(x => x.Key, x => x.Select(i => (long)(i % 10)).ToList());
        foreach (var value in withSum["Bucket"].Values.Where(x => x.Range != "NULL_VALUE"))
        {
            Assert.Equal(bySize[value.Range].Count, value.Count);
            Assert.Equal(bySize[value.Range].Sum(), value.Sum);
            Assert.Equal(bySize[value.Range].Max(), value.Max);
        }

        Assert.Equal(bySize.Count, withSum["Bucket"].Values.Count(x => x.Count > 0 && x.Range != "NULL_VALUE"));

        var ranges = LinqJoin(session, left).AggregateBy(x => x.ByRanges(y => y.Size < 3, y => y.Size >= 3 && y.Size < 7, y => y.Size >= 7)).Execute();
        Assert.Equal([joined.Count(i => i % 10 < 3), joined.Count(i => i % 10 is >= 3 and < 7), joined.Count(i => i % 10 >= 7)], ranges.Values.Single().Values.Select(x => x.Count));
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.ClientApi)]
    [InlineData(false)]
    [InlineData(true)]
    public void AFacetSetupDocumentOverAJoin(bool left)
    {
        using var store = Seed();
        var joined = Joined(store, "tags=codes", left);

        using var session = store.OpenSession();
        var results = LinqJoin(session, left).AggregateUsing(SetupId).Execute();
        AssertSame(TagCounts(joined), Counts(results, "Tags"), "setup Tags");
        AssertSame(GroupCounts(joined), Counts(results, "Bucket"), "setup Bucket");
        Assert.Equal([joined.Count(i => i % 10 < 3), joined.Count(i => i % 10 is >= 3 and < 7), joined.Count(i => i % 10 >= 7)], results["Size"].Values.Select(x => x.Count));

        var raw = session.Advanced.RawQuery<JoinArrayKeyTests.ArrGame>($"{Outer} {(left ? "left join" : "join")} ({JoinArrayKeyTests.Forms["tags=codes"].Sub}) as p on g.Tags = p.Codes select facet(id('{SetupId}'))").ExecuteAggregation();
        AssertSame(TagCounts(joined), Counts(raw, "Tags"), "raw setup Tags");
    }

    private static void AssertRejected(IDocumentStore store, string rql, string mentions)
    {
        using var session = store.OpenSession();
        var e = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<JoinArrayKeyTests.ArrGame>(rql).ExecuteAggregation());
        Assert.Contains(mentions, e.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.Sharding)]
    [InlineData("join")]
    [InlineData("left join")]
    public void FacetsOverAJoinInAShardedDatabaseAreRejected(string join)
    {
        var options = Options.ForMode(RavenDatabaseMode.Sharded);
        var setup = options.ModifyDatabaseRecord;
        options.ModifyDatabaseRecord = record =>
        {
            setup?.Invoke(record);
            record.Settings[RavenConfiguration.GetKey(x => x.Indexing.StaticIndexingEngineType)] = "Corax";
            record.Settings[RavenConfiguration.GetKey(x => x.Indexing.AutoIndexingEngineType)] = "Corax";
        };

        using var store = GetDocumentStore(options);
        JoinArrayKeyTests.SeedData(store, s => Indexes.WaitForIndexing(s));
        var sub = JoinArrayKeyTests.Forms["id"].Sub;

        using (var session = store.OpenSession())
            Assert.True(session.Advanced.RawQuery<JoinArrayKeyTests.ArrGame>($"{Outer} select facet(Bucket)").ExecuteAggregation()["Bucket"].Values.Sum(x => x.Count) > 100);

        AssertRejected(store, $"{Outer} {join} ({sub}) as p on g.ProductIds = id(p) select facet(Bucket)", "JOIN is not supported in sharding");
        AssertRejected(store, $"{Outer} {join} ({sub}) as p on g.ProductIds = id(p) select facet(Tags), facet(Size < 3, Size >= 3)", "JOIN is not supported in sharding");

        using var session2 = store.OpenSession();
        var linq = Assert.ThrowsAny<Exception>(() => LinqJoin(session2, join == "left join").AggregateBy(x => x.ByField(y => y.Bucket)).Execute());
        Assert.Contains("JOIN is not supported in sharding", linq.ToString());
    }

    internal sealed class LuceneArrGamesIndex : AbstractIndexCreationTask<JoinArrayKeyTests.ArrGame>
    {
        public LuceneArrGamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.ProductIds, g.Tags, g.Bucket, g.Size };
            Index(x => x.Name, FieldIndexing.Search);
            Configuration["Indexing.Static.SearchEngineType"] = "Lucene";
        }

        public override string IndexName => "LuceneArrGames";
    }

    internal sealed class LuceneArrProductsIndex : AbstractIndexCreationTask<JoinArrayKeyTests.ArrProduct>
    {
        public LuceneArrProductsIndex()
        {
            Map = products => from p in products select new { p.Name, p.Bucket, p.Codes, p.Rank };
            Index(x => x.Name, FieldIndexing.Search);
            Configuration["Indexing.Static.SearchEngineType"] = "Lucene";
        }

        public override string IndexName => "LuceneArrProducts";
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData("LuceneArrGames", "ArrProducts")] // the main index is a Lucene index
    [InlineData("ArrGames", "LuceneArrProducts")] // the joined index is a Lucene index
    [InlineData("LuceneArrGames", "LuceneArrProducts")]
    public void FacetsOverAJoinWithALuceneIndexAreRejectedAndNeverAnsweredWithoutTheJoin(string mainIndex, string joinedIndex)
    {
        using var store = Seed();
        new LuceneArrGamesIndex().Execute(store);
        new LuceneArrProductsIndex().Execute(store);
        Indexes.WaitForIndexing(store);
        Assert.Equal(SearchEngineType.Lucene, store.Maintenance.Send(new GetIndexStatisticsOperation("LuceneArrGames")).SearchEngineType);
        Assert.Equal(SearchEngineType.Lucene, store.Maintenance.Send(new GetIndexStatisticsOperation("LuceneArrProducts")).SearchEngineType);

        using var session = store.OpenSession();
        var withoutJoin = session.Advanced.RawQuery<JoinArrayKeyTests.ArrGame>($"from index '{mainIndex}' as g select facet(Bucket)").ExecuteAggregation();
        Assert.True(withoutJoin["Bucket"].Values.Sum(x => x.Count) > 100);

        foreach (var join in new[] { "join", "left join" })
        {
            AssertRejected(store, $"from index '{mainIndex}' as g {join} (from index '{joinedIndex}' where search(Name, 'word3 word5')) as p on g.ProductIds = id(p) select facet(Bucket)", "JOIN is supported only for Corax");
            AssertRejected(store, $"from index '{mainIndex}' as g {join} (from index '{joinedIndex}' where search(Name, 'word3 word5')) as p on g.Bucket = p.Bucket select facet(Tags)", "JOIN is supported only for Corax");

            var rows = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<JoinArrayKeyTests.ArrGame>(
                $"from index '{mainIndex}' as g {join} (from index '{joinedIndex}' where search(Name, 'word3 word5')) as p on g.ProductIds = id(p) {Everything}").ToList());
            Assert.Contains("JOIN is supported only for Corax", rows.ToString());
        }
    }
}
