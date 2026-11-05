using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Corax.Querying.Matches.Meta;
using Corax.Querying.Matches.SortingMatches.Meta;

namespace Corax.Querying.Matches
{
    /// <summary>A match that returns its entries in the order the query asks for, and knows how many entries the query has in all: the caller must not sort it.</summary>
    public interface IPresortedMatch : IQueryMatch
    {
        /// <summary>The entries of the query, not only the ones that <see cref="IQueryMatch.Fill"/> returns.</summary>
        long TotalResults { get; }

        /// <summary>The buffers that receive the score of the entry that Fill puts at the same position, when the query returns scores.</summary>
        void SetScoreAndDistanceBuffer(SortingDataTransfer transfer);
    }

    /// <summary>The first entries of a query, in order, with their scores and the number of entries of the query. The arrays are copied.</summary>
    [DebuggerDisplay("{DebugView,nq}")]
    public sealed class PresortedPageMatch : IPresortedMatch
    {
        private readonly long[] _entries;
        private readonly float[] _scores;
        private SortingDataTransfer _transfer;
        private int _position;

        /// <param name="entries">In the order of the query.</param>
        /// <param name="scores">The score of every entry, null when the entries have none.</param>
        public PresortedPageMatch(ReadOnlySpan<long> entries, ReadOnlySpan<float> scores, long total)
        {
            _entries = entries.ToArray();
            _scores = scores.IsEmpty ? null : scores.ToArray();
            if (_scores != null && _scores.Length != _entries.Length)
                throw new ArgumentException("There has to be one score for every entry.", nameof(scores));

            TotalResults = total;
        }

        public long TotalResults { get; }

        public long Count => TotalResults;

        public void SetScoreAndDistanceBuffer(SortingDataTransfer transfer) => _transfer = transfer;

        public SkipSortingResult AttemptToSkipSorting() => SkipSortingResult.SortingIsRequired;

        public QueryCountConfidence Confidence => QueryCountConfidence.High;

        public bool IsBoosting => _scores != null;

        public DuplicatesOccurrence DuplicatesOccurrenceStatus => DuplicatesOccurrence.NotPossible;

        public int Fill(Span<long> matches)
        {
            int count = Math.Min(matches.Length, _entries.Length - _position);
            if (count <= 0)
                return 0;

            _entries.AsSpan(_position, count).CopyTo(matches);
            if (_transfer.IncludeScores && _scores != null)
                _scores.AsSpan(_position, count).CopyTo(_transfer.ScoresBuffer);

            _position += count;
            return count;
        }

        public int AndWith(Span<long> buffer, int matches) => throw new NotSupportedException($"{nameof(PresortedPageMatch)} does not support the operation of {nameof(AndWith)}.");

        public void Score(Span<long> matches, Span<float> scores, float boostFactor)
        {
        }

        public QueryInspectionNode Inspect()
        {
            return new QueryInspectionNode(nameof(PresortedPageMatch),
                parameters: new Dictionary<string, string>
                {
                    { Constants.QueryInspectionNode.IsBoosting, IsBoosting.ToString() },
                    { Constants.QueryInspectionNode.Count, Count.ToString(CultureInfo.InvariantCulture) },
                });
        }

        private string DebugView => Inspect().ToString();
    }
}
