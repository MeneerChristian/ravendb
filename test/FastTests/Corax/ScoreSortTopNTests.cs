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

// order by score() followed by other keys with a small page has to return the same keys, in the same order
public class ScoreSortTopNTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private sealed class Doc
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public long? Rank { get; set; }
        public double? Price { get; set; }
        public string Tag { get; set; }
    }

    private sealed class DocsIndex : AbstractIndexCreationTask<Doc>
    {
        public DocsIndex()
        {
            Map = docs => from d in docs select new { d.Name, d.Rank, d.Price, d.Tag };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Docs";
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(false)]
    [InlineData(true)]
    public void TopNEqualsFullSort(bool tiesHeavy)
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        using var store = GetDocumentStore(options);
        new DocsIndex().Execute(store);

        var random = new Random(tiesHeavy ? 1 : 2);
        const int count = 12_000;
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < count; i++)
            {
                var name = tiesHeavy
                    ? new[] { "alpha beta", "alpha gamma", "alpha delta" }[random.Next(3)]
                    : string.Join(' ', Enumerable.Range(0, 1 + random.Next(10)).Select(_ => "w" + (1 + (int)(30 * Math.Pow(random.NextDouble(), 2)))));
                bulk.Store(new Doc
                {
                    Name = name,
                    Rank = random.Next(10) == 0 ? null : random.Next(tiesHeavy ? 8 : 1000),
                    Price = random.Next(10) == 0 ? null : Math.Round(random.NextDouble() * 50, 1),
                    Tag = random.Next(10) == 0 ? null : "t" + random.Next(tiesHeavy ? 5 : 40)
                }, $"docs/{i}");
            }
        }

        Indexes.WaitForIndexing(store);

        var where = tiesHeavy ? "where search(Name, 'alpha beta gamma')" : "where search(Name, 'w1 w2 w3 w5 w8 w13')";
        var orders = new[]
        {
            "score()", "score() desc",
            "score(), Rank as long", "score(), Rank as long desc", "score() desc, Rank as long", "score() desc, Rank as long desc",
            "score(), Tag", "score(), Tag desc", "score() desc, Tag desc",
            "score(), Price as double", "score(), Price as double desc",
            "score(), Tag, Rank as long", "score(), Rank as long desc, Tag desc",
            "Rank as long, score()", "Tag, score() desc", "Rank as long desc, Tag, score()",
        };
        var pages = new[] { (0, 20), (1000, 20), (0, 1), (3, 7), (200, 50) };

        foreach (var order in orders)
        {
            var full = Fetch(store, order, $"from index 'Docs' {where} order by {order} limit 0, 1000000");
            Assert.True(full.Count > 4000, $"{order}: {full.Count}");
            foreach (var (skip, take) in pages)
            {
                var page = Fetch(store, order, $"from index 'Docs' {where} order by {order} limit {skip}, {take}");
                Assert.Equal(take, page.Count);
                for (var i = 0; i < take; i++)
                    Assert.True(full[skip + i].Key == page[i].Key, $"[{order}] page {skip},{take} position {i}: full {full[skip + i].Key} but page {page[i].Key}");

                var again = Fetch(store, order, $"from index 'Docs' {where} order by {order} limit {skip}, {take}");
                Assert.Equal(page.Select(x => x.Id), again.Select(x => x.Id));
            }

            output.WriteLine($"[{order}] ok ({full.Count} rows)");
        }
    }

    private sealed record Row(string Id, string Key);

    private static List<Row> Fetch(IDocumentStore store, string order, string rql)
    {
        using var session = store.OpenSession();
        var rows = new List<Row>();
        foreach (var d in session.Advanced.RawQuery<Doc>(rql).ToList())
        {
            var score = Convert.ToDouble(session.Advanced.GetMetadataFor(d)[Constants.Documents.Metadata.IndexScore]);
            rows.Add(new Row(d.Id, $"{score:F4}|{(order.Contains("Rank") ? d.Rank : "")}|{(order.Contains("Price") ? d.Price : "")}|{(order.Contains("Tag") ? d.Tag : "")}"));
        }

        return rows;
    }
}
