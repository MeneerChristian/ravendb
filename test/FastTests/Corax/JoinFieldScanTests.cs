using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Corax.Querying;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes.Persistence.Corax;
using Raven.Server.Documents.Queries;
using Raven.Server.ServerWide.Context;
using Tests.Infrastructure;
using Voron;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;
using JoinFieldScan = Raven.Server.Documents.Indexes.Persistence.Corax.CoraxIndexReadOperation.JoinFieldScan;

namespace FastTests.Corax;

// The scan of the sub-query side of a JOIN on a field
public partial class JoinFieldScanTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string InnerSearch = "w2 w6 w15 w40 w120 w400";

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "IndexSearcher")]
    private static extern ref IndexSearcher GetIndexSearcher(CoraxIndexReadOperation operation);

    private static readonly JoinFieldScan[] Candidates = [JoinFieldScan.EntryTerms, JoinFieldScan.TermWalk, JoinFieldScan.BulkMap, JoinFieldScan.Auto];

    private sealed class Item
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Single { get; set; }
        public string Exact { get; set; }
        public string[] Tags { get; set; }
        public string Text { get; set; }
        public string LongText { get; set; }
        public string Nullable { get; set; }
    }

    private sealed class ItemsIndex : AbstractIndexCreationTask<Item>
    {
        public ItemsIndex()
        {
            Map = items => from i in items select new { i.Name, i.Single, i.Exact, i.Tags, i.Text, i.LongText, i.Nullable };
            Index(x => x.Name, FieldIndexing.Search);
            Index(x => x.Text, FieldIndexing.Search);
            Index(x => x.Exact, FieldIndexing.Exact);
            Index(x => x.LongText, FieldIndexing.Exact);
            Index(x => x.Nullable, FieldIndexing.Exact);
        }

        public override string IndexName => "Items";
    }

    private static List<Item> CreateItems(int count)
    {
        var random = new Random(7);
        // few distinct names of a different length: many entries have exactly the same score (ties), others differ
        var names = Enumerable.Range(0, 14).Select(n => string.Join(' ', Enumerable.Range(0, 2 + n % 5).Select(k => $"w{(n * 3 + k * 7) % 41 + 1}"))).ToArray();
        var longs = Enumerable.Range(0, 6).Select(n => new string((char)('a' + n), 300 + n) + "-" + n).ToArray();
        var countries = new[] { "NL", "nl", "De", "fr", "BE", "uk", "es" };

        return Enumerable.Range(0, count).Select(i => new Item
        {
            Id = $"items/{i}",
            Name = names[random.Next(names.Length)],
            Single = countries[random.Next(countries.Length)],
            Exact = countries[random.Next(countries.Length)],
            // one to four values (some repeated), sometimes none
            Tags = random.Next(10) == 0 ? [] : Enumerable.Range(0, 1 + random.Next(4)).Select(_ => "t" + random.Next(40)).ToArray(),
            // several tokens per value; some values hold nothing that the analyzer keeps
            Text = random.Next(8) == 0 ? "!!! ???" : string.Join(' ', Enumerable.Range(0, 1 + random.Next(4)).Select(_ => "x" + random.Next(60))),
            LongText = random.Next(6) == 0 ? null : longs[random.Next(longs.Length)],
            Nullable = random.Next(5) switch { 0 => null, 1 => "", _ => "v" + random.Next(30) }
        }).ToList();
    }

    internal sealed class ScanSession : IDisposable
    {
        private readonly IDisposable _indexContext, _documentsContext;
        private readonly IDisposable _indexTx, _documentsTx;
        public readonly CoraxIndexReadOperation Reader;
        public readonly IndexQueryServerSide Query;
        public readonly string Rql;
        public readonly DocumentsOperationContext DocumentsContext;

        public ScanSession(DocumentDatabase database, string indexName, string rql)
        {
            var index = database.IndexStore.GetIndex(indexName);
            _indexContext = index._contextPool.AllocateOperationContext(out TransactionOperationContext indexContext);
            var indexTx = indexContext.OpenReadTransaction();
            _indexTx = indexTx;
            _documentsContext = database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext documentsContext);
            _documentsTx = documentsContext.OpenReadTransaction();
            DocumentsContext = documentsContext;
            Rql = rql;
            Query = new IndexQueryServerSide(rql) { IsJoinSource = true };
            Reader = (CoraxIndexReadOperation)index.IndexPersistence.OpenIndexReader(indexTx.InnerTransaction, Query);
        }

        public IndexSearcher Searcher => GetIndexSearcher(Reader);

        // The expected keys: the matches of the plain query, and the terms that the index recorded for each of their entries
        public List<(string Key, float Score)> Expected(IDocumentStore store, string field, bool winners, out Dictionary<string, string> winnerByKey)
            => JoinScanOracle.TermScores(store, this, Rql, field, winners, out winnerByKey);

        public List<(string Key, float Score)> Scan(string field, bool winners, JoinFieldScan strategy, out Dictionary<string, string> winnerByKey)
        {
            using var scores = Reader.ScanTermScores(Query, DocumentsContext, field, strategy, winners, CancellationToken.None);
            var results = new List<(string Key, float Score)>(scores.Count);
            winnerByKey = winners ? new Dictionary<string, string>(StringComparer.Ordinal) : null;
            for (var i = 0; i < scores.Count; i++)
            {
                var key = Encoding.UTF8.GetString(scores.Term(i));
                results.Add((key, scores.Score(i)));
                if (winners)
                    winnerByKey[key] = Reader.GetDocumentIdOfEntry(scores.WinnerEntryId(i));
            }

            return results;
        }

        public void Dispose()
        {
            Reader.Dispose();
            _documentsTx.Dispose();
            _documentsContext.Dispose();
            _indexTx.Dispose();
            _indexContext.Dispose();
        }
    }

    private static string Describe(List<(string Key, float Score)> results) => string.Join(", ", results.OrderBy(x => x.Key, StringComparer.Ordinal).Take(8).Select(x => $"'{x.Key}'={x.Score}"));

    private void AssertSame(string label, List<(string Key, float Score)> expected, Dictionary<string, string> expectedWinners, List<(string Key, float Score)> actual,
        Dictionary<string, string> actualWinners)
    {
        var expectedByKey = expected.ToDictionary(x => x.Key, x => x.Score, StringComparer.Ordinal);
        var actualByKey = actual.ToDictionary(x => x.Key, x => x.Score, StringComparer.Ordinal);
        Assert.True(expected.Count == expectedByKey.Count, $"[{label}] the expected keys are duplicated");
        Assert.True(actual.Count == actualByKey.Count, $"[{label}] duplicate keys: {Describe(actual)}");
        Assert.True(expectedByKey.Keys.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(actualByKey.Keys.OrderBy(x => x, StringComparer.Ordinal)),
            $"[{label}] different keys: expected {expected.Count} ({Describe(expected)}), got {actual.Count} ({Describe(actual)})");
        foreach (var (key, score) in expectedByKey)
            Assert.True(BitConverter.SingleToInt32Bits(score) == BitConverter.SingleToInt32Bits(actualByKey[key]), $"[{label}] '{key}': expected {score}, got {actualByKey[key]}");

        if (expectedWinners == null)
        {
            Assert.Null(actualWinners);
            return;
        }

        Assert.NotNull(actualWinners);
        Assert.True(expectedWinners.Count == actualWinners.Count, $"[{label}] winners: expected {expectedWinners.Count}, got {actualWinners.Count}");
        foreach (var (key, id) in expectedWinners)
            Assert.True(actualWinners.TryGetValue(key, out var other) && other == id, $"[{label}] winner of '{key}': expected {id}, got {(actualWinners.TryGetValue(key, out var o) ? o : "none")}");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task EveryScanGivesTheSameKeysScoresAndWinnersAsThePlainQuery()
    {
        using var store = GetDocumentStore(CoraxOptions());
        new ItemsIndex().Execute(store);
        using (var bulk = store.BulkInsert())
        {
            foreach (var item in CreateItems(1500))
                bulk.Store(item, item.Id);
        }

        Indexes.WaitForIndexing(store);
        var database = await Databases.GetDocumentDatabaseInstanceFor(store);

        var checkedFields = new List<string>();
        foreach (var search in new[] { "w2 w6 w15 w40", "w1 w2 w3 w4 w5 w6 w7 w8 w9 w10 w11 w12 w13 w14 w15 w16 w17 w18 w19 w20 w21 w22 w23 w24 w25 w26 w27 w28 w29 w30 w31 w32 w33 w34 w35 w36 w37 w38 w39 w40 w41", "w999" })
        {
            using var session = new ScanSession(database, "Items", $"from index 'Items' where search(Name, '{search}')");
            foreach (var field in new[] { "Single", "Exact", "Tags", "Text", "LongText", "Nullable", "Name" })
            {
                foreach (var winners in new[] { false, true })
                {
                    var expected = session.Expected(store, field, winners, out var expectedWinners);
                    if (search != "w999")
                        Assert.NotEmpty(expected);
                    if (winners && search.Length < 20)
                        output.WriteLine($"{field}: {expected.Count} keys, {expected.Count(x => x.Key.Length == 0)} empty, {expected.Count(x => x.Key.Length > 256)} long, {expected.Select(x => x.Score).Distinct().Count()} distinct scores, {expected.Count(x => x.Key == "null")} 'null'; odd keys: {string.Join("|", expected.Select(x => x.Key).Where(k => k.Length > 0 && (char.IsDigit(k[^1]) == false)).Take(5).Select(k => k.Length > 20 ? k[..20] : k))}");

                    foreach (var strategy in Candidates)
                    {
                        var actual = session.Scan(field, winners, strategy, out var actualWinners);
                        AssertSame($"{field} / {strategy} / winners={winners} / '{search.Length}'", expected, expectedWinners, actual, actualWinners);
                    }
                }

                checkedFields.Add(field);
            }
        }

        // a field that does not exist, and one that exists but holds no matched entry
        using (var session = new ScanSession(database, "Items", $"from index 'Items' where search(Name, '{InnerSearch}')"))
        {
            foreach (var strategy in Candidates)
                Assert.Empty(session.Scan("NoSuchField", true, strategy, out var winners));
            Assert.Empty(session.Expected(store, "NoSuchField", true, out var referenceWinners));
            Assert.Empty(referenceWinners);
        }

        output.WriteLine($"checked {checkedFields.Count} field scans");
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task TheBulkMapLosesKeysOfAMultiValuedFieldAndTheGateKeepsItOut()
    {
        using var store = GetDocumentStore(CoraxOptions());
        new ItemsIndex().Execute(store);
        using (var bulk = store.BulkInsert())
        {
            foreach (var item in CreateItems(1500))
                bulk.Store(item, item.Id);
        }

        Indexes.WaitForIndexing(store);
        var database = await Databases.GetDocumentDatabaseInstanceFor(store);

        using var session = new ScanSession(database, "Items", $"from index 'Items' where search(Name, '{InnerSearch}')");
        var searcher = session.Searcher;

        // the gate: what the index recorded about the field
        foreach (var field in new[] { "Single", "Exact", "LongText", "Nullable" })
            Assert.True(searcher.FieldHoldsOneTermPerEntry(field), field);

        Assert.False(searcher.FieldHoldsOneTermPerEntry("Tags"));
        Assert.False(searcher.FieldHoldsOneTermPerEntry("Text"));
        // Name is analyzed to several tokens too
        Assert.False(searcher.FieldHoldsOneTermPerEntry("Name"));

        // the same fact seen from the data, independent of the flag
        foreach (var field in new[] { "Single", "Exact", "LongText", "Nullable", "Tags", "Text", "Name" })
        {
            var postings = PostingEntries(searcher, field);
            using var nameScope = Slice.From(searcher.Allocator, field, out var name);
            var rows = searcher.EntriesToTermsReader(name)?.NumberOfEntries ?? 0;
            output.WriteLine($"{field}: posting entries {postings}, map rows {rows}, flag single={searcher.FieldHoldsOneTermPerEntry(field)}");
            Assert.Equal(searcher.FieldHoldsOneTermPerEntry(field), postings == rows);
        }

        // the map keeps one term per entry: an array field has to be read by another scan, the gate falls back to it
        foreach (var field in new[] { "Tags", "Text" })
        {
            var gated = session.Scan(field, true, JoinFieldScan.BulkMap, out var gatedWinners);
            var expected = session.Expected(store, field, true, out var expectedWinners);
            AssertSame(field + " gated", expected, expectedWinners, gated, gatedWinners);
        }

        // and for a single-valued field the map is right
        foreach (var field in new[] { "Single", "Exact", "LongText", "Nullable" })
        {
            var expected = session.Expected(store, field, true, out var expectedWinners);
            var actual = session.Scan(field, true, JoinFieldScan.BulkMap, out var actualWinners);
            AssertSame(field + " map", expected, expectedWinners, actual, actualWinners);
        }
    }

    private static long PostingEntries(IndexSearcher searcher, string field)
    {
        var metadata = searcher.FieldMetadataBuilder(field);
        var provider = searcher.TextualAggregation(metadata);
        using var scope = provider.AggregateByTerms(out var terms, out var counts);
        long sum = 0;
        for (var i = 0; i < counts.Length; i++)
            sum += counts[i];

        if (searcher.TryGetPostingListForNonExisting(metadata, out var nonExisting))
            sum += searcher.GetPostingList(nonExisting)?.State.NumberOfEntries ?? 0;

        return sum;
    }
}
