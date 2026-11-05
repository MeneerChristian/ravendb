using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Config.Categories;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes.Joins.Auto;
using Raven.Server.ServerWide.Commands;
using Raven.Server.ServerWide.Commands.Indexes;
using Raven.Server.ServerWide.Context;
using Raven.Server.ServerWide.Maintenance;
using Sparrow;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Auto.JoinIndexAutoQueryTests;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Auto;

/// <summary>The auto join index is an auto index in every way that is not its job: the memory gate, idle and deletion, restart, the writes that it follows.</summary>
public class JoinIndexAutoLifecycleTests(ITestOutputHelper output) : RavenTestBase(output)
{
    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnEstimateAboveTheBudgetBuildsNothingAndIsCounted()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(this, settings: ScoreSettings(("Join.AutoMaxBuildMemoryInMb", "1")));
        var oracle = new JoinIndexQueryOracle(h, AutoName);
        string rql = oracle.JoinOf(FirstSub, FirstTail);

        QueryAnswer first = oracle.Run(rql);
        QueryAnswer second = oracle.Run(rql);
        AssertSame(rql, second, first);
        Assert.True(first.Rows.Count > 0);

        // the query is the join of the server and says why there is no join index
        Assert.Equal(1, first.Counters.GetValueOrDefault("AutoJoinSkippedMemory"));
        Assert.Equal(2, h.Database.IndexStore.AutoJoins.Counters.SkippedForMemory);
        Assert.Equal(0, h.Database.IndexStore.AutoJoins.Counters.Requested);
        await Task.Delay(300);
        Assert.Empty(AutoJoinNames(h));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheEstimateGrowsWithTheEntriesOfTheTwoIndexes()
    {
        // 300 MB of budget: the base of 192 MB fits, 10 MB for each of the documents does not
        using JoinIndexHarness h = await HarnessWithDataAsync(this, settings: ScoreSettings(("Join.AutoMaxBuildMemoryInMb", "300"), ("Join.AutoBuildMemoryPerEntryInBytes", (10 * 1024 * 1024).ToString())));
        var oracle = new JoinIndexQueryOracle(h, AutoName);

        _ = oracle.Run(oracle.JoinOf(FirstSub, FirstTail));

        Assert.Equal(1, h.Database.IndexStore.AutoJoins.Counters.SkippedForMemory);
        Assert.Empty(AutoJoinNames(h));
    }

    /// <summary>The floor of the memory gate that the default of a machine with this much memory is: the gate reads it from the configuration of the database.</summary>
    private static void UseTheDefaultFloorOfAMachineWith(JoinIndexHarness h, int totalMegabytes) =>
        h.Database.Configuration.Join.AutoMinimumAvailableMemory = new JoinConfiguration(new Size(totalMegabytes, SizeUnit.Megabytes)).AutoMinimumAvailableMemory;

    /// <summary>The tests that are about something else than the gate do not read the memory of the machine that runs them: they get a machine with room for any build of the test data.</summary>
    private static void TheMachineHasMemoryToSpare(JoinIndexHarness h) =>
        h.Database.IndexStore.AutoJoins.AvailableMemoryForTesting = () => new Size(64, SizeUnit.Gigabytes);

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task LittleAvailableMemoryBuildsNothingUntilThereIsMore()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(this, seed: 11);
        UseTheDefaultFloorOfAMachineWith(h, 16 * 1024);
        AutoJoinManager manager = h.Database.IndexStore.AutoJoins;
        var oracle = new JoinIndexQueryOracle(h, AutoName);
        string rql = oracle.JoinOf(FirstSub, FirstTail);

        // available minus the estimate (192 MB and a little) has to leave the floor: 2 GB on a machine of 16 GB (1.25 times the low memory limit of its server)
        manager.AvailableMemoryForTesting = () => new Size(2000, SizeUnit.Megabytes);
        QueryAnswer raw = oracle.Run(rql);
        Assert.Equal(1, raw.Counters.GetValueOrDefault("AutoJoinSkippedMemory"));
        Assert.Equal(1, manager.Counters.SkippedForMemory);
        Assert.Equal(0, manager.Counters.Requested);
        Assert.Empty(AutoJoinNames(h));

        // more than the floor and the estimate
        manager.AvailableMemoryForTesting = () => new Size(2500, SizeUnit.Megabytes);
        _ = oracle.Run(rql);
        Assert.Equal(1, manager.Counters.Requested);
        await WaitForTheAutoJoinAsync(h);

        QueryAnswer served = oracle.Run(rql);
        Assert.Equal(1, served.Counters.GetValueOrDefault("Join/c/JoinIndexServed"));
        AssertSame(rql, served, raw);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task OnAMachineWithOneGigabyteASmallJoinIsBuiltWhenItFitsAndNotWhenItDoesNot()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(this, seed: 13);
        UseTheDefaultFloorOfAMachineWith(h, 1024);
        AutoJoinManager manager = h.Database.IndexStore.AutoJoins;
        var oracle = new JoinIndexQueryOracle(h, AutoName);
        string rql = oracle.JoinOf(FirstSub, FirstTail);

        // the floor is 128 MB (the low memory limit of its server is 102 MB): 300 MB available minus the estimate (192 MB and a little) leaves less
        manager.AvailableMemoryForTesting = () => new Size(300, SizeUnit.Megabytes);
        QueryAnswer raw = oracle.Run(rql);
        Assert.Equal(1, raw.Counters.GetValueOrDefault("AutoJoinSkippedMemory"));
        Assert.Equal(0, manager.Counters.Requested);
        Assert.Empty(AutoJoinNames(h));

        // 800 MB available leaves more than the floor, which the fixed 1 GB never allowed on this machine
        manager.AvailableMemoryForTesting = () => new Size(800, SizeUnit.Megabytes);
        _ = oracle.Run(rql);
        Assert.Equal(1, manager.Counters.Requested);
        await WaitForTheAutoJoinAsync(h);

        QueryAnswer served = oracle.Run(rql);
        Assert.Equal(1, served.Counters.GetValueOrDefault("Join/c/JoinIndexServed"));
        AssertSame(rql, served, raw);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheAutoJoinFollowsTheWritesToTheIndexes()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(this, seed: 12);
        var oracle = new JoinIndexQueryOracle(h, AutoName);
        _ = oracle.Run(oracle.JoinOf(FirstSub, FirstTail));
        await WaitForTheAutoJoinAsync(h);

        var random = new Random(12);
        string[] subs = [FirstSub, " where Country = 'nl'", " where Rank > 20 and IsActive != true", " where Name = 'n2' or Country = 'c1'", ""];
        string[] tails = [FirstTail, " order by score() limit 0, 9", " limit 0, 1000"];
        for (int round = 0; round < 5; round++)
        {
            h.RandomRound(random, 40);
            if (round == 2)
                await h.CleanTombstonesAsync();

            await h.AssertCurrentAsync(AutoName);
            foreach (string sub in subs)
            {
                foreach (string tail in tails)
                {
                    // an OR or a range gives the documents of the sub-query different scores: only a query that does not use the score is served
                    bool needsNoScore = sub.Contains(" or ") || sub.Contains("Rank >");
                    oracle.Check(oracle.JoinOf(sub, tail), expectServed: needsNoScore == false || tail == FirstTail);
                }
            }
        }

        Assert.Equal(1, h.Database.IndexStore.AutoJoins.Counters.Requested);
        Assert.Equal([AutoName], AutoJoinNames(h));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnAndOverAGroupThatIsTooBigForTheResidualBudgetIsReadByTheServer()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, ScoreSettings(("Join.MaxResidualRows", "1")), putJoin: false);
        TheMachineHasMemoryToSpare(h);
        h.PutCodedContext(1, "k1", "nl", "n1", 1, true, "x");
        h.PutCodedContext(2, "k1", "de", "n2", 2, false, "y");
        h.PutCodedContext(3, "k2", "nl", "n2", 3, true, "z");
        h.PutCodedContext(4, "k3", "nl", "n1", 4, true, "x");
        h.PutCodedContext(5, "k3", "nl", "n1", 4, true, "x");
        h.PutCodedContext(6, "k4", "de", "n3", 20, false, "x");
        h.PutCodedContext(7, "k4", "de", "n3", 21, false, "x");
        h.PutCodedContext(8, "k4", "c9", "n3", 22, true, "x");
        for (int i = 1; i <= 9; i++)
            h.PutConversation(i, "k" + (1 + i % 4), i % 2 == 0 ? "a" : "b");

        Indexes.WaitForIndexing(h.Store);
        const string joinIndex = "Auto/Conversations/ByContext/Join/Contexts/Search/OnContextIdToCode";
        var oracle = new JoinIndexQueryOracle(h, joinIndex);
        string FieldJoin(string sub, string tail) => $"{Outer} join (from index '{JoinIndexHarness.InnerName}'{sub}) as c on m.ContextId = c.Code{tail}";

        _ = oracle.Run(FieldJoin(" where Country = 'nl' or Name = 'n2' or Rank > 10 or IsActive = true", " order by m.Title limit 0, 100"));
        await WaitForTheAutoJoinAsync(h, joinIndex);

        // an atom is served, and an AND over groups of two or more inner entries is more than the budget of one row
        oracle.Check(FieldJoin(" where Country = 'nl'", " order by m.Title limit 0, 100"));
        string and = FieldJoin(" where Country = 'nl' and Name = 'n2'", " order by m.Title limit 0, 100");
        (QueryAnswer served, QueryAnswer scan) = oracle.Both(and);
        AssertSame(and, served, scan);
        Assert.Equal(0, served.Counters.GetValueOrDefault("Join/c/JoinIndexServed"));
        Assert.Equal(1, served.Counters.GetValueOrDefault("Join/c/JoinFallback:KindNotServed:GroupResidual"));
        Assert.Equal(1, oracle.FallbacksOf("KindNotServed:GroupResidual"));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheAutoJoinIsOpenedAfterARestartAndFollowsTheIndexesWithoutBuilding()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings(), putJoin: false, runInMemory: false);
        TheMachineHasMemoryToSpare(h);
        h.RandomRound(new Random(13), 150);
        Indexes.WaitForIndexing(h.Store);
        var oracle = new JoinIndexQueryOracle(h, AutoName);
        string rql = oracle.JoinOf(FirstSub, FirstTail);

        QueryAnswer raw = oracle.Run(rql);
        await WaitForTheAutoJoinAsync(h);
        Assert.Equal(1, h.Stats(AutoName).Join.Builds);

        await h.Store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(h.Store.Database, disable: true));
        await h.Store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(h.Store.Database, disable: false));
        await h.AttachAsync();

        Assert.Equal(IndexType.AutoJoin, h.Join(AutoName).Type);
        h.TrackJoin(AutoName);
        h.RandomRound(new Random(14), 40);
        await h.AssertCurrentAsync(AutoName);
        Assert.Equal(0, h.Stats(AutoName).Join.Builds);

        // the restarted database does not ask for a second join index and serves from the one it opened
        oracle = new JoinIndexQueryOracle(h, AutoName);
        oracle.Check(rql);
        Assert.Equal([AutoName], AutoJoinNames(h));
        Assert.Equal(0, h.Database.IndexStore.AutoJoins.Counters.Requested);
        Assert.Equal(1, h.Store.Maintenance.Server.Send(new GetDatabaseRecordOperation(h.Store.Database)).AutoIndexes.Count(x => x.Value.Type == IndexType.AutoJoin));
        Assert.True(raw.Rows.Count > 0);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnIdleAutoJoinIsPromotedByAQueryAndDeletedLikeAnAutoIndex()
    {
        DoNotReuseServer();
        using JoinIndexHarness h = await HarnessWithDataAsync(this, seed: 15, operations: 100);
        var oracle = new JoinIndexQueryOracle(h, AutoName);
        string rql = oracle.JoinOf(FirstSub, FirstTail);
        QueryAnswer raw = oracle.Run(rql);
        await WaitForTheAutoJoinAsync(h);

        DocumentDatabase database = h.Database;
        TimeSpan idleAfter = database.Configuration.Indexing.TimeToWaitBeforeMarkingAutoIndexAsIdle.AsTimeSpan;
        TimeSpan deleteAfter = database.Configuration.Indexing.TimeToWaitBeforeDeletingAutoIndexMarkedAsIdle.AsTimeSpan;

        // not queried for a while: the observer marks it as idle, like any auto index
        var commands = await RunObserverAsync(database, IndexState.Normal, idleAfter + TimeSpan.FromSeconds(1));
        Assert.Equal([AutoName], commands);
        Assert.True(SpinWait.SpinUntil(() => h.Join(AutoName).State == IndexState.Idle, TimeSpan.FromSeconds(15)));

        // a query uses it: it is promoted
        QueryAnswer again = oracle.Run(rql);
        AssertSame(rql, again, raw);
        Assert.True(SpinWait.SpinUntil(() => h.Join(AutoName).State == IndexState.Normal, TimeSpan.FromSeconds(15)));
        await h.AssertCurrentAsync(AutoName);
        oracle.Check(rql);

        // the observer sees the recent query and makes the state of the record normal again
        commands = await RunObserverAsync(database, IndexState.Normal, TimeSpan.Zero);
        Assert.Equal([AutoName], commands);
        Assert.Equal(IndexState.Normal, h.Store.Maintenance.Server.Send(new GetDatabaseRecordOperation(h.Store.Database)).AutoIndexes[AutoName].State ?? IndexState.Normal);

        // idle again and not queried for the time of the deletion
        await RunObserverAsync(database, IndexState.Normal, idleAfter + TimeSpan.FromSeconds(1));
        Assert.True(SpinWait.SpinUntil(() => h.Join(AutoName).State == IndexState.Idle, TimeSpan.FromSeconds(15)));
        commands = await RunObserverAsync(database, IndexState.Idle, deleteAfter + TimeSpan.FromSeconds(1));
        Assert.Equal([AutoName], commands);
        Assert.True(SpinWait.SpinUntil(() => h.Join(AutoName) == null, TimeSpan.FromSeconds(15)));
        Assert.DoesNotContain(h.Store.Maintenance.Server.Send(new GetDatabaseRecordOperation(h.Store.Database)).AutoIndexes.Keys, x => x == AutoName);

        // and the next query is the join of the server that asks for it again
        QueryAnswer last = oracle.Run(rql);
        AssertSame(rql, last, raw);
        await WaitForTheAutoJoinAsync(h);
    }

    // the cluster observer decides about the auto indexes of the database from what the nodes report: this node reports the auto join index as last queried that long ago
    private async Task<string[]> RunObserverAsync(DocumentDatabase database, IndexState reportedState, TimeSpan lastQueriedAgo)
    {
        var observer = database.ServerStore.Observer;
        using (database.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
        using (context.OpenReadTransaction())
        using (var rawRecord = database.ServerStore.Cluster.ReadRawDatabaseRecord(context, database.Name, out _))
        {
            var clusterTopology = database.ServerStore.GetClusterTopology(context);
            var previous = observer.Maintenance.GetStats();
            var report = new DatabaseStatusReport
            {
                UpTime = lastQueriedAgo + TimeSpan.FromSeconds(1),
                LastIndexStats = new Dictionary<string, DatabaseStatusReport.ObservedIndexStatus>
                {
                    [AutoName] = new() { State = reportedState, LastQueried = lastQueriedAgo }
                }
            };
            var current = new Dictionary<string, ClusterNodeStatusReport>
            {
                [Server.ServerStore.NodeTag] = new ClusterNodeStatusReport(new ServerReport(), new Dictionary<string, DatabaseStatusReport> { [database.Name] = report },
                    ClusterNodeStatusReport.ReportStatus.Ok, null, database.Time.GetUtcNow(), null)
            };

            var state = new ClusterObserver.DatabaseObservationState(database.Name, rawRecord, rawRecord.Topology, clusterTopology, current, previous, 0, 0);
            var merged = new ClusterObserver.MergedDatabaseObservationState(state.RawDatabase);
            merged.AddState(state);

            var names = new List<string>();
            foreach ((var command, _) in observer.GetUnusedAutoIndexes(merged))
            {
                names.Add(command switch
                {
                    SetIndexStateCommand set => set.IndexName,
                    DeleteIndexCommand delete => delete.IndexName,
                    _ => throw new InvalidOperationException(command.GetType().Name)
                });
                await Server.ServerStore.Engine.SendToLeaderAsync(command);
            }

            return names.ToArray();
        }
    }
}
