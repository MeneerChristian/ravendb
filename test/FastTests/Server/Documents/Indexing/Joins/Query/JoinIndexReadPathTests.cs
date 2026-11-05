using System.Collections.Generic;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>
/// The scored shapes of the read path: a query that asks for the score of the join (the probe of the sub-query) and a join that is part of the where clause with a score (the match is made for the entries of the
/// join and for the score). The answers are the answers of the join of the server for the first query, the repeated ones, the ones after a change of the joined index, and with the cache of the probes cold and warm.
/// </summary>
public class JoinIndexReadPathTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const int Contexts = 40;

    private static Dictionary<string, string> ScoreSettings() => new() { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };

    private static async Task<JoinIndexHarness> HarnessAsync(RavenTestBase test)
    {
        JoinIndexHarness h = await JoinIndexHarness.CreateAsync(test, ScoreSettings());
        h.Save(s =>
        {
            for (int i = 0; i < Contexts; i++)
                s.Store(new JoinIndexHarness.Context { Country = i % 4 == 0 ? "de" : "nl", Name = "n" + i, Rank = i, IsActive = true, Title = "red fox" }, $"contexts/{i}");
        });

        await h.AssertCurrentAsync();
        h.Save(s =>
        {
            for (int i = 0; i < Contexts + 10; i++)
                s.Store(new JoinIndexHarness.Conversation { ContextId = $"contexts/{i}", Title = "t" + i }, $"conversations/{i}");
        });

        await h.AssertCurrentAsync();
        return h;
    }

    private static readonly string[] Wheres =
    [
        " where matches(c) order by score() limit 0, 200",
        " where matches(c) or m.Title = 't45' order by score() limit 0, 200",
        " where matches(c) or m.Title = 't3' order by score() desc limit 0, 7",
        " where m.Title = 't3' or matches(c) order by score() limit 0, 200",
        " where search(m.Title, 't1*') and (matches(c) or m.Title = 't47') order by score() limit 0, 200",
        " where m.Title != 't3' and not matches(c) order by score() limit 0, 200",
    ];

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AJoinInTheWhereClauseWithAScoreIsTheJoinOfTheServerForEveryShapeAndEveryRepetition()
    {
        using JoinIndexHarness h = await HarnessAsync(this);
        var oracle = new JoinIndexQueryOracle(h);

        foreach (string sub in new[] { " where Country = 'nl'", "" })
        {
            foreach (string where in Wheres)
            {
                // the first run asks the sub-query, the next ones find the answer
                oracle.Check(Join(sub, where), expectServed: sub != " where Country = 'de' and Rank > 100" || where.Contains("not matches") == false);
                oracle.Check(Join(sub, where), expectServed: sub != " where Country = 'de' and Rank > 100" || where.Contains("not matches") == false);
            }
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AJoinInTheWhereClauseWithAScoreReadsTheKeysOnceAndGivesTheScoresOfTheJoinOfTheServer()
    {
        using JoinIndexHarness h = await HarnessAsync(this);
        var oracle = new JoinIndexQueryOracle(h);
        string rql = Join("", " where matches(c) or m.Title = 't45' order by score() limit 0, 200");

        QueryAnswer served = oracle.Both(rql).Served;
        oracle.Check(rql);
        Assert.True(served.Rows.Count > Contexts, served.Describe());

        // the entries and the score are two matches over one list of keys: an Indirect join index reads the keys of the answer once
        if (this is IIndirectModeTest)
        {
            Assert.Equal(Contexts, served.Counters.GetValueOrDefault("Join/c/JoinKeys"));
            Assert.Equal(1, served.Counters.GetValueOrDefault("Join/c/JoinKeyListsShared"));
        }
        else
            Assert.True(served.Counters.GetValueOrDefault("Join/c/JoinIndexServed") == 1, string.Join(", ", served.Counters));
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task AJoinInTheWhereClauseWithAScoreFollowsTheChangesOfTheJoinedIndex()
    {
        using JoinIndexHarness h = await HarnessAsync(this);
        var oracle = new JoinIndexQueryOracle(h);
        string rql = Join(" where Country = 'nl'", " where matches(c) or m.Title = 't45' order by score() limit 0, 200");

        oracle.Check(rql);
        oracle.Check(rql);

        h.Save(s => s.Store(new JoinIndexHarness.Context { Country = "nl", Name = "x", Rank = 1, IsActive = true, Title = "red fox" }, "contexts/44"));
        await h.AssertCurrentAsync();
        oracle.Check(rql);

        h.Save(s => s.Delete("contexts/1"));
        await h.AssertCurrentAsync();
        oracle.Check(rql);
        oracle.Check(rql);
    }
}

public class JoinIndexReadPathTestsIndirect(ITestOutputHelper output) : JoinIndexReadPathTests(output), IIndirectModeTest;

public class JoinIndexReadPathTestsIndirectCacheOn(ITestOutputHelper output) : JoinIndexReadPathTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.On; }

public class JoinIndexReadPathTestsIndirectCacheTiny(ITestOutputHelper output) : JoinIndexReadPathTests(output), IIndirectModeTest, IResidentModeTest { public ResidentMode ResidentMode => ResidentMode.Tiny; }
