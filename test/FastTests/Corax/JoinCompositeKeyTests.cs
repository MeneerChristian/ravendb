using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corax.Querying;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Exceptions;
using Raven.Server.Config;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Queries.Parser;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// A JOIN on several fields: the tuple has to be held by the SAME joined entry
public class JoinCompositeKeyTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Everything = "limit 0, 1000000";
    private const string Main = "from index 'CkOuter' as p";
    private const string TwoOn = "p.Country = c.Country and p.City = c.City";
    private static readonly string[] Countries = ["NL", "nl", "De", "BE", null];
    private static readonly string[] Cities = ["Utrecht", "utrecht", "Gent", "Bonn", null];
    private static readonly string[] Regions = ["n", "s", "e", "w"];

    private sealed class Place { public string Id; public string Country; public string City; public List<string> Regions; public string Name; public string Status; }
    private sealed class Cand { public string Id; public string Country; public string City; public List<string> Regions; public string Tag; public string Name; }

    private sealed class OuterIndex : AbstractIndexCreationTask<Place>
    {
        public override string IndexName => "CkOuter";
        public OuterIndex() { Map = docs => from d in docs select new { d.Country, d.City, d.Regions, d.Name, d.Status }; }
    }

    private sealed class OuterExactIndex : AbstractIndexCreationTask<Place>
    {
        public override string IndexName => "CkOuterExact";
        public OuterExactIndex()
        {
            Map = docs => from d in docs select new { d.Country, d.City, d.Name, d.Status };
            Index(x => x.Country, FieldIndexing.Exact);
        }
    }

    private sealed class InnerIndex : AbstractIndexCreationTask<Cand>
    {
        public override string IndexName => "CkInner";
        public InnerIndex() { Map = docs => from d in docs select new { d.Country, d.City, d.Regions, d.Tag, d.Name }; }
    }

    private sealed class InnerExactIndex : AbstractIndexCreationTask<Cand>
    {
        public override string IndexName => "CkInnerExact";
        public InnerExactIndex()
        {
            Map = docs => from d in docs select new { d.Country, d.City, d.Tag, d.Name };
            Index(x => x.Country, FieldIndexing.Exact);
        }
    }

    private sealed class InnerExactCityIndex : AbstractIndexCreationTask<Cand>
    {
        public override string IndexName => "CkInnerExactCity";
        public InnerExactCityIndex()
        {
            Map = docs => from d in docs select new { d.Country, d.City, d.Tag, d.Name };
            Index(x => x.City, FieldIndexing.Exact);
        }
    }

    private sealed class Wide { public string Id; public List<string> A; public List<string> B; public string Tag; }

    private sealed class WideIndex : AbstractIndexCreationTask<Wide>
    {
        public override string IndexName => "CkWide";
        public WideIndex() { Map = docs => from d in docs select new { d.A, d.B, d.Tag }; }
    }

    private sealed class Data
    {
        public readonly List<Place> Places = new();
        public readonly List<Cand> Cands = new();
    }

    private IDocumentStore CreateStore(out Data data)
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        var store = GetDocumentStore(options);
        new OuterIndex().Execute(store);
        new OuterExactIndex().Execute(store);
        new InnerIndex().Execute(store);
        new InnerExactIndex().Execute(store);
        new InnerExactCityIndex().Execute(store);

        var random = new Random(7);
        List<string> RandomRegions() => random.Next(4) == 0 ? null : Enumerable.Range(0, random.Next(0, 4)).Select(_ => Regions[random.Next(Regions.Length)]).ToList();
        data = new Data();
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < 60; i++)
            {
                var cand = new Cand
                {
                    Id = $"cands/{i:D3}", Country = Countries[random.Next(Countries.Length)], City = Cities[random.Next(Cities.Length)], Regions = RandomRegions(),
                    Tag = i % 3 == 0 ? "a" : i % 3 == 1 ? "b" : "c", Name = $"c{i:D3}"
                };
                data.Cands.Add(cand);
                bulk.Store(cand, cand.Id);
            }

            for (var i = 0; i < 300; i++)
            {
                var place = new Place
                {
                    Id = $"places/{i:D3}", Country = Countries[random.Next(Countries.Length)], City = Cities[random.Next(Cities.Length)], Regions = RandomRegions(),
                    Name = $"p{i:D3}", Status = random.Next(3) == 0 ? "open" : "closed"
                };
                data.Places.Add(place);
                bulk.Store(place, place.Id);
            }
        }

        Indexes.WaitForIndexing(store);
        return store;
    }

    private static bool Same(string a, string b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool Tagged(Cand c) => c.Tag is "a" or "b";
    private static bool SharesRegion(Place p, Cand c) => p.Regions != null && c.Regions != null && p.Regions.Any(r => c.Regions.Contains(r));

    private static string[] Ids(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<Place>(rql).ToList().Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static string[] Expect(Data data, Func<Place, bool> predicate) => data.Places.Where(predicate).Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static string Join(string on = TwoOn, string sub = "from index 'CkInner' where Tag in ('a', 'b')", string left = "", string boost = "")
        => $"{left}join ({sub}) as c on {on}{boost}";

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TwoFieldsMatchOnTheSameJoinedEntry()
    {
        using var store = CreateStore(out var data);
        var tagged = data.Cands.Where(Tagged).ToList();
        var expected = Expect(data, p => tagged.Any(c => Same(p.Country, c.Country) && Same(p.City, c.City)));

        Assert.NotEmpty(expected);
        Assert.Equal(expected, Ids(store, $"{Main} {Join()} {Everything}"));
        Assert.Equal(expected, Ids(store, $"{Main} {Join()} order by score() {Everything}"));
        Assert.Equal(expected, Ids(store, $"{Main} {Join()} order by p.Name {Everything}"));

        // a place that matches the country of one entry and the city of another is not a match
        var crossed = data.Places.Where(p => tagged.Any(c => Same(p.Country, c.Country)) && tagged.Any(c => Same(p.City, c.City))).Select(x => x.Id).Except(expected).ToList();
        Assert.NotEmpty(crossed);
        Assert.DoesNotContain(crossed, expected.Contains);

        // the equalities can be written either way round
        Assert.Equal(expected, Ids(store, $"{Main} {Join(on: "c.Country = p.Country and p.City = c.City")} {Everything}"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OuterWhereTotalsAndPaging()
    {
        using var store = CreateStore(out var data);
        var tagged = data.Cands.Where(Tagged).ToList();
        Func<Place, bool> matches = p => tagged.Any(c => Same(p.Country, c.Country) && Same(p.City, c.City));

        var expected = Expect(data, p => p.Status == "open" && matches(p));
        Assert.Equal(expected, Ids(store, $"{Main} {Join()} where p.Status = 'open' {Everything}"));

        var all = data.Places.Where(matches).OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
        using var session = store.OpenSession();
        var page = session.Advanced.RawQuery<Place>($"{Main} {Join()} order by p.Name limit 5, 7").Statistics(out var stats).ToList();
        Assert.Equal(all.Skip(5).Take(7).Select(x => x.Id), page.Select(x => x.Id));
        Assert.Equal(all.Count, stats.TotalResults);

        // the join with an OR: a row matches through the join or through the other side
        Assert.Equal(Expect(data, p => matches(p) || p.Status == "open"), Ids(store, $"{Main} {Join()} where matches(c) or p.Status = 'open' {Everything}"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ThreeFieldsAndArrays()
    {
        using var store = CreateStore(out var data);
        var tagged = data.Cands.Where(Tagged).ToList();

        var three = Expect(data, p => tagged.Any(c => Same(p.Country, c.Country) && Same(p.City, c.City) && SharesRegion(p, c)));
        Assert.NotEmpty(three);
        Assert.Equal(three, Ids(store, $"{Main} {Join(on: TwoOn + " and p.Regions = c.Regions")} {Everything}"));

        var two = Expect(data, p => tagged.Any(c => Same(p.Country, c.Country) && SharesRegion(p, c)));
        Assert.NotEmpty(two);
        Assert.Equal(two, Ids(store, $"{Main} {Join(on: "p.Country = c.Country and p.Regions = c.Regions")} {Everything}"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void LeftJoinKeepsEveryRowAndScoresTheOnesThatMatch()
    {
        using var store = CreateStore(out var data);
        var tagged = data.Cands.Where(Tagged).ToList();
        Func<Place, bool> matches = p => tagged.Any(c => Same(p.Country, c.Country) && Same(p.City, c.City));

        Assert.Equal(Expect(data, _ => true), Ids(store, $"{Main} {Join(left: "left ")} {Everything}"));
        Assert.Equal(Expect(data, p => p.Status == "open"), Ids(store, $"{Main} {Join(left: "left ")} where p.Status = 'open' {Everything}"));
        Assert.Equal(Expect(data, matches), Ids(store, $"{Main} {Join(left: "left ")} where matches(c) {Everything}"));

        // the rows that matched come first when they are ordered by score
        var matched = data.Places.Count(matches);
        using var session = store.OpenSession();
        var top = session.Advanced.RawQuery<Place>($"{Main} {Join(left: "left ")} order by score() limit 0, {matched}").ToList().Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(Expect(data, matches), top);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ProjectionTakesTheBestScoringJoinedEntryOfTheTuple()
    {
        using var store = CreateStore(out var data);
        const string sub = "from index 'CkInner' where boost(Tag = 'a', 5) or Tag = 'b'";
        var tagged = data.Cands.Where(Tagged).ToList();

        using var session = store.OpenSession();
        var rows = session.Advanced.RawQuery<Projected>($"{Main} {Join(sub: sub)} select p.Name as Name, c.Name as CandName {Everything}").ToList().ToDictionary(x => x.Name);
        var seen = 0;
        foreach (var place in data.Places)
        {
            var candidates = tagged.Where(c => Same(place.Country, c.Country) && Same(place.City, c.City)).ToList();
            if (candidates.Count == 0)
            {
                Assert.False(rows.ContainsKey(place.Name), place.Name);
                continue;
            }

            // the entry of the best score (the tag a), the first one indexed on a tie
            var best = candidates.OrderByDescending(c => c.Tag == "a").ThenBy(c => c.Id, StringComparer.Ordinal).First();
            Assert.Equal(best.Name, rows[place.Name].CandName);
            seen++;
        }

        Assert.True(seen > 10);
        Assert.Equal(seen, rows.Count);
    }

    private sealed class Projected { public string Name { get; set; } public string CandName { get; set; } }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void BoostScalesTheScoreAndZeroKeepsTheFilter()
    {
        using var store = CreateStore(out var data);
        var tagged = data.Cands.Where(Tagged).ToList();
        var expected = Expect(data, p => tagged.Any(c => Same(p.Country, c.Country) && Same(p.City, c.City)));

        Assert.Equal(expected, Ids(store, $"{Main} {Join(boost: " boost 0")} order by score() {Everything}"));
        Assert.Equal(expected, Ids(store, $"{Main} {Join(boost: " boost 3")} order by score() {Everything}"));

        // a row whose tuple is held by an entry of the tag a scores above a row that has b entries only, with any boost above 0
        const string sub = "from index 'CkInner' where boost(Tag = 'a', 5) or Tag = 'b'";
        var tagA = data.Cands.Where(c => c.Tag == "a").ToList();
        var withA = Expect(data, p => tagA.Any(c => Same(p.Country, c.Country) && Same(p.City, c.City)));
        Assert.NotEmpty(withA);
        Assert.True(withA.Length < expected.Length);

        using var session = store.OpenSession();
        foreach (var boost in new[] { "", " boost 3", " boost 0.5" })
        {
            var top = session.Advanced.RawQuery<Place>($"{Main} {Join(sub: sub, boost: boost)} order by score() limit 0, {withA.Length}").ToList().Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal);
            Assert.Equal(withA, top);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AJoinedFieldThatKeepsTheCaseIsMatchedWithoutCase()
    {
        using var store = CreateStore(out var data);
        var tagged = data.Cands.Where(Tagged).ToList();
        var expected = Expect(data, p => tagged.Any(c => Same(p.Country, c.Country) && Same(p.City, c.City)));

        Assert.Equal(expected, Ids(store, $"{Main} {Join(sub: "from index 'CkInnerExact' where Tag in ('a', 'b')")} {Everything}"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OnlyTheSecondJoinedFieldKeepsTheCaseAndTheFirstOneIsStillMatchedWithoutCase()
    {
        using var store = CreateStore(out var data);
        var tagged = data.Cands.Where(Tagged).ToList();
        var expected = Expect(data, p => tagged.Any(c => Same(p.Country, c.Country) && Same(p.City, c.City)));

        Assert.Equal(expected, Ids(store, $"{Main} {Join(sub: "from index 'CkInnerExactCity' where Tag in ('a', 'b')")} {Everything}"));
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(true)]
    [InlineData(false)]
    public void TooManyCombinationsOfOneEntryAreAnInvalidQuery(bool inTheSubQuery)
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        using var store = GetDocumentStore(options);
        new WideIndex().Execute(store);
        var many = Enumerable.Range(0, 300).Select(i => $"v{i}").ToList();
        using (var session = store.OpenSession())
        {
            session.Store(new Wide { A = many, B = many, Tag = inTheSubQuery ? "inner" : "outer" }, "wide/1");
            session.Store(new Wide { A = ["v1"], B = ["v1"], Tag = inTheSubQuery ? "outer" : "inner" }, "wide/2");
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);
        using (var session = store.OpenSession())
        {
            var e = Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<Wide>(
                "from index 'CkWide' as p join (from index 'CkWide' where Tag = 'inner') as c on p.A = c.A and p.B = c.B where p.Tag = 'outer' limit 0, 10").ToList());
            Assert.Contains("combinations", e.Message);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AnOuterFieldThatKeepsTheCaseAgainstALowercasedOneIsRejected()
    {
        using var store = CreateStore(out _);
        using var session = store.OpenSession();

        var e = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<Place>($"from index 'CkOuterExact' as p {Join()} {Everything}").ToList());
        Assert.Contains("would never match", e.Message);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void RejectedForms()
    {
        using var store = CreateStore(out _);
        using var session = store.OpenSession();

        void Rejects(string rql, string message)
        {
            var e = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<Place>(rql).ToList());
            Assert.Contains(message, e.Message);
        }

        Rejects($"{Main} {Join()} order by c.Name {Everything}", "joins on several fields");
        Rejects($"{Main} {Join(on: "p.Country = c.Country and p.City = id(c)")} {Everything}", "id(c)");
        Rejects($"{Main} {Join(on: "p.Country = c.Country or p.City = c.City")} {Everything}", "must be an equality");
        Rejects($"{Main} {Join(on: "p.Country = c.Country and p.City > c.City")} {Everything}", "must be an equality");
        Rejects($"{Main} {Join(on: "p.Country = c.Country and p.Country = c.Country")} {Everything}", "repeats");
        Rejects($"{Main} {Join(on: "p.Country = c.Country and p.City = c.Nope")} {Everything}", "is not indexed");
        Rejects($"{Main} {Join()} join (from index 'CkInner') as d on c.Country = d.Country {Everything}", "chain of joins");
        Rejects($"{Main} join (from index 'CkInner') as d on p.Country = d.Country {Join(on: "p.Country = c.Country and d.City = c.City")} {Everything}", "only fields of the main document");
    }

    private static Query Parse(string q)
    {
        var parser = new QueryParser();
        parser.Init(q);
        return parser.Parse();
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void ParserKeepsTheEqualitiesAndTheTextRoundTrips()
    {
        var query = Parse("from index 'A' as p join (from index 'B') as c on c.X = p.A and p.B = c.Y and p.D = c.Z boost 2 where p.Q = 1");
        var join = Assert.Single(query.Joins);

        Assert.True(join.IsComposite);
        Assert.Equal(new[] { "X", "Y", "Z" }, join.Pairs.Select(x => x.InnerField));
        Assert.Equal(new[] { "p.A", "p.B", "p.D" }, join.Pairs.Select(x => ((FieldExpression)x.On.Left).FieldValue));

        var reparsed = Parse(query.ToString());
        var again = Assert.Single(reparsed.Joins);
        Assert.Equal(join.Pairs.Select(x => x.InnerField), again.Pairs.Select(x => x.InnerField));
        Assert.Equal(query.ToString(), reparsed.ToString());
        Assert.NotNull(again.Boost);

        Assert.False(Assert.Single(Parse("from index 'A' as p join (from index 'B') as c on p.A = c.X").Joins).IsComposite);
    }

    [RavenTheory(RavenTestCategory.Querying)]
    [InlineData("from index 'A' as p join (from index 'B') as c on p.A = c.X and p.B = id(c)", "only equality")]
    [InlineData("from index 'A' as p join (from index 'B') as c on p.A = c.X and p.B = p.C", "must be an equality")]
    [InlineData("from index 'A' as p join (from index 'B') as c on p.A = c.X and p.A = c.X", "repeats")]
    [InlineData("from index 'A' as p join (from index 'B') as c on p.A = c.X and", "second part")]
    public void InvalidCompositeOnClauses(string q, string message)
    {
        var e = Assert.ThrowsAny<Exception>(() => Parse(q));
        Assert.Contains(message, e.Message);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void KeysOfDifferentTuplesNeverCollide()
    {
        static byte[] Key(params string[] parts)
        {
            using var builder = new CompositeKeyBuilder(parts.Length);
            for (var i = 0; i < parts.Length; i++)
                builder.Add(i, Encoding.UTF8.GetBytes(parts[i]));

            Assert.Equal(1, builder.Combinations);
            return builder.Combination(0).ToArray();
        }

        var tuples = new[] { new[] { "ab", "c" }, new[] { "a", "bc" }, new[] { "a\0b", "c" }, new[] { "a", "\0bc" }, new[] { "a\0", "bc" }, new[] { "a\u0001", "bc" }, new[] { "a", "\u0001bc" }, new[] { "", "abc" }, new[] { "abc", "" } };
        var keys = tuples.Select(t => Convert.ToHexString(Key(t))).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());

        // the first term comes back as it was, an array makes a key per combination
        var first = new byte[16];
        var length = CompositeKey.FirstTerm(Key("a\0b", "c"), first);
        Assert.Equal("a\0b", Encoding.UTF8.GetString(first, 0, length));

        using var arrays = new CompositeKeyBuilder(2);
        arrays.Add(0, "x"u8);
        arrays.Add(0, "y"u8);
        arrays.Add(1, "1"u8);
        arrays.Add(1, "2"u8);
        arrays.Add(1, "3"u8);
        Assert.Equal(6, arrays.Combinations);
        Assert.Equal(6, Enumerable.Range(0, 6).Select(c => Convert.ToHexString(arrays.Combination(c))).Distinct().Count());

        arrays.Reset();
        arrays.Add(0, "x"u8);
        Assert.Equal(0, arrays.Combinations); // the second field has no value
    }
}
