using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Utils;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Sorters;
using Raven.Client.Documents.Queries.Sorting;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Commercial;
using Raven.Client.Exceptions.Corax;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Compat.CompatDb;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>include revisions(...) in a joined query, a custom sorter on the outer index, and the ways to order by a field of a join alias that are refused.</summary>
public class JoinCompatQueryFeatureTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Games = "from index 'Games' as g";

    private const string SorterCode = @"
using System;
using System.Collections.Generic;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;

namespace SlowTests.Data.RavenDB_8355
{
    public class NameSorter : FieldComparator
    {
        public NameSorter(string fieldName, int numHits, int sortPos, bool reversed, List<string> diagnostics) { }
        public override int Compare(int slot1, int slot2) { return 0; }
        public override void SetBottom(int slot) { }
        public override int CompareBottom(int doc, IState state) { return 0; }
        public override void Copy(int slot, int doc, IState state) { }
        public override void SetNextReader(IndexReader reader, int docBase, IState state) { }
        public override IComparable this[int slot] { get { return 0; } }
    }
}";

    private static async Task SetupRevisionsOrSkipAsync(IDocumentStore store)
    {
        try
        {
            await RevisionsHelper.SetupRevisionsAsync(store);
        }
        catch (LicenseLimitException e) when (e.Message.Contains("revisions", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip($"The license of the test server does not allow a default configuration for revisions: {e.Message}");
        }
    }

    [RavenTheory(RavenTestCategory.Indexes | RavenTestCategory.Querying | RavenTestCategory.Revisions)]
    [InlineData(CompatMode.None)]
    [InlineData(CompatMode.Expanded)]
    public async Task TheRevisionsThatAJoinedQueryIncludesAreLoadedWithItsRowsInOneRequest(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode, populate: false);
        await SetupRevisionsOrSkipAsync(db.Store);
        await db.PopulateAsync();

        // the first version of every game is what the revision of this moment is, then every game changes
        await Task.Delay(100);
        DateTime moment = DateTime.UtcNow;
        await Task.Delay(100);
        using (var session = db.Store.OpenSession())
        {
            for (int i = 0; i < 12; i++)
            {
                var game = session.Load<CompatGame>($"games/{i:D3}");
                game.Rank = 1000 + i;
            }

            session.SaveChanges();
        }

        Indexes.WaitForIndexing(db.Store);
        string join = $"{Games} {VipJoin} order by id() limit 0, 1000 include revisions($moment)";
        string oracle = $"{Games} where {VipOwners} order by id() limit 0, 1000 include revisions($moment)";
        string[] expected = ["games/000", "games/001", "games/004", "games/005", "games/008", "games/009"];

        var before = mode == CompatMode.Expanded ? db.Snapshot() : null;
        foreach (string rql in new[] { join, oracle })
        {
            using IDocumentSession session = db.Store.OpenSession(new SessionOptions { NoCaching = true });
            List<CompatGame> games = session.Advanced.RawQuery<CompatGame>(rql).AddParameter("moment", moment).WaitForNonStaleResults(TimeSpan.FromSeconds(30)).ToList();

            Assert.Equal(expected, games.Select(x => session.Advanced.GetDocumentId(x)).ToArray());
            Assert.All(games, x => Assert.True(x.Rank >= 1000, "the rows are the current versions"));
            Assert.Equal(1, session.Advanced.NumberOfRequests);
            foreach (CompatGame game in games)
            {
                string id = session.Advanced.GetDocumentId(game);
                CompatGame revision = session.Advanced.Revisions.Get<CompatGame>(id, moment);
                Assert.NotNull(revision);
                Assert.Equal(game.Rank - 1000, revision.Rank);
            }

            Assert.Equal(1, session.Advanced.NumberOfRequests);
        }

        if (mode == CompatMode.Expanded)
        {
            CompatDb.Outcome outcome = Since(before, db.Snapshot());
            Assert.True(outcome.Served >= 1, $"the join index served no join: {outcome.Describe()}");
        }
    }

    [RavenTheory(RavenTestCategory.Indexes | RavenTestCategory.Querying)]
    [InlineData(CompatMode.None)]
    [InlineData(CompatMode.Expanded)]
    public async Task ACustomSorterIsRefusedByCoraxWithOrWithoutAJoinAndTheJoinWithoutItIsServed(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode);
        await db.Store.Maintenance.SendAsync(new PutSortersOperation(new SorterDefinition { Name = "NameSorter", Code = SorterCode }));

        string joined = $"{Games} {VipJoin} order by custom(Name, 'NameSorter') limit 0, 100";
        string plain = $"{Games} where {VipOwners} order by custom(Name, 'NameSorter') limit 0, 100";
        foreach (string rql in new[] { joined, plain })
        {
            Exception e = Assert.ThrowsAny<Exception>(() => db.Run(rql));
            Assert.True(e is NotSupportedInCoraxException, e.ToString());
            Assert.Contains("Corax doesn't support Custom OrderBy.", e.Message);
        }

        // the same join without the sorter is answered
        var before = mode == CompatMode.Expanded ? db.Snapshot() : null;
        Assert.Equal(["games/000", "games/001", "games/004", "games/005", "games/008", "games/009"], db.Run($"{Games} {VipJoin} order by id() limit 0, 100").Ids);
        if (mode == CompatMode.Expanded)
            Assert.True(Since(before, db.Snapshot()).Served >= 1);
    }

    public static IEnumerable<object[]> RefusedOrders()
    {
        // an alias that the ordering does not know
        yield return [$"{Games} order by u.Name limit 0, 10", typeof(InvalidQueryException), "Unknown alias u"];

        // the methods keep the alias in the name of the field, which no index has: the stock message for a field that is not indexed
        yield return [$"{Games} {VipJoin} order by custom(u.Name, 'NameSorter') limit 0, 10", typeof(RavenException), "The field 'u.Name' is not indexed in 'Games'"];
        yield return [$"{Games} {VipJoin} order by spatial.distance(u.Location, spatial.point(52, 5)) limit 0, 10", typeof(RavenException), "The field 'u.Location' is not indexed in 'Games'"];
        yield return [$"{Games} {VipJoin} order by random(u.Name) limit 0, 10", typeof(InvalidQueryException), "Invalid ORDER BY 'random()' call, expected value token"];

        // the rules of the join for an ORDER BY of a joined field
        yield return [$"{Games} {VipJoin} where matches(u) order by u.Number as long limit 0, 10", typeof(InvalidQueryException), "cannot be combined with matches(u) in the where clause"];
        yield return [$"{Games} join (from index 'Users' where Kind = 'vip') as u on g.Country = u.Kind and g.Name = u.Name order by u.Number as long limit 0, 10", typeof(InvalidQueryException), "'u' joins on several fields"];
    }

    [RavenTheory(RavenTestCategory.Querying)]
    [MemberData(nameof(RefusedOrders))]
    public async Task OrderingByAFieldOfAJoinAliasThatIsNotSupportedIsRefusedWithItsMessage(string rql, Type exception, string message)
    {
        foreach (CompatMode mode in new[] { CompatMode.None, CompatMode.Expanded })
        {
            using CompatDb db = await CompatDb.CreateAsync(this, mode);
            Exception e = Assert.ThrowsAny<Exception>(() => db.Run(rql));
            output.WriteLine($"{mode}: {e.GetType().Name}: {e.Message.Split('\n')[0]}");
            Assert.True(exception.IsInstanceOfType(e), $"{mode}: {e.GetType()} instead of {exception}: {e}");
            Assert.NotEqual(typeof(NullReferenceException), e.GetType());
            Assert.Contains(message, e.Message);
        }
    }

    [RavenTheory(RavenTestCategory.Querying)]
    [InlineData(CompatMode.None)]
    [InlineData(CompatMode.Expanded)]
    public async Task TheOrdersByAJoinedFieldThatAreSupportedGiveTheOrderOfTheJoinedIndex(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode);
        var before = mode == CompatMode.Expanded ? db.Snapshot() : null;

        // users/weak has Number 2 and users/strong 1: descending puts the games of the weak user first, and the games of a user are ordered by their rank
        Assert.Equal(["games/001", "games/005", "games/009", "games/000", "games/004", "games/008"], db.Run($"{Games} {VipJoin} order by u.Number as long desc, g.Rank as long limit 0, 100").Ids);
        Assert.Equal(["games/000", "games/004", "games/008", "games/001", "games/005", "games/009"], db.Run($"{Games} {VipJoin} order by u.Number as long, g.Rank as long limit 0, 100").Ids);

        // a join that is ordered by a field of the joined index is read by the server: the join index is not asked, so it neither serves nor falls back
        if (mode == CompatMode.Expanded)
        {
            Outcome outcome = Since(before, db.Snapshot());
            Assert.True(outcome.Served == 0 && outcome.Fallbacks == 0, outcome.Describe());
        }
    }
}
