using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Raven.Server.Config;
using Raven.Server.Documents.Queries;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// A JOIN whose page is read from the first matches in the order of the ids: the entries are tested one by one (new path) against the whole join (old path)
public class JoinFirstEntriesTests : RavenTestBase
{
    private const int Companies = 1_500;
    private const int Orders = 20_000;

    private const string Main = "from index 'Orders' as o";
    private const string MainKey = "from index 'OrdersKey' as o";
    private const string AllCompanies = "join (from index 'Companies') as c on o.CompanyId = id(c)";
    private const string AllCompaniesKey = "join (from index 'Companies') as c on o.CompanyId = id(c)";

    public JoinFirstEntriesTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void Dispose(Raven.Server.Utils.ExceptionAggregator exceptionAggregator)
    {
        base.Dispose(exceptionAggregator);
        JoinFirstEntriesSettings.Enabled = true;
        JoinFirstEntriesSettings.MaximumTake = JoinFirstEntriesSettings.DefaultMaximumTake;
        JoinEncodedKeys.Enabled = true;
    }

    private sealed class Company
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Code { get; set; }
        public long Rank { get; set; }
        public string Country { get; set; }
    }

    private sealed class Order
    {
        public string Id { get; set; }
        public string CompanyId { get; set; }
        public string CompanyCode { get; set; }
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
            Map = companies => from c in companies select new { c.Name, c.Code, c.Rank, c.Country };
            Index(x => x.Code, FieldIndexing.Key);
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

    // the key fields are not analyzed: the ids of the joined index reach the tree as they are, and may stay encoded
    private sealed class OrdersKeyIndex : AbstractIndexCreationTask<Order>
    {
        public OrdersKeyIndex()
        {
            Map = orders => from o in orders select new { o.CompanyId, o.CompanyCode, o.Status, o.Amount, o.Tag };
            Index(x => x.CompanyId, FieldIndexing.Key);
            Index(x => x.CompanyCode, FieldIndexing.Key);
        }

        public override string IndexName => "OrdersKey";
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

    // orderByScore: false leaves the matches of a join without an ORDER BY in the order of the entries (the default orders them by score)
    private IDocumentStore Seed(bool orderByScore = true)
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        if (orderByScore == false)
        {
            options.ModifyDatabaseRecord += record =>
            {
                record.Settings[RavenConfiguration.GetKey(x => x.Indexing.OrderByScoreAutomaticallyWhenBoostingIsInvolved)] = false.ToString();
                record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxVectorSearchOrderByScoreAutomatically)] = false.ToString();
            };
        }

        var store = GetDocumentStore(options);
        new CompaniesIndex().Execute(store);
        new OrdersIndex().Execute(store);
        new OrdersKeyIndex().Execute(store);
        new OrdersListIndex().Execute(store);
        new OrdersFanIndex().Execute(store);

        var random = new Random(11);
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < Companies; i++)
            {
                // two hubs, some companies without a name
                var name = i switch { 0 => "n00", 1 => "n99", _ => random.Next(100) < 8 ? null : $"n{random.Next(40):D2}" };
                bulk.Store(new Company { Id = $"companies/{i}", Name = name, Code = $"Code-{i}", Rank = i % 50, Country = $"c{i % 6}" });
            }

            for (var i = 0; i < Orders; i++)
            {
                var pick = random.Next(100);
                var company = pick switch
                {
                    < 5 => 0, // a hub that many orders reference
                    < 10 => 1,
                    < 13 => 99999, // not a company
                    < 15 => -1, // no company
                    _ => random.Next(Companies - 100) // the last hundred companies have no order
                };

                var status = random.Next(1000) < 5 ? "rare" : random.Next(100) < 90 ? "open" : "closed";
                bulk.Store(new Order
                {
                    Id = $"orders/{i}",
                    CompanyId = company < 0 ? null : $"companies/{company}",
                    CompanyCode = company < 0 ? null : $"Code-{company}",
                    CompanyIds = [company < 0 ? "companies/2" : $"companies/{company}", $"companies/{random.Next(Companies)}"],
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

    private sealed record Outcome(List<string> Rows, long Total, long FirstEntries, long Scanned, long Probed, long FromLists);

    private static long Counter(QueryTimings timings, string name)
    {
        long counter = -1;
        if (timings?.Timings != null && timings.Timings.TryGetValue("Join", out var join))
        {
            foreach (var scope in join.Timings.Values)
            {
                if (scope.Counters != null && scope.Counters.TryGetValue(name, out var value))
                    counter = Math.Max(counter, value);
            }
        }

        return counter;
    }

    // withTotal: the statistics are asked for, so the exact number of matches is needed
    private Outcome Run(IDocumentStore store, string rql, bool firstEntries, bool withTotal, bool project = false)
    {
        JoinFirstEntriesSettings.Enabled = firstEntries;
        try
        {
            using var session = store.OpenSession();
            QueryTimings timings = null;
            QueryStatistics stats = null;
            List<string> rows;
            if (project)
            {
                var query = session.Advanced.RawQuery<Row>(rql + " include timings()").NoCaching().Timings(out timings);
                if (withTotal)
                    query = query.Statistics(out stats);
                rows = query.ToList().Select(x => $"{x.Id}|{x.Name}").ToList();
            }
            else
            {
                var query = session.Advanced.RawQuery<Order>(rql + " include timings()").NoCaching().Timings(out timings);
                if (withTotal)
                    query = query.Statistics(out stats);
                rows = query.ToList().Select(x => x.Id).ToList();
            }

            return new Outcome(rows, stats?.TotalResults ?? -1, Counter(timings, "JoinFirstEntries"), Counter(timings, "JoinFirstEntriesScanned"), Counter(timings, "JoinFirstEntriesKeysProbed"),
                Counter(timings, "JoinFirstEntriesFromLists"));
        }
        finally
        {
            JoinFirstEntriesSettings.Enabled = true;
        }
    }

    // both paths return the same rows and the same total, and the new one is the one that ran when expected.
    // expectFirst: null does not check which path ran
    private Outcome Verify(IDocumentStore store, string rql, bool? expectFirst, bool withTotal = true, bool project = false)
    {
        var before = Run(store, rql, firstEntries: false, withTotal, project);
        var after = Run(store, rql, firstEntries: true, withTotal, project);
        Assert.True(before.FirstEntries <= 0, $"[{rql}] the new path ran while it is switched off");
        if (expectFirst != null)
            Assert.True(expectFirst.Value ? after.FirstEntries == 1 : after.FirstEntries <= 0, $"[{rql}] the new path {(expectFirst.Value ? "was expected" : "was not expected")}, counter {after.FirstEntries}");
        Assert.True(before.Total == after.Total, $"[{rql}] total {before.Total} against {after.Total}");
        Assert.True(before.Rows.SequenceEqual(after.Rows), $"[{rql}] rows differ: first difference at {FirstDifference(before.Rows, after.Rows)}");
        return after;
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
        (0, 5), (0, 1), (7, 13), (100, 40), (0, 1_200), (1_990, 30), (950, 2_500), (0, 9_999), (4_000, 500), (9_900, 100)
    ];

    // with and without the total, the page of every window
    private void VerifyWindows(IDocumentStore store, string rql, bool? expectFirstWithTotal = true, bool? expectFirstWithoutTotal = true, bool project = false)
    {
        foreach (var (skip, take) in Windows)
        {
            var fits = skip + take <= JoinFirstEntriesSettings.MaximumTake;
            Verify(store, $"{rql} limit {skip}, {take}", fits ? expectFirstWithTotal : false, withTotal: true, project);
            Verify(store, $"{rql} limit {skip}, {take}", fits ? expectFirstWithoutTotal : false, withTotal: false, project);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AnIdJoinOfAPlainFieldIsAnsweredFromTheFirstEntries()
    {
        using var store = Seed();
        VerifyWindows(store, $"{Main} {AllCompanies}");
        VerifyWindows(store, $"{Main} join (from index 'Companies' where Country = 'c1') as c on o.CompanyId = id(c)");
        VerifyWindows(store, $"{Main} join (from index 'Companies' where Name = 'n07') as c on o.CompanyId = id(c)");
        VerifyWindows(store, $"{Main} join (from index 'Companies' where Name = 'n00') as c on o.CompanyId = id(c)");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AnIdJoinOfAKeyFieldIsAnsweredFromTheFirstEntriesWithEncodedAndDecodedKeys()
    {
        using var store = Seed();
        foreach (var encoded in new[] { true, false })
        {
            JoinEncodedKeys.Enabled = encoded;
            VerifyWindows(store, $"{MainKey} {AllCompaniesKey}");
            VerifyWindows(store, $"{MainKey} join (from index 'Companies' where Country = 'c1') as c on o.CompanyId = id(c)");
            VerifyWindows(store, $"{MainKey} join (from index 'Companies' where Rank > 40) as c on o.CompanyId = id(c)");
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AJoinOnAKeyFieldOfTheJoinedDocumentIsAnsweredFromTheFirstEntries()
    {
        using var store = Seed();
        VerifyWindows(store, $"{MainKey} join (from index 'Companies') as c on o.CompanyCode = c.Code");
        VerifyWindows(store, $"{MainKey} join (from index 'Companies' where Country = 'c2') as c on o.CompanyCode = c.Code");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheNumberOfEntriesOfAnEmptyJoinAndOfAnEmptyIndexIsZero()
    {
        using var store = Seed();
        VerifyWindows(store, $"{Main} join (from index 'Companies' where Country = 'nowhere') as c on o.CompanyId = id(c)");
        VerifyWindows(store, $"{MainKey} join (from index 'Companies' where Country = 'nowhere') as c on o.CompanyId = id(c)");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ThePageIsReadFromTheMiddleAndPastTheEnd()
    {
        using var store = Seed();
        var rql = $"{MainKey} join (from index 'Companies' where Country = 'c1') as c on o.CompanyId = id(c)";
        var total = Run(store, $"{rql} limit 0, 1", firstEntries: false, withTotal: true).Total;
        Assert.InRange(total, 1_500, 5_000);

        foreach (var skip in new[] { total - 1, total - 40, total, total + 5, 3_000, 100 })
        {
            foreach (var take in new[] { 30, 1, 400 })
            {
                var expect = skip + take <= JoinFirstEntriesSettings.MaximumTake;
                Verify(store, $"{rql} limit {skip}, {take}", expect, withTotal: true);
                Verify(store, $"{rql} limit {skip}, {take}", expect, withTotal: false);
            }
        }

        // fewer matches than the page
        Verify(store, $"{MainKey} join (from index 'Companies' where Name = 'n99') as c on o.CompanyId = id(c) limit 0, 5000", true, withTotal: true);
        Verify(store, $"{MainKey} join (from index 'Companies' where Name = 'n99') as c on o.CompanyId = id(c) limit 0, 5000", true, withTotal: false);
    }

    private static readonly string[] Wheres =
    [
        "where o.Status = 'open'",
        "where o.Status = 'rare'",
        "where o.Status = 'closed'",
        "where o.Status = 'nothing'",
        "where exists(o.Status)",
        "where o.Amount > 50",
        "where o.Amount < 3 or o.Status = 'rare'",
        "where o.Tag in ('t1', 't2', 't3')",
        "where search(o.Tag, 't1 t5')",
        "where o.Status = 'open' and o.Amount between 10 and 20"
    ];

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheWhereOfTheOuterQueryIsCombinedWithTheJoinWhenTheMatchesAreNotOrderedByScore()
    {
        using var store = Seed(orderByScore: false);
        foreach (var where in Wheres)
        {
            // the number of matches is not counted from the keys when a where filters the entries: the old path answers
            VerifyWindows(store, $"{MainKey} {AllCompaniesKey} {where}", expectFirstWithTotal: false, expectFirstWithoutTotal: null);
            VerifyWindows(store, $"{Main} {AllCompanies} {where}", expectFirstWithTotal: false, expectFirstWithoutTotal: null);
            VerifyWindows(store, $"{MainKey} join (from index 'Companies' where Country = 'c1') as c on o.CompanyId = id(c) {where}", expectFirstWithTotal: false, expectFirstWithoutTotal: null);
        }

        // a small where against many keys is walked, a where of most entries is not
        Verify(store, $"{MainKey} {AllCompaniesKey} where o.Status = 'rare' limit 0, 5", true, withTotal: false);
        Verify(store, $"{MainKey} {AllCompaniesKey} where o.Status = 'open' limit 0, 5", false, withTotal: false);
        Verify(store, $"{MainKey} {AllCompaniesKey} where o.Status = 'nothing' limit 0, 5", true, withTotal: false);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheMatchesOfAJoinWithAWhereAreOrderedByScoreAndAreReadWhole()
    {
        using var store = Seed();
        foreach (var where in Wheres)
        {
            Verify(store, $"{MainKey} {AllCompaniesKey} {where} limit 0, 20", false, withTotal: true);
            Verify(store, $"{MainKey} {AllCompaniesKey} {where} limit 0, 20", false, withTotal: false);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void KeysThatScoreDifferentlyAreOrderedByTheirScore()
    {
        using var store = Seed();

        // the names are analyzed: the keys of a search do not all have the same score
        Verify(store, $"{MainKey} join (from index 'Companies' where search(Name, 'n01 n02 n03 n04')) as c on o.CompanyId = id(c) limit 0, 20", false, withTotal: true);
        Verify(store, $"{MainKey} join (from index 'Companies' where Name = 'n01' or Name = 'n02' or boost(Country = 'c1', 3)) as c on o.CompanyId = id(c) limit 0, 20", false, withTotal: false);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ThePageProjectsTheOuterAndTheJoinedFields()
    {
        using var store = Seed();
        VerifyWindows(store, $"{Main} {AllCompanies} select id() as Id, c.Name as Name", project: true);
        VerifyWindows(store, $"{MainKey} {AllCompaniesKey} select id() as Id, c.Name as Name", project: true);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AJoinThatScoresOrdersOrKeepsTheOtherRowsIsReadWhole()
    {
        using var store = Seed();

        // a LEFT join, a second join, an order by, a boost that scores, matches(), a page that is too big, several terms per entry
        Verify(store, $"{Main} left join (from index 'Companies') as c on o.CompanyId = id(c) limit 0, 50", false);
        Verify(store, $"{Main} {AllCompanies} join (from index 'Companies' where Country = 'c2') as d on o.CompanyId = id(d) limit 0, 50", false);
        Verify(store, $"{Main} {AllCompanies} order by o.Amount as long limit 0, 50", false);
        Verify(store, $"{Main} {AllCompanies} order by c.Name limit 0, 50", false);
        Verify(store, $"{Main} {AllCompanies} order by score() limit 0, 50", false);
        Verify(store, $"{Main} join (from index 'Companies' where Country = 'c2') as c on o.CompanyId = id(c) boost 2 order by score() limit 0, 50", false);
        Verify(store, $"{Main} join (from index 'Companies' where Country = 'c2') as c on o.CompanyId = id(c) where o.Status = 'rare' or matches(c) limit 0, 50", false);
        Verify(store, $"{Main} {AllCompanies} limit 0, 10001", false);
        Verify(store, $"{Main} {AllCompanies} limit 9000, 2000", false);
        Verify(store, $"from index 'OrdersList' as o join (from index 'Companies') as c on o.CompanyIds = id(c) limit 0, 50", false);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ABoostThatIsNotScoredAndAJoinWithBoostZeroDoNotChangeTheRows()
    {
        using var store = Seed();
        Verify(store, $"{Main} join (from index 'Companies' where Country = 'c2') as c on o.CompanyId = id(c) boost 0 limit 0, 50", null);
        Verify(store, $"{Main} join (from index 'Companies' where Country = 'c2') as c on o.CompanyId = id(c) boost 3 limit 0, 50", null);
        Verify(store, $"{Main} join (from index 'Companies' where Country = 'c2') as c on o.CompanyId = id(c) boost 3 where boost(o.Status = 'rare', 5) limit 0, 50", null);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AnIndexThatHoldsSeveralEntriesPerDocumentIsAnsweredLikeTheWholeJoin()
    {
        using var store = Seed();
        // a page of entries holds the same document more than once: the page is read again with more entries
        VerifyWindows(store, $"from index 'OrdersFan' as o {AllCompanies}");
        VerifyWindows(store, $"from index 'OrdersFan' as o {AllCompanies} where o.Tag = 'a1'", expectFirstWithTotal: false, expectFirstWithoutTotal: false);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ACountQueryUsesTheNumberOfEntriesUnderTheKeys()
    {
        using var store = Seed();
        foreach (var rql in new[]
                 {
                     $"{Main} {AllCompanies} limit 0, 0",
                     $"{MainKey} join (from index 'Companies' where Country = 'c1') as c on o.CompanyId = id(c) limit 0, 0",
                     $"{MainKey} join (from index 'Companies' where Name = 'n99') as c on o.CompanyId = id(c) limit 0, 0"
                 })
        {
            Verify(store, rql, true);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ARowOfTheFirstEntriesChangesWithTheDataAfterTheIndexWasBuilt()
    {
        // orders and companies are stored again: the entries move, their ids are not in the order of the documents any more
        using var store = Seed();
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < Companies; i += 3)
                bulk.Store(new Company { Id = $"companies/{i}", Name = i % 2 == 0 ? $"n{i % 40:D2}" : null, Code = $"Code-{i}", Rank = i % 50, Country = $"c{i % 6}" });
            for (var i = 0; i < Orders; i += 7)
                bulk.Store(new Order { Id = $"orders/{i}", CompanyId = $"companies/{i % Companies}", CompanyCode = $"Code-{i % Companies}", CompanyIds = ["companies/2"], Tags = ["a"], Status = "open", Amount = i % 100, Tag = "t1" });
        }

        Indexes.WaitForIndexing(store);
        VerifyWindows(store, $"{Main} {AllCompanies}");
        VerifyWindows(store, $"{MainKey} {AllCompaniesKey}");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AStreamedQueryReadsEveryRow()
    {
        using var store = Seed();
        var rql = $"{MainKey} {AllCompaniesKey}";
        List<string> Stream(bool firstEntries)
        {
            JoinFirstEntriesSettings.Enabled = firstEntries;
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
                JoinFirstEntriesSettings.Enabled = true;
            }
        }

        var expected = Stream(false);
        Assert.True(expected.Count > 15_000);
        Assert.Equal(expected, Stream(true));
    }
}
