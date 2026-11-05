using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents.Indexes;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Compat.CompatDb;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>
/// The sub-queries that a join index never serves (limit, offset, group by, distinct, filter, load, a method that is not an atom) and a join of an index with itself: with a join index present the answer is the one of a database
/// that has none, and the fallback of the join index names the shape.
/// </summary>
public class JoinNeverServedSubQueryTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Games = "from index 'Games' as g";

    private static string Join(string sub) => $"{Games} join (from index 'Users'{sub}) as u on g.OwnerId = id(u) order by id() limit 0, 1000";

    // reason: the fallback of the join index; null: the join index serves it; "parse": the query language does not have the shape in a sub-query, the join index is never asked
    public static IEnumerable<object[]> Shapes() =>
    [
        ["limit", " where Kind = 'vip' limit 0, 1", "parse:cannot use LIMIT or OFFSET"],
        ["limit and offset", " where Kind = 'vip' limit 1, 1", "parse:cannot use LIMIT or OFFSET"],
        ["limit 0", " where Kind = 'vip' limit 0, 0", "parse:cannot use LIMIT or OFFSET"],
        ["offset keyword", " where Kind = 'vip' limit 1 offset 1", "parse:cannot use LIMIT or OFFSET"],
        ["group by", " where Kind = 'vip' group by Kind select Kind", "parse:Expected ')' at the end of the JOIN sub-query"],
        ["distinct", " where Kind = 'vip' select distinct Kind", "KindNotServed:SubQueryDistinct"],
        ["filter", " where Kind = 'vip' filter Number > 1", "KindNotServed:SubQueryFilter"],
        ["load", " as x where Kind = 'vip' load x.Name as n", "KindNotServed:SubQueryLoad"],
        ["include", " where Kind = 'vip' include Name", "KindNotServed:SubQueryLoad"],
        ["spatial", " where spatial.within(Location, spatial.circle(5, 52.5, 5))", "KindNotServed:Method:spatial.within"],
        ["spatial and field", " where Kind = 'vip' and spatial.within(Location, spatial.circle(5, 52.5, 5))", "KindNotServed:Method:spatial.within"],
        ["search", " where search(Kind, 'vip')", "KindNotServed:Method:search"],
        ["field that is not declared", " where Name = 'chris'", "KindNotServed:UndeclaredField:Name"],
        ["or of two declared fields", " where Kind = 'vip' or Number = 3", null],
        ["order by is ignored", " where Kind = 'vip' order by Number desc", null],
    ];

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [MemberData(nameof(Shapes))]
    public async Task AJoinIsPresentAndTheShapeIsReadByTheServerWithTheSameAnswerAsWithoutAJoin(string name, string sub, string reason)
    {
        using CompatDb served = await CompatDb.CreateAsync(this, CompatMode.Expanded);
        using CompatDb none = await CompatDb.CreateAsync(this, CompatMode.None);
        string rql = Join(sub);
        var before = served.Snapshot();

        if (reason?.StartsWith("parse:", StringComparison.Ordinal) == true)
        {
            // the server refuses the shape with or without a join index, and a join index is never asked
            string text = reason.Substring("parse:".Length);
            Exception expectedError = Assert.ThrowsAny<Exception>(() => none.Run(rql));
            Exception actualError = Assert.ThrowsAny<Exception>(() => served.Run(rql));
            Assert.Equal(expectedError.GetType(), actualError.GetType());
            Assert.Contains(text, actualError.Message);
            Assert.Equal(expectedError.Message.Split('\n')[0], actualError.Message.Split('\n')[0]);
            Outcome refused = Since(before, served.Snapshot());
            Assert.True(refused.Served == 0 && refused.Fallbacks == 0, refused.Describe());
            return;
        }

        (List<string> Ids, long Total) expected = none.Run(rql);
        (List<string> Ids, long Total) actual = served.Run(rql);
        Assert.NotEmpty(expected.Ids);
        Assert.Equal(expected.Ids, actual.Ids);
        Assert.Equal(expected.Total, actual.Total);

        Outcome outcome = Since(before, served.Snapshot());
        if (reason == null)
        {
            Assert.True(outcome.Served == 1 && outcome.Fallbacks == 0, $"{name}: {outcome.Describe()}");
            return;
        }

        Assert.True(outcome.Served == 0 && outcome.Fallbacks == 1 && outcome.Reasons.GetValueOrDefault(reason) == 1, $"{name} should be read by the server because of {reason}: {outcome.Describe()}");
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(CompatMode.Expanded)]
    [InlineData(CompatMode.None)]
    public async Task AnIndexJoinedWithItselfIsTheJoinOfTheServerAndNoJoinIsMadeForIt(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode);
        const string selfJoin = "from index 'Games' as g join (from index 'Games' where Rank > 3) as h on g.RelatedId = id(h) order by id() limit 0, 1000";
        var expected = new[] { "games/003", "games/004", "games/005", "games/006", "games/007", "games/008", "games/009", "games/010" };

        (List<string> ids, long total) = db.Run(selfJoin);
        Assert.Equal(expected, ids);
        Assert.Equal(expected.Length, total);
        (ids, _) = db.Run(selfJoin);
        Assert.Equal(expected, ids);
        Assert.Equal(expected, db.Run("from index 'Games' as g where g.RelatedId in ('games/004', 'games/005', 'games/006', 'games/007', 'games/008', 'games/009', 'games/010', 'games/011') order by id() limit 0, 1000").Ids);

        if (mode == CompatMode.Expanded)
        {
            // the join index of the games and the users is not asked about the games and the games
            JoinIndexStats joinIndex = db.Join;
            Assert.Equal(0, joinIndex.Served);
            Assert.Equal(0, joinIndex.FallbackTotal);
        }
    }
}
