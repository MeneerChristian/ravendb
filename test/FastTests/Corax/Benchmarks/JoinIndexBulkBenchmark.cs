using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Sparrow.Utils;

namespace FastTests.Corax;

/// <summary>
/// Bulk entry updates (RAVEN_JOIN_PROD_PHASES=bulk) and one document that touches many entries (phase bigdoc, with RAVEN_JOIN_PROD_BIGHUB=n).
/// Per cell and repetition one record with: wall until every index is not stale (per index), the thread CPU of each index, its batches (allocation, journal), the counters of each join index
/// (refreshes, queued list changes, flushes, rebuilds), the peaks above the start (managed, native, RSS), and, on the first repetition, a Poisson reader at 20 qps.
/// </summary>
public partial class JoinIndexProductionBenchmark
{
    private string[] AllNames => [.. IndexNames, .. ActiveJoins];

    private bool StaleNow(string name)
    {
        // a join index that is being reset is not in the store for a moment: it is behind
        try
        {
            var index = _database.IndexStore.GetIndex(name);
            if (index == null)
                return true;

            using (var context = QueryOperationContext.Allocate(_database, index))
            using (context.OpenReadTransaction())
                return index.IsStale(context);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>The query that is run 15 times in a row after a cell's repetition has settled (null: none).</summary>
    private string _cellProbe;

    private sealed record ProbeResult(int Count, long Served, double FirstMs, double P50Ms, double MaxMs, long Total, string Ranks);

    private ProbeResult RunProbe(string payload, string[] joinIndexes)
    {
        long servedBefore = joinIndexes.Sum(x => JoinOf(x).QueryCounters.ServedCount);
        var latencies = new List<double>();
        long total = -1;
        string ranks = "";
        for (int i = 0; i < 15; i++)
        {
            var watch = Stopwatch.StartNew();
            string body = PostBody(payload);
            latencies.Add(watch.Elapsed.TotalMilliseconds);
            if (i == 0 && body != null)
            {
                using JsonDocument document = JsonDocument.Parse(body);
                total = document.RootElement.GetProperty("TotalResults").GetInt64();
                ranks = string.Join(',', document.RootElement.GetProperty("Results").EnumerateArray().Select(x => x.GetProperty("Rank").GetInt32()));
            }
        }

        long served = joinIndexes.Sum(x => JoinOf(x).QueryCounters.ServedCount) - servedBefore;
        return new ProbeResult(latencies.Count, served, latencies[0], Percentile(latencies, 0.5), latencies.Max(), total, ranks);
    }

    private static readonly QuerySum NoQueries = new(0, 0, new Dictionary<string, long>(), 0, 0, 0);

    private static QuerySum QSum(JoinIndex joinIndex)
    {
        if (joinIndex == null)
            return NoQueries;

        JoinQueryCounters q = joinIndex.QueryCounters;
        var byReason = new Dictionary<string, long>();
        foreach ((string reason, long count) in q.Fallbacks)
            byReason[reason] = count;

        return new QuerySum(q.ServedCount, q.FallbackTotal, byReason, q.WaitCount, q.WaitTotalInMs, q.WaitMaxInMs);
    }

    private static QuerySum QDelta(QuerySum after, QuerySum before)
    {
        var byReason = new Dictionary<string, long>();
        foreach ((string reason, long count) in after.ByReason)
            byReason[reason] = count - before.ByReason.GetValueOrDefault(reason);

        return new QuerySum(after.Served - before.Served, after.Fallbacks - before.Fallbacks, byReason, after.Waits - before.Waits, after.WaitTotalMs - before.WaitTotalMs, after.WaitMaxMs);
    }

    private sealed class BulkSampler : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        private readonly long _pageSize = Environment.SystemPageSize;
        public readonly long StartNative, StartManaged, StartRss, StartAnon;
        public long PeakNative, PeakManaged, PeakRss, PeakAnon;
        public string PeakThreads = "";
        private long _lastSnapshot;

        public BulkSampler()
        {
            StartNative = PeakNative = NativeMemory.TotalAllocatedMemory;
            StartManaged = PeakManaged = GC.GetTotalMemory(false);
            StartRss = PeakRss = Rss();
            StartAnon = PeakAnon = Host.CgroupMemory().Anon;
            _thread = new Thread(() =>
            {
                int n = 0;
                while (_stop == false)
                {
                    Sample(++n % 10 == 0);
                    Thread.Sleep(10);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        private long Rss()
        {
            try
            {
                return long.Parse(File.ReadAllText("/proc/self/statm").Split(' ')[1]) * _pageSize;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private void Sample(bool slow)
        {
            long native = NativeMemory.TotalAllocatedMemory;
            if (native > PeakNative)
            {
                PeakNative = native;
                if (native - StartNative >= 600L * 1024 * 1024 && native - _lastSnapshot >= 256L * 1024 * 1024)
                {
                    _lastSnapshot = native;
                    PeakThreads = string.Join(",", NativeMemory.AllThreadStats.OrderByDescending(x => x.TotalAllocated).Take(4)
                        .Select(x => $"{x.Name.Replace(';', '_').Replace('=', '_').Replace(',', '_')}:{x.TotalAllocated / 1048576}"));
                }
            }

            PeakManaged = Math.Max(PeakManaged, GC.GetTotalMemory(false));
            PeakRss = Math.Max(PeakRss, Rss());
            if (slow)
                PeakAnon = Math.Max(PeakAnon, Host.CgroupMemory().Anon);
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
            Sample(true);
        }
    }

    private sealed record JSnap(long Refreshes, long NoOps, long Builds, long Ids, long ListUpdates, long Flushes, long Queued, long ThresholdFlushes, long RefreshAlloc, long Partial, Dictionary<string, long> Rebuilds);

    private JSnap SnapOf(string name)
    {
        JoinCounters c = JoinOf(name).Counters;
        return new JSnap(Interlocked.Read(ref c.Refreshes), Interlocked.Read(ref c.NoOps), Interlocked.Read(ref c.Builds), Interlocked.Read(ref c.IdsProcessed), Interlocked.Read(ref c.ListUpdates),
            Interlocked.Read(ref c.Flushes), Interlocked.Read(ref c.QueuedOps), Interlocked.Read(ref c.ThresholdFlushes), Interlocked.Read(ref c.RefreshAllocatedBytes), Interlocked.Read(ref c.PartialRefreshes), new Dictionary<string, long>(c.RebuildsByReason));
    }

    private sealed class ReaderRun
    {
        public readonly List<double> Latencies = [];
        public long Errors;
        public Task Task;
    }

    private ReaderRun StartReader(string payload, CancellationToken stop, Stopwatch clock)
    {
        var run = new ReaderRun();
        run.Task = Task.Run(async () =>
        {
            var rng = new Random(11);
            double qps = double.Parse(Env("BULK_READ_QPS", "20"), System.Globalization.CultureInfo.InvariantCulture);
            var slots = new SemaphoreSlim(8);
            var running = new List<Task>();
            double next = clock.Elapsed.TotalSeconds;
            while (stop.IsCancellationRequested == false)
            {
                next += -Math.Log(1 - rng.NextDouble()) / qps;
                double delay = next - clock.Elapsed.TotalSeconds;
                if (delay > 0)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delay), stop);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                await slots.WaitAsync();
                running.Add(Task.Run(() =>
                {
                    try
                    {
                        var watch = Stopwatch.StartNew();
                        try
                        {
                            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                            using HttpResponseMessage response = _http.PostAsync(_queryUrl, content).GetAwaiter().GetResult();
                            response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                            if (response.IsSuccessStatusCode == false)
                                Interlocked.Increment(ref run.Errors);
                        }
                        catch (Exception)
                        {
                            Interlocked.Increment(ref run.Errors);
                        }

                        lock (run.Latencies)
                            run.Latencies.Add(watch.Elapsed.TotalMilliseconds);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }));
            }

            await Task.WhenAll(running);
        });
        return run;
    }

    private async Task WaitIdleCalmAsync()
    {
        DateTime until = DateTime.UtcNow.AddMinutes(int.Parse(Env("BULK_CAP_MIN", "40")));
        var calm = Stopwatch.StartNew();
        string[] names = AllNames;
        while (calm.ElapsedMilliseconds < 300)
        {
            if (names.Any(StaleNow))
                calm.Restart();

            if (DateTime.UtcNow > until)
                throw new TimeoutException("indexes still stale before the operation");

            await Task.Delay(20);
        }
    }

    /// <summary>The time at which each index was last found not stale (the start of its last not-stale streak); returns when all have been not stale for 40 ms.</summary>
    private async Task<Dictionary<string, double>> WaitClearAsync(Stopwatch watch, string[] names, DateTime until)
    {
        var clear = names.ToDictionary(x => x, _ => -1.0);
        while (true)
        {
            double now = watch.Elapsed.TotalMilliseconds;
            foreach (string name in names)
            {
                if (StaleNow(name))
                    clear[name] = -1;
                else if (clear[name] < 0)
                    clear[name] = now;
            }

            if (clear.Values.All(x => x >= 0) && now - clear.Values.Max() >= 40)
                return clear;

            if (DateTime.UtcNow > until)
                throw new TimeoutException("indexes still stale after the cap");

            await Task.Delay(now < 1000 ? 2 : now < 10_000 ? 20 : 100);
        }
    }

    private static string Q(double v) => F(v);

    /// <summary>One repetition of one cell. <paramref name="write"/> null = control (no write, waits 3 s).</summary>
    private async Task BulkOpAsync(string cell, int n, int rep, bool warm, Func<Task> write, string readerPayload)
    {
        await WaitIdleCalmAsync();
        await Task.Delay(500);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        await Task.Delay(100);
        string[] names = AllNames;
        string[] joinIndexes = ActiveJoins;
        var cpuBefore = names.ToDictionary(x => x, IndexThreadCpuMs);
        var snapBefore = joinIndexes.ToDictionary(x => x, SnapOf);
        var instanceBefore = joinIndexes.ToDictionary(x => x, x => (object)JoinOf(x));
        var queriesBefore = joinIndexes.ToDictionary(x => x, x => QSum(JoinOf(x)));
        DateTime since = DateTime.UtcNow.AddMilliseconds(-20);
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        long allocBefore = GC.GetTotalAllocatedBytes(true);
        using var process = Process.GetCurrentProcess();
        TimeSpan procCpuBefore = process.TotalProcessorTime;
        long cgCpuBefore = Host.CgroupCpuUsec();
        var sampler = new BulkSampler();
        var watch = Stopwatch.StartNew();
        using var stopReader = new CancellationTokenSource();
        ReaderRun reader = readerPayload == null ? null : StartReader(readerPayload, stopReader.Token, watch);
        DateTime until = DateTime.UtcNow.AddMinutes(int.Parse(Env("BULK_CAP_MIN", "40")));
        double storeMs = 0;
        Dictionary<string, double> clear;
        bool timedOut = false;
        try
        {
            if (write == null)
                await Task.Delay(3000);
            else
                await write();

            storeMs = watch.Elapsed.TotalMilliseconds;
            clear = await WaitClearAsync(watch, names, until);
        }
        catch (TimeoutException)
        {
            timedOut = true;
            clear = names.ToDictionary(x => x, _ => -1.0);
        }

        double wallMs = watch.Elapsed.TotalMilliseconds;
        stopReader.Cancel();
        if (reader != null)
            await reader.Task;

        long allocProcess = GC.GetTotalAllocatedBytes(true) - allocBefore;
        sampler.Dispose();
        double procCpuMs = (process.TotalProcessorTime - procCpuBefore).TotalMilliseconds;
        double cgCpuMs = (Host.CgroupCpuUsec() - cgCpuBefore) / 1000.0;
        await Task.Delay(50);
        // a join index that was reset during the cell is a new object whose counters start at zero
        var replaced = joinIndexes.ToDictionary(x => x, x => ReferenceEquals(JoinOf(x), instanceBefore[x]) == false);
        QuerySum queries = NoQueries;
        foreach (string joinIndex in joinIndexes)
        {
            QuerySum d = QDelta(QSum(JoinOf(joinIndex)), replaced[joinIndex] ? NoQueries : queriesBefore[joinIndex]);
            var byReason = new Dictionary<string, long>(queries.ByReason);
            foreach ((string reason, long count) in d.ByReason)
                byReason[reason] = byReason.GetValueOrDefault(reason) + count;

            queries = new QuerySum(queries.Served + d.Served, queries.Fallbacks + d.Fallbacks, byReason, queries.Waits + d.Waits, queries.WaitTotalMs + d.WaitTotalMs, Math.Max(queries.WaitMaxMs, d.WaitMaxMs));
        }

        ProbeResult probe = _cellProbe != null && write != null && timedOut == false ? RunProbe(_cellProbe, joinIndexes) : null;
        var perf = new Dictionary<string, (long Managed, long Unmanaged, long Kb, int Batches, long Inputs)>();
        foreach (IndexPerformanceStats stats in _store.Maintenance.Send(new GetIndexPerformanceStatisticsOperation(names)))
        {
            long managed = 0, unmanaged = 0, pages = 0, inputs = 0;
            int batches = 0;
            foreach (IndexingPerformanceStats batch in stats.Performance ?? [])
            {
                if (batch.Started < since)
                    continue;

                managed += batch.AllocatedManagedBytes?.SizeInBytes ?? 0;
                unmanaged += batch.AllocatedUnmanagedBytes?.SizeInBytes ?? 0;
                pages += FourKbs(batch.Details);
                inputs += batch.InputCount;
                batches++;
            }

            perf[stats.Name] = (managed, unmanaged, pages * 4, batches, inputs);
        }

        var values = new List<(string, object)>
        {
            ("cell", cell), ("n", n), ("rep", rep), ("warm", warm), ("timedOut", timedOut), ("storeMs", Q(storeMs)), ("wallMs", Q(wallMs)), ("procCpuMs", Q(procCpuMs)), ("cgroupCpuMs", Q(cgCpuMs)),
            ("managedPeakAboveStartMB", Q(Mb(sampler.PeakManaged - sampler.StartManaged))), ("nativePeakAboveStartMB", Q(Mb(sampler.PeakNative - sampler.StartNative))),
            ("rssPeakAboveStartMB", Q(Mb(sampler.PeakRss - sampler.StartRss))), ("anonPeakAboveStartMB", Q(Mb(sampler.PeakAnon - sampler.StartAnon))), ("nativePeakThreadsMB", sampler.PeakThreads),
            ("allocProcessMB", Q(Mb(allocProcess))), ("gc0", GC.CollectionCount(0) - gc0), ("gc1", GC.CollectionCount(1) - gc1), ("gc2", GC.CollectionCount(2) - gc2)
        };
        foreach (string name in names)
        {
            string p = name == FfKeyJoinName ? "j1" : name == JoinName ? "j3" : name;
            (long managed, long unmanaged, long kb, int batches, long inputs) = perf.GetValueOrDefault(name);
            values.AddRange(
            [
                (p + "ClearMs", Q(clear[name])), (p + "CpuMs", Q(IndexThreadCpuMs(name) - (replaced.GetValueOrDefault(name) ? 0 : cpuBefore[name]))), (p + "BatchManagedMB", Q(Mb(managed))), (p + "BatchUnmanagedMB", Q(Mb(unmanaged))),
                (p + "JournalKb", kb), (p + "Batches", batches), (p + "Inputs", inputs)
            ]);
        }

        foreach (string joinIndex in joinIndexes)
        {
            string p = Short(joinIndex);
            JSnap a = SnapOf(joinIndex);
            JSnap b = replaced[joinIndex] ? new JSnap(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, long>()) : snapBefore[joinIndex];
            JoinCounters c = JoinOf(joinIndex).Counters;
            string rebuilds = string.Join(',', a.Rebuilds.Select(x => (x.Key, Count: x.Value - b.Rebuilds.GetValueOrDefault(x.Key))).Where(x => x.Count != 0).Select(x => $"{x.Key}:{x.Count}"));
            values.AddRange(
            [
                (p + "Replaced", replaced[joinIndex]), (p + "Refreshes", a.Refreshes - b.Refreshes), (p + "PartialRefreshes", a.Partial - b.Partial), (p + "NoOps", a.NoOps - b.NoOps), (p + "Builds", a.Builds - b.Builds), (p + "Rebuilds", rebuilds), (p + "Ids", a.Ids - b.Ids),
                (p + "ListUpdates", a.ListUpdates - b.ListUpdates), (p + "Flushes", a.Flushes - b.Flushes), (p + "QueuedOps", a.Queued - b.Queued), (p + "ThresholdFlushes", a.ThresholdFlushes - b.ThresholdFlushes),
                (p + "RefreshAllocMB", Q(Mb(a.RefreshAlloc - b.RefreshAlloc))), (p + "LastBuildMs", Q(c.LastBuildDurationTicks * 1000.0 / Stopwatch.Frequency)), (p + "LastBuildAllocMB", Q(Mb(c.LastBuildAllocatedBytes))),
                (p + "LastRefreshMs", Q(c.LastRefreshDurationTicks * 1000.0 / Stopwatch.Frequency)), (p + "LastBreakdown", OneLine(c.LastBreakdown ?? ""))
            ]);
        }

        if (probe != null)
        {
            values.AddRange(
            [
                ("probeQueries", probe.Count), ("probeServedByJoin", joinIndexes.Length == 0 ? "n/a" : probe.Served), ("probeFirstMs", Q(probe.FirstMs)), ("probeP50Ms", Q(probe.P50Ms)), ("probeMaxMs", Q(probe.MaxMs)),
                ("probeTotal", probe.Total), ("probeRanks", probe.Ranks)
            ]);
        }

        if (reader != null)
        {
            List<double> latencies;
            lock (reader.Latencies)
                latencies = [.. reader.Latencies];

            values.AddRange(
            [
                ("readerQueries", latencies.Count), ("readerErrors", reader.Errors), ("readerP50Ms", Q(Percentile(latencies, 0.5))), ("readerP95Ms", Q(Percentile(latencies, 0.95))), ("readerMaxMs", Q(latencies.Count == 0 ? 0 : latencies.Max()))
            ]);
            if (joinIndexes.Length > 0)
            {
                values.AddRange(
                [
                    ("served", queries.Served), ("fallbacks", queries.Fallbacks), ("fallbackReasons", string.Join(',', queries.ByReason.Where(x => x.Value != 0).Select(x => $"{x.Key}:{x.Value}"))),
                    ("queriesThatWaited", queries.Waits), ("waitMaxMs", Q(queries.WaitMaxMs))
                ]);
            }
        }

        Emit("9-bulk", values.ToArray());
        if (timedOut)
            throw new TimeoutException("cell " + cell + " n " + n + " did not finish within the cap; stopping the phase");
    }

    private async Task BulkCheckAsync(string cell, int n)
    {
        if (_arm != "B" || InvariantChecksEnabled == false)
            return;

        foreach (string name in ActiveJoins)
        {
            var watch = Stopwatch.StartNew();
            JoinIndexInvariantChecker.Result result = await CheckGenericAsync(name, TimeSpan.FromMinutes(10));
            Emit("9-bulk-check", ("cell", cell), ("n", n), ("join", name), ("ok", result.Ok), ("compared", result.Compared), ("checkS", F(watch.Elapsed.TotalSeconds)), ("detail", OneLine(result.ToString())));
        }
    }

    private static string RanksOf(Answer answer) => string.Join(',', answer.Rows.Select(x =>
    {
        using JsonDocument document = JsonDocument.Parse(x);
        return document.RootElement.GetProperty("Rank").GetInt32();
    }));

    /// <summary>After a cell: the query as the join index serves it against the same query with the join index told not to serve (the join of the server, which does not read the join index): total and ranks of the first page must agree.</summary>
    private async Task BigOracleAsync(string cell, string companyPayload, string ff1Payload)
    {
        if (_arm != "B")
            return;

        await WaitIdleCalmAsync();
        foreach (string name in ActiveJoins)
        {
            string payload = name == FfKeyJoinName ? ff1Payload : companyPayload;
            JoinIndex joinIndex = JoinOf(name);
            long servedBefore = joinIndex.QueryCounters.ServedCount;
            Answer viaJoin = Ask(Unpayload(payload));
            bool served = joinIndex.QueryCounters.ServedCount > servedBefore;
            joinIndex.QueryHooks = new JoinIndexQueryHooks { ForceFallback = true };
            Answer raw;
            try
            {
                raw = Ask(Unpayload(payload));
            }
            finally
            {
                joinIndex.QueryHooks = null;
            }

            Emit("9-bulk-oracle", ("cell", cell), ("join", name), ("servedByJoin", served), ("same", viaJoin.Total == raw.Total && RanksOf(viaJoin) == RanksOf(raw)), ("joinTotal", viaJoin.Total), ("rawTotal", raw.Total),
                ("joinRanks", RanksOf(viaJoin)), ("rawRanks", RanksOf(raw)));
        }
    }

    private static string Unpayload(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("Query").GetString();
    }

    private async Task RepsBulkAsync(string cell, int n, int warmups, int measured, Func<int, Task> write, string payload)
    {
        for (int r = 0; r < warmups + measured; r++)
        {
            int rep = r;
            await BulkOpAsync(cell, n, r - warmups, r < warmups, () => write(rep), r < int.Parse(Env("BULK_READER_REPS", "1")) ? payload : null);
        }
    }

    // ------------------------------------------------------------------ the writes

    private int _bulkPosition;

    /// <summary>The i-th outer document that is not a hub entry (i % 10 != 0): its key is a small key.</summary>
    private static int NonHubOuter(int position) => position + position / 9 + 1;

    private Task BulkStoreAsync(int count, Func<int, (object Doc, string Id)> make)
    {
        return Task.Run(() =>
        {
            using var bulk = _store.BulkInsert();
            for (int j = 0; j < count; j++)
            {
                (object doc, string id) = make(j);
                bulk.Store(doc, id);
            }
        });
    }

    private Task BulkDeleteAsync(IEnumerable<string> ids)
    {
        return Task.Run(() =>
        {
            using IDocumentSession session = _store.OpenSession();
            foreach (string id in ids)
                session.Delete(id);

            session.SaveChanges();
        });
    }

    private async Task BulkPhaseAsync()
    {
        int measured = int.Parse(Env("BULK_REPS", "3"));
        int warm = int.Parse(Env("BULK_WARM", "1"));
        int[] sizes = Env("BULK_SIZES", "1000,10000,100000").Split(',').Select(int.Parse).ToArray();
        string[] only = Env("BULK_CELLS", "a+e,b,c,d,iupd,iins+idel,comp").Split(',');
        bool ff1 = _arm != "C";
        string outerPayload = _arm == "C" ? Payload(FfQueries("C")[0].Rql) : Payload(FfQueries(_arm)[0].Rql);
        string companyPayload = _arm == "C" ? outerPayload : Payload(FfQueries(_arm)[2].Rql);
        int total = warm + measured;

        await BulkOpAsync("ctl-idle", 0, 0, true, null, null);

        try
        {
            foreach (int n in sizes)
            {
                int bPositions = _bulkPosition;
                _bulkPosition += n;
                int dPositions = _bulkPosition;
                _bulkPosition += n * total;
                int OuterAt(int position) => NonHubOuter(position);
                string Fresh(int rep, int j) => $"ffouters/bk{n}-{rep}-{j}";

                if (only.Contains("a+e"))
                {
                    for (int r = 0; r < total; r++)
                    {
                        int rep = r;
                        bool isWarm = r < warm;
                        await BulkOpAsync("a-insert", n, r - warm, isWarm, () => BulkStoreAsync(n, j =>
                        {
                            int i = 10 * (500_000 + rep * n + j) + 3;
                            return (OuterDoc(i, "k/" + H(31, i) % FfKeys, FfCompanyIds(i)), Fresh(rep, j));
                        }), r == 0 ? outerPayload : null);
                        await BulkOpAsync("e-delete", n, r - warm, isWarm, () => BulkDeleteAsync(Enumerable.Range(0, n).Select(j => Fresh(rep, j))), r == 0 ? outerPayload : null);
                    }

                    await BulkCheckAsync("a+e", n);
                }

                if (only.Contains("b"))
                {
                    await RepsBulkAsync("b-update-rank", n, warm, measured, rep => BulkStoreAsync(n, j =>
                    {
                        int i = OuterAt(bPositions + j);
                        return (OuterDoc(i, rank: (int)((H(3, i) + rep + 1) % 1000)), OuterId(i));
                    }), outerPayload);
                    await BulkCheckAsync("b", n);
                }

                if (only.Contains("c"))
                {
                    await RepsBulkAsync("c-rekey-small-to-small", n, warm, measured, rep => BulkStoreAsync(n, j =>
                    {
                        int i = OuterAt(bPositions + j);
                        return (OuterDoc(i, "k/" + (H(31, i) + 7 + 13 * rep) % FfKeys), OuterId(i));
                    }), outerPayload);
                    await BulkCheckAsync("c", n);
                }

                if (only.Contains("d"))
                {
                    await RepsBulkAsync("d-rekey-small-to-hub", n, warm, measured, rep => BulkStoreAsync(n, j =>
                    {
                        int i = OuterAt(dPositions + rep * n + j);
                        return (OuterDoc(i, "k/hub" + (H(30, i) + rep) % FfHubKeys), OuterId(i));
                    }), outerPayload);
                    await BulkCheckAsync("d", n);
                }

                if (ff1 && only.Contains("iupd"))
                {
                    await RepsBulkAsync("i-update-tag", n, warm, measured, rep => BulkStoreAsync(n, j =>
                        (new FfInner { JoinKey = FfInnerKey(j), Tag = "t" + (H(32, j) + rep + 1) % 100, Rare = FfRare(j) }, $"ffinners/{j}")), outerPayload);
                    await BulkCheckAsync("iupd", n);
                }

                if (ff1 && only.Contains("iins+idel"))
                {
                    string InnerFresh(int rep, int j) => $"ffinners/bk{n}-{rep}-{j}";
                    for (int r = 0; r < total; r++)
                    {
                        int rep = r;
                        bool isWarm = r < warm;
                        await BulkOpAsync("i-insert", n, r - warm, isWarm, () => BulkStoreAsync(n, j =>
                        {
                            int k = 7_000_000 + rep * n + j;
                            return (new FfInner { JoinKey = "k/" + H(34, k) % FfKeys, Tag = FfTag(k), Rare = FfRare(k) }, InnerFresh(rep, j));
                        }), r == 0 ? outerPayload : null);
                        await BulkOpAsync("i-delete", n, r - warm, isWarm, () => BulkDeleteAsync(Enumerable.Range(0, n).Select(j => InnerFresh(rep, j))), r == 0 ? outerPayload : null);
                    }

                    await BulkCheckAsync("iins+idel", n);
                }

                if (only.Contains("comp") && n <= _companies)
                {
                    await RepsBulkAsync("comp-country", n, warm, measured, rep => BulkStoreAsync(n, j =>
                    {
                        Company company = MakeCompany(j, false);
                        company.Country = "C" + (H(4, j) + rep + 1) % 10;
                        return (company, $"companies/{j}");
                    }), companyPayload);
                    await BulkCheckAsync("comp", n);
                }
            }
        }
        catch (TimeoutException e)
        {
            Emit("9-bulk-abort", ("detail", OneLine(e.Message)));
        }
    }

    // ------------------------------------------------------------------ one document that touches many entries (the dataset has BIGHUB outer documents on companies/hubbig and the key k/all)

    private async Task BigDocPhaseAsync()
    {
        int measured = int.Parse(Env("BIGDOC_REPS", "3"));
        int warm = int.Parse(Env("BIGDOC_WARM", "1"));
        string companyPayload = _arm == "C" ? Payload(FfQueries("C")[0].Rql) : Payload(FfQueries(_arm)[2].Rql);
        string ff1Payload = Payload(FfQueries(_arm)[0].Rql);
        await BulkOpAsync("ctl-idle", 0, 0, true, null, null);
        try
        {
            foreach (string kind in new[] { "country", "name", "about" })
            {
                _cellProbe = companyPayload;
                await RepsBulkAsync("big-company-" + kind, _bigHub, warm, measured, rep => Task.Run(() =>
                {
                    using IDocumentSession session = _store.OpenSession();
                    session.Store(Variant(-2, kind, rep % 2 == 0), "companies/hubbig");
                    session.SaveChanges();
                }), companyPayload);
                await BulkCheckAsync("big-company-" + kind, _bigHub);
                await BigOracleAsync("big-company-" + kind, companyPayload, ff1Payload);
            }

            if (_arm != "C")
            {
                _cellProbe = ff1Payload;
                await RepsBulkAsync("big-inner-update-tag", _bigHub, warm, measured, rep => Task.Run(() =>
                {
                    using IDocumentSession session = _store.OpenSession();
                    session.Store(new FfInner { JoinKey = "k/all", Tag = "tz" + rep, Rare = FfRare(5_000_000) }, "ffinners/all0");
                    session.SaveChanges();
                }), ff1Payload);
                await BulkCheckAsync("big-inner-update-tag", _bigHub);
                await BigOracleAsync("big-inner-update-tag", companyPayload, ff1Payload);

                await RepsBulkAsync("big-inner-delete", _bigHub, warm, measured, rep => Task.Run(() =>
                {
                    using IDocumentSession session = _store.OpenSession();
                    session.Delete("ffinners/all" + (rep + 1));
                    session.SaveChanges();
                }), ff1Payload);
                await BulkCheckAsync("big-inner-delete", _bigHub);
                await BigOracleAsync("big-inner-delete", companyPayload, ff1Payload);
            }
        }
        catch (TimeoutException e)
        {
            Emit("9-bulk-abort", ("detail", OneLine(e.Message)));
        }
    }
}
