using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// LEFT JOIN and the BOOST of a join
public class JoinLeftTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Everything = "limit 0, 1000000";
    private const int Users = 300;
    private const int Depts = 100;
    private const int Games = 600;
    private static readonly string[] Countries = ["c0", "c1", "c2", "c3", "c4"];

    private const string Outer = "from index 'Games' as g";
    private const string OuterWhere = "where search(g.Name, 'g1 g4 common')";
    private const string UserSub = "from index 'Users' where search(Name, 'word3 word5')";
    private const string DeptSub = "from index 'Depts' where search(Name, 'dept4 dept9')";

    private sealed record Row(string Id, double Score);

    private static string GameOwner(int i) => i % 11 == 0 ? null : $"users/{i * 7 % 450}";
    private static string GameDept(int i) => $"depts/{i * 3 % Depts}";
    private static string GameCountry(int i) => Countries[i % Countries.Length];
    private static int GameNumber(string name) => int.Parse(name.Split(' ')[0].Substring("game".Length));
    private static int Number(Row row) => int.Parse(row.Id.Substring("games/".Length));
    private static string UserName(int i) => $"word{i % 40} word{i % 7} common";

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData("left join", "", 1.0)]
    [InlineData("left join", " boost 1", 1.0)]
    [InlineData("left join", " boost 0", 0.0)]
    [InlineData("left join", " boost 0.5", 0.5)]
    [InlineData("left join", " boost 2", 2.0)]
    [InlineData("join", " boost 2", 2.0)]
    [InlineData("join", " boost 0.5", 0.5)]
    [InlineData("join", " boost 0", 0.0)]
    [InlineData("join", "", 1.0)]
    public void IdJoinScoreIsOuterPlusBoostTimesInner(string join, string boost, double factor)
    {
        using var store = Seed();
        var isLeft = join == "left join";
        var outer = Fetch(store, $"{Outer} {OuterWhere} order by score() {Everything}");
        var inner = Fetch(store, $"{UserSub} {Everything}").ToDictionary(x => x.Id, x => x.Score);

        var rows = Fetch(store, $"{Outer} {join} ({UserSub}) as u on g.OwnerId = id(u){boost} {OuterWhere} order by score() {Everything}");

        var expected = outer.Select(o =>
        {
            var owner = GameOwner(Number(o));
            var matched = owner != null && inner.ContainsKey(owner);
            return (o.Id, Matched: matched, Score: o.Score + (matched ? factor * inner[owner] : 0));
        }).Where(x => isLeft || x.Matched).ToList();

        AssertRows(expected.Select(x => new Row(x.Id, x.Score)).ToList(), rows);
        Assert.Contains(expected, x => x.Matched);
        if (isLeft)
        {
            Assert.Contains(expected, x => x.Matched == false);
            Assert.Equal(outer.Count, rows.Count);
        }
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData("")]
    [InlineData(" boost 0")]
    [InlineData(" boost 3")]
    public void LeftJoinProjectsTheAliasExactlyWhenTheKeyMatched(string boost)
    {
        using var store = Seed();
        var inner = Fetch(store, $"{UserSub} {Everything}").Select(x => x.Id).ToHashSet();
        var plain = Fetch(store, $"{Outer} {OuterWhere} {Everything}").Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();

        foreach (var order in new[] { "order by score()", "" })
        {
            var rows = Project(store, $"{Outer} left join ({UserSub}) as u on g.OwnerId = id(u){boost} {OuterWhere} {order} select g.Name as GameName, u.Name as UserName {Everything}");
            Assert.Equal(plain, rows.Select(x => $"games/{GameNumber(x.GameName)}").OrderBy(x => x, StringComparer.Ordinal).ToList());

            foreach (var row in rows)
            {
                var owner = GameOwner(GameNumber(row.GameName));
                var matched = owner != null && inner.Contains(owner);
                Assert.Equal(matched, row.UserName != null);
                if (matched)
                    Assert.Equal(UserName(int.Parse(owner.Substring("users/".Length))), row.UserName);
            }

            Assert.Contains(rows, x => x.UserName != null);
            Assert.Contains(rows, x => x.UserName == null);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void LeftJoinWithoutScoresReturnsTheRowsOfThePlainQuery()
    {
        using var store = Seed();
        var plain = Fetch(store, $"{Outer} where g.Country = 'c1' {Everything}").Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var left = Fetch(store, $"{Outer} left join ({UserSub}) as u on g.OwnerId = id(u) where g.Country = 'c1' {Everything}").Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(plain, left);

        var all = Fetch(store, $"{Outer} left join ({UserSub}) as u on g.OwnerId = id(u) {Everything}").Select(x => x.Id).ToList();
        Assert.Equal(Games, all.Count);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void SubQueryThatMatchesNothingOrEverything()
    {
        using var store = Seed();
        var outer = Fetch(store, $"{Outer} {OuterWhere} order by score() {Everything}");

        var none = Fetch(store, $"{Outer} left join (from index 'Users' where Name = 'nothing') as u on g.OwnerId = id(u) {OuterWhere} order by score() {Everything}");
        AssertRows(outer, none);
        var projected = Project(store, $"{Outer} left join (from index 'Users' where Name = 'nothing') as u on g.OwnerId = id(u) {OuterWhere} select g.Name as GameName, u.Name as UserName {Everything}");
        Assert.Equal(outer.Count, projected.Count);
        Assert.All(projected, x => Assert.Null(x.UserName));

        var inner = Fetch(store, $"from index 'Users' where search(Name, 'common') {Everything}").ToDictionary(x => x.Id, x => x.Score);
        Assert.Equal(Users, inner.Count);
        var everything = Fetch(store, $"{Outer} left join (from index 'Users' where search(Name, 'common')) as u on g.OwnerId = id(u) {OuterWhere} order by score() {Everything}");
        AssertRows(outer.Select(o =>
        {
            var owner = GameOwner(Number(o));
            return new Row(o.Id, o.Score + (owner != null && inner.TryGetValue(owner, out var s) ? s : 0));
        }).ToList(), everything);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void PagingAndTotalsOfALeftJoin()
    {
        using var store = Seed();
        var all = Fetch(store, $"{Outer} left join ({UserSub}) as u on g.OwnerId = id(u) {OuterWhere} order by score() {Everything}");
        var plainCount = Fetch(store, $"{Outer} {OuterWhere} {Everything}").Count;
        Assert.Equal(plainCount, all.Count);

        using var session = store.OpenSession();
        var page = session.Advanced.RawQuery<Game>($"{Outer} left join ({UserSub}) as u on g.OwnerId = id(u) {OuterWhere} order by score() limit 20, 15").Statistics(out var stats).ToList();
        Assert.Equal(plainCount, stats.TotalResults);
        Assert.Equal(15, page.Count);
        var scores = page.Select(x => ScoreOf(session, x)).ToList();
        for (var i = 0; i < scores.Count; i++)
            Assert.True(Math.Abs(scores[i] - all[20 + i].Score) < 1e-3, $"page position {i}: {scores[i]} vs {all[20 + i].Score}");
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData("left join", "", 1.0)]
    [InlineData("left join", " boost 0.5", 0.5)]
    [InlineData("left join", " boost 0", 0.0)]
    [InlineData("join", "", 1.0)]
    [InlineData("join", " boost 2", 2.0)]
    public void FieldJoinScoreIsOuterPlusBoostTimesTheBestInnerScoreOfTheValue(string join, string boost, double factor)
    {
        using var store = Seed();
        var isLeft = join == "left join";
        var outer = Fetch(store, $"{Outer} {OuterWhere} order by score() {Everything}");

        var sub = "from index 'Users' where search(Name, 'word3 word5') and Country in ('c0', 'c1', 'c3')";
        var best = new Dictionary<string, double>();
        foreach (var u in Fetch(store, $"{sub} {Everything}"))
        {
            var country = Countries[int.Parse(u.Id.Substring("users/".Length)) % Countries.Length];
            best[country] = Math.Max(best.GetValueOrDefault(country), u.Score);
        }

        Assert.Equal(3, best.Count);

        var rows = Fetch(store, $"{Outer} {join} ({sub}) as u on g.Country = u.Country{boost} {OuterWhere} order by score() {Everything}");
        var expected = outer.Select(o =>
        {
            var matched = best.TryGetValue(GameCountry(Number(o)), out var s);
            return (o.Id, Matched: matched, Score: o.Score + (matched ? factor * s : 0));
        }).Where(x => isLeft || x.Matched).ToList();
        AssertRows(expected.Select(x => new Row(x.Id, x.Score)).ToList(), rows);

        var projected = Project(store, $"{Outer} {join} ({sub}) as u on g.Country = u.Country{boost} {OuterWhere} select g.Name as GameName, u.Name as UserName {Everything}");
        Assert.Equal(expected.Count, projected.Count);
        foreach (var row in projected)
            Assert.Equal(best.ContainsKey(GameCountry(GameNumber(row.GameName))), row.UserName != null);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TwoLeftJoinsAndALeftJoinWithInnerJoinsAndMatches()
    {
        using var store = Seed();
        var outer = Fetch(store, $"{Outer} {OuterWhere} order by score() {Everything}");
        var users = Fetch(store, $"{UserSub} {Everything}").ToDictionary(x => x.Id, x => x.Score);
        var depts = Fetch(store, $"{DeptSub} {Everything}").ToDictionary(x => x.Id, x => x.Score);
        double U(int i) => GameOwner(i) is { } o && users.TryGetValue(o, out var s) ? s : 0;
        double D(int i) => depts.TryGetValue(GameDept(i), out var s) ? s : 0;
        bool HasU(int i) => GameOwner(i) is { } o && users.ContainsKey(o);
        bool HasD(int i) => depts.ContainsKey(GameDept(i));

        var uJoin = $"({UserSub}) as u on g.OwnerId = id(u)";
        var dJoin = $"({DeptSub}) as d on g.DeptId = id(d)";
        Assert.Contains(outer, o => HasU(Number(o)) && HasD(Number(o)));

        // two left joins
        var rows = Fetch(store, $"{Outer} left join {uJoin} left join {dJoin} {OuterWhere} order by score() {Everything}");
        AssertRows(outer.Select(o => new Row(o.Id, o.Score + U(Number(o)) + D(Number(o)))).ToList(), rows);

        // left + inner, in both orders
        rows = Fetch(store, $"{Outer} left join {uJoin} join {dJoin} {OuterWhere} order by score() {Everything}");
        AssertRows(outer.Where(o => HasD(Number(o))).Select(o => new Row(o.Id, o.Score + U(Number(o)) + D(Number(o)))).ToList(), rows);
        rows = Fetch(store, $"{Outer} join {dJoin} left join {uJoin} {OuterWhere} order by score() {Everything}");
        AssertRows(outer.Where(o => HasD(Number(o))).Select(o => new Row(o.Id, o.Score + U(Number(o)) + D(Number(o)))).ToList(), rows);

        // a left join next to the matches() of another join: u stays optional, d is part of the boolean logic
        rows = Fetch(store, $"{Outer} left join {uJoin} join {dJoin} {OuterWhere} and (matches(d) or g.Country = 'c2') order by score() {Everything}");
        AssertRows(outer.Where(o => HasD(Number(o)) || GameCountry(Number(o)) == "c2").Select(o => new Row(o.Id, o.Score + U(Number(o)) + (HasD(Number(o)) ? D(Number(o)) : 0))).ToList(), rows);

        // matches(u) of a left joined alias makes it an ordinary referenced join: it filters
        rows = Fetch(store, $"{Outer} left join {uJoin} {OuterWhere} and matches(u) order by score() {Everything}");
        AssertRows(outer.Where(o => HasU(Number(o))).Select(o => new Row(o.Id, o.Score + U(Number(o)))).ToList(), rows);

        // matches(u) or matches(d) with boost 0 on d
        rows = Fetch(store, $"{Outer} join {uJoin} join ({DeptSub}) as d on g.DeptId = id(d) boost 0 {OuterWhere} and (matches(u) or matches(d)) order by score() {Everything}");
        AssertRows(outer.Where(o => HasU(Number(o)) || HasD(Number(o))).Select(o => new Row(o.Id, o.Score + (HasU(Number(o)) ? U(Number(o)) : 0))).ToList(), rows);
        Assert.Contains(outer, o => HasD(Number(o)) && HasU(Number(o)) == false);

        rows = Fetch(store, $"{Outer} join ({UserSub}) as u on g.OwnerId = id(u) boost 2 join ({DeptSub}) as d on g.DeptId = id(d) boost 0.5 {OuterWhere} and (matches(u) or matches(d)) order by score() {Everything}");
        AssertRows(outer.Where(o => HasU(Number(o)) || HasD(Number(o))).Select(o => new Row(o.Id, o.Score + (HasU(Number(o)) ? 2 * U(Number(o)) : 0) + (HasD(Number(o)) ? 0.5 * D(Number(o)) : 0))).ToList(), rows);

        // projection of two left joins
        var projected = Project(store, $"{Outer} left join {uJoin} left join {dJoin} {OuterWhere} select g.Name as GameName, u.Name as UserName, d.Name as DeptName {Everything}");
        Assert.Equal(outer.Count, projected.Count);
        foreach (var row in projected)
        {
            Assert.Equal(HasU(GameNumber(row.GameName)), row.UserName != null);
            Assert.Equal(HasD(GameNumber(row.GameName)), row.DeptName != null);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void BoostCanBeAParameter()
    {
        using var store = Seed();
        var outer = Fetch(store, $"{Outer} {OuterWhere} order by score() {Everything}");
        var inner = Fetch(store, $"{UserSub} {Everything}").ToDictionary(x => x.Id, x => x.Score);
        using (var session = store.OpenSession())
        {
            var query = session.Advanced.RawQuery<Game>($"{Outer} left join ({UserSub}) as u on g.OwnerId = id(u) boost $b {OuterWhere} order by score() {Everything}");
            query.AddParameter("b", 0.25);
            var rows = query.ToList().Select(x => new Row(x.Id, ScoreOf(session, x))).ToList();
            AssertRows(outer.Select(o =>
            {
                var owner = GameOwner(Number(o));
                return new Row(o.Id, o.Score + (owner != null && inner.TryGetValue(owner, out var s) ? 0.25 * s : 0));
            }).ToList(), rows);
        }

        foreach (var bad in new object[] { -1.0, "abc", null })
        {
            using var session = store.OpenSession();
            var query = session.Advanced.RawQuery<Game>($"{Outer} left join ({UserSub}) as u on g.OwnerId = id(u) boost $b {OuterWhere} {Everything}");
            query.AddParameter("b", bad);
            Assert.Throws<InvalidQueryException>(() => query.ToList());
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void RejectedForms()
    {
        using var store = Seed();

        string Message(string rql)
        {
            using var session = store.OpenSession();
            return Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<Game>(rql).ToList()).Message;
        }

        var left = $"left join ({UserSub}) as u on g.OwnerId = id(u)";
        Assert.Contains("must be a number that is not negative", Message($"{Outer} {left} boost -1 {Everything}"));
        Assert.Contains("must be a number", Message($"{Outer} {left} boost 'abc' {Everything}"));
        Assert.Contains("Expected a number or a parameter after BOOST", Message($"{Outer} {left} boost {Everything}"));
        Assert.Contains("must be a number that is not negative", Message($"{Outer} join ({UserSub}) as u on g.OwnerId = id(u) boost -0.5 {Everything}"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TimingsAreStillReported()
    {
        using var store = Seed();
        using var session = store.OpenSession();
        session.Advanced.RawQuery<Game>($"{Outer} left join ({UserSub}) as u on g.OwnerId = id(u) boost 0.5 {OuterWhere} order by score() {Everything} include timings()").Timings(out var timings).ToList();
        var counters = timings.Timings["Join"].Timings["u"].Counters;
        Assert.True(counters["SourceMatches"] > 0);
        Assert.True(counters.ContainsKey("ScanAllocatedManagedBytes"));
    }

    private static void AssertRows(List<Row> expected, List<Row> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal), actual.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));

        var byId = expected.ToDictionary(x => x.Id);
        foreach (var row in actual)
            Assert.True(Math.Abs(byId[row.Id].Score - row.Score) < 1e-3, $"{row.Id}: expected {byId[row.Id].Score}, got {row.Score}");

        // ordered by score (ties in any order)
        for (var i = 1; i < actual.Count; i++)
            Assert.True(actual[i - 1].Score >= actual[i].Score - 1e-6, $"position {i}: {actual[i - 1].Score} before {actual[i].Score}");
    }

    private static double ScoreOf(IDocumentSession session, object entity)
    {
        var metadata = session.Advanced.GetMetadataFor(entity);
        return metadata.ContainsKey(Constants.Documents.Metadata.IndexScore) ? Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore]) : 0;
    }

    private static List<Row> Fetch(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        var rows = new List<Row>();
        foreach (var entity in session.Advanced.RawQuery<Game>(rql).ToList())
        {
            var metadata = session.Advanced.GetMetadataFor(entity);
            rows.Add(new Row((string)metadata[Constants.Documents.Metadata.Id],
                metadata.ContainsKey(Constants.Documents.Metadata.IndexScore) ? Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore]) : 0));
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
        public string UserName { get; set; }
        public string DeptName { get; set; }
    }

    private IDocumentStore Seed()
    {
        var store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        new UsersIndex().Execute(store);
        new DeptsIndex().Execute(store);
        new GamesIndex().Execute(store);

        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < Users; i++)
                bulk.Store(new User { Id = $"users/{i}", Name = UserName(i), Country = Countries[i % Countries.Length], Rank = i });
            for (var i = 0; i < Depts; i++)
                bulk.Store(new Dept { Id = $"depts/{i}", Name = $"dept{i % 30} common" });
            for (var i = 0; i < Games; i++)
                bulk.Store(new Game { Id = $"games/{i}", Name = $"game{i} g{i % 13} common", OwnerId = GameOwner(i), DeptId = GameDept(i), Country = GameCountry(i) });
        }

        Indexes.WaitForIndexing(store);
        return store;
    }

    private sealed class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Country { get; set; }
        public long Rank { get; set; }
    }

    private sealed class Dept
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    private sealed class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string DeptId { get; set; }
        public string Country { get; set; }
    }

    private sealed class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Country, u.Rank };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    private sealed class DeptsIndex : AbstractIndexCreationTask<Dept>
    {
        public DeptsIndex()
        {
            Map = depts => from d in depts select new { d.Name };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Depts";
    }

    private sealed class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.DeptId, g.Country };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
