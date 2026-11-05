using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Corax.Querying.Matches.Meta;

namespace Corax.Querying.Matches
{
    [DebuggerDisplay("{DebugView,nq}")]
    public struct ScoreAddMatch<TInner> : IQueryMatch
        where TInner : IQueryMatch
    {
        private TInner _inner;
        private readonly ScoreMapMatch _scores;

        public ScoreAddMatch(TInner inner, ScoreMapMatch scores)
        {
            _inner = inner;
            _scores = scores;
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

            _scores.Score(matches, scores, boostFactor);
        }

        public QueryInspectionNode Inspect()
        {
            return new QueryInspectionNode(nameof(ScoreAddMatch<TInner>),
                parameters: new Dictionary<string, string>()
                {
                    { Constants.QueryInspectionNode.IsBoosting, IsBoosting.ToString() },
                    { Constants.QueryInspectionNode.Count, Count.ToString(CultureInfo.InvariantCulture) },
                },
                children: [_inner.Inspect()]);
        }

        string DebugView => Inspect().ToString();
    }
}
