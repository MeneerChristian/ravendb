using System;
using System.Collections.Generic;
using System.Threading;
using Corax.Mappings;
using Corax.Querying;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Queries.Timings;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Server;

namespace Raven.Server.Documents.Indexes.Persistence.Corax;

public partial class CoraxQueryBuilder
{
    public sealed class Parameters
    {
        public readonly IndexSearcher IndexSearcher;
        public readonly TransactionOperationContext ServerContext;
        public readonly DocumentsOperationContext DocumentsContext;
        public readonly IndexQueryServerSide Query;
        public readonly Index Index;
        public readonly BlittableJsonReaderObject QueryParameters;
        public readonly QueryBuilderFactories Factories;
        public readonly IndexFieldsMapping IndexFieldsMapping;
        public readonly FieldsToFetch FieldsToFetch;
        public readonly Dictionary<string, CoraxHighlightingTermIndex> HighlightingTerms;
        public readonly int Take;
        public readonly CancellationToken Token;
        public readonly List<string> BuildSteps;
        public readonly MemoizationMatchProvider<AllEntriesMatch> AllEntries;
        public readonly QueryMetadata Metadata;
        public readonly bool HasDynamics;
        public readonly Lazy<List<string>> DynamicFields;
        public readonly ByteStringContext Allocator;
        public readonly bool QueryHasBoost;
        public readonly bool AllowImplicitScoreOrdering;
        internal int NegationDepth;
        public readonly bool DeduplicationDisabled;
        public readonly IndexReadOperationBase IndexReadOperation;
        public StreamingOptimization StreamingDisabled;
        public bool JoinScoresNeeded;

        // Only a query that reads a page of the matches (and not all of them) may take the first entries of a join in its order, see JoinOrderedWalk
        public bool AllowJoinOrderedWalk;

        // A query that reads a page of the matches in the order of the ids of the entries may test the entries one by one instead of reading the whole join, see JoinFirstEntries
        public bool AllowJoinFirstEntries;
        // Gives the match of the more-like-this condition of a query with a JOIN
        public Func<IQueryMatch> MoreLikeThisMatch;
        public readonly bool IsVectorSingleClause;
        public readonly QueryTimeScope QueryTime;
        public readonly QueryTimingsScope QueryTimings;

        internal Parameters(IndexSearcher searcher, ByteStringContext allocator, TransactionOperationContext serverContext, DocumentsOperationContext documentsContext,
            IndexQueryServerSide query, Index index, BlittableJsonReaderObject queryParameters, QueryBuilderFactories factories, IndexFieldsMapping indexFieldsMapping,
            FieldsToFetch fieldsToFetch, Dictionary<string, CoraxHighlightingTermIndex> highlightingTerms, int take, bool deduplicationDisabled, IndexReadOperationBase indexReadOperation = null, List<string> buildSteps = null, QueryTimeScope queryTime = null, QueryTimingsScope queryTimings = null, CancellationToken token = default, bool allowImplicitScoreOrdering = true)
        {
            QueryTime = queryTime;
            QueryTimings = queryTimings;
            IndexSearcher = searcher;
            ServerContext = serverContext;
            Query = query;
            Index = index;
            QueryParameters = queryParameters;
            Factories = factories;
            IndexFieldsMapping = indexFieldsMapping;
            FieldsToFetch = fieldsToFetch;
            DocumentsContext = documentsContext;
            HighlightingTerms = highlightingTerms;
            Take = take;
            Token = token;
            BuildSteps = buildSteps;
            AllEntries = IndexSearcher.Memoize(IndexSearcher.AllEntries());
            Metadata = query.Metadata;
            HasDynamics = index.Definition.HasDynamicFields;
            IsVectorSingleClause = Metadata.Query.Where is MethodExpression me && QueryMethod.GetMethodType(me.Name.Value) == MethodType.Vector_Search && Metadata.OrderBy is null or {Length: 0};
            DynamicFields = HasDynamics
                ? new Lazy<List<string>>(() => IndexSearcher.GetFields())
                : null;

            AllowImplicitScoreOrdering = allowImplicitScoreOrdering
                                         && (index.Configuration.OrderByScoreAutomaticallyWhenBoostingIsInvolved
                                             || (Metadata.HasVectorSearch && index.Configuration.CoraxVectorSearchOrderByScoreAutomatically));

            // in case when we've implicit boosting we've built primitives with scoring enabled
            // A join adds the scores of its sub-queries to the outer rows, so it asks for scores whether or not the page is ordered by them
            var joinNeedsScores = (query.Metadata.Query.Joins != null && query.Metadata.OrderBy is null && query.Metadata.HasFacet == false)
                                  || query.IsJoinSource
                                  || (index.Configuration.CoraxIncludeDocumentScore && HasJoinOrdering(query.Metadata.OrderBy));
            QueryHasBoost = ((index.HasBoostedFields
                        || query.Metadata.HasBoost
                        || IsVectorSingleClause
                        || (query.Metadata.HasVectorSearch && index.Configuration.CoraxVectorSearchOrderByScoreAutomatically)
                        || HasBoostingAsOrderingType(query.Metadata.OrderBy))
                       && (IsVectorSingleClause || ScoresAreConsumed(query, AllowImplicitScoreOrdering)))
                       || joinNeedsScores;
            Allocator = allocator;
            IndexReadOperation = indexReadOperation;
            DeduplicationDisabled = deduplicationDisabled;
        }
        
        public bool NeedsScoresBuffer() => HasBoost
            && (Index.Configuration.CoraxIncludeDocumentScore || Query.IsJoinSource || (IndexReadOperation?.IsSharded == true && Metadata.HasVectorSearch));

        private static bool HasJoinOrdering(OrderByField[] orderBy)
        {
            if (orderBy is null)
                return false;

            foreach (var field in orderBy)
                if (field.JoinAlias != null)
                    return true;

            return false;
        }

        // AndNotMatch never scores its excluded side
        public bool HasBoost => QueryHasBoost && NegationDepth == 0;

        private static bool ScoresAreConsumed(IndexQueryServerSide query, bool allowImplicitScoreOrdering)
        {
            if (query.PageSize == 0)
                return false;

            if (query.Metadata.HasMoreLikeThis)
                return true;

            if (query.Metadata.OrderBy is not null)
                return HasBoostingAsOrderingType(query.Metadata.OrderBy);

            return allowImplicitScoreOrdering;
        }

        private static bool HasBoostingAsOrderingType(OrderByField[] orderBy)
        {
            if (orderBy is null)
                return false;

            foreach (var field in orderBy)
                if (field.OrderingType == OrderByFieldType.Score)
                    return true;

            return false;
        }
    }
}
