using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>
/// A change of more documents than a commit of the join index takes is applied in several commits. Every commit is an exact intermediate state of the lists, which no query is served from,
/// and the join index is never built again for it.
/// </summary>
public class JoinIndexChunkTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const int Budget = 100;
    private const int Conversations = 450;
    private const int Contexts = 40;

    private static Task<JoinIndexHarness> CreateAsync(RavenTestBase test, bool runInMemory = true, Dictionary<string, string> more = null)
    {
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = Budget.ToString() };
        foreach ((string key, string value) in more ?? new Dictionary<string, string>())
            settings[key] = value;

        return JoinIndexHarness.CreateAsync(test, settings, runInMemory: runInMemory);
    }

    private static void Seed(JoinIndexHarness h, int conversations = Conversations, int contexts = Contexts)
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

    /// <summary>Stops the join index before every chunk (the one before it is committed then), hands the test the state and lets it go on.</summary>
    internal sealed class Gate : IDisposable
    {
        private readonly JoinIndex _join;
        private readonly SemaphoreSlim _reached = new(0);
        private readonly SemaphoreSlim _resume = new(0);
        private volatile bool _open;

        public Gate(JoinIndex joinIndex)
        {
            _join = joinIndex;
            joinIndex.ForTestingPurposes = phase =>
            {
                if (phase != "Discovered" || _open)
                    return;

                _reached.Release();
                _resume.Wait(TimeSpan.FromSeconds(60));
            };
        }

        public bool WaitForChunk(TimeSpan timeout) => _reached.Wait(timeout);

        public void Resume() => _resume.Release();

        public void Open()
        {
            _open = true;
            _resume.Release(1000);
        }

        public void Dispose()
        {
            Open();
            _join.ForTestingPurposes = null;
        }
    }

    private static void AssertNoRebuild(JoinIndex joinIndex)
    {
        Assert.DoesNotContain(joinIndex.Counters.RebuildsByReason.Keys, x => x != nameof(JoinRebuildReason.Initial));
    }

    private async Task<int> RunChunksAsync(JoinIndexHarness h, Action change, Action<int> inBetween = null, int conversations = Conversations, int contexts = Contexts)
    {
        JoinIndex joinIndex = h.Join();
        using var gate = new Gate(joinIndex);
        change();

        int chunks = 0;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (gate.WaitForChunk(TimeSpan.FromMilliseconds(500)) == false)
            {
                if (h.Store.Maintenance.Send(new GetIndexesStatisticsOperation()).Any(x => x.IsStale) == false)
                    break;

                continue;
            }

            chunks++;
            JoinFileConsistencyChecker.Result file = JoinFileConsistencyChecker.Check(joinIndex, conversations, contexts);
            Assert.True(file.Ok, $"after {chunks - 1} commits: {file}");
            inBetween?.Invoke(chunks);
            gate.Resume();
        }

        gate.Open();
        await h.AssertCurrentAsync();
        Assert.True(ReferenceEquals(h.Join(), joinIndex), "the join index was reset");
        AssertNoRebuild(joinIndex);
        Assert.Equal(1, joinIndex.Counters.Builds);

        // at the generation of the indexes the check of the file agrees with the check against the snapshots
        JoinFileConsistencyChecker.Result final = JoinFileConsistencyChecker.Check(joinIndex, conversations, contexts);
        Assert.True(final.Ok && final.Partial == false, final.ToString());
        return chunks;
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ARankOnlyChangeOfManyOuterDocumentsIsAppliedInChunks()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();
        long refreshes = h.Join().Counters.Refreshes;

        int chunks = await RunChunksAsync(h, () => h.Save(s =>
        {
            for (int i = 0; i < Conversations; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{i % Contexts}", Title = "changed" }, $"conversations/{i}");
        }));

        Assert.True(chunks >= Conversations / Budget, $"{chunks} commits");
        Assert.True(h.Join().Counters.PartialRefreshes >= Conversations / Budget - 1);
        Assert.True(h.Join().Counters.Refreshes > refreshes);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheStatisticsShowThePartialRefreshesAndThePublishedGeneration()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();
        JoinIndexStats before = h.Stats().Join;
        Assert.True(before.Generation > 0, "the built lists have a generation");
        Assert.False(before.IsPartial);

        await RunChunksAsync(h, () => h.Save(s =>
        {
            for (int i = 0; i < Conversations; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{i % Contexts}", Title = "changed" }, $"conversations/{i}");
        }));
        await h.AssertCurrentAsync();

        JoinIndexStats after = h.Stats().Join;
        Assert.True(after.PartialRefreshes > 0, "the refresh was cut at the budget of a commit");
        Assert.Equal(h.Join().Counters.PartialRefreshes, after.PartialRefreshes);
        Assert.True(after.Generation > before.Generation, $"generation {before.Generation} then {after.Generation}");
        Assert.False(after.IsPartial, "a join index that caught up publishes a complete generation");

        using (h.Join().TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot) ? snapshot : null)
            Assert.Equal(snapshot.Generation.Serial, after.Generation);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AChangeOfTheKeyOfManyOuterDocumentsIsAppliedInChunks()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        int chunks = await RunChunksAsync(h, () => h.Save(s =>
        {
            for (int i = 0; i < Conversations; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = i % 7 == 0 ? "contexts/none" : $"contexts/{(i * 7 + 3) % Contexts}", Title = "t" }, $"conversations/{i}");
        }));

        Assert.True(chunks >= Conversations / Budget, $"{chunks} commits");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AChangeOfManyInnerDocumentsIsAppliedInChunks()
    {
        const int contexts = 300;
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h, conversations: 400, contexts: contexts);
        await h.AssertCurrentAsync();

        int chunks = await RunChunksAsync(h, () => h.Save(s =>
        {
            for (int i = 0; i < contexts; i++)
                s.Store(new JoinIndexHarness.Context { Country = i % 2 == 0 ? "nl" : "fr", Name = "m" + i % 3, Rank = 1000 - i, IsActive = i % 3 == 0, Title = "blue whale " + i % 5 }, $"contexts/{i}");
        }), conversations: 400, contexts: contexts);

        Assert.True(chunks >= contexts / Budget, $"{chunks} commits");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ADeleteOfManyDocumentsOfBothSidesIsAppliedInChunks()
    {
        const int contexts = 250;
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h, conversations: 400, contexts: contexts);
        await h.AssertCurrentAsync();

        int chunks = await RunChunksAsync(h, () => h.Save(s =>
        {
            for (int i = 0; i < 400; i += 2)
                s.Delete($"conversations/{i}");

            for (int i = 0; i < contexts; i += 3)
                s.Delete($"contexts/{i}");
        }), conversations: 400, contexts: contexts);

        Assert.True(chunks >= 2, $"{chunks} commits");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ChangesOfBothSidesInOneBatchAreAppliedInChunks()
    {
        const int contexts = 260;
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h, conversations: 400, contexts: contexts);
        await h.AssertCurrentAsync();

        int chunks = await RunChunksAsync(h, () => h.Save(s =>
        {
            for (int i = 0; i < 400; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{(i * 5 + 1) % (contexts + 10)}", Title = "t2" }, $"conversations/{i}");

            for (int i = 0; i < contexts; i++)
                s.Store(new JoinIndexHarness.Context { Country = i % 2 == 0 ? "se" : "nl", Name = "q" + i % 4, Rank = i * 3, IsActive = i % 4 != 0, Title = "green frog " + i % 6 }, $"contexts/{i}");
        }), conversations: 400, contexts: contexts);

        Assert.True(chunks >= (400 + contexts) / Budget - 1, $"{chunks} commits");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ARandomWorkloadThatExceedsTheBudgetEveryRoundStaysExact()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h, conversations: 200, contexts: 30);
        await h.AssertCurrentAsync();

        var random = new Random(4711);
        for (int round = 0; round < 4; round++)
        {
            await RunChunksAsync(h, () =>
            {
                h.Save(s =>
                {
                    var used = new HashSet<string>();
                    for (int i = 0; i < 300; i++)
                    {
                        int kind = random.Next(10);
                        int id = random.Next(200);
                        string document = kind < 7 ? $"conversations/{id}" : $"contexts/{id % 30}";

                        // a session names a document once
                        if (used.Add(document) == false)
                            continue;

                        if (kind < 6)
                            s.Store(new JoinIndexHarness.Conversation { ContextId = random.Next(8) == 0 ? null : $"contexts/{random.Next(35)}", Title = "r" + round }, $"conversations/{id}");
                        else if (kind < 7)
                            s.Delete($"conversations/{id}");
                        else if (kind < 9)
                            s.Store(new JoinIndexHarness.Context { Country = "c" + random.Next(4), Name = "n" + random.Next(5), Rank = random.Next(50), IsActive = random.Next(3) != 0, Title = "w" + random.Next(6) }, document);
                        else
                            s.Delete(document);
                    }
                });
            }, conversations: 200, contexts: 30);

            // the first run built the lists, the counters are those of the one join index
            Assert.Equal(1, h.Join().Counters.Builds);
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task QueriesBetweenTheChunksAreServedExactlyOrReadAsUsual()
    {
        using JoinIndexHarness h = await CreateAsync(this, more: new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.QueryWait)] = "20" });
        Seed(h);
        await h.AssertCurrentAsync();

        var oracle = new JoinIndexQueryOracle(h);
        string rql = JoinIndexQueryOracle.Join(" where Country = 'nl'");
        long servedBefore = oracle.Info.Served;
        oracle.Check(rql);
        servedBefore = oracle.Info.Served;

        await RunChunksAsync(h, () => h.Save(s =>
        {
            for (int i = 0; i < Conversations; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{(i + 1) % Contexts}", Title = "moved" }, $"conversations/{i}");
        }), chunks =>
        {
            // the commit before this chunk is a partial state: it is never served, the answer is the join of the server and it is the right one
            Assert.True(h.Join().TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
            using (snapshot)
            {
                if (chunks > 1)
                    Assert.True(snapshot.Generation.IsPartial, snapshot.Generation.ToString());
            }

            long served = oracle.Info.Served;
            (QueryAnswer answer, QueryAnswer scan) = oracle.Both(rql);
            Assert.Equal(served, oracle.Info.Served);
            JoinIndexQueryOracle.AssertSame(rql, answer, scan);
        });

        oracle.Check(rql);
        Assert.True(oracle.Info.Served > servedBefore);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ARestartBetweenTheChunksResumesWithoutBuilding()
    {
        using JoinIndexHarness h = await CreateAsync(this, runInMemory: false);
        Seed(h);
        await h.AssertCurrentAsync();
        JoinIndex before = h.Join();
        Assert.Equal(1, before.Counters.Builds);

        using var reached = new SemaphoreSlim(0);
        using var go = new SemaphoreSlim(0);
        int seen = 0;
        before.ForTestingPurposes = phase =>
        {
            if (phase != "Discovered" || Interlocked.Increment(ref seen) != 3)
                return;

            reached.Release();
            go.Wait(TimeSpan.FromSeconds(30));
        };

        h.Save(s =>
        {
            for (int i = 0; i < Conversations; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{(i + 2) % Contexts}", Title = "again" }, $"conversations/{i}");
        });

        Assert.True(reached.Wait(TimeSpan.FromSeconds(30)), "the join index never reached its third commit");

        // two commits are in, the third is open: the database goes down now
        Task down = Task.Run(() => h.Store.Maintenance.Server.Send(new ToggleDatabasesStateOperation(h.Store.Database, disable: true)));
        await Task.Delay(300);
        go.Release(10);
        await down;
        await h.Store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(h.Store.Database, disable: false));
        await h.AttachAsync();

        JoinIndex after = h.Join();
        Assert.False(ReferenceEquals(before, after));
        await h.AssertCurrentAsync();
        Assert.Equal(0, after.Counters.Builds);
        AssertNoRebuild(after);
        Assert.True(JoinFileConsistencyChecker.Check(after, Conversations, Contexts).Ok);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AFieldJoinWithManyChangesIsAppliedInChunks()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = Budget.ToString() });
        h.Save(s =>
        {
            for (int i = 0; i < 60; i++)
                s.Store(new JoinIndexHarness.Context { Code = "k" + i % 20, Country = i % 2 == 0 ? "nl" : "de", Name = "n" + i % 5, Rank = i, IsActive = i % 2 == 0, Title = "red fox" }, $"contexts/{i}");
        });
        h.Save(s =>
        {
            for (int i = 0; i < 400; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + i % 25, Title = "t" }, $"conversations/{i}");
        });
        await h.AssertCurrentAsync();
        JoinIndex joinIndex = h.Join();

        h.Save(s =>
        {
            for (int i = 0; i < 400; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + (i * 3) % 27, Title = "u" }, $"conversations/{i}");

            for (int i = 0; i < 60; i++)
                s.Store(new JoinIndexHarness.Context { Code = "k" + (i + 5) % 22, Country = i % 3 == 0 ? "nl" : "fr", Name = "n" + i % 4, Rank = 100 - i, IsActive = i % 3 == 0, Title = "blue whale" }, $"contexts/{i}");
        });

        await h.AssertCurrentAsync();
        Assert.True(ReferenceEquals(h.Join(), joinIndex), "the join index was reset");
        AssertNoRebuild(joinIndex);
        Assert.True(joinIndex.Counters.PartialRefreshes >= 3, $"{joinIndex.Counters.PartialRefreshes} partial refreshes");
    }

    /// <summary>200 inner documents with a key of their own and 10 outer entries on each key. Changing the Country of every inner document is 20 list changes for each, 2 for each outer entry.</summary>
    /// <summary>The default of a machine with 1 GB: set, so that the tests do not depend on the memory of the machine that runs them.</summary>
    private const int MinQueued = 65_536;

    private async Task<(JoinIndexHarness Harness, JoinIndex Join)> CreateCostlyInnerChangeAsync(int limit)
    {
        JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = Budget.ToString(),
            [RavenConfiguration.GetKey(x => x.Join.MaxInnerListChangesPerOuterEntry)] = limit.ToString(),
            [RavenConfiguration.GetKey(x => x.Join.MinQueuedListChanges)] = MinQueued.ToString()
        });
        h.Save(s =>
        {
            for (int i = 0; i < 200; i++)
                s.Store(new JoinIndexHarness.Context { Code = "k" + i, Country = "a", Name = "n", Rank = 1, IsActive = true, Title = "t" }, $"contexts/{i}");
        });
        h.Save(s =>
        {
            for (int i = 0; i < 2000; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + i % 200, Title = "t" }, $"conversations/{i}");
        });
        await h.AssertCurrentAsync();
        return (h, h.Join());
    }

    private static void ChangeCountryOfEveryInnerDocument(JoinIndexHarness h)
    {
        h.Save(s =>
        {
            for (int i = 0; i < 200; i++)
                s.Store(new JoinIndexHarness.Context { Code = "k" + i, Country = "b", Name = "n", Rank = 1, IsActive = true, Title = "t" }, $"contexts/{i}");
        });
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task InnerChangesThatMakeMoreListChangesThanTheLimitBuildTheListsAgainBeforeTheFirstCommit()
    {
        // the first commit alone is 2 for each outer entry, which is not more than 2 for each: the 100 documents that are left make it 4,000 changes for 2,000 entries
        (JoinIndexHarness h, JoinIndex before) = await CreateCostlyInnerChangeAsync(limit: 1);
        using (h)
        {
            long partial = before.Counters.PartialRefreshes;
            ChangeCountryOfEveryInnerDocument(h);
            if (h.IsIndirect)
            {
                // the changes of an Indirect file are one for each transition of a key, however many outer entries it has: the rule stays but is not reached by this change
                await h.AssertCurrentAsync();
                Assert.True(ReferenceEquals(h.Join(), before), "the join index was reset");
                AssertNoRebuild(before);
                Assert.True(before.Counters.KeyListChanges > 0);
                return;
            }

            await WaitForRebuildAsync(h, before);
            await h.AssertCurrentAsync();
            Assert.Contains(nameof(JoinRebuildReason.InnerChangesTooCostly), before.Counters.RebuildsByReason.Keys);
            Assert.Equal(partial, before.Counters.PartialRefreshes);
            Assert.Equal(1, h.Join().Counters.Builds);
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task InnerChangesThatMakeNoMoreListChangesThanTheLimitAreAppliedInChunks()
    {
        (JoinIndexHarness h, JoinIndex before) = await CreateCostlyInnerChangeAsync(limit: 2);
        using (h)
        {
            long partial = before.Counters.PartialRefreshes;
            ChangeCountryOfEveryInnerDocument(h);
            await h.AssertCurrentAsync();
            Assert.True(ReferenceEquals(h.Join(), before), "the join index was reset");
            AssertNoRebuild(before);
            Assert.True(before.Counters.PartialRefreshes > partial);
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheLimitOfListChangesCanBeTurnedOff()
    {
        (JoinIndexHarness h, JoinIndex before) = await CreateCostlyInnerChangeAsync(limit: 0);
        using (h)
        {
            ChangeCountryOfEveryInnerDocument(h);
            await h.AssertCurrentAsync();
            Assert.True(ReferenceEquals(h.Join(), before), "the join index was reset");
            AssertNoRebuild(before);
        }
    }

    private static async Task WaitForRebuildAsync(JoinIndexHarness h, JoinIndex before)
    {
        for (int i = 0; i < 1000 && (h.Join() is not { } now || ReferenceEquals(now, before)); i++)
            await Task.Delay(20);

        Assert.False(ReferenceEquals(h.Join(), before), "the join index was not reset");
    }

    private async Task<long> FlushesOfTheCostlyInnerChangeAsync(int queued)
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = "1000",
            [RavenConfiguration.GetKey(x => x.Join.MaxInnerListChangesPerOuterEntry)] = "0",
            [RavenConfiguration.GetKey(x => x.Join.MaxQueuedListChanges)] = queued.ToString(),
            [RavenConfiguration.GetKey(x => x.Join.MinAtomEntriesForDirectWrite)] = "0"
        });
        h.Save(s =>
        {
            for (int i = 0; i < 200; i++)
                s.Store(new JoinIndexHarness.Context { Code = "k" + i, Country = "a", Name = "n", Rank = 1, IsActive = true, Title = "t" }, $"contexts/{i}");
        });
        h.Save(s =>
        {
            for (int i = 0; i < 2000; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + i % 200, Title = "t" }, $"conversations/{i}");
        });
        await h.AssertCurrentAsync();
        JoinIndex joinIndex = h.Join();
        long before = joinIndex.Counters.ListFlushes;
        ChangeCountryOfEveryInnerDocument(h);
        await h.AssertCurrentAsync();
        Assert.True(ReferenceEquals(h.Join(), joinIndex), "the join index was reset");
        return joinIndex.Counters.ListFlushes - before;
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheQueuedListChangesLimitSetsHowOftenTheListsAreFlushed()
    {
        if (this is IIndirectModeTest)
        {
            // an Indirect refresh makes one change of a list for a pair of a key and an atom, not for every outer entry of the key: 400 changes here, which a queue of the least size holds
            Assert.True(await FlushesOfTheCostlyInnerChangeAsync(queued: 1024) <= 1);
            return;
        }

        // the change is 4,000 changes of lists: a limit of 1,024 flushes it three times at least, a limit above it once
        long often = await FlushesOfTheCostlyInnerChangeAsync(queued: 1024);
        long rarely = await FlushesOfTheCostlyInnerChangeAsync(queued: 1_000_000);
        Assert.True(often >= 3, $"{often} flushes");
        Assert.True(rarely < often, $"{rarely} flushes against {often}");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheQueueOfListChangesIsLimitedByTheAvailableMemory()
    {
        (JoinIndexHarness h, JoinIndex joinIndex) = await CreateCostlyInnerChangeAsync(limit: 0);
        using (h)
        {
            // a change is 128 bytes, a quarter of the available memory is the queue, and the queue is never shorter than 262,144 changes while 64 MiB are available and half of the available memory in changes under that, never fewer than 1,024 (Join.MinQueuedListChanges is the queue only when the available memory is not known)
            long available = 64L * 1024 * 1024 * 1024;
            joinIndex.Engine.AvailableMemoryBytes = () => available;
            foreach ((long bytes, long expected) in new[] { (64L << 30, 4_194_304L), (1L << 30, 2_097_152L), (2000L << 20, 4_096_000L), (400L << 20, 819_200L), (100L << 20, 262_144L), (10L << 20, 40_960L), (1L << 20, 4_096L), (100L << 10, 1_024L) })
            {
                available = bytes;
                h.PutConversation(5000 + (int)(bytes >> 20), "k1");
                await h.AssertCurrentAsync();
                Assert.Equal(expected, joinIndex.Counters.LastQueueLimit);
            }
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheQueueOfABuildFollowsTheAvailableMemoryLikeTheQueueOfARefresh()
    {
        (JoinIndexHarness h, JoinIndex joinIndex) = await CreateCostlyInnerChangeAsync(limit: 0);
        using (h)
        {
            // the build asks for the limit with its own cap: a quarter of the available memory in changes of 128 bytes, never fewer than the shortest queue of that memory (or than the cap itself)
            const int BuildCap = 524_288;
            foreach ((long bytes, long expected) in new[] { (64L << 30, (long)BuildCap), (128L << 20, 262_144L), (10L << 20, 40_960L), (1L << 20, 4_096L) })
            {
                joinIndex.Engine.AvailableMemoryBytes = () => bytes;
                Assert.Equal(expected, joinIndex.Engine.QueueLimit(BuildCap));
            }

            // a cap below the floor is the cap
            joinIndex.Engine.AvailableMemoryBytes = () => 1L << 20;
            Assert.Equal(4096, joinIndex.Engine.QueueLimit(4096));
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ALackOfMemoryShortensTheQueueButDoesNotBuildTheListsAgain()
    {
        // 2 list changes for each outer entry are within the limit of 2: with almost no memory available the queue is at its shortest and the change is still applied, not built again
        (JoinIndexHarness h, JoinIndex before) = await CreateCostlyInnerChangeAsync(limit: 2);
        using (h)
        {
            before.Engine.AvailableMemoryBytes = () => 10L << 20;
            ChangeCountryOfEveryInnerDocument(h);
            await h.AssertCurrentAsync();
            Assert.True(ReferenceEquals(h.Join(), before), "the join index was reset");
            Assert.Equal(40_960, before.Counters.LastQueueLimit);
            AssertNoRebuild(before);
            Assert.Equal(1, before.Counters.Builds);
        }
    }
}

/// <summary>The tests of the class with the join indexes on a field in the storage mode Indirect.</summary>
public class JoinIndexChunkTestsIndirect(ITestOutputHelper output) : JoinIndexChunkTests(output), IIndirectModeTest;
