using System;
using System.Collections.Generic;
using System.Diagnostics;
using Corax.Querying;
using Corax.Querying.Matches.Meta;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using Voron.Util;

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

        /// <summary>The most keys of an Indirect file that an AND or a NOT may check against the inner index.</summary>
        public long MaxResidualKeys { get; init; } = long.MaxValue;

        /// <summary>The resident cache of an Indirect join index: the lists of the keys are read from it when the outer snapshot of the query is its epoch. Null for a join index that has none.</summary>
        public JoinResident Resident { get; init; }
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
        bool indirect = snapshot.Schema.IsIndirect;

        // the lists of an Indirect file do not depend on the outer entries, so the answer is exact for any outer snapshot; the rows with several keys that the score of the join depends on are the file's, though
        if (indirect && request.ScoresMayMatter && target.Generation.Outer.CompareTo(request.Outer.Consumed) != GenerationComparison.Equal)
        {
            fallback = JoinFallbacks.BehindTimeout;
            return null;
        }

        // an outer entry that names several documents is in a list when any of them has the atom: the answers of the lists that are not exact for it are read by the server.
        // For a join on a field an entry is joined with several inner entries when its key has a group of two or more (the counter) or when it holds several keys with an inner entry (the other counter),
        // and it is joined with several keys, whose scores the join index cannot add, only in the second case
        bool fieldJoin = snapshot.Schema.IsFieldJoin;
        bool severalJoined = snapshot.RowsWithSeveralJoined > 0;
        bool severalKeys = fieldJoin && snapshot.RowsWithSeveralKeys > 0;
        bool verifyGroups = false;
        if (fieldJoin && request.InnerOrder != null)
        {
            // no value of the joined entries orders an outer entry whose key has many of them; the page of a join on the id is read from the outer entries of the lists, which an Indirect file does not have
            fallback = definition.InnerField != null ? JoinFallbacks.OrderByMultiKey : JoinFallbacks.OrderByIndirect;
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

            // an Indirect file checks keys, not outer entries: only the keys with several inner entries need the inner index, a row with several keys is exact in the key domain
            if ((indirect ? severalJoined : severalJoined || severalKeys) && predicate.HasAndOrNot)
            {
                // the lists are exact for an outer entry (a key of an Indirect file) that is joined with one inner entry; the others (a key with a group of two or more, an entry with several keys that have a group)
                // are checked against the inner index, as many as the budget allows
                long budget = indirect ? target.MaxResidualKeys : target.MaxResidualRows;
                if (request.ScanKeys == null || JoinGroupVerify.ResidualBound(snapshot, predicate) > budget)
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

        JoinResidentRead resident = default;
        if (indirect && target.Resident != null && snapshot is JoinIndexSnapshot file)
            resident = target.Resident.ReadOf(request.Outer.EnvironmentId, request.Outer.TransactionId, file.Seed, predicate.Kind == JoinPredicateKind.Any ? JoinReader.AtomOfAny() : 0, request.Outer.Consumed);

        // no outer entry is in the lists of two keys when no row has two keys, in the outer snapshot that the file was built for, which is the one of the query
        bool disjointKeys = indirect && fieldJoin && severalKeys == false && target.Generation.Outer.CompareTo(request.Outer.Consumed) == GenerationComparison.Equal;
        var memo = new KeyMatchMemo();
        return new JoinServed(
            (outer, scoreNeeded) => CreateMatch(outer, scoreNeeded, snapshot, predicate, request, boost, fieldJoin ? severalKeys : severalJoined, verifyGroups, resident, memo, disjointKeys),
            () => $"join index '{target.Name}' generation {serial}, {predicate}",
            request.Timings == null ? null : new Dictionary<string, long> { ["JoinIndexServed"] = 1, ["JoinIndexGeneration"] = serial },
            // a single list or a union of terms holds existing entries once each; an AND, an OR or a NOT of lists may repeat entries, which the server removes when it intersects the match with all entries.
            // The union of the keys of an Indirect file holds the existing entries of the very snapshot the query reads, once each, for every predicate
            entriesOnly: indirect || predicate.Kind is JoinPredicateKind.Any or JoinPredicateKind.Term or JoinPredicateKind.Grouping or JoinPredicateKind.Range or JoinPredicateKind.Prefix);
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

    /// <summary>
    /// The entries of the keys that an Indirect join index answered with for one searcher, so that the match of a join in the where clause with a score (made twice, once for the entries and once for the score) reads
    /// the keys and the lists once. The matches are made over the same memory, each with its own position.
    /// </summary>
    private sealed class KeyMatchMemo
    {
        public IndexSearcher Searcher;
        public IQueryMatch Entries;
        public int Shared;
    }

    private static IQueryMatch CreateMatch(IndexSearcher outer, bool scoreNeeded, JoinReader snapshot, JoinPredicate predicate, JoinServeRequest request, float boost, bool severalJoined,
        bool verifyGroups, JoinResidentRead resident, KeyMatchMemo memo, bool disjointKeys)
    {
        if (scoreNeeded && severalJoined)
            throw new InvalidOperationException("The score of the join was asked for and an outer entry is joined with several documents, which the join index cannot add.");

        if (scoreNeeded && predicate.ScoresDiffer)
            throw new InvalidOperationException($"The documents that the sub-query of the join matches have different scores, which the join index cannot add for '{predicate}'.");

        IQueryMatch list;
        if (snapshot.Schema.IsIndirect)
        {
            if (ReferenceEquals(memo.Searcher, outer) && JoinKeyUnion.TryRestart(memo.Entries, out list))
            {
                request.Timings?.AddCounter("JoinKeyListsShared", ++memo.Shared);
            }
            else
            {
                long started = Stopwatch.GetTimestamp();
                using ContextBoundNativeList<long> keys = JoinKeyAlgebra.GoodKeys(outer.Allocator, snapshot, predicate, request, verifyGroups);
                long keysRead = Stopwatch.GetTimestamp();
                request.Timings?.AddCounter("JoinKeys", keys.Count);
                list = JoinKeyUnion.Create(outer, snapshot, keys.ToSpan(), request.OuterField, resident, union =>
                {
                    request.Timings?.AddCounter("JoinResidentKeys", union.KeysFromCache);
                    request.Timings?.AddCounter("JoinStoreKeys", union.KeysFromStore);
                    request.Timings?.AddCounter("JoinResidentEpochHit", union.EpochHit ? 1 : 0);
                }, disjointKeys);

                if (request.Timings != null)
                {
                    request.Timings.AddCounter("JoinKeysMicros", (long)Stopwatch.GetElapsedTime(started, keysRead).TotalMicroseconds);
                    request.Timings.AddCounter("JoinUnionMicros", (long)Stopwatch.GetElapsedTime(keysRead).TotalMicroseconds);
                }

                // taken before anything reads the match
                if (JoinKeyUnion.TryRestart(list, out IQueryMatch entries))
                {
                    memo.Searcher = outer;
                    memo.Entries = entries;
                }
            }
        }
        else
        {
            list = verifyGroups ? JoinGroupVerify.Create(outer, snapshot, predicate, request) : JoinMatchFactory.Create(outer, snapshot, predicate);
        }

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

        // the lists of an Indirect file are a function of the inner index alone: an outer index that is ahead or behind does not change what they say, and only a cut on the inner side is not served
        bool indirect = generation.Mode == JoinStorageMode.Indirect;
        GenerationComparison outer = indirect ? GenerationComparison.Equal : generation.Outer.CompareTo(outerConsumed);
        GenerationComparison inner = generation.Inner.CompareTo(innerConsumed);

        if (outer == GenerationComparison.Ahead || inner == GenerationComparison.Ahead)
            return Outcome.Ahead;

        if ((indirect ? generation.InnerPartial : generation.IsPartial) || outer == GenerationComparison.Behind || inner == GenerationComparison.Behind)
            return Outcome.Behind;

        return Outcome.Equal;
    }
}
