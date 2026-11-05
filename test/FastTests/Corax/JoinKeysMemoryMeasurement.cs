using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.BulkInsert;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Raven.Server.Config;
using Raven.Server.ServerWide;
using Sparrow;
using Sparrow.Server.LowMemory;
using Tests.Infrastructure;
using Xunit;
using EngineType = Raven.Client.Documents.Indexes.SearchEngineType;

namespace FastTests.Corax;

// What a JOIN of the server does when its sub-query matches many documents: it answers within Query.JoinMaxKeysMemoryInMb or it fails with the error that names the setting.
// RAVEN_JOIN_KEYS_MEMORY=<matched documents> runs it; RAVEN_JOIN_KEYS_MEMORY_LIMIT_MB overrides the default of the setting; the outcome and the exact counters are appended to RAVEN_JOIN_KEYS_MEMORY_OUT.
public class JoinKeysMemoryMeasurement(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Join = "from index 'Games' as g join (from index 'Users' where Kind = 'vip') as u on g.OwnerId = id(u) order by id() limit 0, 10 include timings()";

    private sealed class Game
    {
        public string Id { get; set; }
        public string OwnerId { get; set; }
    }

    private sealed class User
    {
        public string Id { get; set; }
        public string Kind { get; set; }
    }

    private sealed class Games : AbstractIndexCreationTask<Game>
    {
        public Games()
        {
            Map = games => from g in games select new { g.OwnerId };
            SearchEngineType = EngineType.Corax;
        }
    }

    private sealed class Users : AbstractIndexCreationTask<User>
    {
        public Users()
        {
            Map = users => from u in users select new { u.Kind };
            SearchEngineType = EngineType.Corax;
        }
    }

    [JoinBenchmarkFact("RAVEN_JOIN_KEYS_MEMORY", RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AJoinOfManyMatchedDocumentsAnswersWithinTheLimitOrNamesTheSetting()
    {
        int matched = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_KEYS_MEMORY"));
        string limitOverride = Environment.GetEnvironmentVariable("RAVEN_JOIN_KEYS_MEMORY_LIMIT_MB");
        var settings = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(limitOverride) == false)
            settings[RavenConfiguration.GetKey(x => x.Queries.JoinMaxKeysMemory)] = limitOverride;

        var server = GetNewServer(new ServerCreationOptions { CustomSettings = settings });
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.Server = server;
        using IDocumentStore store = GetDocumentStore(options);
        new Games().Execute(store);
        new Users().Execute(store);

        using (BulkInsertOperation bulk = store.BulkInsert())
        {
            for (int i = 0; i < matched; i++)
                bulk.Store(new User { Id = $"users/{i}", Kind = "vip" });

            for (int i = 0; i < 100; i++)
                bulk.Store(new Game { Id = $"games/{i:D3}", OwnerId = $"users/{(i * 7919L) % matched}" });
        }

        Indexes.WaitForIndexing(store, timeout: TimeSpan.FromMinutes(30));

        long limitBytes = server.Configuration.Queries.JoinMaxKeysMemory.GetValue(SizeUnit.Bytes);
        string outcome;
        using (IDocumentSession session = store.OpenSession(new SessionOptions { NoCaching = true }))
        {
            try
            {
                List<Game> rows = session.Advanced.RawQuery<Game>(Join).Timings(out QueryTimings timings).Statistics(out QueryStatistics stats).ToList();
                var counters = new Dictionary<string, long>();
                Collect(timings, "", counters);
                outcome = $"ANSWERED rows {rows.Count} total {stats.TotalResults} SourceMatches {counters["Join/u/SourceMatches"]} ScanHeldUnmanagedBytes {counters["Join/u/ScanHeldUnmanagedBytes"]}";
                Assert.Equal(100, stats.TotalResults);
                Assert.Equal(matched, counters["Join/u/SourceMatches"]);
            }
            catch (InvalidQueryException e)
            {
                outcome = $"REFUSED {e.Message.Split('\n')[0]}";
                Assert.Contains("Query.JoinMaxKeysMemoryInMb", e.Message);
            }
        }

        // the server is alive and answers
        using (IDocumentSession session = store.OpenSession())
            Assert.Equal(100, session.Query<Game>("Games").Count());

        string report = $"matched {matched:N0} limit {limitBytes:N0} B: {outcome}";
        output.WriteLine(report);
        JoinTestHelpers.AppendReport("RAVEN_JOIN_KEYS_MEMORY_OUT", "join-keys-memory.txt", report);
    }

    private static void Collect(QueryTimings timings, string path, Dictionary<string, long> result)
    {
        foreach ((string name, long value) in timings.Counters ?? new Dictionary<string, long>())
            result[path + name] = value;

        foreach ((string name, QueryTimings child) in timings.Timings ?? new Dictionary<string, QueryTimings>())
            Collect(child, path + name + "/", result);
    }
}
