using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Raven.Server.Documents.Queries;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// A JOIN that orders by the joined index and reads a page: the first entries of the joined order (the walk) against the ranks of all of them
public class JoinOrderedWalkTests : RavenTestBase
{
    private const int Companies = 1_500;
    private const int Orders = 20_000;

    private const string Main = "from index 'Orders' as o";
    private const string AllCompanies = "join (from index 'Companies') as c on o.CompanyId = id(c)";

    private readonly ITestOutputHelper _output;

    public JoinOrderedWalkTests(ITestOutputHelper output) : base(output)
    {
        _output = output;
        JoinOrderedWalkSettings.MinimumKeys = 1;
    }

    protected override void Dispose(Raven.Server.Utils.ExceptionAggregator exceptionAggregator)
    {
        base.Dispose(exceptionAggregator);
        JoinOrderedWalkSettings.Enabled = true;
        JoinOrderedWalkSettings.MinimumKeys = JoinOrderedWalkSettings.DefaultMinimumKeys;
        JoinOrderedWalkSettings.MaximumTake = JoinOrderedWalkSettings.DefaultMaximumTake;
    }

    private sealed class Company
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Const { get; set; }
        public long Rank { get; set; }
        public string Country { get; set; }
    }

    private sealed class Order
    {
        public string Id { get; set; }
        public string CompanyId { get; set; }
        public string[] CompanyIds { get; set; }
        public string[] Tags { get; set; }
        public string Status { get; set; }
        public long Amount { get; set; }
        public string Tag { get; set; }
    }

    private sealed class Row
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    private sealed class CompaniesIndex : AbstractIndexCreationTask<Company>
    {
        public CompaniesIndex()
        {
            Map = companies => from c in companies select new { c.Name, c.Const, c.Rank, c.Country };
        }

        public override string IndexName => "Companies";
    }

    private sealed class OrdersIndex : AbstractIndexCreationTask<Order>
    {
        public OrdersIndex()
        {
            Map = orders => from o in orders select new { o.CompanyId, o.Status, o.Amount, o.Tag };
        }

        public override string IndexName => "Orders";
    }

    private sealed class OrdersListIndex : AbstractIndexCreationTask<Order>
    {
        public OrdersListIndex()
        {
            Map = orders => from o in orders select new { o.CompanyIds, o.Status };
        }

        public override string IndexName => "OrdersList";
    }

    private sealed class OrdersFanIndex : AbstractIndexCreationTask<Order>
    {
        public OrdersFanIndex()
        {
            Map = orders => from o in orders from t in o.Tags select new { o.CompanyId, o.Status, Tag = t };
        }

        public override string IndexName => "OrdersFan";
    }

    private IDocumentStore Seed()
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        var store = GetDocumentStore(options);
        new CompaniesIndex().Execute(store);
        new OrdersIndex().Execute(store);
        new OrdersListIndex().Execute(store);
        new OrdersFanIndex().Execute(store);

        var random = new Random(7);
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < Companies; i++)
            {
                // two hubs that sort first and last, some companies without a name, many that share one
                var name = i switch { 0 => "n00", 1 => "n99", _ => random.Next(100) < 8 ? null : $"n{random.Next(40):D2}" };
                bulk.Store(new Company { Id = $"companies/{i}", Name = name, Const = "same", Rank = i % 50, Country = $"c{i % 6}" });
            }

            for (var i = 0; i < Orders; i++)
            {
                var pick = random.Next(100);
                var company = pick switch
                {
                    < 5 => "companies/0",
                    < 10 => "companies/1",
                    < 13 => "companies/99999", // not a company
                    < 15 => null,
                    _ => $"companies/{random.Next(Companies - 100)}" // the last hundred companies have no order
                };

                var status = random.Next(1000) < 5 ? "rare" : random.Next(100) < 90 ? "open" : "closed";
                bulk.Store(new Order
                {
                    Id = $"orders/{i}",
                    CompanyId = company,
                    CompanyIds = [company ?? "companies/2", $"companies/{random.Next(Companies)}"],
                    Tags = [$"a{i % 3}", $"b{i % 5}"],
                    Status = status,
                    Amount = i % 100,
                    Tag = $"t{i % 11}"
                });
            }
        }

        Indexes.WaitForIndexing(store);
        return store;
    }

    private sealed record Outcome(List<string> Rows, long Total, long Walk);

    private Outcome Run(IDocumentStore store, string rql, bool walk, bool project = false)
    {
        JoinOrderedWalkSettings.Enabled = walk;
        try
        {
            using var session = store.OpenSession();
            QueryTimings timings = null;
            QueryStatistics stats = null;
            List<string> rows;
            if (project)
            {
                var query = session.Advanced.RawQuery<Row>(rql + " include timings()").NoCaching().Statistics(out stats).Timings(out timings);
                rows = query.ToList().Select(x => $"{x.Id}|{x.Name}").ToList();
            }
            else
            {
                var query = session.Advanced.RawQuery<Order>(rql + " include timings()").NoCaching().Statistics(out stats).Timings(out timings);
                rows = query.ToList().Select(x => x.Id).ToList();
            }

            long counter = -1;
            if (timings?.Timings != null && timings.Timings.TryGetValue("Join", out var join))
            {
                foreach (var scope in join.Timings.Values)
                {
                    if (scope.Counters != null && scope.Counters.TryGetValue("JoinOrderedWalk", out var value))
                        counter = Math.Max(counter, value);
                }
            }

            return new Outcome(rows, stats.TotalResults, counter);
        }
        finally
        {
            JoinOrderedWalkSettings.Enabled = true;
        }
    }

    // both paths return the same rows and the same total, and the new one is the one that ran when expected
    private void Verify(IDocumentStore store, string rql, bool expectWalk, bool project = false)
    {
        var before = Run(store, rql, walk: false, project);
        var after = Run(store, rql, walk: true, project);
        Assert.True(before.Walk <= 0, $"[{rql}] the walk ran while it is switched off");
        Assert.True(expectWalk ? after.Walk == 1 : after.Walk <= 0, $"[{rql}] the walk {(expectWalk ? "was expected" : "was not expected")}, counter {after.Walk}");
        Assert.True(before.Total == after.Total, $"[{rql}] total {before.Total} against {after.Total}");
        Assert.True(before.Rows.SequenceEqual(after.Rows), $"[{rql}] rows differ: first difference at {FirstDifference(before.Rows, after.Rows)}");
    }

    private static string FirstDifference(List<string> a, List<string> b)
    {
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i >= a.Count || i >= b.Count || a[i] != b[i])
                return $"{i} ({(i < a.Count ? a[i] : "-")} against {(i < b.Count ? b[i] : "-")}, {a.Count} against {b.Count} rows)";
        }

        return "none";
    }

    private static readonly (int Skip, int Take)[] Windows =
    [
        (0, 20), (0, 1), (7, 13), (100, 40), (0, 1_200), (1_990, 30), (950, 2_500), (0, 9_999), (4_000, 500), (19_000, 1_000)
    ];

    private void VerifyWindows(IDocumentStore store, string rql, bool expectWalk = true, bool project = false)
    {
        foreach (var (skip, take) in Windows)
            Verify(store, $"{rql} limit {skip}, {take}", expectWalk && skip + take <= JoinOrderedWalkSettings.MaximumTake, project);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheWalkAnswersAscendingAndDescendingOrdersOfStringsAndNumbers()
    {
        using var store = Seed();
        VerifyWindows(store, $"{Main} {AllCompanies} order by c.Name");
        VerifyWindows(store, $"{Main} {AllCompanies} order by c.Name desc");
        VerifyWindows(store, $"{Main} {AllCompanies} order by c.Rank as long");
        VerifyWindows(store, $"{Main} {AllCompanies} order by c.Rank as long desc");
        VerifyWindows(store, $"{Main} {AllCompanies} order by c.Name asc");
        VerifyWindows(store, $"{Main} {AllCompanies} order by c.Name, c.Rank as long desc");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void EveryCompanyThatTiesOnTheSortValueIsOneGroupOfEntriesInTheOrderOfTheirIds()
    {
        using var store = Seed();

        // all the companies tie: one group of more keys than a chunk of the walk, and of more entries than a page
        VerifyWindows(store, $"{Main} {AllCompanies} order by c.Const");
        VerifyWindows(store, $"{Main} {AllCompanies} order by c.Const desc");

        // 6 groups, 40 groups, the missing names
        VerifyWindows(store, $"{Main} {AllCompanies} order by c.Country");
        VerifyWindows(store, $"{Main} {AllCompanies} order by c.Country desc");
        VerifyWindows(store, $"{Main} join (from index 'Companies' where Name = 'n07') as c on o.CompanyId = id(c) order by c.Name");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ThePageOfAWalkProjectsTheOuterAndTheJoinedFields()
    {
        using var store = Seed();
        VerifyWindows(store, $"{Main} {AllCompanies} order by c.Name select id() as Id, c.Name as Name", project: true);
        VerifyWindows(store, $"{Main} {AllCompanies} order by c.Rank as long desc select id() as Id, c.Name as Name", project: true);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheWhereOfTheOuterQueryFiltersTheEntriesOfEveryKey()
    {
        using var store = Seed();
        foreach (var where in new[]
                 {
                     "where o.Status = 'open'", // most of them
                     "where o.Status = 'rare'", // 0.5%
                     "where o.Status = 'closed'",
                     "where o.Status = 'nothing'",
                     "where exists(o.Status)",
                     "where o.Amount > 50",
                     "where o.Amount < 3 or o.Status = 'rare'",
                     "where o.Tag in ('t1', 't2', 't3')",
                     "where search(o.Tag, 't1 t5')",
                     "where o.Status = 'open' and o.Amount between 10 and 20"
                 })
        {
            VerifyWindows(store, $"{Main} {AllCompanies} {where} order by c.Name");
            VerifyWindows(store, $"{Main} {AllCompanies} {where} order by c.Rank as long desc");
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheSubQueryMayFilterMatchNothingOrMissTheValue()
    {
        using var store = Seed();
        VerifyWindows(store, $"{Main} join (from index 'Companies' where Country = 'c1') as c on o.CompanyId = id(c) order by c.Name");
        VerifyWindows(store, $"{Main} join (from index 'Companies' where Country = 'c1') as c on o.CompanyId = id(c) where o.Status = 'rare' order by c.Name desc");
        VerifyWindows(store, $"{Main} join (from index 'Companies' where Rank > 40) as c on o.CompanyId = id(c) order by c.Name desc");

        // nothing matches: there are no keys, the ranks are the answer
        VerifyWindows(store, $"{Main} join (from index 'Companies' where Country = 'nowhere') as c on o.CompanyId = id(c) order by c.Name", expectWalk: false);

        // a single company
        VerifyWindows(store, $"{Main} join (from index 'Companies' where Name = 'n00') as c on o.CompanyId = id(c) order by c.Name desc");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ThePageIsReadFromTheMiddleAndPastTheEnd()
    {
        using var store = Seed();
        var total = Run(store, $"{Main} {AllCompanies} order by c.Name limit 0, 1", walk: false).Total;
        Assert.True(total > 15_000, total.ToString());

        foreach (var skip in new[] { total - 1, total - 40, total, total + 5 })
        {
            if (skip + 30 > JoinOrderedWalkSettings.MaximumTake)
            {
                // the page ends past what the walk reads: ranks
                Verify(store, $"{Main} {AllCompanies} order by c.Name limit {skip}, 30", expectWalk: false);
                continue;
            }

            Verify(store, $"{Main} {AllCompanies} order by c.Name limit {skip}, 30", expectWalk: true);
        }

        JoinOrderedWalkSettings.MaximumTake = 30_000;
        foreach (var skip in new[] { total - 1, total - 40, total, total + 5, 12_345 })
        {
            Verify(store, $"{Main} {AllCompanies} order by c.Name limit {skip}, 30", expectWalk: true);
            Verify(store, $"{Main} {AllCompanies} where o.Status = 'open' order by c.Name desc limit {skip / 2}, 700", expectWalk: true);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AJoinThatIsSmallOrWhoseSidesCannotBeWalkedIsRanked()
    {
        using var store = Seed();

        // fewer keys than the walk needs, a page that is too big
        JoinOrderedWalkSettings.MinimumKeys = 10_000;
        Verify(store, $"{Main} {AllCompanies} order by c.Name limit 0, 20", expectWalk: false);
        JoinOrderedWalkSettings.MinimumKeys = 1;
        Verify(store, $"{Main} {AllCompanies} order by c.Name limit 0, 10001", expectWalk: false);
        Verify(store, $"{Main} {AllCompanies} order by c.Name limit 9000, 2000", expectWalk: false);

        // a LEFT join, a second join, a field that holds several terms, no order of the joined index
        Verify(store, $"{Main} left join (from index 'Companies') as c on o.CompanyId = id(c) order by c.Name limit 0, 50", expectWalk: false);
        Verify(store, $"{Main} left join (from index 'Companies') as c on o.CompanyId = id(c) where o.Status = 'rare' order by c.Name desc limit 0, 50", expectWalk: false);
        Verify(store, $"{Main} {AllCompanies} join (from index 'Companies' where Country = 'c2') as d on o.CompanyId = id(d) order by c.Name limit 0, 50", expectWalk: false);
        Verify(store, $"from index 'OrdersList' as o join (from index 'Companies') as c on o.CompanyIds = id(c) order by c.Name limit 0, 50", expectWalk: false);
        Verify(store, $"{Main} {AllCompanies} order by o.Amount as long, c.Name limit 0, 50", expectWalk: false);
        Verify(store, $"{Main} {AllCompanies} order by c.Name, o.Amount as long limit 0, 50", expectWalk: false);
        Verify(store, $"{Main} {AllCompanies} order by o.Amount as long limit 0, 50", expectWalk: false);
        Verify(store, $"{Main} join (from index 'Companies' where Name = 'n07') as c on o.CompanyId = id(c) order by c.Name limit 0, 0", expectWalk: false);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AnIndexThatHoldsSeveralEntriesPerDocumentIsAnsweredLikeTheRanks()
    {
        using var store = Seed();
        foreach (var order in new[] { "order by c.Name", "order by c.Name desc", "order by c.Rank as long" })
        {
            VerifyWindows(store, $"from index 'OrdersFan' as o {AllCompanies} {order}");
            VerifyWindows(store, $"from index 'OrdersFan' as o {AllCompanies} where o.Tag = 'a1' {order}");
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AStreamedQueryReadsEveryRow()
    {
        using var store = Seed();
        var rql = $"{Main} {AllCompanies} order by c.Name desc";
        List<string> Stream(bool walk)
        {
            JoinOrderedWalkSettings.Enabled = walk;
            try
            {
                using var session = store.OpenSession();
                var rows = new List<string>();
                using var stream = session.Advanced.Stream(session.Advanced.RawQuery<Order>(rql));
                while (stream.MoveNext())
                    rows.Add(stream.Current.Document.Id);
                return rows;
            }
            finally
            {
                JoinOrderedWalkSettings.Enabled = true;
            }
        }

        var expected = Stream(false);
        Assert.True(expected.Count > 15_000);
        Assert.Equal(expected, Stream(true));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ThePageOfAWalkIsTheSameWhateverTheOrderOfTheKeysInTheIndexingBatches()
    {
        // the companies are stored again after the orders: their entries move in the joined index
        using var store = Seed();
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < Companies; i += 3)
                bulk.Store(new Company { Id = $"companies/{i}", Name = i % 2 == 0 ? $"n{i % 40:D2}" : null, Const = "same", Rank = i % 50, Country = $"c{i % 6}" });
        }

        Indexes.WaitForIndexing(store);
        VerifyWindows(store, $"{Main} {AllCompanies} order by c.Name");
        VerifyWindows(store, $"{Main} {AllCompanies} where o.Status = 'open' order by c.Name desc");
    }
}
