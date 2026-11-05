using System.Collections.Generic;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>
/// A join index has a direction: the main query reads its outer index and the joined sub-query its inner index. The same two indexes joined the other way round
/// (the roles swapped) are a different join, which the join index of the first direction does not serve: it is read as the join of the server, and a join index
/// with the swapped roles serves it.
/// </summary>
public class JoinIndexDirectionTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string ReverseJoinName = "Joins/Reverse";

    // the contexts are the main query, the conversations the joined sub-query: id of the context = the key of the conversation
    private const string ReverseRql = "from index 'Contexts/Search' as c join (from index 'Conversations/ByContext') as m on id(c) = m.ContextId";

    private static IndexDefinition ReverseJoin() => new()
    {
        Name = ReverseJoinName,
        Join = new JoinDefinition { OuterIndex = JoinIndexHarness.InnerName, OuterField = "id()", InnerField = "ContextId", InnerIndex = JoinIndexHarness.OuterName }
            .AddField("Title")
    };

    private async Task<JoinIndexHarness> HarnessWithDataAsync()
    {
        JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() });
        h.RandomRound(new System.Random(1), 200);
        await h.AssertCurrentAsync();
        return h;
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheJoinWithTheRolesSwappedIsNotServedByTheJoinOfTheOtherDirection()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync();
        var oracle = new JoinIndexQueryOracle(h);

        // the direction of the join index is served
        oracle.Check(Join(" where Country = 'nl'"));

        // the swapped roles: the same answer as the join of the server, and the join index of the other direction did not serve it
        (QueryAnswer served, QueryAnswer scan) = oracle.Both(ReverseRql);
        Assert.True(scan.Total > 0, "the swapped join must match documents, or the test proves nothing");
        AssertSame(ReverseRql, served, scan);
        oracle.Check(ReverseRql, expectServed: false);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AJoinWithTheRolesSwappedServesTheOtherDirectionAndTheFirstJoinStaysOutOfIt()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync();
        await h.PutJoinAsync(ReverseJoin());
        await h.AssertCurrentAsync(ReverseJoinName);

        var forward = new JoinIndexQueryOracle(h) { AlsoForced = [ReverseJoinName] };
        var reverse = new JoinIndexQueryOracle(h, ReverseJoinName, JoinIndexHarness.InnerName, JoinIndexHarness.OuterName) { AlsoForced = [JoinIndexHarness.JoinName] };

        long reverseServedBefore = reverse.Info.Served;
        forward.Check(Join(" where Country = 'nl'"));
        Assert.Equal(reverseServedBefore, reverse.Info.Served);

        long forwardServedBefore = forward.Info.Served;
        reverse.Check(ReverseRql);
        Assert.Equal(forwardServedBefore, forward.Info.Served);
    }
}
