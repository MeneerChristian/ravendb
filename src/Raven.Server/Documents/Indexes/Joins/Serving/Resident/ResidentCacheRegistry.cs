using System;
using System.Collections.Generic;
using System.Threading;
using Sparrow.LowMemory;

namespace Raven.Server.Documents.Indexes.Joins.Serving.Resident;

/// <summary>
/// The caches of the process: the bytes of all of them are charged against one budget, and a low memory notification clears all of them. The notification holds its handlers weakly, so the registry
/// that registers itself is held by <see cref="Instance"/>.
/// </summary>
public sealed class ResidentCacheRegistry : ILowMemoryHandler
{
    public const long DefaultBudgetBytes = 256L * 1024 * 1024;

    public static ResidentCacheRegistry Instance { get; } = new(DefaultBudgetBytes, registerWithLowMemoryNotification: true);

    private readonly object _gate = new();
    private readonly List<ResidentKeyListCache> _caches = [];
    private long _budgetBytes;
    private long _chargedBytes;
    private int _lowMemory;

    public ResidentCacheRegistry(long budgetBytes, bool registerWithLowMemoryNotification)
    {
        _budgetBytes = budgetBytes;
        if (registerWithLowMemoryNotification)
            LowMemoryNotification.Instance?.RegisterLowMemoryHandler(this);
    }

    public long BudgetBytes => Interlocked.Read(ref _budgetBytes);

    /// <summary>The bytes that the caches hold, in the unit of <see cref="ResidentKeyListCache.ResidentBytes"/>.</summary>
    public long ChargedBytes => Interlocked.Read(ref _chargedBytes);

    public bool IsLowMemory => Volatile.Read(ref _lowMemory) != 0;

    public int CacheCount
    {
        get
        {
            lock (_gate)
                return _caches.Count;
        }
    }

    public void SetBudget(long budgetBytes) => Interlocked.Exchange(ref _budgetBytes, budgetBytes);

    /// <summary>The bytes that the registry can still take: the memory gate of the auto join indexes subtracts the charged bytes from the memory it sees as available.</summary>
    public long FreeBytes => Math.Max(0, BudgetBytes - ChargedBytes);

    internal bool TryCharge(long bytes)
    {
        while (true)
        {
            long charged = Interlocked.Read(ref _chargedBytes);
            if (charged + bytes > BudgetBytes)
                return false;

            if (Interlocked.CompareExchange(ref _chargedBytes, charged + bytes, charged) == charged)
                return true;
        }
    }

    internal void Charge(long bytes) => Interlocked.Add(ref _chargedBytes, bytes);

    internal void Release(long bytes) => Interlocked.Add(ref _chargedBytes, -bytes);

    internal bool IsOverBudget => ChargedBytes > BudgetBytes;

    internal void Register(ResidentKeyListCache cache)
    {
        lock (_gate)
            _caches.Add(cache);
    }

    internal void Unregister(ResidentKeyListCache cache)
    {
        lock (_gate)
            _caches.Remove(cache);
    }

    /// <summary>Drops every list of every cache and keeps new lists out until the memory is back. The epochs stay: an empty cache is valid for its epoch.</summary>
    public void LowMemory(LowMemorySeverity lowMemorySeverity)
    {
        if (lowMemorySeverity == LowMemorySeverity.None)
            return;

        Volatile.Write(ref _lowMemory, 1);
        ResidentKeyListCache[] caches;
        lock (_gate)
            caches = _caches.ToArray();

        foreach (ResidentKeyListCache cache in caches)
            cache.DropAllLists();
    }

    public void LowMemoryOver() => Volatile.Write(ref _lowMemory, 0);
}
