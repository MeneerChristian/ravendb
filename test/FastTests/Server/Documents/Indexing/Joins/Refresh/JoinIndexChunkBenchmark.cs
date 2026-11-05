using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NativeMemory = Sparrow.Utils.NativeMemory;
using System.Threading.Tasks;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Sparrow.Utils;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

// Opt-in: RAVEN_JOIN_CHUNK_BENCH=<number of outer documents, for example 150000>; the results are appended to RAVEN_JOIN_CHUNK_BENCH_FILE when it is set.
// What the join index needs to follow a change of a share of the outer documents (a change of a field that is not the key, and a change of the key) and of the inner documents, applied in chunks
// of Join.MaxIdsPerCommit, against building the lists again for the same state (the cost of the reset that a too large change used to cause).
public class JoinIndexChunkBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private const int Contexts = 2000;

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task IncrementalChunksAgainstRebuild()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("RAVEN_JOIN_CHUNK_BENCH"), out int outer) == false)
            return;

        int budget = int.TryParse(Environment.GetEnvironmentVariable("RAVEN_JOIN_CHUNK_BUDGET"), out int b) ? b : 50_000;
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = budget.ToString(),

            // one transaction of the change is one batch of the upstream indexes: the join index sees all of it in one refresh
            ["Indexing.MapBatchSize"] = "5000000"
        }, putJoin: false, runInMemory: false);

        await Fill(h, outer, 0);
        await h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition());
        TimeSpan initial = await WaitJoinAsync(h);
        Report($"outer {outer}, inner {Contexts}, budget {budget}: initial build {initial.TotalSeconds:F2} s, {outer / initial.TotalSeconds:F0} entries/s");

        int round = 0;
        foreach (string kind in new[] { "rank-only", "re-key", "inner" })
        {
            foreach (int percent in new[] { 10, 50, 100 })
            {
                round++;
                int count = kind == "inner" ? Contexts * percent / 100 : (int)((long)outer * percent / 100);
                JoinIndex joinIndex = h.Join();
                long ticksBefore = joinIndex.Counters.TotalRefreshTicks;
                long refreshesBefore = joinIndex.Counters.Refreshes;
                long partial = joinIndex.Counters.PartialRefreshes;
                var wall = Stopwatch.StartNew();

                if (kind == "inner")
                {
                    h.Save(s =>
                    {
                        for (int i = 0; i < count; i++)
                            s.Store(new JoinIndexHarness.Context { Country = (round + i) % 2 == 0 ? "nl" : "de", Name = "n" + (i + round) % 7, Rank = i + round, IsActive = (i + round) % 3 != 0, Title = "red fox " + (i + round) % 5 }, $"contexts/{i}");
                    });
                }
                else
                {
                    int shift = round;
                    await StoreConversations(h, count, i => $"contexts/{(kind == "re-key" ? (i + shift) % Contexts : i % Contexts)}", kind == "rank-only" ? "changed" + round : "t", oneTransaction: true);
                }

                await WaitIndexesAsync(h);
                TimeSpan upstream = wall.Elapsed;
                await WaitJoinAsync(h);
                TimeSpan total = wall.Elapsed;
                Assert.True(ReferenceEquals(joinIndex, h.Join()), "the join index was reset");
                double busy = (joinIndex.Counters.TotalRefreshTicks - ticksBefore) / (double)Stopwatch.Frequency;
                await h.AssertCurrentAsync();
                Assert.DoesNotContain(joinIndex.Counters.RebuildsByReason.Keys, x => x != nameof(JoinRebuildReason.Initial));
                Report($"{kind,9} {percent,3}% ({count} documents): join index busy {busy:F2} s in {joinIndex.Counters.Refreshes - refreshesBefore} refreshes ({joinIndex.Counters.PartialRefreshes - partial} partial), {count / busy:F0} documents/s; wall from the write to the join index current {total.TotalSeconds:F2} s (upstream indexes current after {upstream.TotalSeconds:F2} s)");
            }
        }

        await h.Store.Maintenance.SendAsync(new ResetIndexOperation(JoinIndexHarness.JoinName));
        var sw = Stopwatch.StartNew();
        await Task.Delay(300);
        await WaitJoinAsync(h);
        TimeSpan rebuild = sw.Elapsed;
        double built = h.Join().Counters.TotalBuildTicks / (double)Stopwatch.Frequency;
        Report($"rebuild of the same state: wall from the reset {rebuild.TotalSeconds:F2} s, build itself {built:F2} s, {outer / built:F0} entries/s");
    }

    // Opt-in: RAVEN_JOIN_CHUNK_FF=<outer documents>,<inner documents>[,<hub keys>[,<outer entries per hub key>]]
    // A join on a field whose inner documents sit on a few hub keys (the shape of a bulk change of the inner side): changes 10, 50 and 100 percent of the inner documents (a tag, a delete,
    // an insert) in one transaction each and reports what the join index needed, with the breakdown of every refresh, against building the lists again.
    [RavenFact(RavenTestCategory.Indexes)]
    public async Task HubKeysOfTheInnerSideAgainstRebuild()
    {
        string[] parts = (Environment.GetEnvironmentVariable("RAVEN_JOIN_CHUNK_FF") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return;

        int outer = int.Parse(parts[0]);
        int inner = int.Parse(parts[1]);
        int hubs = parts.Length > 2 ? int.Parse(parts[2]) : 5;
        int perHub = parts.Length > 3 ? int.Parse(parts[3]) : 20_000;
        string shape = parts.Length > 4 ? parts[4] : "hub";
        bool unique = shape == "unique";
        bool ff1 = shape == "ff1";
        int budget = int.TryParse(Environment.GetEnvironmentVariable("RAVEN_JOIN_CHUNK_BUDGET"), out int b) ? b : 50_000;

        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = budget.ToString(),
            [RavenConfiguration.GetKey(x => x.Join.MaxInnerListChangesPerOuterEntry)] = (Environment.GetEnvironmentVariable("RAVEN_JOIN_CHUNK_LIMIT") ?? "0"),
            [RavenConfiguration.GetKey(x => x.Join.MaxQueuedListChanges)] = (Environment.GetEnvironmentVariable("RAVEN_JOIN_CHUNK_QUEUE") ?? "4194304"),
            [RavenConfiguration.GetKey(x => x.Join.MaxQueuedListChangesInBuild)] = (Environment.GetEnvironmentVariable("RAVEN_JOIN_CHUNK_BUILDQ") ?? "1048576"),
            ["Indexing.MapBatchSize"] = "5000000"
        }, putJoin: false, runInMemory: false);

        // the inner documents are spread over the hub keys, a few have a key of their own; the outer entries name a hub key or nothing that exists
        // shape "unique": every changed inner document has a key of its own that some outer entries name, and the 100 documents on the hub keys do not change
        // shape "ff1": 20 inner documents per key (5,000 keys for 100,000 documents), a tag of 100 values and a rare value of 2,000 per document, the outer entries on those keys and 10 percent on the hub keys
        static long Hash(int salt, long i)
        {
            // splitmix64: independent values for the salts, no pattern in the low bits
            ulong z = (ulong)i + 0x9E3779B97F4A7C15UL * (ulong)(salt + 1);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (long)((z ^ (z >> 31)) & 0x7fffffffUL);
        }
        int innerKeys = Math.Max(1, inner / 20);
        string InnerCode(int i) => ff1 ? (i < inner ? "k/" + i / 20 : "k/hub" + (i - inner) / 20 % hubs)
            : unique ? (i < inner ? "u" + i : "h" + (i - inner) % hubs) : (i < inner ? "h" + i % hubs : "own" + i);
        JoinIndexHarness.Context InnerDocument(int i, int version) => ff1
            ? new() { Code = InnerCode(i), Country = "t" + (Hash(32, i) + version) % 100, Name = "r" + Hash(33, i) % 2000, Rank = 0, IsActive = true, Title = "x" }
            : new() { Code = InnerCode(i), Country = i % 2 == 0 ? "nl" : "de", Name = "n" + i % 5, Rank = i % 10, IsActive = i % 3 != 0, Title = "tag" + (i + version) % 3 };
        string OuterCode(int i) => ff1 ? (i % 10 == 0 ? "k/hub" + Hash(30, i) % hubs : "k/" + Hash(31, i) % innerKeys)
            : i < hubs * perHub ? "h" + i % hubs : unique ? "u" + i % inner : "none" + i;

        await using (var bulk = h.Store.BulkInsert())
        {
            for (int i = 0; i < inner + 100; i++)
                await bulk.StoreAsync(InnerDocument(i, 0), $"contexts/{i}");

            for (int i = 0; i < outer; i++)
                await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextId = OuterCode(i), Title = "t" }, $"conversations/{i}");
        }

        TimeSpan initial;
        string initialMemory;
        using (var memory = new MemorySampler())
        {
            await h.PutJoinAsync(JoinIndexHarness.FieldJoinIndexDefinition());
            initial = await WaitJoinAsync(h);
            initialMemory = memory.Describe();
        }

        Report($"field join, outer {outer}, inner {inner + 100} on {hubs} hub keys, {perHub} outer entries per hub key, budget {budget}: initial build {initial.TotalSeconds:F2} s, {initialMemory}");

        int round = 0;
        int[] shares = (Environment.GetEnvironmentVariable("RAVEN_JOIN_CHUNK_SHARES") ?? "10,50,100").Split(',').Select(int.Parse).ToArray();
        int reps = int.TryParse(Environment.GetEnvironmentVariable("RAVEN_JOIN_CHUNK_REPS"), out int r) ? r : 1;
        bool check = Environment.GetEnvironmentVariable("RAVEN_JOIN_CHUNK_CHECK") != "0";
        for (int rep = 0; rep < reps; rep++)
        foreach (int percent in shares)
        {
            int count = inner * percent / 100;
            foreach (string kind in ff1 ? new[] { "tag", "insert", "delete" } : new[] { "tag", "delete", "insert" })
            {
                round++;
                JoinIndex joinIndex = h.Join();
                long ticksBefore = joinIndex.Counters.TotalRefreshTicks;
                long refreshesBefore = joinIndex.Counters.Refreshes;
                var wall = Stopwatch.StartNew();
                bool isolate = Environment.GetEnvironmentVariable("RAVEN_JOIN_CHUNK_ISOLATE") == "1";
                if (isolate)
                {
                    // the join index waits while the documents and the upstream indexes are written, so its memory is the only thing that moves while it catches up
                    await h.Store.Maintenance.SendAsync(new StopIndexOperation(JoinIndexHarness.JoinName));
                    await Task.Delay(300);
                }

                MemorySampler cellMemory = isolate ? null : new MemorySampler();
                var seen = new List<string>();
                using var cts = new System.Threading.CancellationTokenSource();
                Task watcher = Task.Run(async () =>
                {
                    while (cts.IsCancellationRequested == false)
                    {
                        string line = joinIndex.Counters.LastBreakdown;
                        if (line != null && (seen.Count == 0 || seen[^1] != line))
                            seen.Add(line);

                        await Task.Delay(20);
                    }
                });

                using (var session = h.Store.OpenSession())
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (ff1)
                        {
                            // the tag update changes documents that exist; insert and delete use documents of their own, on random keys
                            string fresh = $"contexts/bk{percent}-{i}";
                            if (kind == "tag")
                                session.Store(InnerDocument(i, round), $"contexts/{i}");
                            else if (kind == "insert")
                                session.Store(new JoinIndexHarness.Context { Code = "k/" + Hash(34, 7_000_000 + percent * inner + i) % innerKeys, Country = "t" + Hash(35, i) % 100, Name = "r" + Hash(36, i) % 2000, Rank = 0, IsActive = true, Title = "x" }, fresh);
                            else
                                session.Delete(fresh);

                            continue;
                        }

                        if (kind == "delete")
                            session.Delete($"contexts/{i}");
                        else
                            session.Store(InnerDocument(i, kind == "tag" ? round : 0), $"contexts/{i}");
                    }

                    session.SaveChanges();
                }

                await WaitIndexesAsync(h);
                if (isolate)
                {
                    cellMemory = new MemorySampler();
                    ticksBefore = joinIndex.Counters.TotalRefreshTicks;
                    wall.Restart();
                    await h.Store.Maintenance.SendAsync(new StartIndexOperation(JoinIndexHarness.JoinName));
                }

                await WaitJoinAsync(h);
                cts.Cancel();
                await watcher;
                Assert.True(ReferenceEquals(joinIndex, h.Join()), "the join index was reset");
                double busy = (joinIndex.Counters.TotalRefreshTicks - ticksBefore) / (double)Stopwatch.Frequency;
                Report($"rep {rep} {kind,6} {percent,3}% ({count} inner documents): join index busy {busy:F2} s in {joinIndex.Counters.Refreshes - refreshesBefore} refreshes, wall {wall.Elapsed.TotalSeconds:F2} s, estimated list changes of the last refresh {joinIndex.Counters.LastListChangeEstimate:N0}, queue limit {joinIndex.Counters.LastQueueLimit:N0}, {cellMemory.Describe()}{(isolate ? " (joinIndex alone)" : "")}");
                cellMemory.Dispose();
                foreach (string line in seen)
                    Report("        " + line);

                // exact after every change, not only at the end
                if (check)
                    await h.AssertCurrentAsync();
            }
        }

        if (check)
            await h.AssertCurrentAsync();

        using (var rebuildMemory = new MemorySampler())
        {
            await h.Store.Maintenance.SendAsync(new ResetIndexOperation(JoinIndexHarness.JoinName));
            var sw = Stopwatch.StartNew();
            await Task.Delay(300);
            await WaitJoinAsync(h);
            Report($"rebuild of the same state: wall from the reset {sw.Elapsed.TotalSeconds:F2} s, build itself {h.Join().Counters.TotalBuildTicks / (double)Stopwatch.Frequency:F2} s, {rebuildMemory.Describe()}");
        }
    }

    // Opt-in: RAVEN_JOIN_QUEUE_BENCH=<outer documents>,<outer entries per key>,<queue limit in list changes>,<setup|change>[,<Join.MinAtomEntriesForDirectWrite, default 1>[,<file>]]
    // One process per case: "setup" writes the documents and builds the lists and stops, "change" does the same and then changes the Country of every inner document in one transaction (two list changes for each outer entry of its key), which the join index follows in one refresh.
    // The cost of the change is the cost of the "change" process minus the cost of the "setup" process (measured from outside, for example with /usr/bin/time -l). The line it reports has the exact counters of the refresh.
    [RavenFact(RavenTestCategory.Indexes)]
    public async Task QueueLimitOfOneInnerChange()
    {
        string[] parts = (Environment.GetEnvironmentVariable("RAVEN_JOIN_QUEUE_BENCH") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return;

        int outer = int.Parse(parts[0]);
        int rows = int.Parse(parts[1]);
        int queue = int.Parse(parts[2]);
        bool change = parts[3] == "change";
        int inner = outer / rows;
        string minAtomEntries = parts.Length > 4 ? parts[4] : "1";

        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.MaxInnerListChangesPerOuterEntry)] = "0",
            [RavenConfiguration.GetKey(x => x.Join.MaxQueuedListChanges)] = queue.ToString(),
            [RavenConfiguration.GetKey(x => x.Join.MinAtomEntriesForDirectWrite)] = minAtomEntries,
            ["Indexing.MapBatchSize"] = "5000000"
        }, putJoin: false, runInMemory: false);

        JoinIndexHarness.Context Inner(int i, int version) => new() { Code = "u" + i, Country = (i + version) % 2 == 0 ? "nl" : "de", Name = "n" + i % 5, Rank = i % 10, IsActive = i % 3 != 0, Title = "tag" + i % 3 };

        await using (var bulk = h.Store.BulkInsert())
        {
            for (int i = 0; i < inner; i++)
                await bulk.StoreAsync(Inner(i, 0), $"contexts/{i}");

            for (int i = 0; i < outer; i++)
                await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextId = "u" + i % inner, Title = "t" }, $"conversations/{i}");
        }

        await h.PutJoinAsync(JoinIndexHarness.FieldJoinIndexDefinition());
        try
        {
            await WaitJoinAsync(h);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("errors of the join index: " + string.Join(" | ", h.Join().GetErrors().Select(x => x.Error)));
        }

        await WaitIndexesAsync(h);
        JoinIndex joinIndex = h.Join();
        JoinCounters c = joinIndex.Counters;
        long refreshes = c.Refreshes, commits = c.Commits, flushes = c.ListFlushes, threshold = c.ThresholdFlushes, queued = c.QueuedOps, updates = c.ListUpdates, allocated = c.RefreshAllocatedBytes, ids = c.IdsProcessed;

        if (change)
        {
            using (var session = h.Store.OpenSession())
            {
                for (int i = 0; i < inner; i++)
                    session.Store(Inner(i, 1), $"contexts/{i}");

                session.SaveChanges();
            }

            await WaitIndexesAsync(h);
            await WaitJoinAsync(h);
            Assert.True(ReferenceEquals(joinIndex, h.Join()), "the join index was reset");
            await h.AssertCurrentAsync();
        }

        string line = $"QUEUE outer {outer} rows {rows} queue {queue} direct-write from {minAtomEntries} entries {parts[3]}: refreshes {c.Refreshes - refreshes}, commits {c.Commits - commits}, ids {c.IdsProcessed - ids}, list flushes {c.ListFlushes - flushes} (threshold {c.ThresholdFlushes - threshold}), queued ops {c.QueuedOps - queued}, list updates {c.ListUpdates - updates}, " +
                      $"estimate of the last refresh {c.LastListChangeEstimate}, queue limit {c.LastQueueLimit}, allocated by the refreshes {c.RefreshAllocatedBytes - allocated}";
        Report(line);
        if (parts.Length > 5)
            File.AppendAllText(parts[5], line + Environment.NewLine);
    }

    // Opt-in: RAVEN_JOIN_QUEUE_ROUNDS=<outer documents>,<outer entries per key>,<rounds>,<queue limits separated by ;>,<Join.MinAtomEntriesForDirectWrite>[,<file>] (macOS: the instructions retired are read from proc_pid_rusage)
    // One process builds the lists once and then changes the Country of every inner document <rounds> times for every queue limit (the order of the limits rotates from round to round), each change being one refresh. The join index is stopped while
    // the documents and the upstream indexes are written and started for the refresh, so what is measured, the instructions retired and the cycles of the process from the start of the join index until it is current, is the refresh and the polling of the test.
    // The first round is a warm-up and is not reported. Every line has the exact counters of the refresh.
    [RavenFact(RavenTestCategory.Indexes)]
    public async Task QueueLimitsOfInnerChangesInOneProcess()
    {
        string[] parts = (Environment.GetEnvironmentVariable("RAVEN_JOIN_QUEUE_ROUNDS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
            return;

        int outer = int.Parse(parts[0]);
        int rows = int.Parse(parts[1]);
        int rounds = int.Parse(parts[2]);
        int[] queues = parts[3].Split(';').Select(int.Parse).ToArray();
        int inner = outer / rows;

        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.MaxInnerListChangesPerOuterEntry)] = "0",
            [RavenConfiguration.GetKey(x => x.Join.MinAtomEntriesForDirectWrite)] = parts[4],
            ["Indexing.MapBatchSize"] = "5000000"
        }, putJoin: false, runInMemory: false);

        JoinIndexHarness.Context Inner(int i, int version) => new() { Code = "u" + i, Country = (i + version) % 2 == 0 ? "nl" : "de", Name = "n" + i % 5, Rank = i % 10, IsActive = i % 3 != 0, Title = "tag" + i % 3 };

        await using (var bulk = h.Store.BulkInsert())
        {
            for (int i = 0; i < inner; i++)
                await bulk.StoreAsync(Inner(i, 0), $"contexts/{i}");

            for (int i = 0; i < outer; i++)
                await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextId = "u" + i % inner, Title = "t" }, $"conversations/{i}");
        }

        await h.PutJoinAsync(JoinIndexHarness.FieldJoinIndexDefinition());
        await WaitJoinAsync(h);
        await WaitIndexesAsync(h);
        JoinIndex joinIndex = h.Join();
        JoinCounters c = joinIndex.Counters;

        int version = 0;
        for (int round = 0; round <= rounds; round++)
        {
            for (int k = 0; k < queues.Length; k++)
            {
                int queue = queues[(k + round) % queues.Length];
                version++;
                await h.Store.Maintenance.SendAsync(new StopIndexOperation(JoinIndexHarness.JoinName));
                await Task.Delay(300);
                using (var session = h.Store.OpenSession())
                {
                    for (int i = 0; i < inner; i++)
                        session.Store(Inner(i, version), $"contexts/{i}");

                    session.SaveChanges();
                }

                await WaitIndexesAsync(h);
                await Task.Delay(300);
                joinIndex.Options.MaxQueuedListChanges = queue;
                long refreshes = c.Refreshes, commits = c.Commits, flushes = c.ListFlushes, threshold = c.ThresholdFlushes, queued = c.QueuedOps, updates = c.ListUpdates, allocated = c.RefreshAllocatedBytes, ids = c.IdsProcessed;
                (ulong instructions, ulong cycles) = ProcessCounters();
                var sw = Stopwatch.StartNew();
                await h.Store.Maintenance.SendAsync(new StartIndexOperation(JoinIndexHarness.JoinName));
                await WaitJoinAsync(h, pollMilliseconds: 50);
                (ulong instructionsAfter, ulong cyclesAfter) = ProcessCounters();
                double wall = sw.Elapsed.TotalSeconds;
                Assert.True(ReferenceEquals(joinIndex, h.Join()), "the join index was reset");
                Assert.Equal(queue, c.LastQueueLimit);
                if (round == 0)
                    continue;

                string line = $"ROUND {round} outer {outer} rows {rows} direct-write from {parts[4]} queue {queue}: instructions {instructionsAfter - instructions}, cycles {cyclesAfter - cycles}, wall {wall:F2} s, refreshes {c.Refreshes - refreshes}, commits {c.Commits - commits}, ids {c.IdsProcessed - ids}, " +
                              $"list flushes {c.ListFlushes - flushes} (threshold {c.ThresholdFlushes - threshold}), queued ops {c.QueuedOps - queued}, list updates {c.ListUpdates - updates}, allocated by the refreshes {c.RefreshAllocatedBytes - allocated}";
                Report(line);
                if (parts.Length > 5)
                    File.AppendAllText(parts[5], line + Environment.NewLine);
            }
        }

        await h.AssertCurrentAsync();
    }

    [DllImport("libproc", EntryPoint = "proc_pid_rusage")]
    private static extern unsafe int ProcPidRusage(int pid, int flavor, byte* buffer);

    /// <summary>The instructions retired and the cycles of all threads of the process since it started (RUSAGE_INFO_V4 of macOS, fields 29 and 30 after the 16 bytes of the uuid).</summary>
    private static unsafe (ulong Instructions, ulong Cycles) ProcessCounters()
    {
        const int RusageInfoV4 = 4;
        ulong* buffer = stackalloc ulong[64];
        if (ProcPidRusage(Environment.ProcessId, RusageInfoV4, (byte*)buffer) != 0)
            throw new InvalidOperationException("proc_pid_rusage failed");

        return (buffer[2 + 29], buffer[2 + 30]);
    }

    /// <summary>The native memory that Raven allocated and the anonymous memory of the cgroup (in a container) above what they were at the start, sampled every 100 ms.</summary>
    private sealed class MemorySampler : IDisposable
    {
        private readonly System.Threading.CancellationTokenSource _stop = new();
        private readonly Task _task;
        private readonly long _startNative = NativeMemory.TotalAllocatedMemory;
        private readonly long _startAnon = Anon();
        private long _peakNative;
        private long _peakAnon;

        public MemorySampler()
        {
            _peakNative = _startNative;
            _peakAnon = _startAnon;
            _task = Task.Run(async () =>
            {
                while (_stop.IsCancellationRequested == false)
                {
                    _peakNative = Math.Max(_peakNative, NativeMemory.TotalAllocatedMemory);
                    _peakAnon = Math.Max(_peakAnon, Anon());
                    await Task.Delay(100);
                }
            });
        }

        private static long Anon()
        {
            try
            {
                foreach (string line in File.ReadLines("/sys/fs/cgroup/memory.stat"))
                {
                    if (line.StartsWith("anon ", StringComparison.Ordinal))
                        return long.Parse(line.AsSpan(5));
                }
            }
            catch (Exception)
            {
            }

            return 0;
        }

        public string Describe() => $"native peak +{(_peakNative - _startNative) / 1048576} MB, anon peak +{(_peakAnon - _startAnon) / 1048576} MB (anon {_peakAnon / 1048576} MB)";

        public void Dispose()
        {
            _stop.Cancel();
            _task.Wait();
        }
    }

    private static async Task Fill(JoinIndexHarness h, int outer, int round)
    {
        h.Save(s =>
        {
            for (int i = 0; i < Contexts; i++)
                s.Store(new JoinIndexHarness.Context { Country = i % 2 == 0 ? "nl" : "de", Name = "n" + i % 7, Rank = i, IsActive = i % 3 != 0, Title = "red fox " + i % 5 }, $"contexts/{i}");
        });

        await StoreConversations(h, outer, i => $"contexts/{i % Contexts}", "t");
    }

    private static async Task StoreConversations(JoinIndexHarness h, int count, Func<int, string> context, string title, bool oneTransaction = false)
    {
        if (oneTransaction)
        {
            using var session = h.Store.OpenSession();
            for (int i = 0; i < count; i++)
                session.Store(new JoinIndexHarness.Conversation { ContextId = context(i), Title = title }, $"conversations/{i}");

            session.SaveChanges();
            return;
        }

        await using var bulk = h.Store.BulkInsert();
        for (int i = 0; i < count; i++)
            await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextId = context(i), Title = title }, $"conversations/{i}");
    }

    private static async Task WaitIndexesAsync(JoinIndexHarness h)
    {
        var until = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < until)
        {
            var stats = h.Store.Maintenance.Send(new GetIndexesStatisticsOperation());
            if (stats.Where(x => x.Name != JoinIndexHarness.JoinName).All(x => x.IsStale == false))
                return;

            await Task.Delay(50);
        }

        throw new TimeoutException("the indexes stayed stale");
    }

    private static async Task<TimeSpan> WaitJoinAsync(JoinIndexHarness h, int pollMilliseconds = 20)
    {
        var sw = Stopwatch.StartNew();
        var until = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < until)
        {
            var stats = h.Store.Maintenance.Send(new GetIndexStatisticsOperation(JoinIndexHarness.JoinName));
            if (stats.IsStale == false && stats.Status == Raven.Client.Documents.Indexes.IndexRunningStatus.Running)
                return sw.Elapsed;

            await Task.Delay(pollMilliseconds);
        }

        JoinCounters c = h.Join().Counters;
        var last = h.Store.Maintenance.Send(new GetIndexStatisticsOperation(JoinIndexHarness.JoinName));
        throw new TimeoutException($"the join index stayed stale: status {last.Status}, state {last.State}, stale {last.IsStale}, refreshes {c.Refreshes}, partial {c.PartialRefreshes}, noops {c.NoOps}, builds {c.Builds}, rebuilds {string.Join(',', c.RebuildsByReason)}, last {c.LastBreakdown}, error {c.LastError}");
    }

    private void Report(string line)
    {
        Output.WriteLine(line);
        string file = Environment.GetEnvironmentVariable("RAVEN_JOIN_CHUNK_BENCH_FILE");
        if (string.IsNullOrEmpty(file) == false)
            File.AppendAllText(file, line + Environment.NewLine);
    }
}
