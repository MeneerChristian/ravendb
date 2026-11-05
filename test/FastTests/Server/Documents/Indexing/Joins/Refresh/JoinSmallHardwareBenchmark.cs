using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Auto;
using Sparrow;
using Sparrow.LowMemory;
using Sparrow.Server.LowMemory;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Auto.JoinIndexAutoQueryTests;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

// Opt-in: RAVEN_JOIN_SMALLHW=<outer documents>,<inner documents>,<available MB>,<gate>,<phase>,<load>
//   headroom MB: a native ballast is allocated (and touched) until the memory that the container can still give to the process (the limit minus the anonymous memory; the page cache is reclaimable and not counted) is this much; 0 allocates nothing.
//   The available memory that the server reports counts the page cache and is about 0 in a container once the data has been written: it is reported, but it is not what is controlled
//   gate:  open (the available memory seam of the gate says 64 GB, so the build starts whatever the memory is) or real (the gate reads the memory of the machine)
//   phase: build (the ballast is there when the auto join index is built) or refresh (the join index is built first, the ballast comes, then a change of the indexes is refreshed)
//   load:  1 = other indexes of the database are written to and queried (latency of a query is sampled) all the time, 0 = no other load
// RAVEN_JOIN_SMALLHW_SERVER=key=value;key=value are settings of the server (Memory.*, the environment variables of the runner are not read by the test server). RAVEN_JOIN_SMALLHW_SETTINGS=key=value;key=value overrides database settings (Join.*). RAVEN_JOIN_SMALLHW_FILE: where the lines go (the START line is written first, so a killed run is visible).
public class JoinSmallHardwareBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private sealed class CountingHandler : ILowMemoryHandler
    {
        public int Events;
        public volatile bool Low;
        public void LowMemory(LowMemorySeverity lowMemorySeverity) { Interlocked.Increment(ref Events); Low = true; }
        public void LowMemoryOver() => Low = false;
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AutoJoinUnderMemoryPressure()
    {
        string[] parts = (Environment.GetEnvironmentVariable("RAVEN_JOIN_SMALLHW") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6)
            return;

        int outer = int.Parse(parts[0]);
        int inner = int.Parse(parts[1]);
        long targetAvailableMb = long.Parse(parts[2]);
        bool gateOpen = parts[3] == "open";
        bool gateClosed = parts[3] == "closed";
        bool refreshPhase = parts[4] == "refresh";
        bool load = parts[5] == "1";
        string file = Environment.GetEnvironmentVariable("RAVEN_JOIN_SMALLHW_FILE");
        string tag = Environment.GetEnvironmentVariable("RAVEN_JOIN_SMALLHW_TAG") ?? "";

        void Report(string line)
        {
            output.WriteLine(line);
            if (file != null)
                File.AppendAllText(file, $"{tag} {line}{Environment.NewLine}");
        }

        var settings = ScoreSettings();
        settings["Join.IndirectEnabled"] = "true";
        settings["Join.StorageModeDefault"] = "Indirect";
        foreach (string pair in (Environment.GetEnvironmentVariable("RAVEN_JOIN_SMALLHW_SETTINGS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = pair.IndexOf('=');
            settings[pair[..at]] = pair[(at + 1)..];
        }

        var serverSettings = new Dictionary<string, string>();
        foreach (string pair in (Environment.GetEnvironmentVariable("RAVEN_JOIN_SMALLHW_SERVER") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = pair.IndexOf('=');
            serverSettings[pair[..at]] = pair[(at + 1)..];
        }

        DoNotReuseServer(serverSettings);
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, settings, putJoin: false, runInMemory: false);
        await h.Store.Maintenance.SendAsync(new PutIndexesOperation(
            new IndexDefinition { Name = "Orders/ByStatus", Maps = { "from o in docs.Orders select new { o.Status, o.Amount }" } },
            new IndexDefinition { Name = "Orders/ByCustomer", Maps = { "from o in docs.Orders select new { o.Customer, o.Amount }" } }));

        var configuration = Server.Configuration;
        Report($"START outer {outer} inner {inner} headroom {targetAvailableMb} gate {parts[3]} phase {parts[4]} load {load}; server LowMemoryLimit {configuration.Memory.LowMemoryLimit.GetValue(SizeUnit.Megabytes)} MB, total {MemoryInformation.TotalPhysicalMemory.GetValue(SizeUnit.Megabytes)} MB; settings {string.Join(';', settings.Where(x => x.Key.StartsWith("Join.")).Select(x => x.Key + "=" + x.Value))}");

        await using (var bulk = h.Store.BulkInsert())
        {
            for (int i = 0; i < inner; i++)
                await bulk.StoreAsync(new JoinIndexHarness.Context { Code = "k" + i, Country = i % 2 == 0 ? "nl" : "de", Name = "n" + i % 5, Rank = i % 30, IsActive = i % 3 != 0, Title = "red fox " + i % 7 }, $"contexts/{i}");

            for (int i = 0; i < outer; i++)
                await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextId = "k" + i % inner, Title = "t" + i % 100 }, $"conversations/{i}");

            for (int i = 0; i < 20_000; i++)
                await bulk.StoreAsync(new Order { Status = "s" + i % 20, Customer = "c" + i % 500, Amount = i % 1000 }, $"orders/{i}");
        }

        Indexes.WaitForIndexing(h.Store, timeout: TimeSpan.FromMinutes(10));
        Report("memory after the load: " + Describe());

        AutoJoinManager manager = h.Database.IndexStore.AutoJoins;
        var handler = new CountingHandler();
        LowMemoryNotification.Instance.RegisterLowMemoryHandler(handler);

        // the probe: a query of an index that has nothing to do with the join, and writes to its collection
        var latencies = new ConcurrentQueue<(long At, double Ms)>();
        var stop = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        Task probe = Task.CompletedTask, writer = Task.CompletedTask;
        if (load)
        {
            probe = Task.Run(async () =>
            {
                int n = 0;
                while (stop.IsCancellationRequested == false)
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        using var session = h.Store.OpenSession(new Raven.Client.Documents.Session.SessionOptions { NoCaching = true });
                        _ = session.Advanced.RawQuery<Order>($"from index 'Orders/ByStatus' where Status = 's{n++ % 20}' limit 0, 10").ToList();
                        latencies.Enqueue((clock.ElapsedMilliseconds, sw.Elapsed.TotalMilliseconds));
                    }
                    catch (Exception e)
                    {
                        latencies.Enqueue((clock.ElapsedMilliseconds, double.PositiveInfinity));
                        output.WriteLine("probe: " + e.GetType().Name);
                    }

                    await Task.Delay(20);
                }
            });
            writer = Task.Run(async () =>
            {
                int n = 0;
                while (stop.IsCancellationRequested == false)
                {
                    try
                    {
                        using var session = h.Store.OpenSession();
                        for (int i = 0; i < 10; i++, n++)
                            session.Store(new Order { Status = "s" + n % 20, Customer = "c" + n % 500, Amount = n % 1000 }, $"orders/{n % 20_000}");

                        session.SaveChanges();
                    }
                    catch (Exception e)
                    {
                        output.WriteLine("writer: " + e.GetType().Name);
                    }

                    await Task.Delay(100);
                }
            });
        }

        long minAvailable = long.MaxValue;
        long peakAnon = 0;
        long minHeadroom = long.MaxValue;
        var sampler = Task.Run(async () =>
        {
            while (stop.IsCancellationRequested == false)
            {
                minAvailable = Math.Min(minAvailable, Available());
                minHeadroom = Math.Min(minHeadroom, Headroom());
                peakAnon = Math.Max(peakAnon, Anon());
                await Task.Delay(100);
            }
        });

        var ballast = new List<IntPtr>();
        void TakeBallast()
        {
            if (targetAvailableMb <= 0)
                return;

            const int chunk = 8 * 1024 * 1024;
            while (Headroom() > targetAvailableMb * 1024 * 1024)
            {
                IntPtr p = Marshal.AllocHGlobal(chunk);
                for (int offset = 0; offset < chunk; offset += 4096)
                    Marshal.WriteByte(p, offset, 1);

                ballast.Add(p);
            }
        }

        string Outcome;
        double toServedSeconds = -1;
        double baselineP50 = double.NaN, baselineP99 = double.NaN;
        int lowBeforeBuild = 0;
        long phaseStart = 0;
        try
        {
            var oracle = new JoinIndexQueryOracle(h, AutoFieldName);
            string rql = $"{JoinIndexQueryOracle.Outer} join (from index '{JoinIndexHarness.InnerName}'{FirstSub}) as c on m.ContextId = c.Code{FirstTail}";
            if (gateOpen)
                manager.AvailableMemoryForTesting = () => new Size(64, SizeUnit.Gigabytes);
            else if (gateClosed)
                manager.AvailableMemoryForTesting = () => new Size(0, SizeUnit.Bytes);

            if (refreshPhase)
            {
                _ = oracle.Run(rql);
                await WaitServedAsync(h, oracle, rql, TimeSpan.FromMinutes(10));
            }

            await Task.Delay(load ? 4000 : 0);
            (baselineP50, baselineP99) = Percentiles(latencies.Where(x => x.At > clock.ElapsedMilliseconds - 3500).Select(x => x.Ms));
            TakeBallast();
            Report($"ballast taken: headroom {Headroom() / 1024 / 1024} MB, server available {Available() / 1024 / 1024} MB, {ballast.Count * 8} MB of ballast, anon {Anon() / 1024 / 1024} MB, baseline probe p50 {baselineP50:F1} p99 {baselineP99:F1} ms");
            lowBeforeBuild = handler.Events;
            phaseStart = clock.ElapsedMilliseconds;
            minAvailable = long.MaxValue;
            minHeadroom = long.MaxValue;
            var sw = Stopwatch.StartNew();

            TimeSpan limit = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("RAVEN_JOIN_SMALLHW_TIMEOUT_S"), out int seconds) ? seconds : 600);
            if (refreshPhase)
            {
                Task change = ChangeAndWaitAsync();
                if (await Task.WhenAny(change, Task.Delay(limit)) != change)
                    throw new TimeoutException("the refresh was not served within " + limit);

                await change;
            }
            else
            {
                _ = oracle.Run(rql);
                if (gateOpen == false && manager.Counters.Requested == 0)
                {
                    // nothing is built: the same time is spent, so that the other index is measured under the ballast alone
                    await Task.Delay(gateClosed ? 60_000 : 0);
                    throw new SkippedByTheGateException();
                }

                await WaitServedAsync(h, oracle, rql, limit);
            }

            async Task ChangeAndWaitAsync()
            {
                // a change of every inner document (a tag) and of a tenth of the outer ones in one transaction each: one refresh of the join index
                await using (var bulk = h.Store.BulkInsert())
                {
                    for (int i = 0; i < inner; i++)
                        await bulk.StoreAsync(new JoinIndexHarness.Context { Code = "k" + i, Country = i % 2 == 0 ? "de" : "nl", Name = "n" + (i + 1) % 5, Rank = i % 30, IsActive = i % 3 != 0, Title = "red fox " + i % 7 }, $"contexts/{i}");

                    for (int i = 0; i < outer; i += 10)
                        await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextId = "k" + (i + 1) % inner, Title = "u" + i % 100 }, $"conversations/{i}");
                }

                await WaitServedAsync(h, oracle, rql, limit);
            }

            toServedSeconds = sw.Elapsed.TotalSeconds;
            Outcome = "served";
        }
        catch (SkippedByTheGateException)
        {
            Outcome = "skipped-by-gate";
        }
        catch (Exception e)
        {
            Outcome = "failed:" + e.GetType().Name + ":" + e.Message.Replace('\n', ' ');
        }

        long phaseEnd = clock.ElapsedMilliseconds;
        await Task.Delay(load ? 500 : 0);
        stop.Cancel();
        await Task.WhenAll(probe, writer, sampler);

        double[] during = latencies.Where(x => x.At >= phaseStart && x.At <= phaseEnd).Select(x => x.Ms).ToArray();
        (double p50, double p99) = Percentiles(during);
        string joinState = "none";
        string error = "";
        var joinIndex = h.Database.IndexStore.GetIndex(AutoFieldName);
        if (joinIndex != null)
        {
            joinState = joinIndex.State.ToString();
            var errors = joinIndex.GetErrors();
            if (errors.Count > 0)
                error = errors[0].Error?.Replace('\n', ' ');
        }

        string build = joinIndex is JoinIndex ji
            ? $"build {ji.Counters.LastBuildDurationTicks / (double)Stopwatch.Frequency:F2} s ({ji.Counters.Builds} builds, {ji.Counters.LastBuildListFlushes} list flushes), refresh {ji.Counters.LastRefreshDurationTicks / (double)Stopwatch.Frequency:F2} s ({ji.Counters.Refreshes} refreshes, {ji.Counters.PartialRefreshes} partial, last queue limit {ji.Counters.LastQueueLimit})"
            : "";
        string indexErrors = string.Join(",", new[] { "Orders/ByStatus", "Orders/ByCustomer", JoinIndexHarness.OuterName, JoinIndexHarness.InnerName }
            .Select(x => h.Database.IndexStore.GetIndex(x)).Where(x => x != null && x.GetErrors().Count > 0).Select(x => x.Name));

        Report($"END outcome {Outcome}; to served {toServedSeconds:F1} s; min headroom during {(minHeadroom == long.MaxValue ? -1 : minHeadroom / 1024 / 1024)} MB, min server available {(minAvailable == long.MaxValue ? -1 : minAvailable / 1024 / 1024)} MB; peak anon {peakAnon / 1024 / 1024} MB; " +
               $"low memory events during {handler.Events - lowBeforeBuild} (before {lowBeforeBuild}); probe p50 {p50:F1} p99 {p99:F1} ms over {during.Length} queries, {during.Count(double.IsPositiveInfinity)} failed (baseline p50 {baselineP50:F1} p99 {baselineP99:F1}); " +
               $"join index {joinState} {error}; {build}; other index errors [{indexErrors}]; requested {manager.Counters.Requested} skipped {manager.Counters.SkippedForMemory}; resident charged {ResidentCacheRegistry.Instance.ChargedBytes / 1024 / 1024} MB of budget {ResidentCacheRegistry.Instance.BudgetBytes / 1024 / 1024} MB ({ResidentCacheRegistry.Instance.CacheCount} caches); gc heap {GC.GetTotalMemory(false) / 1024 / 1024} MB");

        foreach (IntPtr p in ballast)
            Marshal.FreeHGlobal(p);
    }

    private const string AutoFieldName = "Auto/Conversations/ByContext/Join/Contexts/Search/OnContextIdToCode";

    private sealed class SkippedByTheGateException : Exception;

    private static async Task WaitServedAsync(JoinIndexHarness h, JoinIndexQueryOracle oracle, string rql, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var joinIndex = h.Database.IndexStore.GetIndex(AutoFieldName);
            if (joinIndex != null && joinIndex.GetErrors().Count > 0)
                throw new InvalidOperationException("the join index is in error: " + joinIndex.GetErrors()[0].Error);

            if (joinIndex != null)
            {
                QueryAnswer answer = oracle.Run(rql);
                if (answer.Counters.GetValueOrDefault("Join/c/JoinIndexServed") == 1)
                    return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("not served within " + timeout);
    }

    private static (double P50, double P99) Percentiles(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(x => x).ToArray();
        if (sorted.Length == 0)
            return (double.NaN, double.NaN);

        return (sorted[sorted.Length / 2], sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))]);
    }

    private static long Available() => MemoryInformation.GetMemoryInfo().AvailableMemoryForProcessing.GetValue(SizeUnit.Bytes);

    private static string Describe()
    {
        var info = MemoryInformation.GetMemoryInfo(extended: false);
        string stat = "";
        try
        {
            stat = string.Join(' ', File.ReadLines("/sys/fs/cgroup/memory.stat").Where(x => x.StartsWith("anon ") || x.StartsWith("file ") || x.StartsWith("file_dirty ") || x.StartsWith("shmem ") || x.StartsWith("file_mapped ") || x.StartsWith("inactive_file ") || x.StartsWith("active_file ")).Select(x => x.Replace(' ', '=')));
            stat += " current=" + File.ReadAllText("/sys/fs/cgroup/memory.current").Trim();
        }
        catch
        {
        }

        return $"UseTotalDirty {LowMemoryNotification.Instance.UseTotalDirtyMemInsteadOfMemUsage}, AvailableMemory {info.AvailableMemory.GetValue(SizeUnit.Megabytes)} MB, ForProcessing {info.AvailableMemoryForProcessing.GetValue(SizeUnit.Megabytes)} MB, SharedClean {info.SharedCleanMemory.GetValue(SizeUnit.Megabytes)} MB, commit {info.CurrentCommitCharge.GetValue(SizeUnit.Megabytes)} MB, total {info.TotalPhysicalMemory.GetValue(SizeUnit.Megabytes)} MB; cgroup {stat}";
    }

    private static long Headroom()
    {
        try
        {
            string max = File.ReadAllText("/sys/fs/cgroup/memory.max").Trim();
            if (max != "max")
                return long.Parse(max) - Anon();
        }
        catch
        {
        }

        return Available();
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
        catch
        {
        }

        return 0;
    }

    private sealed class Order
    {
        public string Status { get; set; }
        public string Customer { get; set; }
        public int Amount { get; set; }
    }
}
