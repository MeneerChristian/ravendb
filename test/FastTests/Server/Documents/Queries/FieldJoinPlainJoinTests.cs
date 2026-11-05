using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Queries;

/// <summary>
/// The join of the server on a field of the joined index (and on an id) as a semi or left join over two indexes, pinned against <see cref="FieldJoinModel"/>.
/// A later stage that answers the same queries from elsewhere has to reproduce what these tests observe.
/// </summary>
public class FieldJoinPlainJoinTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Everything = "limit 0, 1000000";
    private const string OuterRql = "from index 'Holders/ByKey' as h";
    private const string OuterWhere = "where search(h.Text, 'common')";
    private const string InnerAll = "from index 'Items/ByCode'";
    private const string InnerHot = "from index 'Items/ByCode' where Tag = 'hot'";
    private const string InnerWords = "from index 'Items/ByCode' where search(Text, 'alpha beta')";

    private static readonly string[] Subs = [InnerAll, InnerHot, InnerWords];
    private static readonly string[] Boosts = ["", " boost 0", " boost 0.5", " boost 2"];

    private sealed record Row(string Id, double Score);

    private sealed class Holder
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Text { get; set; }
        public string Key { get; set; }
        public string[] Keys { get; set; }
        public string Ref { get; set; }
        public string[] Refs { get; set; }
    }

    private sealed class HolderWithoutKeys
    {
        public string Label { get; set; }
        public string Text { get; set; }
    }

    private sealed class Item
    {
        public string Id { get; set; }
        public string Tag { get; set; }
        public string Text { get; set; }
        public string Code { get; set; }
        public string[] Codes { get; set; }
    }

    private sealed class ItemWithoutKeys
    {
        public string Tag { get; set; }
        public string Text { get; set; }
    }

    private sealed record Form(string Name, string On, Func<Holder, IEnumerable<string>> OuterKeys, Func<Item, IEnumerable<string>> InnerKeys);

    private static readonly Form[] Forms =
    [
        new("field=field", "h.Key = i.Code", x => [x.Key], x => [x.Code]),
        new("array=field", "h.Keys = i.Code", x => x.Keys ?? [], x => [x.Code]),
        new("field=array", "h.Key = i.Codes", x => [x.Key], x => x.Codes ?? []),
        new("array=array", "h.Keys = i.Codes", x => x.Keys ?? [], x => x.Codes ?? []),
        new("field=id", "h.Ref = id(i)", x => [x.Ref], x => [x.Id]),
        new("array=id", "h.Refs = id(i)", x => x.Refs ?? [], x => [x.Id]),
        new("reversed", "i.Code = h.Key", x => [x.Key], x => [x.Code])
    ];

    private static Form FormOf(string name) => Forms.Single(x => x.Name == name);

    /// <summary>The documents the model is computed from; the database holds the same.</summary>
    private sealed class Data
    {
        // documents stored without the key properties at all, as opposed to a null key
        public HashSet<string> Omitted { get; } = ["holders/h07", "items/8"];

        public Dictionary<string, Holder> Holders { get; } = new();
        public Dictionary<string, Item> Items { get; } = new();

        public static Data Create()
        {
            var data = new Data();
            AddHolder(data, "h01", "k1", ["k1"], "items/1", ["items/1"]);
            AddHolder(data, "h02", "k1", ["k1", "k2"], "items/1", ["items/1", "items/2", "items/4"]);
            AddHolder(data, "h03", "k1", ["k2", "k3"], "ITEMS/4", ["items/4", "items/5"]);
            AddHolder(data, "h04", "k2", ["k1", "k2", "k3"], "items/9999", ["items/3", "items/4", "items/5"]);
            AddHolder(data, "h05", "lonely", ["lonely"], "", []);
            AddHolder(data, "h06", null, null, null, null);
            AddHolder(data, "h07", null, null, null, null);
            AddHolder(data, "h08", "", [""], "items/9", ["items/9"]);
            AddHolder(data, "h09", "K2", ["K3"], "items/10", ["items/10", "items/10"]);
            AddHolder(data, "h10", "k4", ["k4", "orphan"], "items/6", ["items/6", "items/7"]);
            AddHolder(data, "h11", "dup", ["dup", "dup"], "items/11", ["items/11"]);
            AddHolder(data, "h12", "k3", [], "items/8", ["items/8", "items/2"]);
            AddHolder(data, "h13", "orphan", ["k1", "K1"], "items/1", ["items/1", "ITEMS/1"]);
            AddHolder(data, "h14", "k1", ["k1"], "items/2", ["items/2"]);

            AddItem(data, 1, "hot", "alpha", "k1", ["k1", "k2"]);
            AddItem(data, 2, "hot", "alpha beta gamma delta", "k1", ["k1"]);
            AddItem(data, 3, "cold", "alpha alpha", "k1", ["k3"]);
            AddItem(data, 4, "hot", "beta", "k2", ["k2", "k3"]);
            AddItem(data, 5, "cold", "gamma", "k3", []);
            AddItem(data, 6, "hot", "delta delta", "orphan", ["orphan", "k4"]);
            AddItem(data, 7, "hot", "epsilon", null, null);
            AddItem(data, 8, "hot", "zeta", null, null);
            AddItem(data, 9, "hot", "eta", "", [""]);
            AddItem(data, 10, "hot", "theta", "K1", ["K1", "K2"]);
            AddItem(data, 11, "hot", "iota", "dup", ["dup", "dup"]);
            return data;
        }

        private static void AddHolder(Data data, string number, string key, string[] keys, string reference, string[] references)
        {
            var id = "holders/" + number;
            data.Holders[id] = new Holder { Id = id, Label = "label-" + number, Text = "common" + string.Concat(Enumerable.Repeat(" pad", int.Parse(number.Substring(1)) % 4)), Key = key, Keys = keys, Ref = reference, Refs = references };
        }

        private static void AddItem(Data data, int number, string tag, string text, string code, string[] codes)
        {
            var id = "items/" + number;
            data.Items[id] = new Item { Id = id, Tag = tag, Text = text, Code = code, Codes = codes };
        }

        public void Store(IDocumentSession session, Holder holder)
        {
            if (Omitted.Contains(holder.Id))
            {
                var entity = new HolderWithoutKeys { Label = holder.Label, Text = holder.Text };
                session.Store(entity, holder.Id);
                session.Advanced.GetMetadataFor(entity)[Constants.Documents.Metadata.Collection] = "Holders";
            }
            else
            {
                session.Store(holder, holder.Id);
            }
        }

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
                Name = "Holders/ByKey",
                Maps = { "from h in docs.Holders select new { h.Key, h.Keys, h.Ref, h.Refs, h.Label, h.Text }" },
                Fields = { ["Text"] = new IndexFieldOptions { Indexing = FieldIndexing.Search } }
            },
            new IndexDefinition
            {
                Name = "Items/ByCode",
                Maps = { "from i in docs.Items select new { i.Code, i.Codes, i.Tag, i.Text }" },
                Fields = { ["Text"] = new IndexFieldOptions { Indexing = FieldIndexing.Search } }
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

    private static string JoinRql(Form form, string sub, bool left, string boost = "", string tail = "order by score() " + Everything) =>
        $"{OuterRql} {(left ? "left join" : "join")} ({sub}) as i on {form.On}{boost} {OuterWhere} {tail}";

    private static double Factor(string boost) => boost.Length == 0 ? 1 : double.Parse(boost.Substring(" boost ".Length), CultureInfo.InvariantCulture);

    /// <summary>The model's answer to a join, from the outer query alone and the sub-query alone (their scores are the inputs of the model).</summary>
    private static List<FieldJoinExpectedRow> Expected(IDocumentStore store, Data data, Form form, string sub, bool left, double boost)
    {
        var outer = Fetch<Holder>(store, $"{OuterRql} {OuterWhere} order by score() {Everything}", out _)
            .Select(x => new FieldJoinOuter(x.Id, x.Score, form.OuterKeys(data.Holders[x.Id]).ToList())).ToList();
        var inner = Fetch<Item>(store, $"{sub} order by score() {Everything}", out _)
            .Select(x => new FieldJoinInner(x.Id, x.Score, form.InnerKeys(data.Items[x.Id]).ToList())).ToList();
        return FieldJoinModel.Expected(outer, inner, left, boost);
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

    private static void CheckJoin(IDocumentStore store, Data data, Form form, string sub, bool left, string boost)
    {
        var rql = JoinRql(form, sub, left, boost);
        AssertRows(rql, Expected(store, data, form, sub, left, Factor(boost)), Fetch<Holder>(store, rql, out var total), total);
    }

    private static string[] Ids(IDocumentStore store, string rql) => Fetch<Holder>(store, rql, out _).Select(x => x.Id.Substring("holders/".Length)).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void EveryFormOfTheOnClauseMatchesTheModelForSemiAndLeftJoins()
    {
        var (store, data) = CreateStore();
        using (store)
        {
            foreach (var form in Forms)
            foreach (var sub in Subs)
            foreach (var left in new[] { false, true })
            foreach (var boost in Boosts)
                CheckJoin(store, data, form, sub, left, boost);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ManyInnerEntriesAndManyOuterRowsPerKeyReturnEachOuterRowOnce()
    {
        var (store, data) = CreateStore();
        using (store)
        {
            // the codes of the items are k1 (items 1 2 3 10), k2, k3, orphan, "" and dup; h05 (lonely), h10 (k4) and the null keys have no inner entry
            Assert.Equal(["h01", "h02", "h03", "h04", "h08", "h09", "h11", "h12", "h13", "h14"], Ids(store, JoinRql(FormOf("field=field"), InnerAll, false)));

            // an inner entry per key is enough, and the entries that fail the sub-query do not count: only cold entries 3 (k1) and 5 (k3) are left out
            Assert.Equal(["h01", "h02", "h03", "h04", "h08", "h09", "h11", "h13", "h14"], Ids(store, JoinRql(FormOf("field=field"), InnerHot, false)));

            // one key of the sub-query carries many outer rows and many inner entries: a hub
            // (items 1 2 10 hold k1, items 1 and 10 also k2)
            Assert.Equal(["h01", "h02", "h03", "h04", "h13", "h14"], Ids(store, JoinRql(FormOf("array=array"), "from index 'Items/ByCode' where Tag = 'hot' and Code = 'k1'", false)));

            // a left join keeps every outer row whatever matches
            Assert.Equal(data.Holders.Count, Ids(store, JoinRql(FormOf("field=field"), InnerHot, true)).Length);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ArraysMatchWhenOneKeyMatchesOnEitherSideAndTheJoinedDocumentIsDeduplicatedPerKey()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            // outer array, inner single: h09 matches through K3, h10 through orphan, h11 through a repeated dup, h12 holds no key in its list
            Assert.Equal(["h01", "h02", "h03", "h04", "h08", "h09", "h10", "h11", "h13", "h14"], Ids(store, JoinRql(FormOf("array=field"), InnerAll, false)));

            // outer single, inner array: h10 matches through k4 of the codes of item 6, h12 through k3 of item 4
            Assert.Equal(["h01", "h02", "h03", "h04", "h08", "h09", "h10", "h11", "h12", "h13", "h14"], Ids(store, JoinRql(FormOf("field=array"), InnerAll, false)));

            // both arrays
            Assert.Equal(["h01", "h02", "h03", "h04", "h08", "h09", "h10", "h11", "h13", "h14"], Ids(store, JoinRql(FormOf("array=array"), InnerAll, false)));

            // an empty list holds no key, whatever the single key Key of h12 says
            Assert.DoesNotContain("h12", Ids(store, JoinRql(FormOf("array=field"), InnerAll, false)));
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void NullAndMissingKeysAreNeverKeysOnEitherSide()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            foreach (var form in Forms)
            foreach (var left in new[] { false, true })
            foreach (var sub in Subs)
            {
                var rows = Fetch<Holder>(store, JoinRql(form, sub, left), out _);
                if (left)
                {
                    // the rows stay, but the score is the score of the outer query alone
                    var outerOnly = Fetch<Holder>(store, $"{OuterRql} {OuterWhere} order by score() {Everything}", out _).ToDictionary(x => x.Id, x => x.Score);
                    foreach (var id in new[] { "holders/h06", "holders/h07" })
                        Assert.Equal(outerOnly[id], rows.Single(x => x.Id == id).Score, 3);
                }
                else
                {
                    // the items 7 and 8 hold a null and a missing key and match the sub-query "all", yet a null or missing key is no key
                    Assert.DoesNotContain(rows, x => x.Id is "holders/h06" or "holders/h07");
                }
            }
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheEmptyStringIsAKeyAndAnEmptyKeyOnTheOuterSideMatchesAnEmptyKeyOnTheInnerSide()
    {
        var (store, data) = CreateStore();
        using (store)
        {
            // h08 holds "" and item 9 holds "": the plain join matches them (the empty string is stored as its own term, not as the null marker)
            foreach (var name in new[] { "field=field", "array=array", "array=field", "field=array" })
                Assert.Contains("h08", Ids(store, JoinRql(FormOf(name), InnerAll, false)));

            // h05 has Ref "" and no item has the id "": no match; an empty Ref is not the null of h06
            Assert.DoesNotContain("h05", Ids(store, JoinRql(FormOf("field=id"), InnerAll, false)));

            // without the item 9 the "" key has no inner entry
            Assert.DoesNotContain("h08", Ids(store, JoinRql(FormOf("field=field"), InnerWords, false)));

            // the empty string is not a null or a missing key either: the items 7 and 8 hold those and match no outer row, not even h06, h07 and h08
            var sub = "from index 'Items/ByCode' where search(Text, 'epsilon zeta')";
            foreach (var form in Forms.Where(x => x.Name != "field=id" && x.Name != "array=id"))
            {
                CheckJoin(store, data, form, sub, false, "");
                Assert.Empty(Ids(store, JoinRql(form, sub, false)));
            }
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void KeysOfTheDefaultAnalyzerCompareWithoutCase()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            // h09 holds K2 and h04 holds k2, the code of item 4
            Assert.Equal(["h04", "h09"], Ids(store, JoinRql(FormOf("field=field"), "from index 'Items/ByCode' where Code = 'k2'", false)));

            // the id of h03 is written ITEMS/4 and id(i) is items/4
            Assert.Contains("h03", Ids(store, JoinRql(FormOf("field=id"), InnerAll, false)));

            // the item 10 alone holds K1: the outer rows of k1 match it
            Assert.Equal(["h01", "h02", "h03", "h14"], Ids(store, JoinRql(FormOf("field=field"), "from index 'Items/ByCode' where search(Text, 'theta')", false)));
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheScoreOfARowIsTheOuterScorePlusBoostTimesTheSumOfTheBestScoreOfEveryDistinctMatchedKey()
    {
        var (store, data) = CreateStore();
        using (store)
        {
            var form = FormOf("array=field");
            var outer = Fetch<Holder>(store, $"{OuterRql} {OuterWhere} order by score() {Everything}", out _).ToDictionary(x => x.Id, x => x.Score);
            var inner = Fetch<Item>(store, $"{InnerWords} order by score() {Everything}", out _).ToDictionary(x => x.Id, x => x.Score);

            // k1 is held by the matched items 1 2 3, k2 by item 4: the best of k1 is not the only score that could be taken
            var scoresOfK1 = new[] { "items/1", "items/2", "items/3" }.Select(x => inner[x]).ToList();
            Assert.True(scoresOfK1.Distinct().Count() > 1, "the entries of one key score differently");
            var bestK1 = scoresOfK1.Max();
            var bestK2 = inner["items/4"];

            foreach (var boost in new[] { 1d, 0.5, 2d })
            {
                var boostText = boost == 1 ? "" : " boost " + boost.ToString(CultureInfo.InvariantCulture);
                var rows = Fetch<Holder>(store, JoinRql(form, InnerWords, false, boostText), out _).ToDictionary(x => x.Id, x => x.Score);

                // h01 k1: one key; h02 k1 k2: two keys; h04 k1 k2 k3: k3 is held by no matched item; h13 k1 K1: one distinct key
                Assert.Equal(outer["holders/h01"] + boost * bestK1, rows["holders/h01"], 3);
                Assert.Equal(outer["holders/h02"] + boost * (bestK1 + bestK2), rows["holders/h02"], 3);
                Assert.Equal(outer["holders/h04"] + boost * (bestK1 + bestK2), rows["holders/h04"], 3);
                Assert.Equal(outer["holders/h13"] + boost * bestK1, rows["holders/h13"], 3);
                Assert.Equal(outer["holders/h03"] + boost * bestK2, rows["holders/h03"], 3);
            }

            // a left join adds the score to the rows that match and leaves the others at the outer score
            var left = Fetch<Holder>(store, JoinRql(form, InnerWords, true), out _).ToDictionary(x => x.Id, x => x.Score);
            Assert.Equal(data.Holders.Count, left.Count);
            Assert.Equal(outer["holders/h05"], left["holders/h05"], 3);
            Assert.Equal(outer["holders/h02"] + bestK1 + bestK2, left["holders/h02"], 3);

            // boost 0 leaves the outer score
            Assert.Equal(outer["holders/h02"], Fetch<Holder>(store, JoinRql(form, InnerWords, false, " boost 0"), out _).Single(x => x.Id == "holders/h02").Score, 3);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void OrderByScoreAndPagingOfAJoinMatchTheModel()
    {
        var (store, data) = CreateStore();
        using (store)
        {
            foreach (var form in new[] { FormOf("field=field"), FormOf("array=array"), FormOf("array=id") })
            foreach (var left in new[] { false, true })
            {
                var expected = Expected(store, data, form, InnerWords, left, 1);

                // the scores in the order of the model, whatever the order of rows with the same score
                var rql = JoinRql(form, InnerWords, left, "", "order by score() " + Everything);
                var rows = Fetch<Holder>(store, rql, out var total);
                Assert.Equal(expected.Count, total);
                Assert.Equal(expected.OrderByDescending(x => x.Score).Select(x => x.Score).ToList(), rows.Select(x => x.Score).ToList(), new ScoreComparer());

                // a page by a field of the outer row is exactly the slice of the model
                var ordered = expected.OrderBy(x => data.Holders[x.Id].Label, StringComparer.Ordinal).Select(x => x.Id).ToList();
                foreach (var (skip, take) in new[] { (0, 3), (3, 4), (5, 100), (0, 1), (ordered.Count, 5), (ordered.Count - 1, 5) })
                {
                    var page = Fetch<Holder>(store, JoinRql(form, InnerWords, left, "", $"order by h.Label limit {skip}, {take}"), out var pageTotal);
                    Assert.Equal(expected.Count, pageTotal);
                    Assert.Equal(ordered.Skip(skip).Take(take).ToList(), page.Select(x => x.Id).ToList());
                }

                // a page by the score: the scores of the slice and no row twice
                var byScore = Fetch<Holder>(store, JoinRql(form, InnerWords, left, "", "order by score() limit 2, 3"), out var scoreTotal);
                Assert.Equal(expected.Count, scoreTotal);
                Assert.Equal(expected.OrderByDescending(x => x.Score).Skip(2).Take(3).Select(x => x.Score).ToList(), byScore.Select(x => x.Score).ToList(), new ScoreComparer());
            }
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ChangesOfInnerAndOuterDocumentsBetweenQueriesAreSeenByTheNextQuery()
    {
        var (store, data) = CreateStore();
        using (store)
        {
            var checks = new[] { "field=field", "array=array", "field=array", "field=id", "array=id" };

            void CheckAll()
            {
                Indexes.WaitForIndexing(store);
                foreach (var name in checks)
                foreach (var sub in Subs)
                foreach (var left in new[] { false, true })
                    CheckJoin(store, data, FormOf(name), sub, left, "");
            }

            void Change(Action<IDocumentSession> action)
            {
                using var session = store.OpenSession();
                action(session);
                session.SaveChanges();
            }

            CheckAll();

            // an inner entry changes its key to a key that outer rows hold, and another changes it away from one
            data.Items["items/6"].Code = "k1";
            data.Items["items/6"].Codes = ["k3"];
            data.Items["items/4"].Code = "elsewhere";
            data.Items["items/4"].Codes = ["elsewhere"];
            Change(s =>
            {
                data.Store(s, data.Items["items/6"]);
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

            // an inner entry is deleted, one is created, and an outer row changes and another is deleted
            data.Items.Remove("items/1");
            data.Items["items/12"] = new Item { Id = "items/12", Tag = "hot", Text = "alpha beta", Code = "lonely", Codes = ["lonely", "k2"] };
            data.Holders["holders/h05"].Keys = ["k2"];
            data.Holders["holders/h05"].Refs = ["items/12"];
            data.Holders.Remove("holders/h01");
            data.Holders["holders/h15"] = new Holder { Id = "holders/h15", Label = "label-h15", Text = "common common", Key = "k3", Keys = ["k3", "lonely"], Ref = "items/12", Refs = ["items/12"] };
            Change(s =>
            {
                s.Delete("items/1");
                data.Store(s, data.Items["items/12"]);
                data.Store(s, data.Holders["holders/h05"]);
                s.Delete("holders/h01");
                data.Store(s, data.Holders["holders/h15"]);
            });
            CheckAll();

            // an outer row loses its keys, a deleted inner entry is created again with the same id and another key
            data.Holders["holders/h02"].Key = null;
            data.Holders["holders/h02"].Keys = null;
            data.Holders["holders/h02"].Ref = null;
            data.Holders["holders/h02"].Refs = null;
            data.Items["items/1"] = new Item { Id = "items/1", Tag = "hot", Text = "alpha", Code = "k2", Codes = ["k2"] };
            Change(s =>
            {
                data.Store(s, data.Holders["holders/h02"]);
                data.Store(s, data.Items["items/1"]);
            });
            CheckAll();

            // every inner entry is deleted: the semi join is empty and the left join keeps every row
            foreach (var id in data.Items.Keys.ToList())
                data.Items.Remove(id);
            Change(s =>
            {
                for (var i = 1; i <= 12; i++)
                    s.Delete("items/" + i);
            });
            CheckAll();
            Assert.Empty(Fetch<Holder>(store, JoinRql(FormOf("field=field"), InnerAll, false), out _));
            Assert.Equal(data.Holders.Count, Fetch<Holder>(store, JoinRql(FormOf("field=field"), InnerAll, true), out _).Count);
        }
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheIdOfTheMainDocumentIsAcceptedAsTheMainSideOfTheOnClauseAgainstAFieldOfTheJoinedDocuments()
    {
        var (store, _) = CreateStore();
        using (store)
        {
            // no code of an item is the id of a holder: the join is accepted and matches nothing (FieldJoinIdOfOuterTests pins the join that does)
            foreach (var on in new[] { "id(h) = i.Code", "i.Code = id(h)" })
            {
                Assert.Empty(Ids(store, $"{OuterRql} join ({InnerAll}) as i on {on} {Everything}"));
                Assert.Equal(Data.Create().Holders.Count, Ids(store, $"{OuterRql} left join ({InnerAll}) as i on {on} {Everything}").Length);
            }

            // the id of the main document is not matched against the id of the joined documents
            using var session = store.OpenSession();
            var exception = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<Holder>($"{OuterRql} join ({InnerAll}) as i on id(h) = id(i) {Everything}").ToList());
            Assert.Contains("can match id(h) only against a field of the joined documents", exception.Message);
        }
    }

    private sealed class ScoreComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-3;

        public int GetHashCode(double obj) => 0;
    }
}
