using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>What the queries did with one join index: how many joins it served, how many were read as usual and why, how long the queries waited for it.</summary>
public sealed class JoinQueryCounters
{
    private long _served;
    private long _peeks;
    private long _waits;
    private long _waitTicks;
    private long _waitMaxTicks;
    private readonly ConcurrentDictionary<string, long> _fallbacks = new();

    public long ServedCount => Interlocked.Read(ref _served);

    /// <summary>How many times a query opened the two indexes to see whether the join index is behind them.</summary>
    internal long PeekCount => Interlocked.Read(ref _peeks);

    public long WaitCount => Interlocked.Read(ref _waits);

    public double WaitTotalInMs => Interlocked.Read(ref _waitTicks) * 1000.0 / Stopwatch.Frequency;

    public double WaitMaxInMs => Interlocked.Read(ref _waitMaxTicks) * 1000.0 / Stopwatch.Frequency;

    public IEnumerable<KeyValuePair<string, long>> Fallbacks => _fallbacks;

    public long FallbackTotal
    {
        get
        {
            long total = 0;
            foreach (KeyValuePair<string, long> fallback in _fallbacks)
                total += fallback.Value;

            return total;
        }
    }

    internal void Peeked() => Interlocked.Increment(ref _peeks);

    public void Served() => Interlocked.Increment(ref _served);

    public void Fallback(string reason) => _fallbacks.AddOrUpdate(reason, 1, static (_, count) => count + 1);

    public void Waited(long ticks)
    {
        Interlocked.Increment(ref _waits);
        Interlocked.Add(ref _waitTicks, ticks);

        long max;
        while (ticks > (max = Interlocked.Read(ref _waitMaxTicks)))
        {
            if (Interlocked.CompareExchange(ref _waitMaxTicks, ticks, max) == max)
                break;
        }
    }
}
