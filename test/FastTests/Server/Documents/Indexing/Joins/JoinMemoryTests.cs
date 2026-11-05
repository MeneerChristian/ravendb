using System;
using System.IO;
using System.Linq;
using Raven.Server.Documents.Indexes.Joins;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins;

public class JoinMemoryTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    private const long Mb = 1024 * 1024;

    private sealed class FakeMachine : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("join-cgroup").FullName;
        public string Meminfo => Path.Combine(Root, "meminfo");
        public string SelfCgroup => Path.Combine(Root, "self-cgroup");

        public FakeMachine(long totalMb = 32 * 1024, long availableMb = 20 * 1024)
        {
            File.WriteAllText(Meminfo, $"MemTotal:       {totalMb * 1024} kB\nMemFree:         1 kB\nMemAvailable:   {availableMb * 1024} kB\nBuffers: 5 kB\n");
        }

        public string GroupV2(string max, long anon, long shmem, long file, long inactiveFile = 0, long activeFile = 0, string subdirectory = "")
        {
            string directory = Directory.CreateDirectory(Path.Combine(Root, subdirectory)).FullName;
            File.WriteAllText(Path.Combine(directory, "memory.max"), max + "\n");
            File.WriteAllText(Path.Combine(directory, "memory.current"), (anon + shmem + file).ToString());
            File.WriteAllText(Path.Combine(directory, "memory.stat"), $"anon {anon}\nfile {file}\nshmem {shmem}\ninactive_file {inactiveFile}\nactive_file {activeFile}\nfile_mapped 5\n");
            return directory;
        }

        public void GroupV1(string limit, long rss, long shmem, long cache, long? totalRss = null, long? totalShmem = null)
        {
            string directory = Directory.CreateDirectory(Path.Combine(Root, "memory")).FullName;
            File.WriteAllText(Path.Combine(directory, "memory.limit_in_bytes"), limit + "\n");
            File.WriteAllText(Path.Combine(directory, "memory.usage_in_bytes"), (rss + cache).ToString());
            string total = totalRss == null ? "" : $"total_rss {totalRss}\ntotal_shmem {totalShmem ?? 0}\n";
            File.WriteAllText(Path.Combine(directory, "memory.stat"), $"cache {cache}\nrss {rss}\nshmem {shmem}\nmapped_file 5\n{total}");
        }

        public JoinMemory Memory() => new(Root, cacheTicks: 0, procSelfCgroup: null, procMeminfo: Meminfo);

        public void Dispose() => Directory.Delete(Root, true);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void WhenThePageCacheFillsTheContainerTheHeadroomIsStillTheLimitWithoutTheAnonymousAndTheSharedMemory()
    {
        using var machine = new FakeMachine();
        machine.GroupV2((1024 * Mb).ToString(), anon: 100 * Mb, shmem: 92 * Mb, file: 831 * Mb, inactiveFile: 462 * Mb, activeFile: 342 * Mb);
        var memory = machine.Memory();

        Assert.Equal(832 * Mb, memory.ContainerHeadroomBytes());

        // the figure of the server counts the page cache as used (about 0, memory.current is at the limit) or is the whole limit: neither is used
        Assert.Equal(832 * Mb, memory.AvailableBytes(0));
        Assert.Equal(832 * Mb, memory.AvailableBytes(1024 * Mb));
        Assert.Equal(832 * Mb, memory.AvailableBytes(100 * Mb));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void WhenTheProcessHoldsTheMemoryTheHeadroomIsSmallWhateverTheServerReports()
    {
        using var machine = new FakeMachine();
        machine.GroupV2((1024 * Mb).ToString(), anon: 800 * Mb, shmem: 50 * Mb, file: 100 * Mb, inactiveFile: 90 * Mb, activeFile: 10 * Mb);
        var memory = machine.Memory();

        Assert.Equal(174 * Mb, memory.AvailableBytes(1024 * Mb));
        Assert.Equal(174 * Mb, memory.AvailableBytes(0));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheHeadroomIsNeverNegative()
    {
        using var machine = new FakeMachine();
        machine.GroupV2((1024 * Mb).ToString(), anon: 1000 * Mb, shmem: 100 * Mb, file: 0);
        Assert.Equal(0, machine.Memory().AvailableBytes(1024 * Mb));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheMemoryThatTheMachineHasAvailableCapsTheHeadroom()
    {
        using var machine = new FakeMachine(totalMb: 32 * 1024, availableMb: 300);
        machine.GroupV2((1024 * Mb).ToString(), anon: 100 * Mb, shmem: 0, file: 800 * Mb);
        Assert.Equal(300 * Mb, machine.Memory().AvailableBytes(1024 * Mb));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void CgroupV1ReadsTheLimitTheResidentAndTheSharedMemoryAndNotTheCache()
    {
        using var machine = new FakeMachine();
        machine.GroupV1((1024 * Mb).ToString(), rss: 120 * Mb, shmem: 30 * Mb, cache: 850 * Mb);
        var memory = machine.Memory();

        Assert.Equal(874 * Mb, memory.ContainerHeadroomBytes());
        Assert.Equal(874 * Mb, memory.AvailableBytes(0));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void CgroupV1PrefersTheTotalsOfTheHierarchy()
    {
        using var machine = new FakeMachine();
        machine.GroupV1((1024 * Mb).ToString(), rss: 10 * Mb, shmem: 1 * Mb, cache: 0, totalRss: 400 * Mb, totalShmem: 24 * Mb);
        Assert.Equal(600 * Mb, machine.Memory().AvailableBytes(0));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void WithoutALimitTheServerFigureIsUsed()
    {
        using (var v2 = new FakeMachine())
        {
            v2.GroupV2("max", 1, 1, 1);
            var memory = v2.Memory();
            Assert.Null(memory.ContainerHeadroomBytes());
            Assert.Equal(777 * Mb, memory.AvailableBytes(777 * Mb));
        }

        using (var v1 = new FakeMachine())
        {
            v1.GroupV1("9223372036854771712", 1, 1, 1);
            Assert.Null(v1.Memory().ContainerHeadroomBytes());
            Assert.Equal(777 * Mb, v1.Memory().AvailableBytes(777 * Mb));
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void ALimitAboveTheMemoryOfTheMachineIsNoLimit()
    {
        using var machine = new FakeMachine(totalMb: 2048);
        machine.GroupV2((4096 * Mb).ToString(), anon: 1, shmem: 1, file: 1);
        Assert.Null(machine.Memory().ContainerHeadroomBytes());
        Assert.Equal(55, machine.Memory().AvailableBytes(55));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void MissingFilesFallBackToTheServerFigure()
    {
        Assert.Equal(5, new JoinMemory(Path.Combine(Path.GetTempPath(), "join-no-such-cgroup"), cacheTicks: 0, procSelfCgroup: null, procMeminfo: null).AvailableBytes(5));

        using var machine = new FakeMachine();
        machine.GroupV2((1024 * Mb).ToString(), 1, 1, 1);
        File.Delete(Path.Combine(machine.Root, "memory.stat"));
        Assert.Equal(9, machine.Memory().AvailableBytes(9));

        File.WriteAllText(Path.Combine(machine.Root, "memory.stat"), "file 5\nshmem 3\n");
        Assert.Equal(9, machine.Memory().AvailableBytes(9));

        File.WriteAllText(Path.Combine(machine.Root, "memory.stat"), "anon notanumber\n");
        Assert.Equal(9, machine.Memory().AvailableBytes(9));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void WithoutTheFileOfTheMachineTheHeadroomIsNotCapped()
    {
        using var machine = new FakeMachine();
        machine.GroupV2((1024 * Mb).ToString(), anon: 100 * Mb, shmem: 0, file: 800 * Mb);
        File.Delete(machine.Meminfo);
        Assert.Equal(924 * Mb, machine.Memory().AvailableBytes(0));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheCgroupOfTheProcessIsFoundThroughProcSelfCgroupAndAParentLimitCounts()
    {
        using var machine = new FakeMachine();
        machine.GroupV2((1024 * Mb).ToString(), anon: 200 * Mb, shmem: 0, file: 0, subdirectory: "kubepods");
        machine.GroupV2("max", anon: 150 * Mb, shmem: 0, file: 0, subdirectory: Path.Combine("kubepods", "pod1", "ctr"));
        File.WriteAllText(machine.SelfCgroup, "0::/kubepods/pod1/ctr\n");

        var memory = new JoinMemory(machine.Root, cacheTicks: 0, procSelfCgroup: machine.SelfCgroup, procMeminfo: machine.Meminfo);
        Assert.Equal(824 * Mb, memory.AvailableBytes(0));

        machine.GroupV2((512 * Mb).ToString(), anon: 150 * Mb, shmem: 0, file: 0, subdirectory: Path.Combine("kubepods", "pod1", "ctr"));
        Assert.Equal(362 * Mb, memory.AvailableBytes(0));

        File.WriteAllText(machine.SelfCgroup, "12:cpu,cpuacct:/x\n11:memory:/other\n");
        Directory.CreateDirectory(Path.Combine(machine.Root, "memory", "other"));
        File.WriteAllText(Path.Combine(machine.Root, "memory", "other", "memory.limit_in_bytes"), (256 * Mb).ToString());
        File.WriteAllText(Path.Combine(machine.Root, "memory", "other", "memory.stat"), $"rss {56 * Mb}\nshmem 0\n");
        Assert.Equal(200 * Mb, memory.AvailableBytes(0));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void AReadingIsKeptForTheCacheTime()
    {
        using var machine = new FakeMachine();
        machine.GroupV2((1024 * Mb).ToString(), anon: 100 * Mb, shmem: 0, file: 0);
        var memory = new JoinMemory(machine.Root, cacheTicks: TimeSpan.TicksPerHour, procSelfCgroup: null, procMeminfo: machine.Meminfo);
        Assert.Equal(924 * Mb, memory.AvailableBytes(0));

        machine.GroupV2((1024 * Mb).ToString(), anon: 500 * Mb, shmem: 0, file: 0);
        Assert.Equal(924 * Mb, memory.AvailableBytes(0));
        Assert.Equal(524 * Mb, machine.Memory().AvailableBytes(0));
    }
}
