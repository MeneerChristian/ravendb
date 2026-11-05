using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Corax.Querying.Matches.Meta;

namespace Corax.Querying.Matches
{
    [DebuggerDisplay("{DebugView,nq}")]
    public unsafe struct ScoreMapMatch : IQueryMatch
    {
        private readonly long* _entries;
        private readonly float* _scores;
        private readonly float* _ranks;
        private readonly int _count;
        private int _position;

        // entries must be sorted ascending without duplicates
        internal ScoreMapMatch(long* entries, float* scores, float* ranks, int count)
        {
            _entries = entries;
            _scores = scores;
            _ranks = ranks;
            _count = count;
            _position = 0;
        }

        public long Count => _count;

        public SkipSortingResult AttemptToSkipSorting() => SkipSortingResult.ResultsNativelySorted;

        public QueryCountConfidence Confidence => QueryCountConfidence.High;

        public bool IsBoosting => true;

        public DuplicatesOccurrence DuplicatesOccurrenceStatus => DuplicatesOccurrence.NotPossible;

        public int Fill(Span<long> matches)
        {
            var count = Math.Min(matches.Length, _count - _position);
            if (count <= 0)
                return 0;

            new ReadOnlySpan<long>(_entries + _position, count).CopyTo(matches);
            _position += count;
            return count;
        }

        public int AndWith(Span<long> buffer, int matches)
        {
            // both sides are sorted, so every lookup can start where the previous one ended
            var from = 0;
            var kept = 0;
            for (var i = 0; i < matches && from < _count; i++)
            {
                var idx = LowerBound(from, buffer[i]);
                if (idx == _count || _entries[idx] != buffer[i])
                {
                    from = idx;
                    continue;
                }

                buffer[kept++] = buffer[i];
                from = idx + 1;
            }

            return kept;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Score(Span<long> matches, Span<float> scores, float boostFactor)
        {
            if (_scores == null)
                return;

            // the matches come in ascending order as a rule
            var from = 0;
            var previous = long.MinValue;
            for (var i = 0; i < matches.Length; i++)
            {
                if (matches[i] < previous)
                    from = 0;

                previous = matches[i];
                var idx = from = LowerBound(from, matches[i]);
                if (idx == _count || _entries[idx] != matches[i])
                    continue;

                scores[i] += _scores[idx] * boostFactor;
            }
        }

        public void Rank(Span<long> matches, Span<float> ranks)
        {
            if (_ranks == null && _count != 0)
                throw new InvalidOperationException("The score map has no ranks.");

            var from = 0;
            var previous = long.MinValue;
            for (var i = 0; i < matches.Length; i++)
            {
                if (matches[i] < previous)
                    from = 0;

                previous = matches[i];
                var idx = from = LowerBound(from, matches[i]);
                ranks[i] = idx == _count || _entries[idx] != matches[i] ? 0 : _ranks[idx];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int LowerBound(int from, long value)
        {
            var low = from;
            var high = _count;
            while (low < high)
            {
                var middle = (int)((uint)(low + high) >> 1);
                if (_entries[middle] < value)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }

        public QueryInspectionNode Inspect()
        {
            return new QueryInspectionNode(nameof(ScoreMapMatch),
                parameters: new Dictionary<string, string>()
                {
                    { Constants.QueryInspectionNode.IsBoosting, IsBoosting.ToString() },
                    { Constants.QueryInspectionNode.Count, Count.ToString(CultureInfo.InvariantCulture) },
                    { Constants.QueryInspectionNode.CountConfidence, Confidence.ToString() },
                });
        }

        string DebugView => Inspect().ToString();
    }
}
