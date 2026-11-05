using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Tests.Infrastructure;
using Xunit;
using Index = Raven.Server.Documents.Indexes.Index;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>What a join index that stands between two commits of a refresh does to the things around it: a query that holds an older snapshot, an upstream index that has documents it did not index yet, the field join, a backup, a replacement of an index.</summary>
public class JoinIndexPartialGenerationTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const int Budget = 100;

    private static Task<JoinIndexHarness> CreateAsync(RavenTestBase test, bool field = false, bool runInMemory = true)
    {
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = Budget.ToString() };
        return field
            ? JoinIndexHarness.CreateFieldJoinAsync(test, settings, runInMemory: runInMemory)
            : JoinIndexHarness.CreateAsync(test, settings, runInMemory: runInMemory);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 1000 && condition() == false; i++)
            await Task.Delay(20);

        Assert.True(condition(), what);
    }

    private static Task WaitIndexAsync(JoinIndexHarness h, string name)
    {
        return WaitForAsync(() => h.Stats(name).IsStale == false, $"{name} is current");
    }

    private static async Task WaitUpstreamAsync(JoinIndexHarness h)
    {
        await WaitIndexAsync(h, JoinIndexHarness.OuterName);
        await WaitIndexAsync(h, JoinIndexHarness.InnerName);
    }

    private static async Task StopJoinAsync(JoinIndexHarness h)
    {
        await h.Store.Maintenance.SendAsync(new StopIndexOperation(JoinIndexHarness.JoinName));
        await WaitForAsync(() => h.Stats().Status == IndexRunningStatus.Paused, "the join index is paused");
    }

    private static void SeedIdJoin(JoinIndexHarness h, int conversations, int contexts)
    {
        h.Save(s =>
        {
            for (int i = 0; i < contexts; i++)
                s.Store(new JoinIndexHarness.Context { Country = i % 3 == 0 ? "nl" : "de", Name = "n" + i % 5, Rank = i, IsActive = i % 2 == 0, Title = "red fox " + i % 4 }, $"contexts/{i}");
        });
        h.Save(s =>
        {
            for (int i = 0; i < conversations; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{i % contexts}", Title = "t" }, $"conversations/{i}");
        });
    }

    /// <summary>
    /// A query that opened its snapshots of the indexes before the second batch of the outer index has the consumed etag of the end of the first batch. A refresh that sees both batches stops after
    /// exactly the documents of the first batch, but the one document of that batch that the second batch wrote again: its cursor is equal to the one of the query, and its lists miss that document.
    /// </summary>
    [RavenFact(RavenTestCategory.Indexes)]
    public async Task APartialStateWithTheCursorsOfAnOlderQuerySnapshotIsBehindAndNeverServed()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        SeedIdJoin(h, 10, 40);
        await h.AssertCurrentAsync();
        JoinIndex joinIndex = h.Join();
        await StopJoinAsync(h);

        h.Save(s =>
        {
            s.Store(new JoinIndexHarness.Conversation { ContextId = "contexts/1", Title = "first" }, "conversations/900");
            for (int i = 0; i < Budget; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{i % 40}", Title = "t" }, $"conversations/{1000 + i}");
        });
        await WaitUpstreamAsync(h);

        Index outerIndex = h.Database.IndexStore.GetIndex(JoinIndexHarness.OuterName);
        Index innerIndex = h.Database.IndexStore.GetIndex(JoinIndexHarness.InnerName);
        Assert.True(outerIndex.TryOpenSnapshot(out IndexSnapshot oldOuter));
        Assert.True(innerIndex.TryOpenSnapshot(out IndexSnapshot oldInner));
        using var outerView = new IndexView(oldOuter, joinIndex.Plan.IdField);
        using var innerView = new IndexView(oldInner, joinIndex.Plan.IdField);

        h.Save(s =>
        {
            s.Store(new JoinIndexHarness.Conversation { ContextId = "contexts/2", Title = "again" }, "conversations/900");
            for (int i = 0; i < 2 * Budget + 50; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{i % 40}", Title = "u" }, $"conversations/{2000 + i}");
        });
        await WaitIndexAsync(h, JoinIndexHarness.OuterName);

        using var gate = new JoinIndexChunkTests.Gate(joinIndex);
        await h.Store.Maintenance.SendAsync(new StartIndexOperation(JoinIndexHarness.JoinName));
        var oracle = new JoinIndexQueryOracle(h);
        string rql = JoinIndexQueryOracle.Join(" where Country = 'nl'");
        int chunks = 0;
        bool checkedPartial = false;
        while (gate.WaitForChunk(TimeSpan.FromSeconds(3)))
        {
            chunks++;
            Assert.True(joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot file));
            using (file)
            {
                if (chunks == 2)
                {
                    JoinIndexGeneration generation = file.Generation;
                    Assert.True(generation.IsPartial, generation.ToString());

                    // the cursors alone cannot tell this state from the state of the older snapshot
                    Assert.Equal(GenerationComparison.Equal, generation.Outer.CompareTo(oldOuter.Consumed));
                    Assert.Equal(GenerationComparison.Equal, generation.Inner.CompareTo(oldInner.Consumed));

                    // and the lists are not the state of that snapshot: the document the second batch wrote again is in the snapshot and not in the lists
                    Assert.True(outerView.TryFindEntry(Encoding.UTF8.GetBytes("conversations/900"), out _, out _));
                    Assert.False(file.TryGetOuterDocument(JoinFileRows.HashDocumentId(Encoding.UTF8.GetBytes("conversations/900"), file.Seed), out _));

                    Assert.Equal(JoinServing.Outcome.Behind, JoinServing.Compare(generation, oldOuter.EnvironmentId, oldOuter.Consumed, oldInner.EnvironmentId, oldInner.Consumed));

                    long served = oracle.Info.Served;
                    (QueryAnswer answer, QueryAnswer scan) = oracle.Both(rql);
                    Assert.Equal(served, oracle.Info.Served);
                    JoinIndexQueryOracle.AssertSame(rql, answer, scan);
                    checkedPartial = true;
                }

                JoinFileConsistencyChecker.Result consistent = JoinFileConsistencyChecker.Check(joinIndex, 3000, 40);
                Assert.True(consistent.Ok, consistent.ToString());
            }

            gate.Resume();
        }

        gate.Open();
        Assert.True(checkedPartial, $"{chunks} commits");
        await h.AssertCurrentAsync();
        oracle.Check(rql);
    }

    /// <summary>
    /// An inner document that was written again after the inner index consumed it is read by the refresh as a change above the snapshot. While the outer side is cut, the outer entries of the
    /// snapshot are not the rows of the file, so the inner side must not be applied then.
    /// </summary>
    [RavenFact(RavenTestCategory.Indexes)]
    public async Task InnerDocumentsAboveTheInnerSnapshotAreNotAppliedWhileTheOuterSideIsCut()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        SeedIdJoin(h, 300, 40);
        await h.AssertCurrentAsync();
        JoinIndex joinIndex = h.Join();
        await StopJoinAsync(h);

        h.PutContext(50, "nl", "Alpha", 1, true, "red fox");
        await WaitIndexAsync(h, JoinIndexHarness.InnerName);
        await h.Store.Maintenance.SendAsync(new StopIndexOperation(JoinIndexHarness.InnerName));
        await WaitForAsync(() => h.Stats(JoinIndexHarness.InnerName).Status == IndexRunningStatus.Paused, "the inner index is paused");
        h.PutContext(50, "fr", "Beta", 2, false, "blue whale");

        h.Save(s =>
        {
            for (int i = 0; i < 300; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = i % 2 == 0 ? "contexts/50" : $"contexts/{(i + 1) % 40}", Title = "moved" }, $"conversations/{i}");
        });
        await WaitIndexAsync(h, JoinIndexHarness.OuterName);

        using var gate = new JoinIndexChunkTests.Gate(joinIndex);
        await h.Store.Maintenance.SendAsync(new StartIndexOperation(JoinIndexHarness.JoinName));
        int chunks = 0;
        while (gate.WaitForChunk(TimeSpan.FromSeconds(3)))
        {
            chunks++;
            JoinFileConsistencyChecker.Result consistent = JoinFileConsistencyChecker.Check(joinIndex, 300, 51);
            Assert.True(consistent.Ok, $"before commit {chunks}: {consistent}");
            gate.Resume();
        }

        gate.Open();
        Assert.True(chunks >= 3, $"{chunks} commits");
        await h.Store.Maintenance.SendAsync(new StartIndexOperation(JoinIndexHarness.InnerName));
        await h.AssertCurrentAsync();
        Assert.True(ReferenceEquals(h.Join(), joinIndex), "the join index was reset");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AFieldJoinIsConsistentBetweenTheCommitsOfAChangeOfBothSides()
    {
        using JoinIndexHarness h = await CreateAsync(this, field: true);
        h.Save(s =>
        {
            for (int i = 0; i < 260; i++)
                s.Store(new JoinIndexHarness.Context { Code = "k" + i % 40, Country = i % 2 == 0 ? "nl" : "de", Name = "n" + i % 5, Rank = i, IsActive = i % 2 == 0, Title = "red fox" }, $"contexts/{i}");
        });
        h.Save(s =>
        {
            for (int i = 0; i < 400; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + i % 45, Title = "t" }, $"conversations/{i}");
        });
        await h.AssertCurrentAsync();
        JoinIndex joinIndex = h.Join();
        Assert.True(joinIndex.Plan.IsFieldJoin);

        using var gate = new JoinIndexChunkTests.Gate(joinIndex);
        h.Save(s =>
        {
            for (int i = 0; i < 400; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + (i * 3) % 47, Title = "u" }, $"conversations/{i}");

            for (int i = 0; i < 260; i++)
                s.Store(new JoinIndexHarness.Context { Code = "k" + (i + 5) % 42, Country = i % 3 == 0 ? "nl" : "fr", Name = "n" + i % 4, Rank = 500 - i, IsActive = i % 3 == 0, Title = "blue whale" }, $"contexts/{i}");
        });

        int chunks = 0;
        while (gate.WaitForChunk(TimeSpan.FromSeconds(3)))
        {
            chunks++;
            JoinFileConsistencyChecker.Result consistent = JoinFileConsistencyChecker.Check(joinIndex, 400, 260);
            Assert.True(consistent.Ok, $"before commit {chunks}: {consistent}");
            Assert.True(consistent.OuterRows > 0);
            gate.Resume();
        }

        gate.Open();
        Assert.True(chunks >= 5, $"{chunks} commits");
        await h.AssertCurrentAsync();
        Assert.True(ReferenceEquals(h.Join(), joinIndex), "the join index was reset");

        // at the generation of the indexes the check of the file agrees with the check against the snapshots
        JoinFileConsistencyChecker.Result final = JoinFileConsistencyChecker.Check(joinIndex, 400, 260);
        Assert.True(final.Ok && final.Partial == false, final.ToString());
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ReplacingAnIndexWhileTheJoinIsPartialBuildsTheListsAgainExactly()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        SeedIdJoin(h, 300, 40);
        await h.AssertCurrentAsync();
        JoinIndex before = h.Join();

        using var gate = new JoinIndexChunkTests.Gate(before);
        h.Save(s =>
        {
            for (int i = 0; i < 300; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{(i + 3) % 40}", Title = "moved" }, $"conversations/{i}");
        });

        // the first commit is in, the second is open
        Assert.True(gate.WaitForChunk(TimeSpan.FromSeconds(30)));
        gate.Resume();
        Assert.True(gate.WaitForChunk(TimeSpan.FromSeconds(30)));
        Assert.True(before.TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
        using (snapshot)
            Assert.True(snapshot.Generation.IsPartial);

        IndexDefinition changed = JoinIndexHarness.OuterIndex();
        changed.Maps.Clear();
        changed.Maps.Add("from c in docs.Conversations select new { c.ContextId, c.Title, Extra = 1 }");
        await h.Store.Maintenance.SendAsync(new PutIndexesOperation(changed));
        gate.Open();

        await WaitForAsync(() => h.Join() is { } now && ReferenceEquals(now, before) == false, "the join index was reset");
        h.PutConversation(11, "contexts/2");
        await h.AssertCurrentAsync();
        Assert.Equal(1, h.Join().Counters.Builds);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ABackupOfADatabaseWhoseJoinIsPartialRestoresAJoinThatIsExact()
    {
        using JoinIndexHarness h = await CreateAsync(this, runInMemory: false);
        SeedIdJoin(h, 300, 40);
        await h.AssertCurrentAsync();
        JoinIndex joinIndex = h.Join();

        using var gate = new JoinIndexChunkTests.Gate(joinIndex);
        h.Save(s =>
        {
            for (int i = 0; i < 300; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{(i + 7) % 40}", Title = "moved" }, $"conversations/{i}");
        });

        Assert.True(gate.WaitForChunk(TimeSpan.FromSeconds(30)));
        gate.Resume();
        Assert.True(gate.WaitForChunk(TimeSpan.FromSeconds(30)));
        Assert.True(joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
        using (snapshot)
            Assert.True(snapshot.Generation.IsPartial);

        string backupPath = NewDataPath(suffix: "BackupFolder");
        var operation = await h.Store.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Backups.BackupOperation(new Raven.Client.Documents.Operations.Backups.BackupConfiguration
        {
            BackupType = Raven.Client.Documents.Operations.Backups.BackupType.Backup,
            LocalSettings = new Raven.Client.Documents.Operations.Backups.LocalSettings { FolderPath = backupPath }
        }));
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(1));
        gate.Open();
        await h.AssertCurrentAsync();

        string restored = $"restored_{h.Store.Database}";
        using (Backup.RestoreDatabase(h.Store, new Raven.Client.Documents.Operations.Backups.RestoreBackupConfiguration
               { BackupLocation = System.IO.Directory.GetDirectories(backupPath).First(), DatabaseName = restored }))
        {
            Raven.Server.Documents.DocumentDatabase database = await GetDatabase(restored);
            await WaitForAsync(() => h.Store.Maintenance.ForDatabase(restored).Send(new GetIndexesStatisticsOperation()).All(x => x.IsStale == false), "the restored indexes are current");

            var seen = new JoinIndexInvariantChecker.SeenTerms(joinIndex.Join.Fields.Count);
            JoinIndexInvariantChecker.Result result = null;
            for (int i = 0; i < 500; i++)
            {
                result = JoinIndexInvariantChecker.Check(database, (JoinIndex)database.IndexStore.GetIndex(JoinIndexHarness.JoinName), seen);
                if (result.Compared)
                    break;

                await Task.Delay(20);
            }

            Assert.True(result is { Ok: true }, result?.ToString());
        }
    }
}
