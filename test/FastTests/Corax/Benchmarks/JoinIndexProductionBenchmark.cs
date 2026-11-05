using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FastTests;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide;
using Raven.Server;
using Raven.Server.Config;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Sparrow.Utils;
using Tests.Infrastructure;
using Voron;
using Xunit;

namespace FastTests.Corax;

/// <summary>
/// Opt-in: RAVEN_JOIN_PROD_BENCH=1000000 is the number of placements (companies, candidates are a tenth of it, users a hundredth). One run is ONE arm in its own process:
/// RAVEN_JOIN_PROD_ARM = A (join indexes only), B (join indexes + a join index), C (LoadDocument index LD1), D (join indexes and LD1 side by side, for the left join oracle).
/// The data, the indexes and the queries are the ones of ldj-docker/ldj.py (Corax, IncludeDocumentScore on).
/// RAVEN_JOIN_PROD_PHASES = comma list run in this order after the load: memory (idle 60 s snapshot), query, writes, live, memlive, ldoracle, overhead (arm B: the join with the join index
/// switched off, present and not serving, and serving), wake (what a wake of an idle index costs) (default memory,query,writes).
/// RAVEN_JOIN_PROD_CLIENTS (1,4,16), _SECONDS (20 per query cell), _REPS (3), _LIVE_RATES (100,1000,10000), _LIVE_SECONDS (30), _MEMLIVE_SECONDS (120), _OUT (file the result lines are appended to),
/// _IDLE_SECONDS (60), _SETTINGS (k=v,k=v database settings), _QUERIES (comma separated names to run, default all).
/// Result lines: rec|arm|scenario|phase|key=value;key=value
/// </summary>
public partial class JoinIndexProductionBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private const ulong M64 = ulong.MaxValue;

    private static uint H(int kind, long i)
    {
        unchecked
        {
            ulong x = (((ulong)(i + 1) * 0x9E3779B97F4A7C15UL) & M64) ^ (((ulong)(kind + 1) * 0xC2B2AE3D27D4EB4FUL) & M64);
            x ^= x >> 33;
            x *= 0xFF51AFD7ED558CCDUL;
            x ^= x >> 33;
            x *= 0xC4CEB9FE1A85EC53UL;
            x ^= x >> 33;
            return (uint)(x & 0xFFFFFFFFUL);
        }
    }

    private static string Words(int kind, long i, int count)
    {
        var sb = new StringBuilder();
        for (int w = 0; w < count; w++)
        {
            uint h = H(kind * 1000 + w, i);
            if (w > 0)
                sb.Append(' ');

            sb.Append('w').Append((int)((h % 2000) * ((h >> 16) & 0xFFFF) / 65535.0));
        }

        return sb.ToString();
    }

    private static readonly string[] Statuses = ["open", "open", "open", "closed", "closed", "closed", "closed", "draft", "draft", "hold"];

    private class Placement { public string Text; public string Status; public int Rank; public string CompanyId; public string CandidateId; public string OwnerId; }
    private class Company { public string Name; public string Country; public int Size; public string About; }
    private class Candidate { public string Name; public string Country; public string CvText; }
    private class User { public string Name; public string Team; }

    private class JMain1 : AbstractIndexCreationTask<Placement>
    {
        public JMain1()
        {
            Map = ps => from p in ps select new { p.Text, p.Status, p.Rank, p.CompanyId };
            Index(x => x.Text, FieldIndexing.Search);
            Index(x => x.CompanyId, FieldIndexing.Key);
        }

        public override string IndexName => "JMain";
    }

    private class JCompanies : AbstractIndexCreationTask<Company>
    {
        public JCompanies() => Map = cs => from c in cs select new { c.Name, c.Country, c.Size };
    }

    private class LD1 : AbstractIndexCreationTask<Placement>
    {
        public LD1()
        {
            Map = ps => from p in ps
                let c = LoadDocument<Company>(p.CompanyId)
                select new { p.Text, p.Status, p.Rank, p.CompanyId, CompanyName = c.Name, CompanyCountry = c.Country, CompanySize = c.Size };
            Index(x => x.Text, FieldIndexing.Search);
        }
    }

    private class JoinOuter { public string CompanyId { get; set; } }
    private class JoinInner { public string Name { get; set; } public string Country { get; set; } public int Size { get; set; } }

    private class CompaniesJoin : AbstractJoinCreationTask<JoinOuter, JoinInner>
    {
        public CompaniesJoin()
        {
            UseOuterIndex("JMain");
            UseInnerIndex("JCompanies");
            JoinOn("CompanyId");
            AddField("Country");
            AddField("Name");
            AddField("Size", JoinFieldKind.Long);
            AddGrouping("Country", "C3");
        }

        public override string IndexName => JoinName;
    }

    private const string Main = "from index 'JMain' as m";
    private const string JC = "join (from index 'JCompanies' where Country = 'C3') as c on m.CompanyId = id(c)";
    private const string L = "limit 0, 20";

    private static List<(string Name, string Rql)> Queries(string arm)
    {
        var q = new List<(string, string)>();
        if (arm == "C")
        {
            q.Add(("plain", $"from index 'LD1' where CompanyCountry = 'C3' select id() {L}"));
            q.Add(("Q1", $"from index 'LD1' where Status = 'open' and CompanyCountry = 'C3' order by score() {L}"));
            q.Add(("Q2", $"from index 'LD1' where Status = 'open' and CompanyCountry = 'C3' order by Rank {L}"));
            q.Add(("Q3", $"from index 'LD1' where exists(Status) order by CompanyName {L}"));
            q.Add(("QL", $"from index 'LD1' where Status = 'open' order by Rank {L}"));
            q.Add(("Q3b", $"from index 'LD1' order by CompanyName {L}"));
            q.Add(("Q3o", $"from index 'LD1' where Status = 'open' order by CompanyName {L}"));
            q.Add(("QR", $"from index 'LD1' where Status = 'open' and CompanySize > 4000 order by Rank {L}"));
            q.Add(("QP", $"from index 'LD1' where Status = 'open' and startsWith(CompanyName, 'Company 12') order by Rank {L}"));
            q.Add(("QO", $"from index 'LD1' where Status = 'open' and (CompanyCountry = 'C3' or CompanyCountry = 'C4') order by Rank {L}"));
            return q;
        }

        q.Add(("plain", $"{Main} {JC} select id() {L}"));
        q.Add(("Q1", $"{Main} {JC} where m.Status = 'open' order by score() {L}"));
        q.Add(("Q2", $"{Main} {JC} where m.Status = 'open' order by m.Rank {L}"));
        q.Add(("Q3", $"{Main} join (from index 'JCompanies') as c on m.CompanyId = id(c) where exists(m.Status) order by c.Name {L}"));
        q.Add(("QL", $"{Main} left join (from index 'JCompanies' where Country = 'C3') as c on m.CompanyId = id(c) where m.Status = 'open' order by m.Rank {L}"));
        q.Add(("Q3b", $"{Main} join (from index 'JCompanies') as c on m.CompanyId = id(c) order by c.Name {L}"));
        q.Add(("Q3o", $"{Main} join (from index 'JCompanies') as c on m.CompanyId = id(c) where m.Status = 'open' order by c.Name {L}"));
        q.Add(("QR", $"{Main} join (from index 'JCompanies' where Size > 4000) as c on m.CompanyId = id(c) where m.Status = 'open' order by m.Rank {L}"));
        q.Add(("QP", $"{Main} join (from index 'JCompanies' where startsWith(Name, 'Company 12')) as c on m.CompanyId = id(c) where m.Status = 'open' order by m.Rank {L}"));
        q.Add(("QO", $"{Main} join (from index 'JCompanies' where Country = 'C3' or Country = 'C4') as c on m.CompanyId = id(c) where m.Status = 'open' order by m.Rank {L}"));
        return q;
    }

    private static string Env(string key, string fallback = null) => Environment.GetEnvironmentVariable("RAVEN_JOIN_PROD_" + key) ?? fallback;

    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static double Mb(long bytes) => bytes / 1048576.0;

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0)
            return 0;

        double[] sorted = values.OrderBy(x => x).ToArray();
        return sorted[(int)Math.Min(sorted.Length - 1, sorted.Length * p)];
    }

    // ------------------------------------------------------------------ host counters (Linux cgroup v2 / procfs; zeros elsewhere)

    private static class Host
    {
        public static long CgroupCpuUsec()
        {
            string text = Read("/sys/fs/cgroup/cpu.stat");
            return text == null ? 0 : Field(text, "usage_usec");
        }

        public static (long Anon, long File, long FileMapped, long Current) CgroupMemory()
        {
            string stat = Read("/sys/fs/cgroup/memory.stat");
            string current = Read("/sys/fs/cgroup/memory.current");
            return stat == null ? default : (Field(stat, "anon"), Field(stat, "file"), Field(stat, "file_mapped"), current == null ? 0 : long.Parse(current.Trim()));
        }

        public static long CgroupWritten()
        {
            string text = Read("/sys/fs/cgroup/io.stat");
            if (text == null)
                return 0;

            long sum = 0;
            foreach (string line in text.Split('\n'))
            {
                foreach (string part in line.Split(' '))
                {
                    if (part.StartsWith("wbytes=", StringComparison.Ordinal))
                        sum += long.Parse(part.AsSpan(7));
                }
            }

            return sum;
        }

        public static long ProcWritten()
        {
            string text = Read("/proc/self/io");
            return text == null ? 0 : Field(text, "write_bytes");
        }

        public static (long Rss, long RssAnon) ProcStatus()
        {
            string text = Read("/proc/self/status");
            return text == null ? default : (Field(text, "VmRSS") * 1024, Field(text, "RssAnon") * 1024);
        }

        public static void Sync()
        {
            try
            {
                using Process process = Process.Start(new ProcessStartInfo("sync") { UseShellExecute = false });
                process?.WaitForExit(60_000);
            }
            catch (Exception)
            {
                // the number is then the writes that reached the device so far
            }
        }

        private static string Read(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static long Field(string text, string name)
        {
            foreach (string line in text.Split('\n'))
            {
                string[] parts = line.Split([' ', '\t', ':'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] == name && long.TryParse(parts[1], out long value))
                    return value;
            }

            return 0;
        }
    }

    private sealed class Sampler : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        public long StartNative = NativeMemory.TotalAllocatedMemory;
        public long PeakNative = NativeMemory.TotalAllocatedMemory;
        public long PeakAnon;
        public long PeakCurrent;
        public long StartManaged = GC.GetTotalMemory(false);
        public long PeakManaged = GC.GetTotalMemory(false);
        public long PeakCommitted;

        public Sampler()
        {
            _thread = new Thread(() =>
            {
                int n = 0;
                while (_stop == false)
                {
                    PeakNative = Math.Max(PeakNative, NativeMemory.TotalAllocatedMemory);
                    PeakManaged = Math.Max(PeakManaged, GC.GetTotalMemory(false));
                    if (++n % 5 == 0)
                    {
                        PeakCommitted = Math.Max(PeakCommitted, GC.GetGCMemoryInfo(GCKind.Any).TotalCommittedBytes);
                        (long anon, _, _, long current) = Host.CgroupMemory();
                        PeakAnon = Math.Max(PeakAnon, anon);
                        PeakCurrent = Math.Max(PeakCurrent, current);
                    }

                    Thread.Sleep(20);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
        }
    }

    private sealed class Measure
    {
        private readonly Stopwatch _wall = Stopwatch.StartNew();
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly TimeSpan _cpu;
        private readonly long _cgCpu = Host.CgroupCpuUsec();
        private readonly long _alloc = GC.GetTotalAllocatedBytes(true);
        private readonly long _written = Host.ProcWritten();
        private readonly long _cgWritten = Host.CgroupWritten();
        private readonly Sampler _sampler = new();

        public Measure()
        {
            _process.Refresh();
            _cpu = _process.TotalProcessorTime;
        }

        public double WallMs => _wall.Elapsed.TotalMilliseconds;

        public double CpuMs
        {
            get
            {
                _process.Refresh();
                return (_process.TotalProcessorTime - _cpu).TotalMilliseconds;
            }
        }

        public long AllocatedBytes => GC.GetTotalAllocatedBytes(true) - _alloc;

        public double NativePeakAboveStartKb => (_sampler.PeakNative - _sampler.StartNative) / 1024.0;

        public (string Key, object Value)[] Stop(bool sync)
        {
            if (sync)
                Host.Sync();

            double wall = _wall.Elapsed.TotalSeconds;
            _process.Refresh();
            double cpu = (_process.TotalProcessorTime - _cpu).TotalSeconds;
            long written = Host.ProcWritten() - _written;
            long cgWritten = Host.CgroupWritten() - _cgWritten;
            long cgCpu = Host.CgroupCpuUsec() - _cgCpu;
            _sampler.Dispose();
            return
            [
                ("wallS", F(wall)), ("cpuS", F(cpu)), ("cgroupCpuS", F(cgCpu / 1e6)), ("managedAllocMB", F(Mb(GC.GetTotalAllocatedBytes(true) - _alloc))),
                ("diskWrittenMB", F(Mb(written))), ("cgroupWrittenMB", F(Mb(cgWritten))), ("nativePeakMB", F(Mb(_sampler.PeakNative))),
                ("nativePeakAboveStartMB", F(Mb(_sampler.PeakNative - _sampler.StartNative))), ("anonPeakMB", F(Mb(_sampler.PeakAnon))),
                ("managedPeakAboveStartMB", F(Mb(_sampler.PeakManaged - _sampler.StartManaged))), ("managedPeakMB", F(Mb(_sampler.PeakManaged))), ("managedCommittedPeakMB", F(Mb(_sampler.PeakCommitted)))
            ];
        }
    }

    // ------------------------------------------------------------------ state of the run

    private string _arm;
    private string _scenario;
    private int _placements;
    private int _companies;
    private int _candidates;
    private int _users;
    private string _outFile;
    private readonly object _emitLock = new();
    private DocumentStore _store;
    private RavenServer _server;
    private DocumentDatabase _database;
    private const string JoinName = "Companies";
    private readonly JoinIndexInvariantChecker.SeenTerms _seen = new(3);
    private HttpClient _http;
    private string _queryUrl;
    private long _nextPlacement;
    private double _pollCostCpuMs;

    private string[] IndexNames => _scenario == "FF" ? (_arm == "C" ? ["FfLD"] : ["FfMain", "FfInner", s_ff3Key ? "JCompaniesK" : "JCompanies"]) : _arm == "D" ? ["JMain", "JCompanies", "LD1"] : _arm == "C" ? ["LD1"] : ["JMain", "JCompanies"];

    private void Emit(string phase, params (string Key, object Value)[] values)
    {
        string line = $"rec|{_arm}|{_scenario}|{phase}|" + string.Join(';', values.Select(x => FormattableString.Invariant($"{x.Key}={x.Value}")));
        lock (_emitLock)
        {
            Output.WriteLine(line);
            if (_outFile != null)
                File.AppendAllText(_outFile, line + Environment.NewLine);
        }
    }

    [JoinBenchmarkFact("RAVEN_JOIN_PROD_BENCH", RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ProductionCost()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("RAVEN_JOIN_PROD_BENCH"), out _placements) == false || _placements == 0)
            return;

        _arm = Env("ARM", "B");
        _scenario = Env("SCENARIO", "J1");
        if (_scenario != "J1" && _scenario != "FF")
            throw new NotSupportedException("only the scenarios J1 (one join) and FF (joins on a field) are implemented");

        _outFile = Env("OUT");
        _bigHub = int.Parse(Env("BIGHUB", "0"));
        _companies = Math.Max(10, _placements / 10);
        _candidates = Math.Max(10, _placements / 10);
        _users = Math.Max(10, _placements / 100);
        string[] phases = Env("PHASES", "memory,query,writes").Split(',', StringSplitOptions.RemoveEmptyEntries);

        var settings = new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString(),
            [RavenConfiguration.GetKey(x => x.Indexing.MapBatchSize)] = "100000"
        };
        if (Env("SETTINGS") is { Length: > 0 } pairs)
        {
            foreach (string pair in pairs.Split(','))
            {
                string[] parts = pair.Split('=');
                settings[parts[0]] = parts[1];
            }
        }

        _server = GetNewServer();
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.Server = _server;
        options.RunInMemory = false;
        Action<DatabaseRecord> previous = options.ModifyDatabaseRecord;
        options.ModifyDatabaseRecord = record =>
        {
            previous?.Invoke(record);
            foreach ((string key, string value) in settings)
                record.Settings[key] = value;
        };

        _store = GetDocumentStore(options);
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _queryUrl = $"{_store.Urls[0]}/databases/{_store.Database}/queries?addTimeSeriesNames=false";
        _database = await _server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(_store.Database);
        _nextPlacement = _placements + 1_000_000;

        Emit("run", ("placements", _placements), ("phases", string.Join('+', phases)), ("cpus", Environment.ProcessorCount), ("hostLoad", "n/a"),
            ("gcServer", System.Runtime.GCSettings.IsServerGC), ("mapBatchSize", 100000), ("includeDocumentScore", true));

        if (_scenario == "FF")
        {
            if (Env("FFMODE") == "indexing")
                await FieldJoinIndexingAsync(phases);
            else
                await FieldJoinsAsync(phases);

            _http.Dispose();
            return;
        }

        CreateIndexes();
        LoadData();
        BuildIndexes();

        if (phases.Contains("memory"))
            IdleMemory(_arm == "C" ? "LoadDocument index" : "join indexes");

        if (_arm == "B")
        {
            await BuildJoinsAsync();
            if (phases.Contains("memory"))
                IdleMemory("join indexes + join index");
        }

        await CalibratePollCostAsync();

        foreach (string phase in phases)
        {
            switch (phase)
            {
                case "query":
                    await QueryPhaseAsync();
                    break;
                case "writes":
                    await WritesPhaseAsync();
                    break;
                case "live":
                    await LivePhaseAsync();
                    break;
                case "memlive":
                    await MemLiveAsync();
                    break;
                case "ldoracle":
                    LdOracle();
                    break;
                case "overhead":
                    await OverheadPhaseAsync();
                    break;
                case "wake":
                    await WakePhaseAsync();
                    break;
            }
        }

        if (_arm == "B")
            await FinalCheckAsync();

        _http.Dispose();
    }

    // ------------------------------------------------------------------ build

    private void CreateIndexes()
    {
        if (_arm == "C")
        {
            new LD1().Execute(_store);
            return;
        }

        if (_arm == "D")
            new LD1().Execute(_store);

        new JMain1().Execute(_store);
        new JCompanies().Execute(_store);
    }

    private static string HubCountry(bool alternative)
    {
        string original = "C" + H(4, -1) % 10;
        return alternative ? (original == "C3" ? "C9" : "C3") : original;
    }

    private static Company MakeCompany(int i, bool changed)
    {
        return new Company { Name = changed ? "Renamed " + i : "Company " + i, Country = changed ? "C9" : "C" + H(4, i) % 10, Size = (int)(H(5, i) % 5000) + (changed ? 1 : 0), About = Words(6, i, 60) };
    }

    private void LoadData()
    {
        var watch = Stopwatch.StartNew();
        _store.Maintenance.Send(new StopIndexingOperation());
        using (var bulk = _store.BulkInsert())
        {
            for (int i = 0; i < _companies; i++)
                bulk.Store(new Company { Name = "Company " + i, Country = "C" + H(4, i) % 10, Size = (int)(H(5, i) % 5000), About = Words(6, i, 60) }, $"companies/{i}");

            bulk.Store(new Company { Name = "Company -1", Country = "C" + H(4, -1) % 10, Size = (int)(H(5, -1) % 5000), About = Words(6, -1, 60) }, "companies/hub");
            for (int i = 0; i < _candidates; i++)
                bulk.Store(new Candidate { Name = "Candidate " + i, Country = "K" + H(7, i) % 10, CvText = "" }, $"candidates/{i}");

            for (int i = 0; i < _users; i++)
                bulk.Store(new User { Name = "User " + i, Team = "T" + H(9, i) % 5 }, $"users/{i}");

            for (int i = 0; i < _placements; i++)
            {
                bulk.Store(new Placement
                {
                    Text = Words(1, i * 10L, 8), Status = Statuses[H(2, i) % 10], Rank = (int)(H(3, i) % 1000),
                    CompanyId = i % 10 == 0 ? "companies/hub" : "companies/" + H(10, i) % _companies, CandidateId = "candidates/" + H(11, i) % _candidates, OwnerId = "users/" + H(12, i) % _users
                }, $"placements/{i}");
            }
        }

        Emit("0-load", ("wallS", F(watch.Elapsed.TotalSeconds)));
    }

    private bool IndexesIdle() => _store.Maintenance.Send(new GetIndexesStatisticsOperation()).All(x => x.IsStale == false);

    private void WaitForIndexes(double pollMs = 20)
    {
        DateTime until = DateTime.UtcNow.AddMinutes(90);
        while (DateTime.UtcNow < until)
        {
            if (IndexesIdle())
                return;

            Thread.Sleep((int)pollMs);
        }

        throw new TimeoutException("indexes are still stale");
    }

    private static readonly FieldInfo EnvironmentField = typeof(Raven.Server.Documents.Indexes.Index).GetField("_environment", BindingFlags.Instance | BindingFlags.NonPublic);

    private (double UsedMb, double AllocatedMb) IndexSize(string name)
    {
        var index = _database.IndexStore.GetIndex(name);
        var environment = (StorageEnvironment)EnvironmentField.GetValue(index);
        var stats = environment.Stats();
        return (Mb(stats.UsedDataFileSizeInBytes), Mb(stats.AllocatedDataFileSizeInBytes));
    }

    private void EmitSizes(string label)
    {
        double used = 0, allocated = 0;
        foreach (string name in IndexNames)
        {
            (double u, double a) = IndexSize(name);
            used += u;
            allocated += a;
            Emit("1-index-size", ("label", label), ("index", name), ("usedMB", F(u)), ("allocatedMB", F(a)));
        }

        Emit("1-index-size-total", ("label", label), ("usedMB", F(used)), ("allocatedMB", F(allocated)));
    }

    private void BuildIndexes()
    {
        var measure = new Measure();
        _store.Maintenance.Send(new StartIndexingOperation());
        WaitForIndexes();
        var values = measure.Stop(sync: true);
        Emit("2-build-indexes", values);
        var stats = _store.Maintenance.Send(new GetIndexesStatisticsOperation());
        Emit("2-build-entries", stats.Select(x => (x.Name, (object)x.EntriesCount)).ToArray());
        EmitSizes(_arm == "C" ? "LoadDocument" : "join indexes");
    }

    private JoinIndex Join => _database.IndexStore.GetIndex(JoinName) as JoinIndex;

    private double JoinBuildMs()
    {
        return Join.Counters.LastBuildDurationTicks * 1000.0 / Stopwatch.Frequency;
    }

    private async Task BuildJoinsAsync()
    {
        var measure = new Measure();
        await new CompaniesJoin().ExecuteAsync(_store);
        DateTime until = DateTime.UtcNow.AddMinutes(60);
        while (true)
        {
            IndexStats stats = _store.Maintenance.Send(new GetIndexStatisticsOperation(JoinName));
            if (stats.IsStale == false && stats.Join is { Builds: >= 1, PendingReason: null })
                break;

            if (DateTime.UtcNow > until)
                throw new TimeoutException("the join index was not built");

            await Task.Delay(20);
        }

        var values = measure.Stop(sync: true);
        Emit("3-build-join", values);
        (double used, double allocated) = IndexSize(JoinName);
        JoinIndexStats joinStats = Join.GetStats().Join;
        Emit("3-join-info", ("join", JoinName), ("buildMs", F(JoinBuildMs())), ("buildThreadAllocMB", F(Mb(Join.Counters.LastBuildAllocatedBytes))), ("fileAllocatedMB", F(allocated)),
            ("usedMB", F(used)), ("builds", joinStats.Builds), ("rebuilds", string.Join(',', joinStats.RebuildsByReason.Select(x => $"{x.Key}:{x.Value}"))));

        // exactness of what was built, not timed
        JoinIndexInvariantChecker.Result result = await CheckJoinAsync(TimeSpan.FromMinutes(5));
        Emit("3-join-check", ("ok", result.Ok), ("compared", result.Compared), ("detail", result.ToString().Replace('\n', ' ').Replace(';', ',').Replace('|', '/')));
        _probePayload = Payload($"{Main} {JC} select id() {L}");

        // the counters of IndexStats.Join are the ones the harness reads in process
        ServedProbe();
        IndexStats http = _store.Maintenance.Send(new GetIndexStatisticsOperation(JoinName));
        Emit("3-counter-check", ("statsServed", http.Join.Served), ("countersServed", Join.QueryCounters.ServedCount), ("statsFallbacks", http.Join.FallbackTotal));
    }

    private async Task<JoinIndexInvariantChecker.Result> CheckJoinAsync(TimeSpan timeout)
    {
        DateTime until = DateTime.UtcNow + timeout;
        while (true)
        {
            while (IndexesIdle() == false && DateTime.UtcNow < until)
                await Task.Delay(20);

            JoinIndexInvariantChecker.Result last = JoinIndexInvariantChecker.Check(_database, Join, _seen);
            if (last.Compared || DateTime.UtcNow > until)
                return last;

            await Task.Delay(20);
        }
    }

    private void IdleMemory(string label)
    {
        Process process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpu = process.TotalProcessorTime;
        double joinCpu = _arm == "B" ? IndexThreadCpuMs(JoinName) : 0;
        int seconds = int.Parse(Env("IDLE_SECONDS", "60"));
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        process.Refresh();
        Emit("5-idle-cpu", ("label", label), ("seconds", seconds), ("processCpuMs", F((process.TotalProcessorTime - cpu).TotalMilliseconds)),
            ("joinThreadCpuMs", F(_arm == "B" ? IndexThreadCpuMs(JoinName) - joinCpu : 0)));
        MemoryNow(label);
    }

    private void MemoryNow(string label)
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        (long anon, long file, long mapped, long current) = Host.CgroupMemory();
        (long rss, long rssAnon) = Host.ProcStatus();
        GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.Any);
        double sizeUsed = IndexNames.Sum(x => IndexSize(x).UsedMb);
        double joinUsed = Join == null ? 0 : IndexSize(JoinName).UsedMb;
        Emit("5-memory", ("label", label), ("anonMB", F(Mb(anon))), ("fileCacheMB", F(Mb(file))), ("fileMappedMB", F(Mb(mapped))), ("cgroupCurrentMB", F(Mb(current))),
            ("processRssMB", F(Mb(rss))), ("processRssAnonMB", F(Mb(rssAnon))), ("managedCommittedMB", F(Mb(info.TotalCommittedBytes))), ("managedHeapMB", F(Mb(GC.GetTotalMemory(false)))),
            ("ravenNativeMB", F(Mb(NativeMemory.TotalAllocatedMemory))), ("indexUsedMB", F(sizeUsed)), ("joinUsedMB", F(joinUsed)));
    }

    // ------------------------------------------------------------------ join index counters

    private sealed record QuerySum(long Served, long Fallbacks, Dictionary<string, long> ByReason, long Waits, double WaitTotalMs, double WaitMaxMs);

    private QuerySum SumQuery()
    {
        JoinIndex[] joinIndexes = _scenario == "FF" ? [Join, _database.IndexStore.GetIndex(FfKeyJoinName) as JoinIndex] : [Join];
        long served = 0, fallbacks = 0, waits = 0;
        double waitTotal = 0, waitMax = 0;
        var byReason = new Dictionary<string, long>();
        foreach (JoinIndex joinIndex in joinIndexes.Where(x => x != null))
        {
            JoinQueryCounters counters = joinIndex.QueryCounters;
            foreach ((string reason, long count) in counters.Fallbacks)
                byReason[reason] = byReason.GetValueOrDefault(reason) + count;

            served += counters.ServedCount;
            fallbacks += counters.FallbackTotal;
            waits += counters.WaitCount;
            waitTotal += counters.WaitTotalInMs;
            waitMax = Math.Max(waitMax, counters.WaitMaxInMs);
        }

        return new QuerySum(served, fallbacks, byReason, waits, waitTotal, waitMax);
    }

    private static string Reasons(QuerySum before, QuerySum after)
    {
        return string.Join(',', after.ByReason.Select(x => (x.Key, Count: x.Value - before.ByReason.GetValueOrDefault(x.Key))).Where(x => x.Count != 0).Select(x => $"{x.Key}:{x.Count}"));
    }

    /// <summary>The processor time of the indexing thread of an index. /proc gives nanoseconds, ProcessThread only whole clock ticks of 10 ms.</summary>
    private double IndexThreadCpuMs(string name)
    {
        ulong id = _database.IndexStore.GetIndex(name)?._indexingThread?.CurrentThreadStats?.UnmanagedThreadId ?? 0;
        if (id == 0)
            return 0;

        string schedstat = $"/proc/self/task/{id}/schedstat";
        try
        {
            if (File.Exists(schedstat))
                return long.Parse(File.ReadAllText(schedstat).Split(' ')[0]) / 1e6;
        }
        catch (Exception)
        {
            // the thread ended, or the file system does not have it: ProcessThread below
        }

        using Process process = Process.GetCurrentProcess();
        foreach (ProcessThread thread in process.Threads)
        {
            if ((ulong)thread.Id == id)
                return thread.TotalProcessorTime.TotalMilliseconds;
        }

        return 0;
    }

    private sealed record JoinSum(double ThreadCpuMs, double OuterThreadCpuMs, long Ids, long Refreshes, long NoOps, long ListUpdates, long Builds, double AllocatedMb);

    private JoinSum SumJoin()
    {
        JoinIndex joinIndex = Join;
        if (joinIndex == null)
            return null;

        JoinCounters counters = joinIndex.Counters;
        return new JoinSum(IndexThreadCpuMs(JoinName), IndexThreadCpuMs("JMain"), Interlocked.Read(ref counters.IdsProcessed), Interlocked.Read(ref counters.Refreshes), Interlocked.Read(ref counters.NoOps),
            Interlocked.Read(ref counters.ListUpdates), Interlocked.Read(ref counters.Builds), IndexSize(JoinName).AllocatedMb);
    }

    private bool ServedProbe()
    {
        long before = Join.QueryCounters.ServedCount;
        PostBody(_probePayload);
        return Join.QueryCounters.ServedCount > before;
    }

    private string PostBody(string payload)
    {
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = _http.PostAsync(_queryUrl, content).GetAwaiter().GetResult();
        string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return response.IsSuccessStatusCode ? body : null;
    }

    private long _polls;
    private readonly Dictionary<string, Func<bool>> _ops = new();
    private double _lastStaleClearMs;
    private string _probePayload;

    private bool NonJoinIdle(IndexStats[] stats) => stats.Where(x => x.Name != JoinName).All(x => x.IsStale == false);

    /// <summary>Waits until every index except the join index is idle and (arm B) the join index is not stale and a query for the grouped join is answered from it. Returns the time of the two steps in ms.</summary>
    private async Task<(double IndexMs, double JoinMs)> WaitCaughtUpAsync(Stopwatch watch, int pollMs = 5, int timeoutMinutes = 10)
    {
        DateTime until = DateTime.UtcNow.AddMinutes(timeoutMinutes);
        while (true)
        {
            Interlocked.Increment(ref _polls);
            IndexStats[] stats = _store.Maintenance.Send(new GetIndexesStatisticsOperation());
            if (_arm != "B" ? stats.All(x => x.IsStale == false) : NonJoinIdle(stats))
                break;

            if (DateTime.UtcNow > until)
                throw new TimeoutException("indexes still stale");

            await Task.Delay(pollMs);
        }

        double indexMs = watch.Elapsed.TotalMilliseconds;
        if (_arm == "B")
        {
            // the standard staleness of the join index is the first criterion (_lastStaleClearMs keeps when it cleared), a served query the second:
            // it is answered from the join index only when it reflects what the idle indexes hold
            _lastStaleClearMs = -1;
            DateTime joinUntil = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                Interlocked.Increment(ref _polls);
                IndexStats joinIndex = _store.Maintenance.Send(new GetIndexStatisticsOperation(JoinName));
                if (joinIndex.IsStale == false)
                {
                    if (_lastStaleClearMs < 0)
                        _lastStaleClearMs = watch.Elapsed.TotalMilliseconds;

                    if (ServedProbe())
                        break;
                }

                if (DateTime.UtcNow > joinUntil)
                    throw new TimeoutException("join index still behind");

                await Task.Delay(pollMs);
            }
        }

        return (indexMs, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>The processor time of one poll of WaitCaughtUpAsync on a quiet server, subtracted from the cpu of the short measurements.</summary>
    private async Task CalibratePollCostAsync()
    {
        await Task.Delay(2000);
        var measure = new Measure();
        _polls = 0;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(4))
        {
            IndexesIdle();
            if (_arm == "B")
            {
                _store.Maintenance.Send(new GetIndexStatisticsOperation(JoinName));
                ServedProbe();
            }

            _polls++;
            await Task.Delay(5);
        }

        double cpu = measure.CpuMs;
        measure.Stop(sync: false);
        _pollCostCpuMs = cpu / Math.Max(1, _polls);
        Emit("0-poll-cost", ("polls", _polls), ("cpuMsPerPoll", F(_pollCostCpuMs)));
    }

    // ------------------------------------------------------------------ queries

    private (double ServerMs, double ManagedKb, double HeldKb, string Join) TimedQuery(string rql)
    {
        using var content = new StringContent("{\"Query\":" + JsonSerializer.Serialize(rql + " include timings()") + "}", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = _http.PostAsync(_queryUrl, content).GetAwaiter().GetResult();
        string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(json);
        double ms = document.RootElement.TryGetProperty("DurationInMs", out JsonElement duration) ? duration.GetDouble() : 0;
        double managed = 0, held = 0;
        var names = new SortedSet<string>();
        if (document.RootElement.TryGetProperty("Timings", out JsonElement timings))
            Walk(timings, ref managed, ref held, names);

        return (ms, managed / 1024, held / 1024, string.Join(',', names));
    }

    private static void Walk(JsonElement element, ref double managed, ref double held, SortedSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                Walk(item, ref managed, ref held, names);

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Contains("Join", StringComparison.Ordinal))
                names.Add(property.Name);

            if (property.Name == "Counters" && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty counter in property.Value.EnumerateObject())
                {
                    if (counter.Value.ValueKind != JsonValueKind.Number)
                        continue;

                    if (counter.Name is "TotalAllocatedManagedBytes" or "AllocatedManagedBytes")
                        managed = Math.Max(managed, counter.Value.GetDouble());
                    else if (counter.Name is "HeldUnmanagedBytesPeak" or "ScanHeldUnmanagedBytes")
                        held = Math.Max(held, counter.Value.GetDouble());
                    else if (counter.Name.Contains("Join", StringComparison.Ordinal))
                        names.Add(counter.Name + "=" + counter.Value.GetDouble());
                }
            }

            Walk(property.Value, ref managed, ref held, names);
        }
    }

    private async Task QueryPhaseAsync()
    {
        int[] clientCounts = Env("CLIENTS", "1,4,16").Split(',').Select(int.Parse).ToArray();
        int seconds = int.Parse(Env("SECONDS", "20"));
        int reps = int.Parse(Env("REPS", "3"));
        HashSet<string> only = Env("QUERIES") is { Length: > 0 } names ? [.. names.Split(',')] : null;
        var queries = (_scenario == "FF" ? FfQueries(_arm) : Queries(_arm)).Where(x => only == null || only.Contains(x.Name)).ToList();
        bool oracle = _arm == "B";

        if (_scenario != "FF" && (only == null || only.Contains("QLs") || only.Contains("QLsDeep")))
        {
            long matched = TotalOf(Queries(_arm).First(x => x.Name == "Q1").Rql);
            Emit("4-leftscore-matched", ("matched", matched));
            foreach ((string n, int skip) in new[] { ("QLs", 0), ("QLsDeep", (int)matched - 10) })
            {
                if (only != null && only.Contains(n) == false)
                    continue;

                if (_arm == "C")
                {
                    int s0 = skip;
                    queries.Add((n, LdScoreMatched(skip)));
                    _ops[n] = () => LdSequence(s0);
                    _extraSingles[n] = [(n + "-second", LdScoreUnmatched(Math.Max(0, skip - (int)matched), skip == 0 ? 20 : 10))];
                }
                else
                {
                    queries.Add((n, LeftScore(skip)));
                }
            }
        }

        foreach ((string name, string rql) in queries)
        {
            if (_scenario == "FF")
            {
                Emit("4-oracle", FfOracleRecord(name, rql));
                if (oracle && name == "FF3")
                    Emit("4-oracle-join", OracleRecord(name, rql));
            }
            else if (oracle)
                Emit("4-oracle", OracleRecord(name, rql));

            SingleTimings(name, rql);
            if (_extraSingles.TryGetValue(name, out (string Name, string Rql)[] extras))
            {
                foreach ((string extraName, string extraRql) in extras)
                    SingleTimings(extraName, extraRql);
            }
        }

        for (int rep = 1; rep <= reps; rep++)
        {
            foreach ((string name, string rql) in queries)
            {
                foreach (int clients in clientCounts)
                    await QueryCellAsync(name, rql, clients, seconds, rep);
            }
        }
    }

    private readonly Dictionary<string, (string Name, string Rql)[]> _extraSingles = new();

    private void SingleTimings(string name, string rql)
    {
        for (int i = 0; i < 5; i++)
            TimedQuery(rql);

        QuerySum before = _arm == "B" ? SumQuery() : null;
        var server = new List<double>();
        var managed = new List<double>();
        var held = new List<double>();
        string joinIndex = null;
        for (int i = 0; i < 30; i++)
        {
            var r = TimedQuery(rql);
            server.Add(r.ServerMs);
            managed.Add(r.ManagedKb);
            held.Add(r.HeldKb);
            joinIndex = r.Join;
        }

        QuerySum after = _arm == "B" ? SumQuery() : null;
        Emit("4-query-one", ("query", name), ("serverMsMedian", F(Percentile(server, 0.5))), ("serverMsMin", F(server.Min())), ("managedKbMedian", F(Percentile(managed, 0.5))),
            ("heldNativeKbMedian", F(Percentile(held, 0.5))), ("joinServed", after == null ? "n/a" : after.Served - before.Served), ("joinFallbacks", after == null ? "n/a" : Reasons(before, after)), ("joinTimings", joinIndex));
    }

    private sealed record Answer(List<string> Rows, long Total);

    private Answer Ask(string rql)
    {
        string body = PostBody(Payload(rql)) ?? throw new InvalidOperationException("the query failed: " + rql);
        using JsonDocument document = JsonDocument.Parse(body);
        return new Answer(document.RootElement.GetProperty("Results").EnumerateArray().Select(x => x.GetRawText()).ToList(), document.RootElement.GetProperty("TotalResults").GetInt64());
    }

    /// <summary>The answer of the join index and the answer of the join of the server (the join index is told not to serve) for the same snapshots: rows with their metadata (@index-score), order and total.</summary>
    private (Answer Served, Answer Scan, bool ServedByJoin) Both(string rql)
    {
        JoinIndex joinIndex = Join;
        joinIndex.QueryHooks = null;
        long servedBefore = joinIndex.QueryCounters.ServedCount;
        Answer served = Ask(rql);
        bool byJoin = joinIndex.QueryCounters.ServedCount > servedBefore;
        joinIndex.QueryHooks = new JoinIndexQueryHooks { ForceFallback = true };
        try
        {
            return (served, Ask(rql), byJoin);
        }
        finally
        {
            joinIndex.QueryHooks = null;
        }
    }

    private static string OneLine(string text) => text.Replace('\n', ' ').Replace(';', ',').Replace('|', '/');

    private (string Key, object Value)[] OracleRecord(string name, string rql)
    {
        try
        {
            (Answer served, Answer scan, bool byJoin) = Both(rql);
            bool same = served.Total == scan.Total && served.Rows.SequenceEqual(scan.Rows);
            return [("query", name), ("same", same), ("total", served.Total), ("scanTotal", scan.Total), ("servedByJoin", byJoin)];
        }
        catch (Exception e)
        {
            return [("query", name), ("same", false), ("detail", OneLine(e.Message))];
        }
    }

    private static string Payload(string rql) => "{\"Query\":" + JsonSerializer.Serialize(rql) + "}";

    private long TotalOf(string rql)
    {
        string body = PostBody(Payload(rql));
        Match match = Regex.Match(body ?? "", "\"TotalResults\":(\\d+)");
        return match.Success ? long.Parse(match.Groups[1].Value) : -1;
    }

    private static string LdScoreMatched(int skip) => $"from index 'LD1' where Status = 'open' and CompanyCountry = 'C3' order by score() limit {skip}, 20";

    private static string LdScoreUnmatched(int skip, long take) => $"from index 'LD1' where Status = 'open' and not CompanyCountry = 'C3' order by score() limit {skip}, {take}";

    private static string LeftScore(int skip) => $"{Main} left join (from index 'JCompanies' where Country = 'C3') as c on m.CompanyId = id(c) where m.Status = 'open' order by score() limit {skip}, 20";

    /// <summary>The left join ordered by score on the LoadDocument index: the matched entries first, then (only when the page is not full) the unmatched ones.</summary>
    private bool LdSequence(int skip, List<string> ids = null)
    {
        string first = PostBody(Payload(LdScoreMatched(skip)));
        if (first == null)
            return false;

        Match match = Regex.Match(first, "\"TotalResults\":(\\d+)");
        long total = long.Parse(match.Groups[1].Value);
        long rows = Math.Clamp(total - skip, 0, 20);
        ids?.AddRange(IdsOf(first));
        if (rows >= 20)
            return true;

        string second = PostBody(Payload(LdScoreUnmatched((int)Math.Max(0, skip - total), 20 - rows)));
        ids?.AddRange(IdsOf(second ?? ""));
        return second != null;
    }

    private static List<string> IdsOf(string body)
    {
        var ids = new List<string>();
        if (body.Length == 0)
            return ids;

        using JsonDocument document = JsonDocument.Parse(body);
        foreach (JsonElement row in document.RootElement.GetProperty("Results").EnumerateArray())
            ids.Add(row.GetProperty("@metadata").GetProperty("@id").GetString());

        return ids;
    }

    private async Task QueryCellAsync(string name, string rql, int clients, int seconds, int rep)
    {
        string payload = "{\"Query\":" + JsonSerializer.Serialize(rql) + "}";
        _ops.TryGetValue(name, out Func<bool> op);
        await Task.Run(() => RunClients(payload, op, 2, TimeSpan.FromSeconds(3)));
        await Task.Delay(500);
        QuerySum before = _arm == "B" ? SumQuery() : null;
        Process process = Process.GetCurrentProcess();
        long cgBefore = Host.CgroupCpuUsec();
        process.Refresh();
        TimeSpan cpu = process.TotalProcessorTime;
        long allocated = GC.GetTotalAllocatedBytes(false);
        using var sampler = new Sampler();
        var wall = Stopwatch.StartNew();
        (List<double> latencies, int errors) = await Task.Run(() => RunClients(payload, op, clients, TimeSpan.FromSeconds(seconds)));
        double elapsed = wall.Elapsed.TotalSeconds;
        process.Refresh();
        double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        double cgCpuMs = (Host.CgroupCpuUsec() - cgBefore) / 1000.0;
        double managedKb = (GC.GetTotalAllocatedBytes(false) - allocated) / 1024.0;
        double nativePeak = sampler.PeakNative;
        QuerySum after = _arm == "B" ? SumQuery() : null;
        int count = latencies.Count;
        double perQuery = cpuMs / Math.Max(1, count);
        Emit("4-cell", ("query", name), ("clients", clients), ("rep", rep), ("queries", count), ("errors", errors), ("qps", F(count / elapsed)), ("p50Ms", F(Percentile(latencies, 0.5))),
            ("p99Ms", F(Percentile(latencies, 0.99))), ("cpuMsPerQuery", F(perQuery)), ("cgroupCpuMsPerQuery", F(cgCpuMs / Math.Max(1, count))), ("cpuCeilingQps4", F(4000.0 / perQuery)),
            ("managedKbPerQuery", F(managedKb / Math.Max(1, count))), ("nativePeakAboveStartMB", F(Mb((long)nativePeak - sampler.StartNative))),
            ("served", after == null ? "n/a" : after.Served - before.Served), ("fallbacks", after == null ? "n/a" : after.Fallbacks - before.Fallbacks), ("fallbackReasons", after == null ? "n/a" : Reasons(before, after)),
            ("waits", after == null ? "n/a" : after.Waits - before.Waits), ("waitTotalMs", after == null ? "n/a" : F(after.WaitTotalMs - before.WaitTotalMs)), ("waitMaxMs", after == null ? "n/a" : F(after.WaitMaxMs)));
    }

    private (List<double> Latencies, int Errors) RunClients(string payload, Func<bool> op, int clients, TimeSpan duration)
    {
        DateTime until = DateTime.UtcNow + duration;
        var all = new List<double>();
        int errors = 0;
        Thread[] threads = Enumerable.Range(0, clients).Select(_ => new Thread(() =>
        {
            var local = new List<double>();
            while (DateTime.UtcNow < until)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    if (op != null)
                    {
                        if (op() == false)
                            Interlocked.Increment(ref errors);
                    }
                    else
                    {
                        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                        using HttpResponseMessage response = _http.PostAsync(_queryUrl, content).GetAwaiter().GetResult();
                        response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                        if (response.IsSuccessStatusCode == false)
                            Interlocked.Increment(ref errors);
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref errors);
                }

                local.Add(watch.Elapsed.TotalMilliseconds);
            }

            lock (all)
                all.AddRange(local);
        })).ToArray();

        foreach (Thread thread in threads)
            thread.Start();

        foreach (Thread thread in threads)
            thread.Join();

        return (all, errors);
    }

    // ------------------------------------------------------------------ writes

    private Placement NewPlacement(Random random, string companyId, int k)
    {
        return new Placement { Text = Words(1, k, 8), Status = "open", Rank = k % 1000, CompanyId = companyId, CandidateId = "candidates/0", OwnerId = "users/0" };
    }

    private (string Key, object Value)[] RefresherDelta(JoinSum before, JoinSum after)
    {
        if (_arm != "B")
            return [];

        return
        [
            ("joinThreadCpuMs", F(after.ThreadCpuMs - before.ThreadCpuMs)), ("outerThreadCpuMs", F(after.OuterThreadCpuMs - before.OuterThreadCpuMs)),
            ("joinIds", after.Ids - before.Ids), ("joinRefreshes", after.Refreshes - before.Refreshes), ("joinNoOps", after.NoOps - before.NoOps), ("joinCommits", after.Refreshes - before.Refreshes),
            ("joinListUpdates", after.ListUpdates - before.ListUpdates), ("joinFileGrowthMB", F(after.AllocatedMb - before.AllocatedMb)), ("joinBuilds", after.Builds - before.Builds),
            ("staleClearMs", F(_lastStaleClearMs))
        ];
    }

    private (long Input, long Output, long Batches) IndexedSince(DateTime since)
    {
        long input = 0, output = 0, batches = 0;
        foreach (IndexPerformanceStats stats in _store.Maintenance.Send(new GetIndexPerformanceStatisticsOperation(IndexNames)))
        {
            foreach (IndexingPerformanceStats batch in stats.Performance ?? [])
            {
                if (batch.Started >= since)
                {
                    input += batch.InputCount;
                    output += batch.OutputCount;
                    batches++;
                }
            }
        }

        return (input, output, batches);
    }

    private Task Inserts(string what, int count, Func<int, string> company, Random random)
    {
        return WriteAsync("6-insert", what, count, (session, k) => session.Store(NewPlacement(random, company(k), k), $"placements/{Interlocked.Increment(ref _nextPlacement)}"));
    }

    private async Task WriteAsync(string phase, string what, int count, Action<IDocumentSession, int> item)
    {
        await Task.Delay(1500);
        JoinSum rBefore = _arm == "B" ? SumJoin() : null;
        DateTime since = DateTime.UtcNow;
        var measure = new Measure();
        _polls = 0;
        var watch = Stopwatch.StartNew();
        using (IDocumentSession session = _store.OpenSession())
        {
            for (int k = 0; k < count; k++)
            {
                item(session, k);
                if (k % 1000 == 999)
                {
                    session.SaveChanges();
                    session.Advanced.Clear();
                }
            }

            session.SaveChanges();
        }

        double storeMs = watch.Elapsed.TotalMilliseconds;
        (double indexMs, double joinMs) = await WaitCaughtUpAsync(watch);
        double cpuMs = Math.Max(0, measure.CpuMs - _polls * _pollCostCpuMs);
        long allocated = measure.AllocatedBytes;
        double native = measure.NativePeakAboveStartKb;
        measure.Stop(sync: false);
        await Task.Delay(100);
        JoinSum rAfter = _arm == "B" ? SumJoin() : null;
        (long indexedInput, long indexedOutput, long indexBatches) = IndexedSince(since);
        var values = new List<(string, object)>
        {
            ("what", what), ("messages", count), ("storeMs", F(storeMs)), ("indexMs", F(indexMs)), ("joinExtraMs", F(joinMs - indexMs)), ("joinStaleExtraMs", F(_arm == "B" ? _lastStaleClearMs - indexMs : 0)), ("totalMs", F(joinMs)), ("usPerMessage", F(joinMs * 1000 / count)),
            ("processCpuMs", F(cpuMs)), ("cpuUsPerMessage", F(cpuMs * 1000 / count)), ("processManagedMB", F(Mb(allocated))), ("nativePeakAboveStartKB", F(native)),
            ("indexedInput", indexedInput), ("indexedOutput", indexedOutput), ("indexBatches", indexBatches)
        };
        if (_arm == "B")
            values.AddRange(RefresherDelta(rBefore, rAfter));

        Emit(phase, values.ToArray());
        if (phase == "6-outer")
            await OracleAfterAsync(what);
    }

    private Placement Original(int i)
    {
        return new Placement
        {
            Text = Words(1, i * 10L, 8), Status = Statuses[H(2, i) % 10], Rank = (int)(H(3, i) % 1000), CompanyId = i % 10 == 0 ? "companies/hub" : "companies/" + H(10, i) % _companies,
            CandidateId = "candidates/" + H(11, i) % _candidates, OwnerId = "users/" + H(12, i) % _users
        };
    }

    /// <summary>Arm B: the answers of the join index for the grouped query and Q2 equal the join of the same snapshots, and the join index served them.</summary>
    private async Task OracleAfterAsync(string what)
    {
        if (_arm != "B")
            return;

        await Task.Delay(50);
        var values = new List<(string, object)> { ("what", what) };
        foreach ((string name, string rql) in new[] { ("grouped", $"{Main} {JC} select id() {L}"), ("Q2", Queries("B").First(x => x.Name == "Q2").Rql) })
        {
            (string Key, object Value)[] record = OracleRecord(name, rql);
            values.Add((name + "Same", record.First(x => x.Key == "same").Value));
            values.Add((name + "Total", record.FirstOrDefault(x => x.Key == "total").Value ?? "-"));
            values.Add((name + "ServedByJoin", record.FirstOrDefault(x => x.Key == "servedByJoin").Value ?? false));
            if (record.FirstOrDefault(x => x.Key == "detail").Value is string detail)
                values.Add((name + "Detail", detail));
        }

        Emit("7-oracle", values.ToArray());
    }

    private async Task Flip(string what, string id, Company changed, int referencing)
    {
        await Task.Delay(1500);
        JoinSum rBefore = _arm == "B" ? SumJoin() : null;
        DateTime since = DateTime.UtcNow;
        var measure = new Measure();
        _polls = 0;
        var watch = Stopwatch.StartNew();
        using (IDocumentSession session = _store.OpenSession())
        {
            session.Store(changed, id);
            session.SaveChanges();
        }

        double storeMs = watch.Elapsed.TotalMilliseconds;
        (double indexMs, double joinMs) = await WaitCaughtUpAsync(watch, pollMs: 2);
        double cpuMs = Math.Max(0, measure.CpuMs - _polls * _pollCostCpuMs);
        long allocated = measure.AllocatedBytes;
        double native = measure.NativePeakAboveStartKb;
        measure.Stop(sync: false);
        await Task.Delay(100);
        JoinSum rAfter = _arm == "B" ? SumJoin() : null;
        (long indexedInput, long indexedOutput, long indexBatches) = IndexedSince(since);
        var values = new List<(string, object)>
        {
            ("what", what), ("referencing", referencing), ("storeMs", F(storeMs)), ("indexMs", F(indexMs)), ("joinExtraMs", F(joinMs - indexMs)), ("joinStaleExtraMs", F(_arm == "B" ? _lastStaleClearMs - indexMs : 0)), ("wallMsUntilNotStale", F(joinMs)),
            ("processCpuMs", F(cpuMs)), ("processManagedMB", F(Mb(allocated))), ("nativePeakAboveStartKB", F(native)),
            ("indexedInput", indexedInput), ("indexedOutput", indexedOutput), ("indexBatches", indexBatches)
        };
        if (_arm == "B")
            values.AddRange(RefresherDelta(rBefore, rAfter));

        Emit("7-flip", values.ToArray());
    }

    private static Company Variant(int i, string kind, bool changed)
    {
        Company company = MakeCompany(i, false);
        if (changed == false)
            return company;

        switch (kind)
        {
            case "country":
                company.Country = company.Country == "C3" ? "C9" : "C3";
                break;
            case "name":
                company.Name = "Renamed " + i;
                break;
            case "size":
                company.Size += 1;
                break;
            case "about":
                company.About = "changed " + company.About;
                break;
        }

        return company;
    }

    private int[] SampleIds(int count)
    {
        var all = new int[_placements];
        for (int i = 0; i < all.Length; i++)
            all[i] = i;

        var random = new Random(99);
        for (int i = 0; i < count; i++)
        {
            int j = i + random.Next(all.Length - i);
            (all[i], all[j]) = (all[j], all[i]);
        }

        return all.AsSpan(0, count).ToArray();
    }

    // the flips come first so that the hub still has the 100,000 placements of the load. Declared by the join index: Country (also the grouping Country = 'C3'), Name, Size; not declared: About
    private async Task WritesPhaseAsync()
    {
        var random = new Random(7);
        int typical = (int)(H(10, 1) % _companies);
        string[] kinds = ["country", "name", "size", "about"];
        for (int round = 0; round <= 3; round++)
        {
            string tag = round == 0 ? "WARM " : "";
            foreach (string kind in kinds)
            {
                string label = kind == "about" ? "about (undeclared field)" : kind == "country" ? "country (declared field + grouping)" : kind + " (declared field)";
                await Flip($"{tag}typical company {label} changes", $"companies/{typical}", Variant(typical, kind, true), _placements / _companies);
                await OracleAfterAsync($"{tag}typical {kind} changes");
                await Flip($"{tag}typical company {label} back", $"companies/{typical}", Variant(typical, kind, false), _placements / _companies);
                await OracleAfterAsync($"{tag}typical {kind} back");
                await Flip($"{tag}hub company {label} changes", "companies/hub", Variant(-1, kind, true), _placements / 10);
                await OracleAfterAsync($"{tag}hub {kind} changes");
                await Flip($"{tag}hub company {label} back", "companies/hub", Variant(-1, kind, false), _placements / 10);
                await OracleAfterAsync($"{tag}hub {kind} back");
            }
        }

        // outer documents that already exist: 10,000 different ones each time (the first round is the warm-up)
        int[] ids = SampleIds(4 * 30_000);
        for (int round = 0; round <= 3; round++)
        {
            string tag = round == 0 ? "WARM " : "";
            int offset = round * 30_000;
            await WriteAsync("6-outer", $"{tag}update 10,000 placements (Rank, a non-join field)", 10_000, (session, k) =>
            {
                int i = ids[offset + k];
                Placement placement = Original(i);
                placement.Rank = (placement.Rank + 1) % 1000;
                session.Store(placement, $"placements/{i}");
            });
            await WriteAsync("6-outer", $"{tag}move 10,000 placements to another company", 10_000, (session, k) =>
            {
                int i = ids[offset + 10_000 + k];
                Placement placement = Original(i);
                placement.CompanyId = "companies/" + (H(10, i) + 7) % _companies;
                session.Store(placement, $"placements/{i}");
            });
            await WriteAsync("6-outer", $"{tag}delete 10,000 placements", 10_000, (session, k) => session.Delete($"placements/{ids[offset + 20_000 + k]}"));
        }

        await Inserts("WARM insert 10,000 placements, random companies", 10_000, _ => "companies/" + random.Next(_companies), random);
        await Inserts("WARM insert 10,000 placements into the hub company", 10_000, _ => "companies/hub", random);
        for (int round = 1; round <= 3; round++)
        {
            await Inserts("insert 10,000 placements, random companies", 10_000, _ => "companies/" + random.Next(_companies), random);
            await Inserts("insert 10,000 placements into the hub company", 10_000, _ => "companies/hub", random);
        }
    }

    // ------------------------------------------------------------------ cost of the join index for a join that it does not serve, and of a wake

    private async Task SetModeAsync(string mode)
    {
        if (mode == "disabled")
        {
            _store.Maintenance.Send(new DisableIndexOperation(JoinName));
            Join.QueryHooks = null;
            return;
        }

        _store.Maintenance.Send(new EnableIndexOperation(JoinName));
        Join.QueryHooks = mode == "forced" ? new JoinIndexQueryHooks { ForceFallback = true } : null;
        DateTime until = DateTime.UtcNow.AddMinutes(5);
        while (true)
        {
            IndexStats stats = _store.Maintenance.Send(new GetIndexStatisticsOperation(JoinName));
            if (stats.IsStale == false && stats.State == IndexState.Normal && stats.Status == IndexRunningStatus.Running)
                break;

            if (DateTime.UtcNow > until)
                throw new TimeoutException("the join index did not catch up");

            await Task.Delay(50);
        }

        await Task.Delay(500);
    }

    /// <summary>Arm B: the same join answered by the join of the server with the join index (serving) serving, (forced) present and told not to serve: the query still opens its snapshot and peeks at the two indexes,
    /// and (disabled) switched off: the query finds the join index and uses nothing of it. Arm A is the join without a join index at all.</summary>
    private async Task OverheadPhaseAsync()
    {
        if (_arm != "B")
            return;

        int[] clientCounts = Env("OVERHEAD_CLIENTS", "1,16").Split(',').Select(int.Parse).ToArray();
        int seconds = int.Parse(Env("SECONDS", "20"));
        int reps = int.Parse(Env("REPS", "3"));
        HashSet<string> names = [.. Env("OVERHEAD_QUERIES", "plain,Q1,Q2").Split(',')];
        var queries = Queries("B").Where(x => names.Contains(x.Name)).ToList();
        string[] modes = ["serving", "forced", "disabled"];
        try
        {
            for (int rep = 1; rep <= reps; rep++)
            {
                foreach (string mode in modes)
                {
                    await SetModeAsync(mode);
                    foreach ((string name, string rql) in queries)
                    {
                        if (rep == 1)
                            SingleTimings(name + "@" + mode, rql);

                        foreach (int clients in clientCounts)
                            await QueryCellAsync(name + "@" + mode, rql, clients, seconds, rep);
                    }
                }
            }
        }
        finally
        {
            await SetModeAsync("serving");
        }
    }

    private static async Task WakeAsync(Raven.Server.Documents.Indexes.Index index)
    {
        var awaiter = index.GetIndexingBatchAwaiter();
        index.ScheduleIndexingRun();
        await awaiter.WaitAsync();
    }

    /// <summary>What a wake of an index that finds nothing to do costs: wall time, processor time of the process and of the indexing thread, managed allocation. The join index answers it from two counters.</summary>
    private async Task WakePhaseAsync()
    {
        int wakes = int.Parse(Env("WAKES", "3000"));
        string[] names = _arm == "B" ? [JoinName, "JCompanies", "JMain"] : _arm == "C" ? ["LD1"] : ["JCompanies", "JMain"];
        Process process = Process.GetCurrentProcess();
        for (int round = 1; round <= 3; round++)
        {
            foreach (string name in names)
            {
                Raven.Server.Documents.Indexes.Index index = _database.IndexStore.GetIndex(name);
                for (int i = 0; i < 200; i++)
                    await WakeAsync(index);

                GC.Collect();
                await Task.Delay(500);
                long noOpsBefore = name == JoinName ? Join.Counters.NoOps : 0;
                process.Refresh();
                TimeSpan cpu = process.TotalProcessorTime;
                double threadCpu = IndexThreadCpuMs(name);
                long allocated = GC.GetTotalAllocatedBytes(true);
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < wakes; i++)
                    await WakeAsync(index);

                double wallMs = watch.Elapsed.TotalMilliseconds;
                process.Refresh();
                Emit("11-wake", ("index", name), ("round", round), ("wakes", wakes), ("usPerWake", F(wallMs * 1000 / wakes)), ("processCpuUsPerWake", F((process.TotalProcessorTime - cpu).TotalMilliseconds * 1000 / wakes)),
                    ("indexThreadCpuUsPerWake", F((IndexThreadCpuMs(name) - threadCpu) * 1000 / wakes)), ("allocatedBytesPerWake", (GC.GetTotalAllocatedBytes(true) - allocated) / wakes),
                    ("noOpRefreshes", name == JoinName ? Join.Counters.NoOps - noOpsBefore : 0));
            }
        }
    }

    // ------------------------------------------------------------------ live writes

    private async Task LivePhaseAsync()
    {
        int seconds = int.Parse(Env("LIVE_SECONDS", "30"));
        int[] rates = Env("LIVE_RATES", "100,1000,10000").Split(',').Select(int.Parse).ToArray();
        foreach (int rate in rates)
            await LiveCellAsync($"M{rate}", rate, 0, false, seconds);

        await LiveCellAsync("C10", 0, 10, false, seconds);
        await LiveCellAsync("hub", 100, 0, true, seconds);
    }

    private async Task MemLiveAsync()
    {
        int seconds = int.Parse(Env("MEMLIVE_SECONDS", "120"));
        await LiveCellAsync("M1000-mem", 1000, 0, false, seconds);
        IdleMemory("after 1,000 writes/s");
    }

    private async Task LiveCellAsync(string label, int writesPerSecond, int contextFlipsPerSecond, bool hubFlips, int seconds)
    {
        await Task.Delay(2000);
        string readerQuery = $"{Main} {JC} select id() {L}";
        string payload = "{\"Query\":" + JsonSerializer.Serialize(readerQuery) + "}";
        using var stop = new CancellationTokenSource();
        long written = 0;
        var saveMs = new List<double>();
        var latencies = new List<double>();
        var waits = new List<double>();
        var lagMs = new List<double>();
        var queueLag = new List<double>();
        long errors = 0, overlapped = 0, starts = 0, clean = 0, flips = 0, ctxFlips = 0;
        int inflight = 0;

        QuerySum qBefore = _arm == "B" ? SumQuery() : null;
        JoinSum rBefore = _arm == "B" ? SumJoin() : null;
        DateTime startedAt = DateTime.UtcNow;
        var seenBatches = new Dictionary<int, IndexingPerformanceStats>();
        var seenJoinBatches = new Dictionary<int, IndexingPerformanceStats>();
        var breakdowns = new List<string>();
        var measure = new Measure();
        var clock = Stopwatch.StartNew();

        static long Commit4Kbs(IndexingPerformanceOperation op) => op == null ? 0 : (op.CommitDetails?.NumberOf4KbsWrittenToDisk ?? 0) + (op.Operations ?? []).Sum(Commit4Kbs);

        void CollectBatches()
        {
            if (_arm == "B")
            {
                foreach (IndexPerformanceStats stats in _store.Maintenance.Send(new GetIndexPerformanceStatisticsOperation([JoinName])))
                {
                    foreach (IndexingPerformanceStats batch in stats.Performance ?? [])
                    {
                        if (batch.Started >= startedAt)
                            seenJoinBatches[batch.Id] = batch;
                    }
                }

                string breakdown = Join?.Counters.LastBreakdown;
                if (breakdown != null && (breakdowns.Count == 0 || breakdowns[^1] != breakdown))
                    breakdowns.Add(breakdown);
            }

            foreach (IndexPerformanceStats stats in _store.Maintenance.Send(new GetIndexPerformanceStatisticsOperation(["JMain"])))
            {
                foreach (IndexingPerformanceStats batch in stats.Performance ?? [])
                {
                    if (batch.Started >= startedAt && batch.InputCount > 0)
                        seenBatches[batch.Id] = batch;
                }
            }
        }

        var writerRandom = new Random(1);
        var flipperRandom = new Random(2);
        var lagRandom = new Random(3);
        Task writer = Task.Run(() =>
        {
            if (writesPerSecond == 0)
                return;

            while (stop.IsCancellationRequested == false)
            {
                long due = (long)(clock.Elapsed.TotalSeconds * writesPerSecond) - Interlocked.Read(ref written);
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
                        session.Store(NewPlacement(writerRandom, "companies/" + writerRandom.Next(_companies), (int)(written + i)), $"placements/{Interlocked.Increment(ref _nextPlacement)}");

                    session.SaveChanges();
                }

                lock (saveMs)
                    saveMs.Add(watch.Elapsed.TotalMilliseconds);

                Interlocked.Add(ref written, due);
                Thread.Sleep(Math.Max(0, 50 - (int)watch.ElapsedMilliseconds));
            }
        });

        Task flipper = Task.Run(async () =>
        {
            bool alternative = false;
            var next = Stopwatch.StartNew();
            while (stop.IsCancellationRequested == false)
            {
                if (hubFlips && next.ElapsedMilliseconds >= 2000)
                {
                    next.Restart();
                    alternative = !alternative;
                    Company hub = MakeCompany(-1, alternative);
                    hub.Country = HubCountry(alternative);
                    using IDocumentSession session = _store.OpenSession();
                    session.Store(hub, "companies/hub");
                    session.SaveChanges();
                    Interlocked.Increment(ref flips);
                }

                if (contextFlipsPerSecond > 0)
                {
                    int id = flipperRandom.Next(_companies);
                    using IDocumentSession session = _store.OpenSession();
                    session.Store(MakeCompany(id, Interlocked.Increment(ref ctxFlips) % 2 == 0), $"companies/{id}");
                    session.SaveChanges();
                    await Task.Delay(1000 / contextFlipsPerSecond);
                }
                else
                {
                    await Task.Delay(20);
                }
            }
        });

        // index lag: a marker document every 250 ms, the time until the outer index answers a query for it
        Task lag = Task.Run(async () =>
        {
            int k = 0;
            while (stop.IsCancellationRequested == false)
            {
                string term = "probe" + label.Replace("-", "").ToLowerInvariant() + k++;
                using (IDocumentSession session = _store.OpenSession())
                {
                    session.Store(new Placement { Text = "marker", Status = term, Rank = 1, CompanyId = "companies/" + lagRandom.Next(_companies), CandidateId = "candidates/0", OwnerId = "users/0" }, $"placements/{Interlocked.Increment(ref _nextPlacement)}");
                    session.SaveChanges();
                }

                var watch = Stopwatch.StartNew();
                string body = "{\"Query\":" + JsonSerializer.Serialize($"from index 'JMain' where Status = '{term}' select id() limit 0, 1") + "}";
                while (watch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await _http.PostAsync(_queryUrl, content);
                    string json = await response.Content.ReadAsStringAsync();
                    if (json.Contains("\"TotalResults\":1", StringComparison.Ordinal))
                        break;

                    await Task.Delay(1);
                }

                lock (lagMs)
                    lagMs.Add(watch.Elapsed.TotalMilliseconds);

                await Task.Delay(250);
            }
        });

        Task batches = Task.Run(async () =>
        {
            while (stop.IsCancellationRequested == false)
            {
                CollectBatches();
                await Task.Delay(400);
            }
        });

        // Poisson readers, 20 queries per second, at most 8 at a time
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

                double scheduled = next;
                await slots.WaitAsync();
                running.Add(Task.Run(() =>
                {
                    try
                    {
                        lock (queueLag)
                            queueLag.Add((clock.Elapsed.TotalSeconds - scheduled) * 1000);

                        long mine = Interlocked.Increment(ref starts);
                        Interlocked.Increment(ref inflight);
                        QuerySum a = _arm == "B" ? SumQuery() : null;
                        var watch = Stopwatch.StartNew();
                        try
                        {
                            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
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
                        QuerySum b = _arm == "B" ? SumQuery() : null;
                        bool alone = Interlocked.Read(ref starts) == mine && Volatile.Read(ref inflight) == 1;
                        lock (latencies)
                        {
                            latencies.Add(ms);
                            if (a != null)
                            {
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
        await Task.WhenAll(writer, flipper, lag, batches, readers);
        double elapsed = clock.Elapsed.TotalSeconds;
        double cpuMs = measure.CpuMs;
        long allocated = measure.AllocatedBytes;
        CollectBatches();
        (long anon, long file, _, _) = Host.CgroupMemory();
        var settle = Stopwatch.StartNew();
        (double indexMs, double joinMs) = await WaitCaughtUpAsync(settle, 10, 5);
        measure.Stop(sync: false);
        QuerySum qAfter = _arm == "B" ? SumQuery() : null;
        JoinSum rAfter = _arm == "B" ? SumJoin() : null;
        var perf = seenBatches.Values.ToList();
        long writes = Interlocked.Read(ref written);
        var values = new List<(string, object)>
        {
            ("label", label), ("readQps", Env("LIVE_READ_QPS", "20")), ("targetWritesPerS", writesPerSecond), ("writes", writes), ("achievedWritesPerS", F(writes / elapsed)), ("hubFlips", flips), ("contextFlips", ctxFlips), ("seconds", F(elapsed)),
            ("queries", latencies.Count), ("errors", errors), ("readerP50Ms", F(Percentile(latencies, 0.5))), ("readerP95Ms", F(Percentile(latencies, 0.95))), ("readerP99Ms", F(Percentile(latencies, 0.99))),
            ("queueLagP95Ms", F(Percentile(queueLag, 0.95))), ("saveChangesP50Ms", F(Percentile(saveMs, 0.5))), ("saveChangesP95Ms", F(Percentile(saveMs, 0.95))),
            ("indexLagP50Ms", F(Percentile(lagMs, 0.5))), ("indexLagP95Ms", F(Percentile(lagMs, 0.95))), ("indexLagP99Ms", F(Percentile(lagMs, 0.99))), ("lagSamples", lagMs.Count),
            ("outerBatches", perf.Count), ("outerBatchAvgMs", F(perf.Count == 0 ? 0 : perf.Average(x => ((x.Completed ?? x.Started) - x.Started).TotalMilliseconds))), ("outerBatchAvgInputs", F(perf.Count == 0 ? 0 : perf.Average(x => x.InputCount))),
            ("processCpuPercentOfCore", F(cpuMs / 1000 / elapsed * 100)), ("processManagedMBPer1000Writes", F(writes == 0 ? 0 : Mb(allocated) / (writes / 1000.0))), ("anonEndMB", F(Mb(anon))), ("fileEndMB", F(Mb(file))),
            ("settleIndexMs", F(indexMs)), ("settleJoinMs", F(joinMs - indexMs))
        };
        if (_arm == "B")
        {
            values.AddRange(
            [
                ("served", qAfter.Served - qBefore.Served), ("fallbacks", qAfter.Fallbacks - qBefore.Fallbacks), ("fallbackReasons", Reasons(qBefore, qAfter)),
                ("fallbackPercent", F(100.0 * (qAfter.Fallbacks - qBefore.Fallbacks) / Math.Max(1, qAfter.Served - qBefore.Served + qAfter.Fallbacks - qBefore.Fallbacks))),
                ("servedPercent", F(100.0 * (qAfter.Served - qBefore.Served) / Math.Max(1, qAfter.Served - qBefore.Served + qAfter.Fallbacks - qBefore.Fallbacks))),
                ("queriesThatWaited", qAfter.Waits - qBefore.Waits), ("waitCleanSamples", clean), ("waitOverlappedSkipped", overlapped), ("waitP50Ms", F(Percentile(waits, 0.5))), ("waitP95Ms", F(Percentile(waits, 0.95))),
                ("waitP99Ms", F(Percentile(waits, 0.99))), ("waitMaxMs", F(qAfter.WaitMaxMs)),
                ("refresherThreadCpuPercentOfCore", F((rAfter.ThreadCpuMs - rBefore.ThreadCpuMs) / 1000 / elapsed * 100)),
                ("outerIndexThreadCpuPercentOfCore", F((rAfter.OuterThreadCpuMs - rBefore.OuterThreadCpuMs) / 1000 / elapsed * 100)),
                ("refreshes", rAfter.Refreshes - rBefore.Refreshes), ("noOpRefreshes", rAfter.NoOps - rBefore.NoOps), ("listUpdates", rAfter.ListUpdates - rBefore.ListUpdates), ("idsProcessed", rAfter.Ids - rBefore.Ids),
                ("joinFileGrowthMB", F(rAfter.AllocatedMb - rBefore.AllocatedMb)),
                ("refreshesPerS", F((rAfter.Refreshes - rBefore.Refreshes) / elapsed)),
                ("joinBatches", seenJoinBatches.Count), ("joinBatchesWithWork", seenJoinBatches.Values.Count(x => Commit4Kbs(x.Details) > 0)),
                ("joinJournalKbPerWrite", F(writes == 0 ? 0 : seenJoinBatches.Values.Sum(x => Commit4Kbs(x.Details)) * 4.0 / writes)),
                ("joinBatchAvgMs", F(seenJoinBatches.Count == 0 ? 0 : seenJoinBatches.Values.Average(x => ((x.Completed ?? x.Started) - x.Started).TotalMilliseconds))),
                ("breakdownSamples", breakdowns.Count), ("breakdownMid", breakdowns.Count == 0 ? "" : breakdowns[breakdowns.Count / 2].Replace(';', ','))
            ]);
        }

        Emit("8-live", values.ToArray());
    }

    /// <summary>Arm D (join indexes and the LoadDocument index side by side, a smaller N): the two queries on LD1 return what the left join returns, page by page.</summary>
    private void LdOracle()
    {
        long matched = TotalOf(Queries("A").First(x => x.Name == "Q1").Rql);
        Emit("10-ld-oracle-matched", ("matched", matched));
        foreach (int skip in new[] { 0, 20, (int)matched - 10, (int)matched - 1, (int)matched, (int)matched + 15, (int)matched + 1000 })
        {
            string joinBody = PostBody(Payload(LeftScore(skip)));
            var ids = new List<string>();
            LdSequence(skip, ids);
            List<string> joinIds = IdsOf(joinBody ?? "");
            int firstDifference = -1;
            for (int i = 0; i < Math.Max(ids.Count, joinIds.Count) && firstDifference < 0; i++)
            {
                if (i >= ids.Count || i >= joinIds.Count || ids[i] != joinIds[i])
                    firstDifference = i;
            }

            Emit("10-ld-oracle", ("query", "left join order by score()"), ("skip", skip), ("joinRows", joinIds.Count), ("sequenceRows", ids.Count), ("sameIdsSameOrder", firstDifference < 0),
                ("sameIdsAnyOrder", ids.OrderBy(x => x).SequenceEqual(joinIds.OrderBy(x => x))), ("firstDifference", firstDifference));
        }

        string joinRank = PostBody(Payload(Queries("A").First(x => x.Name == "QL").Rql));
        string ldRank = PostBody(Payload(Queries("C").First(x => x.Name == "QL").Rql));
        List<string> a = IdsOf(joinRank ?? ""), b = IdsOf(ldRank ?? "");
        Emit("10-ld-oracle", ("query", "left join where Status = 'open' order by Rank"), ("skip", 0), ("joinRows", a.Count), ("sequenceRows", b.Count), ("sameIdsSameOrder", a.SequenceEqual(b)),
            ("sameIdsAnyOrder", a.OrderBy(x => x).SequenceEqual(b.OrderBy(x => x))), ("totalJoin", TotalOf(Queries("A").First(x => x.Name == "QL").Rql)), ("totalLd", TotalOf(Queries("C").First(x => x.Name == "QL").Rql)));
    }

    private async Task FinalCheckAsync()
    {
        try
        {
            JoinIndexInvariantChecker.Result result = await CheckJoinAsync(TimeSpan.FromMinutes(5));
            Emit("9-final-check", ("joinMatchesIndexes", result.Ok), ("compared", result.Compared), ("detail", OneLine(result.ToString())));
        }
        catch (Exception e)
        {
            Emit("9-final-check", ("error", OneLine(e.Message)));
        }
    }

    // ------------------------------------------------------------------ scenario FF: joins on a field
    // FfOuter documents (the placements) carry a key (JoinKey, a hub key for a tenth of them) and an array of company ids; FfInner documents carry the same key, about 20 of them per key.
    // FF1 and FF2 join field to field (many to many), FF3 joins the array of ids to the companies, FF4 is the left join of FF1.

    private class FfOuter { public string JoinKey; public string Status; public int Rank; public string[] CompanyIds; }
    private class FfInner { public string JoinKey; public string Tag; public string Rare; }

    private class FfMain : AbstractIndexCreationTask<FfOuter>
    {
        public FfMain()
        {
            Map = os => from o in os select new { o.JoinKey, o.Status, o.Rank, o.CompanyIds };
            Index(x => x.JoinKey, FieldIndexing.Key);
            Index(x => x.CompanyIds, FieldIndexing.Key);
        }
    }

    private class FfInnerIndex : AbstractIndexCreationTask<FfInner>
    {
        public FfInnerIndex()
        {
            Map = ins => from i in ins select new { i.JoinKey, i.Tag, i.Rare };
            Index(x => x.JoinKey, FieldIndexing.Key);
        }

        public override string IndexName => "FfInner";
    }

    private class FfLdIndex : AbstractIndexCreationTask<FfOuter>
    {
        public FfLdIndex()
        {
            Map = os => from o in os select new { o.Status, o.Rank, o.CompanyIds, CompanyCountry = o.CompanyIds.Select(id => LoadDocument<Company>(id).Country) };
        }

        public override string IndexName => "FfLD";
    }

    private class JCompaniesK : AbstractIndexCreationTask<Company>
    {
        public JCompaniesK()
        {
            Map = cs => from c in cs select new { CompanyKey = MetadataFor(c).Value<string>("@id"), c.Name, c.Country, c.Size };
            Index("CompanyKey", FieldIndexing.Key);
        }

        public override string IndexName => "JCompaniesK";
    }

    private class FfCompaniesKeyJoin : AbstractJoinCreationTask<JoinOuter, JoinInner>
    {
        public FfCompaniesKeyJoin()
        {
            UseOuterIndex("FfMain");
            UseInnerIndex("JCompaniesK");
            JoinOn("CompanyIds", "CompanyKey");
            AddField("Country");
            AddField("Name");
            AddField("Size", JoinFieldKind.Long);
            AddGrouping("Country", "C3");
        }

        public override string IndexName => JoinName;

        public override IndexDefinition CreateIndexDefinition()
        {
            IndexDefinition definition = base.CreateIndexDefinition();
            ApplyStorageMode(definition);
            return definition;
        }
    }

    // harness: the build under test may know a storage mode for its join indexes
    static partial void ApplyStorageMode(IndexDefinition definition);

    static partial void ApplyIdStorageMode(IndexDefinition definition);

    private static bool s_ff3Key => Environment.GetEnvironmentVariable("RAVEN_JOIN_PROD_FF3KEY") == "1";

    private class FfCompaniesJoin : AbstractJoinCreationTask<JoinOuter, JoinInner>
    {
        public FfCompaniesJoin()
        {
            UseOuterIndex("FfMain");
            UseInnerIndex("JCompanies");
            JoinOn("CompanyIds");
            AddField("Country");
            AddField("Name");
            AddField("Size", JoinFieldKind.Long);
            AddGrouping("Country", "C3");
        }

        public override string IndexName => JoinName;

        public override IndexDefinition CreateIndexDefinition()
        {
            IndexDefinition definition = base.CreateIndexDefinition();
            ApplyIdStorageMode(definition);
            return definition;
        }
    }

    private class FfKeyJoin : AbstractJoinCreationTask<JoinOuter, FfJoinInner>
    {
        public FfKeyJoin()
        {
            UseOuterIndex("FfMain");
            UseInnerIndex("FfInner");
            JoinOn("JoinKey", "JoinKey");
            AddField("Tag");
            AddField("Rare");
        }

        public override string IndexName => FfKeyJoinName;

        public override IndexDefinition CreateIndexDefinition()
        {
            IndexDefinition definition = base.CreateIndexDefinition();
            ApplyStorageMode(definition);
            return definition;
        }
    }

    private class FfJoinInner { public string Tag { get; set; } }

    private const string FfKeyJoinName = "FieldKeys";
    private const string FfFrom = "from index 'FfMain' as m";
    private const string FfSubTag = "join (from index 'FfInner' where Tag = 't7') as c on m.JoinKey = c.JoinKey";
    private const string FfSubRare = "join (from index 'FfInner' where Rare = 'r7') as c on m.JoinKey = c.JoinKey";
    private const string FfSubCompany = "join (from index 'JCompanies' where Country = 'C3') as c on m.CompanyIds = id(c)";
    private const string FfSubCompanyKey = "join (from index 'JCompaniesK' where Country = 'C3') as c on m.CompanyIds = c.CompanyKey";
    private const string FfTail = "where m.Status = 'open' order by m.Rank " + L;
    private const int FfInnersPerKey = 20;
    private const int FfHubKeys = 5;

    private int FfInnerCount => Math.Max(FfInnersPerKey, _placements / 10);

    private int FfKeys => FfInnerCount / FfInnersPerKey;

    private static List<(string Name, string Rql)> FfQueries(string arm)
    {
        if (arm == "C")
            return [("FF3", $"from index 'FfLD' where Status = 'open' and CompanyCountry = 'C3' order by Rank {L}")];

        return
        [
            ("FF1", $"{FfFrom} {FfSubTag} {FfTail}"),
            ("FF2", $"{FfFrom} {FfSubRare} {FfTail}"),
            ("FF3", $"{FfFrom} {(s_ff3Key ? FfSubCompanyKey : FfSubCompany)} {FfTail}"),
            ("FF4", $"{FfFrom} left {FfSubTag} {FfTail}")
        ];
    }

    // harness (RAVEN_JOIN_PROD_BIGHUB=n): the first n outer documents all carry the key k/all and the company companies/hubbig
    private int _bigHub;

    private string FfOuterKey(int i) => i < _bigHub ? "k/all" : i % 10 == 0 ? "k/hub" + H(30, i) % FfHubKeys : "k/" + H(31, i) % FfKeys;

    private string[] FfCompanyIds(int i)
    {
        var ids = new string[3];
        for (int j = 0; j < ids.Length; j++)
            ids[j] = j == 0 && i < _bigHub ? "companies/hubbig" : j == 0 && i % 10 == 0 ? "companies/hub" : "companies/" + H(20 + j, i) % _companies;

        return ids;
    }

    private string FfInnerKey(int j) => j < FfInnerCount ? "k/" + j / FfInnersPerKey : "k/hub" + (j - FfInnerCount) / FfInnersPerKey;

    private static string FfTag(int j) => "t" + H(32, j) % 100;

    private static string FfRare(int j) => "r" + H(33, j) % 2000;

    private void CreateFfIndexes()
    {
        if (_arm == "C")
            new FfLdIndex().Execute(_store);
        else
        {
            new FfMain().Execute(_store);
            new FfInnerIndex().Execute(_store);
            if (s_ff3Key)
                new JCompaniesK().Execute(_store);
            else
                new JCompanies().Execute(_store);
        }
    }

    private void LoadFfData()
    {
        var watch = Stopwatch.StartNew();
        _store.Maintenance.Send(new StopIndexingOperation());
        using (var bulk = _store.BulkInsert())
        {
            for (int i = 0; i < _companies; i++)
                bulk.Store(new Company { Name = "Company " + i, Country = "C" + H(4, i) % 10, Size = (int)(H(5, i) % 5000), About = Words(6, i, 60) }, $"companies/{i}");

            bulk.Store(new Company { Name = "Company -1", Country = "C" + H(4, -1) % 10, Size = (int)(H(5, -1) % 5000), About = Words(6, -1, 60) }, "companies/hub");
            if (_bigHub > 0)
            {
                bulk.Store(MakeCompany(-2, false), "companies/hubbig");
                for (int n = 0; n < FfInnersPerKey; n++)
                    bulk.Store(new FfInner { JoinKey = "k/all", Tag = FfTag(5_000_000 + n), Rare = FfRare(5_000_000 + n) }, $"ffinners/all{n}");
            }

            int inners = FfInnerCount + FfHubKeys * FfInnersPerKey;
            for (int j = 0; j < inners; j++)
                bulk.Store(new FfInner { JoinKey = FfInnerKey(j), Tag = FfTag(j), Rare = FfRare(j) }, $"ffinners/{j}");

            for (int i = 0; i < _placements; i++)
                bulk.Store(new FfOuter { JoinKey = FfOuterKey(i), Status = Statuses[H(2, i) % 10], Rank = (int)(H(3, i) % 1000), CompanyIds = FfCompanyIds(i) }, $"ffouters/{i}");
        }

        Emit("0-load", ("wallS", F(watch.Elapsed.TotalSeconds)), ("keys", FfKeys), ("innerPerKey", FfInnersPerKey), ("hubKeys", FfHubKeys));
    }

    private async Task FieldJoinsAsync(string[] phases)
    {
        CreateFfIndexes();
        LoadFfData();
        BuildIndexes();
        if (phases.Contains("memory"))
            IdleMemory(_arm == "C" ? "LoadDocument index" : "join indexes");

        if (_arm == "B")
            await BuildFfJoinsAsync();

        if (phases.Contains("query"))
            await QueryPhaseAsync();

        if (_arm == "B")
            await FinalCheckAsync();
    }

    private async Task BuildFfJoinsAsync()
    {
        var measure = new Measure();
        await new FfCompaniesJoin().ExecuteAsync(_store);
        await new FfKeyJoin().ExecuteAsync(_store);
        DateTime until = DateTime.UtcNow.AddMinutes(60);
        foreach (string name in new[] { JoinName, FfKeyJoinName })
        {
            while (true)
            {
                IndexStats stats = _store.Maintenance.Send(new GetIndexStatisticsOperation(name));
                if (stats.IsStale == false && stats.Join is { Builds: >= 1, PendingReason: null })
                    break;

                if (DateTime.UtcNow > until)
                    throw new TimeoutException("the join index was not built: " + name);

                await Task.Delay(20);
            }
        }

        Emit("3-build-join", measure.Stop(sync: true));
        JoinIndexInvariantChecker.Result result = await CheckJoinAsync(TimeSpan.FromMinutes(5));
        Emit("3-join-check", ("ok", result.Ok), ("compared", result.Compared), ("detail", OneLine(result.ToString())));
        _probePayload = Payload(FfQueries("B")[2].Rql);
        Emit("3-join-probe", ("ff3ServedByJoin", ServedProbe()), ("joinBuildMs", F(JoinBuildMs())));
    }

    private Dictionary<string, (long Total, List<int> Ranks)> _ffExpected = new();

    /// <summary>What the query must return, computed from the generator: the number of outer documents and the ranks of the first page.</summary>
    private (long Total, List<int> Ranks) FfExpected(string name)
    {
        if (_ffExpected.TryGetValue(name, out var cached))
            return cached;

        HashSet<string> keys = null;
        HashSet<int> companies = null;
        if (name is "FF1" or "FF2" or "FF4")
        {
            keys = [];
            for (int j = 0; j < FfInnerCount + FfHubKeys * FfInnersPerKey; j++)
            {
                if (name == "FF2" ? FfRare(j) == "r7" : FfTag(j) == "t7")
                    keys.Add(FfInnerKey(j));
            }
        }
        else
        {
            companies = [];
            for (int c = 0; c < _companies; c++)
            {
                if ("C" + H(4, c) % 10 == "C3")
                    companies.Add(c);
            }
        }

        string hubCountry = "C" + H(4, -1) % 10;
        long total = 0;
        var perRank = new int[1000];
        for (int i = 0; i < _placements; i++)
        {
            if (Statuses[H(2, i) % 10] != "open")
                continue;

            bool match = name == "FF4" || (keys != null ? keys.Contains(FfOuterKey(i)) : FfCompanyIds(i).Any(id => id == "companies/hub" ? hubCountry == "C3" : companies.Contains(int.Parse(id.AsSpan("companies/".Length)))));
            if (match == false)
                continue;

            total++;
            perRank[(int)(H(3, i) % 1000)]++;
        }

        var ranks = new List<int>();
        // the queries order by Rank as a string, like QR of the scenario J1
        foreach (int r in Enumerable.Range(0, perRank.Length).OrderBy(x => x.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal))
        {
            for (int k = 0; k < perRank[r] && ranks.Count < 20; k++)
                ranks.Add(r);
        }

        return _ffExpected[name] = (total, ranks);
    }

    private (string Key, object Value)[] FfOracleRecord(string name, string rql)
    {
        try
        {
            Answer answer = Ask(rql);
            var ranks = answer.Rows.Select(x =>
            {
                using JsonDocument document = JsonDocument.Parse(x);
                return document.RootElement.GetProperty("Rank").GetInt32();
            }).ToList();
            (long total, List<int> expectedRanks) = FfExpected(name);
            return [("query", name), ("same", answer.Total == total && ranks.SequenceEqual(expectedRanks)), ("total", answer.Total), ("expectedTotal", total), ("ranks", string.Join(',', ranks)), ("expectedRanks", string.Join(',', expectedRanks))];
        }
        catch (Exception e)
        {
            return [("query", name), ("same", false), ("detail", OneLine(e.Message))];
        }
    }
}
