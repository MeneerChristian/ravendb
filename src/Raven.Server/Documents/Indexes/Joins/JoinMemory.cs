using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Raven.Server.Documents.Indexes.Joins;

/// <summary>
/// The memory that a join index may still take. The available memory that the server reports is the right figure on a machine, but in a container (a cgroup limit) it is not: without
/// Memory.UseTotalDirtyMemInsteadOfMemUsage the page cache of the data files counts as used and the figure is about 0 as soon as the data was read, and with it the figure is the whole limit
/// whatever the process holds. So when the process has a memory limit the figure is the limit minus the anonymous and the shared memory of the cgroup (the page cache is reclaimed for a build or a refresh,
/// the memory that the process holds is not), at most what the machine has available (MemAvailable also counts the reclaimable cache); the figure of the server is used only without a limit.
/// </summary>
internal sealed class JoinMemory(
    string cgroupRoot = JoinMemory.DefaultCgroupRoot,
    long cacheTicks = 250 * TimeSpan.TicksPerMillisecond,
    string procSelfCgroup = JoinMemory.DefaultProcSelfCgroup,
    string procMeminfo = JoinMemory.DefaultProcMeminfo)
{
    internal const string DefaultCgroupRoot = "/sys/fs/cgroup";
    internal const string DefaultProcSelfCgroup = "/proc/self/cgroup";
    internal const string DefaultProcMeminfo = "/proc/meminfo";

    private sealed record Reading(long ReadAt, long Headroom, long MachineAvailable);

    private Reading _reading;

    /// <summary>The limit of the cgroup minus its anonymous and shared memory, null when the process has no cgroup limit below the memory of the machine (or it cannot be read).</summary>
    public long? ContainerHeadroomBytes()
    {
        long headroom = Current().Headroom;
        return headroom < 0 ? null : headroom;
    }

    /// <summary>The headroom of the container (at most what the machine has available) when the process has a memory limit, else the figure that the server reports.</summary>
    public long AvailableBytes(long serverAvailableBytes)
    {
        Reading reading = Current();
        if (reading.Headroom < 0)
            return serverAvailableBytes;

        return reading.MachineAvailable < 0 ? reading.Headroom : Math.Min(reading.Headroom, reading.MachineAvailable);
    }

    private Reading Current()
    {
        long now = DateTime.UtcNow.Ticks;
        Reading reading = Volatile.Read(ref _reading);
        if (reading != null && now - reading.ReadAt < cacheTicks)
            return reading;

        long machineTotal = MeminfoBytes("MemTotal:");
        reading = new Reading(now, ReadHeadroom(machineTotal), MeminfoBytes("MemAvailable:"));
        Volatile.Write(ref _reading, reading);
        return reading;
    }

    private long ReadHeadroom(long machineTotal)
    {
        string root = Path.GetFullPath(cgroupRoot);
        foreach (string leaf in Leaves(root))
        {
            for (string directory = leaf; directory != null && directory.StartsWith(root, StringComparison.Ordinal); directory = Path.GetDirectoryName(directory))
            {
                long headroom = ReadGroup(directory, machineTotal, out bool limited);
                if (limited)
                    return headroom;

                if (directory.Length <= root.Length)
                    break;
            }
        }

        return -1;
    }

    private IEnumerable<string> Leaves(string root)
    {
        var found = new List<string>();
        try
        {
            if (procSelfCgroup != null && File.Exists(procSelfCgroup))
            {
                foreach (string line in File.ReadLines(procSelfCgroup))
                {
                    string[] parts = line.Split(':', 3);
                    if (parts.Length != 3)
                        continue;

                    string path = parts[2].Trim().TrimStart('/');
                    if (parts[1].Length == 0)
                        found.Add(Path.GetFullPath(Path.Combine(root, path)));
                    else if (Array.IndexOf(parts[1].Split(','), "memory") >= 0)
                        found.Add(Path.GetFullPath(Path.Combine(root, "memory", path)));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        found.Add(root);
        found.Add(Path.Combine(root, "memory"));
        return found;
    }

    private static long ReadGroup(string directory, long machineTotal, out bool limited)
    {
        limited = false;
        try
        {
            string limitFile = File.Exists(Path.Combine(directory, "memory.max")) ? "memory.max" : "memory.limit_in_bytes";
            string limitPath = Path.Combine(directory, limitFile);
            if (File.Exists(limitPath) == false)
                return -1;

            string text = File.ReadAllText(limitPath).Trim();
            if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long limit) == false || (machineTotal > 0 && limit >= machineTotal))
                return -1;

            long anon = -1, shmem = 0, totalAnon = -1, totalShmem = -1;
            foreach (string line in File.ReadLines(Path.Combine(directory, "memory.stat")))
            {
                int space = line.IndexOf(' ');
                if (space <= 0)
                    continue;

                ReadOnlySpan<char> name = line.AsSpan(0, space);
                if (name.SequenceEqual("anon") || name.SequenceEqual("rss"))
                    anon = long.Parse(line.AsSpan(space + 1), CultureInfo.InvariantCulture);
                else if (name.SequenceEqual("shmem"))
                    shmem = long.Parse(line.AsSpan(space + 1), CultureInfo.InvariantCulture);
                else if (name.SequenceEqual("total_rss"))
                    totalAnon = long.Parse(line.AsSpan(space + 1), CultureInfo.InvariantCulture);
                else if (name.SequenceEqual("total_shmem"))
                    totalShmem = long.Parse(line.AsSpan(space + 1), CultureInfo.InvariantCulture);
            }

            long used = (totalAnon >= 0 ? totalAnon : Math.Max(0, anon)) + (totalShmem >= 0 ? totalShmem : shmem);
            if (anon < 0 && totalAnon < 0)
                return -1;

            limited = true;
            return Math.Max(0, limit - used);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            limited = false;
            return -1;
        }
    }

    private long MeminfoBytes(string key)
    {
        try
        {
            if (procMeminfo == null || File.Exists(procMeminfo) == false)
                return -1;

            foreach (string line in File.ReadLines(procMeminfo))
            {
                if (line.StartsWith(key, StringComparison.Ordinal) == false)
                    continue;

                ReadOnlySpan<char> value = line.AsSpan(key.Length).Trim();
                int space = value.IndexOf(' ');
                return long.Parse(space < 0 ? value : value[..space], CultureInfo.InvariantCulture) * 1024;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
        }

        return -1;
    }
}
