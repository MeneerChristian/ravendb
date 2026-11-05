using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// Facets over a JOIN: the facets are computed over the rows the join keeps
public class JoinFacetTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Everything = "limit 0, 1000000";
    private const string Games = "from index 'Games' as g";
    private const string Users = "from index 'Users' where search(Name, 'w2 w6')";
    private const string IdJoin = $"join ({Users}) as u on g.OwnerId = id(u)";
    private const string OtherIdJoin = "join (from index 'Users' where search(Name, 'w3 w9')) as b on g.OwnerId = id(b)";
    private const string FieldJoin = "join (from index 'Users' where Country = 'nl' and search(Name, 'w1 w2')) as u on g.Zip = u.Zip";

    private sealed class Data
    {
        public List<JoinScoreOrderTests.User> Users;
        public List<JoinScoreOrderTests.Game> Games;
        public Dictionary<string, int> Ranks;
        public HashSet<string> IdJoinUsers; // ids of the users that Users matches
        public HashSet<string> OtherUsers;
        public HashSet<string> FieldJoinZips;
    }

    private (IDocumentStore Store, Data Data) Seed()
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        var store = GetDocumentStore(options);
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);

        var generator = new JoinScoreOrderTests.Generator(7);
        var data = new Data { Users = generator.Users(400), Ranks = new() };
        data.Games = generator.Games(1200, 400);
        var zip = 0;
        foreach (var user in data.Users)
            user.Postcode = $"z{zip++ % 40}";
        zip = 0;
        foreach (var game in data.Games)
        {
            game.Postcode = $"z{zip % 40}";
            data.Ranks[game.Id] = zip++ % 10;
        }

        using (var bulk = store.BulkInsert())
        {
            foreach (var user in data.Users)
                bulk.Store(new UserDoc { Name = user.Name, Country = user.Country, Zip = user.Postcode }, user.Id);
            foreach (var game in data.Games)
                bulk.Store(new GameDoc { Name = game.Name, OwnerId = game.OwnerId, Country = game.Country, Zip = game.Postcode, Rank = data.Ranks[game.Id] }, game.Id);
        }

        Indexes.WaitForIndexing(store);

        data.IdJoinUsers = Ids(store, Users);
        data.OtherUsers = Ids(store, "from index 'Users' where search(Name, 'w3 w9')");
        using (var session = store.OpenSession())
        {
            var nl = session.Advanced.RawQuery<UserDoc>($"from index 'Users' where Country = 'nl' and search(Name, 'w1 w2') {Everything}").ToList();
            data.FieldJoinZips = nl.Select(x => x.Zip).ToHashSet();
        }

        return (store, data);
    }

    private static HashSet<string> Ids(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        return session.Advanced.RawQuery<UserDoc>($"{rql} {Everything}").ToList().Select(x => session.Advanced.GetDocumentId(x)).ToHashSet();
    }

    private sealed class FacetValueRow
    {
        public string Range;
        public int Count;
        public double? Sum, Min, Max, Average;
    }

    private sealed class FacetRow
    {
        public string Name;
        public List<FacetValueRow> Values;
        public int RemainingTermsCount;
        public int RemainingHits;
    }

    private static List<FacetRow> Facets(IDocumentStore store, string rql)
    {
        using var http = new HttpClient();
        using var response = http.PostAsync($"{store.Urls[0]}/databases/{store.Database}/queries",
            new StringContent(JsonSerializer.Serialize(new { Query = rql, QueryParameters = new { } }), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.True(response.IsSuccessStatusCode, body);

        using var document = JsonDocument.Parse(body);
        double? Number(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
        return document.RootElement.GetProperty("Results").EnumerateArray().Select(facet => new FacetRow
        {
            Name = facet.GetProperty("Name").GetString(),
            RemainingTermsCount = facet.GetProperty("RemainingTermsCount").GetInt32(),
            RemainingHits = facet.GetProperty("RemainingHits").GetInt32(),
            Values = facet.GetProperty("Values").EnumerateArray().Select(v => new FacetValueRow
            {
                Range = v.GetProperty("Range").GetString(),
                Count = v.GetProperty("Count").GetInt32(),
                Sum = Number(v, "Sum"),
                Min = Number(v, "Min"),
                Max = Number(v, "Max"),
                Average = Number(v, "Average")
            }).ToList()
        }).ToList();
    }

    private static Dictionary<string, int> Counts(List<FacetRow> facets, string name)
        => facets.Single(x => x.Name == name).Values.Where(x => x.Count > 0).ToDictionary(x => x.Range, x => x.Count);

    private static Dictionary<string, int> Expected(IEnumerable<JoinScoreOrderTests.Game> games, Func<JoinScoreOrderTests.Game, string> by)
        => games.GroupBy(by).ToDictionary(x => x.Key, x => x.Count());

    private static void AssertSame(Dictionary<string, int> expected, Dictionary<string, int> actual, string what)
    {
        Assert.True(expected.OrderBy(x => x.Key).SequenceEqual(actual.OrderBy(x => x.Key)),
            $"[{what}] expected {expected.Count} terms summing to {expected.Values.Sum()} but got {actual.Count} terms summing to {actual.Values.Sum()}");
    }

    private IEnumerable<JoinScoreOrderTests.Game> Joined(Data data) => data.Games.Where(x => data.IdJoinUsers.Contains(x.OwnerId));

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ValueFacetsWithoutAWhereHonourTheJoin()
    {
        var (store, data) = Seed();
        using (store)
        {
            var joined = Joined(data).ToList();
            Assert.True(joined.Count > 100 && joined.Count < data.Games.Count, $"the join must filter: {joined.Count} of {data.Games.Count}");

            // the oracle is also what a plain query with "in" returns
            using (var session = store.OpenSession())
            {
                var plain = session.Advanced.RawQuery<GameDoc>($"{Games} where g.OwnerId in ($ids) {Everything}").AddParameter("ids", data.IdJoinUsers.ToArray()).ToList();
                Assert.Equal(joined.Count, plain.Count);
                var joinRows = session.Advanced.RawQuery<GameDoc>($"{Games} {IdJoin} {Everything}").ToList();
                Assert.Equal(joined.Count, joinRows.Count);
            }

            var facets = Facets(store, $"{Games} {IdJoin} select facet(OwnerId), facet(Country)");
            AssertSame(Expected(joined, x => x.OwnerId), Counts(facets, "OwnerId"), "facet(OwnerId)");
            AssertSame(Expected(joined, x => x.Country), Counts(facets, "Country"), "facet(Country)");
            Assert.All(facets, f => Assert.Equal(0, f.RemainingHits));
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ValueFacetsWithAWhereHonourTheJoin()
    {
        var (store, data) = Seed();
        using (store)
        {
            var joined = Joined(data).Where(x => data.Ranks[x.Id] > 3).ToList();
            var facets = Facets(store, $"{Games} {IdJoin} where g.Rank > 3 select facet(OwnerId), facet(Country)");
            AssertSame(Expected(joined, x => x.OwnerId), Counts(facets, "OwnerId"), "facet(OwnerId)");
            AssertSame(Expected(joined, x => x.Country), Counts(facets, "Country"), "facet(Country)");
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void FacetsWithATermLimitCountTheRemainingTermsOfTheJoinedSet()
    {
        var (store, data) = Seed();
        using (store)
        {
            var joined = Joined(data).ToList();
            var byOwner = Expected(joined, x => x.OwnerId);
            var facets = Facets(store, $"{Games} {IdJoin} select facet(OwnerId, $opts)".Replace("$opts", "'{\"PageSize\":5}'"));
            var facet = facets.Single();
            Assert.Equal(5, facet.Values.Count);
            Assert.Equal(byOwner.Count - 5, facet.RemainingTermsCount);
            Assert.Equal(joined.Count - facet.Values.Sum(x => x.Count), facet.RemainingHits);
            foreach (var value in facet.Values)
                Assert.Equal(byOwner[value.Range], value.Count);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void RangeFacetsHonourTheJoin()
    {
        var (store, data) = Seed();
        using (store)
        {
            var joined = Joined(data).ToList();
            var facets = Facets(store, $"{Games} {IdJoin} select facet(Rank < 3, Rank >= 3 and Rank < 7, Rank >= 7)");
            var actual = facets.Single().Values.Where(x => x.Count > 0).Select(x => x.Count).ToList();
            var expected = new[] { joined.Count(x => data.Ranks[x.Id] < 3), joined.Count(x => data.Ranks[x.Id] is >= 3 and < 7), joined.Count(x => data.Ranks[x.Id] >= 7) };
            Assert.Equal(expected, actual);

            var withWhere = Facets(store, $"{Games} {IdJoin} where g.Country = 'nl' select facet(Rank < 3, Rank >= 3 and Rank < 7, Rank >= 7)");
            var nl = joined.Where(x => x.Country == "nl").ToList();
            Assert.Equal(new[] { nl.Count(x => data.Ranks[x.Id] < 3), nl.Count(x => data.Ranks[x.Id] is >= 3 and < 7), nl.Count(x => data.Ranks[x.Id] >= 7) },
                withWhere.Single().Values.Where(x => x.Count > 0).Select(x => x.Count));
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AggregationFacetsHonourTheJoin()
    {
        var (store, data) = Seed();
        using (store)
        {
            var joined = Joined(data).ToList();
            var facets = Facets(store, $"{Games} {IdJoin} select facet(Country, sum(Rank), min(Rank), max(Rank), avg(Rank))");
            var byCountry = joined.GroupBy(x => x.Country).ToDictionary(x => x.Key, x => x.Select(g => data.Ranks[g.Id]).ToList());
            var values = facets.Single().Values.ToDictionary(x => x.Range);
            Assert.Equal(byCountry.Keys.OrderBy(x => x), values.Keys.OrderBy(x => x));
            foreach (var (country, ranks) in byCountry)
            {
                var value = values[country];
                Assert.Equal(ranks.Count, value.Count);
                Assert.Equal(ranks.Sum(), value.Sum);
                Assert.Equal(ranks.Min(), value.Min);
                Assert.Equal(ranks.Max(), value.Max);
                Assert.True(Math.Abs(ranks.Average() - value.Average.Value) < 1e-9, $"{country}: average {ranks.Average()} but {value.Average}");
            }

            var all = Facets(store, $"{Games} {IdJoin} select facet(sum(Rank), max(Rank))").Single().Values.Single();
            Assert.Equal(joined.Count, all.Count);
            Assert.Equal(joined.Sum(x => data.Ranks[x.Id]), all.Sum);
            Assert.Equal(joined.Max(x => data.Ranks[x.Id]), all.Max);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void FacetsOverAFieldJoinHonourIt()
    {
        var (store, data) = Seed();
        using (store)
        {
            var joined = data.Games.Where(x => data.FieldJoinZips.Contains(x.Postcode)).ToList();
            Assert.True(joined.Count > 20 && joined.Count < data.Games.Count, $"the join must filter: {joined.Count} of {data.Games.Count}");
            var facets = Facets(store, $"{Games} {FieldJoin} select facet(Country), facet(OwnerId)");
            AssertSame(Expected(joined, x => x.Country), Counts(facets, "Country"), "facet(Country)");
            AssertSame(Expected(joined, x => x.OwnerId), Counts(facets, "OwnerId"), "facet(OwnerId)");
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void FacetsOverSeveralJoinsHonourAndAndOr()
    {
        var (store, data) = Seed();
        using (store)
        {
            var both = data.Games.Where(x => data.IdJoinUsers.Contains(x.OwnerId) && data.OtherUsers.Contains(x.OwnerId)).ToList();
            var either = data.Games.Where(x => data.IdJoinUsers.Contains(x.OwnerId) || data.OtherUsers.Contains(x.OwnerId)).ToList();
            Assert.True(both.Count > 0 && both.Count < either.Count, $"{both.Count} / {either.Count}");

            // the order of the joins does not matter
            AssertSame(Expected(both, x => x.Country), Counts(Facets(store, $"{Games} {IdJoin} {OtherIdJoin} select facet(Country)"), "Country"), "and");
            AssertSame(Expected(both, x => x.Country), Counts(Facets(store, $"{Games} {OtherIdJoin} {IdJoin} select facet(Country)"), "Country"), "and, other order");
            AssertSame(Expected(either, x => x.Country),
                Counts(Facets(store, $"{Games} {IdJoin} {OtherIdJoin} where matches(u) or matches(b) select facet(Country)"), "Country"), "or");
            AssertSame(Expected(either.Where(x => data.Ranks[x.Id] > 3), x => x.OwnerId),
                Counts(Facets(store, $"{Games} {IdJoin} {OtherIdJoin} where g.Rank > 3 and (matches(u) or matches(b)) select facet(OwnerId)"), "OwnerId"), "or with a clause");
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void FacetOnAFieldOfTheJoinedAliasIsRejected()
    {
        var (store, _) = Seed();
        using (store)
        {
            using var http = new HttpClient();
            using var response = http.PostAsync($"{store.Urls[0]}/databases/{store.Database}/queries",
                new StringContent(JsonSerializer.Serialize(new { Query = $"{Games} {IdJoin} select facet(u.Country)", QueryParameters = new { } }), Encoding.UTF8, "application/json"))
                .GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.False(response.IsSuccessStatusCode, body);
            Assert.Contains("InvalidQueryException", body);
            Assert.Contains("facet", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("'u'", body);
        }
    }


    private static string PostRaw(IDocumentStore store, string path, string rql, out bool ok)
    {
        using var http = new HttpClient();
        using var response = http.PostAsync($"{store.Urls[0]}/databases/{store.Database}{path}",
            new StringContent(JsonSerializer.Serialize(new { Query = rql, QueryParameters = new { } }), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        ok = response.IsSuccessStatusCode;
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private void WithSeed(Action<IDocumentStore, Data, List<string>> test)
    {
        var (store, data) = Seed();
        using (store)
            test(store, data, Joined(data).Select(x => x.Id).OrderBy(x => x).ToList());
    }

    private static List<string> ReturnedIds(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("Results").EnumerateArray().Select(x => x.GetProperty("@metadata").GetProperty("@id").GetString()).OrderBy(x => x).ToList();
    }

    // applied: only rows of the join come back (the whole filtered set when the query has no other clause)
    private static void AssertApplied(IDocumentStore store, string path, string rql, List<string> ids, bool wholeSet)
    {
        var body = PostRaw(store, path, rql, out var ok);
        Assert.True(ok, body);
        var returned = ReturnedIds(body);
        Assert.NotEmpty(returned);
        Assert.All(returned, id => Assert.Contains(id, ids));
        if (wholeSet)
            Assert.Equal(ids, returned);
    }

    private static void AssertRejectedClearly(IDocumentStore store, string rql, string mentions)
    {
        var body = PostRaw(store, "/queries", rql, out var ok);
        Assert.False(ok, $"the join must not be silently ignored: {body}");
        Assert.Contains(mentions, body, StringComparison.OrdinalIgnoreCase);
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AuditQueryAndCountShortcuts() => WithSeed((store, data, ids) =>
    {
        using var session = store.OpenSession();
        var rows = session.Advanced.RawQuery<GameDoc>($"{Games} {IdJoin} {Everything}").Statistics(out var stats).ToList();
        Assert.Equal(ids.Count, rows.Count);
        Assert.Equal(ids.Count, stats.TotalResults);

        // the count shortcut: no rows, only the total
        session.Advanced.RawQuery<GameDoc>($"{Games} {IdJoin} limit 0, 0").Statistics(out var countOnly).ToList();
        Assert.Equal(ids.Count, countOnly.TotalResults);

        // a small page: the rows are still the joined ones
        var page = session.Advanced.RawQuery<GameDoc>($"{Games} {IdJoin} limit 0, 10").ToList();
        Assert.Equal(10, page.Count);
        Assert.All(page, x => Assert.Contains(session.Advanced.GetDocumentId(x), ids));

        var projected = PostRaw(store, "/queries", $"{Games} {IdJoin} select Country {Everything}", out var ok);
        Assert.True(ok, projected);
        using var document = JsonDocument.Parse(projected);
        Assert.Equal(ids.Count, document.RootElement.GetProperty("Results").GetArrayLength());
    });

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AuditStreaming() => WithSeed((store, data, ids) =>
    {
        using (var session = store.OpenSession())
        {
            var streamed = new List<string>();
            using var stream = session.Advanced.Stream(session.Advanced.RawQuery<GameDoc>($"{Games} {IdJoin}"));
            while (stream.MoveNext())
                streamed.Add(stream.Current.Id);
            Assert.Equal(ids, streamed.OrderBy(x => x).ToList());
        }

        var body = PostRaw(store, "/streams/queries", $"{Games} {IdJoin}", out var ok);
        Assert.True(ok, body);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(ids.Count, document.RootElement.GetProperty("Results").GetArrayLength());
    });

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AuditIndexEntries() => WithSeed((store, data, ids) =>
    {
        var body = PostRaw(store, "/queries?debug=entries", $"{Games} {IdJoin} {Everything}", out var ok);
        Assert.True(ok, body);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(ids.Count, document.RootElement.GetProperty("Results").GetArrayLength());
    });

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AuditIncludeTimings() => WithSeed((store, data, ids) =>
    {
        using var session = store.OpenSession();
        var rows = session.Advanced.RawQuery<GameDoc>($"{Games} {IdJoin} {Everything} include timings()").Timings(out var timings).ToList();
        Assert.Equal(ids.Count, rows.Count);
        Assert.True(timings.Timings.ContainsKey("Join"));
    });

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AuditHighlighting() => WithSeed((store, data, ids) =>
    {
        const string search = "search(g.Name, 'w1')";
        const string highlight = "include highlight(Name, 18, 2)";
        var joinedBody = PostRaw(store, "/queries", $"{Games} {IdJoin} where {search} {Everything} {highlight}", out var joinedOk);
        Assert.True(joinedOk, joinedBody);
        var returned = ReturnedIds(joinedBody);
        Assert.NotEmpty(returned);
        Assert.All(returned, id => Assert.Contains(id, ids));

        var joinedFragments = Highlightings(joinedBody, "Name");
        Assert.NotEmpty(joinedFragments);
        // a fragment is given for a row of the join only, and for every row that the search matched
        Assert.All(joinedFragments.Keys, id => Assert.Contains(id, returned));
        Assert.Equal(returned, joinedFragments.Keys.OrderBy(x => x).ToList());
        Assert.All(joinedFragments.Values, fragments => Assert.Contains(fragments, x => x.Contains("w1", StringComparison.Ordinal) && x.Contains("<b", StringComparison.Ordinal)));

        // the same rows of the query without the join give the same fragments
        var plainBody = PostRaw(store, "/queries", $"{Games} where g.OwnerId in ({string.Join(", ", Joined(data).Select(x => x.OwnerId).Distinct().Select(x => $"'{x}'"))}) and {search} {Everything} {highlight}", out var plainOk);
        Assert.True(plainOk, plainBody);
        var plainFragments = Highlightings(plainBody, "Name");
        Assert.Equal(joinedFragments.Count, plainFragments.Count);
        foreach (var (id, fragments) in joinedFragments)
            Assert.Equal(plainFragments[id], fragments);
    });

    private static Dictionary<string, List<string>> Highlightings(string body, string field)
    {
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("Highlightings", out var all), "the response has no highlightings: " + body);
        Assert.True(all.TryGetProperty(field, out var byDocument), $"no highlightings of {field}: {body}");
        return byDocument.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.EnumerateArray().Select(f => f.GetString()).ToList());
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AuditSuggestionsAreRejected() => WithSeed((store, data, ids) =>
        AssertRejectedClearly(store, $"{Games} {IdJoin} select suggest(Name, 'w1')", "join"));

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AuditMoreLikeThisIsJoined() => WithSeed((store, data, ids) =>
    {
        const string mlt = "where morelikethis(id() = 'games/1', '{\"Fields\":[\"Name\"],\"MinimumTermFrequency\":1,\"MinimumDocumentFrequency\":1}')";
        var plain = PostRaw(store, "/queries", $"{Games} {mlt} limit 0, 1000000", out var plainOk);
        var joined = PostRaw(store, "/queries", $"{Games} {IdJoin} {mlt} limit 0, 1000000", out var joinedOk);
        Assert.True(plainOk && joinedOk, joined);
        using var plainDocument = JsonDocument.Parse(plain);
        using var joinedDocument = JsonDocument.Parse(joined);
        var plainIds = plainDocument.RootElement.GetProperty("Results").EnumerateArray().Select(x => x.GetProperty("@metadata").GetProperty("@id").GetString()).ToHashSet();
        var joinedIds = joinedDocument.RootElement.GetProperty("Results").EnumerateArray().Select(x => x.GetProperty("@metadata").GetProperty("@id").GetString()).ToList();
        Assert.True(joinedIds.Count > 0 && joinedIds.Count < plainIds.Count, $"{joinedIds.Count} of {plainIds.Count}");
        Assert.All(joinedIds, id => Assert.Contains(id, plainIds));
    });

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AuditIntersectAndExplanationsAreNotSupportedByCoraxAtAll() => WithSeed((store, data, ids) =>
    {
        AssertRejectedClearly(store, $"{Games} {IdJoin} where intersect(g.Rank > 2, g.Country = 'nl')", "intersect");
        AssertRejectedClearly(store, $"{Games} {IdJoin} where search(g.Name, 'w1') include explanations()", "explanations");
    });

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AuditGroupByIsRejectedOnAnIndexAndDistinctIsAppliedToTheJoinedRows() => WithSeed((store, data, ids) =>
    {
        // group by is not possible on an index, with or without the join
        foreach (var rql in new[] { $"{Games} group by Country select Country, count()", $"{Games} {IdJoin} group by Country select Country, count()" })
        {
            var body = PostRaw(store, "/queries", rql, out var ok);
            Assert.False(ok, $"{rql} was accepted: {body}");
            Assert.Contains("Can't use 'group by' when querying on an Index", body, StringComparison.Ordinal);
        }

        var distinct = PostRaw(store, "/queries", $"{Games} {IdJoin} select distinct Country {Everything}", out var distinctOk);
        Assert.True(distinctOk, distinct);
        using var document = JsonDocument.Parse(distinct);
        var countries = document.RootElement.GetProperty("Results").EnumerateArray().Select(x => x.TryGetProperty("Country", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null).OrderBy(x => x).ToList();
        var expected = Expected(Joined(data), x => x.Country).Keys.OrderBy(x => x).ToList();
        Assert.True(expected.Count > 1, "the data has to hold more than one country, or the distinct proves nothing");
        Assert.Equal(expected, countries);
    });

    private sealed class UserDoc
    {
        public string Name { get; set; }
        public string Country { get; set; }
        public string Zip { get; set; }
    }

    private sealed class GameDoc
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string Country { get; set; }
        public string Zip { get; set; }
        public int Rank { get; set; }
    }

    private sealed class UsersIndex : AbstractIndexCreationTask<UserDoc>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Country, u.Zip };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    private sealed class GamesIndex : AbstractIndexCreationTask<GameDoc>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.Country, g.Zip, g.Rank };
            Index(x => x.Name, FieldIndexing.Search);
            Suggestion(x => x.Name);
        }

        public override string IndexName => "Games";
    }
}
