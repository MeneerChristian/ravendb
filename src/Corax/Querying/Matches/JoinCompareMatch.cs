using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Corax.Querying.Matches.Meta;
using Sparrow.Server.Utils;

namespace Corax.Querying.Matches
{
    [DebuggerDisplay("{DebugView,nq}")]
    public struct JoinCompareMatch : IQueryMatch
    {
        private ScoreMapMatch _map;
        private readonly IEntryFilter _filter;
        private IQueryMatch _driver;

        public JoinCompareMatch(ScoreMapMatch map, IEntryFilter filter)
        {
            _map = map;
            _filter = filter;
            _driver = null;
        }

        public readonly bool IsDriven => _driver != null;

        public JoinCompareMatch DrivenBy(IQueryMatch driver) => new(_map, _filter) { _driver = driver };

        public long Count => _driver?.Count ?? _map.Count;

        public SkipSortingResult AttemptToSkipSorting() => _driver == null || _driver.AttemptToSkipSorting() == SkipSortingResult.ResultsNativelySorted
            ? SkipSortingResult.ResultsNativelySorted
            : SkipSortingResult.SortingIsRequired;

        public QueryCountConfidence Confidence => QueryCountConfidence.Low;


        public bool IsBoosting => true;

        public DuplicatesOccurrence DuplicatesOccurrenceStatus => _driver?.DuplicatesOccurrenceStatus ?? DuplicatesOccurrence.NotPossible;

        public int Fill(Span<long> matches)
        {
            var total = 0;
            if (_driver != null)
            {
                var sorted = _driver.AttemptToSkipSorting() == SkipSortingResult.ResultsNativelySorted;
                while (total < matches.Length)
                {
                    var read = _driver.Fill(matches.Slice(total));
                    if (read == 0)
                        break;

                    if (sorted == false)
                        read = Sorting.SortAndRemoveDuplicates(matches.Slice(total, read));

                    var kept = _map.AndWith(matches.Slice(total), read);
                    total += kept == 0 ? 0 : _filter.Filter(matches.Slice(total, kept));
                }

                return total;
            }

            while (total < matches.Length)
            {
                var read = _map.Fill(matches.Slice(total));
                if (read == 0)
                    break;

                total += _filter.Filter(matches.Slice(total, read));
            }

            return total;
        }

        public int AndWith(Span<long> buffer, int matches)
        {
            var kept = _driver == null ? matches : _driver.AndWith(buffer, matches);
            kept = kept == 0 ? 0 : _map.AndWith(buffer, kept);
            return kept == 0 ? 0 : _filter.Filter(buffer.Slice(0, kept));
        }

        public void Score(Span<long> matches, Span<float> scores, float boostFactor)
        {
            if (_driver is { IsBoosting: true })
                _driver.Score(matches, scores, boostFactor);

            _map.Score(matches, scores, boostFactor);
        }

        public QueryInspectionNode Inspect()
        {
            return new QueryInspectionNode(nameof(JoinCompareMatch),
                parameters: new Dictionary<string, string>()
                {
                    { Constants.QueryInspectionNode.IsBoosting, IsBoosting.ToString() },
                    { Constants.QueryInspectionNode.Count, Count.ToString(CultureInfo.InvariantCulture) },
                    { Constants.QueryInspectionNode.CountConfidence, Confidence.ToString() },
                },
                children: _driver == null ? [_map.Inspect()] : [_driver.Inspect(), _map.Inspect()]);
        }

        string DebugView => Inspect().ToString();
    }
}
