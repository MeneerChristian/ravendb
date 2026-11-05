using System;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.RedTeam;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Documents;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.ServerWide.Context;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>
/// An index that a join index joins is replaced by a definition the join index refuses and then by a valid one: the join index goes to the error state with its reason in the stats and an alert, and comes back by itself
/// when the indexes are valid again, exact, with the tombstones that it held back released.
/// </summary>
public class JoinIndexRefusalRecoveryTests(ITestOutputHelper output) : JoinRedTeamBase(output)
{
    private static IndexDefinition Loading() => new()
    {
        Name = JoinIndexHarness.OuterName,
        Maps = { "from c in docs.Conversations select new { c.ContextId, Title = LoadDocument(c.ContextId, \"Contexts\").Title }" }
    };

    private static async Task CheckAllAsync(JoinIndexHarness h, JoinIndexQueryOracle oracle, bool expectServed)
    {
        bool served = false;
        foreach (string sub in Subs)
        {
            served |= Same(oracle, Join(sub, " order by score() limit 0, 1000"));
            Same(oracle, LeftJoin(sub, " order by m.Title limit 0, 1000"));
        }

        Assert.Equal(expectServed, served);
    }

    private static int Alerts(JoinIndexHarness h)
    {
        using (h.Database.NotificationCenter.GetStored(out var items))
            return items.Count(x => x.Json[nameof(AlertRaised.Reason)].ToString() == AlertReason.Indexing_JoinRefused.ToString());
    }

    private static long Tombstones(JoinIndexHarness h)
    {
        using (h.Database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
        using (context.OpenReadTransaction())
            return h.Database.DocumentsStorage.GetNumberOfTombstones(context);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ARefusedUpstreamDefinitionAndTheValidOneAgainRecoverTheJoinByItselfTwiceAndTheTombstonesAreReleased()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        var rnd = new Random(91);
        h.RandomRound(rnd, 120);
        var oracle = new JoinIndexQueryOracle(h);
        await h.AssertCurrentAsync();
        await CheckAllAsync(h, oracle, expectServed: true);

        for (int cycle = 1; cycle <= 2; cycle++)
        {
            // refused: the join index stops, says why and holds the tombstones back; the answers are the join of the server
            await h.Store.Maintenance.SendAsync(new PutIndexesOperation(Loading()));
            await WaitForStateAsync(h, IndexState.Error);
            var refused = h.Stats().Join;
            Assert.False(string.IsNullOrEmpty(refused.RefusedReason));
            Assert.Equal(1, Alerts(h));
            await CheckAllAsync(h, oracle, expectServed: false);

            for (int i = 0; i < 10; i++)
                h.Delete($"conversations/{rnd.Next(80)}");

            h.RandomRound(rnd, 40);
            await WaitForUpstreamAsync(h);
            await h.CleanTombstonesAsync();
            Assert.True(Tombstones(h) > 0, "the tombstones are held back for the join index that stopped");

            // valid again: no Enable
            await h.Store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndexHarness.OuterIndex()));
            // the join index is built again when the upstream index has another history: it is current when that is done
            await h.AssertCurrentAsync();
            Assert.Equal(IndexState.Normal, StateOf(h));

            var recovered = h.Stats().Join;
            Assert.Null(recovered.RefusedReason);
            Assert.Equal(0, Alerts(h));

            // exact and served again, also after more writes
            await CheckAllAsync(h, oracle, expectServed: true);
            h.RandomRound(rnd, 60);
            await h.AssertCurrentAsync();
            await CheckAllAsync(h, oracle, expectServed: true);

            // the tombstones that the join index held back are cleaned now
            for (int i = 0; i < 10; i++)
                h.Delete($"conversations/{rnd.Next(80)}");

            await h.AssertCurrentAsync();
            await h.CleanTombstonesAsync();
            await h.CleanTombstonesAsync();
            Assert.Equal(0, Tombstones(h));
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ARefusedUpstreamIndexThatIsDeletedAndPutAgainRecoversTheJoin()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        h.RandomRound(new Random(92), 100);
        var oracle = new JoinIndexQueryOracle(h);
        await h.AssertCurrentAsync();

        await h.Store.Maintenance.SendAsync(new PutIndexesOperation(Loading()));
        await WaitForStateAsync(h, IndexState.Error);

        // the index is gone: nothing to read, the join index stays stopped
        await h.Store.Maintenance.SendAsync(new DeleteIndexOperation(JoinIndexHarness.OuterName));
        await Task.Delay(500);
        Assert.Equal(IndexState.Error, h.Stats().State);

        await h.Store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndexHarness.OuterIndex()));
        await h.AssertCurrentAsync();
        Assert.Equal(IndexState.Normal, StateOf(h));
        h.RandomRound(new Random(93), 50);
        await h.AssertCurrentAsync();
        await CheckAllAsync(h, oracle, expectServed: true);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ARefusedUpstreamIndexReplacedByAnotherRefusedDefinitionKeepsTheJoinStopped()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        h.RandomRound(new Random(94), 100);
        var oracle = new JoinIndexQueryOracle(h);
        await h.AssertCurrentAsync();

        await h.Store.Maintenance.SendAsync(new PutIndexesOperation(Loading()));
        await WaitForStateAsync(h, IndexState.Error);

        var other = new IndexDefinition { Name = JoinIndexHarness.OuterName, Maps = { "from c in docs.Conversations select new { c.ContextId, Title = LoadDocument(c.ContextId, \"Contexts\").Name }" } };
        await h.Store.Maintenance.SendAsync(new PutIndexesOperation(other));
        await Task.Delay(1000);
        Assert.Equal(IndexState.Error, h.Stats().State);
        Assert.False(string.IsNullOrEmpty(h.Stats().Join.RefusedReason));
        await CheckAllAsync(h, oracle, expectServed: false);
    }
}
