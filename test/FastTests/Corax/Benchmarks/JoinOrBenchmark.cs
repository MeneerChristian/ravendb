using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Raven.Client.Documents;
using Raven.Client.Exceptions;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;

namespace FastTests.Corax;

public partial class JoinOrTests
{
    private const int Page = 20;

    [JoinBenchmarkFact("RAVEN_JOIN_OR_BENCH", RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void BenchmarkAt200k()
    {
        var userSearch = Environment.GetEnvironmentVariable("RAVEN_JOIN_OR_BENCH_USERS") ?? "w20 w50 w120 w400";
        var deptSearch = Environment.GetEnvironmentVariable("RAVEN_JOIN_OR_BENCH_DEPTS") ?? "w15 w60 w200";
        const int users = 200_000, depts = 20_000, games = 200_000;

        using var store = GetStore();
        var generator = new Generator(200);
        using (var bulk = store.BulkInsert())
        {
            foreach (var user in generator.Users(users))
                bulk.Store(user, user.Id);
            foreach (var dept in generator.Depts(depts))
                bulk.Store(dept, dept.Id);
            foreach (var game in generator.Games(games, users, depts))
                bulk.Store(game, game.Id);
        }

        Indexes.WaitForIndexing(store, timeout: TimeSpan.FromMinutes(30));

        var orJoin = $"{Games} {Joins(userSearch, deptSearch)} where {Outer} and (matches(u) or matches(d)) order by score() limit 0, {Page}";
        var andJoin = $"{Games} {Joins(userSearch, deptSearch)} where {Outer} order by score() limit 0, {Page}";
        var andJoinMatches = $"{Games} {Joins(userSearch, deptSearch)} where {Outer} and matches(u) and matches(d) order by score() limit 0, {Page}";
        var outerOnly = $"{Games} where {Outer} order by score() limit 0, {Page}";

        var report = new StringBuilder();
        report.AppendLine($"## {DateTime.Now:yyyy-MM-dd HH:mm} OR of joins, {users} users, {depts} depts, {games} games, page {Page}, median of 9 runs after 3 warm-up (allocations: process wide, KB)");
        report.AppendLine();
        report.AppendLine("| arm | total results | server ms | wall ms | managed allocated KB | join scan KB (u + d) | join outer match KB (u + d) |");
        report.AppendLine("|---|---|---|---|---|---|---|");

        long total = 0;
        Arm(store, report, "outer clause alone (floor)", outerOnly, out _);
        Arm(store, report, "OR: search and (matches(u) or matches(d))", orJoin, out var orTotal);
        Arm(store, report, "AND: two joins, d and u both inner", andJoin, out var andTotal);
        Arm(store, report, "AND: matches(u) and matches(d)", andJoinMatches, out var andMatchesTotal);
        Assert.Equal(andTotal, andMatchesTotal);
        Assert.True(orTotal >= andTotal);

        // the two-step form: ids of both sub-queries to the client, then the games with in(...) or in(...)
        var server = new List<double>();
        var wall = new List<double>();
        var allocated = new List<double>();
        for (var i = 0; i < 12; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var clock = Stopwatch.StartNew();
            double serverMs = 0;
            List<string> userIds, deptIds;
            using (var session = store.OpenSession())
            {
                userIds = session.Advanced.RawQuery<IdRow>($"{UsersQuery(userSearch)} select id() as Id").NoCaching().Statistics(out var stats).ToList().Select(x => x.Id).ToList();
                serverMs += stats.DurationInMs;
            }

            using (var session = store.OpenSession())
            {
                deptIds = session.Advanced.RawQuery<IdRow>($"{DeptsQuery(deptSearch)} select id() as Id").NoCaching().Statistics(out var stats).ToList().Select(x => x.Id).ToList();
                serverMs += stats.DurationInMs;
            }

            using (var session = store.OpenSession())
            {
                session.Advanced.RawQuery<Doc>($"{Games} where {Outer} and (g.OwnerId in ($u) or g.DeptId in ($d)) order by score() limit 0, {Page}")
                    .AddParameter("u", userIds).AddParameter("d", deptIds).NoCaching().Statistics(out var stats).ToList();
                serverMs += stats.DurationInMs;
                total = stats.TotalResults;
            }

            clock.Stop();
            var after = GC.GetTotalAllocatedBytes(precise: true);
            if (i < 3)
                continue;
            server.Add(serverMs);
            wall.Add(clock.Elapsed.TotalMilliseconds);
            allocated.Add((after - before) / 1024.0);
        }

        Assert.Equal(orTotal, total);
        report.AppendLine($"| two steps: ids of u, ids of d, then in(...) or in(...) (outer score only) | {total:N0} | {Median(server):N0} | {Median(wall):N0} | {Median(allocated):N0} | - | - |");

        output.WriteLine(report.ToString());
        JoinTestHelpers.AppendReport("RAVEN_JOIN_OR_BENCH_REPORT", "join-or-bench-report.txt", report);
    }

    private sealed class IdRow
    {
        public string Id { get; set; }
    }

    private void Arm(IDocumentStore store, StringBuilder report, string label, string rql, out long total)
    {
        var server = new List<double>();
        var wall = new List<double>();
        var allocated = new List<double>();
        var scan = new List<double>();
        var match = new List<double>();
        total = 0;
        for (var i = 0; i < 12; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var clock = Stopwatch.StartNew();
            using var session = store.OpenSession();
            session.Advanced.RawQuery<Doc>(rql).NoCaching().Statistics(out var stats).ToList();
            clock.Stop();
            var after = GC.GetTotalAllocatedBytes(precise: true);
            total = stats.TotalResults;
            if (i < 3)
                continue;
            server.Add(stats.DurationInMs);
            wall.Add(clock.Elapsed.TotalMilliseconds);
            allocated.Add((after - before) / 1024.0);
        }

        var scanText = "-";
        var matchText = "-";
        if (rql.Contains(" join "))
        {
            for (var i = 0; i < 5; i++)
            {
                using var session = store.OpenSession();
                session.Advanced.RawQuery<Doc>(rql + " include timings()").NoCaching().Timings(out var timings).ToList();
                var join = timings.Timings["Join"];
                scan.Add((join.Timings["u"].Counters["ScanAllocatedManagedBytes"] + join.Timings["d"].Counters["ScanAllocatedManagedBytes"]) / 1024.0);
                match.Add((join.Timings["u"].Counters["OuterMatchAllocatedManagedBytes"] + join.Timings["d"].Counters["OuterMatchAllocatedManagedBytes"]) / 1024.0);
            }

            scanText = Median(scan).ToString("N0");
            matchText = Median(match).ToString("N0");
        }

        report.AppendLine($"| {label} | {total:N0} | {Median(server):N0} | {Median(wall):N0} | {Median(allocated):N0} | {scanText} | {matchText} |");
    }
}
