using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Corax.Querying.Matches.Meta;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>
/// The outer entries of a bitmap of 64 bit words with one bit per entry id: ascending, without repeats, with an exact count. It scores nothing. The words belong to the allocator of the query.
/// </summary>
[DebuggerDisplay("{DebugView,nq}")]
internal unsafe struct JoinWordsMatch : IQueryMatch
{
    private readonly ulong* _words;
    private readonly int _wordCount;
    private readonly long _count;
    private int _nextWord;
    private ulong _current;
    private bool _started;

    public JoinWordsMatch(ulong* words, int wordCount)
    {
        _words = words;
        _wordCount = wordCount;
        long count = 0;
        for (int w = 0; w < wordCount; w++)
            count += BitOperations.PopCount(words[w]);

        _count = count;
        _nextWord = 0;
        _current = 0;
        _started = false;
    }

    public long Count => _count;

    /// <summary>The same entries from the first one, over the same memory (which the match only reads).</summary>
    public JoinWordsMatch Restarted()
    {
        JoinWordsMatch copy = this;
        copy._nextWord = 0;
        copy._current = 0;
        copy._started = false;
        return copy;
    }

    public SkipSortingResult AttemptToSkipSorting() => SkipSortingResult.ResultsNativelySorted;

    public QueryCountConfidence Confidence => QueryCountConfidence.High;

    public bool IsBoosting => false;

    public DuplicatesOccurrence DuplicatesOccurrenceStatus => DuplicatesOccurrence.NotPossible;

    public int Fill(Span<long> matches)
    {
        int written = 0;
        while (written < matches.Length)
        {
            if (_started == false)
            {
                if (_nextWord >= _wordCount)
                    break;

                _current = _words[_nextWord];
                _nextWord++;
                _started = true;
            }

            while (_current != 0 && written < matches.Length)
            {
                int bit = BitOperations.TrailingZeroCount(_current);
                matches[written++] = ((long)(_nextWord - 1) << 6) + bit;
                _current &= _current - 1;
            }

            if (_current == 0)
                _started = false;
        }

        return written;
    }

    public int AndWith(Span<long> buffer, int matches)
    {
        int kept = 0;
        for (int i = 0; i < matches; i++)
        {
            long id = buffer[i];
            long word = id >> 6;
            if (id >= 0 && word < _wordCount && (_words[word] & (1UL << (int)(id & 63))) != 0)
                buffer[kept++] = id;
        }

        return kept;
    }

    public void Score(Span<long> matches, Span<float> scores, float boostFactor)
    {
    }

    public QueryInspectionNode Inspect()
    {
        return new QueryInspectionNode(nameof(JoinWordsMatch),
            parameters: new Dictionary<string, string>
            {
                { "IsBoosting", IsBoosting.ToString() },
                { "Count", Count.ToString(CultureInfo.InvariantCulture) }
            });
    }

    private string DebugView => Inspect().ToString();
}
