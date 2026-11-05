using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;
using ScanSession = FastTests.Corax.JoinFieldScanTests.ScanSession;

namespace FastTests.Corax;

// What the sub-query side of a JOIN has to return, worked out without the scans
internal static class JoinScanOracle
{
    private const string Everything = " limit 0, 10000000";

    private sealed class Hit
    {
        public string Id { get; set; }
    }

    // A result that carries its own metadata
    public interface IHasMetadata
    {
        Dictionary<string, object> Metadata { get; }
    }

    // The documents that the query matches, with the score that the plain query gives each of them
    public static List<(string DocumentId, float Score)> Matches(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        var hits = session.Advanced.RawQuery<Hit>(rql + " order by score()" + Everything).ToList();
        return hits.Select(x => (x.Id, ScoreOf(session, x))).ToList();
    }

    // The matches of a query over documents of the type , with their scores and the document itself
    public static List<(T Document, float Score)> MatchesOf<T>(IDocumentStore store, string rql, bool scored = true)
    {
        using var session = store.OpenSession();
        var hits = session.Advanced.RawQuery<T>(rql + (scored ? " order by score()" : "") + Everything).ToList();
        return hits.Select(x => (x, scored ? ScoreOf(session, x) : 0f)).ToList();
    }

    private static float ScoreOf<T>(IDocumentSession session, T entity)
    {
        object score = entity is IHasMetadata result ? result.Metadata[Constants.Documents.Metadata.IndexScore] : session.Advanced.GetMetadataFor(entity)[Constants.Documents.Metadata.IndexScore];
        return Convert.ToSingle(score);
    }

    // The best score of every key
    public static List<(string Key, float Score)> BestPerKey(IEnumerable<(string Key, float Score)> scores) =>
        scores.GroupBy(x => x.Key, StringComparer.Ordinal).Select(g => (g.Key, g.Max(x => x.Score))).ToList();

    // The keys of a join on the id
    public static List<(string Key, float Score)> IdScores(IDocumentStore store, string rql) =>
        Matches(store, rql).Select(x => (x.DocumentId.ToLowerInvariant(), x.Score)).ToList();

    // The keys of a join on the id that orders by its own index
    public static List<(string Key, float Score)> IdRanks<T>(IDocumentStore store, string rql, Func<T, string> id, Func<T, object[]> orderValues)
    {
        var matches = MatchesOf<T>(store, rql, scored: false);
        var results = new List<(string Key, float Score)>(matches.Count);
        object[] previous = null;
        var rank = 0;
        foreach (var (document, _) in matches)
        {
            var values = orderValues(document);
            if (previous == null || values.SequenceEqual(previous) == false)
                rank++;

            previous = values;
            results.Add((id(document).ToLowerInvariant(), rank));
        }

        return results;
    }

    // The keys of a join on a field
    public static List<(string Key, float Score)> TermScores(IDocumentStore store, ScanSession session, string rql, string field, bool winners, out Dictionary<string, string> winnerByKey)
    {
        var scoreByDocument = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (documentId, score) in Matches(store, rql))
            scoreByDocument[documentId.ToLowerInvariant()] = score;

        var searcher = session.Searcher;
        var entries = new List<long>();
        var all = searcher.AllEntries();
        var buffer = new long[4096];
        int read;
        while ((read = all.Fill(buffer)) > 0)
            entries.AddRange(buffer.AsSpan(0, read).ToArray());
        entries.Sort();

        var rootPage = searcher.FieldCache.GetLookupRootPage(field);
        var best = new Dictionary<string, float>(StringComparer.Ordinal);
        var winnerOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var key = new global::Voron.Data.CompactTrees.CompactKey();
        global::Voron.Page page = default;
        foreach (var entry in entries)
        {
            var documentId = session.Reader.GetDocumentIdOfEntry(entry);
            if (scoreByDocument.TryGetValue(documentId.ToLowerInvariant(), out var score) == false)
                continue;

            var reader = searcher.GetEntryTermsReader(entry, ref page, key);
            while (reader.FindNext(rootPage))
            {
                if (reader.IsNull || reader.IsNonExisting)
                    continue;

                var term = Encoding.UTF8.GetString(reader.Current.Decoded());
                if (best.TryGetValue(term, out var current) == false || score > current)
                {
                    best[term] = score;
                    winnerOf[term] = documentId;
                }
            }

            key.Reset();
        }

        winnerByKey = winners ? winnerOf : null;
        return best.Select(x => (x.Key, x.Value)).ToList();
    }

    public static void AssertSameKeysAsMultiset(string label, List<(string Key, float Score)> expected, List<(string Key, float Score)> actual)
    {
        var left = expected.Select(x => (x.Key, Bits: BitConverter.SingleToInt32Bits(x.Score))).OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Bits).ToList();
        var right = actual.Select(x => (x.Key, Bits: BitConverter.SingleToInt32Bits(x.Score))).OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Bits).ToList();
        Assert.True(left.SequenceEqual(right), $"[{label}] {left.Count} keys expected, {right.Count} found");
    }
}
