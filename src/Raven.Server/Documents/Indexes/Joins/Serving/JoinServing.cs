using System;
using System.Collections.Generic;
using Corax.Querying;
using Corax.Querying.Matches.Meta;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>
/// Answers a JOIN from the lists of a join index. The join index snapshot of a query is taken before the query opens the snapshot of any index, so the join index is never ahead of what the query reads:
/// it is current (the lists are exactly the join of the two snapshots of the query), behind (the join is read as usual) or has another history than the indexes (the join is read as usual).
/// Nothing here writes.
/// </summary>
internal static class JoinServing
{
    /// <summary>What the lists of one join index are for a query: the snapshot, the generation it holds and the definition they were built for.</summary>
    public sealed class Target
    {
        public string Name { get; init; }

        public JoinDefinition Definition { get; init; }

        public JoinReader Snapshot { get; set; }

        public JoinIndexGeneration Generation { get; set; }

        /// <summary>Why the join index cannot answer (a <see cref="JoinFallbacks"/> name), null when it holds a snapshot.</summary>
        public string Unavailable { get; init; }

        /// <summary>The most outer entries of a join on a field that an AND or a NOT may check entry by entry.</summary>
        public long MaxResidualRows { get; init; } = long.MaxValue;
    }

    /// <summary>Null when the lists cannot answer the join, with <paramref name="fallback"/> set to the reason (a <see cref="JoinFallbacks"/> name).</summary>
    /// <param name="beforeCompare">Called when the generation of the target is about to be compared with what the query reads; for tests, it may replace the snapshot of the target.</param>
    public static JoinServed Serve(JoinServeRequest request, Target target, Action<Target> beforeCompare, out string fallback)
    {
        fallback = null;
        if (request.IsLeft && request.ScoresMayMatter == false)
        {
            // nothing uses the score of the join, so every entry stays whatever the sub-query matches: the join index is not read
            return new JoinServed((_, scoreNeeded) => scoreNeeded ? throw new InvalidOperationException("The score of a left join that nothing scores was asked for.") : null,
                () => "join index, left join that is not scored", request.Timings == null ? null : new Dictionary<string, long> { ["JoinIndexServed"] = 1 }, entriesOnly: false);
        }

        fallback = target.Unavailable;
        if (fallback != null)
            return null;

        JoinDefinition definition = target.Definition;
        if (JoinPredicateAnalyzer.TryAnalyze(request.Join, definition, request.Query.QueryParameters, request.EncodeInnerTerm, request.ScoresMayMatter, out JoinPredicate predicate, out fallback) == false)
            return null;

        beforeCompare?.Invoke(target);

        Outcome comparison = Compare(target.Generation, request.Outer.EnvironmentId, request.Outer.Consumed, request.Inner.EnvironmentId, request.Inner.Consumed);

        switch (comparison)
        {
            case Outcome.Equal:
                break;
            case Outcome.Behind:
                fallback = JoinFallbacks.BehindTimeout;
                return null;
            case Outcome.Ahead:
                fallback = JoinFallbacks.Ahead;
                return null;
            case Outcome.OtherHistory:
                fallback = JoinFallbacks.EnvironmentChanged;
                return null;
            default:
                throw new ArgumentOutOfRangeException();
        }

        JoinReader snapshot = target.Snapshot;
        long serial = target.Generation.Serial;
        float boost = request.Boost;

        // an outer entry that names several documents is in a list when any of them has the atom: the answers of the lists that are not exact for it are read by the server.
        // For a join on a field an entry is joined with several inner entries when its key has a group of two or more (the counter) or when it holds several keys with an inner entry (the other counter),
        // and it is joined with several keys, whose scores the join index cannot add, only in the second case
        bool fieldJoin = definition.InnerField != null;
        bool severalJoined = snapshot.RowsWithSeveralJoined > 0;
        bool severalKeys = fieldJoin && snapshot.RowsWithSeveralKeys > 0;
        bool verifyGroups = false;
        if (fieldJoin && request.InnerOrder != null)
        {
            // no value of the joined entries orders an outer entry whose key has many of them
            fallback = JoinFallbacks.OrderByMultiKey;
            return null;
        }

        if (fieldJoin)
        {
            // ScoresAsked is not enough: a query that is not ordered (a stream, the facets) may ask for the score of the join when it builds the match, which cannot fall back any more
            if (severalKeys && request.ScoresMayMatter)
            {
                fallback = JoinFallbacks.ScoresMultiKey;
                return null;
            }

            if ((severalJoined || severalKeys) && predicate.HasAndOrNot)
            {
                // the lists are exact for an outer entry that is joined with one inner entry; the others (a key with a group of two or more, an entry with several keys that have a group)
                // are checked against the inner index, as many as the budget allows
                if (request.ScanKeys == null || JoinGroupVerify.ResidualBound(snapshot, predicate) > target.MaxResidualRows)
                {
                    fallback = JoinFallbacks.GroupResidual;
                    return null;
                }

                verifyGroups = true;
            }
        }
        else if (severalJoined)
        {
            if (request.InnerOrder != null)
            {
                fallback = JoinFallbacks.OrderByMultiKey;
                return null;
            }

            if (request.ScoresMayMatter)
            {
                fallback = JoinFallbacks.ScoresMultiKey;
                return null;
            }

            if (predicate.HasAndOrNot)
            {
                fallback = JoinFallbacks.GroupResidual;
                return null;
            }
        }

        // the join index adds one score to every joined document: that is the score of each of them only when the sub-query gives every document it matches the same
        if (request.ScoresAsked && request.ScoresMayMatter)
        {
            JoinScoreProbe probe = request.ProbeInnerScore();
            if (probe.HasMatches && probe.IsUniform == false)
            {
                fallback = JoinFallbacks.KindNotServed("ScoresDiffer");
                return null;
            }
        }

        if (request.InnerOrder != null)
            return ServeOrdered(request, target, predicate, serial, boost, out fallback);

        return new JoinServed(
            (outer, scoreNeeded) => CreateMatch(outer, scoreNeeded, snapshot, predicate, request, boost, fieldJoin ? severalKeys : severalJoined, verifyGroups),
            () => $"join index '{target.Name}' generation {serial}, {predicate}",
            request.Timings == null ? null : new Dictionary<string, long> { ["JoinIndexServed"] = 1, ["JoinIndexGeneration"] = serial },
            // a single list or a union of terms holds existing entries once each; an AND, an OR or a NOT of lists may repeat entries, which the server removes when it intersects the match with all entries
            entriesOnly: predicate.Kind is JoinPredicateKind.Any or JoinPredicateKind.Term or JoinPredicateKind.Grouping or JoinPredicateKind.Range or JoinPredicateKind.Prefix);
    }

    private static JoinServed ServeOrdered(JoinServeRequest request, Target target, JoinPredicate predicate, long serial, float boost, out string fallback)
    {
        fallback = null;
        JoinReader snapshot = target.Snapshot;
        JoinOrderField order = request.InnerOrder[0];
        int field = -1;
        for (int f = 0; f < target.Definition.Fields.Count; f++)
        {
            JoinField declared = target.Definition.Fields[f];
            if (declared.Kind == JoinFieldKind.Exact && string.Equals(declared.Field, order.Field, StringComparison.Ordinal))
                field = f;
        }

        if (request.InnerOrder.Count != 1 || order.Type is not (OrderByFieldType.Implicit or OrderByFieldType.String) || field < 0)
        {
            fallback = JoinFallbacks.KindNotServed("OrderBy:" + order.Field);
            return null;
        }

        if (predicate.Kind != JoinPredicateKind.Any)
        {
            fallback = JoinFallbacks.KindNotServed("OrderedWhere");
            return null;
        }

        // a joined document without a value has no term to be found under: where it sorts is the business of the server
        if (snapshot.NumberOfInnerEntriesWithTerms(field) != snapshot.NumberOfInnerDocuments)
        {
            fallback = JoinFallbacks.KindNotServed("OrderByMissingValues");
            return null;
        }

        float joinScore = 0f;
        if (request.ScoresNeeded)
        {
            JoinScoreProbe probe = request.ProbeInnerScore();
            if (probe.HasMatches && probe.IsUniform == false)
            {
                fallback = JoinFallbacks.KindNotServed("ScoresDiffer");
                return null;
            }

            joinScore = probe.HasMatches ? probe.Score * boost : 0f;
        }

        return new JoinServed((_, _) => throw new InvalidOperationException("The join is answered with a page."), () => $"join index '{target.Name}' generation {serial}, order by {order.Field}",
            request.Timings == null ? null : new Dictionary<string, long> { ["JoinIndexServed"] = 1, ["JoinIndexGeneration"] = serial, ["JoinOrdered"] = 1 })
        {
            OrderedPage = (searcher, where, whereHoldsEveryEntry, whereScore, take, scoresNeeded) =>
            {
                float score = joinScore;
                if (scoresNeeded && request.ScoresNeeded == false)
                {
                    JoinScoreProbe probe = request.ProbeInnerScore();
                    if (probe.HasMatches && probe.IsUniform == false)
                        throw new InvalidOperationException("The documents that the sub-query of the join matches do not have the same score, which the join index assumes.");

                    score = probe.HasMatches ? probe.Score * boost : 0f;
                }

                return JoinOrderedPage.Create(searcher, snapshot, field, order.Ascending, where, whereHoldsEveryEntry, whereScore, take, scoresNeeded, score);
            }
        };
    }

    private static IQueryMatch CreateMatch(IndexSearcher outer, bool scoreNeeded, JoinReader snapshot, JoinPredicate predicate, JoinServeRequest request, float boost, bool severalJoined,
        bool verifyGroups)
    {
        if (scoreNeeded && severalJoined)
            throw new InvalidOperationException("The score of the join was asked for and an outer entry is joined with several documents, which the join index cannot add.");

        if (scoreNeeded && predicate.ScoresDiffer)
            throw new InvalidOperationException($"The documents that the sub-query of the join matches have different scores, which the join index cannot add for '{predicate}'.");

        IQueryMatch list = verifyGroups ? JoinGroupVerify.Create(outer, snapshot, predicate, request) : JoinMatchFactory.Create(outer, snapshot, predicate);
        if (scoreNeeded == false)
            return list;

        JoinScoreProbe probe = request.ProbeInnerScore();
        if (probe.HasMatches == false)
            return new JoinListMatch(outer.EmptyMatch(), 0f);

        if (probe.IsUniform == false)
            throw new InvalidOperationException($"The documents that the sub-query of the join matches do not have the same score, which the join index assumes for '{predicate}'.");

        return new JoinListMatch(list, probe.Score * boost);
    }

    public enum Outcome
    {
        Equal,
        Behind,
        Ahead,

        /// <summary>The cursors cannot be compared: another environment of an index, or other collections.</summary>
        OtherHistory
    }

    public static Outcome Compare(JoinIndexGeneration generation, Guid outerEnvironment, IReadOnlyDictionary<string, (long Doc, long Tombstone)> outerConsumed, Guid innerEnvironment,
        IReadOnlyDictionary<string, (long Doc, long Tombstone)> innerConsumed)
    {
        if (generation.HasSameSources(outerEnvironment, innerEnvironment) == false ||
            generation.Outer.HasSameCollections(outerConsumed) == false || generation.Inner.HasSameCollections(innerConsumed) == false)
            return Outcome.OtherHistory;

        GenerationComparison outer = generation.Outer.CompareTo(outerConsumed);
        GenerationComparison inner = generation.Inner.CompareTo(innerConsumed);

        if (outer == GenerationComparison.Ahead || inner == GenerationComparison.Ahead)
            return Outcome.Ahead;

        if (generation.IsPartial || outer == GenerationComparison.Behind || inner == GenerationComparison.Behind)
            return Outcome.Behind;

        return Outcome.Equal;
    }
}
