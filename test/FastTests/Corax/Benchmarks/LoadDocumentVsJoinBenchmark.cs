using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinTestHelpers;

namespace FastTests.Corax;

// Opt-in: RAVEN_LOADDOC_VS_JOIN=<main documents> (default 200000 when set to 1), RAVEN_LOADDOC_VS_JOIN_SCENARIO=S1|S2|S2s|J1|J3|J3s, RAVEN_LOADDOC_VS_JOIN_OUT=<file>
// One scenario per test host, so that the peak memory of a scenario is its own.
// LoadDocument: ONE index copies fields of the referenced documents. Join: one index per collection, queried with an id join.
public class LoadDocumentVsJoinBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private sealed class Placement
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public string Status { get; set; }
        public int Rank { get; set; }
        public string CompanyId { get; set; }
        public string CandidateId { get; set; }
        public string OwnerId { get; set; }
    }

    private sealed class Company
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Country { get; set; }
        public int Size { get; set; }
        public string About { get; set; }
    }

    private sealed class Candidate
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Country { get; set; }
        public string CvText { get; set; }
    }

    private sealed class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Team { get; set; }
    }

    private sealed class LoadOneIndex : AbstractIndexCreationTask<Placement>
    {
        public override string IndexName => "LD1";

        public LoadOneIndex()
        {
            Map = placements => from p in placements
                let c = LoadDocument<Company>(p.CompanyId)
                select new { p.Text, p.Status, p.Rank, p.CompanyId, CompanyName = c.Name, CompanyCountry = c.Country, CompanySize = c.Size };
            Index("Text", FieldIndexing.Search);
        }
    }

    private sealed class LoadThreeIndex : AbstractIndexCreationTask<Placement>
    {
        public override string IndexName => "LD3";

        public LoadThreeIndex()
        {
            Map = placements => from p in placements
                let c = LoadDocument<Company>(p.CompanyId)
                let k = LoadDocument<Candidate>(p.CandidateId)
                let u = LoadDocument<User>(p.OwnerId)
                select new
                {
                    p.Text, p.Status, p.Rank, p.CompanyId, p.CandidateId, p.OwnerId,
                    CompanyName = c.Name, CompanyCountry = c.Country, CompanySize = c.Size,
                    CandidateName = k.Name, CandidateCountry = k.Country, CandidateCv = k.CvText,
                    UserName = u.Name, UserTeam = u.Team
                };
            Index("Text", FieldIndexing.Search);
            Index("CandidateCv", FieldIndexing.Search);
        }
    }

    private sealed class LoadThreeShortIndex : AbstractIndexCreationTask<Placement>
    {
        public override string IndexName => "LD3";

        public LoadThreeShortIndex()
        {
            Map = placements => from p in placements
                let c = LoadDocument<Company>(p.CompanyId)
                let k = LoadDocument<Candidate>(p.CandidateId)
                let u = LoadDocument<User>(p.OwnerId)
                select new
                {
                    p.Text, p.Status, p.Rank, p.CompanyId, p.CandidateId, p.OwnerId,
                    CompanyName = c.Name, CompanyCountry = c.Country, CompanySize = c.Size,
                    CandidateName = k.Name, CandidateCountry = k.Country,
                    UserName = u.Name, UserTeam = u.Team
                };
            Index("Text", FieldIndexing.Search);
        }
    }

    private sealed class MainOneKeyIndex : AbstractIndexCreationTask<Placement>
    {
        public override string IndexName => "JMain";

        public MainOneKeyIndex()
        {
            Map = placements => from p in placements select new { p.Text, p.Status, p.Rank, p.CompanyId };
            Index("Text", FieldIndexing.Search);
        }
    }

    private sealed class MainThreeKeysIndex : AbstractIndexCreationTask<Placement>
    {
        public override string IndexName => "JMain";

        public MainThreeKeysIndex()
        {
            Map = placements => from p in placements select new { p.Text, p.Status, p.Rank, p.CompanyId, p.CandidateId, p.OwnerId };
            Index("Text", FieldIndexing.Search);
        }
    }

    private sealed class CompaniesIndex : AbstractIndexCreationTask<Company>
    {
        public override string IndexName => "JCompanies";
        public CompaniesIndex() => Map = companies => from c in companies select new { c.Name, c.Country, c.Size };
    }

    private sealed class CandidatesIndex : AbstractIndexCreationTask<Candidate>
    {
        public override string IndexName => "JCandidates";

        public CandidatesIndex()
        {
            Map = candidates => from k in candidates select new { k.Name, k.Country, k.CvText };
            Index("CvText", FieldIndexing.Search);
        }
    }

    private sealed class CandidatesShortIndex : AbstractIndexCreationTask<Candidate>
    {
        public override string IndexName => "JCandidates";
        public CandidatesShortIndex() => Map = candidates => from k in candidates select new { k.Name, k.Country };
    }

    private sealed class UsersIndex : AbstractIndexCreationTask<User>
    {
        public override string IndexName => "JUsers";
        public UsersIndex() => Map = users => from u in users select new { u.Name, u.Team };
    }

    // Deterministic data: every value is a hash of (kind, index), no state
    private static uint Hash(int kind, long i)
    {
        var x = (ulong)(i + 1) * 0x9E3779B97F4A7C15UL ^ (ulong)(kind + 1) * 0xC2B2AE3D27D4EB4FUL;
        x ^= x >> 33;
        x *= 0xFF51AFD7ED558CCDUL;
        x ^= x >> 33;
        x *= 0xC4CEB9FE1A85EC53UL;
        x ^= x >> 33;
        return (uint)x;
    }

    private static string Words(int kind, long i, int count)
    {
        var sb = new StringBuilder(count * 6);
        for (var w = 0; w < count; w++)
        {
            // a skewed vocabulary of 2000 words: low numbers are common
            var h = Hash(kind * 1000 + w, i);
            var word = (int)((double)(h % 2000) * (h >> 16 & 0xFFFF) / 65535.0);
            if (w > 0)
                sb.Append(' ');
            sb.Append('w').Append(word);
        }

        return sb.ToString();
    }

    private static readonly string[] Statuses = ["open", "open", "open", "closed", "closed", "closed", "closed", "draft", "draft", "hold"];

    private sealed record Sizes(int Placements, int Companies, int Candidates, int Users)
    {
        public static Sizes For(int placements) => new(placements, Math.Max(10, placements / 10), Math.Max(10, placements / 10), Math.Max(10, placements / 100));
    }

    private static Placement MakePlacement(int i, Sizes s, int version) => new()
    {
        Id = $"placements/{i}",
        Text = Words(1, i * 10L + version, 8),
        Status = Statuses[Hash(2, i) % 10],
        Rank = (int)(Hash(3, i) % 1000),
        // 10% of the placements reference the one hub company, the rest is spread evenly
        CompanyId = i % 10 == 0 ? "companies/hub" : $"companies/{(int)(Hash(10, i) % s.Companies)}",
        CandidateId = $"candidates/{(int)(Hash(11, i) % s.Candidates)}",
        OwnerId = $"users/{(int)(Hash(12, i) % s.Users)}"
    };

    private static Company MakeCompany(string id, long i, int version) => new()
    {
        Id = id,
        Name = version == 0 ? $"Company {i}" : $"Company {i} v{version}",
        Country = $"C{Hash(4, i) % 10}",
        Size = (int)(Hash(5, i) % 5000),
        About = Words(6, i, 60)
    };

    private static Candidate MakeCandidate(int i) => new() { Id = $"candidates/{i}", Name = $"Candidate {i}", Country = $"K{Hash(7, i) % 10}", CvText = Words(8, i, 200) };

    private static User MakeUser(int i) => new() { Id = $"users/{i}", Name = $"User {i}", Team = $"T{Hash(9, i) % 5}" };

    private sealed record Scenario(string Name, bool IsJoin, int Loads, bool FullCv, string[] Indexes);

    private static Scenario Create(string name) => name switch
    {
        "S1" => new Scenario(name, false, 1, true, ["LD1"]),
        "S2" => new Scenario(name, false, 3, true, ["LD3"]),
        "S2s" => new Scenario(name, false, 3, false, ["LD3"]),
        "J1" => new Scenario(name, true, 1, true, ["JMain", "JCompanies"]),
        "J3" => new Scenario(name, true, 3, true, ["JMain", "JCompanies", "JCandidates", "JUsers"]),
        "J3s" => new Scenario(name, true, 3, false, ["JMain", "JCompanies", "JCandidates", "JUsers"]),
        _ => throw new ArgumentException($"unknown scenario {name}")
    };

    private static void Deploy(IDocumentStore store, Scenario scenario)
    {
        switch (scenario.Name)
        {
            case "S1": new LoadOneIndex().Execute(store); break;
            case "S2": new LoadThreeIndex().Execute(store); break;
            case "S2s": new LoadThreeShortIndex().Execute(store); break;
            case "J1":
                new MainOneKeyIndex().Execute(store);
                new CompaniesIndex().Execute(store);
                break;
            default:
                new MainThreeKeysIndex().Execute(store);
                new CompaniesIndex().Execute(store);
                if (scenario.FullCv)
                    new CandidatesIndex().Execute(store);
                else
                    new CandidatesShortIndex().Execute(store);
                new UsersIndex().Execute(store);
                break;
        }
    }

    private const string FromMain = "from index 'JMain' as m";
    private const string JoinCompany = "join (from index 'JCompanies' where Country = 'C3') as c on m.CompanyId = id(c)";
    private const string JoinCandidate = "join (from index 'JCandidates' where Country = 'K2') as k on m.CandidateId = id(k)";
    private const string JoinUser = "join (from index 'JUsers' where Team = 'T1') as u on m.OwnerId = id(u)";

    private static (string Name, string Rql)[] Queries(Scenario s)
    {
        const string limit = "limit 0, 20";
        var queries = new List<(string, string)>();
        if (s.Loads == 1)
        {
            var join = $"{FromMain} {JoinCompany}";
            queries.Add(("Q1 open+country, score()", s.IsJoin ? $"{join} where m.Status = 'open' order by score() {limit}" : $"from index 'LD1' where Status = 'open' and CompanyCountry = 'C3' order by score() {limit}"));
            queries.Add(("Q2 open+country, order by Rank", s.IsJoin ? $"{join} where m.Status = 'open' order by m.Rank {limit}" : $"from index 'LD1' where Status = 'open' and CompanyCountry = 'C3' order by Rank {limit}"));
            queries.Add(("Q3 most rows, order by company name", s.IsJoin ? $"{FromMain} join (from index 'JCompanies') as c on m.CompanyId = id(c) where exists(m.Status) order by c.Name {limit}" : $"from index 'LD1' where exists(Status) order by CompanyName {limit}"));
            return queries.ToArray();
        }

        var joins = $"{FromMain} {JoinCompany} {JoinCandidate} {JoinUser}";
        const string ldWhere = "Status = 'open' and CompanyCountry = 'C3' and CandidateCountry = 'K2' and UserTeam = 'T1'";
        var index = "'LD3'";
        queries.Add(("Q1 open+3 conditions, score()", s.IsJoin ? $"{joins} where m.Status = 'open' order by score() {limit}" : $"from index {index} where {ldWhere} order by score() {limit}"));
        queries.Add(("Q2 open+3 conditions, order by Rank", s.IsJoin ? $"{joins} where m.Status = 'open' order by m.Rank {limit}" : $"from index {index} where {ldWhere} order by Rank {limit}"));
        queries.Add(("Q3 most rows, order by company name", s.IsJoin ? $"{FromMain} join (from index 'JCompanies') as c on m.CompanyId = id(c) where exists(m.Status) order by c.Name {limit}" : $"from index {index} where exists(Status) order by CompanyName {limit}"));
        if (s.FullCv)
            queries.Add(("Q4 open+search in candidate CV, score()", s.IsJoin
                ? $"{FromMain} join (from index 'JCandidates' where search(CvText, 'w10 w20 w30')) as k on m.CandidateId = id(k) where m.Status = 'open' order by score() {limit}"
                : $"from index {index} where Status = 'open' and search(CandidateCv, 'w10 w20 w30') order by score() {limit}"));
        return queries.ToArray();
    }

    // peak physical footprint per phase, sampled with the same tool that the capped wrapper uses
    internal sealed class FootprintSampler : IDisposable
    {
        private static readonly Regex Pattern = new(@"Footprint:\s+([\d.]+)\s+(KB|MB|GB|B)\b", RegexOptions.Compiled);
        private readonly Thread _thread;
        private volatile bool _stop;
        private long _peakKb;

        public FootprintSampler()
        {
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        public long TakePeakKb() => Interlocked.Exchange(ref _peakKb, 0);

        private void Loop()
        {
            var pid = Environment.ProcessId;
            while (_stop == false)
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo("footprint", $"-p {pid}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                    var text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    var m = Pattern.Match(text);
                    if (m.Success)
                    {
                        var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        var kb = (long)(m.Groups[2].Value switch { "GB" => value * 1048576, "MB" => value * 1024, "B" => value / 1024, _ => value });
                        long current;
                        while (kb > (current = Interlocked.Read(ref _peakKb)))
                            Interlocked.CompareExchange(ref _peakKb, kb, current);
                    }
                }
                catch
                {
                    return;
                }

                Thread.Sleep(200);
            }
        }

        public void Dispose() => _stop = true;
    }

    internal static string Command(string file, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, UseShellExecute = false });
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return text.Trim();
        }
        catch
        {
            return "";
        }
    }

    internal static string Load1() => Command("sysctl", "-n vm.loadavg").Trim('{', '}', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    internal static long DirectoryKb(string path) => Directory.Exists(path) ? long.Parse(Command("du", $"-sk \"{path}\"").Split('\t', ' ')[0]) : 0;

    private sealed record Counts(long MapAttempts, long MapSuccesses, long ReferenceAttempts, long ReferenceSuccesses, long Entries);

    [JoinBenchmarkFact("RAVEN_LOADDOC_VS_JOIN", RavenTestCategory.Corax | RavenTestCategory.Indexes | RavenTestCategory.Querying)]
    public void Run()
    {
        var requested = int.Parse(Environment.GetEnvironmentVariable("RAVEN_LOADDOC_VS_JOIN"));
        var placements = requested <= 1 ? 200_000 : requested;
        var scenario = Create(Environment.GetEnvironmentVariable("RAVEN_LOADDOC_VS_JOIN_SCENARIO") ?? "S1");
        var reps = int.Parse(Environment.GetEnvironmentVariable("RAVEN_LOADDOC_VS_JOIN_REPS") ?? "20");
        var runId = Environment.GetEnvironmentVariable("RAVEN_LOADDOC_VS_JOIN_RUN") ?? "1";
        var sizes = Sizes.For(placements);

        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        using var store = GetDocumentStore(options);
        using var sampler = new FootprintSampler();
        var process = Process.GetCurrentProcess();
        var report = new StringBuilder();

        void Emit(string phase, params (string Key, object Value)[] values)
        {
            var line = $"rec|{scenario.Name}|{placements}|{runId}|{phase}|" + string.Join(';', values.Select(x => FormattableString.Invariant($"{x.Key}={x.Value}")));
            report.AppendLine(line);
            output.WriteLine(line);
        }

        Counts Take(string index)
        {
            var stats = store.Maintenance.Send(new GetIndexesStatisticsOperation()).FirstOrDefault(x => x.Name == index);
            if (stats == null)
                return new Counts(0, 0, 0, 0, 0);

            return new Counts(stats.MapAttempts, stats.MapSuccesses, stats.MapReferenceAttempts ?? 0, stats.MapReferenceSuccesses ?? 0, stats.EntriesCount);
        }

        void WaitNonStale()
        {
            var deadline = DateTime.UtcNow.AddMinutes(40);
            while (DateTime.UtcNow < deadline)
            {
                var stats = store.Maintenance.Send(new GetIndexesStatisticsOperation());
                if (scenario.Indexes.All(name => stats.Any(s => s.Name == name && s.IsStale == false)))
                    return;
                Thread.Sleep(50);
            }

            throw new TimeoutException("the indexes are still stale");
        }

        // runs the work and reports wall time, process CPU, managed allocation, the peak footprint of the phase, and the index work counters
        void Measure(string phase, Action prepare, Action work, Action<List<(string, object)>> after = null)
        {
            prepare?.Invoke();
            var before = scenario.Indexes.ToDictionary(x => x, Take);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            sampler.TakePeakKb();
            var load = Load1();
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var cpu = process.TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            work();
            WaitNonStale();
            watch.Stop();
            var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            var allocatedMb = (GC.GetTotalAllocatedBytes(precise: true) - allocated) / 1024.0 / 1024.0;
            process.Refresh();
            var values = new List<(string, object)>
            {
                ("wallMs", watch.ElapsedMilliseconds),
                ("cpuMs", (long)cpuMs),
                ("allocMB", allocatedMb.ToString("F0", CultureInfo.InvariantCulture)),
                ("peakFootprintMB", sampler.TakePeakKb() / 1024),
                ("load1Start", load),
                ("load1End", Load1())
            };
            long attempts = 0, successes = 0, refAttempts = 0;
            foreach (var index in scenario.Indexes)
            {
                var now = Take(index);
                values.Add(($"{index}.mapAttempts", now.MapAttempts - before[index].MapAttempts));
                values.Add(($"{index}.refAttempts", now.ReferenceAttempts - before[index].ReferenceAttempts));
                attempts += now.MapAttempts - before[index].MapAttempts;
                successes += now.MapSuccesses - before[index].MapSuccesses;
                refAttempts += now.ReferenceAttempts - before[index].ReferenceAttempts;
            }

            values.Add(("mapAttemptsTotal", attempts));
            values.Add(("refAttemptsTotal", refAttempts));
            after?.Invoke(values);
            Emit(phase, values.Select(x => (x.Item1, x.Item2)).ToArray());
        }

        // data first, with no index: the index build is then measured on its own
        var generated = Stopwatch.StartNew();
        InsertData(store, sizes);

        output.WriteLine($"data inserted in {generated.Elapsed.TotalSeconds:F0}s");
        var dataDirectory = store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database)).Settings[RavenConfiguration.GetKey(x => x.Core.DataDirectory)];

        // (1) initial indexing
        Measure("1-initial", null, () => Deploy(store, scenario), values =>
        {
            var folders = Directory.Exists(Path.Combine(dataDirectory, "Indexes")) ? Directory.GetDirectories(Path.Combine(dataDirectory, "Indexes")) : [];
            long total = 0;
            foreach (var folder in folders)
            {
                var kb = DirectoryKb(folder);
                total += kb;
                values.Add(($"indexKB.{Path.GetFileName(folder)}", kb));
            }

            values.Add(("indexKB.total", total));
            values.Add(("databaseKB", DirectoryKb(dataDirectory)));
            foreach (var index in scenario.Indexes)
                values.Add(($"{index}.entries", Take(index).Entries));
        });

        // (4) queries, while the data is pristine
        foreach (var (name, rql) in Queries(scenario))
        {
            for (var i = 0; i < 4; i++)
                ExecuteQuery(rql, out _, out _, out _, out _, out _);

            var server = new List<double>();
            var managed = new List<double>();
            var held = new List<double>();
            var scan = new List<double>();
            long total = 0;
            var load = Load1();
            for (var i = 0; i < reps; i++)
            {
                total = ExecuteQuery(rql, out var ms, out var kb, out var heldBytes, out var scanBytes, out _);
                server.Add(ms);
                managed.Add(kb);
                held.Add(heldBytes);
                scan.Add(scanBytes);
            }

            Emit("4-query", ("query", name.Replace(';', ',')), ("rows", total), ("serverMsMedian", Median(server).ToString("F1", CultureInfo.InvariantCulture)),
                ("serverMsMin", server.Min().ToString("F1", CultureInfo.InvariantCulture)), ("managedKBMedian", Median(managed).ToString("F0", CultureInfo.InvariantCulture)),
                ("heldUnmanagedKBMedian", (Median(held) / 1024).ToString("F0", CultureInfo.InvariantCulture)), ("scanHeldUnmanagedKBMedian", (Median(scan) / 1024).ToString("F0", CultureInfo.InvariantCulture)),
                ("load1", load));
        }

        long ExecuteQuery(string rql, out double serverMs, out double managedKb, out double heldBytes, out double scanBytes, out QueryTimings timings)
        {
            using var session = store.OpenSession();
            var result = session.Advanced.RawQuery<Placement>(rql + " include timings()").NoCaching().Statistics(out var stats).Timings(out timings).ToList();
            _ = result.Count;
            serverMs = stats.DurationInMs;
            var counters = new Dictionary<string, long>();
            Collect(timings, counters);
            managedKb = (counters.TryGetValue("TotalAllocatedManagedBytes", out var total) ? total : counters.GetValueOrDefault("AllocatedManagedBytes")) / 1024.0;
            heldBytes = counters.GetValueOrDefault("HeldUnmanagedBytesPeak");
            scanBytes = counters.GetValueOrDefault("ScanHeldUnmanagedBytes");
            return stats.TotalResults;
        }

        // (2a) a change of 1% of the Companies (the field that the LoadDocument indexes copy)
        var step = 100;
        Measure("2a-update-1pct-companies", () =>
        {
            store.Maintenance.Send(new StopIndexingOperation());
            using (var bulk = store.BulkInsert())
                for (var i = 0; i < sizes.Companies; i += step)
                    bulk.Store(MakeCompany($"companies/{i}", i, 1), $"companies/{i}");
        }, () => ResumeIndexing(store), values => values.Add(("changedDocuments", (sizes.Companies + step - 1) / step)));

        // (2b) the skewed case: ONE company that a tenth of all placements reference
        Measure("2b-update-hub-company", () =>
        {
            store.Maintenance.Send(new StopIndexingOperation());
            using (var bulk = store.BulkInsert())
                bulk.Store(MakeCompany("companies/hub", -1, 1), "companies/hub");
        }, () => ResumeIndexing(store), values => values.Add(("referencingPlacements", Enumerable.Range(0, sizes.Placements).Count(i => i % 10 == 0))));

        // (3) a change of 1% of the main documents
        Measure("3-update-1pct-main", () =>
        {
            store.Maintenance.Send(new StopIndexingOperation());
            using (var bulk = store.BulkInsert())
                for (var i = 0; i < sizes.Placements; i += step)
                    bulk.Store(MakePlacement(i, sizes, 1), $"placements/{i}");
        }, () => ResumeIndexing(store), values => values.Add(("changedDocuments", (sizes.Placements + step - 1) / step)));

        process.Refresh();
        Emit("5-end", ("version", "v7.2 574da8d6ea"), ("machine", Environment.MachineName), ("cores", Environment.ProcessorCount));
        AppendReport("RAVEN_LOADDOC_VS_JOIN_OUT", "loaddoc-vs-join.txt", report);
    }



    [DllImport("libproc", EntryPoint = "proc_pid_rusage")]
    private static extern int ProcPidRusage(int pid, int flavor, byte[] buffer);

    // disk bytes read and written by this process since it started (rusage_info_v4: ri_diskio_bytesread at 144, ri_diskio_byteswritten at 152)
    internal static (long Read, long Written) DiskIo()
    {
        if (OperatingSystem.IsMacOS() == false)
            return (-1, -1);

        var buffer = new byte[512];
        return ProcPidRusage(Environment.ProcessId, 4, buffer) == 0 ? ((long)BitConverter.ToUInt64(buffer, 144), (long)BitConverter.ToUInt64(buffer, 152)) : (-1, -1);
    }

    internal static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture);

    private static void InsertData(IDocumentStore store, Sizes sizes)
    {
        using var bulk = store.BulkInsert();
        for (var i = 0; i < sizes.Companies; i++)
            bulk.Store(MakeCompany($"companies/{i}", i, 0), $"companies/{i}");
        bulk.Store(MakeCompany("companies/hub", -1, 0), "companies/hub");
        for (var i = 0; i < sizes.Candidates; i++)
            bulk.Store(MakeCandidate(i), $"candidates/{i}");
        for (var i = 0; i < sizes.Users; i++)
            bulk.Store(MakeUser(i), $"users/{i}");
        for (var i = 0; i < sizes.Placements; i++)
            bulk.Store(MakePlacement(i, sizes, 0), $"placements/{i}");
    }

    private static long FootprintKb()
    {
        var m = Regex.Match(Command("footprint", $"-p {Environment.ProcessId}"), @"Footprint:\s+([\d.]+)\s+(KB|MB|GB|B)\b");
        if (m.Success == false)
            return 0;
        var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return (long)(m.Groups[2].Value switch { "GB" => value * 1048576, "MB" => value * 1024, "B" => value / 1024, _ => value });
    }

    // peak of the native (unmanaged) allocator counter of the in-process server, sampled every 5 ms
    internal sealed class NativeSampler : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        private long _peak;

        public NativeSampler()
        {
            Reset();
            _thread = new Thread(() =>
            {
                while (_stop == false)
                {
                    var current = global::Sparrow.Utils.NativeMemory.TotalAllocatedMemory;
                    long seen;
                    while (current > (seen = Interlocked.Read(ref _peak)))
                        Interlocked.CompareExchange(ref _peak, current, seen);
                    Thread.Sleep(5);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Reset() => Interlocked.Exchange(ref _peak, global::Sparrow.Utils.NativeMemory.TotalAllocatedMemory);
        public long Peak => Interlocked.Read(ref _peak);
        public void Dispose() => _stop = true;
    }

    // Second pass: resident memory, native memory while indexing, CPU per query, concurrency
    [JoinBenchmarkFact("RAVEN_LOADDOC_VS_JOIN_EXTRA", RavenTestCategory.Corax | RavenTestCategory.Indexes | RavenTestCategory.Querying)]
    public void RunExtra()
    {
        var requested = int.Parse(Environment.GetEnvironmentVariable("RAVEN_LOADDOC_VS_JOIN_EXTRA"));
        var placements = requested <= 1 ? 200_000 : requested;
        var scenario = Create(Environment.GetEnvironmentVariable("RAVEN_LOADDOC_VS_JOIN_SCENARIO") ?? "S1");
        var runId = Environment.GetEnvironmentVariable("RAVEN_LOADDOC_VS_JOIN_RUN") ?? "1";
        var seconds = int.Parse(Environment.GetEnvironmentVariable("RAVEN_LOADDOC_VS_JOIN_SECONDS") ?? "20");
        var sizes = Sizes.For(placements);

        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        using var store = GetDocumentStore(options);
        using var sampler = new FootprintSampler();
        using var native = new NativeSampler();
        var process = Process.GetCurrentProcess();
        var report = new StringBuilder();

        void Emit(string phase, params (string Key, object Value)[] values)
        {
            var line = $"rec|{scenario.Name}|{placements}|{runId}|{phase}|" + string.Join(';', values.Select(x => FormattableString.Invariant($"{x.Key}={x.Value}")));
            report.AppendLine(line);
            output.WriteLine(line);
        }

        void WaitNonStale()
        {
            var deadline = DateTime.UtcNow.AddMinutes(40);
            while (DateTime.UtcNow < deadline)
            {
                var stats = store.Maintenance.Send(new GetIndexesStatisticsOperation());
                if (scenario.Indexes.All(name => stats.Any(s => s.Name == name && s.IsStale == false)))
                    return;
                Thread.Sleep(50);
            }

            throw new TimeoutException("the indexes are still stale");
        }

        void Settle()
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Thread.Sleep(2000);
        }

        InsertData(store, sizes);
        Settle();
        var baselineKb = FootprintKb();
        var nativeBaseline = global::Sparrow.Utils.NativeMemory.TotalAllocatedMemory;

        // x1: native memory while the indexes are built
        native.Reset();
        sampler.TakePeakKb();
        var started = DateTime.UtcNow;
        var io0 = DiskIo();
        var indexingWatch = Stopwatch.StartNew();
        Deploy(store, scenario);
        WaitNonStale();
        indexingWatch.Stop();
        var io1 = DiskIo();
        var performance = store.Maintenance.Send(new GetIndexPerformanceStatisticsOperation(scenario.Indexes)).SelectMany(x => x.Performance).Where(x => x.Started >= started.AddSeconds(-5)).ToList();
        var stats = store.Maintenance.Send(new GetIndexesStatisticsOperation()).Where(x => scenario.Indexes.Contains(x.Name)).ToList();
        var attempts = stats.Sum(x => (long)x.MapAttempts);
        Emit("x1-indexing",
            ("baselineFootprintMB", baselineKb / 1024),
            ("peakFootprintMB", sampler.TakePeakKb() / 1024),
            ("nativePeakMB", (native.Peak - nativeBaseline) / 1024 / 1024),
            ("batchUnmanagedMaxMB", performance.Max(x => x.AllocatedUnmanagedBytes?.SizeInBytes ?? 0) / 1024 / 1024),
            ("batches", performance.Count),
            ("docsRead", performance.Sum(x => x.InputCount)),
            ("entriesWritten", performance.Sum(x => x.OutputCount)),
            ("indexEntries", stats.Sum(x => x.EntriesCount)),
            ("documentsSizeMB", performance.Sum(x => x.DocumentsSize?.SizeInBytes ?? 0) / 1024 / 1024),
            ("mapAttempts", attempts),
            ("loadDocumentCalls", attempts * (scenario.IsJoin ? 0 : scenario.Loads)),
            ("wallMs", indexingWatch.ElapsedMilliseconds), ("readMB", Mb(io1.Read - io0.Read)), ("writtenMB", Mb(io1.Written - io0.Written)),
            ("readMBps", Mb((long)((io1.Read - io0.Read) / indexingWatch.Elapsed.TotalSeconds))), ("writtenMBps", Mb((long)((io1.Written - io0.Written) / indexingWatch.Elapsed.TotalSeconds))));
        var dataDirectory = store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database)).Settings[RavenConfiguration.GetKey(x => x.Core.DataDirectory)];
        var indexesDirectory = Path.Combine(dataDirectory, "Indexes");
        Emit("x1-sizes", ("indexKB", DirectoryKb(indexesDirectory)), ("databaseKB", DirectoryKb(dataDirectory)));

        // x2: what it costs to keep the indexes loaded
        Settle();
        var residentKb = FootprintKb();
        Emit("x2-resident-after-indexing", ("footprintMB", residentKb / 1024), ("deltaVsNoIndexMB", (residentKb - baselineKb) / 1024),
            ("nativeHeldMB", (global::Sparrow.Utils.NativeMemory.TotalAllocatedMemory - nativeBaseline) / 1024 / 1024), ("managedHeapMB", GC.GetTotalMemory(false) / 1024 / 1024));

        var queries = Queries(scenario);

        // x3: CPU per query, 200 sequential queries of every shape
        foreach (var (name, rql) in queries)
        {
            for (var i = 0; i < 5; i++)
                ExecuteOne(rql);
            GC.Collect();
            var load = Load1();
            var cpu = process.TotalProcessorTime;
            var ioQ0 = DiskIo();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 200; i++)
                ExecuteOne(rql);
            watch.Stop();
            var ioQ1 = DiskIo();
            Emit("x3-cpu-per-query", ("query", name.Replace(';', ',')), ("readMB", Mb(ioQ1.Read - ioQ0.Read)), ("writtenMB", Mb(ioQ1.Written - ioQ0.Written)), ("cpuMsPerQuery", ((process.TotalProcessorTime - cpu).TotalMilliseconds / 200).ToString("F2", CultureInfo.InvariantCulture)),
                ("wallMsPerQuery", (watch.Elapsed.TotalMilliseconds / 200).ToString("F2", CultureInfo.InvariantCulture)), ("load1", load));
        }

        Settle();
        var afterQueriesKb = FootprintKb();
        Emit("x2-resident-after-queries", ("footprintMB", afterQueriesKb / 1024), ("deltaVsNoIndexMB", (afterQueriesKb - baselineKb) / 1024),
            ("nativeHeldMB", (global::Sparrow.Utils.NativeMemory.TotalAllocatedMemory - nativeBaseline) / 1024 / 1024));

        // x4: concurrency, the first (selective) and the third (most rows) query shape
        foreach (var shape in new[] { 0, 2 })
        {
            var (name, rql) = queries[shape];
            foreach (var clients in new[] { 1, 4, 16 })
            {
                Settle();
                var latencies = new List<double>[clients];
                var stop = false;
                native.Reset();
                sampler.TakePeakKb();
                var load = Load1();
                var allocated = GC.GetTotalAllocatedBytes(precise: true);
                var cpu = process.TotalProcessorTime;
                var ioC0 = DiskIo();
                var watch = Stopwatch.StartNew();
                var tasks = Enumerable.Range(0, clients).Select(c => System.Threading.Tasks.Task.Run(() =>
                {
                    var list = latencies[c] = new List<double>();
                    while (Volatile.Read(ref stop) == false)
                    {
                        var t = Stopwatch.StartNew();
                        ExecuteOne(rql);
                        list.Add(t.Elapsed.TotalMilliseconds);
                    }
                })).ToArray();
                Thread.Sleep(seconds * 1000);
                Volatile.Write(ref stop, true);
                System.Threading.Tasks.Task.WaitAll(tasks);
                watch.Stop();
                var all = latencies.SelectMany(x => x).ToList();
                var ioC1 = DiskIo();
                Emit("x4-concurrency", ("query", name.Replace(';', ',')), ("clients", clients), ("readMB", Mb(ioC1.Read - ioC0.Read)), ("writtenMB", Mb(ioC1.Written - ioC0.Written)),
                    ("qps", (all.Count / watch.Elapsed.TotalSeconds).ToString("F1", CultureInfo.InvariantCulture)),
                    ("p50ms", Percentile(all, 0.5).ToString("F1", CultureInfo.InvariantCulture)), ("p99ms", Percentile(all, 0.99).ToString("F1", CultureInfo.InvariantCulture)),
                    ("cpuMsPerQuery", ((process.TotalProcessorTime - cpu).TotalMilliseconds / all.Count).ToString("F2", CultureInfo.InvariantCulture)),
                    ("cpuCores", ((process.TotalProcessorTime - cpu).TotalSeconds / watch.Elapsed.TotalSeconds).ToString("F2", CultureInfo.InvariantCulture)),
                    ("allocKBPerQuery", ((GC.GetTotalAllocatedBytes(precise: true) - allocated) / 1024 / all.Count).ToString(CultureInfo.InvariantCulture)),
                    ("peakFootprintMB", sampler.TakePeakKb() / 1024), ("nativePeakDeltaMB", (native.Peak - nativeBaseline) / 1024 / 1024),
                    ("load1", load));
            }
        }


        // x6: updates, with the disk I/O of writing the changed documents and of the re-indexing, and the write amplification
        void Update(string phase, Func<(long Changed, long Bytes)> write)
        {
            Settle();
            var dirKb0 = DirectoryKb(indexesDirectory);
            store.Maintenance.Send(new StopIndexingOperation());
            var w0 = DiskIo();
            var written = write();
            var w1 = DiskIo();
            var before = stats.ToDictionary(x => x.Name, x => store.Maintenance.Send(new GetIndexesStatisticsOperation()).First(y => y.Name == x.Name));
            var load = Load1();
            var nativeBefore = global::Sparrow.Utils.NativeMemory.TotalAllocatedMemory;
            native.Reset();
            sampler.TakePeakKb();
            var cpu = process.TotalProcessorTime;
            var i0 = DiskIo();
            var watch = Stopwatch.StartNew();
            store.Maintenance.Send(new StartIndexingOperation());
            WaitNonStale();
            watch.Stop();
            var i1 = DiskIo();
            var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            var now = store.Maintenance.Send(new GetIndexesStatisticsOperation()).Where(x => scenario.Indexes.Contains(x.Name)).ToDictionary(x => x.Name);
            long mapAttempts = 0, refAttempts = 0;
            foreach (var index in scenario.Indexes)
            {
                mapAttempts += now[index].MapAttempts - before[index].MapAttempts;
                refAttempts += (now[index].MapReferenceAttempts ?? 0) - (before[index].MapReferenceAttempts ?? 0);
            }

            var indexWritten = i1.Written - i0.Written;
            Emit(phase, ("changedDocuments", written.Changed), ("changedBytes", written.Bytes), ("docWriteMB", Mb(w1.Written - w0.Written)), ("docWriteRead", Mb(w1.Read - w0.Read)),
                ("reindexWallMs", watch.ElapsedMilliseconds), ("reindexCpuMs", (long)cpuMs), ("reindexReadMB", Mb(i1.Read - i0.Read)), ("reindexWrittenMB", Mb(indexWritten)),
                ("reindexWrittenMBps", Mb((long)(indexWritten / Math.Max(0.05, watch.Elapsed.TotalSeconds)))), ("reindexReadMBps", Mb((long)((i1.Read - i0.Read) / Math.Max(0.05, watch.Elapsed.TotalSeconds)))),
                ("writeAmplification", ((w1.Written - w0.Written + indexWritten) / (double)Math.Max(1, written.Bytes)).ToString("F1", CultureInfo.InvariantCulture)),
                ("indexWriteAmplification", (indexWritten / (double)Math.Max(1, written.Bytes)).ToString("F1", CultureInfo.InvariantCulture)),
                ("indexDirDeltaKB", DirectoryKb(indexesDirectory) - dirKb0), ("mapAttempts", mapAttempts), ("refAttempts", refAttempts),
                ("nativePeakDeltaMB", (native.Peak - nativeBefore) / 1024 / 1024), ("peakFootprintMB", sampler.TakePeakKb() / 1024), ("load1", load));
        }

        const int step = 100;
        Update("x6-update-1pct-companies", () =>
        {
            long bytes = 0, count = 0;
            using var bulk = store.BulkInsert();
            for (var i = 0; i < sizes.Companies; i += step)
            {
                var company = MakeCompany($"companies/{i}", i, 1);
                bytes += JsonSerializer.SerializeToUtf8Bytes(company).Length;
                count++;
                bulk.Store(company, $"companies/{i}");
            }

            return (count, bytes);
        });
        Update("x6-update-hub-company", () =>
        {
            var company = MakeCompany("companies/hub", -1, 1);
            using var bulk = store.BulkInsert();
            bulk.Store(company, "companies/hub");
            return (1, JsonSerializer.SerializeToUtf8Bytes(company).Length);
        });
        Update("x6-update-1pct-main", () =>
        {
            long bytes = 0, count = 0;
            using var bulk = store.BulkInsert();
            for (var i = 0; i < sizes.Placements; i += step)
            {
                var placement = MakePlacement(i, sizes, 1);
                bytes += JsonSerializer.SerializeToUtf8Bytes(placement).Length;
                count++;
                bulk.Store(placement, $"placements/{i}");
            }

            return (count, bytes);
        });

        Emit("x5-end", ("version", "v7.2 574da8d6ea"));
        AppendReport("RAVEN_LOADDOC_VS_JOIN_OUT", "loaddoc-vs-join-extra.txt", report);

        void ExecuteOne(string rql)
        {
            using var session = store.OpenSession();
            _ = session.Advanced.RawQuery<Placement>(rql).NoCaching().ToList().Count;
        }
    }

    private static void ResumeIndexing(IDocumentStore store) => store.Maintenance.Send(new StartIndexingOperation());

    private static void Collect(QueryTimings timings, Dictionary<string, long> counters)
    {
        if (timings.Counters != null)
            foreach (var c in timings.Counters)
                if (c.Key is "AllocatedManagedBytes" or "TotalAllocatedManagedBytes" or "HeldUnmanagedBytesPeak" or "ScanHeldUnmanagedBytes")
                    counters[c.Key] = Math.Max(counters.GetValueOrDefault(c.Key), Convert.ToInt64(c.Value));

        if (timings.Timings != null)
            foreach (var child in timings.Timings.Values)
                Collect(child, counters);
    }
}
