using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Corax.Querying.Matches.Meta;
using Sparrow.Server;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>
/// The outer entries of the lists of a set of keys that no outer entry shares, for a reader that takes the first few of them (the page of a query): the first entries of every list are copied from the resident cache
/// and merged when they are asked for, the count is the sum of the counts of the lists. When more entries are asked for than the copies hold, the union is made the usual way (<c>complete</c>) and read on from where this
/// match stopped. Ascending, without repeats, an exact count, scores nothing. The memory belongs to the allocator of the query.
/// </summary>
[DebuggerDisplay("{DebugView,nq}")]
internal unsafe struct JoinLazyKeysMatch : IQueryMatch
{
    private readonly ByteStringContext _allocator;
    private readonly long* _ids;
    private readonly int* _ends;
    private readonly int _runs;
    private readonly int _perList;
    private readonly long _count;
    private readonly Func<IQueryMatch> _complete;
    private int* _position;
    private int* _heap;
    private int _heapSize;
    private long _served;
    private IQueryMatch _fallback;

    public JoinLazyKeysMatch(ByteStringContext allocator, long* ids, int* ends, int runs, int perList, long count, Func<IQueryMatch> complete)
    {
        _allocator = allocator;
        _ids = ids;
        _ends = ends;
        _runs = runs;
        _perList = perList;
        _count = count;
        _complete = complete;
        _position = null;
        _heap = null;
        _heapSize = 0;
        _served = 0;
        _fallback = null;
    }

    public long Count => _count;

    /// <summary>The same entries from the first one, over the same copies (which the match only reads).</summary>
    public JoinLazyKeysMatch Restarted() => new(_allocator, _ids, _ends, _runs, _perList, _count, _complete);

    public SkipSortingResult AttemptToSkipSorting() => SkipSortingResult.ResultsNativelySorted;

    public QueryCountConfidence Confidence => QueryCountConfidence.High;

    public bool IsBoosting => false;

    public DuplicatesOccurrence DuplicatesOccurrenceStatus => DuplicatesOccurrence.NotPossible;

    private int Start(int run) => run == 0 ? 0 : _ends[run - 1];

    private long Head(int run) => _ids[_position[run]];

    private void Begin()
    {
        _allocator.Allocate(_runs * sizeof(int), out ByteString positions);
        _allocator.Allocate(_runs * sizeof(int), out ByteString heap);
        _position = (int*)positions.Ptr;
        _heap = (int*)heap.Ptr;
        _heapSize = 0;
        for (int run = 0; run < _runs; run++)
        {
            _position[run] = Start(run);
            if (_position[run] < _ends[run])
                _heap[_heapSize++] = run;
        }

        for (int at = _heapSize / 2 - 1; at >= 0; at--)
            SiftDown(at);
    }

    private void SiftDown(int at)
    {
        int run = _heap[at];
        long head = Head(run);
        while (true)
        {
            int child = 2 * at + 1;
            if (child >= _heapSize)
                break;

            if (child + 1 < _heapSize && Head(_heap[child + 1]) < Head(_heap[child]))
                child++;

            if (Head(_heap[child]) >= head)
                break;

            _heap[at] = _heap[child];
            at = child;
        }

        _heap[at] = run;
    }

    private void Complete()
    {
        _fallback = _complete();
        Span<long> skipped = stackalloc long[256];
        long left = _served;
        while (left > 0)
        {
            int read = _fallback.Fill(skipped.Slice(0, (int)Math.Min(left, skipped.Length)));
            if (read <= 0)
                throw new InvalidOperationException("The union of the keys holds fewer entries than the match has returned.");

            left -= read;
        }
    }

    public int Fill(Span<long> matches)
    {
        if (_fallback != null)
            return _fallback.Fill(matches);

        if (matches.Length == 0)
            return 0;

        if (_heap == null)
            Begin();

        int written = 0;
        while (written < matches.Length && _heapSize > 0)
        {
            int run = _heap[0];
            matches[written++] = Head(run);
            _served++;
            if (++_position[run] == _ends[run])
            {
                // a copy that is as long as the cap may have more entries than it holds: the entries after it are not known
                bool cut = _ends[run] - Start(run) == _perList;
                _heap[0] = _heap[--_heapSize];
                if (_heapSize > 0)
                    SiftDown(0);

                if (cut)
                {
                    Complete();
                    return written + _fallback.Fill(matches.Slice(written));
                }
            }
            else
            {
                SiftDown(0);
            }
        }

        return written;
    }

    public int AndWith(Span<long> buffer, int matches)
    {
        if (_fallback == null)
            Complete();

        return _fallback.AndWith(buffer, matches);
    }

    public void Score(Span<long> matches, Span<float> scores, float boostFactor)
    {
    }

    public QueryInspectionNode Inspect()
    {
        return new QueryInspectionNode(nameof(JoinLazyKeysMatch),
            parameters: new Dictionary<string, string>
            {
                { "IsBoosting", IsBoosting.ToString() },
                { "Count", Count.ToString(CultureInfo.InvariantCulture) }
            });
    }

    private string DebugView => Inspect().ToString();
}
