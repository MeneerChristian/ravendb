using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Corax;
using Corax.Mappings;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;
using Corax.Querying.Matches.SortingMatches.Meta;
using Corax.Utils;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Corax;
using Raven.Server.Documents.Indexes.Persistence.Corax.QueryOptimizer;
using Raven.Server.Documents.Indexes.Persistence.Lucene;
using Raven.Server.Documents.Indexes.Persistence.Lucene.Analyzers;
using Raven.Server.Documents.Indexes.Persistence.Lucene.Analyzers.Collation;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Json;
using Spatial4n.Shapes;
using ArgumentException = System.ArgumentException;
using RavenConstants = Raven.Client.Constants;
using IndexSearcher = Corax.Querying.IndexSearcher;
using CompositeJoinKeys = Corax.Querying.CompositeJoinKeys;
using JoinTermScores = Corax.Querying.JoinTermScores;
using CompositeKeyLimitException = Corax.Querying.CompositeKeyLimitException;
using CompositeKeyBuilder = Corax.Querying.CompositeKeyBuilder;
using CoraxConstants = Corax.Constants;
using SpatialUnits = Raven.Client.Documents.Indexes.Spatial.SpatialUnits;
using FieldIndexing = Raven.Client.Documents.Indexes.FieldIndexing;
using MoreLikeThisQuery = Raven.Server.Documents.Queries.MoreLikeThis.Corax;

namespace Raven.Server.Documents.Indexes.Persistence.Corax;

public static partial class CoraxQueryBuilder
{
    internal const int TakeAll = -1;
    
    public struct StreamingOptimization
    {
        public readonly OrderMetadata SortField;
        public readonly bool OptimizationIsPossible;
        public bool SkipOrderByClause;
        public bool MatchedByCompoundField;
        public bool BinaryMatchInQuery = false;
        public FieldMetadata CompoundField;
        public bool Forward => SortField.Ascending;

        public StreamingOptimization(IndexSearcher searcher, OrderMetadata[] orderMetadata, bool hasBoosting, bool dynamicFieldsMayBypassMarker)
        {
            bool hasSpecialSorter = false;
            foreach (var order in orderMetadata ?? Array.Empty<OrderMetadata>())
            {
                hasSpecialSorter |= order.FieldType is not (MatchCompareFieldType.Sequence or MatchCompareFieldType.Integer or MatchCompareFieldType.Floating);
                if (hasSpecialSorter)
                    break;
            }

            if (orderMetadata is null or { Length: 0 }
                || hasSpecialSorter
                || dynamicFieldsMayBypassMarker
                || searcher.HasMultipleTermsInField(orderMetadata[0].Field)
                || hasBoosting)
            {
                SortField = default;
                OptimizationIsPossible = false;
                return;
            }

            SortField = orderMetadata[0];
            OptimizationIsPossible = true;
        }

        public void BinaryMatchTraversed() => BinaryMatchInQuery = true;

        public bool TrySetMultiTermMatchAsStreamingField(in FieldMetadata queryField, in MethodType methodType)
        {
            if (OptimizationIsPossible == false)
                return false;

            if (FieldMetadataFieldEquals(queryField) == false)
                return false;

            if (BinaryMatchInQuery)
                return false;

            SkipOrderByClause = methodType switch
            {
                MethodType.StartsWith => SortField.FieldType is MatchCompareFieldType.Sequence,
                MethodType.EndsWith => SortField.FieldType is MatchCompareFieldType.Sequence,
                MethodType.Exists => SortField.FieldType is MatchCompareFieldType.Sequence,
                MethodType.Regex => SortField.FieldType is MatchCompareFieldType.Sequence,
                _ => false
            };

            return SkipOrderByClause;
        }

        public bool TrySetAsStreamingField(Parameters builderParameters, in CoraxBooleanItem cbi, IQueryMatch right)
        {
            if (OptimizationIsPossible == false)
                return false;

            if (cbi.Operation is UnaryMatchOperation.NotEquals)
                return false;

            if (SkipOrderByClause && OptimizationIsPossible)
                ThrowFieldIsAlreadyChosen();

            if (cbi.Field.Equals(SortField.Field) == false)
            {
                // Currently, CompoundField does not support two WHERE condition. Do not mark it as a compound field to avoid optimization that could
                // be degradated to a worse query plan.
                if (builderParameters.Index.HasCompoundField(cbi.Field.FieldName, SortField.Field.FieldName, out var bindingId) && right is null)
                {
                    var indexFieldBinding = builderParameters.IndexFieldsMapping.GetByFieldId(bindingId);
                    CompoundField = FieldMetadata.Build(indexFieldBinding.FieldName, indexFieldBinding.FieldTermTotalSumField,
                        indexFieldBinding.FieldId, indexFieldBinding.FieldIndexingMode, indexFieldBinding.Analyzer);
                    MatchedByCompoundField = true;
                    return true;
                }
                return false;
            }

            //In case of MultiTermMatch we cannot apply optimization when:
            //- MTM is inside BinaryMatch
            if (cbi.Operation is not UnaryMatchOperation.Equals && BinaryMatchInQuery)
                return false;

            // In case of TermMatch `order by` has to have exactly the same type as queried field.
            // Since we store number as tuple of (String, Double, Long) we could write query like this:
            // where Numeric = 1 order by Numeric as double. Where clause will match all documents where Numeric casted to long is equal 1 (e.g. 1.2, 1.3, 1.9) but order by will sort with precision.
            // This also valid for MultiTermMatch.
            bool matchOnType = cbi.Term switch
            {
                long => SortField.FieldType is MatchCompareFieldType.Integer,
                double or float => SortField.FieldType is MatchCompareFieldType.Floating,
                _ => SortField.FieldType is MatchCompareFieldType.Sequence
            };

            //Todo: when any document contains list in sorted field we may consider disabling streaming optimization.  

            if (matchOnType == false)
                return false;

            SkipOrderByClause = true;
            return true;
        }

        public bool FieldMetadataFieldEquals(in FieldMetadata otherMetadata) => SortField.Field.Equals(otherMetadata);

        private static void ThrowFieldIsAlreadyChosen()
        {
            throw new InvalidOperationException($"We match TermMatch with the OrderByField but somehow we still wanted to check that. This is bug.");
        }
    }

    private static IQueryMatch MaterializeWhenNeeded(Parameters builderParameters, IQueryMatch source, ref StreamingOptimization streamingConfiguration)
    {
        switch (source)
        {
            case CoraxBooleanQueryBase cbq:
                source = cbq.Materialize();
                break;
            case CoraxBooleanItem cbi:
                streamingConfiguration.TrySetAsStreamingField(builderParameters, cbi, null);
                if (streamingConfiguration.MatchedByCompoundField)
                    return cbi.OptimizeCompoundField(ref streamingConfiguration);

                source = cbi.Materialize(ref streamingConfiguration);
                break;
            case CoraxVectorItem cvi:
                source = cvi.Materialize(null);
                break;
        }

        return source;
    }

    internal static IQueryMatch BuildQuery(Parameters builderParameters, out OrderMetadata[] sortMetadata)
    {
        using (CultureHelper.EnsureInvariantCulture())
        {
            IQueryMatch coraxQuery;
            var metadata = builderParameters.Query.Metadata;
            var indexSearcher = builderParameters.IndexSearcher;
            sortMetadata = GetSortMetadata(builderParameters, out var hasEmptySortingMatches);
            var dynamicFieldsMayBypassMarker = builderParameters.HasDynamics
                                               && IndexDefinitionBaseServerSide.IndexVersion.IsPerEntryMultipleTermsMarkerSupported(builderParameters.Index.Definition.Version) == false;
            builderParameters.JoinScoresNeeded = metadata.Query.Joins != null && (builderParameters.NeedsScoresBuffer() || HasScoreOrdering(sortMetadata) || metadata.HasMoreLikeThis);
            // a LEFT JOIN that nothing scores does not change which entries match or in which order
            var joinsChangeMatches = HasJoinThatChangesMatches(builderParameters);
            var streamingOptimization = new StreamingOptimization(indexSearcher, sortMetadata, builderParameters.HasBoost || joinsChangeMatches, dynamicFieldsMayBypassMarker);

            if (metadata.Query.Where is not null)
            {
                coraxQuery = ToCoraxQuery(builderParameters, metadata.Query.Where, ref streamingOptimization);
                coraxQuery = coraxQuery is CoraxWhenQuery 
                    ? builderParameters.AllEntries.Replay() 
                    : MaterializeWhenNeeded(builderParameters, coraxQuery, ref streamingOptimization);
            }
            else if (sortMetadata is [{ FieldType: MatchCompareFieldType.Floating or MatchCompareFieldType.Integer or MatchCompareFieldType.Sequence } sortBy, ..]
                     && dynamicFieldsMayBypassMarker == false
                     && joinsChangeMatches == false // the join is applied on top of the matches, the sort field cannot be streamed
                     && indexSearcher.SortFieldTreeCoversAllEntries(sortBy.Field, sortBy.FieldType))
            {
                // The cap counts terms, so it may only be applied while every scanned term yields a returned document.
                // It does not when a document holds several terms (RavenDB-27514) or several entries, and not when
                // something rejects documents after the scan, because there is no way to scan more (RavenDB-27564).
                var everyTermYieldsAReturnedDocument = indexSearcher.HasMultipleTermsInField(sortBy.Field) == false
                                                       && indexSearcher.EntryIdPaginationSupportStatus == EntryIdPaginationSupportStatus.Supported
                                                       && metadata.FilterScript is null;

                var maxTermToScan = builderParameters.Take switch
                {
                    < 0 => int.MaxValue, // meaning, take all
                    // We cannot apply this optimization when we are returning statistics (RavenDB-21525).
                    var take when builderParameters.Query.SkipStatistics && everyTermYieldsAReturnedDocument => (long)take + 1,
                    int.MaxValue => (long)int.MaxValue + 1, // avoid overflow
                    _ => int.MaxValue
                };

                // We have no where clause and a sort by index, can just scan over the relevant index if there is a single order by clause
                // if we have multiple clauses, we'll get the first $TAKE+1 terms from the index, then sort just those, leading to the same
                // behavior, but far faster
                var betweenQuery = sortBy.FieldType switch
                {
                    MatchCompareFieldType.Integer => indexSearcher.BetweenQuery(sortBy.Field, long.MinValue, long.MaxValue, forward: sortBy.Ascending, streamingEnabled: true, maxNumberOfTerms: maxTermToScan),
                    MatchCompareFieldType.Floating => indexSearcher.BetweenQuery(sortBy.Field, double.MinValue, double.MaxValue, forward: sortBy.Ascending, streamingEnabled: true, maxNumberOfTerms: maxTermToScan),
                    MatchCompareFieldType.Sequence => indexSearcher.BetweenQuery(sortBy.Field, Constants.BeforeAllKeys, Constants.AfterAllKeys, forward: sortBy.Ascending, streamingEnabled: true, maxNumberOfTerms: maxTermToScan),
                    _ => throw new ArgumentOutOfRangeException("Already checked the FieldType, but was: " + sortBy.FieldType)
                };

                var configIsSmallest = builderParameters.Index.Configuration.NullsSortMode is NullsSortMode.NullsSmallest;
                var nullIsSmallest = sortBy.NullsSortMode is { } sortByMode
                    ? sortByMode == NullsSortMode.NullsSmallest
                    : configIsSmallest;
                var queryWithNullMatches = indexSearcher.IncludeNullMatch(in sortBy.Field, betweenQuery, sortBy.Ascending, nullIsSmallest);

                var indexVersion = builderParameters.Index.Definition.Version;

                if (IndexDefinitionBaseServerSide.IndexVersion.IsNonExistingPostingListSupported(indexVersion))
                {
                    var queryWithNullAndNonExistingMatches = indexSearcher.IncludeNonExistingMatch(in sortBy.Field, queryWithNullMatches, sortBy.Ascending, nullIsSmallest);
                    coraxQuery = queryWithNullAndNonExistingMatches;
                }

                else
                    coraxQuery = queryWithNullMatches;

                streamingOptimization.SkipOrderByClause = true; // manually turn off the order by
            }
            else
            {
                coraxQuery = indexSearcher.Memoize(indexSearcher.AllEntries()).Replay();
            }

            if (metadata.Query.Joins != null)
            {
                var whereMatch = metadata.Query.Where is null ? null : coraxQuery;
                var walk = JoinOrderedWalkOf(builderParameters, sortMetadata, whereMatch) ?? JoinFirstEntriesOf(builderParameters, sortMetadata, whereMatch);
                if (walk != null)
                {
                    coraxQuery = walk;
                    streamingOptimization.SkipOrderByClause = true;
                }
                else
                {
                // the join matches are not ordered by the sort field
                ScoreMapMatch? ranks = null;
                var ranksAreRelevance = false;
                var coraxIsAllEntries = metadata.Query.Where is null;
                for (var i = 0; i < metadata.Query.Joins.Count; i++)
                {
                    if (metadata.Query.Joins[i].ReferencedInWhere)
                        continue;

                    if (metadata.Query.Joins[i].IsLeft)
                    {
                        // an optional join: every entry stays, the score of the joined document is added to the entries that have one
                        var leftMatch = BuildJoinMatch(builderParameters, i, leftJoin: true);
                        if (leftMatch is ScoreMapMatch scores)
                        {
                            if (scores.Count > 0)
                            {
                                coraxQuery = new ScoreAddMatch<IQueryMatch>(coraxQuery, scores);
                                coraxIsAllEntries = false;
                            }

                            // the entries without a joined document have no rank: they sort last
                            if (builderParameters.Query.JoinResults[i].RankedByInnerOrder)
                            {
                                ranks = scores;
                                ranksAreRelevance = builderParameters.Query.JoinResults[i].Keys.HasRanks;
                            }
                        }

                        continue;
                    }

                    var joinMatch = BuildJoinMatch(builderParameters, i);
                    coraxQuery = indexSearcher.And(coraxQuery, joinMatch);
                    coraxIsAllEntries = false;
                    if (builderParameters.Query.JoinResults[i].RankedByInnerOrder && joinMatch is ScoreMapMatch joinRanks)
                    {
                        ranks = joinRanks;
                        ranksAreRelevance = builderParameters.Query.JoinResults[i].Keys.HasRanks;
                    }
                }

                // the position in the order of the join is the sort key, the score stays the relevance
                if (ranks != null)
                    coraxQuery = new RankedMatch<IQueryMatch>(coraxQuery, ranks.Value, ranksAreRelevance);

                if (joinsChangeMatches)
                    streamingOptimization.SkipOrderByClause = false;
                }
            }

            if (sortMetadata is not null)
            {
                // In the case of ordering by multiple fields, we still want to benefit from the streaming
                // optimization, but we have to sort again anyway, this ensure that this happens
                if (streamingOptimization.SkipOrderByClause == false || sortMetadata.Length > 1)
                    coraxQuery = OrderBy(builderParameters, coraxQuery, sortMetadata, hasEmptySortingMatches);
            }

            // The parser already throws parse exception if there is a syntax error.
            // We now return null in the case of a term query that has been fully analyzed, so we need to return a valid query.
            if (builderParameters.DeduplicationDisabled || coraxQuery.DuplicatesOccurrenceStatus == DuplicatesOccurrence.NotPossible)
                return coraxQuery;

            return DeduplicationMatch(builderParameters, coraxQuery);
        }
    }

    private static bool HasScoreOrdering(OrderMetadata[] sortMetadata)
    {
        if (sortMetadata is null)
            return false;

        foreach (var sort in sortMetadata)
        {
            if (sort.FieldType is MatchCompareFieldType.Score or MatchCompareFieldType.Rank)
                return true;
        }

        return false;
    }

    private static bool HasJoinThatChangesMatches(Parameters builderParameters)
    {
        var joins = builderParameters.Metadata.Query.Joins;
        if (joins == null)
            return false;

        for (var i = 0; i < joins.Count; i++)
        {
            if (joins[i].Filters)
                return true;

            if (builderParameters.JoinScoresNeeded && (builderParameters.Query.JoinResults?[i].Boost != 0f || builderParameters.Query.JoinResults[i].RankedByInnerOrder))
                return true;
        }

        return false;
    }

    // The join that orders by its sub-query, answered by the first entries of the order of the joined index. Null when the rank of every entry is needed.
    private static IQueryMatch JoinOrderedWalkOf(Parameters builderParameters, OrderMetadata[] sortMetadata, IQueryMatch outer)
    {
        var metadata = builderParameters.Metadata;
        var join = metadata.Query.Joins[0];
        var joinResult = builderParameters.Query.JoinResults?[0];
        if (joinResult?.OrderedKeys == null || builderParameters.AllowJoinOrderedWalk == false || metadata.Query.Joins.Count != 1)
            return null;

        if (sortMetadata is not [{ FieldType: MatchCompareFieldType.Rank }] || builderParameters.Take < 0 || (metadata.Query.Where is not null && outer is null))
            return null;

        if (builderParameters.NeedsScoresBuffer() || metadata.HasMoreLikeThis || metadata.HasVectorSearch || metadata.HasHighlightings || metadata.HasFacet || metadata.HasExplanations)
            return null;

        var (fieldName, fieldMetadata) = OuterJoinField(builderParameters, join);

        // an entry under several keys would be counted and returned once for each of them
        if (IndexDefinitionBaseServerSide.IndexVersion.IsPerEntryMultipleTermsMarkerSupported(builderParameters.Index.Definition.Version) == false
            || builderParameters.IndexSearcher.HasMultipleTermsInField(fieldMetadata))
            return null;

        var keysAreTerms = ResolveJoinKeyCase(builderParameters, join, joinResult, fieldName, fieldMetadata) == false && joinResult.KeysAreTerms;
        var timings = joinResult.Timings;
        var stopwatch = Stopwatch.StartNew();
        var match = builderParameters.IndexSearcher.JoinOrderedWalk(fieldMetadata, joinResult.Keys, joinResult.OrderedKeys, keysAreTerms, outer, builderParameters.Take);
        timings?.AddCounter("JoinOrderedWalkCountMicroseconds", stopwatch.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
        timings?.AddCounter("JoinOrderedWalk", 1);
        builderParameters.BuildSteps?.Add($"Join ordered walk: {join.On} ({joinResult.Keys.Count} keys, {match.TotalResults} entries)");
        return match;
    }

    // The join of a query without an order whose page is read from the first matches: the entries are tested in the order of their ids, the lists of the keys are not merged. Null when the join has to be read whole.
    // A query that has a join and no ORDER BY orders its matches by score, which does not order them when every key of the join has the same score
    private static IQueryMatch JoinFirstEntriesOf(Parameters builderParameters, OrderMetadata[] sortMetadata, IQueryMatch outer)
    {
        var metadata = builderParameters.Metadata;
        if (JoinFirstEntriesSettings.Enabled == false || builderParameters.AllowJoinFirstEntries == false || metadata.Query.Joins.Count != 1)
            return null;

        var orderedByScoreOnly = sortMetadata is [{ FieldType: MatchCompareFieldType.Score }] && metadata.OrderBy == null;
        if (sortMetadata != null && orderedByScoreOnly == false)
            return null;

        var join = metadata.Query.Joins[0];
        var joinResult = builderParameters.Query.JoinResults?[0];
        if (joinResult == null || joinResult.RankedByInnerOrder || joinResult.OrderedKeys != null)
            return null;

        if (join.IsLeft || join.IsComposite || join.ReferencedInWhere || join.Compare != null || join.Query.Joins != null)
            return null;

        if (builderParameters.Take < 0 || builderParameters.Take > JoinFirstEntriesSettings.MaximumTake || metadata.FilterScript is not null)
            return null;

        // the scores of the entries of a where, and the scores that are returned, are not the ones of the keys
        if (orderedByScoreOnly ? outer != null || builderParameters.NeedsScoresBuffer() || KeysScoreDiffer(joinResult.Keys) : builderParameters.JoinScoresNeeded)
            return null;

        if (metadata.HasMoreLikeThis || metadata.HasVectorSearch || metadata.HasHighlightings || metadata.HasFacet || metadata.HasExplanations)
            return null;

        var (fieldName, fieldMetadata) = OuterJoinField(builderParameters, join);

        // an entry under several keys would be counted and returned once for each of them
        if (IndexDefinitionBaseServerSide.IndexVersion.IsPerEntryMultipleTermsMarkerSupported(builderParameters.Index.Definition.Version) == false
            || builderParameters.IndexSearcher.HasMultipleTermsInField(fieldMetadata))
            return null;

        var keysAreTerms = ResolveJoinKeyCase(builderParameters, join, joinResult, fieldName, fieldMetadata) == false && joinResult.KeysAreTerms;
        var keysReachTerms = keysAreTerms || JoinEncodedKeys.KeysReachTreeUnchanged(joinResult.InnerIndex, builderParameters.Index, fieldName);
        var countTotal = builderParameters.Query.IsCountQuery || builderParameters.Query.SkipStatistics == false;
        var timings = joinResult.Timings;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        if (builderParameters.IndexSearcher.TryJoinFirstEntries(fieldMetadata, joinResult.Keys, keysAreTerms, UseEncodedKeys(builderParameters, joinResult, fieldName), keysReachTerms, outer,
                builderParameters.Take, countTotal, out var match) == false)
            return null;

        timings?.AddCounter("JoinFirstEntries", 1);
        timings?.AddCounter("JoinFirstEntriesMicroseconds", stopwatch.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
        timings?.AddCounter("JoinFirstEntriesScanned", match.ScannedEntries);
        timings?.AddCounter("JoinFirstEntriesKeysProbed", match.ProbedKeys ? 1 : 0);
        timings?.AddCounter("JoinFirstEntriesFromLists", match.CollectedFromLists ? 1 : 0);
        timings?.AddCounter("OuterMatchKeys", joinResult.Keys.Count);
        timings?.AddCounter("OuterMatchAllocatedManagedBytes", GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        builderParameters.BuildSteps?.Add($"Join first entries: {join.On} ({joinResult.Keys.Count} keys, {(match.TotalIsKnown ? match.TotalResults.ToString() : "unknown number of")} entries)");
        return match;
    }

    private static bool KeysScoreDiffer(JoinTermScores keys)
    {
        for (var i = 1; i < keys.Count; i++)
        {
            if (keys.Score(i) != keys.Score(0))
                return true;
        }

        return false;
    }

    private static (string FieldName, FieldMetadata Field) OuterJoinField(Parameters builderParameters, JoinClause join)
    {
        var metadata = builderParameters.Metadata;
        var fieldName = QueryBuilderHelper.ExtractIndexFieldName(metadata.Query, builderParameters.QueryParameters, join.On.Left, metadata);
        var exact = QueryBuilderHelper.IsExact(builderParameters.Index, false, fieldName);
        var fieldMetadata = QueryBuilderHelper.GetFieldMetadata(builderParameters.Allocator, fieldName, builderParameters.Index, builderParameters.IndexFieldsMapping,
            builderParameters.FieldsToFetch, builderParameters.HasDynamics, builderParameters.DynamicFields, exact: exact);
        return (fieldName, fieldMetadata);
    }

    private static IQueryMatch BuildJoinMatch(Parameters builderParameters, int joinIndex, bool leftJoin = false, bool inWhere = false)
    {
        builderParameters.Query.JoinResults[joinIndex].EnsureRanked();
        if (builderParameters.Query.JoinResults[joinIndex].RankedByInnerOrder)
            builderParameters.Query.JoinResults[joinIndex].Timings?.AddCounter("JoinOrderedWalk", 0);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var joinMatch = JoinMatch(builderParameters, joinIndex, builderParameters.JoinScoresNeeded, leftJoin, inWhere);

        // reported next to the scan statistics of the same join
        var joinTimings = builderParameters.Query.JoinResults[joinIndex].Timings;
        joinTimings?.AddCounter("OuterMatchKeys", builderParameters.Query.JoinResults[joinIndex].Keys?.Count ?? 0);
        joinTimings?.AddCounter("OuterMatchAllocatedManagedBytes", GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        return joinMatch;
    }

    private static IQueryMatch MatchesJoin(Parameters builderParameters, MethodExpression matches)
    {
        var alias = ((FieldExpression)matches.Arguments[0]).Compound[0];
        var joins = builderParameters.Metadata.Query.Joins;
        for (var i = 0; i < joins.Count; i++)
        {
            if (joins[i].Alias == alias)
                return BuildJoinMatch(builderParameters, i, inWhere: true);
        }

        throw new InvalidQueryException($"matches({alias}) refers to '{alias}', which is not the alias of a JOIN of this query.", builderParameters.Metadata.QueryText, builderParameters.QueryParameters);
    }

    // Restricts the query to the entries whose join field equals one of the keys returned by the JOIN sub-query
    private static IQueryMatch JoinMatch(Parameters builderParameters, int joinIndex, bool scoreNeeded, bool leftJoin = false, bool inWhere = false)
    {
        var metadata = builderParameters.Metadata;
        var join = metadata.Query.Joins[joinIndex];
        var keys = builderParameters.Query.JoinResults?[joinIndex].Keys
                   ?? throw new InvalidQueryException("JOIN is not supported in this kind of query (it works in queries, index entries queries, streaming and facets, " +
                                                      "not in suggestions or more-like-this queries).", metadata.QueryText, builderParameters.QueryParameters);

        var (fieldName, fieldMetadata) = OuterJoinField(builderParameters, join);

        var joinResult = builderParameters.Query.JoinResults[joinIndex];
        if (join.IsComposite)
            return CompositeJoinMatch(builderParameters, join, joinResult, scoreNeeded, leftJoin);

        // a field that lowercases is searched case-insensitively
        var fold = ResolveJoinKeyCase(builderParameters, join, joinResult, fieldName, fieldMetadata);
        var keysAreTerms = fold == false && joinResult.KeysAreTerms;

        if (join.Compare != null)
        {
            // the rows that have a joined document, kept when the fields of the row and of its joined document compare as asked
            var filter = JoinCompareFilter.Create(builderParameters.IndexSearcher, metadata, builderParameters.QueryParameters, builderParameters.Index, joinResult, join.Compare);
            builderParameters.BuildSteps?.Add($"Join compare: {join.Alias} ({keys.Count} keys)");
            return new JoinCompareMatch(ScoreMapOfJoinKeys(builderParameters, joinResult, fieldName, fieldMetadata, fold, keysAreTerms), filter);
        }

        // a join with BOOST 0 adds nothing to the score (the ranks of a join that orders are still needed)
        scoreNeeded &= joinResult.Boost != 0f || joinResult.RankedByInnerOrder;

        builderParameters.BuildSteps?.Add($"{(leftJoin ? "Left join" : "Join")}: {join.On} ({keys.Count} keys, scores {(scoreNeeded ? "used" : "not needed")}, boost {joinResult.Boost})");

        // a LEFT JOIN that is not scored adds nothing, every entry stays
        if (leftJoin && scoreNeeded == false)
            return null;

        if (scoreNeeded == false)
        {
            // nothing looks at the scores, so a plain IN is all that is needed
            if (keys.Count == 0)
                return builderParameters.IndexSearcher.EmptyMatch();

            // the keys of a join on a field are terms of the joined index: they are matched as they are
            return builderParameters.IndexSearcher.InQuery(fieldMetadata, keys, keysAreTerms, builderParameters.Token, UseEncodedKeys(builderParameters, joinResult, fieldName));
        }

        return ScoreMapOfJoinKeys(builderParameters, joinResult, fieldName, fieldMetadata, fold, keysAreTerms);
    }

    // The keys that fold to the same term are one key: the score of a row counts the best of their scores once
    private static ScoreMapMatch ScoreMapOfJoinKeys(Parameters builderParameters, JoinResult joinResult, string fieldName, in FieldMetadata fieldMetadata, bool fold, bool keysAreTerms)
    {
        if (fold == false)
            return builderParameters.IndexSearcher.ScoreMap(fieldMetadata, joinResult.Keys, keysAreTerms, joinResult.Boost, UseEncodedKeys(builderParameters, joinResult, fieldName));

        joinResult.FoldedDistinctKeys ??= CompositeJoinKeys.FoldDistinct(joinResult.InnerAllocator, joinResult.Keys);
        return builderParameters.IndexSearcher.ScoreMap(fieldMetadata, joinResult.FoldedDistinctKeys, termsAreIndexedTerms: true, scoreFactor: joinResult.Boost);
    }

    // The keys are ids kept as the joined index encoded them, and the outer field holds them unchanged
    private static bool UseEncodedKeys(Parameters builderParameters, JoinResult joinResult, string outerField)
        => joinResult.Keys.IsEncoded && JoinEncodedKeys.KeysReachTreeUnchanged(joinResult.InnerIndex, builderParameters.Index, outerField);

    // A join on several fields: the entries that hold the value of the first field of one of the keys are the candidates
    private static IQueryMatch CompositeJoinMatch(Parameters builderParameters, JoinClause join, JoinResult joinResult, bool scoreNeeded, bool leftJoin)
    {
        var metadata = builderParameters.Metadata;
        var pairs = join.Pairs.ToList();
        var outerFields = new string[pairs.Count];
        var fold = new bool[pairs.Count];
        FieldMetadata first = default;
        for (var i = 0; i < pairs.Count; i++)
        {
            var fieldName = QueryBuilderHelper.ExtractIndexFieldName(metadata.Query, builderParameters.QueryParameters, pairs[i].On.Left, metadata);
            outerFields[i] = fieldName.Value;
            var exact = QueryBuilderHelper.IsExact(builderParameters.Index, false, fieldName);
            var fieldMetadata = QueryBuilderHelper.GetFieldMetadata(builderParameters.Allocator, fieldName, builderParameters.Index, builderParameters.IndexFieldsMapping,
                builderParameters.FieldsToFetch, builderParameters.HasDynamics, builderParameters.DynamicFields, exact: exact);
            if (i == 0)
                first = fieldMetadata;

            fold[i] = ResolveJoinKeyCase(builderParameters, join, joinResult, fieldName, fieldMetadata, joinResult.PairKeyProbes[i], pairs[i].InnerField, joinResult.PairInnerModes[i]);
        }

        if (fold.Contains(true))
            joinResult.CompositeProbe ??= CompositeJoinKeys.Fold(joinResult.InnerAllocator, joinResult.Keys, fold);

        scoreNeeded &= joinResult.Boost != 0f;
        builderParameters.BuildSteps?.Add($"{(leftJoin ? "Left join" : "Join")} on {pairs.Count} fields: {join.On} ({joinResult.Keys.Count} keys, scores {(scoreNeeded ? "used" : "not needed")}, boost {joinResult.Boost})");

        if (leftJoin && scoreNeeded == false)
            return null;

        if (joinResult.Keys.Count == 0)
            return builderParameters.IndexSearcher.EmptyMatch();

        var searcher = builderParameters.IndexSearcher;
        var candidates = searcher.ScoreMap(first, joinResult.CompositeFirstTerms, termsAreIndexedTerms: fold[0] == false);
        try
        {
            return searcher.ScoreMapOfCompositeKeys(candidates, outerFields, joinResult.CompositeProbe ?? joinResult.Keys, scoreNeeded ? joinResult.Boost : 0f);
        }
        catch (CompositeKeyLimitException e)
        {
            throw new InvalidQueryException($"An entry of the query has fields ({string.Join(", ", outerFields)}) that hold more combinations of values than the {CompositeKeyBuilder.MaxCombinations} that are supported by {join.On}.",
                metadata.QueryText, builderParameters.QueryParameters, e);
        }
    }

    // A value with upper case letters and no separator, so that every analyzer maps it to a single token
    internal const string JoinKeyProbe = "\u00c4bCZ1";

    internal static string AnalyzeJoinKeyProbe(IndexSearcher searcher, in FieldMetadata field)
        => Encoding.UTF8.GetString(searcher.EncodeAndApplyAnalyzer(field, JoinKeyProbe).AsReadOnlySpan());

    // A value with separators and upper case letters
    internal const string JoinKeySeparatorProbe = "Ab-C/D e";

    // A join compares the terms of the index
    private static bool ResolveJoinKeyCase(Parameters builderParameters, JoinClause join, JoinResult result, string fieldName, in FieldMetadata lookup)
    {
        var fold = ResolveJoinKeyCase(builderParameters, join, result, fieldName, lookup, result.KeyProbe, join.InnerField, result.InnerIndexingMode);
        if (fold)
            result.FoldCase = true;

        return fold;
    }

    private static bool ResolveJoinKeyCase(Parameters builderParameters, JoinClause join, JoinResult result, string fieldName, in FieldMetadata lookup, string keyProbe, string innerField, FieldIndexingMode innerMode)
    {
        if (keyProbe == null)
            return false;

        // the ids of the entries are lowercased: the keys of a joined field that keeps the case are lowercased to match them
        if (fieldName == RavenConstants.Documents.Indexing.Fields.DocumentIdFieldName)
            return result.KeysAreTerms && keyProbe == JoinKeyProbe;

        // a Key field is lowercased like an id, and like another Key field
        var outerIsKey = IsKeyField(builderParameters.Index, fieldName);
        if (outerIsKey && (result.KeysAreTerms == false || IsKeyField(result.InnerIndex, innerField)))
            return false;

        if (builderParameters.IndexFieldsMapping.TryGetByFieldName(builderParameters.Allocator, fieldName, out var binding) == false)
            return false;

        var searcher = builderParameters.IndexSearcher;
        var stored = AnalyzeJoinKeyProbe(searcher, binding.Metadata);
        // the keys of a field join are terms that are matched as they are
        var looked = result.KeysAreTerms ? keyProbe : Encoding.UTF8.GetString(searcher.EncodeAndApplyAnalyzer(lookup, keyProbe).AsReadOnlySpan());

        if (stored == looked)
            return false;

        if (result.KeysAreTerms && keyProbe == JoinKeyProbe && binding.FieldIndexingMode == FieldIndexingMode.Normal && stored == JoinKeyProbe.ToLowerInvariant() &&
            Encoding.UTF8.GetString(searcher.EncodeAndApplyAnalyzer(binding.Metadata, JoinKeySeparatorProbe).AsReadOnlySpan()) == JoinKeySeparatorProbe.ToLowerInvariant())
        {
            return true;
        }

        var outerIndexing = DescribeIndexing(binding.FieldIndexingMode);
        string message;
        if (result.KeysAreTerms)
        {
            message = $"The JOIN with '{join.Alias}' would never match: the field '{fieldName}' of the index '{builderParameters.Index.Name}' is {outerIndexing} (the value '{JoinKeyProbe}' is indexed as '{stored}'), " +
                      $"but the field '{innerField}' of the joined index '{result.InnerIndexName}' is {DescribeIndexing(innerMode)} ('{JoinKeyProbe}' is indexed as '{keyProbe}'), " +
                      "and a join compares the terms of both fields as they are. Index both fields the same way (both with the default indexing, or both Exact), or index them with FieldIndexing.Key.";
        }
        else
        {
            message = $"The JOIN with '{join.Alias}' would never match: the field '{fieldName}' of the index '{builderParameters.Index.Name}' is {outerIndexing} (the value '{JoinKeyProbe}' is indexed as '{stored}'), " +
                      $"but a join on id({join.Alias}) compares the lowercased ids of the documents of '{join.Alias}' ('{JoinKeyProbe}' becomes '{looked}'). " +
                      $"Index '{fieldName}' with the default indexing (remove the Exact option or the custom analyzer) so that its terms are lowercased too, or index it with FieldIndexing.Key. " +
                      $"A field with the default indexing is searched case-insensitively, like a document id (where {fieldName} = 'COMPANIES-8213-A' finds 'companies-8213-a'), and this is what a join on id({join.Alias}) compares.";
        }

        throw new InvalidQueryException(message, builderParameters.Metadata.QueryText, builderParameters.QueryParameters);
    }

    private static bool IsKeyField(Index index, string field)
        => field != null && index.Definition.IndexFields.TryGetValue(field, out var indexField) && indexField.Indexing == FieldIndexing.Key;

    private static string DescribeIndexing(FieldIndexingMode mode) => mode switch
    {
        FieldIndexingMode.Exact => "indexed Exact",
        FieldIndexingMode.Search => "indexed Search",
        _ => "indexed with the default indexing"
    };

    private static IQueryMatch DeduplicationMatch(Parameters parameters, IQueryMatch match)
    {
        var type = match.GetType();
        if (type == typeof(MultiTermMatch))
            return Build<MultiTermMatch>();
        if (type == typeof(BinaryMatch))
            return Build<BinaryMatch>();
        if (type == typeof(AndNotMatch))
            return Build<AndNotMatch>();
        if (type == typeof(BoostingMatch))
            return Build<BoostingMatch>();
        if (type == typeof(MultiUnaryMatch))
            return Build<MultiUnaryMatch>();
        
        return Build<IQueryMatch>();

        IQueryMatch Build<TInner>() where TInner : IQueryMatch => parameters.IndexSearcher.DeduplicationMatch((TInner)match);
    }

    private static IQueryMatch ToCoraxQuery(Parameters builderParameters, QueryExpression expression, ref StreamingOptimization leftOnlyOptimization, bool exact = false)
    {
        var indexSearcher = builderParameters.IndexSearcher;
        var metadata = builderParameters.Metadata;
        var queryParameters = builderParameters.QueryParameters;
        var serverContext = builderParameters.ServerContext;
        var documentsContext = builderParameters.DocumentsContext;
        var index = builderParameters.Index;
        var fieldsToFetch = builderParameters.FieldsToFetch;
        var indexFieldsMapping = builderParameters.IndexFieldsMapping;
        var allocator = builderParameters.Allocator;

        if (RuntimeHelpers.TryEnsureSufficientExecutionStack() == false)
            QueryBuilderHelper.ThrowQueryTooComplexException(metadata, queryParameters);

        if (expression is null)
        {
            return builderParameters.AllEntries.Replay();
        }

        if (expression is BinaryExpression where)
        {
            builderParameters.BuildSteps?.Add($"Where: {expression.Type} - {expression} (operator: {where.Operator})");
            switch (where.Operator)
            {
                case OperatorType.And:
                    {
                        IQueryMatch left = null;
                        IQueryMatch right = null;

                        // translate ((Foo >= $p1) and (Foo <= $p2)) to a more efficient between query
                        if (@where.Left is BinaryExpression lbe && lbe.IsRangeOperation &&
                            @where.Right is BinaryExpression rbe && rbe.IsRangeOperation && lbe.Left.Equals(rbe.Left) &&
                            lbe.Right is ValueExpression leftVal && rbe.Right is ValueExpression rightVal)
                        {
                            BetweenExpression bq = null;
                            if (lbe.IsGreaterThan && rbe.IsLessThan)
                            {
                                bq = new BetweenExpression(lbe.Left, leftVal, rightVal)
                                {
                                    MinInclusive = lbe.Operator == OperatorType.GreaterThanEqual,
                                    MaxInclusive = rbe.Operator == OperatorType.LessThanEqual,
                                };
                            }

                            if (lbe.IsLessThan && rbe.IsGreaterThan)
                            {
                                bq = new BetweenExpression(lbe.Left, rightVal, leftVal)
                                {
                                    MinInclusive = rbe.Operator == OperatorType.GreaterThanEqual,
                                    MaxInclusive = lbe.Operator == OperatorType.LessThanEqual
                                };
                            }

                            if (bq != null)
                                return TranslateBetweenQuery(builderParameters, bq, exact);
                        }

                        leftOnlyOptimization.BinaryMatchTraversed();
                        switch (@where.Left, @where.Right)
                        {
                            case (NegatedExpression _, NegatedExpression _):
                                throw new NotSupportedException($"You cannot negate two negated expressions at the same time. The expression is '{where.ToString()}'.");
                            case (NegatedExpression leftNegated, _):
                                return HandleNegatedAnd(builderParameters, where.Right, leftNegated, exact);
                            case (_, NegatedExpression rightNegated):
                                return HandleNegatedAnd(builderParameters, where.Left, rightNegated, exact);

                            default:
                                left = ToCoraxQuery(builderParameters, @where.Left, ref leftOnlyOptimization, exact);
                                right = ToCoraxQuery(builderParameters, @where.Right, ref builderParameters.StreamingDisabled, exact);
                                
                                if (left is CoraxWhenQuery)
                                    return right;
                                if (right is CoraxWhenQuery)
                                    return left;
                                
                                // the comparison of fields reads every entry it looks at
                                if (right is JoinCompareMatch { IsDriven: false } rightCompare && left is not JoinCompareMatch)
                                    return rightCompare.DrivenBy(MaterializeWhenNeeded(builderParameters, left, ref builderParameters.StreamingDisabled));
                                if (left is JoinCompareMatch { IsDriven: false } leftCompare && right is not JoinCompareMatch)
                                    return leftCompare.DrivenBy(MaterializeWhenNeeded(builderParameters, right, ref builderParameters.StreamingDisabled));

                                // in case of AND we can materialize only TermMatches, we push streamingOptimization there only for changing order for MultiTermMatch;
                            if (left is CoraxBooleanItem cbi && leftOnlyOptimization.TrySetAsStreamingField(builderParameters, cbi, right))
                                    left = cbi.Materialize(ref leftOnlyOptimization);

                                if (TryAndMergeOrMaterialize(builderParameters, ref left, ref right, out var merged, ref builderParameters.StreamingDisabled))
                                    return merged;

                                // When we do not optimize this node for streaming operation:.
                                // We don't want an unknown size multiterm match to be subject to this optimization. When faced with one that is unknown just execute as
                                // it was written in the query. If we don't have statistics the confidence will be Low, so the optimization wont happen.
                                //This was part of Corax's IndexSearcher optimization (https://github.com/ravendb/ravendb/blob/12b874c06a0003520cd8f188467488cdc526f96b/src/Corax/IndexSearcher/IndexSearcher.BinaryMatches.cs#L16C8-L19)
                                if (leftOnlyOptimization.OptimizationIsPossible == false && left.Count < right.Count && left.Confidence >= QueryCountConfidence.Normal)
                                    return indexSearcher.And(right, left, token: builderParameters.Token);

                                return indexSearcher.And(left, right, token: builderParameters.Token);
                        }
                    }
                case OperatorType.Or:
                    {
                        leftOnlyOptimization.BinaryMatchTraversed();
                        var left = ToCoraxQuery(builderParameters, @where.Left, ref builderParameters.StreamingDisabled, exact);
                        var right = ToCoraxQuery(builderParameters, @where.Right, ref builderParameters.StreamingDisabled, exact);

                        if (left is CoraxWhenQuery)
                            return right;
                        if (right is CoraxWhenQuery)
                            return left;
                        
                        builderParameters.BuildSteps?.Add(
                            $"OR operator: left - {left.GetType().FullName} ({left}) assembly: {left.GetType().Assembly.FullName} assembly location: {left.GetType().Assembly.Location} , right - {right.GetType().FullName} ({right}) assemlbly: {right.GetType().Assembly.FullName} assembly location: {right.GetType().Assembly.Location}");

                        return CoraxOrQueries.Or(builderParameters, left, right);
                    }
                default:
                    {
                        var operation = QueryBuilderHelper.TranslateUnaryMatchOperation(where.Operator);

                        QueryExpression right = where.Right;

                        if (where.Right is MethodExpression rme)
                        {
                            right = QueryBuilderHelper.EvaluateMethod(metadata.Query, metadata, serverContext, documentsContext.DocumentDatabase.CompareExchangeStorage, rme, queryParameters, builderParameters.QueryTime);
                        }


                        var fieldName = QueryBuilderHelper.ExtractIndexFieldName(metadata.Query, queryParameters, where.Left, metadata);

                        exact = QueryBuilderHelper.IsExact(builderParameters.Index, exact, fieldName);

                        var (value, valueType) = QueryBuilderHelper.GetValue(metadata.Query, metadata, queryParameters, right, true);

                        var fieldMetadata = QueryBuilderHelper.GetFieldMetadata(allocator, fieldName, index, indexFieldsMapping, fieldsToFetch, builderParameters.HasDynamics,
                            builderParameters.DynamicFields, exact: exact, hasBoost: builderParameters.HasBoost);

                        CoraxHighlightingTermIndex highlightingTerm = null;
                        bool? isHighlighting = builderParameters.HighlightingTerms?.TryGetValue(fieldName, out highlightingTerm);
                        if (isHighlighting.HasValue && isHighlighting.Value == false)
                        {
                            highlightingTerm = new CoraxHighlightingTermIndex { FieldName = fieldName };
                            builderParameters.HighlightingTerms.TryAdd(fieldName, highlightingTerm);
                        }

                        var match = valueType switch
                        {
                            ValueTokenType.Double => CoraxBooleanItem.Build(indexSearcher, index, fieldMetadata, value, operation, ref builderParameters.StreamingDisabled),
                            ValueTokenType.Long => CoraxBooleanItem.Build(indexSearcher, index, fieldMetadata, value, operation, ref builderParameters.StreamingDisabled),
                            ValueTokenType.True or
                                ValueTokenType.False or
                                ValueTokenType.Null or
                                ValueTokenType.String or
                                ValueTokenType.Parameter => HandleStringUnaryMatch(builderParameters),
                            _ => throw new NotSupportedException($"Unhandled token type: {valueType}")
                        };

                        if (highlightingTerm != null && valueType is ValueTokenType.Double or ValueTokenType.Long)
                        {
                            highlightingTerm.Values = value.ToString();
                        }

                        return match;


                        IQueryMatch HandleStringUnaryMatch(Parameters queryEnvironment)
                        {
                            if (exact && queryEnvironment.Metadata.IsDynamic)
                            {
                                fieldName = new QueryFieldName(AutoIndexField.GetExactAutoIndexFieldName(fieldName), fieldName.IsQuoted);
                                fieldMetadata = QueryBuilderHelper.GetFieldMetadata(allocator, fieldName, index, indexFieldsMapping, fieldsToFetch, builderParameters.HasDynamics,
                                    builderParameters.DynamicFields, exact: exact, hasBoost: builderParameters.HasBoost);
                            }

                            if (value == null)
                            {
                                if (operation is UnaryMatchOperation.Equals)
                                    return CoraxBooleanItem.Build(indexSearcher, index, fieldMetadata, null, UnaryMatchOperation.Equals, ref builderParameters.StreamingDisabled);
                                else if (operation is UnaryMatchOperation.NotEquals)
                                    //Please consider if we ever need to support this.
                                    return CoraxBooleanItem.Build(indexSearcher, index, fieldMetadata, null, UnaryMatchOperation.NotEquals, ref builderParameters.StreamingDisabled);
                                else
                                    throw new NotSupportedException($"Unhandled operation: {operation}");
                            }

                            var valueAsString = QueryBuilderHelper.CoraxGetValueAsString(value);
                            if (highlightingTerm != null)
                                highlightingTerm.Values = valueAsString;

                            return CoraxBooleanItem.Build(indexSearcher, index, fieldMetadata, valueAsString, operation, ref builderParameters.StreamingDisabled);
                        }
                    }
            }
        }

        if (expression is NegatedExpression ne)
        {
            builderParameters.BuildSteps?.Add($"Negated: {expression.Type} - {ne}");

            // 'not foo and bar' should be parsed as:
            // (not foo) and bar, instead of not (foo and bar)
            if (ne.Expression is BinaryExpression nbe &&
                nbe.Parenthesis == false &&
                (nbe.Operator == OperatorType.And || nbe.Operator == OperatorType.Or)
               )
            {
                var newExpr = new BinaryExpression(new NegatedExpression(nbe.Left),
                    nbe.Right, nbe.Operator);
                return ToCoraxQuery(builderParameters, newExpr, ref builderParameters.StreamingDisabled, exact);
            }

            //e.g. or (not exists(Field))
            builderParameters.NegationDepth++;
            var inner = ToCoraxQuery(builderParameters, ne.Expression, ref builderParameters.StreamingDisabled, exact);
            builderParameters.NegationDepth--;
            inner = MaterializeWhenNeeded(builderParameters, inner, ref builderParameters.StreamingDisabled);
            return builderParameters.IndexSearcher.AndNot(builderParameters.IndexSearcher.AllEntries(), inner);
        }

        if (expression is BetweenExpression be)
        {
            builderParameters.BuildSteps?.Add($"Between: {expression.Type} - {be}");

            return TranslateBetweenQuery(builderParameters, be, exact);
        }

        if (expression is InExpression ie)
        {
            return HandleIn(builderParameters, ie, exact);
        }

        if (expression is TrueExpression)
        {
            builderParameters.BuildSteps?.Add($"True: {expression.Type} - {expression}");

            return builderParameters.AllEntries.Replay();
        }

        if (expression is MethodExpression me)
        {
            var methodName = me.Name.Value;
            var methodType = QueryMethod.GetMethodType(methodName);

            builderParameters.BuildSteps?.Add($"Method: {expression.Type} - {me} - method: {methodType}, {methodName}");

            switch (methodType)
            {
                case MethodType.Search:
                    return HandleSearch(builderParameters, me);
                case MethodType.Matches:
                    return MatchesJoin(builderParameters, me);
                case MethodType.Proximity:
                    throw new NotSupportedInCoraxException($"{nameof(Corax)} doesn't support proximity over search() method");
                case MethodType.Fuzzy:
                    throw new NotSupportedInCoraxException($"{nameof(Corax)} doesn't support fuzzy() method");
                case MethodType.Lucene:
                    throw new NotSupportedInCoraxException($"{nameof(Corax)} doesn't support lucene() method");
                case MethodType.Boost:
                    return HandleBoost(builderParameters, me, exact);
                case MethodType.StartsWith:
                    return HandleStartsWith(builderParameters, me, exact, ref leftOnlyOptimization);
                case MethodType.EndsWith:
                    return HandleEndsWith(builderParameters, me, exact, ref leftOnlyOptimization);
                case MethodType.Exists:
                    return HandleExists(builderParameters, me, ref leftOnlyOptimization);
                case MethodType.When:
                    return HandleWhen(builderParameters, me, ref leftOnlyOptimization, exact);
                case MethodType.Exact:
                    return HandleExact(builderParameters, me, ref leftOnlyOptimization);
                case MethodType.Spatial_Within:
                case MethodType.Spatial_Contains:
                case MethodType.Spatial_Disjoint:
                case MethodType.Spatial_Intersects:
                    return HandleSpatial(builderParameters, me, methodType);
                case MethodType.Vector_Search:
                    return HandleVector(builderParameters, me, exact);
                case MethodType.Regex:
                    return HandleRegex(builderParameters, me, ref leftOnlyOptimization);
                case MethodType.MoreLikeThis:
                    return builderParameters.MoreLikeThisMatch != null ? builderParameters.MoreLikeThisMatch() : builderParameters.IndexSearcher.AllEntries();
                default:
                    QueryMethod.ThrowMethodNotSupported(methodType, metadata.QueryText, queryParameters);
                    return null; // never hit
            }
        }

        throw new InvalidQueryException("Unable to understand query", metadata.QueryText, queryParameters);
    }

    private static IQueryMatch HandleNegatedAnd(CoraxQueryBuilder.Parameters builderParameters, QueryExpression leftExpr, NegatedExpression rightExpr, bool exact)
    {
        var indexSearcher = builderParameters.IndexSearcher;
        // The negation is always applied by subtracting the positive match from the left-hand set.
        // Enumerating the complement instead would drop the documents that have no term for the
        // field at all, which is what `A and not (startsWith(field, x))` used to do.
        IQueryMatch left = ToCoraxQuery(builderParameters, leftExpr, ref builderParameters.StreamingDisabled, exact);
        if (left is CoraxWhenQuery)
            left = builderParameters.AllEntries.Replay();

        builderParameters.NegationDepth++;
        IQueryMatch right = ToCoraxQuery(builderParameters, rightExpr.Expression, ref builderParameters.StreamingDisabled, exact);
        builderParameters.NegationDepth--;
        Materialize(builderParameters, ref left, ref right, ref builderParameters.StreamingDisabled);

        return right is CoraxWhenQuery
            ? left
            : indexSearcher.AndNot(left, right, token: builderParameters.Token);
    }
    
    private static IQueryMatch HandleIn(Parameters builderParameters, InExpression ie, bool exact)
    {
        builderParameters.BuildSteps?.Add($"In: {ie.Type} - {ie}");

        QueryMetadata metadata = builderParameters.Metadata;
        BlittableJsonReaderObject queryParameters = builderParameters.QueryParameters;
        var allocator = builderParameters.Allocator;

        var fieldName = QueryBuilderHelper.ExtractIndexFieldName(metadata.Query, queryParameters, ie.Source, metadata);

        CoraxHighlightingTermIndex highlightingTerm = null;
        if (builderParameters.HighlightingTerms != null)
        {
            highlightingTerm = new CoraxHighlightingTermIndex { FieldName = fieldName };
            builderParameters.HighlightingTerms[fieldName] = highlightingTerm;
        }

        exact = QueryBuilderHelper.IsExact(builderParameters.Index, exact, fieldName);
        if (exact && builderParameters.Metadata.IsDynamic)
        {
            fieldName = new QueryFieldName(AutoIndexField.GetExactAutoIndexFieldName(fieldName), fieldName.IsQuoted);
            if (builderParameters.HighlightingTerms != null)
            {
                highlightingTerm.DynamicFieldName = fieldName;
                builderParameters.HighlightingTerms[fieldName] = highlightingTerm;
            }
        }

        var fieldMetadata = QueryBuilderHelper.GetFieldMetadata(allocator, fieldName, builderParameters.Index, builderParameters.IndexFieldsMapping, builderParameters.FieldsToFetch, builderParameters.HasDynamics,
            builderParameters.DynamicFields, exact: exact, hasBoost: builderParameters.HasBoost);
        
        var hasTime = builderParameters.Index.IndexFieldsPersistence.HasTimeValues(fieldName);

        if (ie.All)
        {
            var uniqueMatches = new HashSet<(string Term, bool Exact)>();
            foreach (var tuple in QueryBuilderHelper.GetValuesForIn(metadata.Query, ie, metadata, queryParameters))
            {
                if (exact && builderParameters.Metadata.IsDynamic)
                    fieldName = new QueryFieldName(AutoIndexField.GetExactAutoIndexFieldName(fieldName.Value), fieldName.IsQuoted);

                // `all in` is a conjunction, so only the primary spelling can be used here - see TryGetTimeTermsForInQuery.
                string term = null;
                bool isTime = hasTime && QueryBuilderHelper.TryGetTimeTermsForInQuery(builderParameters.Index, tuple.Value, out term, out _);
                uniqueMatches.Add((term ?? QueryBuilderHelper.CoraxGetValueAsString(tuple.Value), isTime));
            }

            return builderParameters.IndexSearcher.AllInQuery(fieldMetadata, uniqueMatches);
        }

        var matches = new List<(string Term, bool Exact)>();
        foreach (var tuple in QueryBuilderHelper.GetValuesForIn(metadata.Query, ie, metadata, queryParameters))
        {
            string term = null, alternateTerm = null;
            bool isTime = hasTime && QueryBuilderHelper.TryGetTimeTermsForInQuery(builderParameters.Index, tuple.Value, out term, out alternateTerm);

            var termValue = term ?? QueryBuilderHelper.CoraxGetValueAsString(tuple.Value);
            matches.Add((termValue, isTime));

            if (alternateTerm != null && alternateTerm.Equals(termValue, StringComparison.Ordinal) == false)
                matches.Add((alternateTerm, isTime));
        }

        if (highlightingTerm != null)
            highlightingTerm.Values = matches;

        return builderParameters.IndexSearcher.InQuery(fieldMetadata, matches);
    }

    public static MoreLikeThisQuery.MoreLikeThisQuery BuildMoreLikeThisQuery(Parameters builderParameters, QueryExpression whereExpression, bool buildFilter = true)
    {
        using (CultureHelper.EnsureInvariantCulture())
        {
            IQueryMatch filterQuery = null;
            if (buildFilter)
            {
                filterQuery = BuildQuery(builderParameters, out _);
                filterQuery = MaterializeWhenNeeded(builderParameters, filterQuery, ref builderParameters.StreamingDisabled);
            }

            var moreLikeThisQuery = ToMoreLikeThisQuery(builderParameters, whereExpression, out var baseDocument, out var options);
            moreLikeThisQuery = MaterializeWhenNeeded(builderParameters, moreLikeThisQuery, ref builderParameters.StreamingDisabled);

            return new MoreLikeThisQuery.MoreLikeThisQuery
            {
                BaseDocument = baseDocument,
                BaseDocumentQuery = moreLikeThisQuery,
                FilterQuery = filterQuery,
                Options = options
            };
        }
    }

    private static IQueryMatch ToMoreLikeThisQuery(Parameters builderParameters, QueryExpression whereExpression, out string baseDocument, out BlittableJsonReaderObject options)
    {
        var indexSearcher = builderParameters.IndexSearcher;
        var metadata = builderParameters.Metadata;
        var queryParameters = builderParameters.QueryParameters;
        var serverContext = builderParameters.ServerContext;
        var context = builderParameters.DocumentsContext;
        baseDocument = null;
        options = null;

        var moreLikeThisExpression = QueryBuilderHelper.FindMoreLikeThisExpression(whereExpression);
        if (moreLikeThisExpression == null)
            throw new InvalidOperationException("Query does not contain MoreLikeThis method expression");

        if (moreLikeThisExpression.Arguments.Count == 2)
        {
            var value = QueryBuilderHelper.GetValue(metadata.Query, metadata, queryParameters, moreLikeThisExpression.Arguments[1], allowObjectsInParameters: true);
            if (value.Type == ValueTokenType.String)
                options = IndexOperationBase.ParseJsonStringIntoBlittable(QueryBuilderHelper.GetValueAsString(value.Value), context);
            else
                options = value.Value as BlittableJsonReaderObject;
        }

        var firstArgument = moreLikeThisExpression.Arguments[0];
        if (firstArgument is BinaryExpression binaryExpression)
            return ToCoraxQuery(builderParameters, binaryExpression, ref builderParameters.StreamingDisabled);

        var firstArgumentValue = QueryBuilderHelper.GetValueAsString(QueryBuilderHelper.GetValue(metadata.Query, metadata, queryParameters, firstArgument).Value);
        if (bool.TryParse(firstArgumentValue, out var firstArgumentBool))
        {
            if (firstArgumentBool)
                return indexSearcher.AllEntries();

            return indexSearcher.EmptyMatch(); // empty boolean query yields 0 documents
        }

        baseDocument = firstArgumentValue;
        return null;
    }

    private static void Materialize(Parameters builderParameters, ref IQueryMatch lhs, ref IQueryMatch rhs, ref StreamingOptimization streamingOptimization)
    {
        lhs = MaterializeWhenNeeded(builderParameters, lhs, ref streamingOptimization);
        rhs = MaterializeWhenNeeded(builderParameters, rhs, ref streamingOptimization);
    }
    
    private static bool TryAndMergeOrMaterialize(Parameters parameters, ref IQueryMatch lhs, ref IQueryMatch rhs, out CoraxBooleanQueryBase merged, ref StreamingOptimization streamingOptimization, bool requiredMaterialization = false)
    {
        merged = null;
        if (requiredMaterialization)
        {
            lhs = MaterializeWhenNeeded(parameters, lhs, ref streamingOptimization);
            rhs = MaterializeWhenNeeded(parameters, rhs, ref streamingOptimization);
            return false;
        }
        
        merged = CoraxAndQueries.And(parameters, lhs, rhs);
        return true;
    }

    private static IQueryMatch HandleExact(Parameters builderParameters, MethodExpression expression, ref StreamingOptimization streamingConfiguration)
    {
        return ToCoraxQuery(builderParameters, expression.Arguments[0], ref streamingConfiguration, exact: true);
    }

    private static IQueryMatch TranslateBetweenQuery(Parameters builderParameters, BetweenExpression be, bool exact)
    {
        var metadata = builderParameters.Metadata;
        var queryParameters = builderParameters.QueryParameters;
        var index = builderParameters.Index;
        var fieldsToFetch = builderParameters.FieldsToFetch;
        var indexFieldsMapping = builderParameters.IndexFieldsMapping;
        var allocator = builderParameters.Allocator;

        var fieldName = QueryBuilderHelper.ExtractIndexFieldName(metadata.Query, queryParameters, be.Source, metadata);
        var (valueFirst, valueFirstType) = QueryBuilderHelper.GetValue(metadata.Query, metadata, queryParameters, be.Min);
        var (valueSecond, valueSecondType) = QueryBuilderHelper.GetValue(metadata.Query, metadata, queryParameters, be.Max);
        var fieldMetadata = QueryBuilderHelper.GetFieldMetadata(allocator, fieldName, index, indexFieldsMapping, fieldsToFetch, builderParameters.HasDynamics, builderParameters.DynamicFields, exact: exact, hasBoost: builderParameters.HasBoost);
        var leftSideOperation = be.MinInclusive ? UnaryMatchOperation.GreaterThanOrEqual : UnaryMatchOperation.GreaterThan;
        var rightSideOperation = be.MaxInclusive ? UnaryMatchOperation.LessThanOrEqual : UnaryMatchOperation.LessThan;


        return (valueFirstType, valueSecondType) switch
        {
            (ValueTokenType.String, ValueTokenType.String) => HandleStringBetween(),
            _ => CoraxBooleanItem.BuildBetween(builderParameters.IndexSearcher, index, fieldMetadata, valueFirst, valueSecond, leftSideOperation, rightSideOperation, ref builderParameters.StreamingDisabled)
        };

        IQueryMatch HandleStringBetween()
        {
            exact = QueryBuilderHelper.IsExact(index, exact, fieldName);
            var valueFirstAsString = QueryBuilderHelper.CoraxGetValueAsString(valueFirst);
            var valueSecondAsString = QueryBuilderHelper.CoraxGetValueAsString(valueSecond);

            if (builderParameters.HighlightingTerms != null)
            {
                var highlightingTerm = new CoraxHighlightingTermIndex { FieldName = fieldName, Values = new Tuple<string, string>(valueFirstAsString, valueSecondAsString) };
                builderParameters.HighlightingTerms[fieldName] = highlightingTerm;
            }

            return CoraxBooleanItem.BuildBetween(builderParameters.IndexSearcher, index, fieldMetadata, valueFirstAsString, valueSecondAsString, leftSideOperation, rightSideOperation, ref builderParameters.StreamingDisabled);
        }
    }
    
    private static IQueryMatch HandleWhen(Parameters builderParameters, MethodExpression expression, ref StreamingOptimization streamingOptimization, bool exact)
    {
        PortableExceptions.ThrowIf<ArgumentException>(expression.Arguments.Count != 2, $"Method `when` requires exactly 2 arguments, but got {expression.Arguments.Count}");

        var constantExpressionResult = QueryBuilderHelper.EvaluateConstantExpressionForWhenQuery(expression.Arguments[0], builderParameters.Metadata.Query, builderParameters.Metadata, builderParameters.QueryParameters);
        if (constantExpressionResult == false)
            return new CoraxWhenQuery();
            
        return ToCoraxQuery(builderParameters, expression.Arguments[1], ref streamingOptimization, exact);
    }

    private static IQueryMatch HandleExists(Parameters builderParameters, MethodExpression expression, ref StreamingOptimization streamingOptimization)
    {
        var metadata = builderParameters.Metadata;
        var queryParameters = builderParameters.QueryParameters;

        var fieldName = QueryBuilderHelper.ExtractIndexFieldName(metadata.Query, queryParameters, expression.Arguments[0], metadata);
        var fieldMetadata = QueryBuilderHelper.GetFieldMetadata(builderParameters.Allocator, fieldName, builderParameters.Index, builderParameters.IndexFieldsMapping,
            builderParameters.FieldsToFetch, builderParameters.HasDynamics, builderParameters.DynamicFields, hasBoost: builderParameters.HasBoost);

        if (streamingOptimization.TrySetMultiTermMatchAsStreamingField(fieldMetadata, MethodType.Exists) == false)
            return builderParameters.IndexSearcher.ExistsQuery(fieldMetadata);

        var sortBy = streamingOptimization.SortField;
        var nullIsSmallest = (sortBy.NullsSortMode ?? builderParameters.Index.Configuration.NullsSortMode) == NullsSortMode.NullsSmallest;
        var terms = builderParameters.IndexSearcher.BetweenQuery(sortBy.Field, Constants.BeforeAllKeys, Constants.AfterAllKeys, forward: sortBy.Ascending, streamingEnabled: true);
        return builderParameters.IndexSearcher.IncludeNullMatch(in sortBy.Field, terms, sortBy.Ascending, nullIsSmallest);
    }

    private static IQueryMatch HandleStartsWith(Parameters builderParameters, MethodExpression expression, bool exact, ref StreamingOptimization streamingOptimization)
    {
        var metadata = builderParameters.Metadata;
        var queryParameters = builderParameters.QueryParameters;
        var index = builderParameters.Index;
        var fieldsToFetch = builderParameters.FieldsToFetch;
        var indexFieldsMapping = builderParameters.IndexFieldsMapping;
        var allocator = builderParameters.Allocator;

        var fieldName = QueryBuilderHelper.ExtractIndexFieldName(metadata.Query, queryParameters, expression.Arguments[0], metadata);
        var (value, valueType) = QueryBuilderHelper.GetValue(metadata.Query, metadata, queryParameters, (ValueExpression)expression.Arguments[1]);
        if (valueType != ValueTokenType.String)
            QueryBuilderHelper.ThrowMethodExpectsArgumentOfTheFollowingType("startsWith", ValueTokenType.String, valueType, metadata.QueryText, queryParameters);

        var valueAsString = QueryBuilderHelper.CoraxGetValueAsString(value);
        CoraxHighlightingTermIndex highlightingTerm = null;
        if (builderParameters.HighlightingTerms != null)
        {
            highlightingTerm = new CoraxHighlightingTermIndex { FieldName = fieldName, Values = valueAsString };
            builderParameters.HighlightingTerms[fieldName] = highlightingTerm;
        }

        exact = QueryBuilderHelper.IsExact(index, exact, fieldName);
        if (exact && builderParameters.Metadata.IsDynamic)
        {
            fieldName = new QueryFieldName(AutoIndexField.GetExactAutoIndexFieldName(fieldName.Value), fieldName.IsQuoted);
            if (builderParameters.HighlightingTerms != null)
            {
                highlightingTerm.DynamicFieldName = fieldName;
                builderParameters.HighlightingTerms[fieldName] = highlightingTerm;
            }
        }

        var fieldMetadata = QueryBuilderHelper.GetFieldMetadata(allocator, fieldName, index, indexFieldsMapping, fieldsToFetch, builderParameters.HasDynamics,
            builderParameters.DynamicFields, exact: exact, hasBoost: builderParameters.HasBoost);

        return streamingOptimization.TrySetMultiTermMatchAsStreamingField(fieldMetadata, MethodType.StartsWith)
            ? builderParameters.IndexSearcher.StartWithQuery(fieldMetadata, valueAsString, forward: streamingOptimization.Forward, streamingEnabled: true)
            : builderParameters.IndexSearcher.StartWithQuery(fieldMetadata, valueAsString);
    }

    private static MultiTermMatch HandleEndsWith(Parameters builderParameters, MethodExpression expression, bool exact, ref StreamingOptimization streamingOptimization)
    {
        var metadata = builderParameters.Metadata;
        var queryParameters = builderParameters.QueryParameters;
        var highlightingTerms = builderParameters.HighlightingTerms;
        var index = builderParameters.Index;
        var fieldsToFetch = builderParameters.FieldsToFetch;
        var indexFieldsMapping = builderParameters.IndexFieldsMapping;
        var allocator = builderParameters.Allocator;

        var fieldName = QueryBuilderHelper.ExtractIndexFieldName(metadata.Query, queryParameters, expression.Arguments[0], metadata);
        var (value, valueType) = QueryBuilderHelper.GetValue(metadata.Query, metadata, queryParameters, (ValueExpression)expression.Arguments[1]);
        if (valueType != ValueTokenType.String)
            QueryBuilderHelper.ThrowMethodExpectsArgumentOfTheFollowingType("endsWith", ValueTokenType.String, valueType, metadata.QueryText, queryParameters);

        var valueAsString = QueryBuilderHelper.CoraxGetValueAsString(value);

        CoraxHighlightingTermIndex highlightingTerm = null;
        if (highlightingTerms != null)
        {
            highlightingTerm = new CoraxHighlightingTermIndex { FieldName = fieldName, Values = valueAsString };
            highlightingTerms[fieldName] = highlightingTerm;
        }

        exact = QueryBuilderHelper.IsExact(builderParameters.Index, exact, fieldName);
        if (exact && metadata.IsDynamic)
        {
            fieldName = new QueryFieldName(AutoIndexField.GetExactAutoIndexFieldName(fieldName.Value), fieldName.IsQuoted);
            if (highlightingTerms != null)
            {
                highlightingTerm.DynamicFieldName = fieldName;
                highlightingTerms[fieldName] = highlightingTerm;
            }
        }

        var fieldMetadata = QueryBuilderHelper.GetFieldMetadata(allocator, fieldName, index, indexFieldsMapping, fieldsToFetch, builderParameters.HasDynamics,
            builderParameters.DynamicFields, exact: exact, hasBoost: builderParameters.HasBoost);

        return streamingOptimization.TrySetMultiTermMatchAsStreamingField(fieldMetadata, MethodType.EndsWith)
            ? builderParameters.IndexSearcher.EndsWithQuery(fieldMetadata, valueAsString, forward: streamingOptimization.Forward, streamingEnabled: true)
            : builderParameters.IndexSearcher.EndsWithQuery(fieldMetadata, valueAsString);
    }

    private static IQueryMatch HandleBoost(Parameters builderParameters, MethodExpression expression, bool exact)
    {
        var metadata = builderParameters.Metadata;
        var queryParameters = builderParameters.QueryParameters;
        var indexSearcher = builderParameters.IndexSearcher;

        if (expression.Arguments.Count != 2)
        {
            throw new InvalidQueryException($"Boost(expression, boostVal) requires two arguments, but was called with {expression.Arguments.Count}",
                metadata.QueryText, queryParameters);
        }

        float boost;
        var (val, type) = QueryBuilderHelper.GetValue(metadata.Query, metadata, queryParameters, expression.Arguments[1]);
        switch (val)
        {
            case float f:
                boost = f;
                break;
            case double d:
                boost = (float)d;
                break;
            case int i:
                boost = i;
                break;
            case long l:
                boost = l;
                break;
            case string s:
                if (float.TryParse(s, out boost) == false)
                {
                    throw new InvalidQueryException($"The boost value must be a valid float, but was called with {s}",
                        metadata.QueryText, queryParameters);
                }
                break;
            default:
                throw new InvalidQueryException($"Unable to find boost value: {val} ({type})",
                    metadata.QueryText, queryParameters);
        }

        if (float.IsFinite(boost) == false || boost < 0)
            throw new InvalidQueryException($"The boost value must be a finite, non-negative number, but was {val}", metadata.QueryText, queryParameters);


        var query = ToCoraxQuery(builderParameters, expression.Arguments[0], ref builderParameters.StreamingDisabled, exact);
        if (builderParameters.HasBoost == false)
            return query;

        switch (query)
        {
            case CoraxBooleanQueryBase cbqb:
                cbqb.Boosting = boost;
                return cbqb;
            case CoraxBooleanItem cbi:
                cbi.Boosting = boost;
                return cbi;
            case CoraxVectorItem cvi:
                cvi.Boosting = boost;
                return cvi;
            default:
                return indexSearcher.Boost(query, boost);
        }
    }

    private static IQueryMatch HandleSearch(Parameters builderParameters, MethodExpression expression)
    {
        var metadata = builderParameters.Metadata;
        var highlightingTerms = builderParameters.HighlightingTerms;
        var queryParameters = builderParameters.QueryParameters;
        var indexSearcher = builderParameters.IndexSearcher;
        var index = builderParameters.Index;
        var fieldsToFetch = builderParameters.FieldsToFetch;
        var indexFieldsMapping = builderParameters.IndexFieldsMapping;
        var allocator = builderParameters.Allocator;
        IndexSearcher.SearchQueryOptions searchQueryOptions = IndexSearcher.SearchQueryOptions.Legacy;
        if (IndexDefinitionBaseServerSide.IndexVersion.IsCoraxSearchWildcardAdjustmentSupported(builderParameters.Index.Definition.Version))
            searchQueryOptions = IndexSearcher.SearchQueryOptions.PhraseQueryWithWildcardAdjustments;
        else if (builderParameters.Index.Definition.Version >= IndexDefinitionBaseServerSide.IndexVersion.PhraseQuerySupportInCoraxIndexes)
            searchQueryOptions = IndexSearcher.SearchQueryOptions.PhraseQuery;


        QueryFieldName fieldName;
        var isDocumentId = false;
        switch (expression.Arguments[0])
        {
            case FieldExpression ft:
                fieldName = QueryBuilderHelper.ExtractIndexFieldName(metadata.Query, queryParameters, ft, metadata);
                break;
            case ValueExpression vt:
                fieldName = QueryBuilderHelper.ExtractIndexFieldName(metadata.Query, queryParameters, vt, metadata);
                break;
            case MethodExpression me when QueryMethod.GetMethodType(me.Name.Value) == MethodType.Id:
                fieldName = QueryFieldName.DocumentId;
                isDocumentId = true;
                break;
            default:
                throw new InvalidOperationException("search() method can only be called with an identifier or string, but was called with " + expression.Arguments[0]);
        }


        var (value, valueType) = QueryBuilderHelper.GetValue(metadata.Query, metadata, queryParameters, (ValueExpression)expression.Arguments[1]);
        if (valueType != ValueTokenType.String)
            QueryBuilderHelper.ThrowMethodExpectsArgumentOfTheFollowingType("search", ValueTokenType.String, valueType, builderParameters.Metadata.QueryText,
                builderParameters.QueryParameters);

        Debug.Assert(metadata.IsDynamic == false || metadata.WhereFields[fieldName].IsFullTextSearch);

        var valueAsString = QueryBuilderHelper.CoraxGetValueAsString(value);
        if (highlightingTerms != null && highlightingTerms.TryGetValue(fieldName, out var highlightingTerm) == false)
        {
            highlightingTerm = new CoraxHighlightingTermIndex { Values = valueAsString, };

            highlightingTerm.FieldName = fieldName;
            highlightingTerms?.TryAdd(fieldName, highlightingTerm);


            if (metadata.IsDynamic && isDocumentId == false)
            {
                fieldName = new QueryFieldName(AutoIndexField.GetSearchAutoIndexFieldName(fieldName.Value), fieldName.IsQuoted);

                // We now add the dynamic field too. 
                highlightingTerm.DynamicFieldName = fieldName;
            }
        }
        else if (metadata.IsDynamic && isDocumentId == false)
        {
            fieldName = new QueryFieldName(AutoIndexField.GetSearchAutoIndexFieldName(fieldName.Value), fieldName.IsQuoted);
        }

        var fieldMetadata = QueryBuilderHelper.GetFieldMetadata(allocator, fieldName, index, indexFieldsMapping, fieldsToFetch, builderParameters.HasDynamics,
            builderParameters.DynamicFields, handleSearch: true, hasBoost: builderParameters.HasBoost
            , forceDefaultSearchAnalyzer: builderParameters.Index.Configuration.UseSearchAnalyzerForDynamicFieldsIfNotSetExplicitlyInSearchQuery);

        // Wildcard queries:
        if (searchQueryOptions is IndexSearcher.SearchQueryOptions.PhraseQueryWithWildcardAdjustments && valueAsString.Length >= 1 && (valueAsString[0] == '*' || (valueAsString.Length >= 2 && valueAsString[^1] == '*')))
            fieldMetadata = ReplaceAnalyzerForWildcardQueries(fieldMetadata);

        CoraxConstants.Search.Operator @operator = CoraxConstants.Search.Operator.Or;
        if (expression.Arguments.Count == 3)
        {
            var fieldExpression = (FieldExpression)expression.Arguments[2];
            if (fieldExpression.Compound.Count != 1)
                QueryBuilderHelper.ThrowInvalidOperatorInSearch(metadata, queryParameters, fieldExpression);

            var op = fieldExpression.Compound[0];
            if (string.Equals("AND", op.Value, StringComparison.OrdinalIgnoreCase))
                @operator = Constants.Search.Operator.And;
            else if (string.Equals("OR", op.Value, StringComparison.OrdinalIgnoreCase))
                @operator = Constants.Search.Operator.Or;
            else
                QueryBuilderHelper.ThrowInvalidOperatorInSearch(metadata, queryParameters, fieldExpression);
        }

        return indexSearcher.SearchQuery(fieldMetadata, GetValues(), @operator, searchQueryOptions, builderParameters.Token);

        /*
         * Here we need to deal with value that comes from the user, which means that we
         * have to be careful.
         *
         * The rules are that we'll split the terms on whitespace, except if they are quoted
         * using ", however, you may escape the " using \, and \ using \\.
         */
        IEnumerable<string> GetValues()
        {
            List<int> escapePositions = null;

            var quoted = false;
            var lastWordStart = 0;
            for (var i = 0; i < valueAsString.Length; i++)
            {
                switch (valueAsString[i])
                {
                    case '\\' when IsEscaped(valueAsString, i):
                        AddEscapePosition(i);
                        break;
                    case '"':
                        if (IsEscaped(valueAsString, i))
                        {
                            AddEscapePosition(i);
                            continue;
                        }

                        if (lastWordStart != i)
                        {
                            yield return YieldValue(valueAsString, lastWordStart, i - lastWordStart, escapePositions);
                        }

                        quoted = !quoted;
                        lastWordStart = i + 1;
                        break;
                    case '\t':
                    case ' ':
                        if (quoted)
                            continue;

                        if (lastWordStart != i)
                        {
                            yield return YieldValue(valueAsString, lastWordStart, i - lastWordStart, escapePositions);
                        }

                        lastWordStart = i + 1; // skipping
                        break;
                }
            }

            if (valueAsString.Length - lastWordStart > 0)
                yield return YieldValue(valueAsString, lastWordStart, valueAsString.Length - lastWordStart, escapePositions);


            void AddEscapePosition(int i)
            {
                escapePositions ??= new List<int>(16);
                escapePositions.Add(i - 1);
            }
        }

        string YieldValue(string input, int startIndex, int length, List<int> escapePositions)
        {
            if (escapePositions == null || escapePositions.Count == 0)
                return input.Substring(startIndex, length);

            var sb = new StringBuilder(input, startIndex, length, length);

            for (int i = escapePositions.Count - 1; i >= 0; i--)
            {
                sb.Remove(escapePositions[i] - startIndex, 1);
            }

            escapePositions.Clear();

            return sb.ToString();
        }

        bool IsEscaped(string input, int index)
        {
            var count = 0;
            for (int i = index - 1; i >= 0; i--)
            {
                if (input[i] == '\\')
                {
                    count++;
                }
                else
                {
                    break;
                }
            }

            return (count & 1) == 1;
        }

        FieldMetadata ReplaceAnalyzerForWildcardQueries(in FieldMetadata original)
        {
            FieldMetadata result = original;
            // We need to retrieve the analyzer for the dynamic field since the field metadata is created dynamically.
            if (original.IsDynamic)
                result = fieldMetadata.ChangeAnalyzer(original.Mode, builderParameters.IndexFieldsMapping.SearchAnalyzer(original.FieldName.ToString()));
            
            if (result.Analyzer is LuceneAnalyzerAdapter laa)
            {
                //logic from LuceneQueryBuilder
                var luceneAnalyzer = laa.Analyzer switch
                {
                    KeywordAnalyzer keywordAnalyzer => builderParameters.IndexFieldsMapping.ExactAnalyzer(original.FieldName.ToString()),
                    // here we force a lower case keyword analyzer to ensure proper behavior
                    // https://ayende.com/blog/191841-B/understanding-query-processing-and-wildcards-in-ravendb
                    RavenStandardAnalyzer or NGramAnalyzer => builderParameters.IndexFieldsMapping.DefaultAnalyzer,
                    StandardAnalyzer when laa.Analyzer.GetType() == typeof(StandardAnalyzer) => builderParameters.IndexFieldsMapping.DefaultAnalyzer,
                    LowerCaseKeywordAnalyzer or CollationAnalyzer => builderParameters.IndexFieldsMapping.DefaultAnalyzer,
                    // if the user has a custom analyzer, we'll use that, and they can deal with any surprises
                    // in wildcard queries
                    _ => null
                };

                if (luceneAnalyzer != null)
                    result = result.ChangeAnalyzer(FieldIndexingMode.Search, luceneAnalyzer);
            }

            // Currently, we do not have any custom Corax analyzers, so we don't need to address them.
            return result;
        }
    }

    private static IQueryMatch HandleSpatial(Parameters builderParameters, MethodExpression expression, MethodType spatialMethod)
    {
        var metadata = builderParameters.Metadata;
        var queryParameters = builderParameters.QueryParameters;
        var index = builderParameters.Index;
        var indexFieldsMapping = builderParameters.IndexFieldsMapping;
        var fieldsToFetch = builderParameters.FieldsToFetch;
        var allocator = builderParameters.Allocator;

        string fieldName;
        if (metadata.IsDynamic == false)
            fieldName = QueryBuilderHelper.ExtractIndexFieldName(metadata.Query, queryParameters, expression.Arguments[0], metadata);
        else
        {
            var spatialExpression = (MethodExpression)expression.Arguments[0];
            fieldName = metadata.GetSpatialFieldName(spatialExpression, builderParameters.QueryParameters);
        }

        var fieldMetadata = QueryBuilderHelper.GetFieldMetadata(allocator, fieldName, index, indexFieldsMapping, fieldsToFetch, builderParameters.HasDynamics,
            builderParameters.DynamicFields, hasBoost: builderParameters.HasBoost);
        var shapeExpression = (MethodExpression)expression.Arguments[1];

        var distanceErrorPct = RavenConstants.Documents.Indexing.Spatial.DefaultDistanceErrorPct;
        if (expression.Arguments.Count == 3)
        {
            var distanceErrorPctValue = QueryBuilderHelper.GetValue(metadata.Query, metadata, queryParameters, (ValueExpression)expression.Arguments[2]);
            QueryBuilderHelper.AssertValueIsNumber(fieldName, distanceErrorPctValue.Type);

            distanceErrorPct = Convert.ToDouble(distanceErrorPctValue.Value);
        }

        var spatialField = builderParameters.Factories.GetSpatialFieldFactory(fieldName);

        var methodName = shapeExpression.Name;
        var methodType = QueryMethod.GetMethodType(methodName.Value);

        IShape shape = null;
        switch (methodType)
        {
            case MethodType.Spatial_Circle:
                shape = QueryBuilderHelper.HandleCircle(metadata.Query, shapeExpression, metadata, queryParameters, fieldName, spatialField, out _);
                break;
            case MethodType.Spatial_Wkt:
                shape = QueryBuilderHelper.HandleWkt(builderParameters, fieldName, shapeExpression, spatialField, out _);
                break;
            default:
                QueryMethod.ThrowMethodNotSupported(methodType, metadata.QueryText, builderParameters.QueryParameters);
                break;
        }

        Debug.Assert(shape != null);

        var operation = spatialMethod switch
        {
            MethodType.Spatial_Within => global::Corax.Utils.Spatial.SpatialRelation.Within,
            MethodType.Spatial_Disjoint => global::Corax.Utils.Spatial.SpatialRelation.Disjoint,
            MethodType.Spatial_Intersects => global::Corax.Utils.Spatial.SpatialRelation.Intersects,
            MethodType.Spatial_Contains => global::Corax.Utils.Spatial.SpatialRelation.Contains,
            _ => (global::Corax.Utils.Spatial.SpatialRelation)QueryMethod.ThrowMethodNotSupported(spatialMethod, metadata.QueryText, builderParameters.QueryParameters)
        };


        //var args = new SpatialArgs(operation, shape) {DistErrPct = distanceErrorPct};

        return builderParameters.IndexSearcher.SpatialQuery(fieldMetadata, distanceErrorPct, shape, spatialField.GetContext(), operation, token: builderParameters.Token);
    }

    private static IQueryMatch HandleRegex(Parameters builderParameters, MethodExpression expression, ref StreamingOptimization streamingOptimization)
    {
        if (expression.Arguments.Count != 2)
            throw new ArgumentException(
                $"Regex method was invoked with {expression.Arguments.Count} arguments ({expression})" +
                " while it should be invoked with 2 arguments e.g. Regex(foo.Name,\"^[a-z]+?\")");

        var metadata = builderParameters.Metadata;
        var queryParameters = builderParameters.QueryParameters;

        var fieldName = QueryBuilderHelper.ExtractIndexFieldName(metadata.Query, queryParameters, expression.Arguments[0], metadata);
        var (value, valueType) = QueryBuilderHelper.GetValue(metadata.Query, metadata, queryParameters, (ValueExpression)expression.Arguments[1]);
        if (valueType != ValueTokenType.String && !(valueType == ValueTokenType.Parameter && IsStringFamily(value)))
            QueryBuilderHelper.ThrowMethodExpectsArgumentOfTheFollowingType("regex", ValueTokenType.String, valueType, builderParameters.Metadata.QueryText, builderParameters.QueryParameters);

        var valueAsString = QueryBuilderHelper.CoraxGetValueAsString(value);
        var fieldMetadata = QueryBuilderHelper.GetFieldMetadata(builderParameters.Allocator, fieldName, builderParameters.Index, builderParameters.IndexFieldsMapping,
            builderParameters.FieldsToFetch, builderParameters.HasDynamics, builderParameters.DynamicFields, hasBoost: builderParameters.HasBoost);

        return streamingOptimization.TrySetMultiTermMatchAsStreamingField(fieldMetadata, MethodType.Regex)
            ? builderParameters.IndexSearcher.RegexQuery(fieldMetadata, builderParameters.Factories.GetRegexFactory(valueAsString), streamingOptimization.Forward, streamingEnabled: true, token: builderParameters.Token)
            : builderParameters.IndexSearcher.RegexQuery(fieldMetadata, builderParameters.Factories.GetRegexFactory(valueAsString), token: builderParameters.Token);

        bool IsStringFamily(object value)
        {
            return value is string or StringSegment or LazyStringValue;
        }
    }

    public static OrderMetadata[] GetSortMetadata(Parameters builderParameters, out bool hasEmpty)
    {
        hasEmpty = false;
        var query = builderParameters.Query;
        var index = builderParameters.Index;
        var getSpatialField = builderParameters.Factories?.GetSpatialFieldFactory;
        var indexMapping = builderParameters.IndexFieldsMapping;
        var queryMapping = builderParameters.FieldsToFetch;
        var allocator = builderParameters.Allocator;
        if (query.PageSize == 0) // no need to sort when counting only
        {
            return null;
        }

        if (query.Metadata.HasMoreLikeThis && query.Metadata.Query.Joins == null) // MoreLikeThis orders its matches by score itself (with a JOIN the query orders them like any other)
            return null;
        if (query.IsJoinSource && query.Metadata.OrderBy == null)
            return null; // the sub-query of a join is scanned as a whole, the order of its keys is irrelevant (unless the main query orders by it)

        var orderByFields = query.Metadata.OrderBy;

        if (orderByFields == null)
        {
            if (builderParameters.HasBoost && builderParameters.AllowImplicitScoreOrdering)
            {
                // in case when we've single vector clause and we exose the score or documents are boosted, we have to go through 
                // order by primitive to retrieve or boost them; otherwise scores are detected as natively sorted
                if (builderParameters.IsVectorSingleClause && index.Configuration.CoraxIncludeDocumentScore == false && builderParameters.IndexSearcher.DocumentsAreBoosted == false)
                    return null;
                
                if (builderParameters.Metadata.HasVectorSearch == false)
                    builderParameters.IndexReadOperation?.AssertCanOrderByScoreAutomaticallyWhenBoostingOrVectorSearchIsInvolved();
                
                return new[] { new OrderMetadata(true, MatchCompareFieldType.Score) };
            }

            return null;
        }

        int sortIndex = 0;
        var sortArray = new OrderMetadata[16];

        if (orderByFields.Length > sortArray.Length)
            throw new InvalidOperationException($"Corax does not support ordering by more than {sortArray.Length} properties.");

        var fieldIndex = -1;
        foreach (var field in orderByFields)
        {
            fieldIndex++;
            if (field.JoinAlias != null)
            {
                // the keys of the join carry their position in the order of the joined index as a rank
                if (query.JoinResults == null)
                    throw new InvalidQueryException($"ORDER BY {field.JoinAlias}.{field.Name} is only supported in a query that has the JOIN.", query.Query, query.QueryParameters);

                if (builderParameters.IndexSearcher.DocumentsAreBoosted)
                    throw new InvalidQueryException($"ORDER BY {field.JoinAlias}.{field.Name} is not supported when the documents of the main index are boosted.", query.Query, query.QueryParameters);

                if (fieldIndex == 0 || orderByFields[fieldIndex - 1].JoinAlias != field.JoinAlias)
                    sortArray[sortIndex++] = new OrderMetadata(true, MatchCompareFieldType.Rank); // the lowest position first

                continue;
            }

            var nullsSortMode = (field.NullsOrdering, field.Ascending) switch
            {
                (NullsOrderingType.First, Ascending: true) => NullsSortMode.NullsSmallest,
                (NullsOrderingType.First, Ascending: false) => NullsSortMode.NullsLargest,
                (NullsOrderingType.Last, Ascending: true) => NullsSortMode.NullsLargest, 
                (NullsOrderingType.Last, Ascending: false)=> NullsSortMode.NullsSmallest,
                _ => (NullsSortMode?)null
            };

            if (field.OrderingType == OrderByFieldType.Random)
            {
                var seed = field.Arguments is { Length: > 0 } ?
                    (int)Hashing.XXHash32.CalculateRaw(field.Arguments[0].NameOrValue) :
                    Random.Shared.Next(); // use a random seed if none is provided  
                sortArray[sortIndex++] = new OrderMetadata(seed);
                continue;
            }

            if (field.OrderingType == OrderByFieldType.Score)
            {
                if (field.Ascending)
                    sortArray[sortIndex++] = new OrderMetadata(true, MatchCompareFieldType.Score);
                else
                    sortArray[sortIndex++] = new OrderMetadata(true, MatchCompareFieldType.Score, ascending: false);

                continue;
            }

            var fieldMetadata = QueryBuilderHelper.GetFieldIdForOrderBy(allocator, field.Name, index, builderParameters.HasDynamics,
                builderParameters.DynamicFields, indexMapping, queryMapping, false);
            
            bool fieldIsEmpty = builderParameters.IndexSearcher.GetTermAmountInField(fieldMetadata) == 0;
            if (fieldIsEmpty)
            {
                if (builderParameters.IndexReadOperation.IsSharded == false)
                    continue;
                hasEmpty = true;
            }
            
            if (field.OrderingType == OrderByFieldType.Distance)
            {
                var spatialField = getSpatialField(field.Name);

                int lastArgument;
                IPoint point;
                switch (field.Method)
                {
                    case MethodType.Spatial_Circle:
                        var cLatitude = field.Arguments[1].GetDouble(query.QueryParameters);
                        var cLongitude = field.Arguments[2].GetDouble(query.QueryParameters);
                        lastArgument = 2;
                        point = spatialField.ReadPoint(cLatitude, cLongitude).Center;
                        break;
                    case MethodType.Spatial_Wkt:
                        var wkt = field.Arguments[0].GetString(query.QueryParameters);
                        SpatialUnits? spatialUnits = null;
                        lastArgument = 1;
                        if (field.Arguments.Length > 1)
                        {
                            spatialUnits = Enum.Parse<SpatialUnits>(field.Arguments[1].GetString(query.QueryParameters), ignoreCase: true);
                            lastArgument = 2;
                        }

                        point = spatialField.ReadShape(wkt, spatialUnits).Center;
                        break;
                    case MethodType.Spatial_Point:
                        var pLatitude = field.Arguments[0].GetDouble(query.QueryParameters);
                        var pLongitude = field.Arguments[1].GetDouble(query.QueryParameters);
                        lastArgument = 2;
                        point = spatialField.ReadPoint(pLatitude, pLongitude).Center;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                var roundTo = field.Arguments.Length > lastArgument
                    ? field.Arguments[lastArgument].GetDouble(query.QueryParameters)
                    : 0D;

                sortArray[sortIndex++] = new OrderMetadata(fieldMetadata, field.Ascending, MatchCompareFieldType.Spatial, point, roundTo,
                    spatialField.Units is SpatialUnits.Kilometers
                        ? global::Corax.Utils.Spatial.SpatialUnits.Kilometers
                        : global::Corax.Utils.Spatial.SpatialUnits.Miles, fieldIsEmpty, nullsSortMode,
                    useNearestPoint: IndexDefinitionBaseServerSide.IndexVersion.IsSpatialSortByNearestPointSupported(index.Definition.Version));
                continue;
            }

            var orderingType = field.OrderingType;
            if (orderingType is OrderByFieldType.Implicit && index.Configuration.OrderByTicksAutomaticallyWhenDatesAreInvolved && index.IndexFieldsPersistence.HasTimeValues(field.Name.Value))
                orderingType = OrderByFieldType.Long;

            var metadataField = QueryBuilderHelper.GetFieldIdForOrderBy(allocator, field.Name.Value, index, builderParameters.HasDynamics,
                builderParameters.DynamicFields,
                indexMapping, queryMapping, false);
            OrderMetadata? temporaryOrder = null;
            switch (orderingType)
            {
                case OrderByFieldType.Custom:
                    throw new NotSupportedInCoraxException($"{nameof(Corax)} doesn't support Custom OrderBy.");
                case OrderByFieldType.AlphaNumeric:
                    sortArray[sortIndex++] = new OrderMetadata(metadataField, field.Ascending, MatchCompareFieldType.Alphanumeric, fieldIsEmpty, nullsSortMode);
                    continue;
                case OrderByFieldType.Long:
                    temporaryOrder = new OrderMetadata(metadataField, field.Ascending, MatchCompareFieldType.Integer, fieldIsEmpty, nullsSortMode);
                    break;
                case OrderByFieldType.Double:
                    temporaryOrder = new OrderMetadata(metadataField, field.Ascending, MatchCompareFieldType.Floating, fieldIsEmpty, nullsSortMode);
                    break;
            }

            sortArray[sortIndex++] = temporaryOrder ?? new OrderMetadata(metadataField, field.Ascending, MatchCompareFieldType.Sequence, fieldIsEmpty, nullsSortMode);
        }

        return sortArray[0..sortIndex];
    }

    private static IQueryMatch OrderBy(Parameters builderParameters, IQueryMatch match, in OrderMetadata[] orderMetadataSource, bool hasEmptySortingMatches)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var indexSearcher = builderParameters.IndexSearcher;
        var take = builderParameters.Take;
        OrderMetadata[] orderMetadata = null;
        if (hasEmptySortingMatches == false)
            orderMetadata = orderMetadataSource;
        else
        {
            var currentIdx = 0;
            foreach (var orderMetadataItem in orderMetadataSource)
            {
                if (orderMetadataItem.FieldHasNoTerms)
                    continue;
                
                orderMetadata ??= new OrderMetadata[orderMetadataSource.Length];
                orderMetadata[currentIdx++] = orderMetadataItem;
                
            }

            orderMetadata = currentIdx == 0 ? [] : orderMetadata![..currentIdx];
        }

        var defaultNullsSortMode = builderParameters.Index.Configuration.NullsSortMode;
        switch (orderMetadata.Length)
        {
            case 0:
                return match;
            case 1:
                return indexSearcher.OrderBy(match, orderMetadata[0], defaultNullsSortMode, take, builderParameters.Token);
            default:
                return indexSearcher.OrderBy(match, orderMetadata, defaultNullsSortMode, take, builderParameters.Token);
        }
    }
}
