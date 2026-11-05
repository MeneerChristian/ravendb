using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Config;
using Raven.Server.Documents.Queries;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;
using static FastTests.Corax.LoadDocumentVsJoinBenchmark;

namespace FastTests.Corax;

// Opt-in: RAVEN_LDJ_SHAPES=<messages> (1M in the report), RAVEN_LDJ_CONTEXTS=<contexts>, RAVEN_LDJ_IDS=short|guid, RAVEN_LDJ_MODE=ld|join, RAVEN_LDJ_REPS=<default 7>, RAVEN_LDJ_BATCH=<Indexing.MapBatchSize>, RAVEN_LDJ_OUT=<file>
// The data of JoinBigSubBenchmark plus a hub context (10% of the messages reference it, it matches no filter). One mode per test host.
// ld: one index on the messages that copies the context fields through LoadDocument. join: two indexes, queried with an id join: the whole join with stage 1 (encoded keys) off, on and off again, then the join of a page read from the first entries (JoinFirstEntriesSettings) with the total, and without it.
// RAVEN_LDJ_QUERYONLY=1 skips the measures of the build of the indexes and of the changes of a context.
public class LoadDocumentVsJoinShapesBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private class Message { public string ContextDocumentId; public string Text; }
    private class Context { public bool IsActive; public bool Third; public string Name; }
    private class IdRow { public string Id; }

    private const string HubId = "ConversationContexts/hub";
    private const string LdIndex = "LdMessageIndex";
    private const string JoinOuter = "ConversationMessageListIndex";
    private const string JoinInner = "SearchIndex";

    private class JoinMessageIndex : AbstractIndexCreationTask<Message>
    {
        public JoinMessageIndex()
        {
            Map = ms => from m in ms select new { m.ContextDocumentId, m.Text };
            Index(x => x.ContextDocumentId, FieldIndexing.Key);
        }

        public override string IndexName => JoinOuter;
    }

    private class JoinContextIndex : AbstractIndexCreationTask<Context>
    {
        public JoinContextIndex() => Map = cs => from c in cs select new { c.IsActive, c.Third, c.Name };
        public override string IndexName => JoinInner;
    }

    private class LoadDocumentMessageIndex : AbstractIndexCreationTask<Message>
    {
        public LoadDocumentMessageIndex()
        {
            Map = ms => from m in ms
                let c = LoadDocument<Context>(m.ContextDocumentId)
                select new { m.ContextDocumentId, m.Text, ContextIsActive = c.IsActive, ContextThird = c.Third, ContextName = c.Name };
            Index(x => x.ContextDocumentId, FieldIndexing.Key);
        }

        public override string IndexName => LdIndex;
    }

    private static bool IsHubMessage(int message) => message % 100 >= 90;

    private static Context MakeContext(int c, string name = null) => new() { IsActive = true, Third = c % 3 == 0, Name = name ?? (c % 10 == 0 ? "tenth" : "other") };

    [JoinBenchmarkFact("RAVEN_LDJ_SHAPES", RavenTestCategory.Corax | RavenTestCategory.Indexes | RavenTestCategory.Querying)]
    public void Run()
    {
        var messages = int.Parse(Environment.GetEnvironmentVariable("RAVEN_LDJ_SHAPES"));
        var contexts = int.Parse(Environment.GetEnvironmentVariable("RAVEN_LDJ_CONTEXTS") ?? "200000");
        var reps = int.Parse(Environment.GetEnvironmentVariable("RAVEN_LDJ_REPS") ?? "7");
        var mode = Environment.GetEnvironmentVariable("RAVEN_LDJ_MODE") ?? "join";
        var ids = Environment.GetEnvironmentVariable("RAVEN_LDJ_IDS") ?? "guid";
        var isJoin = mode == "join";
        var queryOnly = Environment.GetEnvironmentVariable("RAVEN_LDJ_QUERYONLY") == "1";
        var shape = $"{ids}-{contexts}";
        var indexNames = isJoin ? new[] { JoinOuter, JoinInner } : new[] { LdIndex };

        JoinEncodedKeys.Enabled = true;
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        var batch = Environment.GetEnvironmentVariable("RAVEN_LDJ_BATCH");
        if (string.IsNullOrEmpty(batch) == false)
            options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.MapBatchSize)] = batch;
        using var store = GetDocumentStore(options);
        using var footprint = new FootprintSampler();
        using var native = new NativeSampler();
        var process = Process.GetCurrentProcess();
        var report = new StringBuilder();

        void Emit(string phase, params (string Key, object Value)[] values)
        {
            var line = $"rec|{shape}|{mode}|{messages}|{phase}|" + string.Join(';', values.Select(x => FormattableString.Invariant($"{x.Key}={x.Value}")));
            report.AppendLine(line);
            output.WriteLine(line);
        }

        IndexStats Stats(string name) => store.Maintenance.Send(new GetIndexesStatisticsOperation()).First(x => x.Name == name);

        void WaitNonStale(params string[] names)
        {
            var deadline = DateTime.UtcNow.AddMinutes(60);
            while (DateTime.UtcNow < deadline)
            {
                var stats = store.Maintenance.Send(new GetIndexesStatisticsOperation());
                if (names.All(name => stats.Any(s => s.Name == name && s.IsStale == false)))
                    return;
                Thread.Sleep(50);
            }

            throw new TimeoutException("the indexes are still stale");
        }

        // runs the work, waits for the given indexes, and reports wall, process CPU, managed allocation, native and footprint peaks, disk written (a lower bound: memory-mapped writes are not counted), work counters
        void Measure(string phase, string[] names, Action work, params (string Key, object Value)[] extra)
        {
            var before = names.ToDictionary(x => x, Stats);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            footprint.TakePeakKb();
            var nativeBefore = global::Sparrow.Utils.NativeMemory.TotalAllocatedMemory;
            native.Reset();
            var load = Load1();
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var io = DiskIo();
            var cpu = process.TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            work();
            WaitNonStale(names);
            watch.Stop();
            var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            var io2 = DiskIo();
            var values = new List<(string, object)>
            {
                ("wallMs", watch.ElapsedMilliseconds),
                ("cpuMs", (long)cpuMs),
                ("allocMB", ((GC.GetTotalAllocatedBytes(precise: true) - allocated) / 1048576.0).ToString("F0", CultureInfo.InvariantCulture)),
                ("nativePeakDeltaMB", (native.Peak - nativeBefore) / 1048576),
                ("peakFootprintMB", footprint.TakePeakKb() / 1024),
                ("diskWrittenMB", Mb(io2.Written - io.Written)),
                ("load1", load)
            };
            foreach (var name in names)
            {
                var now = Stats(name);
                values.Add(($"{name}.mapAttempts", now.MapAttempts - before[name].MapAttempts));
                values.Add(($"{name}.refAttempts", (now.MapReferenceAttempts ?? 0) - (before[name].MapReferenceAttempts ?? 0)));
                values.Add(($"{name}.entries", now.EntriesCount));
            }

            values.AddRange(extra.Select(x => (x.Key, x.Value)));
            Emit(phase, values.Select(x => (x.Item1, x.Item2)).ToArray());
        }

        // the indexes exist before the data, so each has the default dictionary; indexing is stopped while the data goes in, so the build is measured on its own
        if (isJoin)
        {
            new JoinMessageIndex().Execute(store);
            new JoinContextIndex().Execute(store);
        }
        else
            new LoadDocumentMessageIndex().Execute(store);

        store.Maintenance.Send(new StopIndexingOperation());
        if (isJoin)
            store.Maintenance.Send(new DisableIndexOperation(JoinInner));

        var generated = Stopwatch.StartNew();
        using (var bulk = store.BulkInsert())
        {
            for (var c = 0; c < contexts; c++)
                bulk.Store(MakeContext(c), JoinBigSubBenchmark.ContextId(c));
            bulk.Store(new Context { IsActive = false, Third = false, Name = "hub" }, HubId);
            for (var m = 0; m < messages; m++)
            {
                var c = JoinBigSubBenchmark.ReferencedContext(m, contexts);
                var contextId = IsHubMessage(m) ? HubId : c >= 0 ? JoinBigSubBenchmark.ContextId(c) : $"other/{m % 5000}";
                bulk.Store(new Message { ContextDocumentId = contextId, Text = "t" }, $"messages/{m}");
            }
        }

        output.WriteLine($"data inserted in {generated.Elapsed.TotalSeconds:F0}s");
        var dataDirectory = store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database)).Settings[RavenConfiguration.GetKey(x => x.Core.DataDirectory)];

        // expected results, from the model
        long expectedAll = 0, expectedThird = 0, expectedTenth = 0, hubMessages = 0;
        for (var m = 0; m < messages; m++)
        {
            if (IsHubMessage(m))
            {
                hubMessages++;
                continue;
            }

            var c = JoinBigSubBenchmark.ReferencedContext(m, contexts);
            if (c < 0)
                continue;
            expectedAll++;
            if (c % 3 == 0)
                expectedThird++;
            if (c % 10 == 0)
                expectedTenth++;
        }

        Emit("0-data", ("messages", messages), ("contexts", contexts), ("hubMessages", hubMessages), ("expectedAll", expectedAll), ("expectedThird", expectedThird), ("expectedTenth", expectedTenth));

        // (1) indexing: the outer index (the one that serves the query) on its own, then the joined index
        var outerName = isJoin ? JoinOuter : LdIndex;
        Measure("1a-index-outer", [outerName], () => store.Maintenance.Send(new StartIndexingOperation()));
        if (isJoin)
            Measure("1b-index-inner", [JoinInner], () => store.Maintenance.Send(new EnableIndexOperation(JoinInner)));

        var database = Databases.GetDocumentDatabaseInstanceFor(store).GetAwaiter().GetResult();
        if (queryOnly == false)
        {
            var indexKb = new Dictionary<string, long>();
            foreach (var folder in Directory.GetDirectories(Path.Combine(dataDirectory, "Indexes")))
                indexKb[Path.GetFileName(folder)] = DirectoryKb(folder);
            Emit("1c-index-size", indexKb.Select(x => ($"indexKB.{x.Key}", (object)x.Value)).Append(("indexKB.total", indexKb.Values.Sum())).ToArray());

            var storage = indexNames.Select(name => (name, stats: database.IndexStore.GetIndex(name)._environment.Stats())).ToList();
            Emit("1d-index-storage", storage.SelectMany(x => new (string, object)[] { ($"usedKB.{x.name}", x.stats.UsedDataFileSizeInBytes / 1024), ($"allocatedKB.{x.name}", x.stats.AllocatedDataFileSizeInBytes / 1024) }).ToArray());

            foreach (var name in indexNames)
            {
                var environment = database.IndexStore.GetIndex(name)._environment;
                using var transaction = environment.ReadTransaction();
                var detailed = environment.GenerateDetailedReport(transaction, false);
                var biggest = detailed.Trees.Select(x => (Name: "tree " + x.Name, Bytes: x.AllocatedSpaceInBytes, Entries: x.NumberOfEntries))
                    .Concat(detailed.Tables.Select(x => (Name: "table " + x.Name, Bytes: x.AllocatedSpaceInBytes, Entries: x.NumberOfEntries)))
                    .OrderByDescending(x => x.Bytes).Take(6);
                Emit("1e-biggest-structures", [("index", name), .. biggest.Select(x => (x.Name, (object)$"{x.Bytes / 1048576}MB/{x.Entries}entries"))]);
            }


        }

        // (2) queries
        (string Name, string Rql, long Expected, Func<int, bool> Match)[] queries = isJoin
            ?
            [
                ("plain", $"from index '{JoinOuter}' as m select id() limit 0, 5", messages, null),
                ("all", $"from index '{JoinOuter}' as m join (from index '{JoinInner}' where IsActive = true) as c on m.ContextDocumentId = id(c) select id() limit 0, 5", expectedAll, c => true),
                ("third", $"from index '{JoinOuter}' as m join (from index '{JoinInner}' where Third = true) as c on m.ContextDocumentId = id(c) select id() limit 0, 5", expectedThird, c => c % 3 == 0),
                ("tenth", $"from index '{JoinOuter}' as m join (from index '{JoinInner}' where Name = 'tenth') as c on m.ContextDocumentId = id(c) select id() limit 0, 5", expectedTenth, c => c % 10 == 0)
            ]
            :
            [
                ("plain", $"from index '{LdIndex}' select id() limit 0, 5", messages, null),
                ("all", $"from index '{LdIndex}' where ContextIsActive = true select id() limit 0, 5", expectedAll, c => true),
                ("third", $"from index '{LdIndex}' where ContextThird = true select id() limit 0, 5", expectedThird, c => c % 3 == 0),
                ("tenth", $"from index '{LdIndex}' where ContextName = 'tenth' select id() limit 0, 5", expectedTenth, c => c % 10 == 0)
            ];

        // the document set of every filter against the oracle (every message that references a context that passes the filter)
        foreach (var (name, rql, expected, match) in queries.Where(x => x.Match != null))
        {
            var expectedIds = new HashSet<string>();
            for (var m = 0; m < messages; m++)
            {
                var c = JoinBigSubBenchmark.ReferencedContext(m, contexts);
                if (IsHubMessage(m) == false && c >= 0 && match(c))
                    expectedIds.Add($"messages/{m}");
            }

            using var session = store.OpenSession();
            var rows = session.Advanced.RawQuery<IdRow>(rql.Replace("select id() limit 0, 5", $"select id() as Id limit 0, {messages + 10}")).NoCaching().ToList();
            var got = rows.Select(x => x.Id).ToList();
            var same = got.Count == expectedIds.Count && expectedIds.SetEquals(got);
            Assert.True(same, $"{mode} {name}: the result is not the oracle set ({got.Count} against {expectedIds.Count})");
            Emit("2a-verify", ("query", name), ("rows", got.Count), ("sameSetAsOracle", same), ("first5", string.Join(',', got.Take(5))));
        }

        void RunPass(string pass, bool withTotal = true)
        {
            foreach (var (name, rql, expected, _) in queries)
            {
                for (var i = 0; i < 2; i++)
                    Execute(rql, out _, withTotal);

                var ms = new List<double>();
                for (var i = 0; i < reps; i++)
                {
                    var total = Execute(rql, out var server, withTotal);
                    if (withTotal)
                        Assert.Equal(expected, total);
                    ms.Add(server);
                }

                var counters = new List<Dictionary<string, long>>();
                for (var i = 0; i < 3; i++)
                {
                    using var session = store.OpenSession();
                    session.Advanced.RawQuery<object>(rql + " include timings()").NoCaching().Timings(out var timings).ToList();
                    var map = new Dictionary<string, long>();
                    Collect(timings, map);
                    counters.Add(map);
                }

                const int cpuRuns = 10;
                GC.Collect();
                var load = Load1();
                var cpu = process.TotalProcessorTime;
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < cpuRuns; i++)
                    Execute(rql, out _, withTotal);
                watch.Stop();
                var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds / cpuRuns;

                long Med(string key) => (long)Median(counters.Select(x => (double)x.GetValueOrDefault(key)));
                Emit("2b-query", ("pass", pass), ("withTotal", withTotal), ("query", name), ("expected", expected), ("serverMsMedian", Median(ms).ToString("F1", CultureInfo.InvariantCulture)), ("serverMsMin", ms.Min()), ("serverMsMax", ms.Max()),
                    ("cpuMsPerQuery", cpuMs.ToString("F1", CultureInfo.InvariantCulture)), ("wallMsPerQuery", (watch.Elapsed.TotalMilliseconds / cpuRuns).ToString("F1", CultureInfo.InvariantCulture)),
                    ("managedKB", Math.Max(Med("AllocatedManagedBytes"), Med("TotalAllocatedManagedBytes")) / 1024), ("nativeHeldKB", Math.Max(Med("HeldUnmanagedBytesPeak"), Med("ScanHeldUnmanagedBytes")) / 1024),
                    ("keysEncoded", Med("KeysEncoded")), ("firstEntries", Med("JoinFirstEntries")), ("firstScanned", Med("JoinFirstEntriesScanned")), ("firstProbed", Med("JoinFirstEntriesKeysProbed")), ("firstFromLists", Med("JoinFirstEntriesFromLists")), ("firstUs", Med("JoinFirstEntriesMicroseconds")), ("load1", load));
            }
        }

        // without the statistics the server does not count the matches of the query
        long Execute(string rql, out double serverMs, bool withTotal = true)
        {
            using var session = store.OpenSession();
            if (withTotal == false)
            {
                session.Advanced.RawQuery<object>(rql + " include timings()").NoCaching().Timings(out var timings).ToList();
                serverMs = timings.DurationInMs;
                return -1;
            }

            session.Advanced.RawQuery<object>(rql).NoCaching().Statistics(out var stats).ToList();
            serverMs = stats.DurationInMs;
            return stats.TotalResults;
        }

        List<string> FirstIds(string rql)
        {
            using var session = store.OpenSession();
            return session.Advanced.RawQuery<IdRow>(rql.Replace("select id() limit 0, 5", "select id() as Id limit 0, 5")).NoCaching().ToList().Select(x => x.Id).ToList();
        }

        if (isJoin)
        {
            foreach (var (pass, encoded, firstEntries, withTotal) in new[]
                     {
                         ("whole-s1off", false, false, true), ("whole-s1on", true, false, true), ("whole-s1off-again", false, false, true),
                         ("first-s1off", false, true, true), ("first-s1on", true, true, true), ("first-s1on-nototal", true, true, false), ("first-s1off-nototal", false, true, false),
                         ("whole-s1on-nototal", true, false, false)
                     })
            {
                JoinEncodedKeys.Enabled = encoded;
                JoinFirstEntriesSettings.Enabled = firstEntries;
                RunPass(pass, withTotal);
            }

            // the first rows of both paths are the same
            foreach (var (name, rql, _, _) in queries.Where(x => x.Match != null))
            {
                JoinFirstEntriesSettings.Enabled = false;
                var whole = FirstIds(rql);
                JoinFirstEntriesSettings.Enabled = true;
                var first = FirstIds(rql);
                Assert.True(whole.SequenceEqual(first), $"{name}: the first rows differ: {string.Join(',', whole)} against {string.Join(',', first)}");
            }

            JoinEncodedKeys.Enabled = true;
            JoinFirstEntriesSettings.Enabled = true;
        }
        else
            RunPass("loaddocument");

        if (queryOnly)
        {
            AppendReport("RAVEN_LDJ_OUT", "ldj-shapes.txt", report);
            return;
        }

        // (3) one change to a context: a context that some messages reference, then the hub
        var typical = JoinBigSubBenchmark.ReferencedContext(1, contexts);
        var typicalFanOut = 0;
        for (var m = 0; m < messages; m++)
            if (IsHubMessage(m) == false && JoinBigSubBenchmark.ReferencedContext(m, contexts) == typical)
                typicalFanOut++;

        void Update(string phase, string id, Context changed, long fanOut)
        {
            store.Maintenance.Send(new StopIndexingOperation());
            using (var bulk = store.BulkInsert())
                bulk.Store(changed, id);
            Measure(phase, indexNames, () => store.Maintenance.Send(new StartIndexingOperation()), ("referencingMessages", fanOut));
        }

        Update("3a-update-typical-context", JoinBigSubBenchmark.ContextId(typical), MakeContext(typical, "changed"), typicalFanOut);
        Update("3b-update-hub-context", HubId, new Context { IsActive = false, Third = false, Name = "hub2" }, hubMessages);

        AppendReport("RAVEN_LDJ_OUT", "ldj-shapes.txt", report);
    }

    private static void Collect(QueryTimings timings, Dictionary<string, long> counters)
    {
        if (timings.Counters != null)
            foreach (var c in timings.Counters)
                if (c.Key is "AllocatedManagedBytes" or "TotalAllocatedManagedBytes" or "HeldUnmanagedBytesPeak" or "ScanHeldUnmanagedBytes" or "KeysEncoded" or "JoinFirstEntries" or "JoinFirstEntriesScanned" or "JoinFirstEntriesKeysProbed" or "JoinFirstEntriesFromLists" or "JoinFirstEntriesMicroseconds")
                    counters[c.Key] = Math.Max(counters.GetValueOrDefault(c.Key), Convert.ToInt64(c.Value));

        if (timings.Timings != null)
            foreach (var child in timings.Timings.Values)
                Collect(child, counters);
    }
}
