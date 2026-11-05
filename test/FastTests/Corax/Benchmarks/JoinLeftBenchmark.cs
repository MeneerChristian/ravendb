using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Text;
using Raven.Client.Documents;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;

namespace FastTests.Corax;

// Opt-in: RAVEN_JOIN_LEFT_BENCH=&lt;users&gt
public class JoinLeftBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Games = "from index 'Games' as g";
    private const string OuterWhere = "where search(g.Name, 'w1 w4 w12 w30 w90 w300')";
    private const string InnerQuery = "from index 'Users' where search(Name, 'w2 w6 w15 w40 w120 w400')";
    private const string On = "as u on g.OwnerId = id(u)";

    [JoinBenchmarkFact("RAVEN_JOIN_LEFT_BENCH", RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void Run()
    {
        var users = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_LEFT_BENCH"));
        var reps = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_LEFT_BENCH_REPS") ?? "15");
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        using var store = GetDocumentStore(options);
        new JoinScoreOrderTests.UsersIndex().Execute(store);
        new JoinScoreOrderTests.GamesIndex().Execute(store);

        var generator = new JoinScoreOrderTests.Generator(200);
        using (var bulk = store.BulkInsert())
        {
            foreach (var user in generator.Users(users))
                bulk.Store(user, user.Id);
            foreach (var game in generator.Games(users, users))
                bulk.Store(game, game.Id);
        }

        Indexes.WaitForIndexing(store, timeout: TimeSpan.FromMinutes(60));
        GC.Collect();

        var report = new StringBuilder();
        report.AppendLine($"=== {users} users, {users} games, {reps} reps, limit 0,20 ({DateTime.Now:yyyy-MM-dd HH:mm}) ===");
        report.AppendLine("query | rows total | server ms (median / min) | managed KB (median)");
        var queries = new (string Name, string Rql)[]
        {
            ("no join, order by score()", $"{Games} {OuterWhere} order by score() limit 0, 20"),
            ("left join, order by score()", $"{Games} left join ({InnerQuery}) {On} {OuterWhere} order by score() limit 0, 20"),
            ("left join boost 0, order by score()", $"{Games} left join ({InnerQuery}) {On} boost 0 {OuterWhere} order by score() limit 0, 20"),
            ("inner join, order by score()", $"{Games} join ({InnerQuery}) {On} {OuterWhere} order by score() limit 0, 20"),
            ("no join, no scores (order by g.Name)", $"{Games} {OuterWhere} order by g.Name limit 0, 20"),
            ("left join, no scores (order by g.Name)", $"{Games} left join ({InnerQuery}) {On} {OuterWhere} order by g.Name limit 0, 20"),
            ("inner join, no scores (order by g.Name)", $"{Games} join ({InnerQuery}) {On} {OuterWhere} order by g.Name limit 0, 20"),
            ("no join, projection g.Name", $"{Games} {OuterWhere} order by score() select g.Name as Name limit 0, 20"),
            ("left join, projection u.Name", $"{Games} left join ({InnerQuery}) {On} {OuterWhere} order by score() select g.Name as Name, u.Name as UserName limit 0, 20"),
            ("left join boost 0, projection u.Name", $"{Games} left join ({InnerQuery}) {On} boost 0 {OuterWhere} order by score() select g.Name as Name, u.Name as UserName limit 0, 20"),
            ("inner join, projection u.Name", $"{Games} join ({InnerQuery}) {On} {OuterWhere} order by score() select g.Name as Name, u.Name as UserName limit 0, 20"),
        };

        var only = Environment.GetEnvironmentVariable("RAVEN_JOIN_LEFT_BENCH_ONLY");
        var totals = new Dictionary<string, long>();
        foreach (var (name, rql) in queries)
        {
            if (only != null && name.Contains(only) == false)
                continue;

            var listener = only != null && Environment.GetEnvironmentVariable("RAVEN_JOIN_LEFT_BENCH_TICKS") != null ? new TickListener() : null;
            for (var i = 0; i < 4; i++)
                Execute(store, rql, out _, out _);

            var server = new List<double>();
            var bytes = new List<double>();
            long total = 0;
            for (var i = 0; i < reps; i++)
            {
                total = Execute(store, rql, out var serverMs, out var managed);
                server.Add(serverMs);
                bytes.Add(managed);
            }

            totals[name] = total;
            if (only != null)
                report.AppendLine("  series: " + string.Join(" ", server.Zip(bytes, (a, b) => $"{a:F0}/{b / 1024:F0}")));
            if (listener != null)
            {
                report.AppendLine(listener.Report());
                listener.Dispose();
            }
            if (only != null)
                Dump(report, name, LastTimings, 1);
            report.AppendLine($"{name} | {total:N0} | {Median(server):F0} / {server.Min():F0} | {Median(bytes) / 1024:N0}");
        }

        if (only != null)
        {
            output.WriteLine(report.ToString());
            AppendReport("RAVEN_JOIN_LEFT_BENCH_OUT", "joinleft.txt", report);
            return;
        }

        // a left join never filters
        var plain = totals["no join, order by score()"];
        Assert.True(plain > 0);
        Assert.Equal(plain, totals["left join, order by score()"]);
        Assert.Equal(plain, totals["left join boost 0, order by score()"]);
        Assert.Equal(totals["no join, no scores (order by g.Name)"], totals["left join, no scores (order by g.Name)"]);
        Assert.Equal(totals["no join, projection g.Name"], totals["left join, projection u.Name"]);
        Assert.Equal(totals["inner join, order by score()"], totals["inner join, no scores (order by g.Name)"]);
        Assert.True(totals["inner join, order by score()"] <= plain);

        output.WriteLine(report.ToString());
        AppendReport("RAVEN_JOIN_LEFT_BENCH_OUT", "joinleft.txt", report);
    }

    private static QueryTimings LastTimings;

    private sealed class TickListener : EventListener
    {
        private readonly Dictionary<string, long> _types = new();

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName == null || e.EventName.StartsWith("GCAllocationTick") == false)
                return;
            var type = (string)e.Payload[e.PayloadNames.IndexOf("TypeName")];
            var amount = Convert.ToInt64(e.Payload[e.PayloadNames.IndexOf("AllocationAmount64")]);
            lock (_types)
                _types[type] = _types.GetValueOrDefault(type) + amount;
        }

        public string Report()
        {
            lock (_types)
                return string.Join(Environment.NewLine, _types.OrderByDescending(x => x.Value).Take(12).Select(x => $"  tick {x.Key}: {x.Value / 1024} KB"));
        }
    }

    private static void Dump(StringBuilder sb, string name, QueryTimings t, int depth)
    {
        sb.Append(' ', depth * 2).Append($"{name}: {t.DurationInMs} ms");
        if (t.Counters != null)
            foreach (var c in t.Counters)
                sb.Append($" [{c.Key}={c.Value}]");
        sb.AppendLine();
        if (t.Timings != null)
            foreach (var c in t.Timings)
                Dump(sb, c.Key, c.Value, depth + 1);
    }

    private static long Execute(IDocumentStore store, string rql, out double serverMs, out double managedBytes)
    {
        using var session = store.OpenSession();
        QueryStatistics stats = null;
        var query = rql.Contains(" select ")
            ? (IEnumerable<object>)session.Advanced.RawQuery<Projection>(rql + " include timings()").NoCaching().Statistics(out stats).Timings(out var timings).ToList()
            : session.Advanced.RawQuery<JoinScoreOrderTests.Doc>(rql + " include timings()").NoCaching().Statistics(out stats).Timings(out timings).ToList();
        _ = query.Count();
        serverMs = stats.DurationInMs;
        managedBytes = timings.Timings["Query"].Counters["AllocatedManagedBytes"];
        LastTimings = timings;
        return stats.TotalResults;
    }

    private sealed class Projection
    {
        public string Name { get; set; }
        public string UserName { get; set; }
    }
}
