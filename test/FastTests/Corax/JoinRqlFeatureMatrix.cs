using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;
using FastTests.Server.Documents.Indexing.Joins.Compat;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;

namespace FastTests.Corax;

// Every RQL clause and feature combined with a JOIN. Each case runs the same RQL twice
public class JoinRqlFeatureMatrix(ITestOutputHelper output, SharedJoinDatabase shared) : RavenTestBase(output), IClassFixture<SharedJoinDatabase>
{
    private const string Everything = "limit 0, 1000000";
    private const string Join = "join (from index 'Users' where search(Name, 'chris')) as u on g.OwnerId = id(u)";
    private const string Owners = "g.OwnerId in ('users/strong', 'users/weak')";

    internal enum Mode
    {
        Ordered, // same rows in the same order
        Unordered, // same rows
        Count // same number of rows (random order, ties)
    }

    internal sealed record Case(string Name, string Where = null, string Order = null, string Load = null, string Filter = null, string Select = null,
        string Limit = Everything, string Include = null, Mode Mode = Mode.Unordered, bool Entities = true, string Declare = null, bool Unsupported = false, string GroupBy = null, string JoinRejects = null);


    internal static readonly Case[] WhereCases =
    [
        new("equals", "g.Rank = 4"),
        new("not equals", "g.Rank != 4"),
        new("greater", "g.Rank > 3"),
        new("greater or equal", "g.Rank >= 3"),
        new("less", "g.Rank < 8"),
        new("less or equal", "g.Rank <= 8"),
        new("between", "g.Rank between 2 and 8"),
        new("in", "g.Rank in (1, 2, 3, 9)"),
        new("all in", "g.Rank all in (1)"),
        new("and", "g.Rank > 2 and g.Rank < 9"),
        new("or", "g.Rank < 2 or g.Rank > 9"),
        new("not", "g.Rank >= 0 and not g.Rank = 4"),
        new("grouping", "(g.Rank < 3 or g.Rank > 8) and g.Country != 'nl'"),
        new("string equals", "g.Country = 'nl'"),
        new("null equals", "g.Country = null"),
        new("null not equals", "g.Country != null"),
        new("exists", "exists(g.Country)"),
        new("not exists", "g.Rank >= 0 and not exists(g.Country)"),
        new("search", "search(g.Name, 'zelda')"),
        new("search several terms", "search(g.Name, 'zelda mario')"),
        new("search and", "search(g.Name, 'zelda mario', and)"),
        new("search two fields", "search(g.Name, 'zelda') or search(g.Country, 'nl')"),
        new("startsWith", "startsWith(g.Name, 'zel')"),
        new("endsWith", "endsWith(g.Name, 'kart')"),
        new("regex", "regex(g.Name, '^mario')"),
        new("boost", "boost(g.Rank > 3, 2)"),
        new("fuzzy", "fuzzy(g.Name = 'zelde', 0.7)", Unsupported: true),
        new("proximity", "proximity(search(g.Name, 'zelda breath'), 2)", Unsupported: true),
        new("intersect", "intersect(g.Rank > 2, g.Country = 'nl')", Unsupported: true),
        new("id equals", "id() = 'games/004'"),
        new("id in", "id() in ('games/000', 'games/001', 'games/002', 'games/003')"),
        new("parameterless true", "g.Rank >= 0"),
        new("spatial within circle", "spatial.within(g.Location, spatial.circle(30, 52.5, 5))"),
        new("spatial within wkt", "spatial.within(g.Location, spatial.wkt('POLYGON((4 52.3, 6 52.3, 6 53.5, 4 53.5, 4 52.3))'))"),
        new("spatial and field", "spatial.within(g.Location, spatial.circle(30, 52.5, 5)) and g.Rank > 4"),
        new("more like this", "morelikethis(id() = 'games/000', '{\"Fields\":[\"Name\"],\"MinimumTermFrequency\":1,\"MinimumDocumentFrequency\":1}')"),
        new("empty result", "g.Rank > 1000"),
    ];

    internal static readonly Case[] OrderCases =
    [
        new("long", Order: "order by g.Rank as long", Mode: Mode.Ordered),
        new("long desc", Order: "order by g.Rank as long desc", Mode: Mode.Ordered),
        new("default type", Order: "order by g.Rank", Mode: Mode.Ordered),
        new("string", Order: "order by g.Name, g.Rank as long", Mode: Mode.Ordered),
        new("string desc", Order: "order by g.Name desc, g.Rank as long desc", Mode: Mode.Ordered),
        new("two keys", Order: "order by g.Country, g.Rank as long desc", Mode: Mode.Ordered),
        new("alphanumeric", Order: "order by g.Name as alphanumeric, g.Rank as long", Mode: Mode.Ordered),
        new("double", Order: "order by g.Rank as double desc", Mode: Mode.Ordered),
        new("id", Order: "order by id()", Mode: Mode.Ordered),
        new("id desc", Order: "order by id() desc", Mode: Mode.Ordered),
        new("random", Order: "order by random()", Mode: Mode.Count),
        new("random seed", Order: "order by random('seed')", Mode: Mode.Count),
        new("score", Where: "search(g.Name, 'zelda')", Order: "order by score()", Mode: Mode.Unordered),
        new("score desc", Where: "search(g.Name, 'zelda')", Order: "order by score() desc", Mode: Mode.Unordered),
        new("score then field", Where: "search(g.Name, 'zelda')", Order: "order by score(), g.Rank as long", Mode: Mode.Unordered),
        new("field then score", Where: "search(g.Name, 'zelda')", Order: "order by g.Country, score()", Mode: Mode.Unordered),
        new("where and order", "g.Rank > 3", "order by g.Rank as long desc", Mode: Mode.Ordered),
        new("spatial distance", Order: "order by spatial.distance(Location, spatial.point(52.45, 5)), g.Rank as long", Mode: Mode.Ordered),
        new("spatial distance desc", Order: "order by spatial.distance(Location, spatial.point(52.45, 5)) desc, g.Rank as long", Mode: Mode.Ordered),
        new("spatial within and distance", "spatial.within(g.Location, spatial.circle(30, 52.5, 5))", "order by spatial.distance(Location, spatial.point(52.45, 5)), g.Rank as long", Mode: Mode.Ordered),
    ];

    internal static readonly Case[] ProjectionCases =
    [
        new("one field", Order: "order by g.Rank as long", Select: "select g.Name", Mode: Mode.Ordered, Entities: false),
        new("fields", Order: "order by g.Rank as long", Select: "select g.Name, g.Rank, g.Country", Mode: Mode.Ordered, Entities: false),
        new("alias", Order: "order by g.Rank as long", Select: "select g.Name as GameName, g.Rank as Position", Mode: Mode.Ordered, Entities: false),
        new("id function", Order: "order by g.Rank as long", Select: "select id() as DocId, g.Rank", Mode: Mode.Ordered, Entities: false),
        new("distinct", Order: "order by g.Country", Select: "select distinct g.Country", Mode: Mode.Unordered, Entities: false),
        new("distinct two", Select: "select distinct g.Name, g.Country", Mode: Mode.Unordered, Entities: false),
        new("json object", Order: "order by g.Rank as long", Select: "select { Title: g.Name, Place: g.Rank }", Mode: Mode.Ordered, Entities: false),
        new("declared function", Order: "order by g.Rank as long", Declare: "declare function shout(x) { return x.Name + '!'; }", Select: "select shout(g) as Shout", Mode: Mode.Ordered, Entities: false),
        new("declared function object", Order: "order by g.Rank as long", Declare: "declare function pick(x) { return { Title: x.Name, Place: x.Rank }; }", Select: "select pick(g)", Mode: Mode.Ordered, Entities: false),
        new("load then select", Order: "order by g.Rank as long", Load: "load g.PublisherId as p", Select: "select g.Name, p.Name as Publisher", Mode: Mode.Ordered,
            Entities: false),
        new("load alone", Order: "order by g.Rank as long", Load: "load g.PublisherId as p", Select: "select p.Name as Publisher", Mode: Mode.Ordered,
            Entities: false),
        new Case("group by (not allowed on an index, with or without the join)", Select: "select g.Country, count() as Count", Mode: Mode.Unordered, Entities: false) with { Unsupported = true, Order = null, GroupBy = "group by g.Country" },
        new Case("group by (not allowed on an index, with or without the join) two keys", Select: "select g.Country, g.Name, count() as Count", Mode: Mode.Unordered, Entities: false) with { Unsupported = true, GroupBy = "group by g.Country, g.Name" },
        new Case("group by (not allowed on an index, with or without the join) sum", Select: "select g.Country, count() as Count, sum(g.Rank) as Total", Mode: Mode.Unordered, Entities: false) with { Unsupported = true, GroupBy = "group by g.Country" },
        new Case("group by (not allowed on an index, with or without the join) where", "g.Rank > 2", Select: "select g.Country, count() as Count", Mode: Mode.Unordered, Entities: false) with { Unsupported = true, GroupBy = "group by g.Country" },
        new Case("group by (not allowed on an index, with or without the join) order", Select: "select g.Country, count() as Count", Mode: Mode.Ordered, Entities: false) with { Unsupported = true, GroupBy = "group by g.Country", Order = "order by Count desc, g.Country" },
        new("filter", Order: "order by g.Rank as long", Filter: "filter g.Rank > 4", Mode: Mode.Ordered),
        new("filter and select", Order: "order by g.Rank as long", Filter: "filter g.Name = 'mario kart'", Select: "select g.Name, g.Rank", Mode: Mode.Ordered,
            Entities: false),
        new("where filter", "g.Rank > 1", "order by g.Rank as long", Filter: "filter g.Rank < 9", Mode: Mode.Ordered),
    ];

    internal static readonly Case[] PagingCases =
    [
        new("limit", Order: "order by g.Rank as long", Limit: "limit 3", Mode: Mode.Ordered),
        new("limit offset", Order: "order by g.Rank as long", Limit: "limit 2, 3", Mode: Mode.Ordered),
        new("offset past the end", Order: "order by g.Rank as long", Limit: "limit 100, 3", Mode: Mode.Ordered),
        new("limit 1", Order: "order by g.Rank as long desc", Limit: "limit 0, 1", Mode: Mode.Ordered),
        new("keywords", Order: "order by g.Rank as long", Limit: "limit 3 offset 2", Mode: Mode.Ordered),
        new("where limit", "g.Rank > 2", "order by g.Rank as long", Limit: "limit 1, 2", Mode: Mode.Ordered),
    ];


    internal static readonly Case[] IncludeCases =
    [
        new("include owner", Order: "order by g.Rank as long", Include: "include g.OwnerId", Mode: Mode.Ordered),
        new("include publisher", Order: "order by g.Rank as long", Include: "include g.PublisherId", Mode: Mode.Ordered),
        new("include both", "g.Rank > 2", "order by g.Rank as long", Include: "include g.OwnerId, g.PublisherId", Mode: Mode.Ordered),
        new("include and select", Order: "order by g.Rank as long", Select: "select g.Name, g.OwnerId", Include: "include g.OwnerId", Mode: Mode.Ordered, Entities: false),
    ];

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData("where")]
    [InlineData("order")]
    [InlineData("projection")]
    [InlineData("paging")]
    [InlineData("include")]
    public void EveryFeatureGivesTheSameRowsWithAndWithoutTheJoin(string group)
    {
        using var store = SeedShared();
        var cases = group switch { "where" => WhereCases, "order" => OrderCases, "projection" => ProjectionCases, "include" => IncludeCases, _ => PagingCases };

        var report = new List<string>();
        foreach (var c in cases)
        {
            var outcome = Run(store, c);
            if (outcome != null)
                report.Add($"[{group}] {c.Name}: {outcome}");
        }

        Assert.True(report.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, report));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ParametersWorkInEveryPartOfAJoinedQuery()
    {
        using var store = SeedShared();

        List<string> Ids(string rql)
        {
            using var session = store.OpenSession();
            return session.Advanced.RawQuery<Game>(rql)
                .AddParameter("owner", "chris").AddParameter("min", 1L).AddParameter("country", "nl").AddParameter("skip", 1).AddParameter("take", 2)
                .ToList().Select(x => x.Id).ToList();
        }

        var joined = Ids("from index 'Games' as g join (from index 'Users' where search(Name, $owner)) as u on g.OwnerId = id(u) where g.Rank > $min and g.Country = $country order by g.Rank as long limit $skip, $take");
        var oracle = Ids($"from index 'Games' as g where g.Rank > $min and g.Country = $country and {Owners} order by g.Rank as long limit $skip, $take");
        Assert.NotEmpty(oracle);
        Assert.Equal(oracle, joined);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void CountersAndTimeSeriesIncludesAreLoadedWithTheJoinedRows()
    {
        using var store = CreateStore();
        using (var session = store.OpenSession())
        {
            session.CountersFor("games/000").Increment("likes", 5);
            session.CountersFor("games/001").Increment("likes", 7);
            session.TimeSeriesFor("games/000", "Plays").Append(DateTime.UtcNow.Date, 1.5);
            session.TimeSeriesFor("games/001", "Plays").Append(DateTime.UtcNow.Date, 2.5);
            session.SaveChanges();
        }

        // the requests the session needs afterwards, with and without the join
        int ExtraRequests(string from, string include, Action<IDocumentSession> read)
        {
            using var session = store.OpenSession();
            var games = session.Advanced.RawQuery<Game>($"{from} where g.Rank < 2 order by g.Rank as long {include}").ToList();
            Assert.Equal(new[] { "games/000", "games/001" }, games.Select(x => x.Id));
            var requests = session.Advanced.NumberOfRequests;
            read(session);
            return session.Advanced.NumberOfRequests - requests;
        }

        void ReadCounters(IDocumentSession session)
        {
            Assert.Equal(5, session.CountersFor("games/000").Get("likes"));
            Assert.Equal(7, session.CountersFor("games/001").Get("likes"));
        }

        void ReadTimeSeries(IDocumentSession session)
        {
            Assert.Single(session.TimeSeriesFor("games/000", "Plays").Get());
            Assert.Single(session.TimeSeriesFor("games/001", "Plays").Get());
        }

        const string plain = "from index 'Games' as g";
        var joined = $"from index 'Games' as g {Join}";

        Assert.Equal(0, ExtraRequests(plain, "include counters('likes')", ReadCounters));
        Assert.Equal(0, ExtraRequests(joined, "include counters('likes')", ReadCounters));
        // reading the whole series is a wider range than the included last(10)
        Assert.Equal(ExtraRequests(plain, "include timeseries('Plays', last(10))", ReadTimeSeries), ExtraRequests(joined, "include timeseries('Plays', last(10))", ReadTimeSeries));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void IncludedDocumentsAreLoadedWithTheRowsOfTheJoin()
    {
        using var store = SeedShared();
        JoinIndexServedRqlMatrixTests.AssertIncludesAreLoaded(store, Join);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void StreamPatchAndDeleteByQueryOnlyTouchTheDocumentsOfTheJoin()
    {
        var expected = new[] { "games/000", "games/001", "games/004", "games/005", "games/008", "games/009" }; // the games of users/strong and users/weak

        // one database for the three
        using var store = CreateStore();

        // streaming: the rows of the join
        {
            using var session = store.OpenSession();
            using var stream = session.Advanced.Stream(session.Advanced.RawQuery<Game>($"from index 'Games' as g {Join} order by g.Rank as long"));
            var ids = new List<string>();
            while (stream.MoveNext())
                ids.Add(stream.Current.Id);

            Assert.Equal(expected, ids);
        }

        // patch by query: only the documents of the join are patched
        {
            store.Operations.Send(new PatchByQueryOperation($"from index 'Games' as g {Join} update {{ this.Patched = true; }}")).WaitForCompletion();
            using var session = store.OpenSession();
            var patched = session.Advanced.RawQuery<Game>("from 'Games' where Patched = true").ToList().Select(x => x.Id).OrderBy(x => x).ToList();
            Assert.Equal(expected, patched);
        }

        // delete by query: only the documents of the join are deleted
        {
            store.Operations.Send(new DeleteByQueryOperation($"from index 'Games' as g {Join}")).WaitForCompletion();
            using var session = store.OpenSession();
            var left = session.Advanced.RawQuery<Game>("from 'Games'").ToList().Select(x => x.Id).OrderBy(x => x).ToList();
            Assert.Equal(Enumerable.Range(0, 12).Select(i => $"games/{i:D3}").Except(expected).ToList(), left);
        }
    }


    private string Run(IDocumentStore store, Case c)
    {
        string Rql(bool join)
        {
            string where;
            if (join)
                where = c.Where == null ? "" : $"where {c.Where}";
            else
                where = c.Where == null ? $"where {Owners}" : $"where ({c.Where}) and {Owners}";

            return string.Join(" ", new[] { c.Declare, "from index 'Games' as g", join ? Join : null, c.GroupBy, where, c.Order, c.Load, c.Filter, c.Select, c.Limit, c.Include }
                .Where(x => string.IsNullOrEmpty(x) == false));
        }

        if (c.Unsupported)
        {
            Exception oracleError = null, joinError = null;
            try { Execute(store, Rql(false), c.Entities); } catch (Exception e) { oracleError = e; }
            try { Execute(store, Rql(true), c.Entities); } catch (Exception e) { joinError = e; }
            if (oracleError == null)
                return "EXPECTED THE FEATURE TO BE UNSUPPORTED, but the query without the join worked: " + Rql(false);
            if (joinError == null || joinError.GetType() != oracleError.GetType())
                return $"the join gives {joinError?.GetType().Name ?? "no error"} where the query without it gives {oracleError.GetType().Name}: {Rql(true)}";
            return null;
        }

        if (c.JoinRejects != null)
        {
            try { Execute(store, Rql(false), c.Entities); } catch (Exception e) { return "THE ORACLE (no join) THROWS: " + First(e); }
            try
            {
                Execute(store, Rql(true), c.Entities);
                return "EXPECTED THE JOIN TO BE REJECTED, but it worked: " + Rql(true);
            }
            catch (Exception e)
            {
                return e.Message.Contains("JOIN", StringComparison.Ordinal) && e.Message.Contains(c.JoinRejects, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : $"the rejection of the join does not say why ({c.JoinRejects}): {First(e)}";
            }
        }

        (List<string> Keys, long Total) expected;
        try
        {
            expected = Execute(store, Rql(false), c.Entities);
        }
        catch (Exception e)
        {
            return "THE ORACLE (no join) THROWS: " + First(e) + " in " + Rql(false);
        }

        (List<string> Keys, long Total) actual;
        try
        {
            actual = Execute(store, Rql(true), c.Entities);
        }
        catch (Exception e)
        {
            return "THE JOIN THROWS: " + First(e) + " in " + Rql(true);
        }

        if (c.Mode == Mode.Count)
        {
            return actual.Keys.Count == expected.Keys.Count && actual.Total == expected.Total
                ? null
                : $"count {actual.Keys.Count}/{actual.Total} instead of {expected.Keys.Count}/{expected.Total} in {Rql(true)}";
        }

        var a = c.Mode == Mode.Ordered ? actual.Keys : actual.Keys.OrderBy(x => x).ToList();
        var e2 = c.Mode == Mode.Ordered ? expected.Keys : expected.Keys.OrderBy(x => x).ToList();
        if (a.SequenceEqual(e2) == false)
            return $"ROWS DIFFER in {Rql(true)}{Environment.NewLine}   join:   {string.Join(" ; ", a)}{Environment.NewLine}   oracle: {string.Join(" ; ", e2)}";

        if (actual.Total != expected.Total)
            return $"total {actual.Total} instead of {expected.Total} in {Rql(true)}";

        return null;
    }

    private static string First(Exception e) => e.Message.Split('\n')[0].Trim();

    private static (List<string> Keys, long Total) Execute(IDocumentStore store, string rql, bool entities)
    {
        using var session = store.OpenSession();
        if (entities)
        {
            var games = session.Advanced.RawQuery<Game>(rql).Statistics(out var stats).ToList();
            return (games.Select(x => (string)session.Advanced.GetMetadataFor(x)[Constants.Documents.Metadata.Id]).ToList(), stats.TotalResults);
        }

        var rows = session.Advanced.RawQuery<Projected>(rql).Statistics(out var projectionStats).ToList();
        return (rows.Select(x => $"{x.Name}|{x.Rank}|{x.Country}|{x.GameName}|{x.Position}|{x.DocId}|{x.Title}|{x.Place}|{x.Shout}|{x.Publisher}|{x.Count}|{x.Total}").ToList(), projectionStats.TotalResults);
    }


    // the tests that only read share one database (see SharedJoinDatabase); the ones that write have their own
    private IDocumentStore SeedShared() => shared.Get("seed", CoraxOptions(), o => GetDocumentStore(o), Populate);

    private IDocumentStore CreateStore()
    {
        var store = GetDocumentStore(CoraxOptions());
        Populate(store);
        return store;
    }

    private void Populate(IDocumentStore store)
    {
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);

        using var session = store.OpenSession();
        session.Store(new User { Id = "users/strong", Name = "chris bob", Number = 1 });
        session.Store(new User { Id = "users/weak", Name = "chris", Number = 2 });
        session.Store(new User { Id = "users/other", Name = "alice", Number = 3 });
        session.Store(new Publisher { Id = "publishers/1", Name = "acme" });
        session.Store(new Publisher { Id = "publishers/2", Name = "globex" });

        var names = new[] { "zelda adventure", "mario kart", "zelda breath" };
        var owners = new[] { "users/strong", "users/weak", "users/other", "users/missing" };
        for (var i = 0; i < 12; i++)
        {
            session.Store(new Game
            {
                Id = $"games/{i:D3}",
                Name = names[i % names.Length],
                OwnerId = owners[i % owners.Length],
                PublisherId = i % 2 == 0 ? "publishers/1" : "publishers/2",
                Country = i % 3 == 0 ? null : i % 2 == 0 ? "nl" : "be",
                Rank = i,
                Latitude = 52 + i * 0.1,
                Longitude = 5
            });
        }

        session.SaveChanges();
        Indexes.WaitForIndexing(store);
    }

    private class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public long Number { get; set; }
    }

    private class Publisher
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    private class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string PublisherId { get; set; }
        public string Country { get; set; }
        public long Rank { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    private class Projected
    {
        public string Name { get; set; }
        public long? Rank { get; set; }
        public string Country { get; set; }
        public string GameName { get; set; }
        public long? Position { get; set; }
        public string DocId { get; set; }
        public string Title { get; set; }
        public long? Place { get; set; }
        public string Shout { get; set; }
        public string Publisher { get; set; }
        public long? Count { get; set; }
        public long? Total { get; set; }
    }

    private class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Number };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    private class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.PublisherId, g.Country, g.Rank, Location = CreateSpatialField(g.Latitude, g.Longitude) };
            Index(x => x.Name, FieldIndexing.Search);
            Index(x => x.Country, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
