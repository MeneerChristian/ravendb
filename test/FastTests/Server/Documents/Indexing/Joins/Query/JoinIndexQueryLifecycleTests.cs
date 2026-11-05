using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>What a query does when the join index is behind, ahead, being rebuilt, disabled or missing: it never changes the answer, only whether the join index serves it.</summary>
public class JoinIndexQueryLifecycleTests(ITestOutputHelper output) : RavenTestBase(output)
{
    // an Indirect file is exact for any outer snapshot, so no query waits for the outer index of the join index, and a query that uses the score of the join reads the file only for the very outer snapshot the file reflects
    private bool IsIndirect => this is IIndirectModeTest;

    private sealed record Counts(long Served, Dictionary<string, long> Fallbacks)
    {
        public static Counts Of(JoinIndex joinIndex) => new(joinIndex.QueryCounters.ServedCount, joinIndex.QueryCounters.Fallbacks.ToDictionary(x => x.Key, x => x.Value));

        public long FallbackTotal => Fallbacks.Values.Sum();

        public long FallbackOf(string reason) => Fallbacks.GetValueOrDefault(reason);

        public override string ToString() => $"served {Served}, fallbacks {string.Join(", ", Fallbacks.Select(x => $"{x.Key}:{x.Value}"))}";
    }

    /// <summary>The query was served by the join index and was not read by the server for any reason.</summary>
    private static void AssertServedOnce(JoinIndex joinIndex, Counts before)
    {
        Counts now = Counts.Of(joinIndex);
        Assert.True(now.Served == before.Served + 1 && now.FallbackTotal == before.FallbackTotal, $"expected one served query and no fallback: {before} -> {now}");
    }

    /// <summary>The query was read by the server for exactly this reason and was not served by the join index.</summary>
    private static void AssertFellBackOnce(JoinIndex joinIndex, Counts before, string reason)
    {
        Counts now = Counts.Of(joinIndex);
        Assert.True(now.Served == before.Served && now.FallbackOf(reason) == before.FallbackOf(reason) + 1 && now.FallbackTotal == before.FallbackTotal + 1, $"expected one {reason} fallback and no served query: {before} -> {now}");
    }

    /// <summary>
    /// An Expanded join index makes the query wait until it is current and serves it. An Indirect one does not wait for the outer index, and a query that scores by the join reads the file only for the very outer snapshot the file reflects:
    /// the join index that was not told about the outer batch (it cannot commit) does not answer, and the server reads the join (BehindTimeout).
    /// </summary>
    private void AssertAnsweredOnce(JoinIndex joinIndex, Counts before)
    {
        if (IsIndirect)
            AssertFellBackOnce(joinIndex, before, JoinFallbacks.BehindTimeout);
        else
            AssertServedOnce(joinIndex, before);
    }

    /// <summary>The refresh of an Indirect join index cannot commit until the returned gate is set, so that it does not reflect the outer batch that the test writes.</summary>
    private static ManualResetEventSlim HoldTheRefresh(JoinIndex joinIndex)
    {
        var gate = new ManualResetEventSlim(false);
        joinIndex.ForTestingPurposes = phase =>
        {
            if (phase == "BeforeCommit")
                gate.Wait(TimeSpan.FromSeconds(20));
        };

        return gate;
    }

    private static Dictionary<string, string> ScoreSettings() => new() { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };

    // a query that waits for the join index waits as long as the join index needs: whether it was served does not depend on the speed of the machine
    private static Dictionary<string, string> PatientSettings()
    {
        Dictionary<string, string> settings = ScoreSettings();
        settings[RavenConfiguration.GetKey(x => x.Join.QueryWait)] = "20000";
        return settings;
    }

    private async Task<JoinIndexHarness> HarnessWithDataAsync(int seed, int operations = 120, bool putJoin = true, bool autoJoins = true, Dictionary<string, string> baseSettings = null)
    {
        var settings = baseSettings ?? ScoreSettings();
        settings["Join.AutoEnabled"] = autoJoins.ToString();

        JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, settings, putJoin);
        h.RandomRound(new Random(seed), operations);
        if (putJoin)
            await h.AssertCurrentAsync();

        return h;
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 800 && condition() == false; i++)
            await Task.Delay(25);

        Assert.True(condition(), "timed out waiting for " + what);
    }

    // the two indexes that the join index joins, not the join index itself
    private static void WaitForTheJoinedIndexes(JoinIndexHarness h)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < until)
        {
            if (h.Store.Maintenance.Send(new GetIndexesStatisticsOperation()).Where(x => x.Name != JoinIndexHarness.JoinName).All(x => x.IsStale == false))
                return;

            Thread.Sleep(10);
        }
    }

    private static QueryAnswer RunWithoutTheJoin(JoinIndexQueryOracle oracle, string rql)
    {
        oracle.ForceFallback(true);
        try
        {
            return oracle.Run(rql);
        }
        finally
        {
            oracle.ForceFallback(false);
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task JoinThatIsBehindMakesTheQueryWaitThenReadTheJoinOfTheServer()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(4);
        var oracle = new JoinIndexQueryOracle(h);
        string rql = Join(" where Country = 'nl'", " order by score() limit 0, 1000");
        oracle.Check(rql);

        using var gate = new ManualResetEventSlim(false);
        h.Join().ForTestingPurposes = phase =>
        {
            if (phase == "BeforeCommit")
                gate.Wait(TimeSpan.FromSeconds(20));
        };

        h.PutConversation(500, "contexts/1");
        h.PutContext(1, "nl", "Alpha", 5, true, "moved");
        await Task.Run(() => WaitForTheJoinedIndexes(h));

        QueryAnswer scan = RunWithoutTheJoin(oracle, rql);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        QueryAnswer served = oracle.Run(rql);
        watch.Stop();

        AssertSame(rql, served, scan);
        Assert.True(oracle.FallbacksOf(JoinFallbacks.BehindTimeout) >= 1, string.Join(", ", oracle.Info.Fallbacks));
        Assert.True(oracle.Info.QueryWaits >= 1);
        Assert.True(watch.Elapsed >= oracle.Wait, "the query returned before the wait of the join index was over");

        gate.Set();
        h.Join().ForTestingPurposes = null;
        await h.AssertCurrentAsync();
        oracle.Check(rql);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task QueryThatWaitsForNonStaleResultsWaitsLongerForTheJoin()
    {
        // the budget of the wait is far above what the join index needs: only the join index decides when the query is served, not the speed of the machine
        Dictionary<string, string> settings = ScoreSettings();
        settings[RavenConfiguration.GetKey(x => x.Join.QueryWaitForNonStale)] = "60000";
        using JoinIndexHarness h = await HarnessWithDataAsync(4, baseSettings: settings);
        var oracle = new JoinIndexQueryOracle(h);
        string rql = Join(" where Country = 'nl'", " order by score() limit 0, 1000");
        oracle.Check(rql);
        Assert.True(oracle.WaitForNonStale > TimeSpan.FromMilliseconds(400));
        Assert.True(oracle.Wait < TimeSpan.FromMilliseconds(400));

        using var gate = new ManualResetEventSlim(false);
        JoinIndex joinIndex = h.Join();
        joinIndex.ForTestingPurposes = phase =>
        {
            if (phase == "BeforeCommit")
                gate.Wait(TimeSpan.FromSeconds(60));
        };

        h.PutContext(1, "nl", "Alpha", 6, true, "again");
        await Task.Run(() => WaitForTheJoinedIndexes(h));

        Counts before = Counts.Of(joinIndex);
        long peeks = joinIndex.QueryCounters.PeekCount;
        Task<QueryAnswer> query = Task.Run(() => oracle.Run(rql, waitForNonStale: true));

        // the query found the join index behind and waits for it; it is held for longer than a query that does not wait for non stale results waits
        await WaitForAsync(() => joinIndex.QueryCounters.PeekCount > peeks, "the query to find the join index behind");
        await Task.Delay(oracle.Wait * 2);
        Assert.False(query.IsCompleted);

        gate.Set();
        QueryAnswer answer = await query;

        AssertServedOnce(joinIndex, before);
        Assert.True(joinIndex.QueryCounters.WaitMaxInMs >= oracle.Wait.TotalMilliseconds, "the query did not wait for the join index");
        joinIndex.ForTestingPurposes = null;
        AssertSame(rql, answer, RunWithoutTheJoin(oracle, rql));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task QueryThatWaitsForNonStaleResultsReadsTheJoinOfTheServerWhenTheJoinStaysBehindBeyondItsBudget()
    {
        Dictionary<string, string> settings = ScoreSettings();
        settings[RavenConfiguration.GetKey(x => x.Join.QueryWaitForNonStale)] = "300";
        using JoinIndexHarness h = await HarnessWithDataAsync(4, baseSettings: settings);
        var oracle = new JoinIndexQueryOracle(h);
        string rql = Join(" where Country = 'nl'", " order by score() limit 0, 1000");
        oracle.Check(rql);

        // the join index cannot commit before the query returns, whatever the speed of the machine
        using var gate = new ManualResetEventSlim(false);
        JoinIndex joinIndex = h.Join();
        joinIndex.ForTestingPurposes = phase =>
        {
            if (phase == "BeforeCommit")
                gate.Wait(TimeSpan.FromSeconds(60));
        };

        h.PutContext(1, "nl", "Alpha", 6, true, "again");
        await Task.Run(() => WaitForTheJoinedIndexes(h));

        Counts before = Counts.Of(joinIndex);
        QueryAnswer answer = oracle.Run(rql, waitForNonStale: true);

        AssertFellBackOnce(joinIndex, before, JoinFallbacks.BehindTimeout);
        gate.Set();
        joinIndex.ForTestingPurposes = null;
        await h.AssertCurrentAsync();
        AssertSame(rql, answer, RunWithoutTheJoin(oracle, rql));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task JoinThatIsAheadOfTheIndexesTheQueryReadsIsNotUsed()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(6);
        var oracle = new JoinIndexQueryOracle(h);
        string rql = Join(" where Country = 'nl'", " order by score() limit 0, 1000");

        // what the query would see if the join index were opened after the indexes: a newer snapshot than the ones the indexes of the attempt read
        int fired = 0;
        h.Join().QueryHooks = new JoinIndexQueryHooks
        {
            BeforeCompare = entry =>
            {
                if (Interlocked.Increment(ref fired) > 1)
                    return;

                long serial = entry.Snapshot.Generation.Serial;
                h.PutConversation(900 + fired, "contexts/does-not-exist");
                for (int i = 0; i < 400; i++)
                {
                    if (entry.Index.TryOpenJoinSnapshot(out JoinIndexSnapshot newer))
                    {
                        if (newer.Generation.Serial > serial)
                        {
                            entry.Replace(newer);
                            return;
                        }

                        newer.Dispose();
                    }

                    Thread.Sleep(25);
                }

                Assert.Fail("the join index did not publish");
            }
        };

        QueryAnswer served = oracle.Run(rql);
        h.Join().QueryHooks = null;
        await h.AssertCurrentAsync();
        QueryAnswer scan = RunWithoutTheJoin(oracle, rql);

        AssertSame(rql, served, scan);
        Assert.Equal(IsIndirect ? 0 : 1, oracle.FallbacksOf(JoinFallbacks.Ahead));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ResetAndDeleteOfAnIndexDuringQueriesNeverBreakAnAnswer()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(8);
        var oracle = new JoinIndexQueryOracle(h);
        string rql = Join(" where Country = 'nl'", " order by score() limit 0, 1000");
        oracle.Check(rql);

        var errors = new List<string>();
        using var stop = new CancellationTokenSource();
        Task readers = Task.Run(() =>
        {
            while (stop.IsCancellationRequested == false)
            {
                try
                {
                    oracle.Run(rql);
                }
                catch (Exception e) when (e.Message.Contains("Index") || e.Message.Contains("index"))
                {
                    // an index that is being reset or deleted may refuse the query, as it does without a join index
                }
                catch (Exception e)
                {
                    lock (errors)
                        errors.Add(e.GetType().Name + ": " + e.Message);
                }
            }
        });

        foreach (string index in new[] { JoinIndexHarness.OuterName, JoinIndexHarness.InnerName })
        {
            await h.Store.Maintenance.SendAsync(new ResetIndexOperation(index));
            await WaitForAsync(() => h.Join() is { } joinIndex && joinIndex.Status == IndexRunningStatus.Running && joinIndex.Counters.Builds >= 1, $"rebuild after the reset of {index}");
            await h.AssertCurrentAsync();
            oracle.Check(rql);
        }

        await h.Store.Maintenance.SendAsync(new DeleteIndexOperation(JoinIndexHarness.InnerName));
        await Task.Delay(300);
        await h.Store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndexHarness.InnerIndex()));
        await h.AssertCurrentAsync();

        stop.Cancel();
        await readers;
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Take(5)));
        oracle.Check(rql);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task JoinThatIsMissingOrDisabledIsNotUsed()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(9, 80, putJoin: false, autoJoins: false);
        string rql = Join(" where Country = 'nl'", " order by score() limit 0, 1000");

        // no join index at all
        Indexes.WaitForIndexing(h.Store);
        QueryAnswer plain = (await QueryWithoutAJoinAsync(h, rql));
        Assert.NotEmpty(plain.Rows);

        await h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition());
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        oracle.Check(rql);

        await h.Store.Maintenance.SendAsync(new DisableIndexOperation(JoinIndexHarness.JoinName));
        long served = oracle.Info.Served;
        AssertSame(rql, oracle.Run(rql), plain);
        Assert.Equal(served, oracle.Info.Served);
        Assert.True(oracle.FallbacksOf(JoinFallbacks.Disabled) >= 1, string.Join(", ", oracle.Info.Fallbacks));

        await h.Store.Maintenance.SendAsync(new EnableIndexOperation(JoinIndexHarness.JoinName));
        await h.AssertCurrentAsync();
        oracle.Check(rql);
    }

    private static Task<QueryAnswer> QueryWithoutAJoinAsync(JoinIndexHarness h, string rql)
    {
        using IDocumentSession session = h.Store.OpenSession(new SessionOptions { NoCaching = true });
        var rows = new List<QueryRow>();
        List<JoinIndexHarness.Conversation> documents = session.Advanced.RawQuery<JoinIndexHarness.Conversation>(rql).Statistics(out QueryStatistics stats).ToList();
        foreach (JoinIndexHarness.Conversation document in documents)
        {
            IMetadataDictionary metadata = session.Advanced.GetMetadataFor(document);
            rows.Add(new QueryRow((string)metadata["@id"], Convert.ToDouble(metadata["@index-score"])));
        }

        return Task.FromResult(new QueryAnswer(rows, stats.TotalResults, 0, stats.IsStale, new Dictionary<string, long>()));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task StatisticsOfTheIndexShowWhatTheQueriesDid()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(11);
        var oracle = new JoinIndexQueryOracle(h);
        oracle.Check(Join(" where Country = 'nl'", " limit 0, 10"));
        oracle.Run(Join(" where Rank > 3", " limit 0, 10"));

        JoinIndexStats stats = oracle.Info;
        Assert.True(stats.Served >= 1);
        Assert.True(stats.Fallbacks.GetValueOrDefault("KindNotServed:Range") >= 1);
        Assert.Equal(stats.Fallbacks.Values.Sum(), stats.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TimingsOfTheQueryCountTheJoinAndTheFallbackReason()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(12);
        var oracle = new JoinIndexQueryOracle(h);

        QueryAnswer served = oracle.Run(Join(" where Country = 'nl'", " limit 0, 10"));
        Assert.Equal(1, served.Counters.GetValueOrDefault("Join/c/JoinIndexServed"));

        QueryAnswer fallback = oracle.Run(Join(" where Rank > 3", " limit 0, 10"));
        Assert.Equal(1, fallback.Counters.GetValueOrDefault("Join/c/JoinFallback:KindNotServed:Range"));
        Assert.Equal(0, fallback.Counters.GetValueOrDefault("Join/c/JoinIndexServed"));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task RepeatedQueryIsNotModifiedUntilTheAnswerOfTheJoinChanges()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        var oracle = new JoinIndexQueryOracle(h);
        h.PutContext(1, "nl", "a", 1, true, "x");
        h.PutContext(2, "de", "b", 2, true, "x");
        for (int i = 0; i < 6; i++)
            h.PutConversation(i, $"contexts/{1 + i % 2}", "t");

        await h.AssertCurrentAsync();

        var statuses = new List<HttpStatusCode>();
        using var client = new DocumentStore { Urls = h.Store.Urls, Database = h.Store.Database };
        client.OnSucceedRequest += (_, args) => statuses.Add(args.Response.StatusCode);
        client.Initialize();

        string rql = Join(" where Country = 'nl'", " order by score() limit 0, 20");
        List<string> first = Ids(client, rql);
        Assert.Equal(3, first.Count);
        Assert.True(oracle.Info.Served >= 1);

        int requests = statuses.Count;
        Assert.Equal(first, Ids(client, rql));
        Assert.Equal(HttpStatusCode.NotModified, statuses[requests]);

        // the joined document changes: the answer changes and so does its etag
        h.PutContext(2, "nl", "b", 2, true, "x");
        await h.AssertCurrentAsync();
        requests = statuses.Count;
        List<string> second = Ids(client, rql);
        Assert.Equal(HttpStatusCode.OK, statuses[requests]);
        Assert.Equal(6, second.Count);

        requests = statuses.Count;
        Assert.Equal(second, Ids(client, rql));
        Assert.Equal(HttpStatusCode.NotModified, statuses[requests]);

        // the main documents change
        h.PutConversation(9, "contexts/1", "t");
        await h.AssertCurrentAsync();
        requests = statuses.Count;
        Assert.Equal(7, Ids(client, rql).Count);
        Assert.Equal(HttpStatusCode.OK, statuses[requests]);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AQueryBetweenTheCommitOfAnUpstreamBatchAndTheNoticeOfTheJoinWaitsForTheJoin()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(7, baseSettings: PatientSettings());
        var oracle = new JoinIndexQueryOracle(h);
        string rql = Join(" where Country = 'nl'", " order by score() limit 0, 20");
        oracle.Check(rql);

        using ManualResetEventSlim hold = IsIndirect ? HoldTheRefresh(h.Join()) : null;
        using var committed = new ManualResetEventSlim(false);
        using var gate = new ManualResetEventSlim(false);
        bool armed = false;
        void OnChange(IndexChange change)
        {
            if (armed && change.Type == IndexChangeTypes.BatchCompleted && string.Equals(change.Name, JoinIndexHarness.OuterName, StringComparison.OrdinalIgnoreCase))
            {
                committed.Set();
                gate.Wait(TimeSpan.FromSeconds(20));
            }
        }

        // the outer index has committed its batch, and the join index is not told about it before the gate opens
        h.Database.Changes.OnIndexChange += OnChange;
        try
        {
            armed = true;
            h.PutConversation(501, "contexts/1");
            Assert.True(committed.Wait(TimeSpan.FromSeconds(20)));

            JoinIndex joinIndex = h.Join();
            Counts before = Counts.Of(joinIndex);
            Task query = Task.Run(() => oracle.Run(rql));
            await Task.Delay(30);
            gate.Set();
            await query;

            AssertAnsweredOnce(joinIndex, before);
        }
        finally
        {
            armed = false;
            gate.Set();
            hold?.Set();
            h.Database.Changes.OnIndexChange -= OnChange;
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AQueryThatFindsTheJoinBehindDoesNotWaitForTheEndOfTheRefreshWindow()
    {
        Dictionary<string, string> settings = PatientSettings();
        settings[RavenConfiguration.GetKey(x => x.Join.RefreshWindow)] = "3000";
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, settings);
        h.RandomRound(new Random(8), 60);
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        string rql = Join(" where Country = 'nl'", " order by score() limit 0, 20");
        oracle.Check(rql);

        JoinIndex joinIndex = h.Join();
        using ManualResetEventSlim hold = IsIndirect ? HoldTheRefresh(joinIndex) : null;
        Counts before = Counts.Of(joinIndex);
        h.PutConversation(501, "contexts/1");
        WaitForTheJoinedIndexes(h);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        oracle.Run(rql);

        Assert.True(watch.ElapsedMilliseconds < 2000, "the query did not return within the bound of the test");
        AssertAnsweredOnce(joinIndex, before);
        hold?.Set();
        joinIndex.ForTestingPurposes = null;
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AChangeOfAJoinedDocumentIsNotHeldBackByTheRefreshWindow()
    {
        Dictionary<string, string> settings = ScoreSettings();
        settings[RavenConfiguration.GetKey(x => x.Join.RefreshWindow)] = "5000";
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, settings);
        h.RandomRound(new Random(10), 40);
        await h.AssertCurrentAsync();
        await Task.Delay(300);

        long refreshes = h.Join().Counters.Refreshes;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        h.PutContext(2, "fr", "Beta", 20, true, "blue whale");
        await WaitForAsync(() => h.Join().Counters.Refreshes > refreshes, "the refresh of the join index");
        Assert.True(watch.ElapsedMilliseconds < 2500, "the refresh did not come within the bound of the test");
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task BatchesThatArriveWithinTheRefreshWindowAreOneRefresh()
    {
        Dictionary<string, string> settings = ScoreSettings();
        settings[RavenConfiguration.GetKey(x => x.Join.RefreshWindow)] = "3000";
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, settings);
        h.RandomRound(new Random(9), 40);
        await h.AssertCurrentAsync();
        await Task.Delay(3500);

        JoinIndex joinIndex = h.Join();
        long refreshes = joinIndex.Counters.Refreshes;
        for (int i = 0; i < 8; i++)
        {
            h.PutConversation(600 + i, "contexts/" + (1 + i % 3));
            await Task.Delay(40);
        }

        await h.AssertCurrentAsync();
        long made = joinIndex.Counters.Refreshes - refreshes;
        Assert.True(made is >= 1 and <= 2, $"8 writes in one throttling window made {made} refreshes");
        var oracle = new JoinIndexQueryOracle(h);
        oracle.Check(Join(" where Country = 'nl'", " order by score() limit 0, 20"));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task QueriesOfAJoinThatIsBehindOpenTheIndexesItJoins()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(6);
        var oracle = new JoinIndexQueryOracle(h);
        string rql = Join(" where Country = 'nl'", " order by score() limit 0, 1000");
        oracle.Check(rql);

        using var gate = new ManualResetEventSlim(false);
        h.Join().ForTestingPurposes = phase =>
        {
            if (phase == "BeforeCommit")
                gate.Wait(TimeSpan.FromSeconds(20));
        };

        h.PutConversation(501, "contexts/1");
        await Task.Run(() => WaitForTheJoinedIndexes(h));
        long peeks = h.Join().QueryCounters.PeekCount;
        QueryAnswer scan = RunWithoutTheJoin(oracle, rql);
        QueryAnswer served = oracle.Run(rql);

        AssertSame(rql, served, scan);
        Assert.True(h.Join().QueryCounters.PeekCount > peeks);

        gate.Set();
        h.Join().ForTestingPurposes = null;
        await h.AssertCurrentAsync();
        oracle.Check(rql);
    }

    private static List<string> Ids(IDocumentStore store, string rql)
    {
        // a session that caches (the default): the second identical request carries the etag of the first
        using IDocumentSession session = store.OpenSession();
        List<JoinIndexHarness.Conversation> result = session.Advanced.RawQuery<JoinIndexHarness.Conversation>(rql).ToList();
        return result.Select(x => session.Advanced.GetMetadataFor(x)["@id"].ToString()).ToList();
    }
}
