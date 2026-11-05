using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using Raven.Server.Documents.Indexes.Joins.Serving;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>
/// Ranges, prefixes, text and OR on the declared fields give the documents of the join different scores, so a join index answers them only when nothing uses the score of the join (the query is
/// ordered by fields of the main documents). The answers equal the join of the server.
/// </summary>
public class JoinIndexUnscoredKindQueryTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static readonly string[] Subs =
    [
        " where Rank > 20",
        " where Rank >= 20 and Rank < 30",
        " where Rank between 5 and 15",
        " where Rank <= 0",
        " where Rank > 100",
        " where Rank < 7 or Rank > 45",
        " where Name >= 'n2'",
        " where Name > 'n2'",
        " where Name between 'n1' and 'n3'",
        " where Name < 'n0'",
        " where startsWith(Name, 'n')",
        " where startsWith(Name, 'n1')",
        " where startsWith(Name, 'zz')",
        " where startsWith(Country, 'c')",
        " where startsWith(Country, 'C')",
        " where search(Title, 'w1')",
        " where Title = 'w2'",
        " where Title = 'w1' or Title = 'w3'",
        " where Country = 'nl' or Country = 'c1'",
        " where Country in ('nl', 'c2', 'nobody')",
        " where Country = 'nl' or Rank > 40",
        " where Country != 'nl' and Name != 'n1'",
        " where Rank > 10 and IsActive = true",
        " where Country != 'nl' and Rank < 25",
        " where Country = 'c2' and (Name = 'n1' or Name = 'n2')",
        " where Rank = 10 or IsActive = true"
    ];

    private static readonly string[] OrderedTails =
    [
        " order by m.Title limit 0, 10",
        " order by m.Title desc limit 3, 7",
        " where m.Title = 'a' order by m.Title limit 0, 1000",
        " order by m.Title limit 1000, 5",
        " where m.Title = 'b' order by m.ContextId desc limit 0, 20"
    ];

    private static readonly string[] ScoredTails =
    [
        " limit 0, 1000",
        " order by score() limit 0, 10",
        " boost 2 limit 0, 10"
    ];

    private static string LeftJoin(string sub, string tail = "") => $"{Outer} left join (from index 'Contexts/Search'{sub}) as c on m.ContextId = id(c){tail}";

    private static Dictionary<string, string> ScoreSettings() => new() { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };

    private async Task<JoinIndexHarness> HarnessWithDataAsync(int seed, int operations = 200)
    {
        JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        h.RandomRound(new Random(seed), operations);
        await h.AssertCurrentAsync();
        return h;
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task KindsWithDifferentScoresAreServedWhenTheQueryHasAnExplicitOrder()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(1);
        var oracle = new JoinIndexQueryOracle(h);

        foreach (string sub in Subs)
        {
            foreach (string tail in OrderedTails)
            {
                oracle.Check(Join(sub, tail));
                oracle.Check(LeftJoin(sub, tail));
            }
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData("", "", "", false)]
    [InlineData("", " order by score()", "", false)]
    [InlineData("", " order by m.Title", "", true)]
    [InlineData(" where m.Title = 'a'", " order by m.Title", "", true)]
    [InlineData(" where m.Title = 'a'", " order by m.Title", " boost 2.5", true)]
    [InlineData(" where boost(m.Title = 'a', 3)", " order by m.Title", "", false)]
    [InlineData(" where m.Title = 'a'", " order by m.Title, score()", "", false)]
    public async Task TheScoreOfTheJoinMattersOnlyWhenTheQueryCanUseIt(string where, string order, string boost, bool served)
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(3);
        var oracle = new JoinIndexQueryOracle(h);

        oracle.Check(Join(" where Rank > 20", $"{boost}{where}{order} limit 0, 10"), expectServed: served);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheSameKindsAreReadByTheServerWhenTheScoreIsUsed()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(2);
        var oracle = new JoinIndexQueryOracle(h);

        foreach (string sub in new[] { Subs[0], Subs[10], Subs[15], Subs[18], Subs[19] })
        {
            foreach (string tail in ScoredTails)
            {
                oracle.Check(Join(sub, tail), expectServed: false);
                oracle.Check(LeftJoin(sub, tail), expectServed: false);
            }
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task BoundariesOfRangesAndPrefixesAreExact()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings());
        var oracle = new JoinIndexQueryOracle(h);

        string[] names = ["Alpha", "Alphabet", "Beta", "alpha", "Bz", "B"];
        int[] ranks = [-5, 0, 9, 10, 11, 20, 30, 31, 100, 1000, 10000];
        for (int i = 0; i < ranks.Length; i++)
        {
            h.PutContext(i, i % 2 == 0 ? "nl" : "de", names[i % names.Length], ranks[i], true, "red");
            h.PutConversation(i, $"contexts/{i}", "a");
            h.PutConversation(100 + i, $"contexts/{i}", "b");
        }

        await h.AssertCurrentAsync();

        string[] subs =
        [
            " where Rank > 10", " where Rank >= 10", " where Rank < 10", " where Rank <= 10", " where Rank > -5", " where Rank >= -5", " where Rank < 1000", " where Rank <= 1000",
            " where Rank between 10 and 30", " where Rank between 11 and 29", " where Rank > 10 and Rank < 31", " where Rank >= 9 and Rank <= 11", " where Rank < 0", " where Rank > 10000", " where Rank >= 10000",
            " where Name > 'Alpha'", " where Name >= 'Alpha'", " where Name < 'Beta'", " where Name <= 'Beta'", " where Name between 'Alpha' and 'Alphabet'", " where Name between 'B' and 'Bz'",
            " where startsWith(Name, 'Alpha')", " where startsWith(Name, 'B')", " where startsWith(Name, 'Alphabet')", " where startsWith(Name, 'alpha')", " where startsWith(Name, '')"
        ];

        foreach (string sub in subs)
        {
            oracle.Check(Join(sub, " order by m.Title, m.ContextId limit 0, 100"));
            oracle.Check(LeftJoin(sub, " order by m.Title, m.ContextId limit 0, 100"));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ConditionsOnTheAliasThatAreOrdOrNegatedAreServedWhenOrdered()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(5);
        var oracle = new JoinIndexQueryOracle(h);

        string[] conditions =
        [
            "c.Country = 'nl' or c.Rank > 40",
            "m.Title != 'zzz' and not (c.Country = 'nl' or c.Rank > 40)",
            "c.Rank between 10 and 20 or m.Title = 'a'",
            "startsWith(c.Name, 'n1') or m.Title = 'b'",
            "m.Title != 'zzz' and not (c.Rank > 20)"
        ];

        foreach (string condition in conditions)
        {
            oracle.Check(Join("", $" where {condition} order by m.Title limit 0, 50"));
            oracle.Check(LeftJoin("", $" where {condition} order by m.Title limit 0, 50"));
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task ReasonsOfTheKindsThatAreStillReadByTheServerAreCounted()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(3);
        var oracle = new JoinIndexQueryOracle(h);

        var notServed = new Dictionary<string, string>
        {
            [Join(" where Rank > 20.5", " order by m.Title limit 0, 20")] = "KindNotServed:ValueType",
            [Join(" where search(Title, 'w1 w2')", " order by m.Title limit 0, 20")] = "KindNotServed:Method:search",
            [Join(" where startsWith(Title, 'w')", " order by m.Title limit 0, 20")] = "KindNotServed:Method:startsWith",
            [Join(" where Title > 'a'", " order by m.Title limit 0, 20")] = "KindNotServed:UndeclaredField:Title",
            [Join(" where exists(Name)", " order by m.Title limit 0, 20")] = "KindNotServed:Method:exists"
        };

        foreach ((string rql, string reason) in notServed)
        {
            long before = oracle.Info.Fallbacks.GetValueOrDefault(reason);
            long served = oracle.Info.Served;
            (QueryAnswer a, QueryAnswer b) = oracle.Both(rql);
            AssertSame(rql, a, b);
            Assert.Equal(served, oracle.Info.Served);
            Assert.True(oracle.Info.Fallbacks.GetValueOrDefault(reason) > before, $"{reason} was not counted for {rql}: {string.Join(", ", oracle.Info.Fallbacks)}");
        }
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task LeftJoinThatNothingScoresIsServedWhateverTheSubQueryIs()
    {
        using JoinIndexHarness h = await HarnessWithDataAsync(4);
        var oracle = new JoinIndexQueryOracle(h);

        foreach (string sub in new[] { " where search(Title, 'w1 w2')", " where Title > 'a'", " where Rank > 20.5", " where Country = 'nl' or Rank > 3", " where startsWith(Title, 'w')", "" })
            oracle.Check(LeftJoin(sub, " order by m.Title limit 0, 20"));
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(12)]
    public async Task RandomWorkloadServedUnscoredKindsEqualTheJoinOfTheServer(int seed)
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
            {
                string sub = Subs[random.Next(Subs.Length)];
                string tail = OrderedTails[random.Next(OrderedTails.Length)];
                oracle.Check(random.Next(3) == 0 ? LeftJoin(sub, tail) : Join(sub, tail));
            }
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }
}
