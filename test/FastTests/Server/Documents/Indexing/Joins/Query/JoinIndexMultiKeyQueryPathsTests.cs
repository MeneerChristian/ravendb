using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Session;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>The query paths other than the plain query (stream, facets, index entries) read a join on an array of ids from the join index too, and answer like the join of the server.</summary>
public class JoinIndexMultiKeyQueryPathsTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private async Task<JoinIndexHarness> HarnessWithDataAsync()
    {
        // the documents do not carry a score: the paths that do not order by score do not use the score of the join
        JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, indexes: [JoinIndexHarness.MultiOuterIndex(), JoinIndexHarness.InnerIndex()]);
        h.RandomMultiRound(new Random(1), 200);
        await h.AssertCurrentAsync();
        return h;
    }

    /// <summary>Every conversation of the workload names one document.</summary>
    private static async Task OneKeyPerEntryAsync(JoinIndexHarness h)
    {
        for (int i = 0; i < 80; i++)
            h.PutConversationKeys(i, [$"contexts/{i % 14}"], i % 3 == 0 ? "a" : "b");

        await h.AssertCurrentAsync();
    }

    private static string Post(JoinIndexHarness h, string path, string rql)
    {
        using var http = new HttpClient();
        using HttpResponseMessage response = http.PostAsync($"{h.Store.Urls[0]}/databases/{h.Store.Database}/{path}",
            new StringContent(JsonSerializer.Serialize(new { Query = rql, QueryParameters = new { } }), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("Results").GetRawText();
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task FacetsOfAJoinedQueryAreTheSameWhenTheJoinServesIt()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync();
        var oracle = new JoinIndexQueryOracle(h);
        string rql = Join(" where Country = 'nl'", " select facet(Title)");

        // facets may use the score of the join: while an entry names two documents they are read by the server, and are the same
        string withJoin = Post(h, "queries", rql);
        Assert.True(oracle.FallbacksOf("KindNotServed:ScoresMultiKey") > 0, string.Join(", ", oracle.Info.Fallbacks));
        oracle.ForceFallback(true);
        string without = Post(h, "queries", rql);
        oracle.ForceFallback(false);
        Assert.Equal(without, withJoin);

        await OneKeyPerEntryAsync(h);
        long served = oracle.Info.Served;
        withJoin = Post(h, "queries", rql);
        Assert.True(oracle.Info.Served > served, $"not served: {string.Join(", ", oracle.Info.Fallbacks)}");

        oracle.ForceFallback(true);
        without = Post(h, "queries", rql);
        oracle.ForceFallback(false);
        Assert.Equal(without, withJoin);
        Assert.Contains("Values", withJoin);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task IndexEntriesOfAJoinedQueryAreTheSameWhenTheJoinServesIt()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync();
        var oracle = new JoinIndexQueryOracle(h);
        string rql = Join(" where Country = 'nl'", " order by m.Title limit 0, 30");

        long served = oracle.Info.Served;
        string withJoin = Post(h, "queries?debug=entries", rql);
        Assert.True(oracle.Info.Served > served, $"not served: {string.Join(", ", oracle.Info.Fallbacks)}");

        oracle.ForceFallback(true);
        string without = Post(h, "queries?debug=entries", rql);
        oracle.ForceFallback(false);
        Assert.Equal(without, withJoin);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task StreamedJoinedQueryIsTheSameWhenTheJoinServesIt()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync();
        var oracle = new JoinIndexQueryOracle(h);
        string rql = Join(" where Country = 'nl'", "");

        List<string> Stream()
        {
            using IDocumentSession session = h.Store.OpenSession();
            using var stream = session.Advanced.Stream(session.Advanced.RawQuery<JoinIndexHarness.Conversation>(rql));
            var ids = new List<string>();
            while (stream.MoveNext())
                ids.Add(stream.Current.Id);

            return ids;
        }

        List<string> withJoin = Stream();
        Assert.NotEmpty(withJoin);
        Assert.True(oracle.FallbacksOf("KindNotServed:ScoresMultiKey") > 0, string.Join(", ", oracle.Info.Fallbacks));
        oracle.ForceFallback(true);
        List<string> without = Stream();
        oracle.ForceFallback(false);
        Assert.Equal(without, withJoin);

        await OneKeyPerEntryAsync(h);
        long served = oracle.Info.Served;
        withJoin = Stream();
        Assert.NotEmpty(withJoin);
        Assert.True(oracle.Info.Served > served, $"not served: {string.Join(", ", oracle.Info.Fallbacks)}");

        oracle.ForceFallback(true);
        without = Stream();
        oracle.ForceFallback(false);
        Assert.Equal(without, withJoin);
    }
}
