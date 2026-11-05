using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Corax;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Documents.Indexes;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Compat.CompatDb;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>Vector search with a join, two morelikethis() in a joined query and a join index whose index is not a map index.</summary>
public class JoinCompatRefusalTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string FromGames = "from index 'Games' as g";

    private sealed class GamesWithVectors : AbstractIndexCreationTask<CompatGame>
    {
        public GamesWithVectors()
        {
            Map = games => from g in games select new { g.OwnerId, g.Rank, Vec = CreateVector(g.Embedding) };
        }

        public override string IndexName => "GamesWithVectors";
    }

    private sealed class GamesByCountry : AbstractIndexCreationTask<CompatGame, GamesByCountry.Result>
    {
        public sealed class Result
        {
            public string Country { get; set; }
            public long Count { get; set; }
        }

        public GamesByCountry()
        {
            Map = games => from g in games select new Result { Country = g.Country, Count = 1 };
            Reduce = results => from r in results group r by r.Country into g select new Result { Country = g.Key, Count = g.Sum(x => x.Count) };
        }

        public override string IndexName => "GamesByCountry";
    }

    private static float[] Embedding(int i) => [(float)Math.Cos(i / 2.0), (float)Math.Sin(i / 2.0), 0.1f, 0.2f];

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(CompatMode.None)]
    [InlineData(CompatMode.Expanded)]
    public async Task VectorSearchOnTheOuterIndexGivesTheRowsOfTheQueryWithoutTheJoin(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode);
        using (var session = db.Store.OpenSession())
        {
            for (int i = 0; i < 12; i++)
            {
                var game = session.Load<CompatGame>($"games/{i:D3}");
                game.Embedding = Embedding(i);
            }

            session.SaveChanges();
        }

        await new GamesWithVectors().ExecuteAsync(db.Store);
        if (mode != CompatMode.None)
        {
            IndexDefinition joinIndex = CompatDb.JoinDefinition("GamesWithVectors/WithVipOwner");
            joinIndex.Join.OuterIndex = "GamesWithVectors";
            await db.Store.Maintenance.SendAsync(new PutIndexesOperation(joinIndex));
        }

        Indexes.WaitForIndexing(db.Store, timeout: TimeSpan.FromSeconds(60));

        const string joined = "from index 'GamesWithVectors' as g join (from index 'Users' where Kind = 'vip') as u on g.OwnerId = id(u) where vector.search(g.Vec, $v) order by id() limit 0, 100";
        const string oracle = "from index 'GamesWithVectors' as g where g.OwnerId in ('users/strong', 'users/weak') and vector.search(g.Vec, $v) order by id() limit 0, 100";

        List<string> Ids(string rql, float[] vector)
        {
            using IDocumentSession session = db.Store.OpenSession(new SessionOptions { NoCaching = true });
            return session.Advanced.RawQuery<CompatGame>(rql).AddParameter("v", vector).ToList().Select(x => session.Advanced.GetDocumentId(x)).ToList();
        }

        int nonEmpty = 0;
        foreach (int probe in new[] { 0, 3, 7, 11 })
        {
            List<string> expected = Ids(oracle, Embedding(probe));
            List<string> actual = Ids(joined, Embedding(probe));
            output.WriteLine($"{mode} probe {probe}: join {string.Join(",", actual)} | without the join {string.Join(",", expected)}");
            Assert.Equal(expected, actual);
            nonEmpty += expected.Count > 0 ? 1 : 0;
        }

        Assert.True(nonEmpty >= 2, "the vector search has to match some of the games, or the test proves nothing");

        if (mode != CompatMode.None)
        {
            JoinIndexStats stats = db.Store.Maintenance.Send(new GetIndexStatisticsOperation("GamesWithVectors/WithVipOwner")).Join;
            JoinTestHelpers.AppendReport("COMPAT_REPORT", "join-compat-report.txt", $"T5 vector {mode}: served {stats.Served}, fallbacks {string.Join(",", stats.Fallbacks)}");
            Assert.Equal(4, stats.Served + stats.FallbackTotal);
        }
    }

    [RavenFact(RavenTestCategory.Querying)]
    public async Task TwoMoreLikeThisInAJoinedQueryAreRefusedWithTheirMessage()
    {
        using CompatDb db = await CompatDb.CreateAsync(this, CompatMode.None);
        const string options = "'{\"Fields\":[\"Name\"],\"MinimumTermFrequency\":1,\"MinimumDocumentFrequency\":1}'";
        string one = $"{FromGames} {VipJoin} where morelikethis(id() = 'games/000', {options})";
        string two = $"{FromGames} {VipJoin} where morelikethis(id() = 'games/000', {options}) or morelikethis(id() = 'games/001', {options})";

        Assert.NotEmpty(db.Run(one).Ids);
        Exception e = Assert.ThrowsAny<Exception>(() => db.Run(two));
        Assert.True(e.ToString().Contains("A query that has a JOIN can contain only one morelikethis()."), e.ToString());
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinOfAMapReduceIndexIsRefusedByThePutOfTheServer()
    {
        using CompatDb db = await CompatDb.CreateAsync(this, CompatMode.None);
        await new GamesByCountry().ExecuteAsync(db.Store);

        foreach (bool asOuter in new[] { true, false })
        {
            IndexDefinition joinIndex = new()
            {
                Name = "MapReduceJoin",
                Join = asOuter
                    ? new JoinDefinition { OuterIndex = "GamesByCountry", OuterField = "Country", InnerIndex = Users }.AddField("Kind")
                    : new JoinDefinition { OuterIndex = Games, OuterField = "OwnerId", InnerIndex = "GamesByCountry" }.AddField("Country")
            };

            IndexInvalidException e = await Assert.ThrowsAnyAsync<IndexInvalidException>(() => db.Store.Maintenance.SendAsync(new PutIndexesOperation(joinIndex)));
            Assert.Contains("Could not create the join index 'MapReduceJoin'", e.Message);
            Assert.Contains("a join index needs a map index", e.Message);
        }

        Assert.DoesNotContain("MapReduceJoin", db.Store.Maintenance.Send(new GetIndexNamesOperation(0, 100)));
    }
}
