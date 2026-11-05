using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using Raven.Server.Documents.Indexes.Joins.Serving;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>A LEFT JOIN that a join index serves equals the left join of the server: every entry of the outer index stays, those with a joined document that matches come first when the score decides.</summary>
public class JoinIndexLeftJoinQueryTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static readonly string[] Subs =
    [
        "",
        " where Country = 'nl'",
        " where Country = 'C1'",
        " where Name = 'n1'",
        " where Rank = 10",
        " where IsActive = true",
        " where Country = 'nl' and IsActive = true",
        " where Country != 'nl'",
        " where IsActive != true",
        " where Country = 'nobody'"
    ];

    private static readonly string[] Tails =
    [
        " limit 0, 1000",
        " order by score() limit 0, 7",
        " order by score() desc limit 3, 7",
        " order by m.Title limit 0, 10",
        " where m.Title = 'a' limit 0, 1000",
        " where m.Title = 'a' order by score() limit 0, 5",
        " boost 2 order by score() limit 0, 20",
        " boost 0 limit 0, 20",
        " limit 1000, 5"
    ];

    private static string LeftJoin(string sub, string tail = "") => $"{Outer} left join (from index 'Contexts/Search'{sub}) as c on m.ContextId = id(c){tail}";

    private static Dictionary<string, string> ScoreSettings() => new() { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };

    private async Task<JoinIndexHarness> HarnessWithDataAsync(int seed, int operations = 120)
    {
        JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        h.RandomRound(new Random(seed), operations);
        await h.AssertCurrentAsync();
        return h;
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task EveryServedKindOfALeftJoinEqualsTheLeftJoinOfTheServer()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(1, 200);
        var oracle = new JoinIndexQueryOracle(h);

        foreach (string sub in Subs)
        {
            foreach (string tail in Tails)
                oracle.Check(LeftJoin(sub, tail));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
        Assert.Equal(Subs.Length * Tails.Length, oracle.Info.Served);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task EveryEntryOfTheOuterIndexStaysAndTheMatchedOnesComeFirst()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(2, 200);
        var oracle = new JoinIndexQueryOracle(h);

        int everything = (int)oracle.Run($"{Outer} limit 0, 1").Total;
        Assert.True(everything > 20);

        foreach (string sub in Subs)
        {
            oracle.Check(LeftJoin(sub, " limit 0, 1"));
            oracle.Check(LeftJoin(sub, " order by score() limit 0, 1000"));
            QueryAnswer left = oracle.Run(LeftJoin(sub, " order by score() limit 0, 1000"));
            Assert.Equal(everything, left.Total);

            QueryAnswer inner = oracle.Run(Join(sub, " order by score() limit 0, 1000"));
            Assert.True(inner.Total <= everything);
            // the entries that the join matches are the first ones of the left join, with the scores they have there
            Assert.Equal(inner.Rows.OrderBy(x => x.Id), left.Rows.Take(inner.Rows.Count).OrderBy(x => x.Id));
            Assert.Equal(left.Rows.Select(x => x.Score).OrderByDescending(x => x), left.Rows.Select(x => x.Score));
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ConditionsOnTheAliasOfALeftJoinAreServed()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(3, 200);
        var oracle = new JoinIndexQueryOracle(h);

        string[] conditions =
        [
            "c.Country = 'nl'",
            "c.Country != 'nl'",
            "m.Title != 'zzz' and not (c.Country = 'nl')",
            "c.Country = 'nl' or m.Title = 'a'",
            "m.Title != 'zzz' and not (c.Country = 'nl' or m.Title = 'a')",
            "c.Country = 'c1' and c.IsActive = true",
            "c.IsActive != true or m.Title = 'b'"
        ];

        foreach (string condition in conditions)
        {
            oracle.Check(LeftJoin("", $" where {condition} order by score() limit 0, 50"));
            oracle.Check(LeftJoin("", $" where {condition} order by m.Title limit 0, 50"));
            oracle.Check(LeftJoin(" where Rank = 10", $" where {condition} limit 0, 1000"));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task EntriesWhoseJoinedDocumentDoesNotExistYetOrAnymoreStayAndGetNoScore()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        var oracle = new JoinIndexQueryOracle(h);

        h.PutConversation(1, "contexts/1");
        h.PutConversation(2, "contexts/2");
        h.PutConversation(3, null);
        h.PutConversation(4, "contexts/missing");
        h.PutContext(1, "nl");
        await h.AssertCurrentAsync();

        string rql = LeftJoin(" where Country = 'nl'", " order by score() limit 0, 10");
        oracle.Check(rql);
        QueryAnswer answer = oracle.Run(rql);
        Assert.Equal(4, answer.Total);
        Assert.Equal("conversations/1", answer.Rows[0].Id);
        Assert.True(answer.Rows[0].Score > answer.Rows[1].Score);

        // the context that is missing is created later
        h.PutContext(2, "nl");
        h.PutContext(9, "nl");
        h.PutConversation(4, "contexts/9");
        await h.AssertCurrentAsync();
        oracle.Check(rql);
        Assert.Equal(new[] { "conversations/1", "conversations/2", "conversations/4" }, oracle.Run(rql).Rows.Take(3).Select(x => x.Id).OrderBy(x => x));

        // deleted and recreated with another country
        h.Delete("contexts/1");
        await h.AssertCurrentAsync();
        oracle.Check(rql);
        Assert.Equal(4, oracle.Run(rql).Total);

        h.PutContext(1, "c3");
        await h.AssertCurrentAsync();
        oracle.Check(rql);
        oracle.Check(LeftJoin(" where Country = 'c3'", " order by score() limit 0, 10"));
        oracle.Check(LeftJoin(" where Country != 'nl'", " order by score() limit 0, 10"));
        oracle.Check(LeftJoin("", " order by score() limit 0, 10"));
        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(12)]
    public async Task RandomWorkloadServedLeftJoinEqualsTheLeftJoinOfTheServer(int seed)
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        var oracle = new JoinIndexQueryOracle(h);
        var random = new Random(seed);

        for (int round = 0; round < 8; round++)
        {
            h.RandomRound(random, round == 0 ? 80 : 14);
            if (round % 4 == 3)
                await h.CleanTombstonesAsync();

            await h.AssertCurrentAsync();
            for (int i = 0; i < 9; i++)
                oracle.Check(LeftJoin(Subs[random.Next(Subs.Length)], Tails[random.Next(Tails.Length)]));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }
}
