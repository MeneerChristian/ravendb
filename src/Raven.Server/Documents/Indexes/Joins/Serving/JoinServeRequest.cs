using System;
using System.Collections.Generic;
using Corax.Querying;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Queries.Timings;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>What a join index is told about one JOIN of a query when it is asked to answer it.</summary>
internal sealed class JoinServeRequest
{
    public IndexQueryServerSide Query { get; init; }

    public JoinClause Join { get; init; }

    /// <summary>A LEFT JOIN: the server keeps every entry of the main query and adds the score of the joined documents to the entries whose joined document the sub-query matches. The answer is the same as for an inner join.</summary>
    public bool IsLeft { get; init; }

    public string OuterIndexName { get; init; }

    public string InnerIndexName { get; init; }

    /// <summary>The field of the outer index the JOIN matches on.</summary>
    public string OuterField { get; init; }

    /// <summary>What the outer index had consumed in the transaction the query reads.</summary>
    public JoinIndexState Outer { get; init; }

    /// <summary>What the joined index had consumed in the transaction the query reads.</summary>
    public JoinIndexState Inner { get; init; }

    /// <summary>The score of the joined documents is added to the score of the main document.</summary>
    public bool ScoresNeeded { get; init; }

    /// <summary>The query returns scores or orders by score(): the score of the joined documents is asked for when the match is built, which cannot fall back any more.</summary>
    public bool ScoresAsked { get; init; }

    /// <summary>
    /// False when nothing in the query can use the score of the joined documents (an explicit order by fields of the main documents, no boost): the answer then does not need to carry scores
    /// and <see cref="JoinServed"/> is asked for a match without them. True when the server cannot tell.
    /// </summary>
    public bool ScoresMayMatter { get; init; } = true;

    /// <summary>The join orders the main query by fields of the joined index and nothing else: the fields in the order of the clause, null when it does not. Only for the first page of a query.</summary>
    public IReadOnlyList<JoinOrderField> InnerOrder { get; init; }

    /// <summary>The BOOST of the join, 1 when it has none: the score of the joined documents is multiplied by it before it is added to the score of the main document.</summary>
    public float Boost { get; init; } = 1f;

    /// <summary>The timings of this join in the query, null when the query does not collect them. Counters added to it are returned with the timings of the query.</summary>
    public QueryTimingsScope Timings { get; init; }

    public Func<JoinScoreProbe> ProbeFunc { get; init; }

    /// <summary>Names the sub-query of the JOIN and its parameters; with the state of the joined index it names what <see cref="ProbeFunc"/> reads.</summary>
    public Func<string> ProbeKey { get; init; }

    internal JoinScoreProbeCache ProbeCache { get; set; }

    public Func<string, string, EncodedTerm> EncodeFunc { get; init; }

    /// <summary>For a join on a field: finds the keys (terms of the key field) that the entries of the sub-query have, among the given keys (their bytes, one after the other, and where each of them ends). Null when it cannot be done.</summary>
    public ScanKeysAmong ScanKeys { get; init; }

    /// <summary>The term that a query on the joined index looks for when it asks for this text in the field, analyzed the way the index analyzes the field.</summary>
    public EncodedTerm EncodeInnerTerm(string field, string text) => EncodeFunc(field, text);

    private JoinScoreProbe _probe;
    private bool _probed;

    /// <summary>
    /// Reads the score that the sub-query of the JOIN gives to the documents it matches, the way the server scores its documents when it scans the sub-query.
    /// The answer is kept for the rest of the request: the inner snapshot does not change within it, so the key of the cache is built once and a request asks the cache once.
    /// </summary>
    public JoinScoreProbe ProbeInnerScore()
    {
        if (_probed)
            return _probe;

        JoinScoreProbe probe;
        if (ProbeCache == null || ProbeKey == null)
        {
            probe = ProbeFunc();
        }
        else
        {
            var consumed = new List<string>();
            foreach (var (collection, (doc, tombstone)) in Inner.Consumed)
                consumed.Add($"{collection}:{doc}:{tombstone}");

            consumed.Sort(StringComparer.Ordinal);
            probe = ProbeCache.GetOrProbe($"{ProbeKey()}|{Inner.EnvironmentId:N}|{string.Join(',', consumed)}", ProbeFunc);
        }

        _probe = probe;
        _probed = true;
        return probe;
    }
}

/// <summary>The keys of the join field that the entries the sub-query matches have, restricted to the given keys. The caller disposes the result.</summary>
internal delegate JoinTermScores ScanKeysAmong(ReadOnlySpan<byte> keyBytes, ReadOnlySpan<int> keyEnds);

/// <summary>One field of the ORDER BY of a join, as the query wrote it.</summary>
internal readonly record struct JoinOrderField(string Field, bool Ascending, OrderByFieldType Type);
