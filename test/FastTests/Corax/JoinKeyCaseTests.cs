using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Exceptions;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;

namespace FastTests.Corax;

// A join compares terms of the index
public class JoinKeyCaseTests(ITestOutputHelper output, SharedJoinDatabase shared) : RavenTestBase(output), IClassFixture<SharedJoinDatabase>
{
    private const string Everything = "limit 0, 1000000";

    private sealed class Company
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Code { get; set; }
        public string Tag { get; set; }
        public string[] Codes { get; set; }
    }

    private sealed class Order
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string CompanyId { get; set; }
        public string Code { get; set; }
        public string[] Codes { get; set; }
    }

    private sealed class CompaniesDefault : AbstractIndexCreationTask<Company>
    {
        public CompaniesDefault()
        {
            Map = companies => from c in companies select new { c.Name, c.Code, c.Tag };
        }

        public override string IndexName => "CompaniesDefault";
    }

    private sealed class CompaniesExact : AbstractIndexCreationTask<Company>
    {
        public CompaniesExact()
        {
            Map = companies => from c in companies select new { c.Name, c.Code, c.Tag };
            Index(x => x.Code, FieldIndexing.Exact);
        }

        public override string IndexName => "CompaniesExact";
    }

    private sealed class OrdersDefault : AbstractIndexCreationTask<Order>
    {
        public OrdersDefault()
        {
            Map = orders => from o in orders select new { o.Name, o.CompanyId, o.Code };
        }

        public override string IndexName => "OrdersDefault";
    }

    private sealed class OrdersExact : AbstractIndexCreationTask<Order>
    {
        public OrdersExact()
        {
            Map = orders => from o in orders select new { o.Name, o.CompanyId, o.Code };
            Index(x => x.CompanyId, FieldIndexing.Exact);
            Index(x => x.Code, FieldIndexing.Exact);
        }

        public override string IndexName => "OrdersExact";
    }

    private sealed class OrdersSearch : AbstractIndexCreationTask<Order>
    {
        public OrdersSearch()
        {
            Map = orders => from o in orders select new { o.Name, o.CompanyId, o.Code };
            Index(x => x.Code, FieldIndexing.Search);
            Index(x => x.CompanyId, FieldIndexing.Search);
        }

        public override string IndexName => "OrdersSearch";
    }

    private sealed class CompaniesMultiDefault : AbstractIndexCreationTask<Company>
    {
        public CompaniesMultiDefault()
        {
            Map = companies => from c in companies select new { c.Name, c.Tag, c.Codes };
        }

        public override string IndexName => "CompaniesMultiDefault";
    }

    private sealed class CompaniesMultiExact : AbstractIndexCreationTask<Company>
    {
        public CompaniesMultiExact()
        {
            Map = companies => from c in companies select new { c.Name, c.Tag, c.Codes };
            Index(x => x.Codes, FieldIndexing.Exact);
        }

        public override string IndexName => "CompaniesMultiExact";
    }

    private sealed class OrdersMultiDefault : AbstractIndexCreationTask<Order>
    {
        public OrdersMultiDefault()
        {
            Map = orders => from o in orders select new { o.Name, o.Codes };
        }

        public override string IndexName => "OrdersMultiDefault";
    }

    private sealed class OrdersMultiExact : AbstractIndexCreationTask<Order>
    {
        public OrdersMultiExact()
        {
            Map = orders => from o in orders select new { o.Name, o.Codes };
            Index(x => x.Codes, FieldIndexing.Exact);
        }

        public override string IndexName => "OrdersMultiExact";
    }

    // the tests that only read share one database (see SharedJoinDatabase); the one that adds documents has its own
    private IDocumentStore Seed() => shared.Get("seed", CoraxOptions(), o => GetDocumentStore(o), store =>
    {
        Populate(store);
        new CompaniesWithDocumentId().Execute(store);
        new OrdersWithKeyCopy().Execute(store);
        Indexes.WaitForIndexing(store);
    });

    private IDocumentStore SeedOwn()
    {
        var store = GetDocumentStore(CoraxOptions());
        Populate(store);
        return store;
    }

    private void Populate(IDocumentStore store)
    {
        new CompaniesDefault().Execute(store);
        new CompaniesExact().Execute(store);
        new OrdersDefault().Execute(store);
        new OrdersExact().Execute(store);
        new OrdersSearch().Execute(store);

        using (var session = store.OpenSession())
        {
            session.Store(new Company { Id = "companies/8213-A", Name = "alpha", Code = "8213-A", Tag = "big" });
            session.Store(new Company { Id = "companies/ÄÖ-7", Name = "umlaut", Code = "ÄÖ-7", Tag = "big" });
            session.Store(new Company { Id = "companies/lower-1", Name = "lower", Code = "lower-1", Tag = "small" });
            session.Store(new Company { Id = "companies/Mixed-Case-3", Name = "mixed", Code = "Mixed-Case-3", Tag = "small" });

            session.Store(new Order { Id = "orders/1", Name = "one", CompanyId = "companies/8213-A", Code = "8213-A" });
            session.Store(new Order { Id = "orders/2", Name = "two", CompanyId = "companies/ÄÖ-7", Code = "ÄÖ-7" });
            session.Store(new Order { Id = "orders/3", Name = "three", CompanyId = "companies/lower-1", Code = "lower-1" });
            session.Store(new Order { Id = "orders/4", Name = "four", CompanyId = "companies/8213-A", Code = "8213-A" });
            session.Store(new Order { Id = "orders/5", Name = "five", CompanyId = "companies/other", Code = "other" });
            session.Store(new Order { Id = "orders/6", Name = "six", CompanyId = "COMPANIES/mixed-case-3", Code = "Mixed-Case-3" }); // the case of the id in the key is not the case of the document
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
    }

    private static string[] Names(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<Order>($"{rql} {Everything}").ToList().Select(x => x.Name).OrderBy(x => x).ToArray();
    }

    private void Rejects(IDocumentStore store, string rql, string expectedMessage)
    {
        var e = Assert.Throws<InvalidQueryException>(() => Names(store, rql));
        var message = e.Message.Split('\n')[0].TrimEnd('\r');
        message = message.Substring(message.IndexOf("The JOIN", StringComparison.Ordinal));
        output.WriteLine(message);
        Assert.Equal(expectedMessage, message);
    }

    private const string BigCompanies = "(from index 'CompaniesDefault' where Tag = 'big')";
    private const string AllCompanies = "(from index 'CompaniesDefault' where search(Name, 'alpha umlaut lower mixed'))";

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void IdJoinOnDefaultIndexedKeyMatchesMixedCaseIds()
    {
        using var store = Seed();

        Assert.Equal(["four", "one", "two"], Names(store, $"from index 'OrdersDefault' as o join {BigCompanies} as c on o.CompanyId = id(c)"));
        Assert.Equal(["four", "one", "two"], Names(store, $"from index 'OrdersDefault' as o join {BigCompanies} as c on o.CompanyId = id(c) order by score()"));
        Assert.Equal(["five", "four", "one", "two"], Names(store, $"from index 'OrdersDefault' as o join {BigCompanies} as c on o.CompanyId = id(c) where search(o.Name, 'five') or matches(c)"));
        Assert.Equal(["six", "three"], Names(store, $"from index 'OrdersDefault' as o join (from index 'CompaniesDefault' where Tag = 'small') as c on o.CompanyId = id(c)"));
        Assert.Equal(["four", "one", "six", "three", "two"], Names(store, $"from index 'OrdersDefault' as o join {AllCompanies} as c on o.CompanyId = id(c)"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void IdJoinWithInnerSortAndOrAndTimings()
    {
        using var store = Seed();

        using (var session = store.OpenSession())
        {
            var rows = session.Advanced.RawQuery<Order>($"from index 'OrdersDefault' as o join {AllCompanies} as c on o.CompanyId = id(c) order by c.Name {Everything}").ToList();
            // alpha (one, four), lower (three), mixed (six), umlaut (two)
            Assert.Equal(5, rows.Count);
            var names = rows.Select(x => x.Name).ToList();
            Assert.True(names.IndexOf("three") > names.IndexOf("one") && names.IndexOf("three") > names.IndexOf("four"));
            Assert.True(names.IndexOf("six") > names.IndexOf("three"));
            Assert.True(names.IndexOf("two") > names.IndexOf("six"));
        }

        var or = Names(store, $"from index 'OrdersDefault' as o join {BigCompanies} as c on o.CompanyId = id(c) join (from index 'CompaniesDefault' where Tag = 'small') as s on o.CompanyId = id(s) where matches(c) or matches(s)");
        Assert.Equal(["four", "one", "six", "three", "two"], or);

        using (var session = store.OpenSession())
        {
            var rows = session.Advanced.RawQuery<Order>($"from index 'OrdersDefault' as o join {BigCompanies} as c on o.CompanyId = id(c) order by score() include timings() {Everything}")
                .Timings(out var timings).ToList();
            Assert.Equal(3, rows.Count);
            Assert.Equal(2, timings.Timings["Join"].Timings["c"].Counters["SourceMatches"]);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void FieldJoinOfTwoFieldsIndexedTheSameWayMatches()
    {
        using var store = Seed();

        // both default: lowercased on both sides, whatever the case of the values
        Assert.Equal(["four", "one", "two"], Names(store, "from index 'OrdersDefault' as o join (from index 'CompaniesDefault' where Tag = 'big') as c on o.Code = c.Code"));
        Assert.Equal(["four", "one", "two"], Names(store, "from index 'OrdersDefault' as o join (from index 'CompaniesDefault' where Tag = 'big') as c on o.Code = c.Code order by score()"));
        Assert.Equal(["six", "three"], Names(store, "from index 'OrdersDefault' as o join (from index 'CompaniesDefault' where Tag = 'small') as c on o.Code = c.Code"));

        // both Exact: the case is kept on both sides, the terms are equal
        Assert.Equal(["four", "one", "two"], Names(store, "from index 'OrdersExact' as o join (from index 'CompaniesExact' where Tag = 'big') as c on o.Code = c.Code"));
        Assert.Equal(["six", "three"], Names(store, "from index 'OrdersExact' as o join (from index 'CompaniesExact' where Tag = 'small') as c on o.Code = c.Code order by score()"));
        Assert.Equal(["four", "one", "six", "three", "two"], Names(store, "from index 'OrdersExact' as o join (from index 'CompaniesExact' where search(Name, 'alpha umlaut lower mixed')) as c on o.Code = c.Code where matches(c) or search(o.Name, 'one')"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void IdJoinOnExactKeyIsRejected()
    {
        using var store = Seed();

        const string expected = "The JOIN with 'c' would never match: the field 'CompanyId' of the index 'OrdersExact' is indexed Exact (the value 'ÄbCZ1' is indexed as 'ÄbCZ1'), " +
                                "but a join on id(c) compares the lowercased ids of the documents of 'c' ('ÄbCZ1' becomes 'äbcz1'). " +
                                "Index 'CompanyId' with the default indexing (remove the Exact option or the custom analyzer) so that its terms are lowercased too, or index it with FieldIndexing.Key. " +
                                "A field with the default indexing is searched case-insensitively, like a document id (where CompanyId = 'COMPANIES-8213-A' finds 'companies-8213-a'), and this is what a join on id(c) compares.";

        Rejects(store, $"from index 'OrdersExact' as o join {BigCompanies} as c on o.CompanyId = id(c)", expected);
        Rejects(store, $"from index 'OrdersExact' as o join {BigCompanies} as c on o.CompanyId = id(c) order by score()", expected);
        Rejects(store, $"from index 'OrdersExact' as o join {BigCompanies} as c on o.CompanyId = id(c) where matches(c) or search(o.Name, 'one')", expected);
        Rejects(store, $"from index 'OrdersExact' as o join {BigCompanies} as c on o.CompanyId = id(c) order by c.Name", expected);
        // even when nothing matches: the query is wrong, not empty
        Rejects(store, "from index 'OrdersExact' as o join (from index 'CompaniesDefault' where Tag = 'nothing') as c on o.CompanyId = id(c)", expected);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void FieldJoinOfFieldsThatCannotMatchIsRejected()
    {
        using var store = Seed();

        const string searchOuter = "The JOIN with 'c' would never match: the field 'Code' of the index 'OrdersSearch' is indexed Search (the value 'ÄbCZ1' is indexed as 'äbcz1'), " +
                                   "but the field 'Code' of the joined index 'CompaniesExact' is indexed Exact ('ÄbCZ1' is indexed as 'ÄbCZ1'), " +
                                   "and a join compares the terms of both fields as they are. Index both fields the same way (both with the default indexing, or both Exact), or index them with FieldIndexing.Key.";
        Rejects(store, "from index 'OrdersSearch' as o join (from index 'CompaniesExact' where Tag = 'big') as c on o.Code = c.Code", searchOuter);

        const string exactOuter = "The JOIN with 'c' would never match: the field 'Code' of the index 'OrdersExact' is indexed Exact (the value 'ÄbCZ1' is indexed as 'ÄbCZ1'), " +
                                  "but the field 'Code' of the joined index 'CompaniesDefault' is indexed with the default indexing ('ÄbCZ1' is indexed as 'äbcz1'), " +
                                  "and a join compares the terms of both fields as they are. Index both fields the same way (both with the default indexing, or both Exact), or index them with FieldIndexing.Key.";
        Rejects(store, "from index 'OrdersExact' as o join (from index 'CompaniesDefault' where Tag = 'big') as c on o.Code = c.Code", exactOuter);
        Rejects(store, "from index 'OrdersExact' as o join (from index 'CompaniesDefault' where Tag = 'big') as c on o.Code = c.Code where matches(c) or search(o.Name, 'one')", exactOuter);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void SearchIndexedKeyKeepsWorking()
    {
        using var store = Seed();

        // the Search analyzer lowercases (the lookup uses the default analyzer): single token values keep matching
        Assert.Equal(["four", "one", "two"], Names(store, "from index 'OrdersSearch' as o join (from index 'CompaniesDefault' where Tag = 'big') as c on o.Code = c.Code"));
    }

    private sealed class Projection
    {
        public string Name { get; set; }
    }

    private static string[] Projected(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<Projection>($"{rql} {Everything}").ToList().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void IdJoinOnByteKeysProjectsTheJoinedDocumentWithMixedCaseAndNonAsciiIds()
    {
        using var store = Seed();

        Assert.Equal(["alpha", "alpha", "umlaut"], Projected(store, $"from index 'OrdersDefault' as o join {BigCompanies} as c on o.CompanyId = id(c) select c.Name"));
        Assert.Equal(["alpha", "alpha", "umlaut"], Projected(store, $"from index 'OrdersDefault' as o join {BigCompanies} as c on o.CompanyId = id(c) order by score() select c.Name"));
        Assert.Equal(["alpha", "alpha", "lower", "mixed", "umlaut"], Projected(store, $"from index 'OrdersDefault' as o join {AllCompanies} as c on o.CompanyId = id(c) select c.Name"));
        // with OR the alias is only the joined document of the rows that the join matched
        Assert.Equal(["five", "four", "one", "two"], Names(store, $"from index 'OrdersDefault' as o join {BigCompanies} as c on o.CompanyId = id(c) where search(o.Name, 'five') or matches(c)"));
        Assert.Equal(["alpha", "alpha", "umlaut"], Projected(store, $"from index 'OrdersDefault' as o join {BigCompanies} as c on o.CompanyId = id(c) where matches(c) or search(o.Name, 'six') select c.Name").Where(x => x != null).ToArray());

        const string expected = "The JOIN with 'c' would never match: the field 'CompanyId' of the index 'OrdersExact' is indexed Exact (the value 'ÄbCZ1' is indexed as 'ÄbCZ1'), " +
                                "but a join on id(c) compares the lowercased ids of the documents of 'c' ('ÄbCZ1' becomes 'äbcz1'). " +
                                "Index 'CompanyId' with the default indexing (remove the Exact option or the custom analyzer) so that its terms are lowercased too, or index it with FieldIndexing.Key. " +
                                "A field with the default indexing is searched case-insensitively, like a document id (where CompanyId = 'COMPANIES-8213-A' finds 'companies-8213-a'), and this is what a join on id(c) compares.";
        var e = Assert.Throws<InvalidQueryException>(() => Projected(store, $"from index 'OrdersExact' as o join {BigCompanies} as c on o.CompanyId = id(c) select c.Name"));
        Assert.Contains(expected, e.Message);
        e = Assert.Throws<InvalidQueryException>(() => Projected(store, $"from index 'OrdersExact' as o join {BigCompanies} as c on o.CompanyId = id(c) where matches(c) or search(o.Name, 'one') select c.Name"));
        Assert.Contains(expected, e.Message);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void FieldJoinMatchesMixedCaseValuesWhateverTheScanStrategy()
    {
        using var store = Seed();

        // one term per entry and few terms: the entry -> term map (BulkMap) is read
        Assert.Equal(["four", "one", "two"], Names(store, "from index 'OrdersDefault' as o join (from index 'CompaniesDefault' where Tag = 'big') as c on o.Code = c.Code"));
        Assert.Equal(["alpha", "alpha", "umlaut"], Projected(store, "from index 'OrdersDefault' as o join (from index 'CompaniesDefault' where Tag = 'big') as c on o.Code = c.Code select c.Name"));
        Assert.Equal(["four", "one", "six", "three", "two"], Names(store, "from index 'OrdersDefault' as o join (from index 'CompaniesDefault' where search(Name, 'alpha umlaut lower mixed')) as c on o.Code = c.Code order by score()"));
        Assert.Equal(["five", "four", "one", "two"], Names(store, "from index 'OrdersDefault' as o join (from index 'CompaniesDefault' where Tag = 'big') as c on o.Code = c.Code where search(o.Name, 'five') or matches(c)"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheKeyCheckDoesNotDependOnTheScanStrategy()
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        using var store = GetDocumentStore(options);
        new CompaniesMultiDefault().Execute(store);
        new CompaniesMultiExact().Execute(store);
        new OrdersMultiDefault().Execute(store);
        new OrdersMultiExact().Execute(store);
        new CompaniesDefault().Execute(store);

        using (var session = store.OpenSession())
        {
            // many entries that never match
            for (var i = 0; i < 400; i++)
                session.Store(new Company { Id = $"companies/filler-{i}", Name = $"filler{i}", Tag = "filler", Codes = [$"Filler-{i}", $"Other-{i}"] });
            session.Store(new Company { Id = "companies/a", Name = "alpha", Tag = "few", Codes = ["Mixed-A", "ÄÖ-7"] });
            for (var i = 0; i < 300; i++)
                session.Store(new Company { Id = $"companies/many-{i}", Name = $"many{i}", Tag = "many", Codes = [$"Many-{i}", $"Many-X-{i}"] });
            session.Store(new Order { Id = "orders/1", Name = "one", Codes = ["mixed-a"] });
            session.Store(new Order { Id = "orders/2", Name = "two", Codes = ["äö-7", "zzz"] });
            session.Store(new Order { Id = "orders/3", Name = "three", Codes = ["many-5"] });
            session.Store(new Order { Id = "orders/4", Name = "four", Codes = ["nothing"] });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        Assert.Equal(["one", "two"], Names(store, "from index 'OrdersMultiDefault' as o join (from index 'CompaniesMultiDefault' where Tag = 'few') as c on o.Codes = c.Codes"));
        Assert.Equal(["three"], Names(store, "from index 'OrdersMultiDefault' as o join (from index 'CompaniesMultiDefault' where Tag = 'many') as c on o.Codes = c.Codes"));
        Assert.Equal(["one", "three", "two"], Names(store, "from index 'OrdersMultiDefault' as o join (from index 'CompaniesMultiDefault' where Tag = 'many' or Tag = 'few') as c on o.Codes = c.Codes"));

        // a default indexed outer field and an Exact joined field match case-insensitively
        Assert.Equal(["one", "two"], Names(store, "from index 'OrdersMultiDefault' as o join (from index 'CompaniesMultiExact' where Tag = 'few') as c on o.Codes = c.Codes"));
        Assert.Equal(["three"], Names(store, "from index 'OrdersMultiDefault' as o join (from index 'CompaniesMultiExact' where Tag = 'many') as c on o.Codes = c.Codes"));

        // the other way round the case cannot be recovered from the index
        const string expected = "The JOIN with 'c' would never match: the field 'Codes' of the index 'OrdersMultiExact' is indexed Exact (the value 'ÄbCZ1' is indexed as 'ÄbCZ1'), " +
                                "but the field 'Codes' of the joined index 'CompaniesMultiDefault' is indexed with the default indexing ('ÄbCZ1' is indexed as 'äbcz1'), " +
                                "and a join compares the terms of both fields as they are. Index both fields the same way (both with the default indexing, or both Exact), or index them with FieldIndexing.Key.";
        foreach (var tag in new[] { "few", "many", "filler", "nothing" })
            Rejects(store, $"from index 'OrdersMultiExact' as o join (from index 'CompaniesMultiDefault' where Tag = '{tag}') as c on o.Codes = c.Codes", expected);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void SearchIndexedKeyIsTokenizedSoTheJoinMatchesOnlyAWholeSingleToken()
    {
        using var store = Seed();

        output.WriteLine("Code terms: " + string.Join(" | ", store.Maintenance.Send(new GetTermsOperation("OrdersSearch", "Code", null, 100))));
        output.WriteLine("CompanyId terms: " + string.Join(" | ", store.Maintenance.Send(new GetTermsOperation("OrdersSearch", "CompanyId", null, 100))));

        var idJoin = Names(store, $"from index 'OrdersSearch' as o join {AllCompanies} as c on o.CompanyId = id(c)");
        output.WriteLine("id join over a Search FK: " + string.Join(",", idJoin));
        // the Search analyzer keeps 'companies/8213-a' as one token but splits 'companies/lower-1'
        Assert.Equal(["four", "one"], idJoin);
        var codeJoin = Names(store, "from index 'OrdersSearch' as o join (from index 'CompaniesDefault' where Tag = 'small') as c on o.Code = c.Code");
        output.WriteLine("field join over a Search Code, Tag small: " + string.Join(",", codeJoin));
        // 'lower-1' is one token, 'mixed-case-3' is split into 'mixed' and 'case-3' (order six never matches)
        Assert.Equal(["three"], codeJoin);
    }

    private const string ExactCompanies = "from index 'CompaniesExact'";

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void FieldJoinOfADefaultOuterFieldAndAnExactInnerFieldIsCaseInsensitive()
    {
        using var store = Seed();

        // a default indexed outer field is searched case-insensitively
        Assert.Equal(["four", "one", "two"], Names(store, $"from index 'OrdersDefault' as o join ({ExactCompanies} where Tag = 'big') as c on o.Code = c.Code"));
        Assert.Equal(["six", "three"], Names(store, $"from index 'OrdersDefault' as o join ({ExactCompanies} where Tag = 'small') as c on o.Code = c.Code order by score()"));
        Assert.Equal(["four", "one", "six", "three", "two"], Names(store, $"from index 'OrdersDefault' as o join ({ExactCompanies} where search(Name, 'alpha umlaut lower mixed')) as c on o.Code = c.Code"));
        Assert.Equal(["five", "four", "one", "two"], Names(store, $"from index 'OrdersDefault' as o join ({ExactCompanies} where Tag = 'big') as c on o.Code = c.Code where search(o.Name, 'five') or matches(c)"));
        Assert.Equal(["alpha", "alpha", "umlaut"], Projected(store, $"from index 'OrdersDefault' as o join ({ExactCompanies} where Tag = 'big') as c on o.Code = c.Code select c.Name"));
        Assert.Equal(["alpha", "alpha", "lower", "mixed", "umlaut"], Projected(store, $"from index 'OrdersDefault' as o join ({ExactCompanies} where search(Name, 'alpha umlaut lower mixed')) as c on o.Code = c.Code select c.Name"));

        using var session = store.OpenSession();
        var rows = session.Advanced.RawQuery<Order>($"from index 'OrdersDefault' as o join ({ExactCompanies} where Tag = 'big') as c on o.Code = c.Code order by score() include timings() {Everything}")
            .Timings(out var timings).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, timings.Timings["Join"].Timings["c"].Counters["SourceMatches"]);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void SeveralKeysThatFoldToOneTermAddUpTheirScores()
    {
        using var store = SeedOwn();
        using (var session = store.OpenSession())
        {
            session.Store(new Company { Id = "companies/f1", Name = "first", Code = "Abc", Tag = "dup" });
            session.Store(new Company { Id = "companies/f2", Name = "second", Code = "ABC", Tag = "dup" });
            session.Store(new Company { Id = "companies/f3", Name = "third", Code = "Xyz", Tag = "dup" });
            session.Store(new Order { Id = "orders/x", Name = "x", Code = "abc" });
            session.Store(new Order { Id = "orders/y", Name = "y", Code = "xyz" });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        // 'Abc' and 'ABC' are both keys of the outer term 'abc': the entry is under two keys and adds up their scores
        var ranked = Names(store, $"from index 'OrdersDefault' as o join ({ExactCompanies} where Tag = 'dup') as c on o.Code = c.Code");
        Assert.Equal(["x", "y"], ranked);

        using var session2 = store.OpenSession();
        var rows = session2.Advanced.RawQuery<Order>($"from index 'OrdersDefault' as o join ({ExactCompanies} where Tag = 'dup') as c on o.Code = c.Code order by score() {Everything}").ToList();
        Assert.Equal(["x", "y"], rows.Select(x => x.Name).ToArray());

        var projected = session2.Advanced.RawQuery<Projection>($"from index 'OrdersDefault' as o join ({ExactCompanies} where Tag = 'dup') as c on o.Code = c.Code order by score() select o.Name as Name {Everything}").ToList();
        Assert.Equal(2, projected.Count);
        // the joined document of 'abc' is the best scoring of the documents that fold to it, the first one on a tie
        Assert.Equal(["first", "third"], Projected(store, $"from index 'OrdersDefault' as o join ({ExactCompanies} where Tag = 'dup') as c on o.Code = c.Code order by score() select c.Name"));
    }

    private sealed class TwoNames
    {
        public string Key { get; set; }
        public string First { get; set; }
        public string Second { get; set; }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrOfTwoJoinsOnTheSameOuterFieldAndAFieldJoinNextToAnIdJoinMatchAnOracle()
    {
        using var store = Seed();

        var companies = new[]
        {
            (Id: "companies/8213-A", Name: "alpha", Code: "8213-A", Tag: "big"),
            (Id: "companies/ÄÖ-7", Name: "umlaut", Code: "ÄÖ-7", Tag: "big"),
            (Id: "companies/lower-1", Name: "lower", Code: "lower-1", Tag: "small"),
            (Id: "companies/Mixed-Case-3", Name: "mixed", Code: "Mixed-Case-3", Tag: "small"),
        };
        var orders = new[]
        {
            (Name: "one", CompanyId: "companies/8213-A", Code: "8213-A"),
            (Name: "two", CompanyId: "companies/ÄÖ-7", Code: "ÄÖ-7"),
            (Name: "three", CompanyId: "companies/lower-1", Code: "lower-1"),
            (Name: "four", CompanyId: "companies/8213-A", Code: "8213-A"),
            (Name: "five", CompanyId: "companies/other", Code: "other"),
            (Name: "six", CompanyId: "COMPANIES/mixed-case-3", Code: "Mixed-Case-3"),
        };

        static bool Same(string a, string b) => string.Equals(a.ToLowerInvariant(), b.ToLowerInvariant(), StringComparison.Ordinal);

        (string Key, string First, string Second)[] Oracle(string[] firstNames, Func<(string Name, string CompanyId, string Code), string, bool> firstMatches, string[] secondNames, bool or)
        {
            var rows = new List<(string, string, string)>();
            foreach (var o in orders)
            {
                var first = companies.Where(c => firstNames.Contains(c.Name) && firstMatches(o, c.Id + "|" + c.Code)).Select(c => c.Name).FirstOrDefault();
                var second = companies.Where(c => secondNames.Contains(c.Name) && Same(o.CompanyId, c.Id)).Select(c => c.Name).FirstOrDefault();
                if (or ? first != null || second != null : first != null && second != null)
                    rows.Add((o.Name, first, second));
            }

            return rows.OrderBy(x => x.Item1, StringComparer.Ordinal).ToArray();
        }

        (string Key, string First, string Second)[] Run(string rql)
        {
            using var session = store.OpenSession();
            return session.Advanced.RawQuery<TwoNames>($"{rql} {Everything}").ToList().Select(x => (x.Key, x.First, x.Second)).OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
        }

        // two id joins on the same outer field, OR: the alias in the select picks the join that its name says
        const string small = "(from index 'CompaniesDefault' where search(Name, 'umlaut lower mixed'))";
        var idIdFirst = new[] { "alpha", "umlaut" };
        var idIdSecond = new[] { "umlaut", "lower", "mixed" };
        var idJoins = $"from index 'OrdersDefault' as o join {BigCompanies} as u on o.CompanyId = id(u) join {small} as d on o.CompanyId = id(d)";
        var idOracleOr = Oracle(idIdFirst, (o, c) => Same(o.CompanyId, c.Split('|')[0]), idIdSecond, or: true);
        Assert.Equal(idOracleOr, Run($"{idJoins} where matches(u) or matches(d) select o.Name as Key, u.Name as First, d.Name as Second"));
        Assert.Equal(idOracleOr, Run($"{idJoins} where matches(u) or matches(d) order by score() select o.Name as Key, u.Name as First, d.Name as Second"));
        var idOracleAnd = Oracle(idIdFirst, (o, c) => Same(o.CompanyId, c.Split('|')[0]), idIdSecond, or: false);
        Assert.Equal(idOracleAnd, Run($"{idJoins} select o.Name as Key, u.Name as First, d.Name as Second"));
        // the rows that matched only one join show null for the other alias
        Assert.Contains(idOracleOr, x => x.First == null && x.Second != null);
        Assert.Contains(idOracleOr, x => x.First != null && x.Second == null);
        Assert.Contains(idOracleOr, x => x.First != null && x.Second != null);

        // a field join next to an id join on the other outer field
        var mixed = $"from index 'OrdersDefault' as o join ({ExactCompanies} where Tag = 'big') as c on o.Code = c.Code join {small} as d on o.CompanyId = id(d)";
        var fieldFirst = new[] { "alpha", "umlaut" };
        bool CodeMatches(string name, string code) => Same(name, code);
        (string Key, string First, string Second)[] MixedOracle(bool or)
        {
            var rows = new List<(string, string, string)>();
            foreach (var o in orders)
            {
                var first = companies.Where(c => fieldFirst.Contains(c.Name) && CodeMatches(o.Code, c.Code)).Select(c => c.Name).FirstOrDefault();
                var second = companies.Where(c => idIdSecond.Contains(c.Name) && Same(o.CompanyId, c.Id)).Select(c => c.Name).FirstOrDefault();
                if (or ? first != null || second != null : first != null && second != null)
                    rows.Add((o.Name, first, second));
            }

            return rows.OrderBy(x => x.Item1, StringComparer.Ordinal).ToArray();
        }

        Assert.Equal(MixedOracle(or: false), Run($"{mixed} select o.Name as Key, c.Name as First, d.Name as Second"));
        Assert.Equal(MixedOracle(or: true), Run($"{mixed} where matches(c) or matches(d) select o.Name as Key, c.Name as First, d.Name as Second"));
        Assert.Equal(MixedOracle(or: true), Run($"{mixed} where matches(d) or matches(c) order by score() select o.Name as Key, c.Name as First, d.Name as Second"));
        Assert.Contains(MixedOracle(or: true), x => x.First == null && x.Second != null);
        Assert.Contains(MixedOracle(or: true), x => x.First != null && x.Second != null);
    }

    private sealed class CompaniesWithDocumentId : AbstractIndexCreationTask<Company>
    {
        public CompaniesWithDocumentId()
        {
            Map = companies => from c in companies select new { c.Name, c.Tag, DocId = MetadataFor(c)["@id"] };
            Index("DocId", FieldIndexing.Exact);
        }

        public override string IndexName => "CompaniesWithDocumentId";
    }

    private sealed class OrdersWithKeyCopy : AbstractIndexCreationTask<Order>
    {
        public OrdersWithKeyCopy()
        {
            Map = orders => from o in orders select new { o.Name, o.CompanyId, CompanyIdKey = o.CompanyId };
            Index(x => x.CompanyId, FieldIndexing.Exact);
        }

        public override string IndexName => "OrdersWithKeyCopy";
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void RecipesForAnExactForeignKey()
    {
        using var store = Seed();

        // an Exact foreign key matches a document id only as it is written
        var filtered = Names(store, "from index 'OrdersExact' where CompanyId in ('companies/8213-A', 'companies/ÄÖ-7', 'companies/lower-1')");
        Assert.Equal(["four", "one", "three", "two"], filtered);

        // recipe 1: the joined index holds the id as it is written (Exact), the join is a join on the fields, both Exact
        Assert.Equal(["four", "one", "two"], Names(store, "from index 'OrdersExact' as o join (from index 'CompaniesWithDocumentId' where Tag = 'big') as c on o.CompanyId = c.DocId"));
        Assert.Equal(["four", "one", "two"], Names(store, "from index 'OrdersExact' as o join (from index 'CompaniesWithDocumentId' where Tag = 'big') as c on o.CompanyId = c.DocId order by score()"));
        Assert.Equal(["alpha", "alpha", "umlaut"], Projected(store, "from index 'OrdersExact' as o join (from index 'CompaniesWithDocumentId' where Tag = 'big') as c on o.CompanyId = c.DocId select c.Name"));
        Assert.Equal(filtered, Names(store, "from index 'OrdersExact' as o join (from index 'CompaniesWithDocumentId' where search(Name, 'alpha umlaut lower')) as c on o.CompanyId = c.DocId"));

        // recipe 2: a second, default indexed copy of the key in the outer index, joined on the id
        Assert.Equal(["four", "one", "two"], Names(store, $"from index 'OrdersWithKeyCopy' as o join {BigCompanies} as c on o.CompanyIdKey = id(c)"));
        Assert.Equal(["four", "one", "six", "three", "two"], Names(store, $"from index 'OrdersWithKeyCopy' as o join {AllCompanies} as c on o.CompanyIdKey = id(c) order by score()"));
        // (a projection of c needs the key to be a field of the document: the copy is only in the index)
    }
}
