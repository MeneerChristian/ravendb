using System;
using System.Collections.Generic;
using FastTests;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Serving;

/// <summary>A request asks the cache of the score probes once, however often the serving asks for the probe: the key is built once and the sub-query is probed once.</summary>
public class JoinServeRequestProbeTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    private static JoinIndexState Inner(long doc) => new()
    {
        EnvironmentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Consumed = new Dictionary<string, (long Doc, long Tombstone)> { ["Contexts"] = (doc, 0), ["Other"] = (3, 4) }
    };

    private sealed class Counts
    {
        public int Keys;
        public int Probes;
    }

    private static JoinServeRequest Request(Counts counts, JoinScoreProbeCache cache, long doc, float score = 2f) => new()
    {
        Inner = Inner(doc),
        ProbeCache = cache,
        ProbeKey = () =>
        {
            counts.Keys++;
            return "from Contexts|{}";
        },
        ProbeFunc = () =>
        {
            counts.Probes++;
            return new JoinScoreProbe(true, true, score);
        }
    };

    [RavenFact(RavenTestCategory.Querying)]
    public void TheProbeOfARequestIsReadFromTheCacheAndTheKeyIsBuiltOnce()
    {
        var counts = new Counts();
        var cache = new JoinScoreProbeCache();
        JoinServeRequest request = Request(counts, cache, 10);

        for (int i = 0; i < 4; i++)
        {
            JoinScoreProbe probe = request.ProbeInnerScore();
            Assert.True(probe.HasMatches && probe.IsUniform);
            Assert.Equal(2f, probe.Score);
        }

        Assert.Equal(1, counts.Keys);
        Assert.Equal(1, counts.Probes);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void ARequestOfTheSameStateHitsTheCacheAndARequestOfAnotherStateProbesAgain()
    {
        var counts = new Counts();
        var cache = new JoinScoreProbeCache();

        Request(counts, cache, 10).ProbeInnerScore();
        JoinScoreProbe hit = Request(counts, cache, 10, score: 99f).ProbeInnerScore();
        Assert.Equal(2f, hit.Score);
        Assert.Equal(1, counts.Probes);

        JoinScoreProbe miss = Request(counts, cache, 11, score: 7f).ProbeInnerScore();
        Assert.Equal(7f, miss.Score);
        Assert.Equal(2, counts.Probes);
        Assert.Equal(3, counts.Keys);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void WithoutACacheTheSubQueryIsProbedOncePerRequest()
    {
        var counts = new Counts();
        JoinServeRequest request = Request(counts, null, 10);
        request.ProbeInnerScore();
        request.ProbeInnerScore();
        request.ProbeInnerScore();
        Assert.Equal(1, counts.Probes);
        Assert.Equal(0, counts.Keys);
    }
}
