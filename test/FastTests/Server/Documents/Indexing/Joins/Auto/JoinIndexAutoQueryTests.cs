using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Auto;
using Sparrow;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Auto;

/// <summary>
/// A join that a join index could answer creates an auto join index. The query that creates it is the join of the server and does not wait; the queries after it are answered by the join index,
/// with the same documents, order, total and score.
/// </summary>
public class JoinIndexAutoQueryTests(ITestOutputHelper output) : RavenTestBase(output)
{
    internal const string AutoName = "Auto/Conversations/ByContext/Join/Contexts/Search/OnContextIdToId";

    // declares Country and Name (exact terms), Rank (a number) and IsActive; the scores do not matter, so every kind of atom is eligible
    internal const string FirstSub = " where Country = 'nl' or Name = 'n1' or Rank > 10 or IsActive = true";
    internal const string FirstTail = " order by m.Title limit 0, 1000";

    internal static Dictionary<string, string> ScoreSettings(params (string Key, string Value)[] more)
    {
        var settings = new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString(),
            [RavenConfiguration.GetKey(x => x.Join.AutoEnabled)] = true.ToString()
        };
        foreach ((string key, string value) in more)
            settings[key] = value;

        return settings;
    }

    /// <summary>The gate of the auto join indexes sees a machine with room for any build of the test data, unless the test is about the real memory: the tests do not depend on the machine that runs them.</summary>
    internal static async Task<JoinIndexHarness> HarnessWithDataAsync(RavenTestBase test, int seed = 1, int operations = 200, Dictionary<string, string> settings = null, bool realMemory = false)
    {
        JoinIndexHarness h = await JoinIndexHarness.CreateAsync(test, settings ?? ScoreSettings(), putJoin: false);
        if (realMemory == false)
            h.Database.IndexStore.AutoJoins.AvailableMemoryForTesting = () => new Size(64, SizeUnit.Gigabytes);

        h.RandomRound(new Random(seed), operations);
        test.Indexes.WaitForIndexing(h.Store);
        return h;
    }

    /// <summary>Waits until the auto join index is a join index of the node, then until it is built and equals the indexes it joins.</summary>
    internal static async Task WaitForTheAutoJoinAsync(JoinIndexHarness h, string name = AutoName)
    {
        for (int i = 0; i < 800 && h.Join(name) == null; i++)
            await Task.Delay(25);

        AutoJoinCounters counters = h.Database.IndexStore.AutoJoins.Counters;
        Assert.True(h.Join(name) != null, $"no auto join index: requested {counters.Requested}, created {counters.Created}, failed {counters.Failed}, skipped for memory {counters.SkippedForMemory}");
        h.TrackJoin(name);
        await h.AssertCurrentAsync(name);
    }

    internal static string[] AutoJoinNames(JoinIndexHarness h) => h.Store.Maintenance.Send(new GetIndexNamesOperation(0, 100)).Where(x => x.Contains("/Join/")).ToArray();

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheFirstQueryIsTheJoinOfTheServerAndCreatesTheJoinTheSecondOneIsServed()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(this);
        var oracle = new JoinIndexQueryOracle(h, AutoName);
        string rql = oracle.JoinOf(FirstSub, FirstTail);

        QueryAnswer first = oracle.Run(rql);
        Assert.True(first.Rows.Count > 0, "the data has to match the query, or the test proves nothing");
        Assert.Equal(0, first.Counters.GetValueOrDefault("Join/c/JoinIndexServed"));

        await WaitForTheAutoJoinAsync(h);

        QueryAnswer second = oracle.Run(rql);
        Assert.Equal(1, second.Counters.GetValueOrDefault("Join/c/JoinIndexServed"));
        AssertSame(rql, second, first);
        Assert.Equal(1, oracle.Info.Served);

        Assert.Equal(1, h.Database.IndexStore.AutoJoins.Counters.Requested);
        Assert.Equal(1, h.Database.IndexStore.AutoJoins.Counters.Created);
        Assert.Equal(0, h.Database.IndexStore.AutoJoins.Counters.SkippedForMemory);

        Assert.Equal([AutoName], AutoJoinNames(h));
        IndexStats stats = h.Stats(AutoName);
        Assert.Equal(IndexType.AutoJoin, stats.Type);
        Assert.Equal(new[] { "Country", "IsActive", "Name", "Rank" }, h.Join(AutoName).Join.Fields.Select(x => x.Field));
        Assert.Equal(new[] { JoinFieldKind.Exact, JoinFieldKind.Exact, JoinFieldKind.Exact, JoinFieldKind.Long }, h.Join(AutoName).Join.Fields.Select(x => x.Kind));

        var record = h.Store.Maintenance.Server.Send(new GetDatabaseRecordOperation(h.Store.Database));
        Assert.True(record.AutoIndexes.TryGetValue(AutoName, out var auto));
        Assert.Equal(IndexType.AutoJoin, auto.Type);
        Assert.Equal("Contexts/Search", auto.Join.InnerIndex);
        Assert.DoesNotContain(record.Indexes.Keys, x => x.Contains("/Join/"));
    }

    /// <summary>The gate decides in the query that asks for the join index, and what it reads is the memory of the machine (in a container the page cache fills what the container may use): either the build was refused and counted, or the join index is created.</summary>
    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheGateThatReadsTheRealMemoryEitherRefusesTheBuildOnceOrCreatesTheJoin()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(this, seed: 3, realMemory: true);
        var oracle = new JoinIndexQueryOracle(h, AutoName);
        string rql = oracle.JoinOf(FirstSub, FirstTail);
        QueryAnswer first = oracle.Run(rql);
        Assert.True(first.Rows.Count > 0, "the data has to match the query, or the test proves nothing");

        AutoJoinCounters counters = h.Database.IndexStore.AutoJoins.Counters;
        Assert.Equal(1, counters.Requested + counters.SkippedForMemory);
        if (counters.Requested == 1)
        {
            await WaitForTheAutoJoinAsync(h);
            Assert.Equal(1, counters.Created);
            Assert.Equal([AutoName], AutoJoinNames(h));
        }
        else
        {
            Assert.Equal(0, counters.Created);
            Assert.Empty(AutoJoinNames(h));
            Assert.Null(h.Join(AutoName));
        }

        // the join of the server answers whatever the gate said
        AssertSame(rql, oracle.Run(rql), first);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task EveryServedShapeOfTheAutoJoinEqualsTheJoinOfTheServer()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(this, seed: 2);
        var oracle = new JoinIndexQueryOracle(h, AutoName);
        oracle.Run(oracle.JoinOf(FirstSub, FirstTail));
        await WaitForTheAutoJoinAsync(h);

        string[] subs =
        [
            "",
            " where Country = 'nl'",
            " where Country = 'C1'",
            " where Name = 'n1'",
            " where Rank = 10",
            " where IsActive = true",
            " where Country = 'nl' and IsActive = true",
            " where Country != 'nl'",
            " where IsActive != true",
            " where Country = 'nobody'"
        ];

        string[] tails =
        [
            " limit 0, 1000",
            " order by score() limit 0, 7",
            " order by score() desc limit 3, 7",
            " order by m.Title limit 0, 10",
            " where m.Title = 'a' order by score() limit 0, 5",
            " boost 2 order by score() limit 0, 20"
        ];

        foreach (string sub in subs)
        {
            foreach (string tail in tails)
                oracle.Check(oracle.JoinOf(sub, tail));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
        Assert.Equal(subs.Length * tails.Length, oracle.Info.Served);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AQueryThatWaitsForNonStaleResultsWaitsForTheAutoJoinAndIsServed()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(this, seed: 3);
        var oracle = new JoinIndexQueryOracle(h, AutoName);
        string rql = oracle.JoinOf(FirstSub, FirstTail);

        QueryAnswer waited = oracle.Run(rql, waitForNonStale: true);
        Assert.Equal(1, waited.Counters.GetValueOrDefault("Join/c/JoinIndexServed"));
        Assert.True(waited.Rows.Count > 0);

        h.TrackJoin(AutoName);
        oracle.ForceFallback(true);
        QueryAnswer scan = oracle.Run(rql);
        oracle.ForceFallback(false);
        Assert.Equal(0, scan.Counters.GetValueOrDefault("Join/c/JoinIndexServed"));
        AssertSame(rql, waited, scan);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AQueryThatDoesNotWaitNeverWaitsForTheBuildOfTheJoin()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(this, seed: 4, operations: 400);
        var oracle = new JoinIndexQueryOracle(h, AutoName);
        string rql = oracle.JoinOf(FirstSub, FirstTail);

        // while the join index is created and built, every query is the join of the server and has the same answer
        QueryAnswer first = oracle.Run(rql);
        for (int i = 0; i < 20; i++)
        {
            QueryAnswer next = oracle.Run(rql);
            AssertSame(rql, next, first);
        }

        await WaitForTheAutoJoinAsync(h);
        Assert.Equal(1, h.Database.IndexStore.AutoJoins.Counters.Requested);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnUndeclaredFieldIsReadByTheServerWithTheCounterOfTheJoin()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(this, seed: 5);
        var oracle = new JoinIndexQueryOracle(h, AutoName);
        oracle.Run(oracle.JoinOf(FirstSub, FirstTail));
        await WaitForTheAutoJoinAsync(h);

        string rql = oracle.JoinOf(" where Title = 'w1'", FirstTail);
        QueryAnswer answer = oracle.Run(rql);
        Assert.Equal(0, answer.Counters.GetValueOrDefault("Join/c/JoinIndexServed"));
        Assert.Equal(1, answer.Counters.GetValueOrDefault("Join/c/JoinFallback:KindNotServed:UndeclaredField:Title"));
        Assert.Equal(1, oracle.FallbacksOf("KindNotServed:UndeclaredField:Title"));

        // no join index of the join declares Title: a second one is made that declares it and the fields of the first, the first is not changed
        string[] names = null;
        for (int i = 0; i < 400 && (names = AutoJoinNames(h)).Length < 2; i++)
            await Task.Delay(25);

        Assert.Equal(2, names.Length);
        string widened = names.Single(x => x != AutoName);
        Assert.StartsWith(AutoName + "/With", widened);
        Assert.Equal(2, h.Database.IndexStore.AutoJoins.Counters.Requested);
        Assert.Equal(4, h.Join(AutoName).Join.Fields.Count);
        Assert.Equal(5, h.Join(widened).Join.Fields.Count);

        await WaitForTheAutoJoinAsync(h, widened);
        var both = new JoinIndexQueryOracle(h, widened) { AlsoForced = [AutoName] };
        both.Check(rql);

        // what the first join index declares is served by it, and no third join index is made
        var first = new JoinIndexQueryOracle(h, AutoName) { AlsoForced = [widened] };
        first.Check(first.JoinOf(FirstSub, FirstTail));
        Assert.Equal(2, AutoJoinNames(h).Length);
        Assert.Equal(2, h.Database.IndexStore.AutoJoins.Counters.Requested);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(" where Country = 'nl' or Country = 'c1'", " order by score() limit 0, 10", "KindNotServed:Or")]
    [InlineData(" where Rank > 10", " order by score() limit 0, 10", "KindNotServed:Range")]
    [InlineData(" where exists(Name)", " order by m.Title limit 0, 10", "Where")]
    public async Task AJoinThatNoJoinCouldAnswerCreatesNothing(string sub, string tail, string reason)
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(this, seed: 6, operations: 80);
        var oracle = new JoinIndexQueryOracle(h, AutoName);

        _ = oracle.Run(oracle.JoinOf(sub, tail));

        AutoJoinCounters counters = h.Database.IndexStore.AutoJoins.Counters;
        Assert.Equal(0, counters.Requested);
        Assert.Contains(counters.NotEligible, x => x.Key.EndsWith(reason, StringComparison.Ordinal));
        Assert.Empty(AutoJoinNames(h));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AJoinThatProjectsTheJoinedDocumentCreatesNothing()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(this, seed: 7, operations: 80);
        var oracle = new JoinIndexQueryOracle(h, AutoName);

        _ = oracle.Run($"from index '{JoinIndexHarness.OuterName}' as m join (from index '{JoinIndexHarness.InnerName}' where Country = 'nl') as c on m.ContextId = id(c) select m.Title, c.Country limit 0, 10");

        Assert.Equal(0, h.Database.IndexStore.AutoJoins.Counters.Requested);
        Assert.Empty(AutoJoinNames(h));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ADisabledAutoJoinCreatesNothing()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(this, seed: 8, operations: 80, settings: ScoreSettings(("Join.AutoEnabled", "false")));
        var oracle = new JoinIndexQueryOracle(h, AutoName);

        QueryAnswer answer = oracle.Run(oracle.JoinOf(FirstSub, FirstTail));
        Assert.True(answer.Rows.Count > 0);

        Assert.Equal(0, h.Database.IndexStore.AutoJoins.Counters.Requested);
        Assert.Empty(AutoJoinNames(h));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AStaticJoinOfTheSameJoinMeansNoAutoJoin()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        h.RandomRound(new Random(9), 80);
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);

        // the static join index does not declare Title: that is its counter, not a reason for a second join index
        QueryAnswer answer = oracle.Run(oracle.JoinOf(" where Country = 'nl' and Rank = 10", FirstTail));
        Assert.Equal(1, answer.Counters.GetValueOrDefault("Join/c/JoinIndexServed"));
        _ = oracle.Run(Join(" where Country = 'nl'", " order by m.Title limit 0, 10"));

        Assert.Equal(0, h.Database.IndexStore.AutoJoins.Counters.Requested);
        Assert.Empty(AutoJoinNames(h));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AJoinThatIsPutAnswersBeforeTheAutoJoinOfTheSameJoin()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(this, seed: 10);
        var auto = new JoinIndexQueryOracle(h, AutoName);
        string rql = auto.JoinOf(" where Country = 'nl'", " order by m.Title limit 0, 100");
        _ = auto.Run(rql);
        await WaitForTheAutoJoinAsync(h);

        await h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition());
        await h.AssertCurrentAsync();
        var put = new JoinIndexQueryOracle(h) { AlsoForced = [AutoName] };
        long autoServed = auto.Info.Served;

        put.Check(rql);

        Assert.Equal(autoServed, auto.Info.Served);
        Assert.Equal(1, put.Info.Served);
    }
}
