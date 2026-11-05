using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Corax.Querying.Matches.Meta;
using Sparrow.Server;

namespace Corax.Querying.Matches
{
    /// <summary>
    /// Matches what the inner match matches and adds the score of the members match to the entries that are members of it. The members are never matched or counted: an entry that is not
    /// one of them keeps the score it has.
    /// </summary>
    [DebuggerDisplay("{DebugView,nq}")]
    public struct ScoreAddMemberMatch<TInner, TMembers> : IQueryMatch
        where TInner : IQueryMatch
        where TMembers : IQueryMatch
    {
        private readonly ByteStringContext _allocator;
        private TInner _inner;
        private TMembers _members;

        public ScoreAddMemberMatch(ByteStringContext allocator, TInner inner, TMembers members)
        {
            _allocator = allocator;
            _inner = inner;
            _members = members;
        }

        public long Count => _inner.Count;

        public SkipSortingResult AttemptToSkipSorting() => _inner.AttemptToSkipSorting();

        public QueryCountConfidence Confidence => _inner.Confidence;

        public bool IsBoosting => true;

        public DuplicatesOccurrence DuplicatesOccurrenceStatus => _inner.DuplicatesOccurrenceStatus;

        public int Fill(Span<long> matches) => _inner.Fill(matches);

        public int AndWith(Span<long> buffer, int matches) => _inner.AndWith(buffer, matches);

        public unsafe void Score(Span<long> matches, Span<float> scores, float boostFactor)
        {
            if (_inner.IsBoosting)
                _inner.Score(matches, scores, boostFactor);

            if (matches.Length == 0)
                return;

            using var _ = _allocator.Allocate(matches.Length * sizeof(long), out ByteString idsBuffer);
            using var __ = _allocator.Allocate(matches.Length * sizeof(float), out ByteString scoresBuffer);

            // AndWith takes ascending entries without repeats
            var ids = new Span<long>(idsBuffer.Ptr, matches.Length);
            matches.CopyTo(ids);
            var ascending = true;
            for (var i = 1; i < ids.Length && ascending; i++)
                ascending = ids[i - 1] < ids[i];

            if (ascending == false)
            {
                ids.Sort();
                var unique = 1;
                for (var i = 1; i < ids.Length; i++)
                {
                    if (ids[i] != ids[unique - 1])
                        ids[unique++] = ids[i];
                }

                ids = ids.Slice(0, unique);
            }

            var members = _members.AndWith(ids, ids.Length);
            if (members == 0)
                return;

            ids = ids.Slice(0, members);
            var memberScores = new Span<float>(scoresBuffer.Ptr, members);
            memberScores.Clear();
            if (_members.IsBoosting)
                _members.Score(ids, memberScores, boostFactor);

            if (ascending)
            {
                // both are ascending: one pass, not a search for every entry
                var member = 0;
                for (var i = 0; i < matches.Length && member < ids.Length; i++)
                {
                    while (member < ids.Length && ids[member] < matches[i])
                        member++;

                    if (member < ids.Length && ids[member] == matches[i])
                        scores[i] += memberScores[member];
                }

                return;
            }

            for (var i = 0; i < matches.Length; i++)
            {
                var position = ids.BinarySearch(matches[i]);
                if (position >= 0)
                    scores[i] += memberScores[position];
            }
        }

        public QueryInspectionNode Inspect()
        {
            return new QueryInspectionNode(nameof(ScoreAddMemberMatch<TInner, TMembers>),
                parameters: new Dictionary<string, string>()
                {
                    { Constants.QueryInspectionNode.IsBoosting, IsBoosting.ToString() },
                    { Constants.QueryInspectionNode.Count, Count.ToString(CultureInfo.InvariantCulture) },
                },
                children: [_inner.Inspect(), _members.Inspect()]);
        }

        string DebugView => Inspect().ToString();
    }
}
