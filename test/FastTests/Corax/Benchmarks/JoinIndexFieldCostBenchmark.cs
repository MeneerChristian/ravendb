using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Serving;

namespace FastTests.Corax;

/// <summary>
/// The indexing cost of the field join indexes (scenario FF, RAVEN_JOIN_PROD_FFMODE=indexing). Arm A: the three join indexes only; arm B: the two join indexes FF1 (field join on JoinKey, join index FieldKeys) and FF3 (join index on the array of ids,
/// join index Companies) built one after the other, each measured alone; arm C: the LoadDocument index FfLD (the shape of FF3).
/// Phases: ops (single document writes, each timed until the join indexes are not stale), live (RAVEN_JOIN_PROD_LIVE_RATES outer inserts per second for _LIVE_SECONDS, with Poisson readers on FF1).
/// RAVEN_JOIN_PROD_OPS_REPS (12 measured) and _OPS_WARM (2) per kind of op.
/// </summary>
public partial class JoinIndexProductionBenchmark
{
    private JoinIndex JoinOf(string name) => _database.IndexStore.GetIndex(name) as JoinIndex;

    // RAVEN_JOIN_PROD_JOINS = FF1 | FF3 | both (default), RAVEN_JOIN_PROD_CHECK = 0 skips every invariant check (the served probe stays)
    private string[] ActiveJoins => _arm != "B" ? [] : Env("JOINS", "both").ToUpperInvariant() switch
    {
        "FF1" => [FfKeyJoinName],
        "FF3" => [JoinName],
        _ => [FfKeyJoinName, JoinName]
    };

    private bool InvariantChecksEnabled => Env("CHECK", "1") != "0";

    private static string Short(string joinIndex) => joinIndex == FfKeyJoinName ? "j1" : "j3";

    private sealed record JStat(double CpuMs, long Refreshes, long NoOps, long Ids, long ListUpdates, double AllocatedMb);

    private JStat JStatOf(string name)
    {
        JoinCounters c = JoinOf(name).Counters;
        return new JStat(IndexThreadCpuMs(name), Interlocked.Read(ref c.Refreshes), Interlocked.Read(ref c.NoOps), Interlocked.Read(ref c.IdsProcessed), Interlocked.Read(ref c.ListUpdates), IndexSize(name).AllocatedMb);
    }

    private static long FourKbs(IndexingPerformanceOperation op) => op == null ? 0 : (op.CommitDetails?.NumberOf4KbsWrittenToDisk ?? 0) + (op.Operations ?? []).Sum(FourKbs);

    private (long Kb, int Batches) JournalKb(string[] names, DateTime since)
    {
        long pages = 0;
        int batches = 0;
        foreach (IndexPerformanceStats stats in _store.Maintenance.Send(new GetIndexPerformanceStatisticsOperation(names)))
        {
            foreach (IndexingPerformanceStats batch in stats.Performance ?? [])
            {
                if (batch.Started >= since)
                {
                    pages += FourKbs(batch.Details);
                    batches++;
                }
            }
        }

        return (pages * 4, batches);
    }

    private bool ServedBy(string joinName, string payload)
    {
        long before = JoinOf(joinName).QueryCounters.ServedCount;
        PostBody(payload);
        return JoinOf(joinName).QueryCounters.ServedCount > before;
    }

    private string _ff1Payload;
    private string _ff3Payload;

    private string PayloadOf(string joinIndex) => joinIndex == FfKeyJoinName ? _ff1Payload : _ff3Payload;

    // ------------------------------------------------------------------ build

    private async Task FieldJoinIndexingAsync(string[] phases)
    {
        CreateFfIndexes();
        LoadFfData();
        BuildIndexes();
        MemoryNow("after the join indexes");

        if (_arm == "B")
        {
            string[] active = ActiveJoins;
            if (active.Contains(FfKeyJoinName))
            {
                await BuildOneJoinAsync("j1", FfKeyJoinName, () => new FfKeyJoin().ExecuteAsync(_store));
                MemoryNow("after join index FF1");
            }

            if (active.Contains(JoinName))
            {
                await BuildOneJoinAsync("j3", JoinName, () => s_ff3Key ? new FfCompaniesKeyJoin().ExecuteAsync(_store) : new FfCompaniesJoin().ExecuteAsync(_store));
                MemoryNow("after join index FF3");
            }

            _ff1Payload = Payload(FfQueries("B")[0].Rql);
            _ff3Payload = Payload(FfQueries("B")[2].Rql);
            Emit("3-probe", ("ff1Served", active.Contains(FfKeyJoinName) ? ServedBy(FfKeyJoinName, _ff1Payload) : (object)"n/a"), ("ff3Served", active.Contains(JoinName) ? ServedBy(JoinName, _ff3Payload) : (object)"n/a"));
        }

        foreach (string phase in phases)
        {
            switch (phase)
            {
                case "ops":
                    await OpsPhaseAsync();
                    break;
                case "live":
                    if (_arm == "B")
                        await FfLivePhaseAsync();
                    break;
                case "bulk":
                    await BulkPhaseAsync();
                    break;
                case "bigdoc":
                    await BigDocPhaseAsync();
                    break;
            }
        }

        if (_arm == "B" && InvariantChecksEnabled)
        {
            foreach (string name in ActiveJoins)
            {
                JoinIndexInvariantChecker.Result result = await CheckGenericAsync(name, TimeSpan.FromMinutes(5));
                Emit("9-final-check", ("join", name), ("ok", result.Ok), ("compared", result.Compared), ("detail", OneLine(result.ToString())));
            }
        }
    }

    private async Task<JoinIndexInvariantChecker.Result> CheckGenericAsync(string name, TimeSpan timeout)
    {
        DateTime until = DateTime.UtcNow + timeout;
        var seen = new JoinIndexInvariantChecker.SeenTerms(3);
        while (true)
        {
            while (IndexesIdle() == false && DateTime.UtcNow < until)
                await Task.Delay(20);

            JoinIndexInvariantChecker.Result last = JoinIndexInvariantChecker.Check(_database, JoinOf(name), seen);
            if (last.Compared || DateTime.UtcNow > until)
                return last;

            await Task.Delay(20);
        }
    }

    private async Task BuildOneJoinAsync(string tag, string name, Func<Task> create)
    {
        var measure = new Measure();
        await create();
        DateTime until = DateTime.UtcNow.AddMinutes(60);
        while (true)
        {
            IndexStats stats = _store.Maintenance.Send(new GetIndexStatisticsOperation(name));
            if (stats.IsStale == false && stats.Join is { Builds: >= 1, PendingReason: null })
                break;

            if (DateTime.UtcNow > until)
                throw new TimeoutException("the join index was not built: " + name);

            await Task.Delay(20);
        }

        Emit("3-build-" + tag, measure.Stop(sync: true));
        (double used, double allocated) = IndexSize(name);
        JoinIndex joinIndex = JoinOf(name);
        Emit("3-join-info-" + tag, ("join", name), ("buildMs", F(joinIndex.Counters.LastBuildDurationTicks * 1000.0 / Stopwatch.Frequency)),
            ("buildThreadAllocMB", F(Mb(joinIndex.Counters.LastBuildAllocatedBytes))), ("usedMB", F(used)), ("allocatedMB", F(allocated)), ("builds", joinIndex.GetStats().Join.Builds));
        if (InvariantChecksEnabled == false)
            return;

        JoinIndexInvariantChecker.Result result = await CheckGenericAsync(name, TimeSpan.FromMinutes(5));
        Emit("3-join-check-" + tag, ("ok", result.Ok), ("compared", result.Compared), ("detail", OneLine(result.ToString())));
    }

    // ------------------------------------------------------------------ one operation

    private async Task<(double IndexMs, Dictionary<string, double> JoinMs)> WaitOpAsync(Stopwatch watch, string[] joinIndexes)
    {
        DateTime until = DateTime.UtcNow.AddMinutes(10);
        while (true)
        {
            IndexStats[] stats = _store.Maintenance.Send(new GetIndexesStatisticsOperation());
            if (stats.Where(x => joinIndexes.Contains(x.Name) == false).All(x => x.IsStale == false))
                break;

            if (DateTime.UtcNow > until)
                throw new TimeoutException("indexes still stale");

            await Task.Delay(1);
        }

        double indexMs = watch.Elapsed.TotalMilliseconds;
        var clear = joinIndexes.ToDictionary(x => x, _ => -1.0);
        while (joinIndexes.Length > 0)
        {
            IndexStats[] stats = _store.Maintenance.Send(new GetIndexesStatisticsOperation());
            double now = watch.Elapsed.TotalMilliseconds;
            foreach (string joinIndex in joinIndexes)
            {
                if (stats.FirstOrDefault(x => x.Name == joinIndex)?.IsStale ?? true)
                    clear[joinIndex] = -1;
                else if (clear[joinIndex] < 0)
                    clear[joinIndex] = now;
            }

            // not stale for 40 ms in a row: the refresh of a change that the join index has not noticed yet would show up as stale again
            if (clear.Values.All(x => x >= 0) && now - clear.Values.Max() >= 40)
                break;

            if (DateTime.UtcNow > until)
                throw new TimeoutException("join index still behind");

            await Task.Delay(1);
        }

        return (indexMs, clear);
    }

    private async Task RunOpAsync(string op, int rep, bool warm, Action<IDocumentSession> write)
    {
        await Task.Delay(400);
        string[] joinIndexes = ActiveJoins;
        var before = joinIndexes.ToDictionary(x => x, JStatOf);
        double indexCpu = IndexNames.Sum(IndexThreadCpuMs);
        DateTime since = DateTime.UtcNow.AddMilliseconds(-20);
        var watch = Stopwatch.StartNew();
        using (IDocumentSession session = _store.OpenSession())
        {
            write(session);
            session.SaveChanges();
        }

        double storeMs = watch.Elapsed.TotalMilliseconds;
        (double indexMs, Dictionary<string, double> clear) = await WaitOpAsync(watch, joinIndexes);
        var served = joinIndexes.ToDictionary(x => x, x => ServedBy(x, PayloadOf(x)));
        await Task.Delay(50);
        (long indexKb, int indexBatches) = JournalKb(IndexNames, since);
        var values = new List<(string, object)>
        {
            ("op", op), ("rep", rep), ("warm", warm), ("storeMs", F(storeMs)), ("indexMs", F(indexMs)), ("indexCpuMs", F(IndexNames.Sum(IndexThreadCpuMs) - indexCpu)),
            ("indexJournalKb", indexKb), ("indexBatches", indexBatches)
        };
        foreach (string joinIndex in joinIndexes)
        {
            string p = Short(joinIndex);
            JStat a = JStatOf(joinIndex);
            JStat b = before[joinIndex];
            (long kb, int batches) = JournalKb([joinIndex], since);
            JoinCounters c = JoinOf(joinIndex).Counters;
            values.AddRange(
            [
                (p + "StaleMs", F(clear[joinIndex])), (p + "ExtraMs", F(clear[joinIndex] - indexMs)), (p + "CpuMs", F(a.CpuMs - b.CpuMs)), (p + "Refreshes", a.Refreshes - b.Refreshes), (p + "NoOps", a.NoOps - b.NoOps),
                (p + "Ids", a.Ids - b.Ids), (p + "ListUpdates", a.ListUpdates - b.ListUpdates), (p + "JournalKb", kb), (p + "Batches", batches), (p + "GrowthMB", F(a.AllocatedMb - b.AllocatedMb)),
                (p + "Served", served[joinIndex]), (p + "RefreshMs", F(c.LastRefreshDurationTicks * 1000.0 / Stopwatch.Frequency)), (p + "RefreshAllocKB", F(c.LastRefreshAllocatedBytes / 1024.0)),
                (p + "Breakdown", a.Refreshes - b.Refreshes > 0 ? OneLine(c.LastBreakdown ?? "") : "")
            ]);
        }

        Emit("9-op", values.ToArray());
    }

    private int _opCounter;

    private async Task RepsAsync(string op, int measured, int warmups, Action<IDocumentSession, int> write)
    {
        for (int r = 0; r < measured + warmups; r++)
        {
            int rep = r;
            await RunOpAsync(op, r - warmups, r < warmups, session => write(session, rep));
        }
    }

    // ------------------------------------------------------------------ the writes

    private FfOuter OuterDoc(int i, string key = null, string[] ids = null, int? rank = null)
    {
        return new FfOuter { JoinKey = key ?? FfOuterKey(i), Status = Statuses[H(2, i) % 10], Rank = rank ?? (int)(H(3, i) % 1000), CompanyIds = ids ?? FfCompanyIds(i) };
    }

    private static string OuterId(int i) => $"ffouters/{i}";

    private int SmallOuter(int slot, int r) => 100_001 + 10 * (slot * 40 + r);

    private int HubOuter(int slot, int r) => 700_000 + 10 * (slot * 40 + r);

    private string FreshKey(int r) => "k/" + (2000 + (r * 37) % 3000);

    private async Task PrepareFreshKeysAsync(int smallKeys, int hubKeys)
    {
        var watch = Stopwatch.StartNew();
        using (var bulk = _store.BulkInsert())
        {
            for (int r = 0; r < smallKeys; r++)
            {
                for (int n = 0; n < 200; n++)
                    bulk.Store(new FfOuter { JoinKey = "k/sm" + r, Status = "open", Rank = n % 1000, CompanyIds = FfCompanyIds(900_000_000 + r * 1000 + n) }, $"ffouters/sm{r}-{n}");
            }

            for (int r = 0; r < hubKeys; r++)
            {
                for (int n = 0; n < 20_000; n++)
                    bulk.Store(new FfOuter { JoinKey = "k/solo" + r, Status = "open", Rank = n % 1000, CompanyIds = FfCompanyIds(910_000_000 + r * 20_000 + n) }, $"ffouters/solo{r}-{n}");
            }
        }

        double loadMs = watch.Elapsed.TotalMilliseconds;
        await WaitOpAsync(watch, ActiveJoins);
        Emit("9-prepare", ("smallKeys", smallKeys), ("outersPerSmallKey", 200), ("hubKeys", hubKeys), ("outersPerHubKey", 20_000), ("loadMs", F(loadMs)), ("caughtUpMs", F(watch.Elapsed.TotalMilliseconds)));
    }

    private async Task OpsPhaseAsync()
    {
        int measured = int.Parse(Env("OPS_REPS", "12"));
        int warm = int.Parse(Env("OPS_WARM", "2"));
        int hubReps = 5;
        bool ff1 = _arm == "B";
        if (ff1)
            await PrepareFreshKeysAsync(measured + warm, hubReps);

        // outer writes (the join index of FF3 reacts to the ids, the join index of FF1 to the key)
        await RepsAsync("o-insert-small-key", measured, warm, (s, r) => s.Store(OuterDoc(0, FreshKey(r), FfCompanyIds(800_000_000 + r)), $"ffouters/ins{Interlocked.Increment(ref _opCounter)}"));
        await RepsAsync("o-insert-hub-key", measured, warm, (s, r) => s.Store(OuterDoc(0, "k/hub" + r % FfHubKeys, FfCompanyIds(810_000_000 + r)), $"ffouters/ins{Interlocked.Increment(ref _opCounter)}"));
        await RepsAsync("o-update-rank", measured, warm, (s, r) =>
        {
            int i = SmallOuter(0, r);
            s.Store(OuterDoc(i, rank: (int)(H(3, i) % 1000 + 1) % 1000), OuterId(i));
        });
        if (ff1)
        {
            await RepsAsync("o-rekey-small-to-small", measured, warm, (s, r) =>
            {
                int i = SmallOuter(1, r);
                s.Store(OuterDoc(i, "k/" + (H(31, i) + 7) % FfKeys), OuterId(i));
            });
            await RepsAsync("o-rekey-small-to-hub", measured, warm, (s, r) =>
            {
                int i = SmallOuter(2, r);
                s.Store(OuterDoc(i, "k/hub" + r % FfHubKeys), OuterId(i));
            });
            await RepsAsync("o-rekey-hub-to-small", measured, warm, (s, r) =>
            {
                int i = HubOuter(0, r);
                s.Store(OuterDoc(i, "k/" + (H(31, i) + 7) % FfKeys), OuterId(i));
            });
        }

        await RepsAsync("o-ids-change-small", measured, warm, (s, r) =>
        {
            int i = SmallOuter(3, r);
            s.Store(OuterDoc(i, ids: [.. Enumerable.Range(0, 3).Select(j => "companies/" + (H(20 + j, i) + 11) % _companies)]), OuterId(i));
        });
        await RepsAsync("o-ids-add-hub-company", measured, warm, (s, r) =>
        {
            int i = SmallOuter(4, r);
            s.Store(OuterDoc(i, ids: ["companies/hub", "companies/" + H(21, i) % _companies, "companies/" + H(22, i) % _companies]), OuterId(i));
        });
        await RepsAsync("o-delete-small", measured, warm, (s, r) => s.Delete(OuterId(SmallOuter(5, r))));

        if (ff1)
        {
            // inner writes in a small group (180 outer entries) and in a hub group (20,000 outer entries): 20 inner entries each
            foreach (bool hub in new[] { false, true })
            {
                string size = hub ? "hub" : "small";
                int reps = hub ? hubReps : measured;
                int warmups = hub ? 0 : warm;
                (string Key, int Member, int Ref) Target(int slot, int r)
                {
                    int rr = r;
                    if (hub)
                    {
                        int hk = rr % 5;
                        return ("k/hub" + hk, 3 * slot + rr / 5, 100_000 + hk * 20 + 19);
                    }

                    int key = 1000 + slot * 100 + rr;
                    return ("k/" + key, slot, key * 20 + 19);
                }

                int InnerId(int slot, int r)
                {
                    (string key, int member, _) = Target(slot, r);
                    return hub ? 100_000 + (r % 5) * 20 + member : int.Parse(key.AsSpan(2)) * 20 + member;
                }

                // a new tag in the group: the pair (key, tag) goes from 0 to 1 and the list of the tag changes for every outer entry of the key
                await RepsAsync($"i-insert-new-tag-{size}", reps, warmups, (s, r) => s.Store(new FfInner { JoinKey = Target(0, r).Key, Tag = "tn" + r, Rare = "r0" }, $"ffinners/n{Interlocked.Increment(ref _opCounter)}"));
                // a tag the group has already: the count goes from 1 to 2 and no list changes
                await RepsAsync($"i-insert-same-tag-{size}", reps, warmups, (s, r) => s.Store(new FfInner { JoinKey = Target(1, r).Key, Tag = FfTag(Target(1, r).Ref), Rare = "r0" }, $"ffinners/n{Interlocked.Increment(ref _opCounter)}"));
                await RepsAsync($"i-update-tag-{size}", reps, warmups, (s, r) =>
                {
                    int j = InnerId(2, r);
                    s.Store(new FfInner { JoinKey = Target(2, r).Key, Tag = "tu" + r, Rare = FfRare(j) }, $"ffinners/{j}");
                });
                await RepsAsync($"i-update-rare-{size}", reps, warmups, (s, r) =>
                {
                    int j = InnerId(3, r);
                    s.Store(new FfInner { JoinKey = Target(3, r).Key, Tag = FfTag(j), Rare = "rx" + r }, $"ffinners/{j}");
                });
                await RepsAsync($"i-delete-{size}", reps, warmups, (s, r) => s.Delete($"ffinners/{InnerId(4, r)}"));
            }

            // the group of a key with outer entries and no inner entry: 0 -> 1 -> several (2) -> 1 -> 0
            foreach (bool hub in new[] { false, true })
            {
                string size = hub ? "hub" : "small";
                int reps = hub ? hubReps : measured;
                int warmups = hub ? 0 : warm;
                string KeyOf(int r) => hub ? "k/solo" + r : "k/sm" + r;
                await RepsAsync($"i-group-0-to-1-{size}", reps, warmups, (s, r) => s.Store(new FfInner { JoinKey = KeyOf(r), Tag = "sa", Rare = "r0" }, $"ffinners/sevA-{KeyOf(r)}"));
                await RepsAsync($"i-group-1-to-2-{size}", reps, warmups, (s, r) => s.Store(new FfInner { JoinKey = KeyOf(r), Tag = "sb", Rare = "r0" }, $"ffinners/sevB-{KeyOf(r)}"));
                await RepsAsync($"i-group-2-to-1-{size}", reps, warmups, (s, r) => s.Delete($"ffinners/sevB-{KeyOf(r)}"));
                await RepsAsync($"i-group-1-to-0-{size}", reps, warmups, (s, r) => s.Delete($"ffinners/sevA-{KeyOf(r)}"));
            }
        }

        // companies (the inner side of FF3, what the LoadDocument index reads): typical = about 30 outer entries, hub = 100,000
        foreach (string kind in new[] { "country", "name", "about" })
        {
            await RepsAsync($"c-{kind}-typical", measured, warm, (s, r) =>
            {
                int id = (int)(H(10, 1000 + r) % _companies);
                s.Store(Variant(id, kind, true), $"companies/{id}");
            });
            await RepsAsync($"c-{kind}-hub", 6, 2, (s, r) => s.Store(Variant(-1, kind, r % 2 == 0), "companies/hub"));
        }

        await RepsAsync("c-insert-unreferenced", measured, warm, (s, r) => s.Store(MakeCompany(r, false), $"companies/new{Interlocked.Increment(ref _opCounter)}"));
    }

    // ------------------------------------------------------------------ live: outer inserts per second, Poisson readers on FF1

    private async Task FfLivePhaseAsync()
    {
        int seconds = int.Parse(Env("LIVE_SECONDS", "30"));
        foreach (int rate in Env("LIVE_RATES", "100,1000").Split(',').Select(int.Parse))
            await FfLiveCellAsync(rate, seconds);
    }

    private long _liveCounter = 20_000_000;

    private async Task FfLiveCellAsync(int rate, int seconds)
    {
        await Task.Delay(3000);
        await WaitOpAsync(Stopwatch.StartNew(), ActiveJoins);
        await Task.Delay(1000);
        using var stop = new CancellationTokenSource();
        long written = 0, errors = 0, overlapped = 0, starts = 0, clean = 0;
        int inflight = 0;
        var saveMs = new List<double>();
        var latencies = new List<double>();
        var waits = new List<double>();
        var perfBatches = ActiveJoins.ToDictionary(x => x, _ => new Dictionary<int, IndexingPerformanceStats>());
        var breakdowns = new Dictionary<string, List<string>> { [FfKeyJoinName] = [], [JoinName] = [] };
        var outerBatches = new Dictionary<int, IndexingPerformanceStats>();
        JoinIndex j1 = JoinOf(FfKeyJoinName);

        QuerySum Sum()
        {
            JoinQueryCounters q = j1.QueryCounters;
            var byReason = new Dictionary<string, long>();
            foreach ((string reason, long count) in q.Fallbacks)
                byReason[reason] = count;

            return new QuerySum(q.ServedCount, q.FallbackTotal, byReason, q.WaitCount, q.WaitTotalInMs, q.WaitMaxInMs);
        }

        QuerySum qBefore = Sum();
        var before = ActiveJoins.ToDictionary(x => x, JStatOf);
        double outerCpuBefore = IndexThreadCpuMs("FfMain");
        DateTime startedAt = DateTime.UtcNow;
        var measure = new Measure();
        var clock = Stopwatch.StartNew();

        void Collect()
        {
            foreach (string joinIndex in ActiveJoins)
            {
                foreach (IndexPerformanceStats stats in _store.Maintenance.Send(new GetIndexPerformanceStatisticsOperation([joinIndex])))
                {
                    foreach (IndexingPerformanceStats batch in stats.Performance ?? [])
                    {
                        if (batch.Started >= startedAt)
                            perfBatches[joinIndex][batch.Id] = batch;
                    }
                }

                string breakdown = JoinOf(joinIndex).Counters.LastBreakdown;
                if (breakdown != null && (breakdowns[joinIndex].Count == 0 || breakdowns[joinIndex][^1] != breakdown))
                    breakdowns[joinIndex].Add(breakdown);
            }

            foreach (IndexPerformanceStats stats in _store.Maintenance.Send(new GetIndexPerformanceStatisticsOperation(["FfMain"])))
            {
                foreach (IndexingPerformanceStats batch in stats.Performance ?? [])
                {
                    if (batch.Started >= startedAt && batch.InputCount > 0)
                        outerBatches[batch.Id] = batch;
                }
            }
        }

        string readerPayload = _ff1Payload;
        var writerRandom = new Random(1);
        Task writer = Task.Run(() =>
        {
            while (stop.IsCancellationRequested == false)
            {
                long due = (long)(clock.Elapsed.TotalSeconds * rate) - Interlocked.Read(ref written);
                if (due <= 0)
                {
                    Thread.Sleep(5);
                    continue;
                }

                due = Math.Min(due, 2000);
                var watch = Stopwatch.StartNew();
                using (IDocumentSession session = _store.OpenSession())
                {
                    for (int i = 0; i < due; i++)
                    {
                        int k = writerRandom.Next(1_000_000);
                        session.Store(OuterDoc(k, ids: FfCompanyIds(k)), $"ffouters/live{Interlocked.Increment(ref _liveCounter)}");
                    }

                    session.SaveChanges();
                }

                lock (saveMs)
                    saveMs.Add(watch.Elapsed.TotalMilliseconds);

                Interlocked.Add(ref written, due);
                Thread.Sleep(Math.Max(0, 50 - (int)watch.ElapsedMilliseconds));
            }
        });

        Task batches = Task.Run(async () =>
        {
            while (stop.IsCancellationRequested == false)
            {
                Collect();
                await Task.Delay(400);
            }
        });

        Task readers = Task.Run(async () =>
        {
            var rng = new Random(11);
            double readQps = double.Parse(Env("LIVE_READ_QPS", "20"), System.Globalization.CultureInfo.InvariantCulture);
            var slots = new SemaphoreSlim(8);
            var running = new List<Task>();
            double next = 0;
            while (stop.IsCancellationRequested == false)
            {
                next += -Math.Log(1 - rng.NextDouble()) / readQps;
                double delay = next - clock.Elapsed.TotalSeconds;
                if (delay > 0)
                    await Task.Delay(TimeSpan.FromSeconds(delay));

                await slots.WaitAsync();
                running.Add(Task.Run(() =>
                {
                    try
                    {
                        long mine = Interlocked.Increment(ref starts);
                        Interlocked.Increment(ref inflight);
                        QuerySum a = Sum();
                        var watch = Stopwatch.StartNew();
                        try
                        {
                            using var content = new StringContent(readerPayload, Encoding.UTF8, "application/json");
                            using HttpResponseMessage response = _http.PostAsync(_queryUrl, content).GetAwaiter().GetResult();
                            response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                            if (response.IsSuccessStatusCode == false)
                                Interlocked.Increment(ref errors);
                        }
                        catch (Exception)
                        {
                            Interlocked.Increment(ref errors);
                        }

                        double ms = watch.Elapsed.TotalMilliseconds;
                        QuerySum b = Sum();
                        bool alone = Interlocked.Read(ref starts) == mine && Volatile.Read(ref inflight) == 1;
                        lock (latencies)
                        {
                            latencies.Add(ms);
                            if (alone)
                            {
                                clean++;
                                waits.Add(b.Waits - a.Waits > 0 ? b.WaitTotalMs - a.WaitTotalMs : 0);
                            }
                            else
                            {
                                overlapped++;
                            }
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref inflight);
                        slots.Release();
                    }
                }));
            }

            await Task.WhenAll(running);
        });

        await Task.Delay(TimeSpan.FromSeconds(seconds));
        stop.Cancel();
        await Task.WhenAll(writer, batches, readers);
        double elapsed = clock.Elapsed.TotalSeconds;
        double cpuMs = measure.CpuMs;
        long allocated = measure.AllocatedBytes;
        Collect();
        (long anon, long file, _, _) = Host.CgroupMemory();
        var settle = Stopwatch.StartNew();
        (double indexMs, Dictionary<string, double> clear) = await WaitOpAsync(settle, ActiveJoins);
        var peaks = measure.Stop(sync: false);
        QuerySum qAfter = Sum();
        long writes = Interlocked.Read(ref written);
        var outer = outerBatches.Values.ToList();
        var values = new List<(string, object)>
        {
            ("label", "FF1-M" + rate), ("readQps", Env("LIVE_READ_QPS", "20")), ("targetWritesPerS", rate), ("writes", writes), ("achievedWritesPerS", F(writes / elapsed)), ("seconds", F(elapsed)),
            ("queries", latencies.Count), ("errors", errors), ("readerP50Ms", F(Percentile(latencies, 0.5))), ("readerP95Ms", F(Percentile(latencies, 0.95))), ("readerP99Ms", F(Percentile(latencies, 0.99))),
            ("saveChangesP50Ms", F(Percentile(saveMs, 0.5))), ("saveChangesP95Ms", F(Percentile(saveMs, 0.95))),
            ("outerBatches", outer.Count), ("outerBatchAvgMs", F(outer.Count == 0 ? 0 : outer.Average(x => ((x.Completed ?? x.Started) - x.Started).TotalMilliseconds))), ("outerBatchAvgInputs", F(outer.Count == 0 ? 0 : outer.Average(x => x.InputCount))),
            ("outerIndexThreadCpuPercentOfCore", F((IndexThreadCpuMs("FfMain") - outerCpuBefore) / 1000 / elapsed * 100)),
            ("processCpuPercentOfCore", F(cpuMs / 1000 / elapsed * 100)), ("processManagedMBPer1000Writes", F(writes == 0 ? 0 : Mb(allocated) / (writes / 1000.0))), ("anonEndMB", F(Mb(anon))), ("fileEndMB", F(Mb(file))),
            ("settleIndexMs", F(indexMs)), ("settleJoinsMs", F(clear.Values.Max() - indexMs)),
            ("served", qAfter.Served - qBefore.Served), ("fallbacks", qAfter.Fallbacks - qBefore.Fallbacks), ("fallbackReasons", Reasons(qBefore, qAfter)),
            ("servedPercent", F(100.0 * (qAfter.Served - qBefore.Served) / Math.Max(1, qAfter.Served - qBefore.Served + qAfter.Fallbacks - qBefore.Fallbacks))),
            ("queriesThatWaited", qAfter.Waits - qBefore.Waits), ("waitCleanSamples", clean), ("waitOverlappedSkipped", overlapped), ("waitP50Ms", F(Percentile(waits, 0.5))), ("waitP95Ms", F(Percentile(waits, 0.95))),
            ("waitP99Ms", F(Percentile(waits, 0.99))), ("waitMaxMs", F(qAfter.WaitMaxMs))
        };
        values.AddRange(peaks.Where(x => x.Key is "nativePeakAboveStartMB" or "managedPeakAboveStartMB" or "managedPeakMB" or "managedCommittedPeakMB" or "anonPeakMB"));
        foreach (string joinIndex in ActiveJoins)
        {
            string p = Short(joinIndex);
            JStat a = JStatOf(joinIndex);
            JStat b = before[joinIndex];
            var mine = perfBatches[joinIndex].Values.ToList();
            values.AddRange(
            [
                (p + "ThreadCpuPercentOfCore", F((a.CpuMs - b.CpuMs) / 1000 / elapsed * 100)), (p + "Refreshes", a.Refreshes - b.Refreshes), (p + "NoOps", a.NoOps - b.NoOps), (p + "ListUpdates", a.ListUpdates - b.ListUpdates),
                (p + "IdsProcessed", a.Ids - b.Ids), (p + "RefreshesPerS", F((a.Refreshes - b.Refreshes) / elapsed)), (p + "FileGrowthMB", F(a.AllocatedMb - b.AllocatedMb)), (p + "Batches", mine.Count),
                (p + "JournalKbPerWrite", F(writes == 0 ? 0 : mine.Sum(x => FourKbs(x.Details)) * 4.0 / writes)), (p + "JournalKbTotal", mine.Sum(x => FourKbs(x.Details)) * 4),
                (p + "BatchAvgMs", F(mine.Count == 0 ? 0 : mine.Average(x => ((x.Completed ?? x.Started) - x.Started).TotalMilliseconds))),
                (p + "BreakdownSamples", breakdowns[joinIndex].Count), (p + "BreakdownMid", breakdowns[joinIndex].Count == 0 ? "" : OneLine(breakdowns[joinIndex][breakdowns[joinIndex].Count / 2]))
            ]);
        }

        Emit("8-live", values.ToArray());
    }
}
