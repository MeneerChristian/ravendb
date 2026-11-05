using System;
using System.Collections.Generic;
using Corax.Querying;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>Builds the first <paramref name="take"/> entries of an ordered join, in order, with their scores when <paramref name="scoresNeeded"/>, and the number of entries of the query (see <see cref="PresortedPageMatch"/>).</summary>
/// <param name="where">The match of the where clause of the main query, null when it has none: the entries of the page are among its entries. It is a <see cref="MemoizationMatch"/>: read every entry of it with
/// <see cref="MemoizationMatch.FillAndRetrieve"/> and score all of them with one call of <see cref="IQueryMatch.Score"/>, starting from <see cref="Corax.Utils.Bm25Relevance.InitialScoreValue"/>, as the sort of the server does.
/// A match that is read in pieces does not score the entries of the earlier pieces.</param>
/// <param name="whereHoldsEveryEntry">The where clause matches every entry of the index: nothing is filtered and the page is the first entries of the order. <paramref name="where"/> is then not read, and
/// <paramref name="whereScore"/> is what it adds to the score of each entry.</param>
/// <param name="take">The number of entries of the page counted from the first entry of the order, the skipped ones included; all of them when negative (the server asks for all of them when it cannot tell how many it needs).</param>
internal delegate IPresortedMatch CreateOrderedPage(IndexSearcher searcher, IQueryMatch where, bool whereHoldsEveryEntry, float whereScore, int take, bool scoresNeeded);

/// <summary>The answer of a join index to a JOIN: the match of the main query's entries that have a joined document.</summary>
internal sealed class JoinServed
{
    private readonly Func<IndexSearcher, bool, IQueryMatch> _createMatch;
    private readonly Func<string> _description;

    /// <param name="createMatch">Builds the match over the searcher of the main query when the query is built, which is too late to read the join as usual: an exception fails the query.
    /// The flag tells whether the score of the joined document is needed.</param>
    /// <param name="description">Shown in the build steps of the query: made when it is asked for, which most queries never do.</param>
    /// <param name="counters">Added to the timings of the join; null when the query does not collect them.</param>
    /// <param name="entriesOnly">Every entry the match returns is an entry of the searcher it is created for (the join index answered for the very transaction the query reads),
    /// so the server does not have to intersect it with the entries of the index, and a match that has a constant score and an exact count can be sorted by score without scoring all of it.</param>
    public JoinServed(Func<IndexSearcher, bool, IQueryMatch> createMatch, Func<string> description = null, IReadOnlyDictionary<string, long> counters = null, bool entriesOnly = false)
    {
        _createMatch = createMatch ?? throw new ArgumentNullException(nameof(createMatch));
        _description = description;
        Counters = counters;
        EntriesOnly = entriesOnly;
    }

    public bool EntriesOnly { get; }

    /// <summary>
    /// Set when the join index answers a join that orders by a field of the joined index (<see cref="JoinServeRequest.InnerOrder"/>): the server asks it for the page instead of for the match of the join,
    /// and does not scan, rank or sort.
    /// </summary>
    public CreateOrderedPage OrderedPage { get; init; }

    public string Description => _description?.Invoke();

    public IReadOnlyDictionary<string, long> Counters { get; }

    public IQueryMatch CreateMatch(IndexSearcher searcher, bool scoreNeeded) => _createMatch(searcher, scoreNeeded);
}
