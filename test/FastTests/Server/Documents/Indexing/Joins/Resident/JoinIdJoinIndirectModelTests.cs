using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

/// <summary>
/// The join on the id of the inner documents (o.A = id(i)) in the storage mode Indirect: the file holds the keys of the inner documents (their ids) and the outer entries of a key are read from the outer index of the query.
/// Every answer is compared with the join of the server, whatever the cache does and whatever the two indexes did.
/// </summary>
public class JoinIdJoinIndirectModelTests(ITestOutputHelper output) : RavenTestBase(output), IIndirectModeTest
{
    public enum Driver
    {
        EveryStep,
        EveryThirdStep,
        Restart,
        Tiny,
        CacheOff
    }

    private static readonly string[] Subs =
    [
        "",
        " where Country = 'nl'",
        " where Country != 'nl'",
        " where IsActive = true and Name = 'n1'",
        " where Country = 'nl' or Name = 'n3'",
        " where Rank > 20",
        " where IsActive != true",
        " where Country = 'nobody'"
    ];

    public static IEnumerable<object[]> Cases()
    {
        for (int seed = 1; seed <= 4; seed++)
        {
            foreach (Driver driver in Enum.GetValues<Driver>())
            {
                yield return [seed, driver, false];
                yield return [seed, driver, true];
            }
        }
    }

    private static string Query(string sub) => Join(sub, " order by m.Title limit 0, 100");

    private static Task<JoinIndexHarness> CreateAsync(RavenTestBase test, bool multiKey)
    {
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };
        return JoinIndexHarness.CreateAsync(test, settings, indexes: multiKey ? [JoinIndexHarness.MultiOuterIndex(), JoinIndexHarness.InnerIndex()] : null);
    }

    private static void Round(JoinIndexHarness h, Random random, bool multiKey)
    {
        if (multiKey)
            h.RandomMultiRound(random, 12);
        else
            h.RandomRound(random, 12);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [MemberData(nameof(Cases))]
    public async Task EveryAnswerEqualsTheJoinOfTheServerWhateverTheCacheDoes(int seed, Driver driver, bool multiKey)
    {
        using JoinIndexHarness h = await CreateAsync(this, multiKey);
        JoinIndexHarness.UseResident(h.Join(), driver switch { Driver.Tiny => ResidentMode.Tiny, Driver.CacheOff => ResidentMode.Off, _ => ResidentMode.On });
        Assert.True(h.Join().Plan.Schema.IsIndirect);
        Assert.True(h.Join().Plan.Schema.IsFieldJoin);

        var random = new Random(seed * 31 + (int)driver);
        var restartRandom = new Random(seed);
        var oracle = new JoinIndexQueryOracle(h);
        int reads = 0, restarts = 0;
        for (int round = 0; round < 9; round++)
        {
            Round(h, random, multiKey);
            await h.AssertCurrentAsync();

            if (driver == Driver.Restart && restartRandom.Next(3) == 0)
            {
                restarts++;
                await h.Store.Maintenance.SendAsync(new StopIndexOperation(JoinIndexHarness.JoinName));
                for (int i = 0; i < 500 && h.Stats().Status != IndexRunningStatus.Paused; i++)
                    await Task.Delay(20);

                JoinIndexHarness.UseResident(h.Join(), ResidentMode.On);
                await h.Store.Maintenance.SendAsync(new StartIndexOperation(JoinIndexHarness.JoinName));
                await h.AssertCurrentAsync();
            }

            if (driver == Driver.EveryThirdStep && round % 3 != 2)
                continue;

            reads++;
            foreach (string sub in Subs.Where((_, i) => (i + round) % 3 != 0))
            {
                oracle.Check(Query(sub));
            }

            if (driver != Driver.CacheOff)
                await h.AssertResidentAsync();
        }

        Assert.True(reads > 0);
        if (driver == Driver.Restart)
            Assert.True(restarts > 0, "the join index was never restarted");

        Assert.Equal(0, oracle.Info.FallbackTotal);
        Assert.Equal(1, h.Stats().Join.Builds);
        await h.AssertCurrentAsync();
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryChangeOfBothSidesIsFollowedByTheServedAnswer(bool multiKey)
    {
        using JoinIndexHarness h = await CreateAsync(this, multiKey);
        var oracle = new JoinIndexQueryOracle(h);

        void Conversation(int id, string key, string title) { if (multiKey) h.PutConversationKeys(id, key == null ? null : [key], title); else h.PutConversation(id, key, title); }

        h.PutContext(1, "nl", "n1", 10, true, "red fox");
        h.PutContext(2, "de", "n2", 20, false, "blue whale");
        h.PutContext(3, "nl", "n3", 30, true, "red panda");
        Conversation(1, "contexts/1", "a");
        Conversation(2, "contexts/1", "b");
        Conversation(3, "contexts/2", "a");
        Conversation(4, "contexts/99", "b");
        Conversation(5, null, "a");
        await h.AssertCurrentAsync();

        async Task StepAsync(string what)
        {
            await h.AssertCurrentAsync();
            foreach (string sub in Subs)
            {
                try
                {
                    oracle.Check(Query(sub));
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException($"after: {what}; for{sub}", e);
                }
            }
        }

        await StepAsync("build");

        // the inner side: an insert for a key that was waiting, an update that moves terms and the grouping, a delete, a delete that is recreated, and a document that was never referenced
        h.PutContext(99, "fr", "n9", 5, true, "late");
        await StepAsync("insert of the context a conversation waited for");
        h.PutContext(1, "be", "n1x", 11, false, "green fox");
        await StepAsync("update of a context");
        h.Delete("contexts/3");
        await StepAsync("delete of a context");
        h.PutContext(3, "nl", "n3", 31, true, "back");
        await StepAsync("recreate of a deleted context");
        h.PutContext(50, "nl", "n5", 1, true, "nobody points here");
        await StepAsync("insert of a context nobody names");

        // the outer side: an insert, a change of the key (a move), a delete, a delete in one batch and a recreate in the next, a key that is dropped and one that is given
        Conversation(6, "contexts/3", "c");
        await StepAsync("insert of a conversation");
        Conversation(1, "contexts/3", "a");
        await StepAsync("move of a conversation to another context");
        h.Delete("conversations/2");
        await StepAsync("delete of a conversation");
        h.Delete("conversations/3");
        await StepAsync("delete of a conversation (2)");
        Conversation(3, "contexts/1", "b");
        await StepAsync("recreate of a deleted conversation");
        Conversation(5, "contexts/2", "a");
        await StepAsync("a key is given");
        Conversation(4, null, "b");
        await StepAsync("a key is dropped");

        Assert.Equal(0, oracle.Info.FallbackTotal);
        Assert.Equal(1, h.Stats().Join.Builds);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AnOrderByAFieldOfTheJoinedDocumentsIsReadByTheServerAndEqualsIt()
    {
        using JoinIndexHarness h = await CreateAsync(this, multiKey: false);
        h.RandomRound(new Random(5), 150);
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);

        oracle.Check(Join("", " order by c.Name limit 0, 10"), expectServed: false);
        oracle.Check(Join(" where Country = 'nl'", " order by c.Name desc limit 0, 10"), expectServed: false);
        Assert.Equal(2, oracle.FallbacksOf("KindNotServed:OrderByIndirect"));

        // the same join without the order of a joined field is served
        oracle.Check(Join("", " order by m.Title limit 0, 10"));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinOnTheIdOfTheInnerDocumentsMayBeIndirectAndAJoinOnTheIdOfTheOuterDocumentMayNot()
    {
        var onId = new JoinDefinition { OuterIndex = "o", OuterField = "A", InnerIndex = "i", StorageMode = JoinStorageMode.Indirect };
        Assert.Empty(onId.ValidateShape());

        var onOuterId = new JoinDefinition { OuterIndex = "o", OuterField = JoinDefinition.OuterIdKey, InnerField = "B", InnerIndex = "i", StorageMode = JoinStorageMode.Indirect };
        Assert.NotEmpty(onOuterId.ValidateShape());

        using JoinIndexHarness h = await CreateAsync(this, multiKey: false);
        var joinIndex = h.Join();
        Assert.Equal(JoinStorageMode.Indirect, joinIndex.Plan.BuildMode);
        Assert.True(joinIndex.Plan.IsFieldJoin);
        Assert.Equal(JoinStorageMode.Indirect, joinIndex.Plan.Definition.StorageMode);
        Assert.Null(joinIndex.Plan.Definition.InnerField);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnInnerIdOverTheLimitOfTheKeysIsAnErrorLikeAKeyOfAJoinOnAField()
    {
        using JoinIndexHarness h = await CreateAsync(this, multiKey: false);
        h.PutContext(1, "nl", "n1", 1, true, "x");
        h.PutConversation(1, "contexts/1");
        await h.AssertCurrentAsync();

        h.Save(s => s.Store(new JoinIndexHarness.Context { Country = "nl", Name = "long" }, "contexts/" + new string('x', JoinLimits.MaxTermLength + 1)));
        for (int i = 0; i < 1000 && h.Stats().State != IndexState.Error; i++)
            await Task.Delay(20);

        Assert.Equal(IndexState.Error, h.Stats().State);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnOuterKeyOverTheLimitOfTheKeysJoinsNothingAndIsNotAnError()
    {
        using JoinIndexHarness h = await CreateAsync(this, multiKey: false);
        h.PutContext(1, "nl", "n1", 1, true, "x");
        h.PutConversation(1, "contexts/1");
        h.PutConversation(2, "contexts/" + new string('y', JoinLimits.MaxTermLength + 5));
        await h.AssertCurrentAsync();
        var oracle = new JoinIndexQueryOracle(h);
        oracle.Check(Query(""));
        h.PutConversation(2, "contexts/1");
        await h.AssertCurrentAsync();
        oracle.Check(Query(""));
        Assert.Equal(IndexState.Normal, h.Stats().State);
    }
}

public class JoinIdJoinIndirectModelTestsCacheOff(ITestOutputHelper output) : JoinIdJoinIndirectModelTests(output), IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.Off; }
