using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>A join index adds one score to every joined document, so it serves a scored join only when every document that the sub-query matches has that score: all of them, not the first ones.</summary>
public class JoinIndexScoreProbeTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private sealed class ContextWithCountries
    {
        public string[] Country { get; set; }

        public string Name { get; set; }

        public int Rank { get; set; }

        public bool IsActive { get; set; }

        public string Title { get; set; }
    }

    private static Dictionary<string, string> ScoreSettings() => new() { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };

    private static void PutContextWithTheCountryTwice(JoinIndexHarness h, int id)
    {
        h.Save(s =>
        {
            var context = new ContextWithCountries { Country = ["nl", "nl"], Name = "n" + id, Rank = id, IsActive = true, Title = "red fox" };
            s.Store(context, $"contexts/{id}");
            s.Advanced.GetMetadataFor(context)[Constants.Documents.Metadata.Collection] = "Contexts";
        });
    }

    private static async Task<JoinIndexHarness> HarnessWithManyContextsAsync(RavenTestBase test, int contexts)
    {
        JoinIndexHarness h = await JoinIndexHarness.CreateAsync(test, ScoreSettings());
        h.Save(s =>
        {
            for (int i = 0; i < contexts; i++)
                s.Store(new JoinIndexHarness.Context { Country = "nl", Name = "n" + i, Rank = i, IsActive = true, Title = "red fox" }, $"contexts/{i}");
        });

        await h.AssertCurrentAsync();
        h.Save(s =>
        {
            for (int i = 0; i < contexts + 1; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{i}", Title = "t" + i }, $"conversations/{i}");
        });

        return h;
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ADocumentThatScoresDifferentlyAfterTheFirstOnesIsNotServedAsIfAllScoredAlike()
    {
        using JoinIndexHarness h = await HarnessWithManyContextsAsync(this, 150);
        PutContextWithTheCountryTwice(h, 150);
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);

        oracle.Check(Join(" where Country = 'nl'", " order by score() limit 0, 200"), expectServed: false);
        oracle.Check(Join(" where Country = 'nl'", " order by score() desc limit 0, 20"), expectServed: false);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnAnswerAboutTheScoresIsNotKeptWhenTheJoinedIndexChanges()
    {
        using JoinIndexHarness h = await HarnessWithManyContextsAsync(this, 150);
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        string rql = Join(" where Country = 'nl'", " order by score() limit 0, 200");

        oracle.Check(rql);
        oracle.Check(rql);

        PutContextWithTheCountryTwice(h, 150);
        await h.AssertCurrentAsync();

        oracle.Check(rql, expectServed: false);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ScoresThatAreTheSameForEveryDocumentAreStillServed()
    {
        using JoinIndexHarness h = await HarnessWithManyContextsAsync(this, 150);
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);

        oracle.Check(Join(" where Country = 'nl'", " order by score() limit 0, 200"));
        oracle.Check(Join(" where Country = 'nl' and IsActive = true", " order by score() desc limit 0, 20"));
        Assert.Equal(0, oracle.Info.FallbackTotal);
    }
}
