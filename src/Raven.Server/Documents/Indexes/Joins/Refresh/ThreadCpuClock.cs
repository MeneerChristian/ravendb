using System;
using System.Runtime.InteropServices;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>The processor time the calling thread has used so far, in 100 ns ticks. The time a thread waits for the disk is not part of it.</summary>
public static class ThreadCpuClock
{
    private const int ClockThreadCpuTimeLinux = 3;
    private const int ClockThreadCpuTimeMacOs = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int ClockGetTime(int clockId, out TimeSpec time);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThread")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", EntryPoint = "GetThreadTimes")]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    public static long Now()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user) ? kernel + user : 0;

            int clock = OperatingSystem.IsMacOS() ? ClockThreadCpuTimeMacOs : ClockThreadCpuTimeLinux;
            return ClockGetTime(clock, out TimeSpec time) == 0 ? time.Seconds * TimeSpan.TicksPerSecond + time.Nanoseconds / 100 : 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }
}
