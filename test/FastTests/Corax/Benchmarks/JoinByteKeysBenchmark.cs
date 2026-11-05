using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;

namespace FastTests.Corax;

// Opt-in: RAVEN_JOIN_BYTES_BENCH=&lt;users&gt
public class JoinByteKeysBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Games = "from index 'Games' as g";
    private const string OuterWhere = "where search(g.Name, 'w1 w4 w12 w30 w90 w300')";
    private const string InnerQuery = "from index 'Users' where search(Name, 'w2 w6 w15 w40 w120 w400')";

    [JoinBenchmarkFact("RAVEN_JOIN_BYTES_BENCH", RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void Run()
    {
        var users = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_BYTES_BENCH"));
        var reps = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_BYTES_BENCH_REPS") ?? "15");
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
        report.AppendLine("query | keys | client wall ms (median / min) | server ms (median) | scan managed KB | outer match managed KB | whole join managed KB");
        var queries = new (string Name, string Rql)[]
        {
            ("id join, order by score()", $"{Games} join ({InnerQuery}) as u on g.OwnerId = id(u) {OuterWhere} order by score() limit 0, 20"),
            ("id join, no scores (order by g.Name)", $"{Games} join ({InnerQuery}) as u on g.OwnerId = id(u) {OuterWhere} order by g.Name limit 0, 20"),
            ("id join, inner sort (order by u.Postcode)", $"{Games} join ({InnerQuery}) as u on g.OwnerId = id(u) {OuterWhere} order by u.Postcode limit 0, 20"),
            ("field join Country, order by score()", $"{Games} join ({InnerQuery}) as u on g.Country = u.Country {OuterWhere} order by score() limit 0, 20"),
            ("field join Country, no scores", $"{Games} join ({InnerQuery}) as u on g.Country = u.Country {OuterWhere} order by g.Name limit 0, 20"),
            ("field join Postcode, order by score()", $"{Games} join ({InnerQuery}) as u on g.Postcode = u.Postcode {OuterWhere} order by score() limit 0, 20"),
            ("field join Postcode, no scores", $"{Games} join ({InnerQuery}) as u on g.Postcode = u.Postcode {OuterWhere} order by g.Name limit 0, 20"),
            ("field join Postcode, projection u.Name", $"{Games} join ({InnerQuery}) as u on g.Postcode = u.Postcode {OuterWhere} order by score() select g.Name as Name, u.Name as UserName limit 0, 20"),
        };

        foreach (var (name, rql) in queries)
        {
            for (var i = 0; i < 4; i++)
                Execute(store, rql, out _);

            var wall = new List<double>();
            var server = new List<double>();
            for (var i = 0; i < reps; i++)
            {
                wall.Add(Execute(store, rql, out var serverMs));
                server.Add(serverMs);
            }

            var scan = new List<double>();
            var outer = new List<double>();
            var total = new List<double>();
            long keys = 0;
            for (var i = 0; i < 5; i++)
            {
                using var session = store.OpenSession();
                var rows = session.Advanced.RawQuery<JoinScoreOrderTests.Doc>(rql + " include timings()").NoCaching().Timings(out var timings).ToList();
                Assert.Equal(20, rows.Count);
                var join = timings.Timings["Join"];
                var counters = join.Timings["u"].Counters;
                keys = counters["SourceMatches"];
                scan.Add(counters["ScanAllocatedManagedBytes"]);
                outer.Add(counters["OuterMatchAllocatedManagedBytes"]);
                total.Add(join.Counters["TotalAllocatedManagedBytes"]);
            }

            report.AppendLine($"{name} | {keys:N0} | {Median(wall):F1} / {wall.Min():F1} | {Median(server):F0} | {Median(scan) / 1024:N0} | {Median(outer) / 1024:N0} | {Median(total) / 1024:N0}");
        }

        output.WriteLine(report.ToString());
        AppendReport("RAVEN_JOIN_BYTES_BENCH_OUT", "joinbytes.txt", report);
    }

    private static double Execute(IDocumentStore store, string rql, out double server)
    {
        using var session = store.OpenSession();
        QueryStatistics stats = null;
        var sw = Stopwatch.StartNew();
        if (rql.Contains(" select "))
            session.Advanced.RawQuery<Projection>(rql).NoCaching().Statistics(out stats).ToList();
        else
            session.Advanced.RawQuery<JoinScoreOrderTests.Doc>(rql).NoCaching().Statistics(out stats).ToList();
        sw.Stop();
        server = stats.DurationInMs;
        return sw.Elapsed.TotalMilliseconds;
    }

    private sealed class Projection
    {
        public string Name { get; set; }
        public string UserName { get; set; }
    }
}
