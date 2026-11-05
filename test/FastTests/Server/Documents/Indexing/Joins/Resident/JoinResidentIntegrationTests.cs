using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Corax.Mappings;
using Corax.Querying.Matches.Meta;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Voron;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Sparrow.Server;
using Tests.Infrastructure;
using Voron.Data.CompactTrees;
using Voron.Util;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

/// <summary>
/// The resident key-list cache wired into an Indirect join index: the epoch is the outer snapshot of the refresh, a reader of another snapshot reads the store, the changes of a refresh are applied only after its commit,
/// a partial refresh and a restart leave an empty cache that is still valid, and every resident list is the posting list of its key in the snapshot of the epoch.
/// </summary>
public class JoinResidentIntegrationTests(ITestOutputHelper output) : RavenTestBase(output), IIndirectModeTest
{
    private const string Order = " order by m.Title limit 0, 50";

    private static readonly string[] Subs =
    [
        "",
        " where Country = 'nl'",
        " where Country != 'nl'",
        " where IsActive = true and Name = 'n1'",
        " where Country = 'nl' or Name = 'n3'",
        " where Rank > 20"
    ];

    private static string FieldJoin(string sub, string tail = Order) => $"{Outer} join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code{tail}";

    private static string FieldLeftJoin(string sub, string tail = Order) => $"{Outer} left join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code{tail}";

    private static Task<JoinIndexHarness> CreateAsync(RavenTestBase test, Dictionary<string, string> settings = null, bool arrays = false) => JoinIndexHarness.CreateFieldJoinAsync(test, settings, arrays: arrays);

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 1000 && condition() == false; i++)
            await Task.Delay(20);

        Assert.True(condition(), what);
    }

    private static async Task WaitForEpochAsync(JoinIndexHarness h)
    {
        await WaitForAsync(() => h.Join().Resident is { } resident && resident.Cache.Epoch.IsNone == false, "the cache has an epoch");
    }

    private static void Seed(JoinIndexHarness h, int contexts = 8, int conversations = 60)
    {
        for (int i = 0; i < contexts; i++)
            h.PutCodedContext(i, "k" + i % 6, i % 2 == 0 ? "nl" : "de", "n" + i % 4, i * 5, i % 3 != 0, "w" + i);

        for (int i = 0; i < conversations; i++)
            h.PutConversation(i, "k" + i % 7, i % 3 == 0 ? "a" : "b");
    }

    private static void CheckAll(JoinIndexQueryOracle oracle)
    {
        foreach (string sub in Subs)
        {
            oracle.Check(FieldJoin(sub));
            oracle.Check(FieldLeftJoin(sub));
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ACacheThatIsWarmServesTheJoinFromItsListsAndTheAnswersAreExact()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();

        // a cache that is made when the file already holds the keys (the first run after the open of a database) is warmed from the any list
        JoinIndexHarness.UseResident(h.Join(), ResidentMode.Default);
        await WaitForAsync(() => h.Join().Resident is { } resident && resident.Cache.Epoch.IsNone == false && resident.Cache.GetStats().WarmUpKeys > 0, "the warm-up loaded the lists");
        var oracle = new JoinIndexQueryOracle(h);

        CheckAll(oracle);

        JoinIndexStats stats = oracle.Info;
        Assert.True(stats.ResidentKeys > 0, "no key list is resident");
        Assert.True(stats.ResidentBytes > 0);
        Assert.True(stats.ResidentWarmUpKeys > 0, "the warm-up loaded nothing");
        Assert.True(stats.ResidentHits > 0, "no list was read from the cache");
        Assert.Equal(0, stats.ResidentEpochMisses);
        Assert.Equal(0, stats.ResidentMisses);
        Assert.True(await h.AssertResidentAsync(atLeast: 1) > 0);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task WithoutACacheTheStoreServesTheSameAnswers()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();
        JoinIndexHarness.UseResident(h.Join(), ResidentMode.Off);
        Assert.Null(h.Join().Resident);

        CheckAll(new JoinIndexQueryOracle(h));
        Assert.Equal(0, h.Stats().Join.ResidentBytes);
        Assert.Null(h.Join().Resident);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task EveryRefreshMovesTheEpochToItsOuterSnapshotAndPatchesTheLists()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();
        await WaitForEpochAsync(h);
        var oracle = new JoinIndexQueryOracle(h);
        CheckAll(oracle);
        await h.AssertResidentAsync(atLeast: 1);

        JoinResident resident = h.Join().Resident;
        long applied = resident.PatchesApplied;
        ResidentEpoch before = resident.Cache.Epoch;

        // a move to another key, an insert, a delete and a re-key of the same document
        h.PutConversation(1, "k3", "moved");
        h.PutConversation(100, "k1", "new");
        h.Delete("conversations/2");
        h.PutConversation(4, "k5", "again");
        await h.AssertCurrentAsync();
        await WaitForAsync(() => resident.PatchesApplied > applied && resident.Cache.Epoch.TransactionId != before.TransactionId, "the patch of the refresh was applied");

        Assert.Equal(0, resident.PatchEpochMismatches);
        await h.AssertResidentAsync(atLeast: 1);
        CheckAll(oracle);
        Assert.Equal(0, oracle.Info.ResidentEpochMisses);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AReaderOfAnotherOuterSnapshotReadsTheStoreAndIsExact()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();
        await WaitForEpochAsync(h);
        JoinIndex joinIndex = h.Join();
        JoinResident resident = joinIndex.Resident;
        CheckAll(new JoinIndexQueryOracle(h));
        await h.AssertResidentAsync(atLeast: 1);

        // a reader opens the outer index now: it sees the entries as they are before the next write
        Raven.Server.Documents.Indexes.Index outerIndex = h.Database.IndexStore.GetIndex(JoinIndexHarness.OuterName);
        Assert.True(outerIndex.TryOpenSnapshot(out IndexSnapshot older));
        using (older)
        {
            ResidentEpoch epoch = resident.Cache.Epoch;

            long applied = resident.PatchesApplied;
            h.PutConversation(1, "k3", "moved");
            h.PutConversation(100, "k1", "new");
            h.Delete("conversations/2");
            await h.AssertCurrentAsync();
            await WaitForAsync(() => resident.PatchesApplied > applied, "the cache moved to the newer snapshot");
            Assert.NotEqual(epoch.TransactionId, resident.Cache.Epoch.TransactionId);

            Assert.True(joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot file));
            using (file)
            {
                long[] keys = KeysOf(file);
                Assert.NotEmpty(keys);

                // the older reader: the epoch is not its snapshot, every list comes from the store, which is its own snapshot
                (long[] ids, ResidentUnion union) = Union(older, file, resident, keys, older.Transaction.LowLevelTransaction.Id);
                Assert.False(union.EpochHit);
                Assert.Equal(0, union.KeysFromCache);
                Assert.Equal(StoreRows(older, file, keys), ids);

                // a reader that says it is the newest snapshot is the one the cache is for: it differs from the older reader's rows, so the epoch check is what separates them
                Assert.True(outerIndex.TryOpenSnapshot(out IndexSnapshot current));
                using (current)
                {
                    (long[] newest, ResidentUnion hit) = Union(current, file, resident, keys, current.Transaction.LowLevelTransaction.Id);
                    Assert.True(hit.EpochHit);
                    Assert.Equal(StoreRows(current, file, keys), newest);
                    Assert.NotEqual(newest, ids);
                }
            }
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ACacheThatWasReplacedIsEmptyHasNoEpochAndIsEstablishedByTheNextRun()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();
        await WaitForEpochAsync(h);
        var oracle = new JoinIndexQueryOracle(h);
        CheckAll(oracle);

        // a restart: nothing of the cache is durable
        JoinIndexHarness.UseResident(h.Join(), ResidentMode.On);
        CheckAll(oracle);

        await WaitForAsync(() => h.Join().Resident is { } resident && resident.Cache.Epoch.IsNone == false && resident.Cache.GetStats().WarmUpKeys > 0, "the warm-up loaded the lists");
        Assert.True(h.Join().Resident.Cache.ResidentKeys > 0);
        CheckAll(oracle);
        await h.AssertResidentAsync(atLeast: 1);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AColdCacheIsWarmedBeforeTheCommitOfARefreshWhoseChangesAPatchCanHold()
    {
        const int rows = 3;
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();
        JoinIndex joinIndex = h.Join();
        joinIndex.UseResidentCache(new ResidentCacheOptions { BudgetBytes = long.MaxValue / 4, Admission = ResidentAdmission.Always, MaxPatchDocuments = 5 });
        await WaitForAsync(() => joinIndex.Resident is { } resident && resident.Cache.Epoch.IsNone == false && resident.WarmUps > 0, "the cache was warmed");
        var oracle = new JoinIndexQueryOracle(h);
        CheckAll(oracle);

        // the cache is cold again (an epoch that was dropped); the next refresh establishes one
        JoinResident cache = joinIndex.Resident;
        cache.Cache.Invalidate();
        long warmedBefore = cache.WarmUps;
        long warmedAtTheCommit = -1;
        joinIndex.ForTestingPurposes = phase =>
        {
            if (phase == "BeforeIndexCommit")
                Interlocked.CompareExchange(ref warmedAtTheCommit, cache.WarmUps, -1);
        };

        h.Save(s =>
        {
            for (int i = 0; i < rows; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + i % 5, Title = "bulk" }, $"conversations/{200 + i}");
        });
        await h.AssertCurrentAsync();
        await WaitForAsync(() => Interlocked.Read(ref warmedAtTheCommit) >= 0, "the refresh reached its commit");
        joinIndex.ForTestingPurposes = null;

        Assert.True(warmedAtTheCommit > warmedBefore, "a refresh that a patch can hold did not warm the cache");

        // the refresh after it establishes the cache again and loads the lists
        await WaitForAsync(() => cache.Cache.Epoch.IsNone == false && cache.WarmUps > warmedBefore, "the cache was warmed by the refresh after it");
        CheckAll(oracle);
        await h.AssertResidentAsync(atLeast: 1);
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData("BeforeCommit")]
    [InlineData("BeforeIndexCommit")]
    public async Task AChangeOfTheOuterRowsThatWasRolledBackLeavesTheCacheAsItWasAndTheRetryIsExact(string failAt)
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h);
        await h.AssertCurrentAsync();
        await WaitForEpochAsync(h);
        var oracle = new JoinIndexQueryOracle(h);
        CheckAll(oracle);
        await h.AssertResidentAsync(atLeast: 1);
        JoinIndex joinIndex = h.Join();
        JoinResident resident = joinIndex.Resident;

        // the first attempt of the refresh fails before its commit, after it changed everything it was going to; the documents change again, and the outer index has them, before the retry
        int failed = 0;
        joinIndex.ForTestingPurposes = phase =>
        {
            if (phase != failAt || Interlocked.Increment(ref failed) != 1)
                return;

            h.PutConversation(1, "k4", "moved again");
            h.Delete("conversations/100");
            h.PutConversation(101, "k2", "other");
            for (int i = 0; i < 500 && h.Store.Maintenance.Send(new GetIndexStatisticsOperation(JoinIndexHarness.OuterName)).IsStale; i++)
                Thread.Sleep(20);

            throw new InvalidOperationException("the refresh failed before its commit");
        };

        h.PutConversation(1, "k3", "moved");
        h.PutConversation(100, "k1", "new");
        h.Delete("conversations/2");
        await WaitForAsync(() => Volatile.Read(ref failed) >= 1, "the refresh did not fail");
        await Task.Delay(200);
        joinIndex.ForTestingPurposes = null;

        if (h.Stats().State is IndexState.Error)
            await h.Store.Maintenance.SendAsync(new EnableIndexOperation(JoinIndexHarness.JoinName));

        await h.AssertCurrentAsync();
        await h.AssertResidentAsync(atLeast: 1);
        CheckAll(oracle);
        Assert.True(resident == joinIndex.Resident || joinIndex.Resident == null, "the cache object was replaced");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task APartialRefreshHasNoEpochAndTheLastChunkEstablishesOne()
    {
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = "10" };
        using JoinIndexHarness h = await CreateAsync(this, settings);
        Seed(h);
        await h.AssertCurrentAsync();
        await WaitForEpochAsync(h);
        var oracle = new JoinIndexQueryOracle(h);
        CheckAll(oracle);
        JoinIndex joinIndex = h.Join();
        JoinResident resident = joinIndex.Resident;
        long partials = joinIndex.Counters.PartialRefreshes;
        long drops = resident.PartialDrops;

        // 60 documents against a budget of 10 for a commit: six chunks, between them the cache has no epoch and every answer comes from the store
        int seen = 0;
        using var reached = new SemaphoreSlim(0);
        using var go = new SemaphoreSlim(0);
        joinIndex.ForTestingPurposes = phase =>
        {
            if (phase != "Discovered" || Interlocked.Increment(ref seen) != 2)
                return;

            reached.Release();
            go.Wait(TimeSpan.FromSeconds(30));
        };

        h.Save(s =>
        {
            for (int i = 0; i < 60; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = "k" + (i + 3) % 7, Title = "again" }, $"conversations/{i}");
        });

        Assert.True(reached.Wait(TimeSpan.FromSeconds(30)), "the join index never reached its second commit");
        Assert.True(joinIndex.Counters.PartialRefreshes > partials);

        // the first chunk was cut on the outer side: its commit dropped the epoch, and the second chunk (open now) established an empty one at its own snapshot
        Assert.True(resident.PartialDrops > drops, "the partial commit did not drop the epoch");
        oracle.ForceFallback(false);
        QueryAnswer served = oracle.Run(FieldJoin("", Order));
        oracle.ForceFallback(true);
        QueryAnswer scan = oracle.Run(FieldJoin("", Order));
        oracle.ForceFallback(false);
        AssertSame("partial", served, scan);

        go.Release(100);
        joinIndex.ForTestingPurposes = null;
        await h.AssertCurrentAsync();
        await WaitForEpochAsync(h);
        await h.AssertResidentAsync(atLeast: 1);
        CheckAll(oracle);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ARowThatKeepsItsEntryIdOrGetsAnotherIsPatchedInTheListsOfBothKeys()
    {
        using JoinIndexHarness h = await CreateAsync(this, arrays: true);
        h.PutCodedContextKeys(1, ["k1", "k2"], "nl", "n1", 1, true, "x");
        h.PutCodedContextKeys(2, ["k3"], "de", "n2", 2, true, "y");
        h.PutCodedContextKeys(3, ["k4", "k1"], "nl", "n3", 3, true, "z");
        for (int i = 0; i < 12; i++)
            h.PutConversationKeys(i, i % 3 == 0 ? ["k1", "k2"] : i % 3 == 1 ? ["k3", "k4"] : ["k1"], "a");

        await h.AssertCurrentAsync();
        await WaitForEpochAsync(h);
        var oracle = new JoinIndexQueryOracle(h);
        string[] queries = [FieldJoin(""), FieldJoin(" where Country = 'nl'"), FieldJoin(" where Name = 'n2'"), FieldLeftJoin(" where Country = 'nl'")];
        foreach (string query in queries)
            oracle.Check(query);

        await h.AssertResidentAsync(atLeast: 1);

        // the same document loses one key and keeps one, gets a key, loses all of them, comes back; others move across the two keys of the first
        string[][] steps =
        [
            ["k1"], ["k1", "k3"], ["k2", "k4"], [], ["k4"], ["k1", "k2", "k3", "k4"], ["k3", "k3"], ["k2"]
        ];

        foreach (string[] keys in steps)
        {
            h.PutConversationKeys(0, keys, "step");
            h.PutConversationKeys(3, keys.Reverse().ToArray(), "step");
            h.Delete("conversations/6");
            h.PutConversationKeys(6, ["k1", "k4"], "back");
            await h.AssertCurrentAsync();
            await h.AssertResidentAsync(atLeast: 1);
            foreach (string query in queries)
                oracle.Check(query);
        }

        Assert.Equal(0, h.Join().Resident.PatchEpochMismatches);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ARandomWorkloadWithRowsOfSeveralKeysKeepsEveryResidentListExact(int seed)
    {
        using JoinIndexHarness h = await CreateAsync(this, arrays: true);
        var random = new Random(seed);
        var oracle = new JoinIndexQueryOracle(h);
        int checks = 0;
        for (int round = 0; round < 10; round++)
        {
            h.RandomFieldArrayRound(random, 12, outerKeys: 3, innerKeys: 3);
            await h.AssertCurrentAsync();
            if (round % 3 == 1)
                continue;

            await h.AssertResidentAsync();
            foreach (string sub in Subs.Where((_, i) => (i + round) % 2 == 0))
                oracle.Check(FieldJoin(sub));

            checks++;
        }

        Assert.True(checks > 0);
        await WaitForEpochAsync(h);
        Assert.True(await h.AssertResidentAsync(atLeast: 0) >= 0);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ReadersAndAWriterThatTheJoinFollowsKeepTheAnswersOfTheStableRowsExact()
    {
        using JoinIndexHarness h = await CreateAsync(this);
        Seed(h, contexts: 10, conversations: 0);
        for (int i = 0; i < 40; i++)
            h.PutConversation(i, "k" + i % 6, "stable");

        await h.AssertCurrentAsync();
        await WaitForEpochAsync(h);

        // the readers ask for the stable rows only: the rows the writer changes are never in the answer, but their lists are patched, evicted and re-read under the readers all the time
        string[] subs = ["", " where Country = 'nl'", " where Country != 'nl'", " where IsActive = true or Name = 'n3'"];
        var expected = new Dictionary<string, List<string>>();
        var oracle = new JoinIndexQueryOracle(h);
        foreach (string sub in subs)
        {
            string rql = FieldJoin(sub, " where m.Title = 'stable' order by m.Title limit 0, 100");
            (QueryAnswer served, QueryAnswer scan) = oracle.Both(rql);
            AssertSame(rql, served, scan);
            expected[rql] = scan.Rows.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        using var stop = new CancellationTokenSource();
        var failures = new List<string>();
        long reads = 0;
        Task writer = Task.Run(async () =>
        {
            var random = new Random(7);
            for (int i = 0; stop.IsCancellationRequested == false && i < 150; i++)
            {
                int id = 1000 + random.Next(30);
                if (random.Next(4) == 0)
                    h.Delete($"conversations/{id}");
                else
                    h.PutConversation(id, "k" + random.Next(7), "churn");

                await Task.Delay(random.Next(15));
            }
        });

        Task[] readers = Enumerable.Range(0, 3).Select(r => Task.Run(() =>
        {
            var local = new JoinIndexQueryOracle(h);
            while (stop.IsCancellationRequested == false)
            {
                foreach ((string rql, List<string> ids) in expected)
                {
                    QueryAnswer answer = local.Run(rql);
                    Interlocked.Increment(ref reads);
                    List<string> got = answer.Rows.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    if (got.SequenceEqual(ids) == false)
                    {
                        lock (failures)
                            failures.Add($"{rql}: expected {ids.Count} rows, got {got.Count}: {string.Join(",", got.Except(ids).Take(5))} extra, {string.Join(",", ids.Except(got).Take(5))} missing");

                        stop.Cancel();
                        return;
                    }
                }
            }
        })).ToArray();

        await writer;
        await Task.Delay(100);
        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(Interlocked.Read(ref reads) > 20, $"{reads} reads");
        await h.AssertCurrentAsync();
        await WaitForEpochAsync(h);
        await h.AssertResidentAsync();
        JoinIndexStats stats = h.Stats().Join;
        Output.WriteLine($"reads {reads}, hits {stats.ResidentHits}, misses {stats.ResidentMisses}, epoch misses {stats.ResidentEpochMisses}, patches {stats.ResidentPatchesApplied}, mismatches {stats.ResidentPatchEpochMismatches}, invalidations {stats.ResidentInvalidations}, served {stats.Served}, fallbacks {stats.FallbackTotal}");
    }

    private static long[] KeysOf(JoinIndexSnapshot file)
    {
        var keys = new ContextBoundNativeList<long>(file.Transaction.Allocator);
        try
        {
            file.ReadAtomKeys(JoinReader.AtomOfAny(), ref keys);
            return keys.ToSpan().ToArray();
        }
        finally
        {
            keys.Dispose();
        }
    }

    private static (long[] Ids, ResidentUnion Union) Union(IndexSnapshot snapshot, JoinIndexSnapshot file, JoinResident resident, long[] keys, long transactionId)
    {
        var read = resident.ReadOf(snapshot.EnvironmentId, transactionId, file.Seed, 0, snapshot.Consumed);
        ResidentUnion captured = default;
        IQueryMatch match = JoinKeyUnion.Create(snapshot.Searcher, file, keys, "ContextId", read, union => captured = union);
        var ids = new List<long>();
        var buffer = new long[256];
        int count;
        while ((count = match.Fill(buffer)) > 0)
            ids.AddRange(buffer.AsSpan(0, count).ToArray());

        return (ids.ToArray(), captured);
    }

    private static long[] StoreRows(IndexSnapshot snapshot, JoinIndexSnapshot file, long[] keys)
    {
        Slice.From(snapshot.Searcher.Allocator, "ContextId", ByteStringType.Immutable, out Slice field);
        CompactTree tree = snapshot.Searcher.GetTermsFor(field);
        FieldMetadata metadata = snapshot.Searcher.FieldMetadataBuilder("ContextId");
        var rows = new ContextBoundNativeList<long>(snapshot.Searcher.Allocator);
        try
        {
            foreach (long keyRef in keys)
                JoinOuterKeyLists.ReadRowsOfKey(snapshot.Searcher, metadata, file, tree, keyRef, ref rows);

            long[] all = rows.ToSpan().ToArray();
            return all.Distinct().OrderBy(x => x).ToArray();
        }
        finally
        {
            rows.Dispose();
        }
    }
}

/// <summary>The random model against the cache modes: the cache is read every step, every third step (many patches between two reads), restarted between steps (an empty cache and a stopped and started join index, compared with a twin that never restarts) and tiny (evictions in the middle).</summary>
public class JoinResidentModelTests(ITestOutputHelper output) : RavenTestBase(output), IIndirectModeTest
{
    public enum Driver
    {
        EveryStep,
        EveryThirdStep,
        Restart,
        Tiny
    }

    private static readonly string[] Subs = ["", " where Country = 'nl'", " where Country != 'nl'", " where IsActive = true and Name = 'n1'", " where Country = 'nl' or Name = 'n3'", " where Rank > 20", " where IsActive != true"];

    private static string FieldJoin(string sub) => $"{Outer} join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code order by m.Title limit 0, 100";

    public static IEnumerable<object[]> Cases()
    {
        for (int seed = 1; seed <= 5; seed++)
        {
            foreach (Driver driver in Enum.GetValues<Driver>())
                yield return [seed, driver];
        }
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [MemberData(nameof(Cases))]
    public async Task EveryAnswerEqualsTheScanWhateverTheCacheDoes(int seed, Driver driver)
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, arrays: true);
        using JoinIndexHarness twin = driver == Driver.Restart ? await JoinIndexHarness.CreateFieldJoinAsync(this, arrays: true) : null;
        JoinIndexHarness.UseResident(h.Join(), driver == Driver.Tiny ? ResidentMode.Tiny : ResidentMode.On);
        var random = new Random(seed * 31 + (int)driver);
        var twinRandom = new Random(seed * 31 + (int)driver);
        var restartRandom = new Random(seed);
        var oracle = new JoinIndexQueryOracle(h);
        var twinOracle = twin == null ? null : new JoinIndexQueryOracle(twin);
        int reads = 0, restarts = 0;
        for (int round = 0; round < 9; round++)
        {
            h.RandomFieldArrayRound(random, 10, outerKeys: 3, innerKeys: 3);
            twin?.RandomFieldArrayRound(twinRandom, 10, outerKeys: 3, innerKeys: 3);
            await h.AssertCurrentAsync();
            if (twin != null)
                await twin.AssertCurrentAsync();

            if (driver == Driver.Restart && restartRandom.Next(3) == 0)
            {
                restarts++;
                await h.Store.Maintenance.SendAsync(new StopIndexOperation(JoinIndexHarness.JoinName));
                for (int i = 0; i < 500 && h.Stats().Status != IndexRunningStatus.Paused; i++)
                    await Task.Delay(20);

                JoinIndexHarness.UseResident(h.Join(), ResidentMode.On);
                await h.Store.Maintenance.SendAsync(new StartIndexOperation(JoinIndexHarness.JoinName));
                await h.AssertCurrentAsync();
            }

            if (driver == Driver.EveryThirdStep && round % 3 != 2)
                continue;

            reads++;
            foreach (string sub in Subs.Where((_, i) => (i + round) % 3 != 0))
            {
                string rql = FieldJoin(sub);
                oracle.Check(rql);
                if (twinOracle != null)
                {
                    twinOracle.Check(rql);
                    Assert.True(oracle.Run(rql).Rows.Select(x => x.Id).SequenceEqual(twinOracle.Run(rql).Rows.Select(x => x.Id)), $"the restarted join index and its twin answer differently: {rql}");
                }
            }

            await h.AssertResidentAsync();
        }

        Assert.True(reads > 0);
        if (driver == Driver.Restart)
            Assert.True(restarts > 0, "the join index was never restarted");

        await h.AssertCurrentAsync();
    }
}

public class JoinIndexFieldJoinQueryTestsIndirectCacheOn(ITestOutputHelper output) : JoinIndexFieldJoinQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.On; }

public class JoinIndexFieldJoinQueryTestsIndirectCacheTiny(ITestOutputHelper output) : JoinIndexFieldJoinQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.Tiny; }

public class JoinIndexFieldJoinQueryTestsIndirectCacheOff(ITestOutputHelper output) : JoinIndexFieldJoinQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.Off; }

public class JoinIndexFieldJoinGroupQueryTestsIndirectCacheOn(ITestOutputHelper output) : JoinIndexFieldJoinGroupQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.On; }

public class JoinIndexFieldJoinGroupQueryTestsIndirectCacheTiny(ITestOutputHelper output) : JoinIndexFieldJoinGroupQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.Tiny; }

public class JoinIndexFieldJoinMultiKeyQueryTestsIndirectCacheOn(ITestOutputHelper output) : JoinIndexFieldJoinMultiKeyQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.On; }

public class JoinIndexFieldJoinMultiKeyQueryTestsIndirectCacheTiny(ITestOutputHelper output) : JoinIndexFieldJoinMultiKeyQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.Tiny; }

public class JoinIndexFieldJoinMultiKeyQueryTestsIndirectCacheOff(ITestOutputHelper output) : JoinIndexFieldJoinMultiKeyQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.Off; }

public class JoinIndexFieldJoinMultiKeyGroupQueryTestsIndirectCacheOn(ITestOutputHelper output) : JoinIndexFieldJoinMultiKeyGroupQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.On; }

public class JoinIndexFieldJoinMultiKeyGroupQueryTestsIndirectCacheTiny(ITestOutputHelper output) : JoinIndexFieldJoinMultiKeyGroupQueryTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.Tiny; }
