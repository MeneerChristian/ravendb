using System;
using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Indexes.Spatial;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;

namespace FastTests.Corax;

public class FieldIndexingKeyTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Everything = "limit 0, 1000000";

    private sealed class Company
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Code { get; set; }
        public string Tag { get; set; }
    }

    private sealed class Order
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string CompanyId { get; set; }
        public string Code { get; set; }
        public string[] Codes { get; set; }
    }

    private sealed class CompaniesPlain : AbstractIndexCreationTask<Company>
    {
        public CompaniesPlain() => Map = companies => from c in companies select new { c.Name, c.Code, c.Tag };
    }

    private sealed class CompaniesKeyCode : AbstractIndexCreationTask<Company>
    {
        public CompaniesKeyCode()
        {
            Map = companies => from c in companies select new { c.Name, c.Code, c.Tag };
            Index(x => x.Code, FieldIndexing.Key);
        }
    }

    private sealed class OrdersPlain : AbstractIndexCreationTask<Order>
    {
        public OrdersPlain() => Map = orders => from o in orders select new { o.Name, o.CompanyId, o.Code, o.Codes };
    }

    private sealed class OrdersKey : AbstractIndexCreationTask<Order>
    {
        public OrdersKey()
        {
            Map = orders => from o in orders select new { o.Name, o.CompanyId, o.Code, o.Codes };
            Index(x => x.CompanyId, FieldIndexing.Key);
            Index(x => x.Code, FieldIndexing.Key);
            Index(x => x.Codes, FieldIndexing.Key);
        }
    }

    private IDocumentStore Seed()
    {
        var store = GetDocumentStore(CoraxOptions());
        using (var session = store.OpenSession())
        {
            session.Store(new Company { Id = "companies/8213-A", Name = "alpha", Code = "8213-A", Tag = "big" });
            session.Store(new Company { Id = "companies/ÄÖ-7", Name = "umlaut", Code = "ÄÖ-7", Tag = "big" });
            session.Store(new Company { Id = "companies/lower-1", Name = "lower", Code = "lower-1", Tag = "small" });
            session.Store(new Company { Id = "companies/Mixed-Case-3", Name = "mixed", Code = "Mixed-Case-3", Tag = "small" });

            session.Store(new Order { Id = "orders/1", Name = "one", CompanyId = "companies/8213-A", Code = "8213-A", Codes = ["A-1", "b-2"] });
            session.Store(new Order { Id = "orders/2", Name = "two", CompanyId = "companies/ÄÖ-7", Code = "ÄÖ-7", Codes = ["a-1"] });
            session.Store(new Order { Id = "orders/3", Name = "three", CompanyId = "companies/lower-1", Code = "lower-1" });
            session.Store(new Order { Id = "orders/4", Name = "four", CompanyId = "companies/8213-A", Code = "8213-A", Codes = [] });
            session.Store(new Order { Id = "orders/5", Name = "five", CompanyId = null, Code = null });
            session.Store(new Order { Id = "orders/6", Name = "six", CompanyId = "COMPANIES/mixed-case-3", Code = "Mixed-Case-3", Codes = ["C-3"] });
            session.Store(new Order { Id = "orders/7", Name = "seven" });
            session.SaveChanges();
        }

        new CompaniesPlain().Execute(store);
        new CompaniesKeyCode().Execute(store);
        new OrdersPlain().Execute(store);
        new OrdersKey().Execute(store);
        Indexes.WaitForIndexing(store);
        return store;
    }

    private static string[] Names(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<Order>($"{rql} {Everything}").ToList().Select(x => x.Name).OrderBy(x => x).ToArray();
    }

    private static string[] Terms(IDocumentStore store, string index, string field)
        => store.Maintenance.Send(new GetTermsOperation(index, field, null, 1000)).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static IndexDefinition Definition(string field, IndexFieldOptions options, string name = "Orders/Invalid")
        => new()
        {
            Name = name,
            Maps = { "from o in docs.Orders select new { o.CompanyId, o.Code }" },
            Fields = { [field] = options }
        };

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Indexes)]
    public void KeyFieldHoldsOneLowercasedTermPerValue()
    {
        using var store = Seed();

        Assert.Equal(["companies/8213-a", "companies/lower-1", "companies/mixed-case-3", "companies/äö-7"], Terms(store, "OrdersKey", "CompanyId").Where(x => x != "NULL_VALUE").ToArray());
        Assert.Equal(["a-1", "b-2", "c-3"], Terms(store, "OrdersKey", "Codes").Where(x => x != "NULL_VALUE" && x != "EMPTY_STRING").ToArray());

        // the same terms as the default indexing
        Assert.Equal(Terms(store, "OrdersPlain", "CompanyId"), Terms(store, "OrdersKey", "CompanyId"));
        Assert.Equal(Terms(store, "OrdersPlain", "Code"), Terms(store, "OrdersKey", "Code"));
        Assert.Equal(Terms(store, "OrdersPlain", "Codes"), Terms(store, "OrdersKey", "Codes"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void KeyFieldIsSearchedCaseInsensitively()
    {
        using var store = Seed();

        Assert.Equal(["four", "one"], Names(store, "from index 'OrdersKey' where CompanyId = 'COMPANIES/8213-A'"));
        Assert.Equal(["two"], Names(store, "from index 'OrdersKey' where CompanyId = 'companies/äö-7'"));
        Assert.Equal(["two"], Names(store, "from index 'OrdersKey' where CompanyId = 'COMPANIES/ÄÖ-7'"));
        Assert.Equal(["one", "two"], Names(store, "from index 'OrdersKey' where Codes = 'A-1'"));
        Assert.Equal(["one", "six", "two"], Names(store, "from index 'OrdersKey' where Codes in ('a-1', 'B-2', 'c-3')"));
        Assert.Equal(["five", "seven"], Names(store, "from index 'OrdersKey' where CompanyId = null"));
        Assert.Equal(Names(store, "from index 'OrdersPlain' where CompanyId = 'COMPANIES/8213-A' or Codes = 'B-2' or CompanyId = null"),
            Names(store, "from index 'OrdersKey' where CompanyId = 'COMPANIES/8213-A' or Codes = 'B-2' or CompanyId = null"));

        using var session = store.OpenSession();
        Assert.Equal(["four", "one"], session.Query<Order, OrdersKey>().Where(x => x.CompanyId == "COMPANIES/8213-A").ToList().Select(x => x.Name).OrderBy(x => x));
        Assert.Equal(["four", "one"], session.Advanced.DocumentQuery<Order, OrdersKey>().WhereEquals(x => x.CompanyId, "Companies/8213-a").ToList().Select(x => x.Name).OrderBy(x => x));
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Indexes)]
    [InlineData("Analyzer")]
    [InlineData("Suggestions")]
    [InlineData("TermVector")]
    [InlineData("Spatial")]
    [InlineData("Default")]
    [InlineData("Lucene")]
    public void KeyFieldWithOtherOptionsIsRejectedWhenTheIndexIsCreated(string case_)
    {
        using var store = GetDocumentStore(CoraxOptions());

        var options = new IndexFieldOptions { Indexing = FieldIndexing.Key };
        var field = "CompanyId";
        string expected;
        const string prefix = "The field 'CompanyId' of the index 'Orders/Invalid' is indexed with FieldIndexing.Key";
        var definition = Definition(field, options);
        switch (case_)
        {
            case "Analyzer":
                options.Analyzer = "StandardAnalyzer";
                expected = $"{prefix}, so it cannot have the analyzer 'StandardAnalyzer': a Key field is always one lowercased term. Remove the analyzer, or index the field with FieldIndexing.Search.";
                break;
            case "Suggestions":
                options.Suggestions = true;
                expected = $"{prefix}, so it cannot have suggestions.";
                break;
            case "TermVector":
                options.TermVector = FieldTermVector.WithPositionsAndOffsets;
                expected = $"{prefix}, so it cannot have the term vector 'WithPositionsAndOffsets'.";
                break;
            case "Spatial":
                options.Spatial = new SpatialOptions();
                expected = $"{prefix}, so it cannot have spatial options.";
                break;
            case "Default":
                definition = Definition(Raven.Client.Constants.Documents.Indexing.Fields.AllFields, options);
                expected = "The field '__all_fields' of the index 'Orders/Invalid' is indexed with FieldIndexing.Key, which is a setting of a single field and cannot be the default of all the fields.";
                break;
            default:
                definition.Configuration[RavenConfiguration.GetKey(x => x.Indexing.StaticIndexingEngineType)] = nameof(SearchEngineType.Lucene);
                expected = $"{prefix}, which is supported only by the Corax search engine, but this index uses 'Lucene'.";
                break;
        }

        var e = Assert.Throws<IndexInvalidException>(() => store.Maintenance.Send(new PutIndexesOperation(definition)));
        output.WriteLine(e.Message);
        Assert.Contains(expected, e.Message);
        Assert.Empty(store.Maintenance.Send(new GetIndexNamesOperation(0, 10)).Where(x => x == "Orders/Invalid"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Indexes)]
    public void KeyFieldMayBeStored()
    {
        using var store = GetDocumentStore(CoraxOptions());
        store.Maintenance.Send(new PutIndexesOperation(Definition("CompanyId", new IndexFieldOptions { Indexing = FieldIndexing.Key, Storage = FieldStorage.Yes }, "Orders/Stored")));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void IdJoinOnKeyFieldEqualsTheJoinOnADefaultField()
    {
        using var store = Seed();

        const string companies = "(from index 'CompaniesPlain' where Tag = '{0}') as c";
        foreach (var tag in new[] { "big", "small" })
        {
            var expected = Names(store, $"from index 'OrdersPlain' as o join {string.Format(companies, tag)} on o.CompanyId = id(c)");
            Assert.NotEmpty(expected);
            Assert.Equal(expected, Names(store, $"from index 'OrdersKey' as o join {string.Format(companies, tag)} on o.CompanyId = id(c)"));
            Assert.Equal(expected, Names(store, $"from index 'OrdersKey' as o join {string.Format(companies, tag)} on o.CompanyId = id(c) order by score()"));
        }

        Assert.Equal(["four", "one", "two"], Names(store, $"from index 'OrdersKey' as o join {string.Format(companies, "big")} on o.CompanyId = id(c)"));
        Assert.Equal(["six", "three"], Names(store, $"from index 'OrdersKey' as o join {string.Format(companies, "small")} on o.CompanyId = id(c)"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void FieldJoinsOfKeyFieldsMatch()
    {
        using var store = Seed();

        var expected = Names(store, "from index 'OrdersPlain' as o join (from index 'CompaniesPlain' where Tag = 'big') as c on o.Code = c.Code");
        Assert.Equal(["four", "one", "two"], expected);

        // Key and Key
        Assert.Equal(expected, Names(store, "from index 'OrdersKey' as o join (from index 'CompaniesKeyCode' where Tag = 'big') as c on o.Code = c.Code"));
        // Key outer, default-indexed joined field
        Assert.Equal(expected, Names(store, "from index 'OrdersKey' as o join (from index 'CompaniesPlain' where Tag = 'big') as c on o.Code = c.Code"));
        // default outer, Key joined field
        Assert.Equal(expected, Names(store, "from index 'OrdersPlain' as o join (from index 'CompaniesKeyCode' where Tag = 'big') as c on o.Code = c.Code"));
        Assert.Equal(["six", "three"], Names(store, "from index 'OrdersKey' as o join (from index 'CompaniesKeyCode' where Tag = 'small') as c on o.Code = c.Code"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Indexes)]
    public void ChangingTheIndexingOfAFieldToKeyReplacesTheIndex()
    {
        using var store = Seed();

        var before = store.Maintenance.Send(new GetIndexOperation("OrdersPlain"));
        var created = store.Maintenance.Send(new GetIndexStatisticsOperation("OrdersPlain")).CreatedTimestamp;

        var changed = new OrdersPlain().CreateIndexDefinition();
        changed.Name = "OrdersPlain";
        changed.Fields["CompanyId"] = new IndexFieldOptions { Indexing = FieldIndexing.Key };
        Assert.False(before.Equals(changed));
        store.Maintenance.Send(new PutIndexesOperation(changed));
        Indexes.WaitForIndexing(store);

        var after = store.Maintenance.Send(new GetIndexOperation("OrdersPlain"));
        Assert.Equal(FieldIndexing.Key, after.Fields["CompanyId"].Indexing);
        Assert.NotEqual(created, store.Maintenance.Send(new GetIndexStatisticsOperation("OrdersPlain")).CreatedTimestamp);
        Assert.Equal(["four", "one"], Names(store, "from index 'OrdersPlain' where CompanyId = 'COMPANIES/8213-A'"));

        // the same definition again changes nothing
        var again = store.Maintenance.Send(new GetIndexStatisticsOperation("OrdersPlain")).CreatedTimestamp;
        store.Maintenance.Send(new PutIndexesOperation(changed));
        Assert.Equal(again, store.Maintenance.Send(new GetIndexStatisticsOperation("OrdersPlain")).CreatedTimestamp);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Indexes)]
    public void KeyRoundTripsThroughTheDefinition()
    {
        using var store = GetDocumentStore(CoraxOptions());

        var definition = new OrdersKey().CreateIndexDefinition();
        Assert.Equal(FieldIndexing.Key, definition.Fields["CompanyId"].Indexing);
        Assert.Equal(FieldIndexing.Key, definition.Fields["Codes"].Indexing);

        new OrdersKey().Execute(store);
        var stored = store.Maintenance.Send(new GetIndexOperation("OrdersKey"));
        Assert.Equal(FieldIndexing.Key, stored.Fields["CompanyId"].Indexing);
        Assert.True(definition.Equals(stored));
    }
}
