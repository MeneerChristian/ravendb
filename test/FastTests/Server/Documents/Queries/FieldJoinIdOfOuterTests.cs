using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Newtonsoft.Json.Linq;
using Raven.Client.Exceptions;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Queries;

/// <summary>
/// The join of the server that matches the id of the main document against a field of the joined documents (ON id(h) = i.Owner), pinned against <see cref="FieldJoinModel"/>:
/// the key of an outer row is its id, many joined documents can hold the same id.
/// </summary>
public class FieldJoinIdOfOuterTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Everything = "limit 0, 1000000";
    private const string OuterRql = "from index 'Holders/ByLabel' as h";
    private const string OuterWhere = "where search(h.Text, 'common')";
    private const string InnerAll = "from index 'Items/ByOwner'";
    private const string InnerHot = "from index 'Items/ByOwner' where Tag = 'hot'";
    private const string InnerWords = "from index 'Items/ByOwner' where search(Text, 'alpha beta')";

    private static readonly string[] Subs = [InnerAll, InnerHot, InnerWords];
    private static readonly string[] Boosts = ["", " boost 0", " boost 0.5", " boost 2"];

    private sealed record Row(string Id, double Score);

    private sealed class Holder
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Text { get; set; }
    }

    private sealed class Item
    {
        public string Id { get; set; }
        public string Tag { get; set; }
        public string Text { get; set; }
        public string Owner { get; set; }
        public string[] Owners { get; set; }
        public long Rank { get; set; }
    }

    private sealed class ItemWithoutKeys
    {
        public string Tag { get; set; }
        public string Text { get; set; }
    }

    private sealed record Form(string Name, string On, Func<Item, IEnumerable<string>> InnerKeys);

    private static readonly Form[] Forms =
    [
        new("id=field", "id(h) = i.Owner", x => [x.Owner]),
        new("id=array", "id(h) = i.Owners", x => x.Owners ?? []),
        new("field=id", "i.Owner = id(h)", x => [x.Owner]),
        new("array=id", "i.Owners = id(h)", x => x.Owners ?? [])
    ];

    private static Form FormOf(string name) => Forms.Single(x => x.Name == name);

    private sealed class Data
    {
        public HashSet<string> Omitted { get; } = ["items/8"];

        public Dictionary<string, Holder> Holders { get; } = new();
        public Dictionary<string, Item> Items { get; } = new();

        public static Data Create()
        {
            var data = new Data();
            for (var i = 1; i <= 12; i++)
                AddHolder(data, "holders/h" + i.ToString("00"));
            AddHolder(data, "holders/h13 x");

            // items 1 and 2 and 10 hold the id of h01 (many joined documents for one outer row), the mixed case id of item 3 is h02
            AddItem(data, 1, "hot", "alpha", "holders/h01", ["holders/h01", "holders/h02"]);
            AddItem(data, 2, "hot", "alpha beta gamma delta", "holders/h01", ["holders/h03"]);
            AddItem(data, 3, "cold", "alpha alpha", "HOLDERS/H02", ["HOLDERS/H04", "holders/h04"]);
            AddItem(data, 4, "hot", "beta", "holders/h05", []);
            AddItem(data, 5, "cold", "gamma", null, null);
            AddItem(data, 6, "hot", "kappa kappa", "", [""]);
            AddItem(data, 7, "hot", "epsilon", "holders/h99", ["holders/h99", "holders/h06"]);
            AddItem(data, 8, "hot", "zeta", null, null);
            AddItem(data, 9, "hot", "eta", "holders/h07", ["holders/h07", "holders/h07"]);
            AddItem(data, 10, "hot", "theta", "holders/h01", ["holders/h01", "holders/h09"]);
            AddItem(data, 11, "hot", "alpha beta", "holders/h13 x", ["holders/h13 x", "holders/h13 x"]);
            AddItem(data, 12, "cold", "iota", "holders/h10", ["holders/h11"]);
            return data;
        }

        private static void AddHolder(Data data, string id)
        {
            var number = id.Substring("holders/h".Length, 2);
            data.Holders[id] = new Holder { Id = id, Label = "label-" + number, Text = "common" + string.Concat(Enumerable.Repeat(" pad", int.Parse(number) % 4)) };
        }

        private static void AddItem(Data data, int number, string tag, string text, string owner, string[] owners)
        {
            var id = "items/" + number;
            data.Items[id] = new Item { Id = id, Tag = tag, Text = text, Owner = owner, Owners = owners, Rank = (number * 7) % 13 };
        }

        public void Store(IDocumentSession session, Holder holder) => session.Store(holder, holder.Id);

        public void Store(IDocumentSession session, Item item)
        {
            if (Omitted.Contains(item.Id))
            {
                var entity = new ItemWithoutKeys { Tag = item.Tag, Text = item.Text };
                session.Store(entity, item.Id);
                session.Advanced.GetMetadataFor(entity)[Constants.Documents.Metadata.Collection] = "Items";
            }
            else
            {
                session.Store(item, item.Id);
            }
        }
    }

    private (DocumentStore Store, Data Data) CreateStore()
    {
        var store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax, includeScoresAndDistances: true));
        var data = Data.Create();
        using (var session = store.OpenSession())
        {
            foreach (var holder in data.Holders.Values)
                data.Store(session, holder);
            foreach (var item in data.Items.Values)
                data.Store(session, item);
            session.SaveChanges();
        }

        store.Maintenance.Send(new PutIndexesOperation(
            new IndexDefinition
            {
                Name = "Holders/ByLabel",
                Maps = { "from h in docs.Holders select new { h.Label, h.Text }" },
                Fields = { ["Text"] = new IndexFieldOptions { Indexing = FieldIndexing.Search } }
            },
            new IndexDefinition
            {
                Name = "Items/ByOwner",
                Maps = { "from i in docs.Items select new { i.Owner, i.Owners, i.Tag, i.Text, i.Rank }" },
                Fields = { ["Text"] = new IndexFieldOptions { Indexing = FieldIndexing.Search } }
            },
            new IndexDefinition
            {
                Name = "Items/ByOwnerExact",
                Maps = { "from i in docs.Items select new { i.Owner, i.Owners, i.Tag, i.Text, i.Rank }" },
                Fields =
                {
                    ["Text"] = new IndexFieldOptions { Indexing = FieldIndexing.Search },
                    ["Owner"] = new IndexFieldOptions { Indexing = FieldIndexing.Exact },
                    ["Owners"] = new IndexFieldOptions { Indexing = FieldIndexing.Exact }
                }
            },
            new IndexDefinition
            {
                Name = "Items/ByOwnerKey",
                Maps = { "from i in docs.Items select new { i.Owner, i.Owners, i.Tag, i.Text, i.Rank }" },
                Fields =
                {
                    ["Text"] = new IndexFieldOptions { Indexing = FieldIndexing.Search },
                    ["Owner"] = new IndexFieldOptions { Indexing = FieldIndexing.Key },
                    ["Owners"] = new IndexFieldOptions { Indexing = FieldIndexing.Key }
                }
            }));
        Indexes.WaitForIndexing(store);
        return (store, data);
    }

    private static double ScoreOf(IMetadataDictionary metadata) =>
        metadata.ContainsKey(Constants.Documents.Metadata.IndexScore) ? Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore], CultureInfo.InvariantCulture) : 0;

    private static List<Row> Fetch<T>(IDocumentStore store, string rql, out long total)
    {
        using var session = store.OpenSession(new SessionOptions { NoCaching = true });
        var documents = session.Advanced.RawQuery<T>(rql).Statistics(out var stats).ToList();
        total = stats.TotalResults;
        return documents.Select(x =>
        {
            var metadata = session.Advanced.GetMetadataFor(x);
            return new Row((string)metadata[Constants.Documents.Metadata.Id], ScoreOf(metadata));
        }).ToList();
    }

    private static string JoinRql(Form form, string sub, bool left, string boost = "", string tail = "order by score() " + Everything, string where = OuterWhere) =>
        $"{OuterRql} {(left ? "left join" : "join")} ({sub}) as i on {form.On}{boost} {where} {tail}";

    private static double Factor(string boost) => boost.Length == 0 ? 1 : double.Parse(boost.Substring(" boost ".Length), CultureInfo.InvariantCulture);

    private static List<FieldJoinExpectedRow> Expected(IDocumentStore store, Form form, string sub, bool left, double boost, string where = OuterWhere)
    {
        // the key of an outer row is its id
        var outer = Fetch<Holder>(store, $"{OuterRql} {where} order by score() {Everything}", out _)
            .Select(x => new FieldJoinOuter(x.Id, x.Score, [x.Id])).ToList();
        var inner = Fetch<Item>(store, $"{sub} order by score() {Everything}", out _)
            .Select(x => new FieldJoinInner(x.Id, x.Score, form.InnerKeys(ItemsOf(store)[x.Id]).ToList())).ToList();
        return FieldJoinModel.Expected(outer, inner, left, boost);
    }

    // the documents of the database as they are now
    private static Dictionary<string, Item> ItemsOf(IDocumentStore store)
    {
        using var session = store.OpenSession(new SessionOptions { NoCaching = true });
        return session.Advanced.RawQuery<Item>("from Items").ToList().ToDictionary(x => session.Advanced.GetDocumentId(x), x => x, StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertRows(string rql, List<FieldJoinExpectedRow> expected, List<Row> actual, long total)
    {
        var byId = expected.ToDictionary(x => x.Id);
        Assert.True(actual.Select(x => x.Id).Distinct().Count() == actual.Count, $"an outer row is returned more than once: {rql}");
        Assert.True(expected.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(actual.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal)),
            $"rows differ: {rql}{Environment.NewLine}expected {string.Join(", ", expected.Select(x => x.Id).OrderBy(x => x))}{Environment.NewLine}actual   {string.Join(", ", actual.Select(x => x.Id).OrderBy(x => x))}");
        Assert.Equal(expected.Count, total);
        foreach (var row in actual)
            Assert.True(Math.Abs(byId[row.Id].Score - row.Score) < 1e-3, $"{row.Id}: expected score {byId[row.Id].Score}, actual {row.Score}: {rql}");

        for (var i = 1; i < actual.Count; i++)
            Assert.True(actual[i - 1].Score >= actual[i].Score - 1e-6, $"position {i}: {actual[i - 1].Score} before {actual[i].Score}: {rql}");
    }

    private static void CheckJoin(IDocumentStore store, Form form, string sub, bool left, string boost, string where = OuterWhere)
    {
        var rql = JoinRql(form, sub, left, boost, where: where);
        AssertRows(rql, Expected(store, form, sub, left, Factor(boost), where), Fetch<Holder>(store, rql, out var total), total);
    }

    private static string[] Ids(IDocumentStore store, string rql) => Fetch<Holder>(store, rql, out _).Select(x => x.Id.Substring("holders/".Length)).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void EveryFormOfTheOnClauseMatchesTheModelForSemiAndLeftJoins()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            foreach (var form in Forms)
            foreach (var sub in Subs)
            foreach (var left in new[] { false, true })
            foreach (var boost in Boosts)
                CheckJoin(store, form, sub, left, boost);
        }
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData("Items/ByOwnerExact", " boost 0")]
    [InlineData("Items/ByOwnerExact", "")]
    [InlineData("Items/ByOwnerExact", " boost 2")]
    [InlineData("Items/ByOwnerKey", "")]
    public void AJoinedFieldThatKeepsTheCaseOrIsAKeyMatchesTheLowercasedIdLikeADefaultField(string innerIndex, string boost)
    {
        var (store, _) = CreateStore();
        using (store)
        {
            // item 3 holds HOLDERS/H02: the id of a row is compared lowercased, as the field of another outer index with the default indexing would be
            // the keys that differ in case only (item 3 holds HOLDERS/H04 and holders/h04) are one key: its best score counts once
            foreach (var form in Forms)
            foreach (var sub in Subs)
            foreach (var left in new[] { false, true })
                CheckJoin(store, form, sub.Replace("Items/ByOwner", innerIndex), left, boost);

            Assert.Contains("h02", Ids(store, JoinRql(FormOf("id=field"), InnerAll.Replace("Items/ByOwner", innerIndex), false)));
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ManyInnerEntriesPerOuterIdReturnEachOuterRowOnce()
    {
        var (store, data) = CreateStore();
        using (store)
        {
            // the owners of the items: h01 (items 1 2 10), h02 (3 in upper case), h05, h07, h13 x, h10, h99 (no outer row); the empty string and the null keys are no id
            Assert.Equal(["h01", "h02", "h05", "h07", "h10", "h13 x"], Ids(store, JoinRql(FormOf("id=field"), InnerAll, false)));

            // the lists add h02 h03 h04 h06 h07 h09 h11 h13 x h01
            Assert.Equal(["h01", "h02", "h03", "h04", "h06", "h07", "h09", "h11", "h13 x"], Ids(store, JoinRql(FormOf("id=array"), InnerAll, false)));

            // the entries that fail the sub-query do not count: item 3 is cold, so h02 is held by the list of item 1 only
            Assert.Equal(["h01", "h05", "h07", "h13 x"], Ids(store, JoinRql(FormOf("field=id"), InnerHot, false)));

            // a left join keeps every outer row whatever matches
            Assert.Equal(data.Holders.Count, Ids(store, JoinRql(FormOf("id=field"), InnerHot, true)).Length);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void NullMissingAndEmptyKeysOfTheJoinedDocumentsMatchNoOuterRow()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            // item 5 holds a null key, item 8 a missing one and item 6 the empty string: none of them is the id of a row
            foreach (var sub in new[] { "from index 'Items/ByOwner' where search(Text, 'zeta kappa')", "from index 'Items/ByOwner' where Tag = 'cold' and search(Text, 'gamma')" })
            foreach (var form in Forms)
            {
                CheckJoin(store, form, sub, false, "");
                CheckJoin(store, form, sub, true, "");
                Assert.Empty(Ids(store, JoinRql(form, sub, false)));
            }
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void IdsCompareWithoutCaseAndTheWholeIdIsTheKey()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            // item 3 holds HOLDERS/H02 and the id() of the row is holders/h02; the id with a blank is not split into words
            Assert.Equal(["h02"], Ids(store, JoinRql(FormOf("id=field"), "from index 'Items/ByOwner' where Tag = 'cold' and search(Text, 'alpha')", false)));
            Assert.Equal(["h13 x"], Ids(store, JoinRql(FormOf("id=field"), "from index 'Items/ByOwner' where search(Text, 'alpha beta') and Tag = 'hot' and Owner = 'holders/h13 x'", false)));
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheScoreOfARowIsTheOuterScorePlusBoostTimesTheSumOfTheBestScoreOfEveryDistinctMatchedKey()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            // an outer row has one key, its id, whatever the number of joined documents that hold it: the best score of them is added once
            var outer = Fetch<Holder>(store, $"{OuterRql} {OuterWhere} order by score() {Everything}", out _).ToDictionary(x => x.Id, x => x.Score);
            var inner = Fetch<Item>(store, $"{InnerAll} order by score() {Everything}", out _).ToDictionary(x => x.Id, x => x.Score);
            var form = FormOf("id=field");
            var bestH01 = new[] { "items/1", "items/2", "items/10" }.Max(x => inner[x]);

            foreach (var boost in new[] { 1d, 0.5, 2d })
            {
                var boostText = boost == 1 ? "" : " boost " + boost.ToString(CultureInfo.InvariantCulture);
                var rows = Fetch<Holder>(store, JoinRql(form, InnerAll, false, boostText), out _).ToDictionary(x => x.Id, x => x.Score);
                Assert.Equal(outer["holders/h01"] + boost * bestH01, rows["holders/h01"], 3);
                Assert.Equal(outer["holders/h05"] + boost * inner["items/4"], rows["holders/h05"], 3);
            }

            // a left join leaves the rows that match nothing at the outer score
            var left = Fetch<Holder>(store, JoinRql(form, InnerAll, true), out _).ToDictionary(x => x.Id, x => x.Score);
            Assert.Equal(outer["holders/h03"], left["holders/h03"], 3);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByScoreAndPagingOfAJoinMatchTheModel()
    {
        var (store, data) = CreateStore();
        using (store)
        {
            foreach (var form in new[] { FormOf("id=field"), FormOf("array=id") })
            foreach (var left in new[] { false, true })
            {
                var expected = Expected(store, form, InnerWords, left, 1);

                var rows = Fetch<Holder>(store, JoinRql(form, InnerWords, left, "", "order by score() " + Everything), out var total);
                Assert.Equal(expected.Count, total);
                Assert.Equal(expected.OrderByDescending(x => x.Score).Select(x => x.Score).ToList(), rows.Select(x => x.Score).ToList(), new ScoreComparer());

                var ordered = expected.OrderBy(x => data.Holders[x.Id].Label, StringComparer.Ordinal).Select(x => x.Id).ToList();
                foreach (var (skip, take) in new[] { (0, 3), (3, 4), (5, 100), (0, 1), (ordered.Count, 5), (Math.Max(ordered.Count - 1, 0), 5) })
                {
                    var page = Fetch<Holder>(store, JoinRql(form, InnerWords, left, "", $"order by h.Label limit {skip}, {take}"), out var pageTotal);
                    Assert.Equal(expected.Count, pageTotal);
                    Assert.Equal(ordered.Skip(skip).Take(take).ToList(), page.Select(x => x.Id).ToList());
                }

                var byScore = Fetch<Holder>(store, JoinRql(form, InnerWords, left, "", "order by score() limit 2, 3"), out var scoreTotal);
                Assert.Equal(expected.Count, scoreTotal);
                Assert.Equal(expected.OrderByDescending(x => x.Score).Skip(2).Take(3).Select(x => x.Score).ToList(), byScore.Select(x => x.Score).ToList(), new ScoreComparer());
            }
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ThePagesOfAJoinWithoutAnOrderAreRowsOfTheModel()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            foreach (var form in new[] { FormOf("id=field"), FormOf("array=id") })
            foreach (var left in new[] { false, true })
            foreach (var boost in new[] { "", " boost 0" })
            foreach (var (skip, take) in new[] { (0, 3), (2, 3), (0, 1), (4, 100) })
            {
                var expected = Expected(store, form, InnerHot, left, Factor(boost)).Select(x => x.Id).ToHashSet();
                var rql = JoinRql(form, InnerHot, left, boost, $"limit {skip}, {take}");
                var rows = Fetch<Holder>(store, rql, out var total);
                Assert.Equal(expected.Count, total);
                Assert.Equal(Math.Max(0, Math.Min(take, expected.Count - skip)), rows.Count);
                Assert.True(rows.All(x => expected.Contains(x.Id)), rql);
                Assert.Equal(rows.Count, rows.Select(x => x.Id).Distinct().Count());
            }
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByAFieldOfTheJoinedDocumentsOrdersTheRowsByTheirFirstJoinedDocument()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            var items = ItemsOf(store);
            foreach (var form in new[] { FormOf("id=field"), FormOf("field=id") })
            foreach (var descending in new[] { false, true })
            {
                var expected = Expected(store, form, InnerHot, false, 1).Select(x => x.Id).ToList();
                var inner = Fetch<Item>(store, $"{InnerHot} {Everything}", out _).Select(x => items[x.Id]).ToList();

                // the position of a row is the best position of the joined documents that hold its id (every joined document holds one id here)
                long PositionOf(string id)
                {
                    var ranks = inner.Where(x => string.Equals(x.Owner, id, StringComparison.OrdinalIgnoreCase)).Select(x => x.Rank).ToList();
                    return descending ? -ranks.Max() : ranks.Min();
                }

                var ordered = expected.OrderBy(PositionOf).ToList();
                var tail = $"order by i.Rank as long {(descending ? "desc" : "asc")}";
                Assert.Equal(ordered, Fetch<Holder>(store, JoinRql(form, InnerHot, false, "", $"{tail} {Everything}"), out _).Select(x => x.Id).ToList());

                foreach (var (skip, take) in new[] { (0, 2), (1, 2), (3, 100) })
                    Assert.Equal(ordered.Skip(skip).Take(take).ToList(), Fetch<Holder>(store, JoinRql(form, InnerHot, false, "", $"{tail} limit {skip}, {take}"), out _).Select(x => x.Id).ToList());
            }
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void MatchesOfTheJoinInTheWhereClauseSelectsTheRowsOfTheModel()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            foreach (var form in new[] { FormOf("id=field"), FormOf("id=array") })
            foreach (var sub in Subs)
            {
                var joined = Expected(store, form, sub, false, 1).Select(x => x.Id).ToHashSet();
                var common = Fetch<Holder>(store, $"{OuterRql} {OuterWhere} {Everything}", out _).Select(x => x.Id).ToHashSet();
                var byLabel = Fetch<Holder>(store, $"{OuterRql} where h.Label in ('label-h03', 'label-h12') {Everything}", out _).Select(x => x.Id).ToHashSet();

                Assert.Equal(joined.Concat(byLabel).Distinct().OrderBy(x => x).ToList(),
                    Fetch<Holder>(store, JoinRql(form, sub, false, "", Everything, "where matches(i) or h.Label in ('label-h03', 'label-h12')"), out _).Select(x => x.Id).OrderBy(x => x).ToList());
                Assert.Equal(joined.Intersect(common).OrderBy(x => x).ToList(),
                    Fetch<Holder>(store, JoinRql(form, sub, false, "", Everything, "where matches(i) and search(h.Text, 'common')"), out _).Select(x => x.Id).OrderBy(x => x).ToList());
                Assert.Equal(common.Except(joined).OrderBy(x => x).ToList(),
                    Fetch<Holder>(store, JoinRql(form, sub, true, "", Everything, "where search(h.Text, 'common') and not matches(i)"), out _).Select(x => x.Id).OrderBy(x => x).ToList());
            }
        }
    }

    private sealed class Projection
    {
        public string Label { get; set; }
    }

    private const string ByLabel = "order by Label ";

    private static string ProjectionRql(Form form, string sub, bool left, string select, string where = OuterWhere, string tail = Everything) =>
        JoinRql(form, sub, left, "", $"{ByLabel}select {select} {tail}", where);

    // the ids of the joined documents of every row, in the order of the rows
    private static List<(string Id, string[] Items)> FetchProjectedItems(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession(new SessionOptions { NoCaching = true });
        return session.Advanced.RawQuery<JObject>(rql).ToList().Select(x => ((string)x["@metadata"]["@id"], ((JArray)x["i"]).Select(item => (string)item["@metadata"]["@id"]).ToArray())).ToList();
    }

    // the joined documents of a row: the documents that the sub-query matches and whose key is the id of the row, in the order of their ids
    private static List<(string Id, string[] Items)> ExpectedItems(IDocumentStore store, Form form, string sub, bool left, string where = OuterWhere)
    {
        var rows = Expected(store, form, sub, left, 1, where);
        var matched = Fetch<Item>(store, $"{sub} order by score() {Everything}", out _).Select(x => x.Id).ToList();
        var items = ItemsOf(store);
        return rows
            .Select(row => (row.Id, Items: matched
                .Where(id => form.InnerKeys(items[id]).Any(key => key != null && FieldJoinModel.DefaultFold(key) == FieldJoinModel.DefaultFold(row.Id)))
                .OrderBy(id => id.ToLowerInvariant(), StringComparer.Ordinal).ToArray()))
            .OrderBy(x => "label-" + x.Id.Substring("holders/h".Length), StringComparer.Ordinal)
            .ToList();
    }

    private static void CheckProjectedItems(IDocumentStore store, Form form, string sub, bool left, string where = OuterWhere)
    {
        var rql = ProjectionRql(form, sub, left, "h.Label, i", where);
        var expected = ExpectedItems(store, form, sub, left, where);
        var actual = FetchProjectedItems(store, rql);
        Assert.True(expected.Select(x => x.Id).SequenceEqual(actual.Select(x => x.Id)), $"rows differ: {rql}{Environment.NewLine}expected {string.Join(", ", expected.Select(x => x.Id))}{Environment.NewLine}actual   {string.Join(", ", actual.Select(x => x.Id))}");
        for (var i = 0; i < expected.Count; i++)
            Assert.True(expected[i].Items.SequenceEqual(actual[i].Items), $"{expected[i].Id}: expected {string.Join(", ", expected[i].Items)}, actual {string.Join(", ", actual[i].Items)}: {rql}");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheJoinedAliasIsTheArrayOfTheDocumentsThatHoldTheIdOfTheRowForEveryFormAndSubQuery()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            foreach (var form in Forms)
            foreach (var sub in Subs)
            foreach (var left in new[] { false, true })
                CheckProjectedItems(store, form, sub, left);

            // h01 is held by items 1 2 and 10: in the order of their ids; the mixed case HOLDERS/H02 of item 3 is the id of h02
            var rows = FetchProjectedItems(store, ProjectionRql(FormOf("id=field"), InnerAll, false, "h.Label, i", where: "where h.Label = 'label-01' or h.Label = 'label-02'")).ToDictionary(x => x.Id, x => x.Items);
            Assert.Equal(["items/1", "items/10", "items/2"], rows["holders/h01"]);
            Assert.Equal(["items/3"], rows["holders/h02"]);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AFieldOfTheJoinedAliasIsTheArrayOfItsValuesAndTheAliasAloneIsTheDocuments()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            using var session = store.OpenSession(new SessionOptions { NoCaching = true });
            foreach (var form in Forms)
            foreach (var left in new[] { false, true })
            {
                var expected = ExpectedItems(store, form, InnerAll, left);
                var texts = ItemsOf(store);

                // select i.X: one array of the values of X for every row
                var values = session.Advanced.RawQuery<JObject>(ProjectionRql(form, InnerAll, left, "h.Label, i.Text")).ToList()
                    .Select(x => ((string)x["@metadata"]["@id"], x["Text"] is JArray array ? array.Select(t => (string)t).ToArray() : [])).ToList();
                Assert.Equal(expected.Select(x => x.Id), values.Select(x => x.Item1));
                for (var i = 0; i < expected.Count; i++)
                    Assert.Equal(expected[i].Items.Select(id => texts[id].Text), values[i].Item2);

                // select i: the documents themselves, the rows that have none contribute none
                var documents = session.Advanced.RawQuery<JObject>(ProjectionRql(form, InnerAll, left, "i")).ToList().Select(x => (string)x["@metadata"]["@id"]).ToList();
                Assert.Equal(expected.SelectMany(x => x.Items), documents);

                // select {...}: the alias is the array in the script
                var scripted = session.Advanced.RawQuery<JObject>(ProjectionRql(form, InnerAll, left, "{ Label: h.Label, Count: i.length, Ids: i.map(x => x.Tag) }")).ToList()
                    .Select(x => ((string)x["@metadata"]["@id"], (int)x["Count"], ((JArray)x["Ids"]).Select(t => (string)t).ToArray())).ToList();
                Assert.Equal(expected.Select(x => (x.Id, x.Items.Length, x.Items.Select(id => texts[id].Tag).ToArray())), scripted);
            }
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ARowThatHasNoJoinedDocumentIsAnEmptyArrayInALeftJoinAndIsNotReturnedInASemiJoin()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            var form = FormOf("id=field");
            var where = "where h.Label = 'label-03' or h.Label = 'label-01'";
            var semi = FetchProjectedItems(store, ProjectionRql(form, InnerAll, false, "h.Label, i", where));
            Assert.Equal(["holders/h01"], semi.Select(x => x.Id));

            var left = FetchProjectedItems(store, ProjectionRql(form, InnerAll, true, "h.Label, i", where));
            Assert.Equal(["holders/h01", "holders/h03"], left.Select(x => x.Id));
            Assert.Empty(left.Single(x => x.Id == "holders/h03").Items);

            // the sub-query decides which documents of a row count: item 2 only
            var hot = FetchProjectedItems(store, ProjectionRql(form, "from index 'Items/ByOwner' where search(Text, 'gamma')", false, "h.Label, i", where));
            Assert.Equal("holders/h01", Assert.Single(hot).Id);
            Assert.Equal(["items/2"], hot[0].Items);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OnlyTheReturnedPageLooksUpTheJoinedDocumentsAndPagesAreSlicesOfTheModel()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            foreach (var form in Forms)
            foreach (var left in new[] { false, true })
            {
                var expected = ExpectedItems(store, form, InnerAll, left);
                foreach (var (skip, take) in new[] { (0, 1), (1, 2), (2, 3), (expected.Count - 1, 5), (expected.Count, 5), (0, expected.Count) })
                {
                    var rql = ProjectionRql(form, InnerAll, left, "h.Label, i", tail: $"limit {skip}, {take}");
                    var actual = FetchProjectedItems(store, rql);
                    var page = expected.Skip(skip).Take(take).ToList();
                    Assert.True(page.Select(x => x.Id).SequenceEqual(actual.Select(x => x.Id)), $"rows differ: {rql}");
                    for (var i = 0; i < page.Count; i++)
                        Assert.True(page[i].Items.SequenceEqual(actual[i].Items), $"{page[i].Id}: {rql}");
                }
            }
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheJoinedDocumentsOfAKeyFieldAreFoundAndOfAFieldThatKeepsTheCaseAreRefused()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            foreach (var form in Forms)
            foreach (var left in new[] { false, true })
                CheckProjectedItems(store, form, InnerAll.Replace("Items/ByOwner", "Items/ByOwnerKey"), left);

            using var session = store.OpenSession(new SessionOptions { NoCaching = true });
            var exception = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<JObject>(ProjectionRql(FormOf("id=field"), InnerAll.Replace("Items/ByOwner", "Items/ByOwnerExact"), false, "h.Label, i")).ToList());
            Assert.IsType<InvalidQueryException>(exception);
            Assert.Contains("keeps the case of its values", exception.Message);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ChangesOfTheJoinedDocumentsBetweenQueriesAreSeenByTheProjection()
    {
        var (store, data) = CreateStore();
        using (store)
        {
            void CheckAll()
            {
                Indexes.WaitForIndexing(store);
                foreach (var form in Forms)
                foreach (var left in new[] { false, true })
                {
                    CheckProjectedItems(store, form, InnerAll, left);
                    CheckProjectedItems(store, form, InnerHot, left);
                }
            }

            void Change(Action<IDocumentSession> action)
            {
                using var session = store.OpenSession();
                action(session);
                session.SaveChanges();
            }

            CheckAll();

            data.Items["items/1"].Owner = "holders/h08";
            data.Items["items/1"].Owners = ["holders/h08", "holders/h12"];
            data.Items["items/10"].Tag = "cold";
            Change(s =>
            {
                data.Store(s, data.Items["items/1"]);
                data.Store(s, data.Items["items/10"]);
            });
            CheckAll();

            data.Items.Remove("items/2");
            data.Items["items/13"] = new Item { Id = "items/13", Tag = "hot", Text = "alpha beta", Owner = "holders/h01", Owners = ["holders/h01", "holders/h03"] };
            data.Holders.Remove("holders/h02");
            Change(s =>
            {
                s.Delete("items/2");
                data.Store(s, data.Items["items/13"]);
                s.Delete("holders/h02");
            });
            CheckAll();

            // the text of a joined document changes: the projection reads the document as it is now
            data.Items["items/13"].Text = "omega";
            Change(s => data.Store(s, data.Items["items/13"]));
            CheckAll();
            Assert.Contains("omega", ProjectedTexts(store, "holders/h01"));

            foreach (var id in data.Items.Keys.ToList())
                data.Items.Remove(id);
            Change(s =>
            {
                for (var i = 1; i <= 13; i++)
                    s.Delete("items/" + i);
            });
            CheckAll();
            Assert.Empty(FetchProjectedItems(store, ProjectionRql(FormOf("id=field"), InnerAll, false, "h.Label, i")));
            Assert.All(FetchProjectedItems(store, ProjectionRql(FormOf("id=field"), InnerAll, true, "h.Label, i")), x => Assert.Empty(x.Items));
        }
    }

    private static string[] ProjectedTexts(IDocumentStore store, string holderId)
    {
        using var session = store.OpenSession(new SessionOptions { NoCaching = true });
        return session.Advanced.RawQuery<JObject>(ProjectionRql(FormOf("id=field"), InnerAll, false, "h.Label, i.Text", where: $"where id() = '{holderId}'")).ToList().SelectMany(x => ((JArray)x["Text"]).Select(t => (string)t)).ToArray();
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void FieldsOfTheMainDocumentCanBeProjectedWithoutTheJoinedAlias()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            var form = FormOf("id=field");
            using var session = store.OpenSession(new SessionOptions { NoCaching = true });
            var expected = Ids(store, JoinRql(form, InnerAll, false)).Select(x => x.Substring(1, 2)).ToList();
            var projected = session.Advanced.RawQuery<Projection>(JoinRql(form, InnerAll, false, "", "select h.Label " + Everything)).ToList().Select(x => x.Label.Substring("label-".Length)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(expected, projected);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AnAliasThatOtherJoinsAreChainedThroughCannotBeProjected()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            using var session = store.OpenSession(new SessionOptions { NoCaching = true });
            var rql = $"{OuterRql} join ({InnerAll}) as i on id(h) = i.Owner join (from index 'Holders/ByLabel') as k on i.Label = id(k) select k.Label {Everything}";
            var exception = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<JObject>(rql).ToList());
            Assert.IsType<InvalidQueryException>(exception);
            Assert.Contains("cannot be used to join other documents", exception.Message);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AFieldOfTheJoinedDocumentsThatIsNotIndexedIsRejected()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            using var session = store.OpenSession();
            var exception = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<Holder>($"{OuterRql} join ({InnerAll}) as i on id(h) = i.Nothing {Everything}").ToList());
            Assert.Contains("'Nothing'", exception.Message);
            Assert.Contains("not indexed", exception.Message);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void WhereClausesOfTheMainQueryAreCombinedWithTheJoinLikeThePlainJoin()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            var wheres = new[]
            {
                "where h.Label = 'label-h01'",
                "where h.Label in ('label-h01', 'label-h02', 'label-h07', 'label-h08')",
                "where search(h.Text, 'pad') and h.Label != 'label-h05'",
                "where search(h.Text, 'pad') or h.Label = 'label-h07'",
                "where search(h.Text, 'common') and not h.Label = 'label-h01'",
                "where h.Label between 'label-h02' and 'label-h09'",
                "where startsWith(h.Label, 'label-h1')"
            };

            foreach (var form in new[] { FormOf("id=field"), FormOf("id=array"), FormOf("field=id") })
            foreach (var where in wheres)
            foreach (var left in new[] { false, true })
            foreach (var boost in new[] { "", " boost 0" })
                CheckJoin(store, form, InnerAll, left, boost, where);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ChangesOfInnerAndOuterDocumentsBetweenQueriesAreSeenByTheNextQuery()
    {
        var (store, data) = CreateStore();
        using (store)
        {
            void CheckAll()
            {
                Indexes.WaitForIndexing(store);
                foreach (var form in Forms)
                foreach (var sub in Subs)
                foreach (var left in new[] { false, true })
                    CheckJoin(store, form, sub, left, "");
            }

            void Change(Action<IDocumentSession> action)
            {
                using var session = store.OpenSession();
                action(session);
                session.SaveChanges();
            }

            CheckAll();

            // an inner entry points to another outer row and another stops pointing to one
            data.Items["items/1"].Owner = "holders/h08";
            data.Items["items/1"].Owners = ["holders/h08", "holders/h12"];
            data.Items["items/4"].Owner = "nobody";
            data.Items["items/4"].Owners = ["nobody"];
            Change(s =>
            {
                data.Store(s, data.Items["items/1"]);
                data.Store(s, data.Items["items/4"]);
            });
            CheckAll();

            // an inner entry stops matching the sub-query and another starts to
            data.Items["items/3"].Tag = "hot";
            data.Items["items/2"].Tag = "cold";
            data.Items["items/2"].Text = "omega";
            Change(s =>
            {
                data.Store(s, data.Items["items/3"]);
                data.Store(s, data.Items["items/2"]);
            });
            CheckAll();

            // an inner entry is deleted, one is created, an outer row is deleted and one is created
            data.Items.Remove("items/10");
            data.Items["items/13"] = new Item { Id = "items/13", Tag = "hot", Text = "alpha beta", Owner = "holders/h14", Owners = ["holders/h14", "holders/h03"] };
            data.Holders.Remove("holders/h08");
            data.Holders["holders/h14"] = new Holder { Id = "holders/h14", Label = "label-h14", Text = "common common" };
            Change(s =>
            {
                s.Delete("items/10");
                data.Store(s, data.Items["items/13"]);
                s.Delete("holders/h08");
                data.Store(s, data.Holders["holders/h14"]);
            });
            CheckAll();

            // an outer row is deleted and created again with the same id, an inner entry loses its keys
            Change(s => s.Delete("holders/h02"));
            CheckAll();
            data.Items["items/9"].Owner = null;
            data.Items["items/9"].Owners = null;
            Change(s =>
            {
                data.Store(s, data.Holders["holders/h02"]);
                data.Store(s, data.Items["items/9"]);
            });
            CheckAll();

            // every inner entry is deleted: the semi join is empty and the left join keeps every row
            foreach (var id in data.Items.Keys.ToList())
                data.Items.Remove(id);
            Change(s =>
            {
                for (var i = 1; i <= 13; i++)
                    s.Delete("items/" + i);
            });
            CheckAll();
            Assert.Empty(Fetch<Holder>(store, JoinRql(FormOf("id=field"), InnerAll, false), out _));
        }
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData("id(h) = id(i)", "id(h)")]
    [InlineData("id(i) = id(h)", "id(h)")]
    [InlineData("id(i) = i.Owner", "main document")]
    [InlineData("i.Owner = id(i)", "main document")]
    [InlineData("id(x) = i.Owner", "main document")]
    [InlineData("id() = i.Owner", "main document")]
    [InlineData("id(h, i) = i.Owner", "main document")]
    [InlineData("id(h) = h.Label", "id(h)")]
    [InlineData("id(h) = i.Owner and h.Label = i.Tag", "only equality")]
    [InlineData("h.Label = i.Tag and id(h) = i.Owner", "only equality")]
    [InlineData("id(h) = i.Owner and id(h) = i.Owners", "only equality")]
    [InlineData("id(h) > i.Owner", "main document")]
    [InlineData("id(h) = 'holders/h01'", "id(h)")]
    public void AnIdOfTheMainDocumentThatIsNotMatchedAgainstAFieldOfTheJoinedDocumentsIsRejectedWithAMessage(string on, string expectedPart)
    {
        var (store, _) = CreateStore();
        using (store)
        {
            using var session = store.OpenSession();
            var exception = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<Holder>($"{OuterRql} join ({InnerAll}) as i on {on} {Everything}").ToList());
            Assert.Contains(expectedPart, exception.Message);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheIdOfTheMainDocumentCannotBeJoinedWhenTheMainQueryIsOnACollection()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            using var session = store.OpenSession();
            var exception = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<Holder>($"from Holders as h join ({InnerAll}) as i on id(h) = i.Owner {Everything}").ToList());
            Assert.IsType<InvalidQueryException>(exception);
            Assert.Contains("JOIN is supported only for queries on an index", exception.Message);
        }
    }

    private sealed class ScoreComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-3;

        public int GetHashCode(double obj) => 0;
    }
}
