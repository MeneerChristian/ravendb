using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Corax.Querying.Matches.Meta;

namespace Corax.Querying.Matches
{
    public interface IRankedMatch
    {
        void Rank(Span<long> matches, Span<float> ranks);
    }

    [DebuggerDisplay("{DebugView,nq}")]
    public struct RankedMatch<TInner> : IQueryMatch, IRankedMatch
        where TInner : IQueryMatch
    {
        private TInner _inner;
        private readonly ScoreMapMatch _ranks;
        private readonly bool _scoreIsRelevance;

        public RankedMatch(TInner inner, ScoreMapMatch ranks, bool scoreIsRelevance)
        {
            _inner = inner;
            _ranks = ranks;
            _scoreIsRelevance = scoreIsRelevance;
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
            if (_scoreIsRelevance)
                _inner.Score(matches, scores, boostFactor);
        }

        public void Rank(Span<long> matches, Span<float> ranks)
        {
            _ranks.Rank(matches, ranks);

            // 0 is the rank of an entry that is not in the map
            for (var i = 0; i < ranks.Length; i++)
            {
                if (ranks[i] == 0)
                    ranks[i] = float.PositiveInfinity;
            }
        }

        public QueryInspectionNode Inspect()
        {
            return new QueryInspectionNode(nameof(RankedMatch<TInner>),
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
