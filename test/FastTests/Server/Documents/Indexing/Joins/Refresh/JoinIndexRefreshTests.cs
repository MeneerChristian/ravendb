using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Server.Config;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>The join index builds and refreshes its lists in its own write transaction: after every refresh they equal a scan of the two index snapshots (the oracle), and the standard etags say what it reflects.</summary>
public class JoinIndexRefreshTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static void Seed(JoinIndexHarness h)
    {
        h.PutContext(1, "nl", "Alpha", 10, true, "red fox");
        h.PutContext(2, "de", "Beta", 20, false, "blue whale");
        h.PutContext(3, "nl", "Gamma", 30, true, "red panda red");
        h.PutConversation(1, "contexts/1");
        h.PutConversation(2, "contexts/1");
        h.PutConversation(3, "contexts/2");
        h.PutConversation(4, "contexts/99");
        h.PutConversation(5, null);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 1000 && condition() == false; i++)
            await Task.Delay(20);

        Assert.True(condition(), what);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task BuildMatchesAScanOfTheSnapshots()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, putJoin: false);
        Seed(h);
        await h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition());

        JoinIndexInvariantChecker.Result result = await h.CheckAsync();
        Assert.True(result.Ok, result.ToString());
        Assert.Equal(3, result.Joined);
        Assert.Equal(1, h.Stats().Join.Builds);
        Assert.False(h.Stats().IsStale);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task RefreshAllocatesLittlePerChangedDocument()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, putJoin: false);
        Seed(h);
        await h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition());
        await h.AssertCurrentAsync();

        h.Save(s =>
        {
            for (int i = 0; i < 1000; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{1 + i % 2}", Title = "t" }, $"conversations/{100 + i}");
        });

        await h.AssertCurrentAsync();
        JoinCounters counters = h.Join().Counters;
        Output.WriteLine($"last refresh: {counters.LastRefreshIds} ids, {counters.LastRefreshAllocatedBytes} bytes");
        Assert.True(counters.IdsProcessed >= 1000);
        Assert.True(counters.LastRefreshIds < 100 || counters.LastRefreshAllocatedBytes < 1024 * counters.LastRefreshIds, $"{counters.LastRefreshAllocatedBytes} bytes for {counters.LastRefreshIds} ids");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task IncrementalChangesMatchAScan()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, putJoin: false);
        Seed(h);
        await h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition());
        await h.AssertCurrentAsync();

        // outer: insert, update the key (move), delete, delete and recreate
        h.PutConversation(6, "contexts/3");
        await h.AssertCurrentAsync();
        h.PutConversation(1, "contexts/3");
        await h.AssertCurrentAsync();
        h.Delete("conversations/2");
        await h.AssertCurrentAsync();
        h.Delete("conversations/3");
        h.PutConversation(3, "contexts/1");
        await h.AssertCurrentAsync();

        // inner: flip terms and the grouping, create the context a conversation waits for, delete one
        h.PutContext(1, "be", "Alpha2", 11, false, "green fox");
        await h.AssertCurrentAsync();
        h.PutContext(99, "fr", "Omega", 5, true, "late");
        await h.AssertCurrentAsync();
        h.Delete("contexts/3");
        await h.AssertCurrentAsync();
        h.PutContext(3, "nl", "Gamma", 31, true, "back");
        await h.AssertCurrentAsync();

        Assert.Equal(1, h.Stats().Join.Builds);
        Assert.True(h.Stats().Join.Refreshes > 0);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task FlipsOfAHubKeepTheGroupingsExact()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, putJoin: false);
        h.PutContext(0, "nl", "Hub", 1, true, "hub");
        h.Save(s =>
        {
            for (int i = 0; i < 300; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "contexts/0", Title = "t" }, $"conversations/{i}");
        });
        await h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition());
        await h.AssertCurrentAsync();

        for (int round = 0; round < 6; round++)
        {
            h.PutContext(0, "nl", "Hub", round, round % 2 == 1, "hub " + round);
            await h.AssertCurrentAsync();

            h.Save(s =>
            {
                for (int i = 0; i < 30; i++)
                    s.Store(new JoinIndexHarness.Conversation { ContextId = "contexts/0", Title = "t" }, $"conversations/{1000 + round * 30 + i}");
            });
            h.PutContext(0, "nl", "Hub", round + 100, round % 2 == 0, "again " + round);
            await h.AssertCurrentAsync();
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task GroupingValueIsEncodedWithTheAnalyzerOfTheField()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, putJoin: false);
        h.PutContext(1, "NL", "Alpha");
        h.PutContext(2, "nl", "Beta");
        h.PutContext(3, "de", "Gamma");
        h.PutConversation(1, "contexts/1");
        h.PutConversation(2, "contexts/2");
        h.PutConversation(3, "contexts/3");

        Indexes.WaitForIndexing(h.Store);

        // the field has the default analyzer, which lowers the case: the index holds 'nl' for the first two contexts, whatever case the definition and the documents use
        foreach (string value in new[] { "NL", "nl", "Nl" })
        {
            string name = "Grouped" + value;
            IndexDefinition definition = JoinIndexHarness.JoinIndexDefinition(name);
            definition.Join.Groupings.Clear();
            definition.Join.AddGrouping("Country", value);
            await h.PutJoinAsync(definition);
            await h.AssertCurrentAsync(name);

            using (h.Join(name).TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot) ? snapshot : throw new InvalidOperationException("no snapshot"))
            {
                Assert.Equal(2, JoinIndexInvariantChecker.ReadList(snapshot, snapshot.SlotHandle(snapshot.GroupingMembersSlot(0))).Length);
                Assert.Equal(2, JoinIndexInvariantChecker.ReadList(snapshot, snapshot.SlotHandle(JoinReader.GroupingListSlot(0))).Length);
            }

            await h.Store.Maintenance.SendAsync(new DeleteIndexOperation(name));
        }
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(12)]
    public async Task RandomWorkloadKeepsTheJoinEqualToAScan(int seed)
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        var random = new Random(seed);

        for (int round = 0; round < 12; round++)
        {
            h.RandomRound(random, round == 0 ? 60 : 12);
            if (round % 4 == 3)
                await h.CleanTombstonesAsync();

            JoinIndexInvariantChecker.Result result = await h.CheckAsync();
            Assert.True(result.Ok, $"seed {seed} round {round}: {result}");
        }

        Assert.Equal(IndexRunningStatus.Running, h.Stats().Status);
        Assert.Equal(IndexState.Normal, h.Stats().State);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task RandomWorkloadWithManyDocumentsPerRefreshMatchesAScan()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        var random = new Random(42);

        for (int round = 0; round < 6; round++)
        {
            h.RandomRound(random, 150, contexts: 30, conversations: 400);
            JoinIndexInvariantChecker.Result result = await h.CheckAsync();
            Assert.True(result.Ok, $"round {round}: {result}");
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheOracleSeesAListThatWasChangedBehindTheRefresher()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        // negative control: take an entry (an Indirect file: a key) out of the any list in a commit of the index environment
        JoinIndex joinIndex = h.Join();
        long entry;
        using (joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot) ? snapshot : throw new InvalidOperationException("no snapshot"))
        {
            entry = JoinIndexInvariantChecker.ReadList(snapshot, snapshot.SlotHandle(JoinReader.AnySlot)).First();
        }

        using (joinIndex._contextPool.AllocateOperationContext(out TransactionOperationContext context))
        using (RavenTransaction tx = context.OpenWriteTransaction())
        {
            var file = new JoinIndexFile(tx.InnerTransaction, joinIndex.Plan.Schema);
            using (JoinWriter writer = file.BeginWrite())
            {
                if (joinIndex.Plan.Schema.IsIndirect)
                    writer.RemoveKeyFromAtom(JoinReader.AtomOfAny(), entry);
                else
                    writer.RemoveFromAny(entry);

                writer.Commit(file.Published);
            }

            tx.Commit();
        }

        JoinIndexInvariantChecker.Result result = JoinIndexInvariantChecker.Check(h.Database, joinIndex, h.Seen);
        Assert.True(result.Compared);
        Assert.False(result.Ok, "the oracle did not see the removed entry");
        Assert.Contains("any", result.Mismatches[0]);
    }

    private static readonly (string Operation, bool Documents, bool Tombstones)[] LagCases =
    [
        ("insert context", true, false),
        ("update context", true, false),
        ("delete context", false, true),
        ("insert conversation", true, false),
        ("update conversation", true, false),
        ("delete conversation", false, true)
    ];

    public static IEnumerable<object[]> LagOperations() => LagCases.Select(x => new object[] { x.Operation, x.Documents, x.Tombstones });

    [RavenTheory(RavenTestCategory.Indexes)]
    [MemberData(nameof(LagOperations))]
    public async Task OneChangeThatTheJoinHasNotCommittedIsLagAndStaleness(string operation, bool documents, bool tombstones)
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        h.PutContext(1, "nl");
        h.PutContext(2, "de");
        h.PutConversation(1, "contexts/1");
        h.PutConversation(2, "contexts/2");
        await h.AssertCurrentAsync();

        IndexStats before = h.Stats();
        Assert.False(before.IsStale);
        Assert.All(before.Collections.Values, x => Assert.Equal(0, x.DocumentLag));
        Assert.All(before.Collections.Values, x => Assert.Equal(0, x.TombstoneLag));

        // the join index holds its commit while the change is written: what the indexes have read but the join index has not committed is lag
        using var gate = new ManualResetEventSlim(false);
        using var reached = new ManualResetEventSlim(false);
        h.Join().ForTestingPurposes = phase =>
        {
            if (phase != "BeforeCommit")
                return;

            reached.Set();
            gate.Wait(TimeSpan.FromSeconds(30));
        };

        try
        {
            switch (operation)
            {
                case "insert context": h.PutContext(3, "nl"); break;
                case "update context": h.PutContext(1, "fr"); break;
                case "delete context": h.Delete("contexts/2"); break;
                case "insert conversation": h.PutConversation(3, "contexts/1"); break;
                case "update conversation": h.PutConversation(1, "contexts/2"); break;
                case "delete conversation": h.Delete("conversations/2"); break;
                default: throw new ArgumentOutOfRangeException(nameof(operation));
            }

            Assert.True(reached.Wait(TimeSpan.FromSeconds(20)), "the join index never refreshed");

            IndexStats pending = h.Stats();
            Assert.True(pending.IsStale);
            Assert.Equal(documents, pending.Collections.Values.Sum(x => x.DocumentLag) > 0);
            Assert.Equal(tombstones, pending.Collections.Values.Sum(x => x.TombstoneLag) > 0);
        }
        finally
        {
            gate.Set();
            h.Join().ForTestingPurposes = null;
        }

        await h.AssertCurrentAsync();
        IndexStats after = h.Stats();
        Assert.False(after.IsStale);
        Assert.All(after.Collections.Values, x => Assert.Equal(0, x.DocumentLag));
        Assert.All(after.Collections.Values, x => Assert.Equal(0, x.TombstoneLag));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ADocumentOfAnotherCollectionDoesNotMakeTheJoinStale()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        h.PutContext(1, "nl");
        h.PutConversation(1, "contexts/1");
        await h.AssertCurrentAsync();

        h.Save(s => s.Store(new { Name = "other" }, "others/1"));
        await Task.Delay(300);

        IndexStats stats = h.Stats();
        Assert.False(stats.IsStale);
        Assert.All(stats.Collections.Values, x => Assert.Equal(0, x.DocumentLag));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinThatWaitsForAnIndexIsStaleAndCatchesUpWhenTheIndexIsBack()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        JoinIndex before = h.Join();
        await h.Store.Maintenance.SendAsync(new DeleteIndexOperation(JoinIndexHarness.InnerName));
        await WaitForAsync(() => h.Stats().Status == IndexRunningStatus.Pending, "pending");

        h.PutContext(4, "fr", "Delta", 5, true, "new");
        h.PutConversation(7, "contexts/4");
        await h.Store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndexHarness.InnerIndex()));

        // the new index is another environment: the join index is built again
        await WaitForAsync(() => h.Join() is { } now && ReferenceEquals(now, before) == false, "the join index was reset");
        await WaitForAsync(() => h.Stats().Status == IndexRunningStatus.Running, "running again");
        await h.AssertCurrentAsync();
        Assert.False(h.Stats().IsStale);
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(JoinIndexHarness.OuterName)]
    [InlineData(JoinIndexHarness.InnerName)]
    public async Task ResetOfAJoinedIndexBuildsTheListsAgain(string index)
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();
        JoinIndex before = h.Join();

        await h.Store.Maintenance.SendAsync(new ResetIndexOperation(index));

        await WaitForAsync(() => h.Join() is { } now && ReferenceEquals(now, before) == false, "the join index was reset");
        await h.AssertCurrentAsync();
        h.PutConversation(9, "contexts/2");
        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task SideBySideReplacementOfTheOuterIndexBuildsTheListsAgain()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();
        JoinIndex before = h.Join();

        IndexDefinition changed = JoinIndexHarness.OuterIndex();
        changed.Maps.Clear();
        changed.Maps.Add("from c in docs.Conversations select new { c.ContextId, c.Title, Extra = 1 }");
        await h.Store.Maintenance.SendAsync(new PutIndexesOperation(changed));

        await WaitForAsync(() => h.Join() is { } now && ReferenceEquals(now, before) == false, "the join index was reset");
        h.PutConversation(11, "contexts/2");
        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task MoreChangesThanACommitTakesAreAppliedWithoutBuildingTheListsAgain()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = "10" });
        Seed(h);
        await h.AssertCurrentAsync();
        JoinIndex before = h.Join();

        h.Save(s =>
        {
            for (int i = 0; i < 50; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "contexts/1", Title = "t" }, $"conversations/{200 + i}");
        });

        await h.AssertCurrentAsync();
        Assert.True(ReferenceEquals(h.Join(), before), "the join index was reset");
        Assert.Equal(IndexState.Normal, h.Stats().State);
        Assert.Equal(1, before.Counters.Builds);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TermDictionaryThatIsMostlyEmptyBuildsTheListsAgain()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.MinTermsForEmptyRatio)] = "10",
            [RavenConfiguration.GetKey(x => x.Join.MaxEmptyTermRatio)] = "0.4"
        });
        Seed(h);
        await h.AssertCurrentAsync();

        h.Save(s =>
        {
            for (int i = 0; i < 30; i++)
                s.Store(new JoinIndexHarness.Context { Country = "c" + i, Name = "n" + i, Rank = i, IsActive = true, Title = "t" + i }, $"contexts/{50 + i}");
        });
        h.PutConversation(10, "contexts/50");
        await h.AssertCurrentAsync();
        JoinIndex before = h.Join();

        h.Save(s =>
        {
            for (int i = 0; i < 30; i++)
                s.Delete($"contexts/{50 + i}");
        });

        await WaitForAsync(() => h.Join() is { } now && ReferenceEquals(now, before) == false, "the join index was reset");
        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task MultiValuedKeyIsJoinedWithEveryDocumentItNames()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        h.Save(s =>
        {
            object doc = new { ContextId = new[] { "contexts/1", "contexts/2" }, Title = "two" };
            s.Store(doc, "conversations/77");
            s.Advanced.GetMetadataFor(doc)[Constants.Documents.Metadata.Collection] = "Conversations";
        });

        await h.AssertCurrentAsync();
        Assert.Equal(IndexState.Normal, h.Stats().State);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task NonNumericValueInALongFieldPutsTheJoinIndexInTheErrorState()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, putJoin: false);
        h.Save(s =>
        {
            object doc = new { Country = "nl", Name = "x", Rank = "not a number", IsActive = true, Title = "t" };
            s.Store(doc, "contexts/1");
            s.Advanced.GetMetadataFor(doc)[Constants.Documents.Metadata.Collection] = "Contexts";
        });
        h.PutConversation(1, "contexts/1");
        await h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition());

        await WaitForAsync(() => h.Stats().State == IndexState.Error, "error state");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task RestartResumesFromTheStoredGenerationWithoutBuilding()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, runInMemory: false);
        Seed(h);
        await h.AssertCurrentAsync();
        Assert.Equal(1, h.Stats().Join.Builds);

        await h.Store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(h.Store.Database, disable: true));
        await h.Store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(h.Store.Database, disable: false));
        await h.AttachAsync();

        h.PutConversation(8, "contexts/2");
        h.PutContext(4, "be", "Delta", 7, true, "after");
        await h.AssertCurrentAsync();

        Assert.Equal(0, h.Stats().Join.Builds);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task DeletesWhileTheDatabaseWasDownAreFoundAfterTheRestart()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, runInMemory: false);
        Seed(h);
        await h.AssertCurrentAsync();

        await h.Store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(h.Store.Database, disable: true));
        await h.Store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(h.Store.Database, disable: false));
        await h.AttachAsync();

        h.Delete("conversations/1");
        h.Delete("contexts/2");
        await h.CleanTombstonesAsync();
        await h.AssertCurrentAsync();
        Assert.Equal(0, h.Stats().Join.Builds);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheTombstonesThatTheJoinHasNotReadAreKept()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        using var gate = new ManualResetEventSlim(false);
        using var reached = new ManualResetEventSlim(false);
        h.Join().ForTestingPurposes = phase =>
        {
            if (phase != "BeforeCommit")
                return;

            reached.Set();
            gate.Wait(TimeSpan.FromSeconds(30));
        };

        try
        {
            h.Delete("contexts/2");
            Assert.True(reached.Wait(TimeSpan.FromSeconds(20)), "the join index never refreshed");
            await h.CleanTombstonesAsync();
        }
        finally
        {
            gate.Set();
            h.Join().ForTestingPurposes = null;
        }

        await h.AssertCurrentAsync();
    }
}
