using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Corax.Querying.Matches.Meta;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>
/// The outer entries of a join that the lists of a join index answered, each with the same score added: the score that the sub-query of the join gives to every document it matches, times the boost of the join.
/// The entries of the inner match are read as they are, a posting list is streamed and nothing is copied. Entries come ascending and are the entries of the outer index of the query
/// (the join index answered for the very transactions the query reads).
/// </summary>
[DebuggerDisplay("{DebugView,nq}")]
internal struct JoinListMatch : IConstantScoreMatch
{
    private IQueryMatch _inner;
    private readonly float _score;

    public JoinListMatch(IQueryMatch inner, float score)
    {
        _inner = inner;
        _score = score;
    }

    public long Count => _inner.Count;

    public SkipSortingResult AttemptToSkipSorting() => _inner.AttemptToSkipSorting();

    public QueryCountConfidence Confidence => _inner.Confidence;

    public bool IsBoosting => true;

    public DuplicatesOccurrence DuplicatesOccurrenceStatus => _inner.DuplicatesOccurrenceStatus;

    public int Fill(Span<long> matches) => _inner.Fill(matches);

    public int AndWith(Span<long> buffer, int matches) => _inner.AndWith(buffer, matches);

    public void Score(Span<long> matches, Span<float> scores, float boostFactor)
    {
        if (_inner.IsBoosting)
            _inner.Score(matches, scores, boostFactor);

        float add = _score * boostFactor;
        for (int i = 0; i < matches.Length; i++)
            scores[i] += add;
    }

    public QueryInspectionNode Inspect()
    {
        return new QueryInspectionNode(nameof(JoinListMatch),
            parameters: new Dictionary<string, string>
            {
                { "IsBoosting", IsBoosting.ToString() },
                { "Count", Count.ToString(CultureInfo.InvariantCulture) },
                { "Score", _score.ToString(CultureInfo.InvariantCulture) },
            },
            children: [_inner.Inspect()]);
    }

    private string DebugView => Inspect().ToString();
}
