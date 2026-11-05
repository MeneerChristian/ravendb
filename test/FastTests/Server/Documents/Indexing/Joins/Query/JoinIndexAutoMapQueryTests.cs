using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Exceptions;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

/// <summary>A join index over auto-map or JavaScript map indexes (named as a query names them) answers like the join of the server: rows, order, total and @index-score.</summary>
public class JoinIndexAutoMapQueryTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string AutoOuterPrefix = "Auto/Conversations/";
    private const string AutoInnerPrefix = "Auto/Contexts/";
    private const string AutoJoin = "AutoPairs";

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
        " where m.Title = 'a' order by score() limit 0, 5",
        " boost 2 order by score() limit 0, 20"
    ];

    private static Dictionary<string, string> ScoreSettings() => new()
    {
        [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString(),
        [RavenConfiguration.GetKey(x => x.Join.AutoEnabled)] = true.ToString()
    };

    private static string FindAuto(IDocumentStore store, string prefix)
    {
        return store.Maintenance.Send(new GetIndexNamesOperation(0, 100)).Single(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static IndexDefinition DefinitionOf(string outer, string inner, bool autoInner)
    {
        var joinIndex = new JoinDefinition { OuterIndex = outer, OuterField = "ContextId", InnerIndex = inner }
            .AddField("Country")
            .AddField("Name", JoinFieldKind.Exact)
            .AddField("Rank", JoinFieldKind.Long)
            .AddGrouping("IsActive", "true");

        if (autoInner == false)
            joinIndex.AddField("Title", JoinFieldKind.Text);

        return new IndexDefinition { Name = AutoJoin, Join = joinIndex };
    }

    private async Task<(JoinIndexHarness Harness, JoinIndexQueryOracle Oracle)> CreateAsync(bool autoOuter, bool autoInner)
    {
        var statics = new List<IndexDefinition>();
        if (autoOuter == false)
            statics.Add(JoinIndexHarness.OuterIndex());
        if (autoInner == false)
            statics.Add(JoinIndexHarness.InnerIndex());

        JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings(), putJoin: false, indexes: statics.ToArray());
        if (autoOuter)
        {
            using var session = h.Store.OpenSession();
            _ = session.Advanced.RawQuery<JoinIndexHarness.Conversation>("from Conversations where ContextId = 'x' and Title = 'x'").ToList();
        }

        if (autoInner)
        {
            using var session = h.Store.OpenSession();
            _ = session.Advanced.RawQuery<JoinIndexHarness.Context>("from Contexts where Country = 'x' and IsActive = true and Name = 'x' and Rank = 1").ToList();
        }

        string outer = autoOuter ? FindAuto(h.Store, AutoOuterPrefix) : JoinIndexHarness.OuterName;
        string inner = autoInner ? FindAuto(h.Store, AutoInnerPrefix) : JoinIndexHarness.InnerName;
        await h.PutJoinAsync(DefinitionOf(outer, inner, autoInner));
        return (h, new JoinIndexQueryOracle(h, AutoJoin, outer, inner));
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EveryServedKindEqualsTheJoinOfTheServer(bool autoOuter, bool autoInner)
    {
        (JoinIndexHarness h, JoinIndexQueryOracle oracle) = await CreateAsync(autoOuter, autoInner);
        using (h)
        {
            h.RandomRound(new Random(1), 200);
            await h.AssertCurrentAsync(AutoJoin);

            foreach (string sub in Subs)
            {
                foreach (string tail in Tails)
                    oracle.Check(oracle.JoinOf(sub, tail));
            }

            Assert.Equal(0, oracle.Info.FallbackTotal);
            Assert.Equal(Subs.Length * Tails.Length, oracle.Info.Served);
        }
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 3)]
    [InlineData(true, true, 7)]
    public async Task RandomWorkloadServedEqualsTheJoinOfTheServer(bool autoOuter, bool autoInner, int seed)
    {
        (JoinIndexHarness h, JoinIndexQueryOracle oracle) = await CreateAsync(autoOuter, autoInner);
        using (h)
        {
            var random = new Random(seed);
            for (int round = 0; round < 8; round++)
            {
                h.RandomRound(random, round == 0 ? 80 : 14);
                if (round % 4 == 3)
                    await h.CleanTombstonesAsync();

                await h.AssertCurrentAsync(AutoJoin);
                for (int i = 0; i < 9; i++)
                    oracle.Check(oracle.JoinOf(Subs[random.Next(Subs.Length)], Tails[random.Next(Tails.Length)]));
            }

            Assert.Equal(0, oracle.Info.FallbackTotal);
        }
    }

    private static IndexDefinition JsOuter() => new() { Name = JoinIndexHarness.OuterName, Maps = { "map('Conversations', c => ({ ContextId: c.ContextId, Title: c.Title }))" } };

    private static IndexDefinition JsInner()
    {
        return new IndexDefinition
        {
            Name = JoinIndexHarness.InnerName,
            Maps = { "map('Contexts', c => ({ Country: c.Country, Name: c.Name, Rank: c.Rank, IsActive: c.IsActive, Title: c.Title }))" },
            Fields =
            {
                ["Title"] = new IndexFieldOptions { Indexing = FieldIndexing.Search },
                ["Name"] = new IndexFieldOptions { Indexing = FieldIndexing.Exact }
            }
        };
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task JavaScriptMapIndexesServeLikeTheJoinOfTheServer(int seed)
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, ScoreSettings(), indexes: [JsOuter(), JsInner()]);
        var oracle = new JoinIndexQueryOracle(h);
        var random = new Random(seed);

        for (int round = 0; round < 6; round++)
        {
            h.RandomRound(random, round == 0 ? 80 : 14);
            if (round % 3 == 2)
                await h.CleanTombstonesAsync();

            await h.AssertCurrentAsync();
            foreach (string sub in Subs)
                oracle.Check(JoinIndexQueryOracle.Join(sub, Tails[random.Next(Tails.Length)]));
        }

        Assert.Equal(0, oracle.Info.FallbackTotal);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task JavaScriptMapIndexThatLoadsDocumentsIsRefused()
    {
        IndexDefinition loading = JsInner();
        loading.Maps.Clear();
        loading.Maps.Add("map('Contexts', c => { const o = load(c.Other, 'Contexts'); return { Country: c.Country, Name: c.Name, Rank: c.Rank, IsActive: c.IsActive, Title: o ? o.Title : c.Title }; })");

        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, putJoin: false, indexes: [JsOuter(), loading]);
        var exception = await Assert.ThrowsAnyAsync<RavenException>(() => h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition()));
        Assert.Contains("LoadDocument", exception.Message);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task JavaScriptMapIndexThatReturnsAnArrayHasNoFieldsToDeclare()
    {
        // the fields of a JavaScript map are the properties of the object it returns: those of an array are not known before documents are indexed
        IndexDefinition fanOut = JsInner();
        fanOut.Maps.Clear();
        fanOut.Maps.Add("map('Contexts', c => [{ Country: c.Country, Name: c.Name, Rank: c.Rank, IsActive: c.IsActive, Title: c.Title }, { Country: 'again', Name: c.Name, Rank: c.Rank, IsActive: c.IsActive, Title: c.Title }])");

        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this, putJoin: false, indexes: [JsOuter(), fanOut]);
        var exception = await Assert.ThrowsAnyAsync<RavenException>(() => h.PutJoinAsync(JoinIndexHarness.JoinIndexDefinition()));
        Assert.Contains("FieldNotIndexed", exception.Message);
    }
}
