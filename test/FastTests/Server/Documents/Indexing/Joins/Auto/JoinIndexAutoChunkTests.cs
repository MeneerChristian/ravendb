using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Auto.JoinIndexAutoQueryTests;

namespace FastTests.Server.Documents.Indexing.Joins.Auto;

/// <summary>An auto join index applies a change of more documents than a commit takes in several commits, like any join index, and is served again when they are all applied.</summary>
public class JoinIndexAutoChunkTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const int Budget = 100;
    private const int Conversations = 450;
    private const int Contexts = 40;

    private async Task<JoinIndexHarness> CreateSeededAsync()
    {
        var settings = ScoreSettings((RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit), Budget.ToString()));
        JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, settings, putJoin: false);
        h.Save(s =>
        {
            for (int i = 0; i < Contexts; i++)
                s.Store(new JoinIndexHarness.Context { Country = i % 3 == 0 ? "nl" : "de", Name = "n" + i % 5, Rank = i, IsActive = i % 2 == 0, Title = "red fox " + i % 4 }, $"contexts/{i}");
        });
        h.Save(s =>
        {
            for (int i = 0; i < Conversations; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{i % Contexts}", Title = "t" }, $"conversations/{i}");
        });
        Indexes.WaitForIndexing(h.Store);
        return h;
    }

    private static void BulkWrite(JoinIndexHarness h)
    {
        h.Save(s =>
        {
            for (int i = 0; i < Conversations; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{(i * 7 + 3) % Contexts}", Title = "changed" }, $"conversations/{i}");
        });
    }

    private static void AssertChunkedWithoutRebuild(JoinIndex joinIndex)
    {
        Assert.Equal(1, joinIndex.Counters.Builds);
        Assert.DoesNotContain(joinIndex.Counters.RebuildsByReason.Keys, x => x != nameof(JoinRebuildReason.Initial));
        Assert.True(joinIndex.Counters.PartialRefreshes >= Conversations / Budget - 1, $"{joinIndex.Counters.PartialRefreshes} partial refreshes");
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ABulkWriteAfterTheBuildOfTheAutoJoinIsAppliedInChunksAndServed()
    {
        using JoinIndexHarness h = await CreateSeededAsync();
        var oracle = new JoinIndexQueryOracle(h, AutoName);
        string rql = oracle.JoinOf(FirstSub, FirstTail);
        _ = oracle.Run(rql);
        await WaitForTheAutoJoinAsync(h);

        BulkWrite(h);
        Indexes.WaitForIndexing(h.Store);
        await h.AssertCurrentAsync(AutoName);

        AssertChunkedWithoutRebuild(h.Join(AutoName));
        oracle.Check(rql, expectServed: true);
        Assert.Equal([AutoName], AutoJoinNames(h));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ABulkWriteThatArrivesWhileTheAutoJoinIsBuiltIsAppliedWithoutBuildingAgain()
    {
        using JoinIndexHarness h = await CreateSeededAsync();
        var oracle = new JoinIndexQueryOracle(h, AutoName);
        string rql = oracle.JoinOf(FirstSub, FirstTail);
        _ = oracle.Run(rql);

        BulkWrite(h);
        Indexes.WaitForIndexing(h.Store);
        await WaitForTheAutoJoinAsync(h);

        JoinIndex joinIndex = h.Join(AutoName);
        Assert.Equal(1, joinIndex.Counters.Builds);
        Assert.DoesNotContain(joinIndex.Counters.RebuildsByReason.Keys, x => x != nameof(JoinRebuildReason.Initial));
        oracle.Check(rql, expectServed: true);
    }
}
