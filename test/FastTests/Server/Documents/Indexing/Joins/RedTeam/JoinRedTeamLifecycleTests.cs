using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Utils;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Documents.Smuggler;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.RedTeam;

/// <summary>Categories A and C of the red team review: the documents and the indexes that the join index reads change under it in ways other than a plain write.</summary>
public class JoinRedTeamLifecycleTests(ITestOutputHelper output) : JoinRedTeamBase(output)
{
    private async Task CheckAllAsync(JoinIndexHarness h, JoinIndexQueryOracle oracle, bool expectServed = true)
    {
        await h.AssertCurrentAsync();
        bool served = false;
        foreach (string sub in Subs)
        {
            served |= Same(oracle, Join(sub, " order by score() limit 0, 1000"));
            Same(oracle, LeftJoin(sub, " order by m.Title limit 0, 1000"));
        }

        if (expectServed)
            Assert.True(served, "the join index served nothing: " + string.Join(", ", oracle.Info.Fallbacks));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT14_DocumentsThatChangeTheirCollectionKeepTheJoinExact()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        for (int i = 0; i < 6; i++)
        {
            h.PutContext(i, i % 2 == 0 ? "nl" : "de");
            h.PutConversation(i, $"contexts/{i % 4}");
        }

        var oracle = new JoinIndexQueryOracle(h);
        await CheckAllAsync(h, oracle);

        // the inner document 2 and the outer document 3 move to another collection (same id), then come back
        // the server refuses to change the collection by an update: the document is deleted and created again
        h.Delete("contexts/2");
        PutRaw(h, "contexts/2", "Archived", new { Country = "nl", Name = "n", Rank = 5, IsActive = true, Title = "t" });
        h.Delete("conversations/3");
        PutRaw(h, "conversations/3", "Archived", new { ContextId = "contexts/0", Title = "moved" });
        await CheckAllAsync(h, oracle);

        h.Delete("contexts/2");
        PutRaw(h, "contexts/2", "Contexts", new { Country = "nl", Name = "n", Rank = 5, IsActive = true, Title = "back" });
        h.Delete("conversations/3");
        PutRaw(h, "conversations/3", "Conversations", new { ContextId = "contexts/2", Title = "back" });
        await CheckAllAsync(h, oracle);

        // the old collection and the new one in the same batch of the indexes: the document is in neither, then in the other
        h.Delete("contexts/1");
        PutRaw(h, "contexts/1", "Archived", new { Country = "nl", Name = "n", Rank = 5, IsActive = true, Title = "t" });
        h.Delete("contexts/1");
        PutRaw(h, "contexts/1", "Contexts", new { Country = "de", Name = "n", Rank = 6, IsActive = false, Title = "t2" });
        await CheckAllAsync(h, oracle);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes | RavenTestCategory.Revisions)]
    public async Task RT15_RevisionsAndTheirRevertDoNotDisturbTheJoin()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        await h.Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(new RevisionsConfiguration
        {
            Collections = new Dictionary<string, RevisionsCollectionConfiguration>
            {
                ["Conversations"] = new() { Disabled = false, MinimumRevisionsToKeep = 2 },
                ["Contexts"] = new() { Disabled = false, MinimumRevisionsToKeep = 2 }
            }
        }));
        var oracle = new JoinIndexQueryOracle(h);
        var rnd = new Random(21);
        h.RandomRound(rnd, 120);
        await CheckAllAsync(h, oracle);

        await Task.Delay(1100);
        DateTime checkpoint = DateTime.UtcNow;
        await Task.Delay(1100);
        h.RandomRound(rnd, 150);
        await CheckAllAsync(h, oracle);

        Operation revert = await h.Store.Maintenance.SendAsync(new RevisionsHelper.RevertRevisionsOperation(checkpoint, 60));
        await revert.WaitForCompletionAsync(TimeSpan.FromSeconds(60));
        await CheckAllAsync(h, oracle);

        h.RandomRound(rnd, 60);
        await CheckAllAsync(h, oracle);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes | RavenTestCategory.Smuggler)]
    public async Task RT16_ADocumentImportThatRewritesTheDocumentsWithNewEtagsKeepsTheJoinExact()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        var rnd = new Random(31);
        h.RandomRound(rnd, 150);
        var oracle = new JoinIndexQueryOracle(h);
        await CheckAllAsync(h, oracle);

        string path = Path.Combine(NewDataPath(), "redteam.ravendbdump");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var export = await h.Store.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions { OperateOnTypes = DatabaseItemType.Documents }, path);
        await export.WaitForCompletionAsync(TimeSpan.FromSeconds(60));

        // the live data moves on, then the older data comes back over it (documents come back that were deleted, others get their older values)
        h.RandomRound(rnd, 200);
        await CheckAllAsync(h, oracle);

        var import = await h.Store.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions { OperateOnTypes = DatabaseItemType.Documents }, path);
        await import.WaitForCompletionAsync(TimeSpan.FromSeconds(60));
        await CheckAllAsync(h, oracle);

        h.RandomRound(rnd, 60);
        await CheckAllAsync(h, oracle);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT17_AnOuterIndexReplacedByADefinitionThatTheJoinRefusesPutsItInTheErrorStateAndTheAnswersStayExact()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        h.RandomRound(new Random(41), 100);
        var oracle = new JoinIndexQueryOracle(h);
        await CheckAllAsync(h, oracle);

        // the outer index loads documents now: the server joins it as before, a join index cannot represent its entries
        var loading = new IndexDefinition { Name = JoinIndexHarness.OuterName, Maps = { "from c in docs.Conversations select new { c.ContextId, Title = LoadDocument(c.ContextId, \"Contexts\").Title }" } };
        await h.Store.Maintenance.SendAsync(new PutIndexesOperation(loading));
        await WaitForStateAsync(h, IndexState.Error);

        foreach (string sub in Subs)
            Same(oracle, Join(sub, " order by score() limit 0, 1000"));

        // the operator puts the old definition back
        await h.Store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndexHarness.OuterIndex()));
        h.RandomRound(new Random(42), 40);
        for (int i = 0; i < 200 && StateOf(h) is null or IndexState.Error; i++)
            await Task.Delay(50);

        bool recoveredByItself = StateOf(h) is not null and not IndexState.Error;
        Output.WriteLine("recovered by itself after the outer index got its old definition back: " + recoveredByItself);
        if (recoveredByItself == false)
            Enable(h);

        await WaitForStateAsync(h, IndexState.Normal);
        await CheckAllAsync(h, oracle);
        Assert.True(recoveredByItself, "the join index stays in the error state although the outer index is valid again: only an Enable of the join index brings it back");
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT18_AnInnerIndexThatLosesADeclaredFieldIsRefusedAndTheAnswersStayExact()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        h.RandomRound(new Random(51), 100);
        var oracle = new JoinIndexQueryOracle(h);
        await CheckAllAsync(h, oracle);

        var smaller = new IndexDefinition { Name = JoinIndexHarness.InnerName, Maps = { "from c in docs.Contexts select new { c.Country, c.IsActive }" } };
        await h.Store.Maintenance.SendAsync(new PutIndexesOperation(smaller));
        await WaitForStateAsync(h, IndexState.Error);
        foreach (string sub in new[] { "", " where Country = 'nl'", " where IsActive = true" })
        {
            Same(oracle, Join(sub, " order by score() limit 0, 1000"));
            Same(oracle, LeftJoin(sub, " order by m.Title limit 0, 1000"));
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT19_ConstantChurnOfTheUpstreamIndexesAndTheirStateEndsInAJoinThatIsCurrentAndExact()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        var oracle = new JoinIndexQueryOracle(h);
        using var stop = new CancellationTokenSource();
        var failures = new List<string>();
        int round = 0;

        Task writer = Task.Run(() =>
        {
            var rnd = new Random(61);
            while (stop.IsCancellationRequested == false)
            {
                try { h.RandomRound(rnd, 20); }
                catch (Exception e) { lock (failures) failures.Add("write: " + e.GetType().Name + " " + e.Message); }
            }
        });

        Task reader = Task.Run(() =>
        {
            while (stop.IsCancellationRequested == false)
            {
                try { oracle.Run(Join(" where Country = 'nl'", " order by score() limit 0, 1000")); }
                catch (Exception e) when (e is IndexDoesNotExistException || e.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("paused", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("not exist", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("stale", StringComparison.OrdinalIgnoreCase)) { }
                catch (Exception e) { lock (failures) failures.Add("query: " + e.GetType().Name + " " + e.Message); }
            }
        });

        DateTime until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            string index = round % 2 == 0 ? JoinIndexHarness.OuterName : JoinIndexHarness.InnerName;
            switch (round++ % 6)
            {
                case 0:
                    h.Store.Maintenance.Send(new StopIndexOperation(index));
                    await Task.Delay(100);
                    h.Store.Maintenance.Send(new StartIndexOperation(index));
                    break;
                case 1:
                    h.Store.Maintenance.Send(new DisableIndexOperation(index, false));
                    await Task.Delay(150);
                    h.Store.Maintenance.Send(new EnableIndexOperation(index, false));
                    break;
                case 2:
                    h.Store.Maintenance.Send(new ResetIndexOperation(index));
                    break;
                case 3:
                    IndexDefinition replaced = index == JoinIndexHarness.OuterName ? JoinIndexHarness.OuterIndex() : JoinIndexHarness.InnerIndex();
                    replaced.Maps.Add(replaced.Maps.First() + " ");
                    replaced.Maps.Remove(replaced.Maps.First());
                    h.Store.Maintenance.Send(new PutIndexesOperation(replaced));
                    break;
                case 4:
                    h.Store.Maintenance.Send(new SetIndexesPriorityOperation(index, IndexPriority.High));
                    h.Store.Maintenance.Send(new SetIndexesPriorityOperation(index, IndexPriority.Low));
                    break;
                default:
                    h.Store.Maintenance.Send(new StopIndexOperation(JoinIndexHarness.JoinName));
                    await Task.Delay(100);
                    h.Store.Maintenance.Send(new StartIndexOperation(JoinIndexHarness.JoinName));
                    break;
            }

            await Task.Delay(300);
        }

        stop.Cancel();
        await Task.WhenAll(writer, reader);
        foreach (string index in new[] { JoinIndexHarness.OuterName, JoinIndexHarness.InnerName, JoinIndexHarness.JoinName })
        {
            await RetryWhileMissingAsync(() => h.Store.Maintenance.Send(new EnableIndexOperation(index, false)));
            await RetryWhileMissingAsync(() => h.Store.Maintenance.Send(new StartIndexOperation(index)));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Distinct().Take(10)));
        await WaitForStateAsync(h, IndexState.Normal);
        await CheckAllAsync(h, oracle);
        Output.WriteLine("operations: " + round);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT20_ConstantWritesNeverStarveTheServedQueriesOfTheIndexForLong()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        h.RandomRound(new Random(71), 150);
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        using var stop = new CancellationTokenSource();
        Task[] writers = Enumerable.Range(0, 3).Select(w => Task.Run(() =>
        {
            var rnd = new Random(100 + w);
            while (stop.IsCancellationRequested == false)
                h.RandomRound(rnd, 5);
        })).ToArray();

        long servedBefore = oracle.Info.Served, fallbackBefore = oracle.Info.FallbackTotal;
        int queries = 0;
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            oracle.Run(Join(" where Country = 'nl'", " order by score() limit 0, 1000"));
            queries++;
        }

        long served = oracle.Info.Served - servedBefore, fallbacks = oracle.Info.FallbackTotal - fallbackBefore;
        stop.Cancel();
        await Task.WhenAll(writers);
        Output.WriteLine($"under constant writes: {queries} queries, served by the join index {served}, fell back {fallbacks}, waits {oracle.Info.QueryWaits}, waited {oracle.Info.QueryWaitTotalInMs} ms, max {oracle.Info.QueryWaitMaxInMs} ms");

        // once the writes stop the join index serves again within a few seconds
        await h.AssertCurrentAsync();
        Assert.True(Same(oracle, Join(" where Country = 'nl'", " order by score() limit 0, 1000")));
        Assert.True(served > 0, $"not one of {queries} queries was served under constant writes");
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT21_AQueryThatIsCancelledWhileItWaitsForTheJoinLeavesNothingBehind()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        h.RandomRound(new Random(81), 100);
        await h.AssertCurrentAsync();
        string rql = Join(" where Country = 'nl'", " order by score() limit 0, 1000");

        using var gate = new ManualResetEventSlim(false);
        h.Join().ForTestingPurposes = phase =>
        {
            if (phase == "BeforeCommit")
                gate.Wait(TimeSpan.FromSeconds(30));
        };

        h.PutContext(1, "nl", "Alpha", 6, true, "again");
        h.PutConversation(700, "contexts/1");
        for (int i = 0; i < 100 && h.Stats().IsStale == false; i++)
            await Task.Delay(20);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
            using var session = h.Store.OpenAsyncSession();
            try
            {
                await session.Advanced.AsyncRawQuery<JoinIndexHarness.Conversation>(rql).WaitForNonStaleResults(TimeSpan.FromSeconds(20)).ToListAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        gate.Set();
        h.Join().ForTestingPurposes = null;
        await h.AssertCurrentAsync();

        // nothing holds the join index: it can be reset and deleted at once
        var reset = Task.Run(() => h.Store.Maintenance.Send(new ResetIndexOperation(JoinIndexHarness.JoinName)));
        Assert.True(await Task.WhenAny(reset, Task.Delay(TimeSpan.FromSeconds(30))) == reset, "the reset of the join index hangs after cancelled queries");
        await h.AssertCurrentAsync();
        var delete = Task.Run(() => h.Store.Maintenance.Send(new DeleteIndexOperation(JoinIndexHarness.JoinName)));
        Assert.True(await Task.WhenAny(delete, Task.Delay(TimeSpan.FromSeconds(30))) == delete, "the delete of the join index hangs after cancelled queries");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task RT22_UnrelatedBatchesOfTheOuterIndexDoNotGrowTheFileOfTheJoinWithoutBound()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings(), runInMemory: false);
        h.RandomRound(new Random(91), 150);
        await h.AssertCurrentAsync();
        long Used() => h.Join()._environment.Stats().UsedDataFileSizeInBytes;
        long Allocated() => h.Join()._environment.Stats().AllocatedDataFileSizeInBytes;
        long OnDisk() => new DirectoryInfo(h.Join().Configuration.StoragePath.Combine(IndexDefinitionBaseServerSide.GetIndexNameSafeForFileSystem(JoinIndexHarness.JoinName)).FullPath).EnumerateFiles("*", SearchOption.AllDirectories).Sum(x => x.Length);
        long usedBefore = Used(), allocatedBefore = Allocated(), diskBefore = OnDisk();
        long commitsBefore = h.Stats().Join.Refreshes + h.Stats().Join.NoOpRefreshes;

        // 400 batches of the outer index that change only its title: no key moves
        for (int i = 0; i < 400; i++)
        {
            h.PutConversation(i % 10, $"contexts/{i % 5}", "t" + i);
            if (i % 10 == 0)
                await h.AssertCurrentAsync();
        }

        await h.AssertCurrentAsync();
        long usedGrowth = Used() - usedBefore, allocatedGrowth = Allocated() - allocatedBefore, diskGrowth = OnDisk() - diskBefore;
        Output.WriteLine($"400 title-only batches: used {usedBefore} -> {Used()} (+{usedGrowth}), allocated {allocatedBefore} -> {Allocated()} (+{allocatedGrowth}), on disk {diskBefore} -> {OnDisk()} (+{diskGrowth}), refreshes {h.Stats().Join.Refreshes + h.Stats().Join.NoOpRefreshes - commitsBefore}");
        Assert.True(allocatedGrowth < 16 * 1024 * 1024, $"the file of the join index grew by {allocatedGrowth} bytes in 400 batches that moved no key");
        Assert.True(diskGrowth < 64 * 1024 * 1024, $"the directory of the join index grew by {diskGrowth} bytes in 400 batches that moved no key");
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT23_EveryPageOfASweepOverTiesIsTheSamePageOfTheJoinOfTheServer()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        for (int c = 0; c < 4; c++)
            h.PutContext(c, c % 2 == 0 ? "nl" : "de");

        // many outer documents with the same score, some without a joined document
        for (int i = 0; i < 61; i++)
            h.PutConversation(i, i % 9 == 0 ? "contexts/missing" : $"contexts/{i % 4}", i % 2 == 0 ? "a" : "b");

        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        foreach (string join in new[] { "join", "left join" })
        {
            foreach (string order in new[] { " order by score()", " order by m.Title", "" })
            {
                var pages = new List<string>();
                for (int skip = 0; skip < 70; skip += 7)
                {
                    string rql = $"{Outer} {join} (from index 'Contexts/Search' where Country = 'nl') as c on m.ContextId = id(c){order} limit {skip}, 7";
                    (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
                    AssertSame(rql, served, scan);
                    pages.AddRange(served.Rows.Select(x => x.Id));
                }

                Assert.Equal(pages.Count, pages.Distinct().Count());
            }
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT24_TombstonesAreKeptWhileTheJoinIsInTheErrorStateAndItIsExactAfterTheEnable()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, ScoreSettings());
        h.PutCodedContext(1, "k1");
        h.PutCodedContext(2, "k2");
        h.PutConversation(1, "k1");
        h.PutConversation(2, "k2");
        h.PutConversation(3, "k1");
        await h.AssertCurrentAsync();

        h.PutCodedContext(9, new string('z', JoinLimits.MaxTermLength + 1));
        await WaitForStateAsync(h, IndexState.Error);

        // deletes that the join index does not see while it is in the error state, then a run of the tombstone cleaner
        h.Delete("contexts/1");
        h.Delete("conversations/2");
        h.Delete("conversations/3");
        await WaitForUpstreamAsync(h);
        await h.CleanTombstonesAsync();
        await h.CleanTombstonesAsync();

        h.Delete("contexts/9");
        await WaitForUpstreamAsync(h);
        Enable(h);
        await WaitForStateAsync(h, IndexState.Normal);
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        Assert.True(Same(oracle, FieldJoin("", " order by m.Title limit 0, 1000")), string.Join(", ", oracle.Info.Fallbacks));
        Same(oracle, FieldLeftJoin("", " order by m.Title limit 0, 1000"));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT25_ADeleteOfTheJoinWhileItIsBuiltDoesNotHangAndTheJoinCanBePutAgain()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings(), putJoin: false);
        using (var bulk = h.Store.BulkInsert())
        {
            for (int i = 0; i < 30_000; i++)
                bulk.Store(new JoinIndexHarness.Context { Country = i % 2 == 0 ? "nl" : "de", Name = "n", Rank = i % 50, IsActive = true, Title = "t" }, $"contexts/{i}");

            for (int i = 0; i < 30_000; i++)
                bulk.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{i}", Title = "a" }, $"conversations/{i}");
        }

        await WaitForUpstreamAsync(h, 120);
        for (int round = 0; round < 3; round++)
        {
            await h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition());
            await Task.Delay(30 + round * 40);
            var delete = Task.Run(() => h.Store.Maintenance.Send(new DeleteIndexOperation(JoinIndexHarness.JoinName)));
            Assert.True(await Task.WhenAny(delete, Task.Delay(TimeSpan.FromSeconds(60))) == delete, "the delete of a join index that is built hangs");
            await delete;
        }

        await h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition());
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        Assert.True(Same(oracle, Join(" where Country = 'nl'", " order by m.Title limit 0, 100")), string.Join(", ", oracle.Info.Fallbacks));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RT26_IndexesThatReadSeveralCollectionsEachAreJoinedExactly()
    {
        var outer = new IndexDefinition
        {
            Name = JoinIndexHarness.OuterName,
            Maps = { "from c in docs.Conversations select new { c.ContextId, c.Title }", "from c in docs.Archived select new { ContextId = c.Ref, c.Title }" }
        };
        IndexDefinition inner = JoinIndexHarness.InnerIndex();
        inner.Maps.Add("from c in docs.Others select new { c.Country, c.Name, c.Rank, c.IsActive, c.Title }");
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings(), indexes: [outer, inner]);

        var rnd = new Random(17);
        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i < 60; i++)
            {
                string target = rnd.Next(2) == 0 ? $"contexts/{rnd.Next(10)}" : $"others/{rnd.Next(10)}";
                switch (rnd.Next(6))
                {
                    case 0:
                        h.PutConversation(rnd.Next(30), target, rnd.Next(2) == 0 ? "a" : "b");
                        break;
                    case 1:
                        PutRaw(h, $"archived/{rnd.Next(30)}", "Archived", new { Ref = target, Title = rnd.Next(2) == 0 ? "a" : "b" });
                        break;
                    case 2:
                        h.PutContext(rnd.Next(10), rnd.Next(3) == 0 ? "nl" : "de", "n", rnd.Next(50), rnd.Next(3) != 0);
                        break;
                    case 3:
                        PutRaw(h, $"others/{rnd.Next(10)}", "Others", new { Country = rnd.Next(3) == 0 ? "nl" : "de", Name = "n", Rank = rnd.Next(50), IsActive = rnd.Next(3) != 0, Title = "x" });
                        break;
                    case 4:
                        h.Delete(rnd.Next(2) == 0 ? $"conversations/{rnd.Next(30)}" : $"archived/{rnd.Next(30)}");
                        break;
                    default:
                        h.Delete(rnd.Next(2) == 0 ? $"contexts/{rnd.Next(10)}" : $"others/{rnd.Next(10)}");
                        break;
                }
            }

            await h.AssertCurrentAsync();
        }

        var oracle = new JoinIndexQueryOracle(h);
        bool served = false;
        foreach (string sub in Subs)
        {
            served |= Same(oracle, Join(sub, " order by score() limit 0, 1000"));
            Same(oracle, LeftJoin(sub, " order by m.Title limit 0, 1000"));
        }

        Assert.True(served, string.Join(", ", oracle.Info.Fallbacks));
    }
}
