using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>A join that a join index serves equals the join of the server: rows, order, total and @index-score, for real documents on a real server.</summary>
public class JoinIndexQueryTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static readonly string[] Subs =
    [
        "",
        " where Country = 'nl'",
        " where Country = 'c1'",
        " where Country = 'C1'",
        " where Name = 'n1'",
        " where Name = 'N1'",
        " where Rank = 10",
        " where IsActive = true",
        " where Country = 'nl' and IsActive = true",
        " where Country = 'c2' and Name = 'n3' and IsActive = true",
        " where Country != 'nl'",
        " where Country != 'c1' and IsActive = true",
        " where Country = 'c2' and IsActive != true",
        " where IsActive != true",
        " where Country = 'nobody'"
    ];

    private static readonly string[] Tails =
    [
        " limit 0, 1000",
        " order by score() limit 0, 7",
        " order by score() desc limit 3, 7",
        " order by m.Title limit 0, 10",
        " where m.Title = 'a' limit 0, 1000",
        " where m.Title = 'a' order by score() limit 0, 5",
        " boost 2 order by score() limit 0, 20",
        " limit 1000, 5"
    ];

    private static Dictionary<string, string> ScoreSettings() => new() { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };

    private async Task<JoinIndexHarness> HarnessWithDataAsync(int seed, int operations = 120)
    {
        JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        h.RandomRound(new Random(seed), operations);
        await h.AssertCurrentAsync();
        return h;
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task EveryServedKindEqualsTheJoinOfTheServer()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(1, 200);
        var oracle = new JoinIndexQueryOracle(h);

        foreach (string sub in Subs)
        {
            foreach (string tail in Tails)
                oracle.Check(Join(sub, tail));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
        Assert.Equal(Subs.Length * Tails.Length, oracle.Info.Served);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ParametersAndProjectionsOfTheOuterDocumentAreServed()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(2, 150);
        var oracle = new JoinIndexQueryOracle(h);

        oracle.Check(Join(" where Country = $country and Rank = $rank and IsActive = $active", " order by score() limit 0, 10"),
            new Dictionary<string, object> { ["country"] = "c1", ["rank"] = 10, ["active"] = true });
        oracle.Check(Join(" where Country = $country", " boost $boost order by score() limit 0, 10"), new Dictionary<string, object> { ["country"] = "nl", ["boost"] = 3 });
        oracle.Check(Join(" where Country = 'nl'", " select m.Title limit 0, 10"));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task EverythingElseIsReadByTheServerAndCounted()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(3, 150);
        var oracle = new JoinIndexQueryOracle(h);

        var notServed = new Dictionary<string, string>
        {
            [Join(" where Country = 'nl' or Country = 'c1'", " limit 0, 1000")] = "KindNotServed:Or",
            [Join(" where Rank > 20", " limit 0, 1000")] = "KindNotServed:Range",
            [Join(" where search(Title, 'w1')", " limit 0, 1000")] = "KindNotServed:Method:search",
            [Join(" where Title = 'w1'", " limit 0, 1000")] = "KindNotServed:Text",
            [Join(" where Rank = 10", " select c.Name limit 0, 1000")] = null,
            [Join(" where Rank = 10", " order by c.Rank limit 0, 1000")] = null,
        };

        foreach ((string rql, string reason) in notServed)
        {
            long before = oracle.Info.Fallbacks.GetValueOrDefault(reason ?? "-");
            long served = oracle.Info.Served;
            (QueryAnswer a, QueryAnswer b) = oracle.Both(rql);
            AssertSame(rql, a, b);
            Assert.Equal(served, oracle.Info.Served);
            if (reason != null)
                Assert.True(oracle.Info.Fallbacks.GetValueOrDefault(reason) > before, $"{reason} was not counted for {rql}: {string.Join(", ", oracle.Info.Fallbacks)}");
        }
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(12)]
    public async Task RandomWorkloadServedEqualsTheJoinOfTheServer(int seed)
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        var oracle = new JoinIndexQueryOracle(h);
        var random = new Random(seed);

        for (int round = 0; round < 8; round++)
        {
            h.RandomRound(random, round == 0 ? 80 : 14);
            if (round % 4 == 3)
                await h.CleanTombstonesAsync();

            await h.AssertCurrentAsync();
            for (int i = 0; i < 9; i++)
                oracle.Check(Join(Subs[random.Next(Subs.Length)], Tails[random.Next(Tails.Length)]));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
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
    public async Task QueriesDuringAWorkloadAreExactForTheSnapshotsTheyRead()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        var oracle = new JoinIndexQueryOracle(h);
        h.RandomRound(new Random(5), 60);
        await h.AssertCurrentAsync();

        using var stop = new CancellationTokenSource();
        Task writer = Task.Run(() =>
        {
            var random = new Random(77);
            while (stop.IsCancellationRequested == false)
            {
                h.RandomRound(random, 4);
                Thread.Sleep(random.Next(1, 8));
            }
        });

        var failures = new List<string>();
        int compared = 0;
        int different = 0;
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        var queryRandom = new Random(3);
        while (DateTime.UtcNow < until)
        {
            string rql = Join(Subs[queryRandom.Next(Subs.Length)], Tails[queryRandom.Next(Tails.Length)]);
            // the join of the server before and after the join index: when the three answers were read from the same state of the indexes, the join index has to give the same answer
            QueryAnswer before = RunWithoutTheJoin(oracle, rql);
            QueryAnswer served;
            try
            {
                served = oracle.Run(rql);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"served: {rql}", e);
            }

            QueryAnswer after = RunWithoutTheJoin(oracle, rql);

            // the indexes move while the workload runs: only answers read from the same state of the indexes can be compared
            if (before.ResultEtag != served.ResultEtag || served.ResultEtag != after.ResultEtag)
            {
                different++;
                continue;
            }

            QueryAnswer scan = before;
            compared++;
            try
            {
                AssertSame(rql, served, scan);
            }
            catch (Xunit.Sdk.XunitException e)
            {
                failures.Add(e.Message);
            }
        }

        stop.Cancel();
        await writer;

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(3)));
        Assert.True(compared > 5, $"only {compared} pairs read the same state ({different} did not)");
        JoinIndexStats info = oracle.Info;
        Assert.True(info.Served > 0, "nothing was served");
        Assert.Equal(0, info.Fallbacks.GetValueOrDefault(JoinFallbacks.Ahead));
        Assert.Equal(0, info.Fallbacks.GetValueOrDefault(JoinFallbacks.Failed));
        Output.WriteLine($"compared {compared}, other state {different}, served {info.Served}, fallbacks {string.Join(", ", info.Fallbacks)}, waits {info.QueryWaits} (max {info.QueryWaitMaxInMs:0.0} ms)");
    }
}
