using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Indexes.Vector;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Queries.MoreLikeThis;
using Raven.Client.Documents.Queries.TimeSeries;
using Raven.Client.Documents.Queries.Vector;
using Raven.Client.Documents.Session.Operations;
using Raven.Client.Documents.Session.Operations.Lazy;
using Raven.Client.Documents.Session.Tokens;
using Raven.Client.Exceptions;
using Raven.Client.Extensions;
using Sparrow;
using Sparrow.Json;
using Sparrow.Utils;

namespace Raven.Client.Documents.Session
{
    /// <summary>
    ///   A query against a Raven index
    /// </summary>
    public abstract partial class AbstractDocumentQuery<T, TSelf> : IDocumentQueryCustomization, IAbstractDocumentQuery<T>, IAbstractDocumentQueryAccessor
                                                            where TSelf : AbstractDocumentQuery<T, TSelf>
    {
        private readonly Dictionary<string, string> _aliasToGroupByFieldName = new Dictionary<string, string>();
        private const string DefaultParameterPrefix = "p";
        private static readonly List<string> _pregeneratedParametersWithDefaultPrefix = Enumerable.Range(0, 128).Select(i => $"{DefaultParameterPrefix}{i.ToInvariantString()}").ToList();
        protected QueryOperator DefaultOperator;

        private readonly LinqPathProvider _linqPathProvider;

        protected readonly HashSet<Type> RootTypes = new HashSet<Type>
        {
            typeof (T)
        };

        private static TypeCache<Func<object, string>> _implicitStringsCache = new (1024);

        /// <summary>
        /// Whether to negate the next operation
        /// </summary>
        protected bool Negate;

        /// <summary>
        /// Whether to negate the next operation in Filter
        /// </summary>
        protected bool NegateFilter;

        /// <summary>
        /// The index to query
        /// </summary>
        public string IndexName { get; }

        public string CollectionName { get; }

        private int _currentClauseDepth;

        protected string QueryRaw;

        internal bool IsFilterActive { get { return FilterModeStack.Any() && FilterModeStack.Peek() is true; } }

        protected Stack<bool> FilterModeStack = new();

        protected internal Parameters QueryParameters = new Parameters();

        protected bool IsIntersect;

        protected bool IsGroupBy;


        /// <summary>
        /// The session for this query
        /// </summary>
        protected readonly InMemoryDocumentSessionOperations TheSession;

        /// <summary>
        ///   The page size to use when querying the index
        /// </summary>
        protected long? PageSize;

        protected LinkedList<QueryToken> SelectTokens = new LinkedList<QueryToken>();

        protected readonly FromToken FromToken;

        protected readonly IEnumerable<DeclareToken> DeclareTokens;

        protected List<LoadToken> LoadTokens;

        public FieldsToFetchToken FieldsToFetchToken { get; set; }

        public bool IsProjectInto { get; }

        protected LinkedList<QueryToken> WhereTokens = new LinkedList<QueryToken>();

        protected LinkedList<QueryToken> GroupByTokens = new LinkedList<QueryToken>();

        protected LinkedList<QueryToken> OrderByTokens = new LinkedList<QueryToken>();

        protected LinkedList<QueryToken> FilterTokens = new();

        /// <summary>
        ///   which record to start reading from
        /// </summary>
        protected long Start;

        private readonly DocumentConventions _conventions;

        /// <summary>
        /// Limits filter clause.
        /// </summary>
        protected long? FilterLimit;

        /// <summary>
        /// Timeout for this query
        /// </summary>
        protected TimeSpan? Timeout;

        /// <summary>
        /// Should we wait for non stale results
        /// </summary>
        protected bool TheWaitForNonStaleResults;

        /// <summary>
        /// Holds the query stats
        /// </summary>
        protected QueryStatistics QueryStats = new QueryStatistics();

        /// <summary>
        /// Determines if entities should be tracked and kept in memory
        /// </summary>
        protected bool DisableEntitiesTracking;

        /// <summary>
        /// Determine if query results should be cached.
        /// </summary>
        protected bool DisableCaching;

        protected string QueryTag;

        protected ProjectionBehavior? ProjectionBehavior;

        public bool IsDistinct => SelectTokens.First?.Value is DistinctToken;

        /// <summary>
        /// Gets the document convention from the query session
        /// </summary>
        public DocumentConventions Conventions => _conventions;

        /// <summary>
        ///   Gets the session associated with this document query
        /// </summary>
        public IDocumentSession Session => (IDocumentSession)TheSession;

        public IAsyncDocumentSession AsyncSession => (IAsyncDocumentSession)TheSession;

        public bool IsDynamicMapReduce => GroupByTokens.Count > 0;

        private bool _isInMoreLikeThis;

        private string _includesAlias;

        private TimeSpan DefaultTimeout
        {
            get
            {
                if (Debugger.IsAttached) // increase timeout if we are debugging
                    return TimeSpan.FromMinutes(15);

                return _conventions.WaitForNonStaleResultsTimeout;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AbstractDocumentQuery{T, TSelf}"/> class.
        /// </summary>
        protected AbstractDocumentQuery(InMemoryDocumentSessionOperations session,
                                     string indexName,
                                     string collectionName,
                                     bool isGroupBy,
                                     IEnumerable<DeclareToken> declareTokens,
                                     List<LoadToken> loadTokens,
                                     string fromAlias = null,
                                     bool? isProjectInto = false)
        {
            IsGroupBy = isGroupBy;
            IndexName = indexName;
            CollectionName = collectionName;

            FromToken = FromToken.Create(indexName, collectionName, fromAlias);

            DeclareTokens = declareTokens;

            LoadTokens = loadTokens;

            TheSession = session;
            AfterQueryExecuted(UpdateStatsHighlightingsAndExplanations);

            _conventions = session == null ? DocumentConventions.Default : session.Conventions;
            _linqPathProvider = new LinqPathProvider(_conventions);

            IsProjectInto = isProjectInto ?? false;
        }

        #region TSelf Members

        public void UsingDefaultOperator(QueryOperator @operator)
        {
            if (GetCurrentWhereTokens().Count != 0)
                throw new InvalidOperationException("Default operator can only be set before any where clause is added.");

            DefaultOperator = @operator;
        }

        /// <inheritdoc cref="IDocumentQueryCustomization.WaitForNonStaleResults" />
        public void WaitForNonStaleResults(TimeSpan? waitTimeout = null)
        {
            if (TheWaitForNonStaleResults)
            {
                if (Timeout == null || waitTimeout.HasValue && Timeout < waitTimeout.Value)
                {
                    Timeout = waitTimeout;
                }
                return;
            }
            TheWaitForNonStaleResults = true;
            Timeout = waitTimeout ?? DefaultTimeout;
        }

        internal LazyQueryOperation<T> GetLazyQueryOperation()
        {
            QueryOperation ??= InitializeQueryOperation();

            return new LazyQueryOperation<T>(TheSession, QueryOperation, AfterQueryExecutedCallback);
        }

        public QueryOperation InitializeQueryOperation()
        {
            var beforeQueryExecutedEventArgs = new BeforeQueryEventArgs(TheSession, this);
            TheSession.OnBeforeQueryInvoke(beforeQueryExecutedEventArgs);

            var indexQuery = GetIndexQuery();

            return new QueryOperation(TheSession,
                IndexName,
                indexQuery,
                FieldsToFetchToken,
                DisableEntitiesTracking,
                isProjectInto: IsProjectInto);
        }

        /// <summary>The queries this query is joined with: the RQL of each source query (its parameters already renamed), the field of this query to join on, the field of the joined documents (null: their id), whether it is a left join and the query parameter that holds the boost (null: none).</summary>
        internal List<JoinInfo> Joins;
        private const string DefaultJoinAlias = "j";

        private static string JoinAliasOf(int index) => index == 0 ? DefaultJoinAlias : DefaultJoinAlias + index.ToInvariantString();

        /// <inheritdoc />
        public void Join(IndexQuery source, string outerKeyField, bool left = false, double? boost = null) => Join(source, outerKeyField, joinedField: null, left, boost);

        /// <inheritdoc />
        public void Join(IndexQuery source, string outerKeyField, string joinedField, bool left = false, double? boost = null)
        {
            AssertNoRawQuery();

            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(outerKeyField))
                throw new ArgumentException("The field to join on cannot be empty.", nameof(outerKeyField));
            if (boost.HasValue && ((double.IsNaN(boost.Value) || double.IsInfinity(boost.Value)) || boost.Value < 0))
                throw new ArgumentOutOfRangeException(nameof(boost), boost, "The boost of a join must be a number that is not negative.");

            // the source query was generated on its own, so its parameter names (p0, p1...) collide with ours
            var renamed = new Dictionary<string, string>();
            foreach (var parameter in source.QueryParameters ?? new Parameters())
                renamed[parameter.Key] = AddQueryParameter(parameter.Value);

            var sourceQuery = RenameParameters(source.Query, renamed);

            Joins ??= new List<JoinInfo>();
            Joins.Add(new JoinInfo(sourceQuery,
                EnsureValidFieldName(outerKeyField, isNestedPath: false),
                joinedField == null ? null : QueryFieldUtil.EscapeIfNecessary(joinedField, isPath: true),
                left,
                boost.HasValue ? AddQueryParameter(boost.Value) : null));
        }

        /// <summary>
        /// Renames the $parameters of an RQL text. The text is scanned by token: a '$name' inside a string literal, a comment or a regular expression is text, not a parameter
        /// (the JavaScript of a projection can hold an apostrophe in a comment or in a regular expression, which is not the start of a literal).
        /// </summary>
        private static string RenameParameters(string rql, Dictionary<string, string> renamed)
        {
            if (renamed.Count == 0)
                return rql;

            var result = new StringBuilder(rql.Length + 16);
            for (var i = 0; i < rql.Length;)
            {
                var c = rql[i];
                if (c == '/' && i + 1 < rql.Length && (rql[i + 1] == '/' || rql[i + 1] == '*'))
                {
                    var end = rql[i + 1] == '/' ? rql.IndexOf('\n', i) : rql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end == -1 ? rql.Length : end + (rql[i + 1] == '/' ? 0 : 2);
                    result.Append(rql, i, end - i);
                    i = end;
                    continue;
                }

                if (c == '/' && CanStartRegex(rql, i))
                {
                    var end = i + 1;
                    var inClass = false;
                    while (end < rql.Length && (rql[end] != '/' || inClass) && rql[end] != '\n')
                    {
                        if (rql[end] == '\\')
                            end++;
                        else if (rql[end] == '[')
                            inClass = true;
                        else if (rql[end] == ']')
                            inClass = false;

                        end++;
                    }

                    // no closing slash on the line: it was a division, not a regular expression
                    if (end < rql.Length && rql[end] == '/')
                    {
                        end++;
                        result.Append(rql, i, end - i);
                        i = end;
                        continue;
                    }
                }

                if (c == '\'' || c == '"' || c == '`')
                {
                    var end = i + 1;
                    while (end < rql.Length && rql[end] != c)
                        end += rql[end] == '\\' ? 2 : 1;

                    end = Math.Min(end + 1, rql.Length);
                    result.Append(rql, i, end - i);
                    i = end;
                    continue;
                }

                if (c == '$')
                {
                    var end = i + 1;
                    while (end < rql.Length && (char.IsLetterOrDigit(rql[end]) || rql[end] == '_'))
                        end++;

                    var name = rql.Substring(i + 1, end - i - 1);
                    result.Append('$').Append(renamed.TryGetValue(name, out var newName) ? newName : name);
                    i = end;
                    continue;
                }

                result.Append(c);
                i++;
            }

            return result.ToString();
        }

        // a slash starts a regular expression after an operator or an opening bracket, and a division after a value
        private static bool CanStartRegex(string text, int slash)
        {
            for (var i = slash - 1; i >= 0; i--)
            {
                if (char.IsWhiteSpace(text[i]))
                    continue;

                return "(,=:[!&|?{};+-*%<>~^".IndexOf(text[i]) != -1;
            }

            return true;
        }

        private LinkedList<QueryToken> AddAliasToTokens(LinkedList<QueryToken> tokens)
        {
            var joinAliases = GetJoinAliases(JoinOuterAlias);
            var result = new LinkedList<QueryToken>();
            foreach (var token in tokens)
            {
                result.AddLast(token switch
                {
                    WhereToken where when JoinInfo.IsJoinField(where.FieldName, joinAliases) => where,
                    WhereToken where => where.AddAlias(JoinOuterAlias),
                    OrderByToken orderBy when orderBy.IsJoinField(joinAliases) => orderBy,
                    OrderByToken orderBy => orderBy.AddAlias(JoinOuterAlias),
                    _ => token
                });
            }

            return result;
        }

        /// <inheritdoc cref="IDocumentQueryBase{T, TSelf}.JoinAlias"/>
        public void JoinAlias(string alias)
        {
            if (Joins == null || Joins.Count == 0)
                throw new InvalidOperationException("JoinAlias names the alias of the last join: call Join first.");

            if (string.IsNullOrWhiteSpace(alias))
                throw new ArgumentException("The alias of a join cannot be empty.", nameof(alias));

            if (JoinInfo.IsValidAlias(alias) == false)
                throw new ArgumentException($"'{alias}' cannot be the alias of a join: an alias is a name made of letters, digits and '_', and is not a keyword of RQL.", nameof(alias));

            if (alias == JoinOuterAlias || alias == FromToken?.Alias)
                throw new ArgumentException($"'{alias}' is the alias of the main query, a join needs an alias of its own.", nameof(alias));

            for (var i = 0; i < Joins.Count - 1; i++)
            {
                if (Joins[i].Alias == alias)
                    throw new ArgumentException($"'{alias}' is already the alias of another join.", nameof(alias));
            }

            Joins[Joins.Count - 1].Alias = alias;
        }

        /// <summary>The aliases of the joins in the order of the joins: the one that was named with JoinAlias, otherwise j, j1, j2... by position (skipping a name that the main query or another join uses).</summary>
        private string[] GetJoinAliases(string outerAlias)
        {
            if (Joins == null)
                return Array.Empty<string>();

            var aliases = new string[Joins.Count];
            var named = new HashSet<string>();
            foreach (var join in Joins)
            {
                if (join.Alias != null)
                    named.Add(join.Alias);
            }

            var aliasIndex = 0;
            for (var i = 0; i < Joins.Count; i++)
            {
                var alias = Joins[i].Alias;
                if (alias == null)
                {
                    // the alias of the main document comes from the user (the lambda parameter of a projection) and could be j, j1...
                    alias = JoinAliasOf(aliasIndex++);
                    while (alias == outerAlias || named.Contains(alias))
                        alias = JoinAliasOf(aliasIndex++);

                    if (HasMember(typeof(T), alias))
                        throw new InvalidOperationException($"The default alias '{alias}' of the join #{i + 1} is also the name of a member of {typeof(T).Name}, so '{alias}.<field>' could mean a field of the main document or of the joined documents. Name the join with JoinAlias.");
                }
                else if (alias == outerAlias)
                {
                    throw new InvalidOperationException($"'{alias}' is the alias of the main query, a join needs an alias of its own.");
                }

                aliases[i] = alias;
            }

            return aliases;
        }

        private static bool HasMember(Type type, string name)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private void BuildJoin(StringBuilder writer, string outerAlias)
        {
            if (Joins == null)
                return;

            var aliases = GetJoinAliases(outerAlias);
            for (var i = 0; i < Joins.Count; i++)
            {
                var alias = aliases[i];
                writer.Append(Joins[i].IsLeft ? " left join (" : " join (").Append(Joins[i].SourceQuery).Append(") as ").Append(alias).Append(" on ");

                // the id of the main document is not a field of it
                if (Joins[i].OuterKeyField == Constants.Documents.Indexing.Fields.DocumentIdFieldName)
                    writer.Append("id(").Append(outerAlias).Append(')');
                else
                    writer.Append(outerAlias).Append('.').Append(Joins[i].OuterKeyField);

                writer.Append(" = ");

                if (Joins[i].JoinedField == null)
                    writer.Append("id(").Append(alias).Append(')');
                else
                    writer.Append(alias).Append('.').Append(Joins[i].JoinedField);

                if (Joins[i].BoostParameter != null)
                    writer.Append(" boost $").Append(Joins[i].BoostParameter);
            }
        }

        public IndexQuery GetIndexQuery()
        {
            var serverVersion = TheSession?.RequestExecutor?.LastServerVersion;
            var compatibilityMode = serverVersion != null && string.Compare(serverVersion, "4.2", StringComparison.Ordinal) < 0;

            var query = ToString(compatibilityMode);
            var indexQuery = GenerateIndexQuery(query);
            BeforeQueryExecutedCallback?.Invoke(indexQuery);

            return indexQuery;
        }

        /// <summary>
        ///   Gets the fields for projection
        /// </summary>
        /// <returns></returns>
        public IEnumerable<string> GetProjectionFields()
        {
            return FieldsToFetchToken?.Projections ?? Enumerable.Empty<string>();
        }

        /// <summary>
        /// Order the search results randomly
        /// </summary>
        public void RandomOrdering()
        {
            AssertNoRawQuery();

            NoCaching();
            OrderByTokens.AddLast(OrderByToken.Random);
        }

        /// <summary>
        /// Order the search results randomly using the specified seed
        /// this is useful if you want to have repeatable random queries
        /// </summary>
        public void RandomOrdering(string seed)
        {
            AssertNoRawQuery();

            if (string.IsNullOrWhiteSpace(seed))
            {
                RandomOrdering();
                return;
            }

            NoCaching();
            OrderByTokens.AddLast(OrderByToken.CreateRandom(seed));
        }

#if FEATURE_CUSTOM_SORTING
        public void CustomSortUsing(string typeName, bool descending)
        {
            if (descending)
            {
                OrderByDescending(Constants.Documents.Indexing.Fields.CustomSortFieldName + ";" + typeName);
                return;
            }

            OrderBy(Constants.Documents.Indexing.Fields.CustomSortFieldName + ";" + typeName);
        }
#endif

        private void Projection(ProjectionBehavior projectionBehavior)
        {
            ProjectionBehavior = projectionBehavior;
        }

        internal void AddGroupByAlias(string fieldName, string projectedName)
        {
            _aliasToGroupByFieldName[projectedName] = fieldName;
        }

        private void AssertNoRawQuery()
        {
            if (QueryRaw != null)
                throw new InvalidOperationException(
                    "RawQuery was called, cannot modify this query by calling on operations that would modify the query (such as Where, Select, OrderBy, GroupBy, etc)");
        }

        public void RawQuery(string query)
        {
            if (SelectTokens.Count != 0 ||
                WhereTokens.Count != 0 ||
                OrderByTokens.Count != 0 ||
                GroupByTokens.Count != 0 ||
                FilterTokens.Count != 0)
                throw new InvalidOperationException("You can only use RawQuery on a new query, without applying any operations (such as Where, Select, OrderBy, GroupBy, etc)");
            QueryRaw = query;
        }

        public void AddParameter(string name, object value)
        {
            name = name.TrimStart('$');
            if (QueryParameters.ContainsKey(name))
                throw new InvalidOperationException("The parameter " + name + " was already added");

            QueryParameters[name] = value;
        }

        /// <summary>
        /// Sets a user-defined tag on the query request.
        /// </summary>
        /// <param name="tag">User-defined query tag.</param>
        public void WithTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                throw new ArgumentException("Query tag cannot be null or whitespace.", nameof(tag));

            QueryTag = tag;
        }

        /// <inheritdoc cref="IDocumentQuery{T}.GroupBy(string,string[])"/>
        public void GroupBy(string fieldName, params string[] fieldNames)
        {
            GroupBy((fieldName, GroupByMethod.None), fieldNames?.Select(x => (x, GroupByMethod.None)).ToArray());
        }

        /// <inheritdoc cref="IAbstractDocumentQuery{T}.GroupBy(ValueTuple{string, GroupByMethod}, ValueTuple{string, GroupByMethod}[])"/>
        public void GroupBy((string Name, GroupByMethod Method) field, params (string Name, GroupByMethod Method)[] fields)
        {
            if (FromToken.IsDynamic == false)
                throw new InvalidOperationException("GroupBy only works with dynamic queries.");
            AssertNoRawQuery();
            IsGroupBy = true;

            var fieldName = EnsureValidFieldName(field.Name, isNestedPath: false);

            GroupByTokens.AddLast(GroupByToken.Create(fieldName, field.Method));

            if (fields == null || fields.Length <= 0)
                return;

            foreach (var item in fields)
            {
                fieldName = EnsureValidFieldName(item.Name, isNestedPath: false);

                GroupByTokens.AddLast(GroupByToken.Create(fieldName, item.Method));
            }
        }

        /// <inheritdoc />
        public void GroupByKey(string fieldName = null, string projectedName = null)
        {
            AssertNoRawQuery();
            IsGroupBy = true;

            if (projectedName != null && _aliasToGroupByFieldName.TryGetValue(projectedName, out var aliasedFieldName))
            {
                if (fieldName == null || fieldName.Equals(projectedName, StringComparison.Ordinal))
                    fieldName = aliasedFieldName;
            }
            else if (fieldName != null && _aliasToGroupByFieldName.TryGetValue(fieldName, out aliasedFieldName))
            {
                fieldName = aliasedFieldName;
            }

            SelectTokens.AddLast(GroupByKeyToken.Create(fieldName, projectedName));
        }

        /// <inheritdoc />
        public void GroupBySum(string fieldName, string projectedName = null)
        {
            AssertNoRawQuery();
            IsGroupBy = true;

            fieldName = EnsureValidFieldName(fieldName, isNestedPath: false);

            SelectTokens.AddLast(GroupBySumToken.Create(fieldName, projectedName));
        }

        /// <inheritdoc />
        public void GroupByCount(string projectedName = null)
        {
            AssertNoRawQuery();
            IsGroupBy = true;

            SelectTokens.AddLast(GroupByCountToken.Create(projectedName));
        }

        /// <inheritdoc cref="IDocumentQueryBase{T, TSelf}.WhereMatches"/>
        public void WhereMatches(string joinAlias)
        {
            if (string.IsNullOrWhiteSpace(joinAlias))
                throw new ArgumentException("The alias of the join cannot be empty.", nameof(joinAlias));

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);
            NegateIfNeeded(tokens, null);

            tokens.AddLast(MatchesToken.Create(joinAlias));
        }

        public void WhereTrue()
        {
            var tokens = GetCurrentWhereTokens();

            AppendOperatorIfNeeded(tokens);
            NegateIfNeeded(tokens, null);

            tokens.AddLast(TrueToken.Instance);
        }

        public MoreLikeThisScope MoreLikeThis()
        {
            AppendOperatorIfNeeded(WhereTokens);

            var token = new MoreLikeThisToken();
            WhereTokens.AddLast(token);

            _isInMoreLikeThis = true;
            return new MoreLikeThisScope(token, AddQueryParameter, () => _isInMoreLikeThis = false);
        }

        /// <summary>
        ///   This function exists solely to forbid in memory where clause on IDocumentQuery, because
        ///   that is nearly always a mistake.
        /// </summary>
        [Obsolete(
            @"
You cannot issue an in memory filter - such as Where(x=>x.Name == ""Ayende"") - on IDocumentQuery.
This is likely a bug, because this will execute the filter in memory, rather than in RavenDB.
Consider using session.Query<T>() instead of session.Advanced.DocumentQuery<T>. The session.Query<T>() method fully supports Linq queries, while session.Advanced.DocumentQuery<T>() is intended for lower level API access.
If you really want to do in memory filtering on the data returned from the query, you can use: session.Advanced.DocumentQuery<T>().ToList().Where(x=>x.Name == ""Ayende"")
"
            , true)]
        public IEnumerable<T> Where(Func<T, bool> predicate)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        ///   This function exists solely to forbid in memory where clause on IDocumentQuery, because
        ///   that is nearly always a mistake.
        /// </summary>
        [Obsolete(
            @"
You cannot issue an in memory filter - such as Count(x=>x.Name == ""Ayende"") - on IDocumentQuery.
This is likely a bug, because this will execute the filter in memory, rather than in RavenDB.
Consider using session.Query<T>() instead of session.Advanced.DocumentQuery<T>. The session.Query<T>() method fully supports Linq queries, while session.Advanced.DocumentQuery<T>() is intended for lower level API access.
If you really want to do in memory filtering on the data returned from the query, you can use: session.Advanced.DocumentQuery<T>().ToList().Count(x=>x.Name == ""Ayende"")
"
            , true)]
        public int Count(Func<T, bool> predicate)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        ///   This function exists solely to forbid Linq group by clause on IDocumentQuery
        /// </summary>
        [Obsolete(
            @"
Use session.Query<T>() instead of session.Advanced.DocumentQuery<T>. The session.Query<T>() method fully supports Linq to issue group by grouping, while session.Advanced.DocumentQuery<T>() is intended for lower level API access.
"
            , true)]
        public IEnumerable<IGrouping<TKey, T>> GroupBy<TKey>(Func<T, TKey> keySelector)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        ///   This function exists solely to forbid Linq group by clause on IDocumentQuery
        /// </summary>
        [Obsolete(
            @"
Use session.Query<T>() instead of session.Advanced.DocumentQuery<T>. The session.Query<T>() method fully supports Linq to issue group by grouping, while session.Advanced.DocumentQuery<T>() is intended for lower level API access.
"
            , true)]
        public IEnumerable<IGrouping<TKey, TElement>> GroupBy<TKey, TElement>(Func<T, TKey> keySelector, Func<T, TElement> elementSelector)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        ///   This function exists solely to forbid Linq group by clause on IDocumentQuery
        /// </summary>
        [Obsolete(
            @"
Use session.Query<T>() instead of session.Advanced.DocumentQuery<T>. The session.Query<T>() method fully supports Linq to issue group by grouping, while session.Advanced.DocumentQuery<T>() is intended for lower level API access.
"
            , true)]
        public IEnumerable<IGrouping<TKey, T>> GroupBy<TKey>(Func<T, TKey> keySelector, IEqualityComparer<TKey> comparer)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        ///   This function exists solely to forbid Linq group by clause on IDocumentQuery
        /// </summary>
        [Obsolete(
            @"
Use session.Query<T>() instead of session.Advanced.DocumentQuery<T>. The session.Query<T>() method fully supports Linq to issue group by grouping, while session.Advanced.DocumentQuery<T>() is intended for lower level API access.
"
            , true)]
        public IEnumerable<IGrouping<TKey, TElement>> GroupBy<TKey, TElement>(Func<T, TKey> keySelector, Func<T, TElement> elementSelector, IEqualityComparer<TKey> comparer)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        ///   Takes the specified count.
        /// </summary>
        /// <param name = "count">The count.</param>
        /// <returns></returns>
        public void Take(long count)
        {
            PageSize = count;
        }

        /// <summary>
        ///   Skips the specified count.
        /// </summary>
        /// <param name = "count">The count.</param>
        /// <returns></returns>
        public void Skip(long count)
        {
            Start = count;
        }

        /// <summary>
        ///   Filter the results from the index using the specified where clause.
        /// </summary>
        public void WhereLucene(string fieldName, string whereClause, bool exact)
        {
            fieldName = EnsureValidFieldName(fieldName, isNestedPath: false);

            var tokens = GetCurrentWhereTokens();

            AppendOperatorIfNeeded(tokens);
            NegateIfNeeded(tokens, fieldName);

            var options = exact ? new WhereToken.WhereOptions(exact) : null;
            var whereToken = WhereToken.Create(WhereOperator.Lucene, fieldName, AddQueryParameter(whereClause), options);
            tokens.AddLast(whereToken);
        }

        /// <summary>
        ///   Simplified method for opening a new clause within the query
        /// </summary>
        /// <returns></returns>
        public void OpenSubclause()
        {
            _currentClauseDepth++;

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);
            NegateIfNeeded(tokens, null);

            tokens.AddLast(OpenSubclauseToken.Create());
        }

        /// <summary>
        ///   Simplified method for closing a clause within the query
        /// </summary>
        /// <returns></returns>
        public void CloseSubclause()
        {
            _currentClauseDepth--;

            var tokens = GetCurrentWhereTokens();
            tokens.AddLast(CloseSubclauseToken.Create());
        }

        /// <summary>
        ///   Matches value
        /// </summary>
        public void WhereEquals(string fieldName, object value, bool exact = false)
        {
            WhereEquals(new WhereParams
            {
                FieldName = fieldName,
                Value = value,
                Exact = exact
            });
        }

        public void WhereEquals(string fieldName, MethodCall method, bool exact = false)
        {
            WhereEquals(fieldName, (object)method, exact);
        }

        /// <summary>
        ///   Matches value
        /// </summary>
        public void WhereEquals(WhereParams whereParams)
        {
            if (Negate)
            {
                Negate = false;
                WhereNotEquals(whereParams);
                return;
            }

            whereParams.FieldName = EnsureValidFieldName(whereParams.FieldName, whereParams.IsNestedPath);

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);

            if (IfValueIsMethod(WhereOperator.Equals, whereParams, tokens))
                return;

            var transformToEqualValue = TransformValue(whereParams);
            var addQueryParameter = AddQueryParameter(transformToEqualValue);
            var whereToken = WhereToken.Create(WhereOperator.Equals, whereParams.FieldName, addQueryParameter,
                new WhereToken.WhereOptions(whereParams.Exact));
            tokens.AddLast(whereToken);
        }

        private bool IfValueIsMethod(WhereOperator op, WhereParams whereParams, LinkedList<QueryToken> tokens)
        {
            if (whereParams.Value is MethodCall mc)
            {
                var args = new string[mc.Args.Length];
                for (var index = 0; index < mc.Args.Length; index++)
                {
                    args[index] = AddQueryParameter(mc.Args[index]);
                }

                WhereToken token;
                var type = mc.GetType();
                if (type == typeof(CmpXchg))
                {
                    token = WhereToken.Create(op, whereParams.FieldName, null,
                        new WhereToken.WhereOptions(WhereToken.MethodsType.CmpXchg, args, mc.AccessPath, whereParams.Exact));
                }
                else if (mc is RavenDocumentQuery.Time dtmc)
                {
                    token = WhereToken.Create(op, whereParams.FieldName, null,
                        new WhereToken.WhereOptions(dtmc.MethodType, args, mc.AccessPath, whereParams.Exact));
                }
                else
                {
                    throw new ArgumentException($"Unknown method {type}");
                }

                tokens.AddLast(token);
                return true;
            }
            return false;
        }

        /// <summary>
        ///   Not matches value
        /// </summary>
        public void WhereNotEquals(string fieldName, object value, bool exact = false)
        {
            WhereNotEquals(new WhereParams
            {
                FieldName = fieldName,
                Value = value,
                Exact = exact
            });
        }

        public void WhereNotEquals(string fieldName, MethodCall method, bool exact = false)
        {
            WhereNotEquals(fieldName, (object)method, exact);
        }

        /// <summary>
        ///   Not matches value
        /// </summary>
        public void WhereNotEquals(WhereParams whereParams)
        {
            if (Negate)
            {
                Negate = false;
                WhereEquals(whereParams);
                return;
            }

            var transformToEqualValue = TransformValue(whereParams);

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);

            whereParams.FieldName = EnsureValidFieldName(whereParams.FieldName, whereParams.IsNestedPath);

            if (IfValueIsMethod(WhereOperator.NotEquals, whereParams, tokens))
                return;

            var whereToken = WhereToken.Create(WhereOperator.NotEquals, whereParams.FieldName, AddQueryParameter(transformToEqualValue),
                new WhereToken.WhereOptions(whereParams.Exact));
            tokens.AddLast(whereToken);
        }

        ///<summary>
        /// Negate the next operation
        ///</summary>
        public void NegateNext()
        {
            Negate = !Negate;
        }

        /// <summary>
        /// Check that the field has one of the specified value
        /// </summary>
        public void WhereIn(string fieldName, IEnumerable<object> values, bool exact = false)
        {
            AssertMethodIsCurrentlySupported(nameof(WhereIn));

            fieldName = EnsureValidFieldName(fieldName, isNestedPath: false);

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);
            NegateIfNeeded(tokens, fieldName);

            var whereToken = WhereToken.Create(WhereOperator.In, fieldName, AddQueryParameter(TransformEnumerable(fieldName, UnpackEnumerable(values)).ToArray()),
                new WhereToken.WhereOptions(exact));
            tokens.AddLast(whereToken);
        }

        /// <inheritdoc />
        public void WhereStartsWith(string fieldName, object value)
        {
            WhereStartsWith(fieldName, value, exact: false);
        }

        /// <inheritdoc />
        public void WhereStartsWith(string fieldName, object value, bool exact)
        {
            AssertMethodIsCurrentlySupported(nameof(WhereStartsWith));

            var whereParams = new WhereParams
            {
                FieldName = fieldName,
                Value = value,
                AllowWildcards = true
            };

            var transformToEqualValue = TransformValue(whereParams);

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);

            whereParams.FieldName = EnsureValidFieldName(whereParams.FieldName, whereParams.IsNestedPath);
            NegateIfNeeded(tokens, whereParams.FieldName);
            var whereToken = WhereToken.Create(WhereOperator.StartsWith, whereParams.FieldName, AddQueryParameter(transformToEqualValue), new WhereToken.WhereOptions(exact));
            tokens.AddLast(whereToken);
        }

        /// <inheritdoc />
        public void WhereEndsWith(string fieldName, object value)
        {
            WhereEndsWith(fieldName, value, exact: false);
        }

        /// <inheritdoc />
        public void WhereEndsWith(string fieldName, object value, bool exact)
        {
            AssertMethodIsCurrentlySupported(nameof(WhereEndsWith));

            var whereParams = new WhereParams
            {
                FieldName = fieldName,
                Value = value,
                AllowWildcards = true
            };

            var transformToEqualValue = TransformValue(whereParams);

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);

            whereParams.FieldName = EnsureValidFieldName(whereParams.FieldName, whereParams.IsNestedPath);
            NegateIfNeeded(tokens, whereParams.FieldName);
            var whereToken = WhereToken.Create(WhereOperator.EndsWith, whereParams.FieldName, AddQueryParameter(transformToEqualValue), new WhereToken.WhereOptions(exact));
            tokens.AddLast(whereToken);
        }

        /// <summary>
        ///   Matches fields where the value is between the specified start and end, inclusive
        /// </summary>
        /// <param name = "fieldName">Name of the field.</param>
        /// <param name = "start">The start.</param>
        /// <param name = "end">The end.</param>
        /// <returns></returns>
        public void WhereBetween(string fieldName, object start, object end, bool exact = false)
        {
            AssertMethodIsCurrentlySupported(nameof(WhereBetween));

            fieldName = EnsureValidFieldName(fieldName, isNestedPath: false);

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);
            NegateIfNeeded(tokens, fieldName);

            var fromParameterName = AddQueryParameter(start == null ? 
                Constants.Documents.Querying.Terms.LeftNullValueOfBetweenQuery 
                : TransformValue(new WhereParams { Value = start, FieldName = fieldName }, forRange: true));
            
            var toParameterName = AddQueryParameter(end == null 
                ? Constants.Documents.Querying.Terms.RightNullValueOfBetweenQuery 
                : TransformValue(new WhereParams { Value = end, FieldName = fieldName }, forRange: true));

            var whereToken = WhereToken.Create(WhereOperator.Between, fieldName, null, new WhereToken.WhereOptions(exact, fromParameterName, toParameterName));
            tokens.AddLast(whereToken);
        }

        /// <summary>
        ///   Matches fields where the value is greater than the specified value
        /// </summary>
        /// <param name = "fieldName">Name of the field.</param>
        /// <param name = "value">The value.</param>
        public void WhereGreaterThan(string fieldName, object value, bool exact = false)
        {
            fieldName = EnsureValidFieldName(fieldName, isNestedPath: false);

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);
            NegateIfNeeded(tokens, fieldName);

            if (IfValueIsMethod(WhereOperator.GreaterThan, new WhereParams { FieldName = fieldName, Value = value, Exact = exact }, tokens))
                return;

            var parameter = AddQueryParameter(value == null
                ? "*"
                : TransformValue(new WhereParams
                {
                    Value = value,
                    FieldName = fieldName
                }, forRange: true));
            var whereToken = WhereToken.Create(WhereOperator.GreaterThan, fieldName, parameter, new WhereToken.WhereOptions(exact));
            tokens.AddLast(whereToken);
        }

        /// <summary>
        ///   Matches fields where the value is greater than or equal to the specified value
        /// </summary>
        /// <param name = "fieldName">Name of the field.</param>
        /// <param name = "value">The value.</param>
        public void WhereGreaterThanOrEqual(string fieldName, object value, bool exact = false)
        {
            fieldName = EnsureValidFieldName(fieldName, isNestedPath: false);

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);
            NegateIfNeeded(tokens, fieldName);

            if (IfValueIsMethod(WhereOperator.GreaterThanOrEqual, new WhereParams { FieldName = fieldName, Value = value, Exact = exact }, tokens))
                return;

            var parameter = AddQueryParameter(value == null
                ? "*"
                : TransformValue(new WhereParams
                {
                    Value = value,
                    FieldName = fieldName
                }, forRange: true));
            var whereToken = WhereToken.Create(WhereOperator.GreaterThanOrEqual, fieldName, parameter, new WhereToken.WhereOptions(exact));
            tokens.AddLast(whereToken);
        }

        /// <summary>
        ///   Matches fields where the value is less than the specified value
        /// </summary>
        /// <param name = "fieldName">Name of the field.</param>
        /// <param name = "value">The value.</param>
        public void WhereLessThan(string fieldName, object value, bool exact = false)
        {
            fieldName = EnsureValidFieldName(fieldName, isNestedPath: false);

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);
            NegateIfNeeded(tokens, fieldName);

            if (IfValueIsMethod(WhereOperator.LessThan, new WhereParams { FieldName = fieldName, Value = value, Exact = exact }, tokens))
                return;

            var parameter = AddQueryParameter(value == null
                ? "NULL"
                : TransformValue(new WhereParams
                {
                    Value = value,
                    FieldName = fieldName
                }, forRange: true));
            var whereToken = WhereToken.Create(WhereOperator.LessThan, fieldName, parameter, new WhereToken.WhereOptions(exact));
            tokens.AddLast(whereToken);
        }

        /// <summary>
        ///   Matches fields where the value is less than or equal to the specified value
        /// </summary>
        /// <param name = "fieldName">Name of the field.</param>
        /// <param name = "value">The value.</param>
        public void WhereLessThanOrEqual(string fieldName, object value, bool exact = false)
        {
            fieldName = EnsureValidFieldName(fieldName, isNestedPath: false);

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);
            NegateIfNeeded(tokens, fieldName);

            if (IfValueIsMethod(WhereOperator.LessThanOrEqual, new WhereParams { FieldName = fieldName, Value = value, Exact = exact }, tokens))
                return;

            var parameter = AddQueryParameter(value == null
                ? "NULL"
                : TransformValue(new WhereParams
                {
                    Value = value,
                    FieldName = fieldName
                }, forRange: true));
            var whereToken = WhereToken.Create(WhereOperator.LessThanOrEqual, fieldName, parameter, new WhereToken.WhereOptions(exact));
            tokens.AddLast(whereToken);
        }

        /// <summary>
        ///   Matches fields where Regex.IsMatch(filedName, pattern)
        /// </summary>
        /// <param name = "fieldName">Name of the field.</param>
        /// <param name="pattern"> The pattern to match</param>
        public void WhereRegex(string fieldName, string pattern)
        {
            AssertMethodIsCurrentlySupported(nameof(WhereRegex));

            fieldName = EnsureValidFieldName(fieldName, isNestedPath: false);

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);
            NegateIfNeeded(tokens, fieldName);
            var parameter = AddQueryParameter(TransformValue(new WhereParams
            {
                Value = pattern,
                FieldName = fieldName
            }));
            var whereToken = WhereToken.Create(WhereOperator.Regex, fieldName, parameter);
            tokens.AddLast(whereToken);
        }

        /// <summary>
        ///   Add an AND to the query
        /// </summary>
        public void AndAlso()
        {
            AndAlso(wrapPreviousQueryClauses: false);
        }

        /// <summary>
        ///   Wraps previous query with clauses and adds an AND operator to the given query
        /// </summary>
        public void AndAlso(bool wrapPreviousQueryClauses)
        {
            var tokens = GetCurrentWhereTokens();

            if (tokens.Last == null)
                return;

            if (tokens.Last.Value is QueryOperatorToken)
                throw new InvalidOperationException("Cannot add AND, previous token was already an operator token.");

            if (wrapPreviousQueryClauses == false)
            {
                tokens.AddLast(QueryOperatorToken.And);
            }
            else
            {
                tokens.AddFirst(OpenSubclauseToken.Create());
                tokens.AddLast(CloseSubclauseToken.Create());
                tokens.AddLast(QueryOperatorToken.And);
            }
        }



        /// <summary>
        ///   Add an OR to the query
        /// </summary>
        public void OrElse()
        {
            var tokens = GetCurrentWhereTokens();

            if (tokens.Last == null)
                return;

            if (tokens.Last.Value is QueryOperatorToken)
                throw new InvalidOperationException("Cannot add OR, previous token was already an operator token.");

            tokens.AddLast(QueryOperatorToken.Or);
        }

        internal IDisposable SetFilterMode(bool @on)
        {
            return new FilterModeScope(FilterModeStack, @on);
        }

        private sealed class FilterModeScope : IDisposable
        {
            private Stack<bool> _modeStack;
            public FilterModeScope(Stack<bool> modeStack, bool @on)
            {
                _modeStack = modeStack;
                _modeStack.Push(@on);
            }

            public void Dispose()
            {
                _modeStack.Pop();
            }
        }
        
        internal IDisposable SetAnyMode()
        {
            return new AnyModeScope(this);
        }

        private class AnyModeScope : IDisposable
        {
            private readonly AbstractDocumentQuery<T, TSelf> _documentQuery;
            private LinkedListNode<QueryToken> _lastNodeBeforeClauseStart;
            
            public AnyModeScope(AbstractDocumentQuery<T, TSelf> documentQuery)
            {
                _documentQuery = documentQuery;
                _lastNodeBeforeClauseStart = _documentQuery.GetCurrentWhereTokens().Last;
            }
            
            public void Dispose()
            {
                if (_documentQuery.IndexName != null || _documentQuery.IsDynamicMapReduce)
                    return;
                
                _lastNodeBeforeClauseStart ??= _documentQuery.GetCurrentWhereTokens().First;

                if (_lastNodeBeforeClauseStart == null)
                    return;
                
                for (var node = _lastNodeBeforeClauseStart.Next; node != null ; node = node.Next)
                {
                    if (node.Value is QueryOperatorToken queryOperatorToken && queryOperatorToken == QueryOperatorToken.And)
                    {
                        if (node.Previous?.Value is WhereToken left && node.Next?.Value is WhereToken right && left.FieldName != right.FieldName)
                        {
                            if (UsesKeyAndValue(left, right))
                                continue;
                            
                            throw new InvalidOperationException($"Using multiple fields inside method '{nameof(Enumerable.Any)}' can lead to unexpected query results for auto index. " +
                                                                $"A static fanout index should be used instead.");
                        }
                    }
                }

                bool UsesKeyAndValue(WhereToken left, WhereToken right)
                {
                    if (left.FieldName.EndsWith("Value") && right.FieldName.EndsWith("Key"))
                        return true;
                    
                    if (left.FieldName.EndsWith("Key") && right.FieldName.EndsWith("Value"))
                        return true;
                    
                    return false;
                }
            }
        }
    
        
        /// <summary>
        ///   Specifies a boost weight to the previous where clause.
        ///   The higher the boost factor, the more relevant the term will be.
        /// </summary>
        /// <param name = "boost">boosting factor where 1.0 is default, less than 1.0 is lower weight, greater than 1.0 is higher weight</param>
        /// <returns></returns>
        /// <remarks>
        ///   http://lucene.apache.org/java/2_4_0/queryparsersyntax.html#Boosting%20a%20Term
        /// </remarks>
        public void Boost(decimal boost)
        {
            AssertMethodIsCurrentlySupported(nameof(Boost));

            if (boost == 1m) // 1.0 is the default
                return;

            if (boost < 0m)
                throw new ArgumentOutOfRangeException(nameof(boost), "Boost factor must be a non-negative number");

            var tokens = GetCurrentWhereTokens();
            var last = tokens.Last;
            switch (last?.Value)
            {
                case WhereToken whereToken:
                    whereToken.Options.Boost = boost;
                    return;

                case CloseSubclauseToken close:
                    string parameter = AddQueryParameter(boost);
                    int openSubclauseToSkip = 0;
                    while (last != null)
                    {
                        last = last.Previous; // find the previous option
                        
                        switch (last?.Value)
                        {
                            case CloseSubclauseToken prevClose:
                            {
                                // We have to count how many inner subclauses were inside current subclause
                                openSubclauseToSkip++;
                                continue;
                            }
                            case OpenSubclauseToken open when openSubclauseToSkip > 0:
                                // Inner subclause open - we have to skip it because we want to match only the leftmost opening.
                                openSubclauseToSkip--;
                                continue;
                            case OpenSubclauseToken open:
                                open.BoostParameterName = parameter;
                                close.BoostParameterName = parameter;
                                return;
                        }
                    }
                    break;

                default:
                    throw new InvalidOperationException("Cannot apply boost on: " + (last?.Value.ToString() ?? "null"));
            }
        }

        /// <summary>
        ///   Specifies a fuzziness factor to the single word term in the last where clause
        /// </summary>
        /// <param name = "fuzzy">0.0 to 1.0 where 1.0 means closer match</param>
        /// <returns></returns>
        /// <remarks>
        ///   http://lucene.apache.org/java/2_4_0/queryparsersyntax.html#Fuzzy%20Searches
        /// </remarks>
        public void Fuzzy(decimal fuzzy)
        {
            AssertMethodIsCurrentlySupported(nameof(Fuzzy));

            var tokens = GetCurrentWhereTokens();
            var whereToken = tokens.Last?.Value as WhereToken;
            if (whereToken == null)
                throw new InvalidOperationException("Fuzzy can only be used right after Where clause");

            if (whereToken.WhereOperator != WhereOperator.Equals)
                throw new InvalidOperationException("Fuzzy can only be used right after Where clause with equals operator");

            if (fuzzy < 0m || fuzzy > 1m)
                throw new ArgumentOutOfRangeException(nameof(fuzzy), "Fuzzy distance must be between 0.0 and 1.0");

            whereToken.Options.Fuzzy = fuzzy;
        }

        /// <summary>
        ///   Specifies a proximity distance for the phrase in the last search clause
        /// </summary>
        /// <param name = "proximity">Number of terms between the search terms</param>
        /// <returns></returns>
        /// <remarks>
        ///   https://lucene.apache.org/core/2_9_4/queryparsersyntax.html#Proximity%20Searches
        /// </remarks>
        public void Proximity(int proximity)
        {
            AssertMethodIsCurrentlySupported(nameof(Proximity));

            var tokens = GetCurrentWhereTokens();
            var whereToken = tokens.Last?.Value as WhereToken;
            if (whereToken == null || whereToken.WhereOperator != WhereOperator.Search)
                throw new InvalidOperationException("Proximity can only be used right after Search clause");

            if (proximity < 0)
                throw new ArgumentOutOfRangeException(nameof(proximity), "Proximity distance must be a number greater than or equal to 0");

            whereToken.Options.Proximity = proximity;
        }

        public void OrderBy(string field, string sorterName)
        {
            if (string.IsNullOrWhiteSpace(sorterName))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(sorterName));

            AssertNoRawQuery();
            var f = EnsureValidFieldName(field, isNestedPath: false);
            OrderByTokens.AddLast(OrderByToken.CreateAscending(f, sorterName));
        }

        /// <summary>
        ///   Order the results by the specified fields
        ///   The fields are the names of the fields to sort, defaulting to sorting by ascending.
        ///   You can prefix a field name with '-' to indicate sorting by descending or '+' to sort by ascending
        /// </summary>
        public void OrderBy(string field, OrderingType ordering = OrderingType.String)
        {
            OrderBy(field, NullsOrdering.Default, ordering);
        }

        public void OrderBy(string field, NullsOrdering nulls, OrderingType ordering = OrderingType.String)
        {
            AssertNoRawQuery();
            var f = EnsureValidFieldName(field, isNestedPath: false);
            OrderByTokens.AddLast(OrderByToken.CreateAscending(f, ordering, nulls));
        }

        public void OrderByDescending(string field, string sorterName)
        {
            if (string.IsNullOrWhiteSpace(sorterName))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(sorterName));

            AssertNoRawQuery();
            var f = EnsureValidFieldName(field, isNestedPath: false);
            OrderByTokens.AddLast(OrderByToken.CreateDescending(f, sorterName));
        }

        /// <summary>
        ///   Order the results by the specified fields
        ///   The fields are the names of the fields to sort, defaulting to sorting by descending.
        ///   You can prefix a field name with '-' to indicate sorting by descending or '+' to sort by ascending
        /// </summary>
        /// <param name = "fields">The fields.</param>
        public void OrderByDescending(string field, OrderingType ordering = OrderingType.String)
        {
            OrderByDescending(field, NullsOrdering.Default, ordering);
        }

        public void OrderByDescending(string field, NullsOrdering nulls, OrderingType ordering = OrderingType.String)
        {
            AssertNoRawQuery();
            var f = EnsureValidFieldName(field, isNestedPath: false);
            OrderByTokens.AddLast(OrderByToken.CreateDescending(f, ordering, nulls));
        }

        public void OrderByScore()
        {
            AssertNoRawQuery();
            OrderByTokens.AddLast(OrderByToken.ScoreAscending);
        }

        public void OrderByScoreDescending()
        {
            AssertNoRawQuery();
            OrderByTokens.AddLast(OrderByToken.ScoreDescending);
        }

        /// <summary>
        /// Provide statistics about the query, such as total count of matching records
        /// </summary>
        public void Statistics(out QueryStatistics stats)
        {
            stats = QueryStats;
            stats.RequestedByUser = true;
        }

        /// <summary>
        /// Called externally to raise the after query executed callback
        /// </summary>
        public void InvokeAfterQueryExecuted(QueryResult result)
        {
            AfterQueryExecutedCallback?.Invoke(result);
        }

        /// <summary>
        /// Called externally to raise the after stream executed callback
        /// </summary>
        public void InvokeAfterStreamExecuted(BlittableJsonReaderObject result)
        {
            AfterStreamExecutedCallback?.Invoke(result);
        }

        #endregion TSelf Members

        /// <summary>
        ///   Generates the index query.
        /// </summary>
        /// <param name = "query">The query.</param>
        /// <returns></returns>
        protected IndexQuery GenerateIndexQuery(string query)
        {
            var indexQuery = new IndexQuery
            {
                Query = query,
                WaitForNonStaleResults = TheWaitForNonStaleResults,
                WaitForNonStaleResultsTimeout = Timeout,
                QueryParameters = QueryParameters,
                DisableCaching = DisableCaching,
                Tag = QueryTag,
                ProjectionBehavior = ProjectionBehavior,
                SkipStatistics = QueryStats.RequestedByUser == false
            };

            return indexQuery;
        }

        /// <summary>
        /// Perform a search for documents which fields that match the searchTerms.
        /// If there is more than a single term, each of them will be checked independently.
        /// </summary>
        public void Search(string fieldName, string searchTerms, SearchOperator @operator = SearchOperator.Or)
        {
            AssertMethodIsCurrentlySupported(nameof(Search));

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);

            fieldName = EnsureValidFieldName(fieldName, isNestedPath: false);
            NegateIfNeeded(tokens, fieldName);
            var whereToken = WhereToken.Create(WhereOperator.Search, fieldName, AddQueryParameter(searchTerms), new WhereToken.WhereOptions(@operator));
            tokens.AddLast(whereToken);
        }

        private const string JoinOuterAlias = "__alias_join";

        private string ToString(bool compatibilityMode)
        {
            // a join needs an alias for the main document: the query is rendered with one, the tokens and the fields of the query stay as they are
            if (Joins != null && QueryRaw == null && FromToken.Alias == null)
                return Render(compatibilityMode, JoinOuterAlias, AddAliasToTokens(WhereTokens), AddAliasToTokens(OrderByTokens), AddAliasToTokens(FilterTokens));

            return Render(compatibilityMode, FromToken?.Alias, WhereTokens, OrderByTokens, FilterTokens);
        }

        private string Render(bool compatibilityMode, string outerAlias, LinkedList<QueryToken> whereTokens, LinkedList<QueryToken> orderByTokens, LinkedList<QueryToken> filterTokens)
        {
            if (QueryRaw != null)
            {
                if (compatibilityMode)
                    return QueryRaw;

                var rawQueryText = new StringBuilder(QueryRaw);
                BuildPagination(rawQueryText);

                return rawQueryText.ToString();
            }

            if (_currentClauseDepth != 0)
                throw new InvalidOperationException(string.Format("A clause was not closed correctly within this query, current clause depth = {0}", _currentClauseDepth));

            var queryText = new StringBuilder();

            BuildDeclare(queryText);
            BuildFrom(queryText, outerAlias);
            BuildJoin(queryText, outerAlias);
            BuildGroupBy(queryText);
            BuildWhere(queryText, whereTokens);
            BuildOrderBy(queryText, orderByTokens);
            BuildLoad(queryText);
            BuildFilter(queryText, filterTokens);
            BuildSelect(queryText);
            BuildInclude(queryText);

            if (compatibilityMode == false)
                BuildPagination(queryText);

            return queryText.ToString();
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return ToString(compatibilityMode: false);
        }

        private void BuildPagination(StringBuilder queryText)
        {
            if (Start > 0 || PageSize.HasValue)
            {
                queryText
                    .Append(" limit $")
                    .Append(AddQueryParameter(Start))
                    .Append(", $")
                    .Append(AddQueryParameter(PageSize));
            }

            if (FilterTokens.Count > 0 && FilterLimit is not null)
            {
                queryText
                    .Append(" filter_limit $")
                    .Append(AddQueryParameter(FilterLimit));
            }
        }

        private void BuildInclude(StringBuilder queryText)
        {
            if (DocumentIncludes.Count == 0 &&
                HighlightingTokens.Count == 0 &&
                ExplanationToken == null &&
                QueryTimings.ShouldBeIncluded == false &&
                CounterIncludesTokens == null &&
                RevisionsIncludesTokens == null &&
                TimeSeriesIncludesTokens == null &&
                CompareExchangeValueIncludesTokens == null)
                return;

            queryText.Append(" include ");
            var first = true;
            foreach (var include in DocumentIncludes)
            {
                if (first == false)
                    queryText.Append(",");
                first = false;

                if (IncludesUtil.RequiresQuotes(include, out var escapedInclude))
                    queryText.Append("'").Append(escapedInclude).Append("'");
                else
                    QueryToken.WriteField(queryText, include);
            }

            WriteIncludeTokens(CounterIncludesTokens);
            WriteIncludeTokens(TimeSeriesIncludesTokens);
            WriteIncludeTokens(RevisionsIncludesTokens);
            WriteIncludeTokens(CompareExchangeValueIncludesTokens);
            WriteIncludeTokens(HighlightingTokens);

            if (ExplanationToken != null)
            {
                if (first == false)
                    queryText.Append(",");
                first = false;

                ExplanationToken.WriteTo(queryText);
            }

            if (QueryTimings.ShouldBeIncluded)
            {
                if (first == false)
                    queryText.Append(",");
                first = false;

                TimingsToken.Instance.WriteTo(queryText);
            }

            void WriteIncludeTokens<TToken>(IEnumerable<TToken> tokens) where TToken : QueryToken
            {
                if (tokens == null)
                    return;

                foreach (var token in tokens)
                {
                    if (first == false)
                        queryText.Append(",");
                    first = false;

                    token.WriteTo(queryText);
                }
            }
        }

        public void Intersect()
        {
            var tokens = GetCurrentWhereTokens();
            var last = tokens.Last?.Value;
            if (last is WhereToken || last is CloseSubclauseToken)
            {
                IsIntersect = true;

                tokens.AddLast(IntersectMarkerToken.Instance);
            }
            else
                throw new InvalidOperationException("Cannot add INTERSECT at this point.");
        }

        public void WhereExists(string fieldName)
        {
            fieldName = EnsureValidFieldName(fieldName, isNestedPath: false);

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);
            NegateIfNeeded(tokens, fieldName: null); // RavenDB-14088

            tokens.AddLast(WhereToken.Create(WhereOperator.Exists, fieldName, null));
        }

        public void ContainsAny(string fieldName, IEnumerable<object> values)
        {
            AssertMethodIsCurrentlySupported(nameof(ContainsAny));

            fieldName = EnsureValidFieldName(fieldName, isNestedPath: false);

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);
            NegateIfNeeded(tokens, fieldName);

            var array = TransformEnumerable(fieldName, UnpackEnumerable(values))
                .ToArray();

            var whereToken = WhereToken.Create(WhereOperator.In, fieldName, AddQueryParameter(array), new WhereToken.WhereOptions(false));

            tokens.AddLast(whereToken);
        }

        public void ContainsAll(string fieldName, IEnumerable<object> values)
        {
            AssertMethodIsCurrentlySupported(nameof(ContainsAll));

            fieldName = EnsureValidFieldName(fieldName, isNestedPath: false);

            var tokens = GetCurrentWhereTokens();
            AppendOperatorIfNeeded(tokens);
            NegateIfNeeded(tokens, fieldName);

            var array = TransformEnumerable(fieldName, UnpackEnumerable(values))
                .ToArray();

            if (array.Length == 0)
            {
                tokens.AddLast(TrueToken.Instance);
                return;
            }

            var whereToken = WhereToken.Create(WhereOperator.AllIn, fieldName, AddQueryParameter(array));

            tokens.AddLast(whereToken);
        }

        public void AddRootType(Type type)
        {
            RootTypes.Add(type);
        }

        public string GetMemberQueryPathForOrderBy(Expression expression)
        {
            var memberQueryPath = GetMemberQueryPath(expression);
            return memberQueryPath;
        }

        public string GetMemberQueryPath(Expression expression)
        {
            var result = _linqPathProvider.GetPath(expression);
            result.Path = result.Path.Substring(result.Path.IndexOf('.') + 1);

            if (expression.NodeType == ExpressionType.ArrayLength)
                result.Path += ".Length";

            var propertyName = IndexName == null || FromToken.IsDynamic
                ? _conventions.FindPropertyNameForDynamicIndex(typeof(T), IndexName, "", result.Path)
                : _conventions.FindPropertyNameForIndex(typeof(T), IndexName, "", result.Path);
            return propertyName;
        }
        
        
        public void VectorSearch(IVectorEmbeddingFieldFactoryAccessor fieldFactoryAccessor, IVectorFieldValueFactoryAccessor fieldValueFactoryAccessor, float? minimumSimilarity, int? numberOfCandidates, bool isExact)
        {
            var fieldName = fieldFactoryAccessor.FieldName;
            var sourceQuantizationType = fieldFactoryAccessor.SourceQuantizationType;
            var targetQuantizationType = fieldFactoryAccessor.DestinationQuantizationType;
            var embeddingsGenerationTaskIdentifier = fieldFactoryAccessor.EmbeddingsGenerationTaskIdentifier;
            
            var text = fieldValueFactoryAccessor.Text;
            var texts = fieldValueFactoryAccessor.Texts;
            var embeddings = fieldValueFactoryAccessor.Embeddings;
            var byId = fieldValueFactoryAccessor.ById;
            var embeddingsGenerationTaskIdentifierFromValue = fieldValueFactoryAccessor.EmbeddingsGenerationTaskIdentifier;

            VectorSearch(fieldName, sourceQuantizationType, targetQuantizationType, minimumSimilarity, numberOfCandidates, isExact, text, texts,  embeddings, byId, embeddingsGenerationTaskIdentifier, embeddingsGenerationTaskIdentifierFromValue);
        }
        
        internal void VectorSearch(VectorEmbeddingFieldFactory<T> embeddingFieldFactory, VectorFieldValueFactory embeddingValueFactory,
            float? minimumSimilarity, int? numberOfCandidates, bool isExact)
        {
            var fieldName = embeddingFieldFactory.FieldName;
            var sourceQuantizationType = embeddingFieldFactory.SourceQuantizationType;
            var targetQuantizationType = embeddingFieldFactory.DestinationQuantizationType;
            var embeddingsGenerationTaskIdentifier = embeddingFieldFactory.EmbeddingsGenerationTaskIdentifier;
            
            var text = embeddingValueFactory.Text;
            var texts = embeddingValueFactory.Texts;
            var embeddings = embeddingValueFactory.Embeddings;
            var byId = embeddingValueFactory.ById;
            var embeddingsGenerationTaskIdentifierFromValue = embeddingValueFactory.EmbeddingsGenerationTaskIdentifier;

            VectorSearch(fieldName, sourceQuantizationType, targetQuantizationType, minimumSimilarity, numberOfCandidates, isExact, text, texts, embeddings, byId, embeddingsGenerationTaskIdentifier, embeddingsGenerationTaskIdentifierFromValue);
        }
        
        private void VectorSearch(string fieldName, VectorEmbeddingType sourceQuantizationType, VectorEmbeddingType targetQuantizationType, float? minimumSimilarity,
            int? numberOfCandidates, bool isExact, string text, IEnumerable<string> texts, object embeddings, string byId, string embeddingsGenerationTaskIdentifier = null, string embeddingsGenerationTaskIdentifierValue = null)
        {
            string queryParameterName;

            bool isDocumentId = string.IsNullOrEmpty(byId) is false;
            if (text != null)
                queryParameterName = AddQueryParameter(text);
            else if (texts != null)
            {
                queryParameterName = AddQueryParameter(texts);
            }
            else if (embeddings != null)
            {
                // for well-known types we can convert the array into Base64
                queryParameterName = AddQueryParameter(embeddings switch
                {
                    float[] fa => Convert.ToBase64String(MemoryMarshal.Cast<float, byte>(fa)
#if !NETCOREAPP3_1_OR_GREATER
                        .ToArray() // For newer frameworks we use Span overload in Convert.
#endif
                    ),
                    byte[] ba => Convert.ToBase64String(ba),
                    sbyte[] sb => Convert.ToBase64String(MemoryMarshal.Cast<sbyte, byte>(sb)
#if !NETCOREAPP3_1_OR_GREATER
                        .ToArray()
#endif
                    ),
                    _  => embeddings
                });
            }
            else if (isDocumentId)
            {
                queryParameterName = AddQueryParameter(byId);
            }
            else
            {
                throw new InvalidOperationException("Cannot use VectorSearch without text(s), document id or embedding(s).");
            }
            
            var vectorSearchToken = new VectorSearchToken(fieldName, queryParameterName, sourceQuantizationType, targetQuantizationType, minimumSimilarity, numberOfCandidates, isExact, isDocumentId, embeddingsGenerationTaskIdentifier, embeddingsGenerationTaskIdentifierValue);

            WhereTokens.AddLast(vectorSearchToken);
        }

        public void VectorSearch(IVectorFieldFactory<T> fieldFactory, IVectorFieldValueFactory valueFactory, float? minimumSimilarity, int? numberOfCandidates, bool isExact)
        {
            VectorSearch((VectorEmbeddingFieldFactory<T>)fieldFactory, (VectorFieldValueFactory)valueFactory, minimumSimilarity, numberOfCandidates, isExact);
        }

        public void Distinct()
        {
            if (IsDistinct)
                throw new InvalidOperationException("This is already a distinct query.");

            SelectTokens.AddFirst(DistinctToken.Instance);
        }

        private void UpdateStatsHighlightingsAndExplanations(QueryResult queryResult)
        {
            QueryStats.UpdateQueryStats(queryResult);
            QueryHighlightings.Update(queryResult);
            if (Explanations.ShouldBeIncluded)
                Explanations.Update(queryResult);
            if (QueryTimings.ShouldBeIncluded)
                QueryTimings.Update(queryResult);
        }

        private void BuildSelect(StringBuilder writer)
        {
            if (SelectTokens.Count == 0)
                return;

            writer
                .Append(" select ");

            var token = SelectTokens.First;
            if (SelectTokens.Count == 1 && token.Value is DistinctToken)
            {
                token.Value.WriteTo(writer);
                writer.Append(" *");

                return;
            }

            while (token != null)
            {
                if (token.Previous != null && token.Previous.Value is DistinctToken == false)
                    writer.Append(",");

                DocumentQueryHelper.AddSpaceIfNeeded(token.Previous?.Value, token.Value, writer);

                token.Value.WriteTo(writer);

                token = token.Next;
            }
        }

        private void BuildFrom(StringBuilder writer, string outerAlias)
        {
            FromToken.WriteTo(writer, outerAlias);
        }

        private void BuildDeclare(StringBuilder writer)
        {
            if (DeclareTokens == null)
                return;

            foreach (var token in DeclareTokens)
            {
                token.WriteTo(writer);
            }
        }

        private void BuildLoad(StringBuilder writer)
        {
            if (LoadTokens == null || LoadTokens.Count == 0)
                return;

            writer.Append(" load ");

            for (int i = 0; i < LoadTokens.Count; i++)
            {
                if (i != 0)
                    writer.Append(", ");
                LoadTokens[i].WriteTo(writer);
            }
        }

        private void BuildWhere(StringBuilder writer, LinkedList<QueryToken> whereTokens)
        {
            if (whereTokens.Count == 0)
                return;

            writer
                .Append(" where ");

            if (IsIntersect)
                writer.Append("intersect(");

            var token = whereTokens.First;
            while (token != null)
            {
                DocumentQueryHelper.AddSpaceIfNeeded(token.Previous?.Value, token.Value, writer);

                token.Value.WriteTo(writer);

                token = token.Next;
            }

            if (IsIntersect)
                writer.Append(") ");
        }

        private void BuildGroupBy(StringBuilder writer)
        {
            if (GroupByTokens.Count == 0)
                return;

            writer
                .Append(" group by ");

            var token = GroupByTokens.First;
            while (token != null)
            {
                if (token.Previous != null)
                    writer.Append(", ");

                token.Value.WriteTo(writer);

                token = token.Next;
            }
        }

        private void BuildFilter(StringBuilder writer, LinkedList<QueryToken> filterTokens)
        {
            if (filterTokens.Count == 0)
                return;

            writer
                .Append(" filter ");

            var token = filterTokens.First;
            while (token != null)
            {
                DocumentQueryHelper.AddSpaceIfNeeded(token.Previous?.Value, token.Value, writer);

                token.Value.WriteTo(writer);

                token = token.Next;
            }
        }

        private void BuildOrderBy(StringBuilder writer, LinkedList<QueryToken> orderByTokens)
        {
            if (orderByTokens.Count == 0)
                return;

            writer
                .Append(" order by ");

            var token = orderByTokens.First;
            while (token != null)
            {
                if (token.Previous != null)
                    writer.Append(", ");

                token.Value.WriteTo(writer);

                token = token.Next;
            }
        }

        private void AppendOperatorIfNeeded(LinkedList<QueryToken> tokens)
        {
            AssertNoRawQuery();

            if (tokens.Count == 0)
                return;

            var lastToken = tokens.Last.Value;

            if (lastToken is TrueToken)
            {
                tokens.AddLast(GetDefaultOperatorToken());
                return;
            }

            if (lastToken is WhereToken == false && lastToken is CloseSubclauseToken == false)
                return;

            WhereToken lastWhere = null;

            var current = tokens.Last;
            while (current != null)
            {
                lastWhere = current.Value as WhereToken;

                if (lastWhere != null)
                    break;

                current = current.Previous;
            }

            var token = GetDefaultOperatorToken();

            if (lastWhere.Options?.SearchOperator != null)
                token = QueryOperatorToken.Or; // default to OR operator after search if AND was not specified explicitly

            tokens.AddLast(token);
            return;

            QueryOperatorToken GetDefaultOperatorToken()
            {
                return DefaultOperator == QueryOperator.And ? QueryOperatorToken.And : QueryOperatorToken.Or;
            }
        }

        private IEnumerable<object> TransformEnumerable(string fieldName, IEnumerable<object> values)
        {
            foreach (var value in values)
            {
                var enumerable = value as IEnumerable;
                if (enumerable != null && value is string == false)
                {
                    foreach (var transformedValue in TransformEnumerable(fieldName, enumerable.Cast<object>()))
                        yield return transformedValue;

                    continue;
                }

                var nestedWhereParams = new WhereParams
                {
                    AllowWildcards = true,
                    FieldName = fieldName,
                    Value = value
                };

                yield return TransformValue(nestedWhereParams);
            }
        }

        private void NegateIfNeeded(LinkedList<QueryToken> tokens, string fieldName)
        {
            if (Negate == false)
                return;

            Negate = false;

            if (tokens.Count == 0 || tokens.Last.Value is OpenSubclauseToken)
            {
                if (fieldName != null)
                    WhereExists(fieldName);
                else
                    WhereTrue();

                AndAlso();
            }

            tokens.AddLast(NegateToken.Instance);
        }

        private static IEnumerable<object> UnpackEnumerable(IEnumerable items)
        {
            foreach (var item in items)
            {
                var enumerable = item as IEnumerable;
                if (enumerable != null && item is string == false)
                {
                    foreach (var nested in UnpackEnumerable(enumerable))
                    {
                        yield return nested;
                    }
                }
                else
                {
                    yield return item;
                }
            }
        }

        private string EnsureValidFieldName(string fieldName, bool isNestedPath)
        {
            if (TheSession?.Conventions == null || isNestedPath || IsGroupBy)
                return QueryFieldUtil.EscapeIfNecessary(fieldName, isNestedPath);

            foreach (var rootType in RootTypes)
            {
                var identityProperty = TheSession.Conventions.GetIdentityProperty(rootType);
                var identityConverted = TheSession.Conventions.GetConvertedPropertyNameFor(identityProperty);

                if (identityProperty != null && identityConverted == fieldName)
                {
                    return Constants.Documents.Indexing.Fields.DocumentIdFieldName;
                }
            }

            return QueryFieldUtil.EscapeIfNecessary(fieldName);
        }

        private static Func<object, string> GetImplicitStringConversion(Type type)
        {
            if (type == null)
                return null;

            var localStringsCache = _implicitStringsCache;
            if (localStringsCache.TryGet(type, out Func<object, string> value))
                return value;

            var methodInfo = type.GetMethod("op_Implicit", new[] { type });

            if (methodInfo == null || methodInfo.ReturnType != typeof(string))
            {
                localStringsCache.Put(type, null);
                return null;
            }

            var arg = Expression.Parameter(typeof(object), "self");
            var func = (Func<object, string>)Expression.Lambda(Expression.Call(methodInfo, Expression.Convert(arg, type)), arg).Compile();

            localStringsCache.Put(type, func);
            return func;
        }

        private object TransformValue(WhereParams whereParams, bool forRange = false)
        {
            var actualValue = whereParams.Value;

            // These 2 are shortcuts for common case of it is null or string.
            if (actualValue == null)
                return null;
            if (actualValue is string)
                return actualValue;

            // Now the fun begins. We need to get the actual non nullable type of the object.
            var baseType = actualValue.GetType();
            if (_conventions.TryConvertValueToObjectForQuery(whereParams.FieldName, baseType, actualValue, forRange, out var objValue))
                return objValue;

            // This is much faster than doing reflection to get the non-nullable type.
            if (AbstractDocumentQueryCache.TransformNonNullableTypeCache.TryGet(baseType, out var type) == false)
            {
                type = baseType.GetNonNullableType();
                AbstractDocumentQueryCache.TransformNonNullableTypeCache.Put(baseType, type);
            }

            if (AbstractDocumentQueryCache.TransformTypeCache.TryGet(type, out var typeFormat))
            {
                switch (typeFormat)
                {
                    case AbstractDocumentQueryCache.TransformationMode.Value:
                        return actualValue;
                    case AbstractDocumentQueryCache.TransformationMode.Dates:
                        return whereParams.Exact
                            ? ((DateTimeOffset)actualValue).ToString(DefaultFormat.DateTimeOffsetFormatsToWrite, CultureInfo.InvariantCulture)
                            : actualValue;
                    case AbstractDocumentQueryCache.TransformationMode.Ticks:
                        return forRange ? ((TimeSpan)actualValue).Ticks : actualValue;
                }
            }

#if FEATURE_DATEONLY_TIMEONLY_SUPPORT
            if (type == typeof(DateOnly))
                return ((DateOnly)actualValue).ToString(DefaultFormat.DateOnlyFormatToWrite, CultureInfo.InvariantCulture);
            if (type == typeof(TimeOnly))
                return ((TimeOnly)actualValue).ToString(DefaultFormat.TimeOnlyFormatToWrite, CultureInfo.InvariantCulture);
#endif

            if (type.IsEnum)
                return whereParams.Value;

            if (actualValue is ValueType)
                return Convert.ToString(actualValue, CultureInfo.InvariantCulture);

            var result = GetImplicitStringConversion(baseType);
            if (result != null)
                return result(actualValue);

            return actualValue;
        }

        private string AddQueryParameter(object value)
        {
            string parameterName;
            if (ParameterPrefix == DefaultParameterPrefix && QueryParameters.Count < 128)
                parameterName = _pregeneratedParametersWithDefaultPrefix[QueryParameters.Count];
            else
                parameterName = $"{ParameterPrefix}{QueryParameters.Count.ToInvariantString()}";
            QueryParameters.Add(parameterName, value);
            return parameterName;
        }

        public string ParameterPrefix { get; set; } = DefaultParameterPrefix;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AssertMethodIsCurrentlySupported(string methodName)
        {
            if (IsFilterActive == false)
                return;

            throw new InvalidQueryException(
                $"{methodName} is currently unsupported for {nameof(LinqExtensions.Filter)}. If you want to use {methodName} in where method you have to put it before \"Filter\".");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private LinkedList<QueryToken> GetCurrentWhereTokens()
        {
            if (IsFilterActive)
                return FilterTokens;

            if (_isInMoreLikeThis == false)
                return WhereTokens;

            if (WhereTokens.Count == 0)
                throw new InvalidOperationException($"Cannot get '{nameof(MoreLikeThisToken)}' because there are no where tokens specified.");

            var moreLikeThisToken = WhereTokens.Last.Value as MoreLikeThisToken;

            if (moreLikeThisToken == null)
                throw new InvalidOperationException($"Last token is not '{nameof(MoreLikeThisToken)}'.");

            return moreLikeThisToken.WhereTokens;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private LinkedList<QueryToken> GetCurrentFilterTokens()
        {
            return FilterTokens;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private LinkedList<QueryToken> GetCurrentOrderByTokens()
        {
            return OrderByTokens;
        }

        protected void UpdateFieldsToFetchToken(FieldsToFetchToken fieldsToFetch, bool hadInclude = false)
        {
            FieldsToFetchToken = fieldsToFetch;

            if (SelectTokens.Count == 0)
            {
                SelectTokens.AddLast(fieldsToFetch);
            }
            else if (hadInclude == false)
            {
                var current = SelectTokens.First;
                var replaced = false;

                while (current != null)
                {
                    if (current.Value is FieldsToFetchToken)
                    {
                        current.Value = fieldsToFetch;
                        replaced = true;
                        break;
                    }

                    current = current.Next;
                }

                if (replaced == false)
                    SelectTokens.AddLast(fieldsToFetch);
            }
        }

        /// <summary>
        ///   Adds an alias to the FieldName of each where token
        /// </summary>
        /// <param name = "fromAlias">The alias</param>
        public void AddFromAliasToWhereTokens(string fromAlias)
        {
            var tokens = GetCurrentWhereTokens();
            AddFromAliasToTokens(fromAlias, tokens);
        }

        public void AddFromAliasToOrderByTokens(string fromAlias)
        {
            var tokens = GetCurrentOrderByTokens();
            AddFromAliasToTokens(fromAlias, tokens);
        }

        public void AddFromAliasToFilterTokens(string fromAlias)
        {
            var tokens = GetCurrentFilterTokens();
            AddFromAliasToTokens(fromAlias, tokens);
        }

        private void AddFromAliasToTokens(string fromAlias, LinkedList<QueryToken> tokens)
        {
            if (string.IsNullOrEmpty(fromAlias))
                throw new InvalidOperationException("Alias cannot be null or empty");

            var joinAliases = GetJoinAliases(fromAlias);
            var current = tokens.First;
            while (current != null)
            {
                switch (current.Value)
                {
                    case WhereToken w when JoinInfo.IsJoinField(w.FieldName, joinAliases):
                    case OrderByToken o when o.IsJoinField(joinAliases):
                        break;
                    case WhereToken w:
                        current.Value = w.AddAlias(fromAlias);
                        break;
                    case OrderByToken o:
                        current.Value = o.AddAlias(fromAlias);
                        break;
                }
                current = current.Next;
            }
        }
        
        public string AddAliasToIncludesTokens(string fromAlias)
        {
            if (_includesAlias == null)
                return fromAlias;

            if (fromAlias == null)
            {
                fromAlias = _includesAlias;
                AddFromAliasToWhereTokens(fromAlias);
            }

            if (CounterIncludesTokens != null)
            {
                foreach (var token in CounterIncludesTokens)
                {
                    token.AddAliasToPath(fromAlias);
                }
            }

            if (TimeSeriesIncludesTokens != null)
            {
                foreach (var token in TimeSeriesIncludesTokens)
                {
                    token.AddAliasToPath(fromAlias);
                }
            }

            return fromAlias;
        }

        protected static void GetSourceAliasIfExists(QueryData queryData, string[] fields, out string sourceAlias)
        {
            sourceAlias = null;

            if (fields.Length != 1 || fields[0] == null)
                return;

            var indexOf = fields[0].IndexOf(".", StringComparison.Ordinal);
            if (indexOf == -1)
                return;

            var possibleAlias = fields[0].Substring(0, indexOf);
            if (queryData.FromAlias != null &&
                queryData.FromAlias == possibleAlias)
            {
                sourceAlias = possibleAlias;
                return;
            }

            if (queryData.LoadTokens == null ||
                queryData.LoadTokens.Count == 0)
                return;
            if (queryData.LoadTokens.Any(lt => lt.Alias == possibleAlias) == false)
                return;

            sourceAlias = possibleAlias;
        }

        protected QueryData CreateTimeSeriesQueryData<TTs>(Func<ITimeSeriesQueryBuilder, TTs> timeSeriesQuery)
        {
            var builder = new TimeSeriesQueryBuilder<T>(this, _linqPathProvider);
            timeSeriesQuery.Invoke(builder);

            var fields = new[] { $"{Constants.TimeSeries.SelectFieldName}({builder.QueryText})" };
            var projections = new[] { Constants.TimeSeries.QueryFunction };
            return new QueryData(fields, projections);
        }

        public string ProjectionParameter(object value)
        {
            return "$" + AddQueryParameter(value);
        }

        internal void AddFilterLimit(int filterLimit)
        {
            if (filterLimit <= 0)
                throw new InvalidDataException("filter_limit needs to be positive and bigger than 0.");
            if (filterLimit is not int.MaxValue)
                FilterLimit = filterLimit;
        }
    }

    internal sealed class AbstractDocumentQueryCache
    {
        public enum TransformationMode
        {
            None = 0,
            Value,
            Ticks,
            Dates,
        }

        public static readonly TypeCache<TransformationMode> TransformTypeCache;
        public static readonly TypeCache<Type> TransformNonNullableTypeCache = new(256);

        static AbstractDocumentQueryCache()
        {
            TransformTypeCache = new(64);
            TransformTypeCache.Put(typeof(DateTime), TransformationMode.Value);
            TransformTypeCache.Put(typeof(int), TransformationMode.Value);
            TransformTypeCache.Put(typeof(long), TransformationMode.Value);
            TransformTypeCache.Put(typeof(decimal), TransformationMode.Value);
            TransformTypeCache.Put(typeof(double), TransformationMode.Value);
            TransformTypeCache.Put(typeof(float), TransformationMode.Value);
            TransformTypeCache.Put(typeof(bool), TransformationMode.Value);
            TransformTypeCache.Put(typeof(Guid), TransformationMode.Value);
            TransformTypeCache.Put(typeof(TimeSpan), TransformationMode.Ticks);
            TransformTypeCache.Put(typeof(DateTimeOffset), TransformationMode.Dates);
        }
    }

    internal sealed class JoinInfo
    {
        public readonly string SourceQuery;
        public readonly string OuterKeyField;
        public readonly string JoinedField;
        public readonly bool IsLeft;
        public readonly string BoostParameter;
        public string Alias;

        public static bool IsValidAlias(string alias)
        {
            if (string.IsNullOrEmpty(alias) || (char.IsLetter(alias[0]) == false && alias[0] != '_') || QueryToken.IsKeyword(alias))
                return false;

            foreach (var c in alias)
            {
                if (char.IsLetterOrDigit(c) == false && c != '_')
                    return false;
            }

            return true;
        }

        // a field written as <alias>.<field>, where the alias is the one of a join, belongs to the joined documents and not to the main document
        public static bool IsJoinField(string fieldName, string[] joinAliases)
        {
            if (fieldName == null)
                return false;

            foreach (var alias in joinAliases)
            {
                if (fieldName.Length > alias.Length && fieldName[alias.Length] == '.' && fieldName.StartsWith(alias, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        public JoinInfo(string sourceQuery, string outerKeyField, string joinedField, bool isLeft, string boostParameter)
        {
            SourceQuery = sourceQuery;
            OuterKeyField = outerKeyField;
            JoinedField = joinedField;
            IsLeft = isLeft;
            BoostParameter = boostParameter;
        }
    }
}
