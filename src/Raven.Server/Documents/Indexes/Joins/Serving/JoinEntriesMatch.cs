using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Corax.Querying.Matches.Meta;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>
/// The outer entries of a native array that is sorted ascending and has no repeats. It scores nothing. The array belongs to the allocator of the query.
/// </summary>
[DebuggerDisplay("{DebugView,nq}")]
internal unsafe struct JoinEntriesMatch : IQueryMatch
{
    private readonly long* _entries;
    private readonly int _count;
    private int _position;

    public JoinEntriesMatch(long* entries, int count)
    {
        _entries = entries;
        _count = count;
        _position = 0;
    }

    public long Count => _count;

    public SkipSortingResult AttemptToSkipSorting() => SkipSortingResult.ResultsNativelySorted;

    public QueryCountConfidence Confidence => QueryCountConfidence.High;

    public bool IsBoosting => false;

    public DuplicatesOccurrence DuplicatesOccurrenceStatus => DuplicatesOccurrence.NotPossible;

    public int Fill(Span<long> matches)
    {
        int count = Math.Min(matches.Length, _count - _position);
        if (count <= 0)
            return 0;

        new ReadOnlySpan<long>(_entries + _position, count).CopyTo(matches);
        _position += count;
        return count;
    }

    public int AndWith(Span<long> buffer, int matches)
    {
        // both sides are ascending: every lookup starts where the previous one ended
        int from = 0;
        int kept = 0;
        for (int i = 0; i < matches && from < _count; i++)
        {
            int index = new ReadOnlySpan<long>(_entries + from, _count - from).BinarySearch(buffer[i]);
            if (index < 0)
            {
                from += ~index;
                continue;
            }

            buffer[kept++] = buffer[i];
            from += index + 1;
        }

        return kept;
    }

    public void Score(Span<long> matches, Span<float> scores, float boostFactor)
    {
    }

    public QueryInspectionNode Inspect()
    {
        return new QueryInspectionNode(nameof(JoinEntriesMatch),
            parameters: new Dictionary<string, string>
            {
                { "IsBoosting", IsBoosting.ToString() },
                { "Count", Count.ToString(CultureInfo.InvariantCulture) }
            });
    }

    private string DebugView => Inspect().ToString();
}
