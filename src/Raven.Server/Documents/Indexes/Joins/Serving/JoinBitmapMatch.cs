using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Corax.Querying.Matches.Meta;
using Sparrow.Server.Collections;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>
/// The outer entries of a bitmap with one bit per entry id: ascending, without repeats, with an exact count. It scores nothing. The memory of the bitmap belongs to the allocator of the query.
/// </summary>
[DebuggerDisplay("{DebugView,nq}")]
internal struct JoinBitmapMatch : IQueryMatch
{
    private GrowableBitArray _bitmap;
    private readonly long _count;
    private long _next;

    public JoinBitmapMatch(GrowableBitArray bitmap, long count)
    {
        _bitmap = bitmap;
        _count = count;
        _next = 0;
    }

    public long Count => _count;

    /// <summary>The same entries from the first one, over the same memory (which the match only reads).</summary>
    public JoinBitmapMatch Restarted() => new(_bitmap, _count);

    public SkipSortingResult AttemptToSkipSorting() => SkipSortingResult.ResultsNativelySorted;

    public QueryCountConfidence Confidence => QueryCountConfidence.High;

    public bool IsBoosting => false;

    public DuplicatesOccurrence DuplicatesOccurrenceStatus => DuplicatesOccurrence.NotPossible;

    public int Fill(Span<long> matches)
    {
        if (matches.Length == 0 || _next > _bitmap.MaxSetBit)
            return 0;

        int count = _bitmap.Fill(matches, _next);
        if (count > 0)
            _next = matches[count - 1] + 1;

        return count;
    }

    public int AndWith(Span<long> buffer, int matches)
    {
        long min = _bitmap.MinSetBit;
        long max = _bitmap.MaxSetBit;
        int kept = 0;
        for (int i = 0; i < matches; i++)
        {
            long id = buffer[i];
            if (id >= min && id <= max && _bitmap.Contains(id))
                buffer[kept++] = id;
        }

        return kept;
    }

    public void Score(Span<long> matches, Span<float> scores, float boostFactor)
    {
    }

    public QueryInspectionNode Inspect()
    {
        return new QueryInspectionNode(nameof(JoinBitmapMatch),
            parameters: new Dictionary<string, string>
            {
                { "IsBoosting", IsBoosting.ToString() },
                { "Count", Count.ToString(CultureInfo.InvariantCulture) }
            });
    }

    private string DebugView => Inspect().ToString();
}
