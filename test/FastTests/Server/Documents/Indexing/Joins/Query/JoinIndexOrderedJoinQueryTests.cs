using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>A join that orders by a declared Exact field of the joined index is answered from the terms of that field: the same rows, order, total and @index-score as the rank of the server.</summary>
public class JoinIndexOrderedJoinQueryTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static readonly string[] Tails =
    [
        " order by c.Name limit 0, 10",
        " order by c.Name limit 0, 1000",
        " order by c.Name desc limit 3, 7",
        " order by c.Name limit 1000, 5",
        " where exists(m.Title) order by c.Name limit 0, 20",
        " where m.Title = 'a' order by c.Name limit 0, 20",
        " where m.Title = 'a' order by c.Name desc limit 5, 20",
        " where m.Title = 'a' or m.Title = 'b' order by c.Name limit 0, 15",
        " where m.Title = 'nothing' order by c.Name limit 0, 15",
        " boost 2 where m.Title = 'b' order by c.Name limit 0, 15"
    ];

    private static Dictionary<string, string> ScoreSettings() => new() { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };

    private async Task<JoinIndexHarness> HarnessAsync(bool scores, int seed, int operations = 200)
    {
        JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, scores ? ScoreSettings() : null);
        h.RandomRound(new Random(seed), operations);
        await h.AssertCurrentAsync();
        return h;
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrderByAFieldOfTheJoinedIndexIsServedFromItsTerms(bool scores)
    {
        using JoinIndexHarness h = await HarnessAsync(scores, 1);
        var oracle = new JoinIndexQueryOracle(h);

        foreach (string tail in Tails)
            oracle.Check(Join("", tail));

        Assert.Equal(0, oracle.Info.FallbackTotal);
        Assert.Equal(Tails.Length, oracle.Info.Served);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EntriesOfOneNameComeInTheOrderOfTheirEntries(bool scores)
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, scores ? ScoreSettings() : null);
        var oracle = new JoinIndexQueryOracle(h);

        // three contexts share a name, one has none that is shared; conversations are written interleaved
        h.PutContext(1, "nl", "same", 1, true, "x");
        h.PutContext(2, "nl", "same", 2, true, "x");
        h.PutContext(3, "nl", "same", 3, true, "x");
        h.PutContext(4, "nl", "aaa", 4, true, "x");
        h.PutContext(5, "nl", "zzz", 5, true, "x");
        for (int i = 0; i < 30; i++)
            h.PutConversation(i, $"contexts/{1 + i % 5}", i % 2 == 0 ? "a" : "b");

        h.PutConversation(100, "contexts/missing");
        h.PutConversation(101, null);
        await h.AssertCurrentAsync();

        foreach (string tail in Tails.Concat([" order by c.Name limit 0, 100", " order by c.Name desc limit 0, 100", " order by c.Name limit 29, 5", " order by c.Name desc limit 29, 5"]))
            oracle.Check(Join("", tail));

        // a joined document that is deleted and one that comes later
        h.Delete("contexts/4");
        h.PutContext(6, "nl", "bbb", 6, true, "x");
        h.PutConversation(102, "contexts/6");
        await h.AssertCurrentAsync();
        foreach (string tail in Tails)
            oracle.Check(Join("", tail));

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TotalOfAWhereClauseWhenEveryEntryHasAJoinedDocument(bool scores)
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, scores ? ScoreSettings() : null);
        var oracle = new JoinIndexQueryOracle(h);

        for (int i = 0; i < 6; i++)
            h.PutContext(i, "nl", i % 3 == 0 ? "x" : "name" + i, i, true, "x");

        for (int i = 0; i < 40; i++)
            h.PutConversation(i, $"contexts/{i % 6}", i % 4 == 0 ? "a" : "b");

        await h.AssertCurrentAsync();
        foreach (string tail in Tails)
            oracle.Check(Join("", tail));

        // one entry loses its joined document, then every entry has one again
        h.PutConversation(7, "contexts/missing", "a");
        await h.AssertCurrentAsync();
        foreach (string tail in Tails)
            oracle.Check(Join("", tail));

        h.PutConversation(7, "contexts/2", "a");
        h.Delete("conversations/8");
        await h.AssertCurrentAsync();
        foreach (string tail in Tails)
            oracle.Check(Join("", tail));

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistsOfAFieldThatEveryEntryHasIsAnsweredWithoutReadingTheWhere(bool scores)
    {
        using JoinIndexHarness h = await HarnessAsync(scores, 3);
        var oracle = new JoinIndexQueryOracle(h);

        string[] everyEntry =
        [
            " where exists(m.Title) order by c.Name limit 0, 20",
            " where exists(m.Title) order by c.Name desc limit 7, 20",
            " where exists(m.Title) order by c.Name limit 0, 1000",
            " where exists(m.Title) order by c.Name limit 1000, 5",
            " where exists(m.ContextId) order by c.Name limit 0, 20"
        ];

        string[] readWhere =
        [
            " where m.Title = 'a' order by c.Name limit 0, 20",
            " where exists(m.Title) or m.Title = 'a' order by c.Name limit 0, 20"
        ];

        foreach (string tail in everyEntry)
        {
            Assert.Equal(0, oracle.Run(Join("", tail)).Counters.GetValueOrDefault("Join/c/JoinServedPageWhereRead", -1));
            oracle.Check(Join("", tail));
        }

        foreach (string tail in readWhere)
        {
            Assert.Equal(1, oracle.Run(Join("", tail)).Counters.GetValueOrDefault("Join/c/JoinServedPageWhereRead", -1));
            oracle.Check(Join("", tail));
        }

        // some entries have no value for the field: exists() does not hold every entry any more and is read
        h.Save(s =>
        {
            for (int i = 0; i < 5; i++)
            {
                object doc = new { Title = "no key" };
                s.Store(doc, $"conversations/{900 + i}");
                s.Advanced.GetMetadataFor(doc)[Raven.Client.Constants.Documents.Metadata.Collection] = "Conversations";
            }
        });

        await h.AssertCurrentAsync();
        foreach (string tail in everyEntry)
        {
            bool holdsEveryEntry = tail.Contains("m.Title");
            Assert.Equal(holdsEveryEntry ? 0 : 1, oracle.Run(Join("", tail)).Counters.GetValueOrDefault("Join/c/JoinServedPageWhereRead", -1));
            oracle.Check(Join("", tail));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task WhatTheJoinCannotAnswerInThatOrderIsReadByTheServer()
    {
        using JoinIndexHarness h = await HarnessAsync(true, 2);
        var oracle = new JoinIndexQueryOracle(h);

        var notServed = new Dictionary<string, string>
        {
            [Join(" where Country = 'nl'", " order by c.Name limit 0, 20")] = "KindNotServed:OrderedWhere",
            [Join("", " order by c.Rank limit 0, 20")] = "KindNotServed:OrderBy:Rank",
            [Join("", " order by c.Title limit 0, 20")] = "KindNotServed:OrderBy:Title",
            [Join("", " order by c.Name as alphanumeric limit 0, 20")] = "KindNotServed:OrderBy:Name"
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

        // left joins, two order items and pages too far are never offered
        oracle.Check($"{Outer} left join (from index 'Contexts/Search') as c on m.ContextId = id(c) order by c.Name limit 0, 20", expectServed: false);
        oracle.Check(Join("", " order by c.Name, m.Title limit 0, 20"), expectServed: false);
        oracle.Check(Join("", " order by c.Name limit 9995, 20"), expectServed: false);
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(12)]
    public async Task RandomWorkloadServedOrderedJoinEqualsTheRankedJoinOfTheServer(int seed)
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, seed % 2 == 0 ? ScoreSettings() : null);
        var oracle = new JoinIndexQueryOracle(h);
        var random = new Random(seed);

        for (int round = 0; round < 8; round++)
        {
            h.RandomRound(random, round == 0 ? 80 : 14);
            if (round % 4 == 3)
                await h.CleanTombstonesAsync();

            await h.AssertCurrentAsync();
            for (int i = 0; i < 9; i++)
                oracle.Check(Join("", Tails[random.Next(Tails.Length)]));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }
}
