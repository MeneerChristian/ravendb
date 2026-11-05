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
using static FastTests.Corax.JoinTestHelpers;

namespace FastTests.Corax;

// A JOIN on ids keeps the ids as the joined index encoded them when the outer index has a dictionary with the same table, and the outer field holds them unchanged:
// the rows and the scores are the ones of the path that decodes and encodes the ids
public class JoinEncodedKeysTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Everything = "limit 0, 1000000";
    private const int Companies = 3_000;
    private const int Orders = 6_000;

    public enum Tables
    {
        // both indexes are created on an empty database: both get the default dictionary
        Equal,

        // both indexes are created after the data: each trains a dictionary on its own terms
        Different
    }

    public enum Ids
    {
        Short,
        Guid
    }

    private sealed class Company
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Tag { get; set; }
        public long Rank { get; set; }
    }

    private sealed class Order
    {
        public string Id { get; set; }
        public string CompanyId { get; set; }
        public string Name { get; set; }
        public string Country { get; set; }
    }

    private sealed class CompaniesIndex : AbstractIndexCreationTask<Company>
    {
        public CompaniesIndex() => Map = companies => from c in companies select new { c.Name, c.Tag, c.Rank };
        public override string IndexName => "Companies";
    }

    private sealed class OrdersKeyIndex : AbstractIndexCreationTask<Order>
    {
        public OrdersKeyIndex()
        {
            Map = orders => from o in orders select new { o.CompanyId, o.Name, o.Country };
            Index(x => x.CompanyId, FieldIndexing.Key);
        }

        public override string IndexName => "OrdersKey";
    }

    private sealed class OrdersPlainIndex : AbstractIndexCreationTask<Order>
    {
        public OrdersPlainIndex() => Map = orders => from o in orders select new { o.CompanyId, o.Name, o.Country };
        public override string IndexName => "OrdersPlain";
    }

    private static string CompanyId(int c, Ids ids)
    {
        // the case and the characters of the ids are ones that a lowercasing analyzer changes
        var suffix = c % 17 == 0 ? $"ÄÖ-{c}" : c % 5 == 0 ? $"Mixed-{c}-A" : $"{c}-a";
        if (ids == Ids.Short)
            return $"Companies/{suffix}";

        var h = (uint)c * 2654435761u;
        return $"Companies/{new Guid((int)h, (short)(h >> 7), (short)(h >> 3), (byte)c, (byte)(c >> 8), (byte)(c >> 16), 4, 5, 6, 7, 8)}".ToUpperInvariant();
    }

    private IDocumentStore Seed(Tables tables, Ids ids)
    {
        var store = GetDocumentStore(CoraxOptions());
        if (tables == Tables.Equal)
            CreateIndexes(store);

        var random = new Random(31);
        using (var bulk = store.BulkInsert())
        {
            for (var c = 0; c < Companies; c++)
                bulk.Store(new Company { Name = c % 10 == 0 ? "tenth" : "other", Tag = c % 3 == 0 ? "big" : "small", Rank = c % 50 }, CompanyId(c, ids));

            for (var o = 0; o < Orders; o++)
            {
                var roll = random.Next(100);
                var company = roll switch
                {
                    < 8 => null,
                    < 25 => $"missing/{random.Next(500)}",
                    < 35 => CompanyId(random.Next(Companies), ids).ToLowerInvariant(),
                    _ => CompanyId(random.Next(Companies), ids)
                };
                bulk.Store(new Order { CompanyId = company, Name = o % 7 == 0 ? "x" : "y", Country = $"c{o % 6}" }, $"orders/{o}");
            }
        }

        if (tables == Tables.Different)
            CreateIndexes(store);

        Indexes.WaitForIndexing(store);
        return store;
    }

    private static void CreateIndexes(IDocumentStore store)
    {
        new CompaniesIndex().Execute(store);
        new OrdersKeyIndex().Execute(store);
        new OrdersPlainIndex().Execute(store);
    }

    private sealed class Row
    {
        public string Name { get; set; }
        public string CompanyName { get; set; }
    }

    private sealed record Outcome(List<(string Id, double Score)> Rows, long Total, long KeysEncoded);

    private static Outcome Run(IDocumentStore store, string rql, bool encoded)
    {
        var project = rql.Contains(" select ");
        JoinEncodedKeys.Enabled = encoded;
        try
        {
            using var session = store.OpenSession();
            List<(string Id, double Score)> rows;
            QueryStatistics stats;
            QueryTimings timings;

            // a projection has no score: it is compared by its values, in the order of the query
            if (project)
            {
                rows = session.Advanced.RawQuery<Row>(rql + " include timings()").NoCaching().Statistics(out stats).Timings(out timings).ToList()
                    .Select(x => ($"{x.Name}|{x.CompanyName}", 0d)).ToList();
            }
            else
            {
                rows = session.Advanced.RawQuery<Order>(rql + " include timings()").NoCaching().Statistics(out stats).Timings(out timings).ToList()
                    .Select(x => (x.Id, Convert.ToDouble(session.Advanced.GetMetadataFor(x)["@index-score"]))).ToList();
            }

            long keysEncoded = 0;
            if (timings?.Timings != null && timings.Timings.TryGetValue("Join", out var join))
            {
                foreach (var scope in join.Timings.Values)
                {
                    if (scope.Counters != null && scope.Counters.TryGetValue("KeysEncoded", out var value))
                        keysEncoded = Math.Max(keysEncoded, value);
                }
            }

            return new Outcome(rows, stats.TotalResults, keysEncoded);
        }
        finally
        {
            JoinEncodedKeys.Enabled = true;
        }
    }

    // both paths return the same rows with the same scores, and the encoded path ran when expected
    private static void Verify(IDocumentStore store, string rql, bool expectEncoded)
    {
        var decoded = Run(store, rql, encoded: false);
        var encoded = Run(store, rql, encoded: true);
        Assert.True(decoded.KeysEncoded == 0, $"[{rql}] the ids were kept encoded while the path is switched off");
        Assert.True(expectEncoded ? encoded.KeysEncoded == 1 : encoded.KeysEncoded == 0, $"[{rql}] encoded keys {(expectEncoded ? "were expected" : "were not expected")}, counter {encoded.KeysEncoded}");
        Assert.True(decoded.Rows.Count > 0, $"[{rql}] matches nothing");
        Assert.Equal(decoded.Total, encoded.Total);
        Assert.Equal(decoded.Rows.Count, encoded.Rows.Count);
        for (var i = 0; i < decoded.Rows.Count; i++)
            Assert.True(decoded.Rows[i] == encoded.Rows[i], $"[{rql}] row {i}: {decoded.Rows[i]} against {encoded.Rows[i]}");
    }

    private const string Key = "from index 'OrdersKey' as o";
    private const string Plain = "from index 'OrdersPlain' as o";
    private const string Big = "join (from index 'Companies' where Tag = 'big') as c on o.CompanyId = id(c)";
    private const string All = "join (from index 'Companies') as c on o.CompanyId = id(c)";
    private const string Tenth = "join (from index 'Companies' where Name = 'tenth') as c on o.CompanyId = id(c)";

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(Tables.Equal, Ids.Short)]
    [InlineData(Tables.Equal, Ids.Guid)]
    [InlineData(Tables.Different, Ids.Short)]
    [InlineData(Tables.Different, Ids.Guid)]
    public void IdJoinOnAKeyFieldIsTheSameWithEncodedKeys(Tables tables, Ids ids)
    {
        using var store = Seed(tables, ids);
        var expectEncoded = tables == Tables.Equal;

        Verify(store, $"{Key} {Big} order by score() {Everything}", expectEncoded);
        Verify(store, $"{Key} {All} order by score() {Everything}", expectEncoded);
        Verify(store, $"{Key} {Tenth} order by score() {Everything}", expectEncoded);
        Verify(store, $"{Key} {Big} boost 2.5 order by score() {Everything}", expectEncoded);
        Verify(store, $"{Key} {Big} where o.Name = 'x' order by score() {Everything}", expectEncoded);
        Verify(store, $"{Key} left join (from index 'Companies' where Tag = 'big') as c on o.CompanyId = id(c) boost 0.5 order by score() {Everything}", expectEncoded);
        Verify(store, $"{Key} {Big} where matches(c) or o.Name = 'x' order by score() {Everything}", expectEncoded);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(Tables.Equal)]
    [InlineData(Tables.Different)]
    public void JoinsThatNeedTheDecodedIdsAreTheSame(Tables tables)
    {
        using var store = Seed(tables, Ids.Short);

        // the outer field is analyzed on its own: the ids are decoded
        Verify(store, $"{Plain} {Big} order by score() {Everything}", expectEncoded: false);
        // the joined index orders the rows, the ids are ranked
        Verify(store, $"{Key} {Big} order by c.Rank, score() {Everything}", expectEncoded: false);
        // the joined document of a row is looked up by its decoded id: the keys are decoded when it is first asked for
        Verify(store, $"{Key} {Big} select o.Name as Name, c.Name as CompanyName {Everything}", expectEncoded: tables == Tables.Equal);
        // a join on several fields
        Verify(store, $"{Key} join (from index 'OrdersKey' where Name = 'x') as c on o.CompanyId = c.CompanyId and o.Country = c.Country order by score() {Everything}", expectEncoded: false);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ChainOfJoinsIsTheSame()
    {
        using var store = Seed(Tables.Equal, Ids.Short);

        // the sub-query of the join joins documents of its own
        var rql = $"{Key} join (from index 'OrdersKey' as p join (from index 'Companies' where Tag = 'big') as d on p.CompanyId = id(d) where p.Name = 'x') as c on o.CompanyId = c.CompanyId order by score() {Everything}";
        var decoded = Run(store, rql, encoded: false);
        var encoded = Run(store, rql, encoded: true);
        Assert.Equal(decoded.Rows, encoded.Rows);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ChangedIdsAfterTheQueryAreSeen()
    {
        using var store = Seed(Tables.Equal, Ids.Short);
        var rql = $"{Key} {All} order by score() {Everything}";
        var before = Run(store, rql, encoded: true);

        using (var session = store.OpenSession())
        {
            session.Delete(CompanyId(1, Ids.Short));
            session.Store(new Company { Name = "new", Tag = "big", Rank = 1 }, "Companies/NEW-1");
            session.Store(new Order { CompanyId = "companies/new-1", Name = "n", Country = "c1" }, "orders/new");
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
        var decoded = Run(store, rql, encoded: false);
        var encoded = Run(store, rql, encoded: true);
        Assert.Equal(decoded.Rows, encoded.Rows);
        Assert.Contains(encoded.Rows, x => x.Id == "orders/new");
        Assert.NotEqual(before.Rows.Count, encoded.Rows.Count);
    }
}
