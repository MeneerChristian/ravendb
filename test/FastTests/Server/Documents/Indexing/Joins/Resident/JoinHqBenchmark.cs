using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

// Harness only, not for merge. RAVEN_JOIN_HQ=<arm>,<outer>,<contexts>,<hub>,<updates>,<reps>,<queries per rep>  arm = Expanded | Indirect
public class JoinHqBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Join = "HqIdJoin";

    [DllImport("libc", EntryPoint = "proc_pid_rusage")]
    private static extern int proc_pid_rusage(int pid, int flavor, byte[] buffer);

    [DllImport("libc", EntryPoint = "getrusage")]
    private static extern int getrusage(int who, byte[] buffer);

    private static (long Instructions, long Cycles) Cpu()
    {
        var buffer = new byte[1024];
        if (proc_pid_rusage(Environment.ProcessId, 6, buffer) != 0 && proc_pid_rusage(Environment.ProcessId, 5, buffer) != 0)
            return (-1, -1);

        return (BitConverter.ToInt64(buffer, 16 + 29 * 8), BitConverter.ToInt64(buffer, 16 + 30 * 8));
    }

    private static (long Minor, long Major) Faults()
    {
        var buffer = new byte[256];
        getrusage(0, buffer);
        return (BitConverter.ToInt64(buffer, 64), BitConverter.ToInt64(buffer, 72));
    }

    private void Report(string line)
    {
        Output.WriteLine(line);
        string file = Environment.GetEnvironmentVariable("RAVEN_JOIN_DIAG_FILE");
        if (string.IsNullOrEmpty(file) == false)
            File.AppendAllText(file, line + Environment.NewLine);
    }

    private static long Hash(int salt, long i)
    {
        ulong z = (ulong)i + 0x9E3779B97F4A7C15UL * (ulong)(salt + 1);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return (long)((z ^ (z >> 31)) & 0x7fffffffUL);
    }

    private static async Task WaitQuietAsync(JoinIndexHarness h)
    {
        DateTime until = DateTime.UtcNow.AddMinutes(30);
        long last = -1;
        int stable = 0;
        while (DateTime.UtcNow < until)
        {
            var stats = h.Store.Maintenance.Send(new GetIndexesStatisticsOperation());
            JoinIndex j = h.Join(Join);
            long seen = j == null ? -1 : j.Counters.Refreshes * 1000003 + j.Counters.NoOps * 1009 + j.Counters.Builds;
            if (stats.All(x => x.IsStale == false) && seen == last)
            {
                if (++stable >= 40)
                    return;
            }
            else
            {
                stable = 0;
                last = seen;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("the indexes stayed busy");
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task HubQueryCost()
    {
        string[] parts = (Environment.GetEnvironmentVariable("RAVEN_JOIN_HQ") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 7)
            return;

        string arm = parts[0];
        int outer = int.Parse(parts[1]);
        int contexts = int.Parse(parts[2]);
        int hub = int.Parse(parts[3]);
        int updates = int.Parse(parts[4]);
        int reps = int.Parse(parts[5]);
        int queries = int.Parse(parts[6]);
        string shape = $"{arm}: outer {outer}, contexts {contexts}, hub {hub}, updates {updates}";

        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.IndirectEnabled)] = "true" };
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, settings, putJoin: false, runInMemory: false, indexes: [JoinIndexHarness.MultiOuterIndex(), JoinIndexHarness.InnerIndex()]);

        JoinIndexHarness.Context Inner(int i, string country) => new() { Country = country, Name = "r" + Hash(33, i) % 200, Rank = 0, IsActive = true, Title = "x" };
        string KeyOf(long i) => i < hub ? "contexts/0" : "contexts/" + (1 + Hash(5, i) % (contexts - 1));

        await using (var bulk = h.Store.BulkInsert())
        {
            await bulk.StoreAsync(Inner(0, "hub0"), "contexts/0");
            for (int i = 1; i < contexts; i++)
                await bulk.StoreAsync(Inner(i, "t" + Hash(32, i) % 100), $"contexts/{i}");

            for (int i = 0; i < outer; i++)
                await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextIds = [KeyOf(i)], Title = "t" }, $"conversations/{i}");
        }

        await WaitQuietAsync(h);
        var definition = new IndexDefinition
        {
            Name = Join,
            Join = new JoinDefinition
            {
                OuterIndex = JoinIndexHarness.OuterName, OuterField = "ContextId", InnerIndex = JoinIndexHarness.InnerName,
                StorageMode = arm == "Expanded" ? JoinStorageMode.Expanded : JoinStorageMode.Indirect
            }.AddField("Country").AddField("Name").AddGrouping("IsActive", "true")
        };
        await h.PutJoinAsync(definition);
        await WaitQuietAsync(h);

        if (updates > 0)
        {
            await using var bulk = h.Store.BulkInsert();
            for (int i = 0; i < updates; i++)
                await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextIds = [KeyOf(i)], Title = "u" }, $"conversations/{i}");
        }

        await WaitQuietAsync(h);
        var js = h.Store.Maintenance.Send(new GetIndexStatisticsOperation(Join)).Join;
        Report($"[{shape}] ready; StorageMode={js?.StorageMode} Resolved={js?.ResolvedStorageMode}");

        var oracle = new JoinIndexQueryOracle(h, Join);
        string JoinRql(string country) =>
            $"from index '{JoinIndexHarness.OuterName}' as m join (from index '{JoinIndexHarness.InnerName}' where Country = '{country}') as c on m.ContextId = id(c) limit 0, 20";

        var cells = new List<(string Name, string Country)> { ("pct1", "t5") };
        if (hub > 0)
            cells.Add(("hub", "hub0"));

        foreach ((string name, string country) in cells)
        {
            string rql = JoinRql(country);
            long total = 0;
            for (int i = 0; i < 2000; i++)
                total = oracle.Run(rql).Total;

            await Task.Delay(1000);
            var idle0 = Cpu();
            long idleT = Stopwatch.GetTimestamp();
            await Task.Delay(2000);
            var idle1 = Cpu();
            double idleSeconds = (Stopwatch.GetTimestamp() - idleT) / (double)Stopwatch.Frequency;
            double idleInstrPerUs = (idle1.Instructions - idle0.Instructions) / idleSeconds / 1e6;
            Report($"[{shape}] idle {name}: instrPerUs={idleInstrPerUs:F1}");

            for (int rep = 0; rep <= reps; rep++)
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                var resident = h.Join(Join).Resident;
                var stats0 = resident?.Cache.GetStats();
                long served0 = oracle.Info.Served;
                long alloc0 = GC.GetTotalAllocatedBytes(true);
                var cpu0 = Cpu();
                var faults0 = Faults();
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < queries; i++)
                    total = oracle.Run(rql).Total;

                long ticks = Stopwatch.GetTimestamp() - t0;
                var cpu1 = Cpu();
                var faults1 = Faults();
                long alloc1 = GC.GetTotalAllocatedBytes(true);
                var stats1 = resident?.Cache.GetStats();
                Report($"[{shape}] Q {name} rep={rep} warm={rep == 0} n={queries} total={total} served={oracle.Info.Served - served0} " +
                       $"wallUs={ticks * 1e6 / Stopwatch.Frequency / queries:F2} instrRaw={(double)(cpu1.Instructions - cpu0.Instructions) / queries:F0} instr={((double)(cpu1.Instructions - cpu0.Instructions) - idleInstrPerUs * ticks * 1e6 / Stopwatch.Frequency) / queries:F0} cycles={(double)(cpu1.Cycles - cpu0.Cycles) / queries:F0} " +
                       $"allocB={(double)(alloc1 - alloc0) / queries:F0} minflt={(double)(faults1.Minor - faults0.Minor) / queries:F3} majflt={(faults1.Major - faults0.Major)} " +
                       $"hits={(double)((stats1?.Hits ?? 0) - (stats0?.Hits ?? 0)) / queries:F1} misses={(double)((stats1?.Misses ?? 0) - (stats0?.Misses ?? 0)) / queries:F1} resKeys={stats1?.ResidentKeys}");
            }
        }

        if (hub > 0)
        {
            oracle.ForceFallback(true);
            try
            {
                string rql = JoinRql("hub0");
                for (int i = 0; i < 200; i++)
                    oracle.Run(rql);

                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < 2000; i++)
                    oracle.Run(rql);

                Report($"[{shape}] Q hub server-join wallUs={(Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency / 2000:F2}");
            }
            finally
            {
                oracle.ForceFallback(false);
            }
        }
    }
}
