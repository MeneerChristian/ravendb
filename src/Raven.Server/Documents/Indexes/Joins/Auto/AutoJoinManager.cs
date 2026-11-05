using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents.Indexes;
using Raven.Client.Util;
using Raven.Server.Config;
using Raven.Server.Config.Categories;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Sharding;
using Raven.Server.Logging;
using Sparrow.Logging;
using Raven.Server.ServerWide.Commands.Indexes;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.LowMemory;
using Sparrow.Server.Logging;
using Size = Sparrow.Size;

namespace Raven.Server.Documents.Indexes.Joins.Auto;

/// <summary>
/// Creates the auto join indexes of a database. A query that holds a JOIN that a join index could answer, and has none, asks for one: the definition is derived from the JOIN, put into the auto indexes of the
/// database record by a cluster command like an auto index, and built by the join index of every node. The query itself never waits for it, it is read as usual; only a query that asked to wait for
/// non stale results waits, the way a query that creates an auto index does.
/// </summary>
internal sealed class AutoJoinManager
{
    private static readonly RavenLogger Logger = RavenLogManager.Instance.GetLoggerForServer<AutoJoinManager>();

    // a creation that failed is not tried again for this long, so a query that cannot have a join index does not ask the cluster again and again
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private readonly DocumentDatabase _database;
    private readonly ConcurrentDictionary<string, Task<JoinIndex>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _failed = new(StringComparer.OrdinalIgnoreCase);

    public AutoJoinManager(DocumentDatabase database)
    {
        _database = database;
    }

    public AutoJoinCounters Counters { get; } = new();

    /// <summary>For tests: the available memory that the gate sees.</summary>
    internal Func<Size> AvailableMemoryForTesting { get; set; }

    /// <summary>
    /// Asks for the join index of every join of the query that has none and that one could answer. Returns when the creation was started, or, for a query that waits for non stale results,
    /// when the join index is a join index of this node (or the creation failed, the query is then read as usual).
    /// </summary>
    public async ValueTask ConsiderAsync(IndexQueryServerSide query, Index outer, CancellationToken token)
    {
        JoinConfiguration configuration = _database.Configuration.Join;
        if (configuration.AutoEnabled == false || _database is ShardedDocumentDatabase)
            return;

        Task<JoinIndex> waitFor = null;
        foreach (JoinClause join in query.Metadata.Query.Joins)
        {
            Task<JoinIndex> creation = Consider(query, outer, join, configuration);
            if (creation != null && query.WaitForNonStaleResults)
                waitFor = creation;
        }

        if (waitFor == null)
            return;

        TimeSpan budget = query.WaitForNonStaleResultsTimeout ?? configuration.AutoBuildWaitForNonStale.AsTimeSpan;
        try
        {
            await waitFor.WaitAsync(budget, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // the query is read as usual
        }
    }

    private Task<JoinIndex> Consider(IndexQueryServerSide query, Index outer, JoinClause join, JoinConfiguration configuration)
    {
        if (join.Query.From.Index == false || join.On.Left is not (FieldExpression or MethodExpression) || (join.On.Left is MethodExpression && join.InnerField == null) || join.IsComposite)
            return null;

        Index inner = _database.IndexStore.GetIndex(join.Query.From.From.FieldValue);
        if (inner == null)
            return null;

        string outerField = QueryBuilderHelper.ExtractIndexFieldName(query.Metadata.Query, query.QueryParameters, join.On.Left, query.Metadata).Value;
        if (AnyCouldServe(query, outer, inner, join, outerField))
            return null;

        List<JoinDefinition> autoJoins = AutoJoinsOf(outer, inner, outerField, join.InnerField);
        if (AutoJoinShape.TryDerive(join, query, outer, inner, autoJoins, out JoinDefinition joinIndex, out string reason) == false)
        {
            Counters.NotEligibleFor(reason);
            return null;
        }

        string name = AutoJoinShape.NameOf(joinIndex, withFields: autoJoins != null);
        if (_pending.TryGetValue(name, out Task<JoinIndex> pending))
            return pending;

        if (_failed.TryGetValue(name, out DateTime failedAt) && SystemTime.UtcNow - failedAt < RetryAfterFailure)
            return null;

        if (_database.ServerStore.Engine.CommandsVersionManager.CanPutCommand(nameof(PutAutoJoinIndexCommand)) == false)
        {
            Counters.SkipForClusterVersion();
            query.Timings?.AddCounter(AutoJoinCounters.SkippedClusterVersion, 1);
            return null;
        }

        if (MemoryAllows(outer, inner, configuration) == false)
        {
            Counters.SkipForMemory();
            query.Timings?.AddCounter(AutoJoinCounters.SkippedMemory, 1);
            return null;
        }

        var completion = new TaskCompletionSource<JoinIndex>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_pending.TryAdd(name, completion.Task) == false)
            return _pending.TryGetValue(name, out pending) ? pending : null;

        Counters.Request();
        string collection = outer.Collections.First();
        _ = Task.Run(async () =>
        {
            try
            {
                completion.SetResult(await CreateAsync(name, collection, joinIndex).ConfigureAwait(false));
            }
            catch (Exception e)
            {
                Counters.Fail();
                _failed[name] = SystemTime.UtcNow;
                if (Logger.IsInfoEnabled)
                    Logger.Info($"Could not create the auto join index '{name}'.", e);

                completion.SetResult(null);
            }
            finally
            {
                _pending.TryRemove(name, out _);
            }
        });

        return completion.Task;
    }

    private async Task<JoinIndex> CreateAsync(string name, string collection, JoinDefinition joinIndex)
    {
        var definition = new AutoIndexDefinition { Name = name, Type = IndexType.AutoJoin, Collection = collection, Join = joinIndex };
        await _database.IndexStore.Create.CreateAutoJoinAsync(definition, RaftIdGenerator.NewId()).ConfigureAwait(false);

        var created = _database.IndexStore.GetIndex(name) as JoinIndex;
        created?.MarkUsedByQuery();
        Counters.Create();
        return created;
    }

    private static bool JoinsOn(JoinIndex joinIndex, Index outer, Index inner, string outerField, string innerField)
    {
        return string.Equals(joinIndex.OuterName, outer.Name, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(joinIndex.InnerName, inner.Name, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(joinIndex.Join.OuterField, outerField, StringComparison.Ordinal) &&
               string.Equals(joinIndex.Join.InnerField, innerField, StringComparison.Ordinal);
    }

    /// <summary>
    /// A join index of the same indexes and keys, of any kind and in any state, that declares what the join needs: the query is served by it (or by the server, while it is built or behind), a second join index would only
    /// duplicate it. It is the same set of join indexes that a query tries, and the same analysis of the sub-query that serves it.
    /// </summary>
    private bool AnyCouldServe(IndexQueryServerSide query, Index outer, Index inner, JoinClause join, string outerField)
    {
        bool prepared = false;
        List<(QueryExpression Expression, OrderByFieldType FieldType, bool Ascending, NullsOrderingType NullsOrdering)> innerOrder = null;
        bool scoresMayMatter = false;

        foreach (JoinIndex joinIndex in _database.IndexStore.GetJoins())
        {
            if (JoinsOn(joinIndex, outer, inner, outerField, join.InnerField) == false)
                continue;

            if (prepared == false)
            {
                if (AutoJoinShape.TryPrepare(join, query, outer, out innerOrder, out scoresMayMatter) == false)
                    return false;

                prepared = true;
            }

            if (AutoJoinShape.CouldServe(join, query, joinIndex.Join, innerOrder, scoresMayMatter))
                return true;
        }

        return false;
    }

    /// <summary>The definitions of the auto join indexes of the same join: a new auto join index declares their fields too, so the join indexes that are made for one join do not multiply with every query.</summary>
    private List<JoinDefinition> AutoJoinsOf(Index outer, Index inner, string outerField, string innerField)
    {
        List<JoinDefinition> result = null;
        foreach (JoinIndex joinIndex in _database.IndexStore.GetJoins())
        {
            if (joinIndex.Type == IndexType.AutoJoin && JoinsOn(joinIndex, outer, inner, outerField, innerField))
                (result ??= new List<JoinDefinition>(1)).Add(joinIndex.Join);
        }

        return result;
    }

    /// <summary>
    /// A build is not started when its estimated memory is more than the budget, or when the available memory without it would be less than the floor. The estimate is a base plus
    /// a share per entry of the two indexes (counted as the documents of their collections).
    /// </summary>
    private bool MemoryAllows(Index outer, Index inner, JoinConfiguration configuration)
    {
        long documents = CountDocuments(outer, inner);
        long estimate = configuration.AutoBuildMemoryBase.GetValue(SizeUnit.Bytes) + configuration.AutoBuildMemoryPerEntry * documents;
        if (estimate > configuration.AutoMaxBuildMemory.GetValue(SizeUnit.Bytes))
            return false;

        Size available = AvailableMemoryForTesting?.Invoke() ??
                         _database.ServerStore.Server.MetricCacher.GetValue<MemoryInfoResult>(MetricCacher.Keys.Server.MemoryInfo).AvailableMemoryForProcessing;
        long floor = configuration.AutoMinimumAvailableMemory.GetValue(SizeUnit.Bytes);
        return available.GetValue(SizeUnit.Bytes) - estimate >= floor;
    }

    private long CountDocuments(Index outer, Index inner)
    {
        long documents = 0;
        using (_database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
        using (context.OpenReadTransaction())
        {
            foreach (string collection in outer.Collections.Concat(inner.Collections).Distinct(StringComparer.OrdinalIgnoreCase))
                documents += _database.DocumentsStorage.GetNumberOfDocumentsFor(collection, context);
        }

        return documents;
    }
}
