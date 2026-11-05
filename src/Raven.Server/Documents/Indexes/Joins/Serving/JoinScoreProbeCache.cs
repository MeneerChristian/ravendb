using System;
using System.Collections.Concurrent;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>
/// The probes of the sub-queries of the JOINs that a join index served, per sub-query and state of the joined index. A probe scores every match of the sub-query, which a query
/// does not have to pay again while the joined index has not consumed anything.
/// </summary>
internal sealed class JoinScoreProbeCache
{
    private const int Capacity = 128;

    private readonly ConcurrentDictionary<string, JoinScoreProbe> _probes = new();

    public JoinScoreProbe GetOrProbe(string key, Func<JoinScoreProbe> probe)
    {
        if (_probes.TryGetValue(key, out JoinScoreProbe cached))
            return cached;

        JoinScoreProbe probed = probe();
        if (_probes.Count >= Capacity)
            _probes.Clear();

        _probes[key] = probed;
        return probed;
    }
}
