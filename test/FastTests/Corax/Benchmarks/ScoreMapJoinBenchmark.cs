using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;

namespace FastTests.Corax;

// Opt-in benchmark of the RQL JOIN, it does nothing unless RAVEN_JOIN_BENCH=1 is set
public class ScoreMapJoinBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    // RAVEN_JOIN_BENCH_WARMUP / RAVEN_JOIN_BENCH_ITERATIONS: fewer runs for the big scales
    private static readonly int Warmup = EnvInt("RAVEN_JOIN_BENCH_WARMUP", 3);
    private static readonly int Iterations = EnvInt("RAVEN_JOIN_BENCH_ITERATIONS", 15);
    private static readonly int MemoryIterations = EnvInt("RAVEN_JOIN_BENCH_ITERATIONS", 9);
    // RAVEN_JOIN_BENCH_SKIP_TWOSTEP=1: the two separate queries need the ids of all matching users on the client
    private static readonly bool SkipTwoStep = Environment.GetEnvironmentVariable("RAVEN_JOIN_BENCH_SKIP_TWOSTEP") == "1";
    private static readonly TimeSpan IndexingTimeout = TimeSpan.FromMinutes(EnvInt("RAVEN_JOIN_BENCH_INDEXING_MINUTES", 20));
    private const int Page = 20;

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    [JoinBenchmarkFact("RAVEN_JOIN_BENCH", RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void Run()
    {
        var scales = Parse("RAVEN_JOIN_BENCH_SCALES", "10000");
        var fanouts = Parse("RAVEN_JOIN_BENCH_FANOUT", "1,10");

        var report = new StringBuilder();
        report.AppendLine($"## Run {DateTime.Now:yyyy-MM-dd HH:mm} (server DurationInMs, median / p90 of {Iterations} runs after {Warmup} warm-up, page {Page})");

        foreach (var users in scales)
        foreach (var fanout in fanouts)
            RunScale(users, fanout, report);

        var path = Path.Combine(FindRepoRoot(), ".agents", "join-benchmark-results.md");
        File.AppendAllText(path, report + Environment.NewLine);
        output.WriteLine(report.ToString());
    }

    private void RunScale(int users, int fanout, StringBuilder report)
    {
        var games = users * fanout;
        using var store = SeedStore(users, fanout);

        foreach (var name in new[] { "Users", "Games" })
        {
            var engine = store.Maintenance.Send(new GetIndexStatisticsOperation(name)).SearchEngineType;
            Assert.Equal(SearchEngineType.Corax, engine);
        }

        report.AppendLine();
        report.AppendLine($"### {users:N0} users, {games:N0} games ({fanout} per user)");
        report.AppendLine();
        report.AppendLine("| inner (users matched) | outer where (games matched) | arm | total results | server ms (median / p90) | join scan ms |");
        report.AppendLine("|---|---|---|---|---|---|");

        foreach (var (innerTerm, ownersMatched) in new[] { ("chris", users), ("tenth", users / 10) })
        foreach (var (outerTerm, outerMatched) in new[] { ("zelda", games), ("rare", (games + 99) / 100) })
        {
            // games.Name 'rare' = every hundredth game: g % 100 == 0; owner = g % users
            var expectedJoined = CountJoined(users, games, innerTerm, outerTerm);
            var inner = $"{innerTerm} ({ownersMatched:N0})";
            var outer = $"{outerTerm} ({outerMatched:N0})";

            // floor: the outer query without the join
            Measure(report, store, inner, outer, "outer alone, order by score() [floor]", outerMatched,
                take => $"from index 'Games' as g where search(g.Name, '{outerTerm}') order by score() limit 0, {take}", null);

            Measure(report, store, inner, outer, "join, order by score()", expectedJoined,
                take => $"from index 'Games' as g join (from index 'Users' where search(Name, '{innerTerm}')) as u on g.OwnerId = id(u) " +
                        $"where search(g.Name, '{outerTerm}') order by score() limit 0, {take}", "Join");

            Measure(report, store, inner, outer, "join, order by g.Name (scores unused)", expectedJoined,
                take => $"from index 'Games' as g join (from index 'Users' where search(Name, '{innerTerm}')) as u on g.OwnerId = id(u) " +
                        $"where search(g.Name, '{outerTerm}') order by g.Name limit 0, {take}", "Join");

            MeasureTwoStep(report, store, inner, outer, expectedJoined, innerTerm, outerTerm);
        }
    }

    // Memory of the same arms. RAVEN_JOIN_BENCH_MEMORY=1
    [JoinBenchmarkFact("RAVEN_JOIN_BENCH_MEMORY", RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void RunMemory()
    {
        var report = new StringBuilder();
        report.AppendLine($"## Memory run {DateTime.Now:yyyy-MM-dd HH:mm} (median of {MemoryIterations} runs after {Warmup} warm-up, page {Page}; KB)");

        foreach (var users in Parse("RAVEN_JOIN_BENCH_SCALES", "10000"))
        foreach (var fanout in Parse("RAVEN_JOIN_BENCH_FANOUT", "1,10"))
        {
            var games = users * fanout;
            using var store = SeedStore(users, fanout);

            report.AppendLine();
            report.AppendLine($"### {users:N0} users, {games:N0} games ({fanout} per user)");
            report.AppendLine();
            report.AppendLine("| inner (users matched) | outer where (games matched) | arm | total results | managed allocated KB (process) | peak unmanaged KB | join scan KB | join outer match built KB | join outer query KB | join total KB | server ms (timings runs) |");
            report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");

            foreach (var (innerTerm, ownersMatched) in new[] { ("chris", users), ("tenth", users / 10) })
            foreach (var (outerTerm, outerMatched) in new[] { ("zelda", games), ("rare", (games + 99) / 100) })
            {
                var expectedJoined = CountJoined(users, games, innerTerm, outerTerm);
                var inner = $"{innerTerm} ({ownersMatched:N0})";
                var outer = $"{outerTerm} ({outerMatched:N0})";

                MeasureMemory(report, store, null, inner, outer, "outer alone, order by score() [floor]", outerMatched, () =>
                {
                    using var session = store.OpenSession();
                    session.Advanced.RawQuery<Game>($"from index 'Games' as g where search(g.Name, '{outerTerm}') order by score() limit 0, {Page}")
                        .NoCaching().Statistics(out var stats).ToList();
                    return stats.TotalResults;
                });

                var joinScoreRql = $"from index 'Games' as g join (from index 'Users' where search(Name, '{innerTerm}')) as u on g.OwnerId = id(u) " +
                                   $"where search(g.Name, '{outerTerm}') order by score() limit 0, {Page}";
                var joinFieldRql = $"from index 'Games' as g join (from index 'Users' where search(Name, '{innerTerm}')) as u on g.OwnerId = id(u) " +
                                   $"where search(g.Name, '{outerTerm}') order by g.Name limit 0, {Page}";

                MeasureMemory(report, store, joinScoreRql, inner, outer, "join, order by score()", expectedJoined, () =>
                {
                    using var session = store.OpenSession();
                    session.Advanced.RawQuery<Game>($"from index 'Games' as g join (from index 'Users' where search(Name, '{innerTerm}')) as u on g.OwnerId = id(u) " +
                                                    $"where search(g.Name, '{outerTerm}') order by score() limit 0, {Page}")
                        .NoCaching().Statistics(out var stats).ToList();
                    return stats.TotalResults;
                });

                MeasureMemory(report, store, joinFieldRql, inner, outer, "join, order by g.Name (scores unused)", expectedJoined, () =>
                {
                    using var session = store.OpenSession();
                    session.Advanced.RawQuery<Game>($"from index 'Games' as g join (from index 'Users' where search(Name, '{innerTerm}')) as u on g.OwnerId = id(u) " +
                                                    $"where search(g.Name, '{outerTerm}') order by g.Name limit 0, {Page}")
                        .NoCaching().Statistics(out var stats).ToList();
                    return stats.TotalResults;
                });

                if (SkipTwoStep == false)
                    MeasureTwoStepMemory(report, store, inner, outer, expectedJoined, innerTerm, outerTerm);
            }
        }

        var path = Path.Combine(FindRepoRoot(), ".agents", "join-benchmark-results.md");
        File.AppendAllText(path, report + Environment.NewLine);
        output.WriteLine(report.ToString());
    }

    // The two separate queries a client can run without the join, measured per step and together
    private void MeasureTwoStepMemory(StringBuilder report, IDocumentStore store, string inner, string outer, long expectedTotal, string innerTerm, string outerTerm)
    {
        var step1 = new List<double>();
        var step2 = new List<double>();
        var both = new List<double>();
        long total = 0;

        for (var i = 0; i < Warmup + MemoryIterations; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var start = GC.GetTotalAllocatedBytes(precise: true);
            List<string> ids;
            using (var session = store.OpenSession())
            {
                ids = session.Advanced.RawQuery<IdRow>($"from index 'Users' where search(Name, '{innerTerm}') select id() as Id")
                    .NoCaching().ToList().Select(x => x.Id).ToList();
            }

            var middle = GC.GetTotalAllocatedBytes(precise: true);
            using (var session = store.OpenSession())
            {
                session.Advanced.RawQuery<Game>($"from index 'Games' as g where search(g.Name, '{outerTerm}') and g.OwnerId in ($ids) order by score() limit 0, {Page}")
                    .AddParameter("ids", ids).NoCaching().Statistics(out var stats).ToList();
                total = stats.TotalResults;
            }

            var end = GC.GetTotalAllocatedBytes(precise: true);

            if (i < Warmup)
                continue;

            step1.Add((middle - start) / 1024.0);
            step2.Add((end - middle) / 1024.0);
            both.Add((end - start) / 1024.0);
        }

        Assert.Equal(expectedTotal, total);
        report.AppendLine($"| {inner} | {outer} | two separate queries (1: ids of the users, 2: games with IN) | {total:N0} | {Median(both):N0} | n/a | {Median(step1):N0} | - | {Median(step2):N0} | {Median(both):N0} | - |");
    }


    private void MeasureMemory(StringBuilder report, IDocumentStore store, string joinRql, string inner, string outer, string arm, long expectedTotal, Func<long> run)
    {
        var managed = new List<double>();
        var unmanaged = new List<double>();
        long total = 0;

        for (var i = 0; i < Warmup + MemoryIterations; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var nativeBefore = global::Sparrow.Utils.NativeMemory.TotalAllocatedMemory;
            long peak = nativeBefore;
            var stop = false;
            var sampler = new Thread(() =>
            {
                while (Volatile.Read(ref stop) == false)
                {
                    var current = global::Sparrow.Utils.NativeMemory.TotalAllocatedMemory;
                    if (current > peak)
                        peak = current;
                }
            }) { IsBackground = true };
            sampler.Start();

            var before = GC.GetTotalAllocatedBytes(precise: true);
            total = run();
            var after = GC.GetTotalAllocatedBytes(precise: true);

            Volatile.Write(ref stop, true);
            sampler.Join();

            if (i < Warmup)
                continue;

            managed.Add((after - before) / 1024.0);
            unmanaged.Add(Math.Max(0, peak - nativeBefore) / 1024.0);
        }

        Assert.Equal(expectedTotal, total);

        // what the join itself reports (scan, outer match, both together) comes back with the query timings
        string reported = "| - | - | - | - | - |";
        if (joinRql != null)
        {
            var scan = new List<double>();
            var match = new List<double>();
            var query = new List<double>();
            var both = new List<double>();
            var duration = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                using var session = store.OpenSession();
                session.Advanced.RawQuery<Game>(joinRql + " include timings()").NoCaching().Timings(out var timings).ToList();
                var join = timings.Timings["Join"];
                var joined = join.Timings["u"].Counters;
                scan.Add(joined["ScanAllocatedManagedBytes"] / 1024.0);
                match.Add(joined["OuterMatchAllocatedManagedBytes"] / 1024.0);
                query.Add(join.Counters["OuterQueryAllocatedManagedBytes"] / 1024.0);
                both.Add(join.Counters["TotalAllocatedManagedBytes"] / 1024.0);
                duration.Add(timings.DurationInMs);
            }

            reported = $"| {Median(scan):N0} | {Median(match):N0} | {Median(query):N0} | {Median(both):N0} | {Median(duration):N0} |";
        }

        report.AppendLine($"| {inner} | {outer} | {arm} | {total:N0} | {Median(managed):N0} | {Median(unmanaged):N0} {reported}");
    }

    private IDocumentStore SeedStore(int users, int fanout)
    {
        var games = users * fanout;
        var store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);

        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < users; i++)
            {
                // every user matches 'chris' (100%), every tenth one also matches 'tenth' (10%)
                bulk.Store(new User { Name = i % 10 == 0 ? "chris tenth" : "chris" }, $"users/{i}");
            }

            for (var g = 0; g < games; g++)
            {
                // every game matches 'zelda' (wide outer filter), every hundredth one also 'rare' (narrow outer filter)
                bulk.Store(new Game { Name = g % 100 == 0 ? "zelda rare" : "zelda", OwnerId = $"users/{g % users}" }, $"games/{g}");
            }
        }

        Indexes.WaitForIndexing(store, timeout: IndexingTimeout);
        return store;
    }

    private static long CountJoined(int users, int games, string innerTerm, string outerTerm)
    {
        long count = 0;
        for (var g = 0; g < games; g++)
        {
            if (outerTerm == "rare" && g % 100 != 0)
                continue;

            var owner = g % users;
            if (innerTerm == "tenth" && owner % 10 != 0)
                continue;

            count++;
        }

        return count;
    }

    private void Measure(StringBuilder report, IDocumentStore store, string inner, string outer, string arm, long expectedTotal, Func<int, string> rql, string timingScope)
    {
        var durations = new List<double>();
        var scans = new List<double>();
        long total = 0;

        for (var i = 0; i < Warmup + Iterations; i++)
        {
            // fresh session per iteration; varying the page size busts any cache
            using var session = store.OpenSession();
            session.Advanced.RawQuery<Game>(rql(Page + i % 3)).NoCaching().Statistics(out var stats).ToList();
            total = stats.TotalResults;

            if (i < Warmup)
                continue;

            durations.Add(stats.DurationInMs);
        }

        // the scan time comes from a separate, timed run so the timing instrumentation does not skew the latency figures
        if (timingScope != null)
        {
            for (var i = 0; i < 5; i++)
            {
                using var session = store.OpenSession();
                session.Advanced.RawQuery<Game>(rql(Page + i % 3) + " include timings()").NoCaching().Timings(out var timings).ToList();
                if (i == 0)
                    output.WriteLine("TIMINGS: " + (timings.Timings == null ? "null" : string.Join(",", timings.Timings.Select(x => x.Key + "(" + (x.Value.Timings == null ? "-" : string.Join("/", x.Value.Timings.Keys)) + ")"))));

                if (timings.Timings != null && timings.Timings.TryGetValue(timingScope, out var join) && join.Timings != null
                    && join.Timings.TryGetValue("u", out var joined) && joined.Timings != null && joined.Timings.TryGetValue("Scan", out var scan))
                    scans.Add(scan.DurationInMs);
            }
        }

        Assert.Equal(expectedTotal, total); // calibration: a probe that returns the wrong rows must not look fast

        report.AppendLine($"| {inner} | {outer} | {arm} | {total:N0} | {Median(durations):N0} / {Percentile(durations, 0.9):N0} | {(scans.Count > 0 ? Median(scans).ToString("N0") : "-")} |");
    }

    private void MeasureTwoStep(StringBuilder report, IDocumentStore store, string inner, string outer, long expectedTotal, string innerTerm, string outerTerm)
    {
        // what a client can do today without the join
        var durations = new List<double>();
        long total = 0;
        try
        {
            for (var i = 0; i < Warmup + Iterations; i++)
            {
                using var session = store.OpenSession();
                var ids = session.Advanced.RawQuery<IdRow>($"from index 'Users' where search(Name, '{innerTerm}') select id() as Id")
                    .NoCaching().Statistics(out var idStats).ToList().Select(x => x.Id).ToList();

                using var second = store.OpenSession();
                var query = second.Advanced.RawQuery<Game>($"from index 'Games' as g where search(g.Name, '{outerTerm}') and g.OwnerId in ($ids) order by score() limit 0, {Page + i % 3}")
                    .AddParameter("ids", ids).NoCaching().Statistics(out var stats);
                query.ToList();
                total = stats.TotalResults;

                if (i >= Warmup)
                    durations.Add(idStats.DurationInMs + stats.DurationInMs);
            }

            Assert.Equal(expectedTotal, total);
            report.AppendLine($"| {inner} | {outer} | two queries + IN [server time of both, transfer of the ids NOT included] | {total:N0} | {Median(durations):N0} / {Percentile(durations, 0.9):N0} | - |");
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            report.AppendLine($"| {inner} | {outer} | two queries + IN | failed | {e.GetType().Name}: {e.Message.Split('\n')[0]} | - |");
        }
    }

    private static int[] Parse(string variable, string fallback) =>
        (Environment.GetEnvironmentVariable(variable) ?? fallback).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && Directory.Exists(Path.Combine(directory.FullName, ".agents")) == false)
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root (a folder containing .agents).");
    }

    private class IdRow
    {
        public string Id { get; set; }
    }

    private class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    private class Game
    {
        public string Name { get; set; }
        public string OwnerId { get; set; }
    }

    private class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    private class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
