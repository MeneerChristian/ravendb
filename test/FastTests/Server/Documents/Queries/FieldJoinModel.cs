using System;
using System.Collections.Generic;
using System.Linq;

namespace FastTests.Server.Documents.Queries;

/// <summary>An outer row of a field join: its key terms (null terms are no keys) and the score of the outer query alone.</summary>
internal sealed record FieldJoinOuter(string Id, double Score, IReadOnlyCollection<string> Keys);

/// <summary>An inner entry that the sub-query matched: its key terms and the score of the sub-query alone.</summary>
internal sealed record FieldJoinInner(string Id, double Score, IReadOnlyCollection<string> Keys);

internal sealed record FieldJoinExpectedRow(string Id, bool Matched, int MatchedKeys, double Score);

/// <summary>
/// What a join of the server returns, computed in memory: an outer row matches when one of its keys is a key of a matched inner entry,
/// and its score is the outer score plus boost times the sum, over the distinct matched keys, of the best score of the inner entries that carry the key.
/// </summary>
internal static class FieldJoinModel
{
    /// <summary>The key terms of the default analyzer, which lowercases.</summary>
    public static string DefaultFold(string term) => term.ToLowerInvariant();

    public static List<FieldJoinExpectedRow> Expected(IEnumerable<FieldJoinOuter> outer, IEnumerable<FieldJoinInner> matchedInner, bool left, double boost, Func<string, string> fold = null)
    {
        fold ??= DefaultFold;
        var bestByKey = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var inner in matchedInner)
        {
            foreach (var key in KeysOf(inner.Keys, fold))
                bestByKey[key] = bestByKey.TryGetValue(key, out var best) ? Math.Max(best, inner.Score) : inner.Score;
        }

        var rows = new List<FieldJoinExpectedRow>();
        foreach (var row in outer)
        {
            var matched = KeysOf(row.Keys, fold).Where(bestByKey.ContainsKey).ToList();
            if (matched.Count == 0 && left == false)
                continue;

            rows.Add(new FieldJoinExpectedRow(row.Id, matched.Count > 0, matched.Count, row.Score + boost * matched.Sum(x => bestByKey[x])));
        }

        return rows;
    }

    private static IEnumerable<string> KeysOf(IEnumerable<string> keys, Func<string, string> fold) => keys.Where(x => x != null).Select(fold).Distinct(StringComparer.Ordinal);
}
