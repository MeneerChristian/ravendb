using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Corax;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Corax.JoinRqlFeatureMatrix;
using static FastTests.Server.Documents.Indexing.Joins.Compat.CompatDb;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>
/// The RQL feature matrix of the join (<see cref="JoinRqlFeatureMatrix"/>) with a join index that is asked to serve the join: every feature gives the rows of the query without the join, and the join index
/// either served the join or counted the reason why it did not. The join index is one that is put (storage mode Expanded).
/// </summary>
public class JoinIndexServedRqlMatrixTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Games = "from index 'Games' as g";

    private sealed record CaseOutcome(string Name, Outcome Join);

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(CompatMode.Expanded, "where")]
    [InlineData(CompatMode.Expanded, "order")]
    [InlineData(CompatMode.Expanded, "projection")]
    [InlineData(CompatMode.Expanded, "paging")]
    [InlineData(CompatMode.Expanded, "include")]
    public async Task EveryFeatureGivesTheSameRowsAsTheQueryWithoutTheJoinAndTheJoinIsAsked(CompatMode mode, string group)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode);
        await db.WaitForTheJoinAsync();
        Case[] cases = group switch { "where" => WhereCases, "order" => OrderCases, "projection" => ProjectionCases, "include" => IncludeCases, _ => PagingCases };

        var problems = new List<string>();
        var outcomes = new List<CaseOutcome>();
        foreach (Case c in cases)
        {
            var before = db.Snapshot();
            string problem = Run(db, c);
            if (problem != null)
                problems.Add($"[{mode} {group}] {c.Name}: {problem}");

            outcomes.Add(new CaseOutcome(c.Name, Since(before, db.Snapshot())));
        }

        foreach (CaseOutcome outcome in outcomes)
        {
            output.WriteLine($"{mode} {group} | {outcome.Name} | {outcome.Join.Describe()}");
            JoinTestHelpers.AppendReport("COMPAT_REPORT", "join-compat-report.txt", $"{mode} {group} | {outcome.Name} | {outcome.Join.Describe()}");
        }

        Assert.True(problems.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, problems));

        // every feature of the matrix is a join that the join index serves: one join, served, and no fallback
        foreach ((Case c, CaseOutcome outcome) in cases.Zip(outcomes))
        {
            if (c.Unsupported)
                continue;

            Assert.True(outcome.Join.Served == 1 && outcome.Join.Fallbacks == 0, $"[{mode} {group}] {c.Name}: the join index did not serve the join: {outcome.Join.Describe()}");
        }
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(CompatMode.Expanded)]
    public async Task TheCoreShapesOfTheMatrixAreServedByTheJoin(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode);
        await db.WaitForTheJoinAsync();

        var servedShapes = new[]
        {
            "where g.Rank > 3",
            "where g.Country = 'nl'",
            "where g.Rank > 3 order by g.Rank as long desc",
            "order by id()",
            "where g.Rank in (1, 2, 3, 9) limit 1, 2",
            "where exists(g.Country)",
            "where search(g.Name, 'zelda')",
            "where search(g.Name, 'zelda') order by score() limit 0, 1000",
            "where g.Rank > 1000"
        };

        foreach (string tail in servedShapes)
        {
            string rql = $"{Games} {VipJoin} {tail}";
            var before = db.Snapshot();
            (List<string> ids, long total) = db.Run(rql);
            var outcome = Since(before, db.Snapshot());
            (List<string> expectedIds, long expectedTotal) = db.Run($"{Games} where {VipOwners} {(tail.StartsWith("where ", StringComparison.Ordinal) ? "and " + tail.Substring(6) : tail)}");
            Assert.True(total == expectedTotal && ids.OrderBy(x => x).SequenceEqual(expectedIds.OrderBy(x => x)), $"{rql}: {string.Join(",", ids)} instead of {string.Join(",", expectedIds)}");
            Assert.True(outcome.Served == 1, $"{rql} was not served: {outcome.Describe()}");
        }
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(CompatMode.Expanded)]
    public async Task StreamPatchAndDeleteByQueryOnlyTouchTheDocumentsOfTheJoinWhenAJoinServesIt(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode);
        await db.WaitForTheJoinAsync();
        var expected = new[] { "games/000", "games/001", "games/004", "games/005", "games/008", "games/009" };

        {
            var before = db.Snapshot();
            using var session = db.Store.OpenSession();
            using var stream = session.Advanced.Stream(session.Advanced.RawQuery<CompatGame>($"{Games} {VipJoin} order by g.Rank as long"));
            var ids = new List<string>();
            while (stream.MoveNext())
                ids.Add(stream.Current.Id);

            Assert.Equal(expected, ids);
            Assert.Equal(1, Since(before, db.Snapshot()).Served);
        }

        {
            var before = db.Snapshot();
            db.Store.Operations.Send(new PatchByQueryOperation($"{Games} {VipJoin} update {{ this.Patched = true; }}")).WaitForCompletion();
            Assert.True(Since(before, db.Snapshot()).Served >= 1, "the patch by query was not served");
            using var session = db.Store.OpenSession();
            var patched = session.Advanced.RawQuery<CompatGame>("from 'CompatGames' where Patched = true").ToList().Select(x => x.Id).OrderBy(x => x).ToList();
            Assert.Equal(expected, patched);
        }

        {
            var before = db.Snapshot();
            db.Store.Operations.Send(new DeleteByQueryOperation($"{Games} {VipJoin}")).WaitForCompletion();
            Assert.True(Since(before, db.Snapshot()).Served >= 1, "the delete by query was not served");
            using var session = db.Store.OpenSession();
            var left = session.Advanced.RawQuery<CompatGame>("from 'CompatGames'").ToList().Select(x => x.Id).OrderBy(x => x).ToList();
            Assert.Equal(Enumerable.Range(0, 12).Select(i => $"games/{i:D3}").Except(expected).ToList(), left);
        }

        // what the join index follows is the delete: it is current and the next join is served and empty of the deleted games
        await db.WaitForTheJoinAsync();
        var afterDelete = db.Snapshot();
        Assert.Empty(db.Run($"{Games} {VipJoin}").Ids);
        Assert.Equal(1, Since(afterDelete, db.Snapshot()).Served);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(CompatMode.Expanded)]
    public async Task IncludedDocumentsAreLoadedWithTheRowsOfAServedJoin(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode);
        await db.WaitForTheJoinAsync();
        var before = db.Snapshot();
        AssertIncludesAreLoaded(db.Store, VipJoin);
        Assert.True(Since(before, db.Snapshot()).Served >= 1, "the join was not served");
    }

    /// <summary>The documents of an include are in the session after the query of the join, so reading them takes no request.</summary>
    public static void AssertIncludesAreLoaded(IDocumentStore store, string join)
    {
        using IDocumentSession session = store.OpenSession();
        List<CompatGame> games = session.Advanced.RawQuery<CompatGame>($"from index 'Games' as g {join} order by g.Rank as long include g.OwnerId, g.PublisherId").ToList();
        Assert.Equal(6, games.Count);
        int requests = session.Advanced.NumberOfRequests;
        foreach (CompatGame game in games)
        {
            Assert.True(session.Advanced.IsLoaded(game.OwnerId), $"{game.OwnerId} of {game.Id} was not included");
            Assert.True(session.Advanced.IsLoaded(game.PublisherId), $"{game.PublisherId} of {game.Id} was not included");
            Assert.NotNull(session.Load<CompatUser>(game.OwnerId));
            Assert.NotNull(session.Load<CompatPublisher>(game.PublisherId));
        }

        Assert.Equal(requests, session.Advanced.NumberOfRequests);
    }

    private static string Run(CompatDb db, Case c)
    {
        string Rql(bool join)
        {
            string where;
            if (join)
                where = c.Where == null ? "" : $"where {c.Where}";
            else
                where = c.Where == null ? $"where {VipOwners}" : $"where ({c.Where}) and {VipOwners}";

            return string.Join(" ", new[] { c.Declare, Games, join ? VipJoin : null, c.GroupBy, where, c.Order, c.Load, c.Filter, c.Select, c.Limit, c.Include }
                .Where(x => string.IsNullOrEmpty(x) == false));
        }

        if (c.Unsupported)
        {
            Exception oracleError = null, joinError = null;
            try { Execute(db.Store, Rql(false), c.Entities); } catch (Exception e) { oracleError = e; }
            try { Execute(db.Store, Rql(true), c.Entities); } catch (Exception e) { joinError = e; }
            if (oracleError == null)
                return "EXPECTED THE FEATURE TO BE UNSUPPORTED, but the query without the join worked: " + Rql(false);

            if (joinError == null || joinError.GetType() != oracleError.GetType())
                return $"the join gives {joinError?.GetType().Name ?? "no error"} where the query without it gives {oracleError.GetType().Name}: {Rql(true)}";

            return null;
        }

        (List<string> Keys, long Total) expected;
        try
        {
            expected = Execute(db.Store, Rql(false), c.Entities);
        }
        catch (Exception e)
        {
            return "THE ORACLE (no join) THROWS: " + First(e) + " in " + Rql(false);
        }

        (List<string> Keys, long Total) actual;
        try
        {
            actual = Execute(db.Store, Rql(true), c.Entities);
        }
        catch (Exception e)
        {
            return "THE JOIN THROWS: " + First(e) + " in " + Rql(true);
        }

        if (c.Mode == JoinRqlFeatureMatrix.Mode.Count)
        {
            return actual.Keys.Count == expected.Keys.Count && actual.Total == expected.Total
                ? null
                : $"count {actual.Keys.Count}/{actual.Total} instead of {expected.Keys.Count}/{expected.Total} in {Rql(true)}";
        }

        List<string> a = c.Mode == JoinRqlFeatureMatrix.Mode.Ordered ? actual.Keys : actual.Keys.OrderBy(x => x).ToList();
        List<string> e2 = c.Mode == JoinRqlFeatureMatrix.Mode.Ordered ? expected.Keys : expected.Keys.OrderBy(x => x).ToList();
        if (a.SequenceEqual(e2) == false)
            return $"ROWS DIFFER in {Rql(true)}{Environment.NewLine}   join:   {string.Join(" ; ", a)}{Environment.NewLine}   oracle: {string.Join(" ; ", e2)}";

        if (actual.Total != expected.Total)
            return $"total {actual.Total} instead of {expected.Total} in {Rql(true)}";

        return null;
    }

    private static string First(Exception e) => e.Message.Split('\n')[0].Trim();

    private static (List<string> Keys, long Total) Execute(IDocumentStore store, string rql, bool entities)
    {
        using IDocumentSession session = store.OpenSession();
        if (entities)
        {
            List<CompatGame> games = session.Advanced.RawQuery<CompatGame>(rql).Statistics(out QueryStatistics stats).ToList();
            return (games.Select(x => (string)session.Advanced.GetMetadataFor(x)[Constants.Documents.Metadata.Id]).ToList(), stats.TotalResults);
        }

        List<Projected> rows = session.Advanced.RawQuery<Projected>(rql).Statistics(out QueryStatistics projectionStats).ToList();
        return (rows.Select(x => $"{x.Name}|{x.Rank}|{x.Country}|{x.GameName}|{x.Position}|{x.DocId}|{x.Title}|{x.Place}|{x.Shout}|{x.Publisher}|{x.Count}|{x.Total}|{x.OwnerId}").ToList(), projectionStats.TotalResults);
    }

    private sealed class Projected
    {
        public string Name { get; set; }
        public long? Rank { get; set; }
        public string Country { get; set; }
        public string GameName { get; set; }
        public long? Position { get; set; }
        public string DocId { get; set; }
        public string Title { get; set; }
        public long? Place { get; set; }
        public string Shout { get; set; }
        public string Publisher { get; set; }
        public long? Count { get; set; }
        public long? Total { get; set; }
        public string OwnerId { get; set; }
    }
}
