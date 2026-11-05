using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Compat.CompatDb;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>Documents that arrive by replication into a database that has a join index: they are indexed like writes, and a conflict on one of them is handled like a conflict that was created in the storage.</summary>
public class JoinCompatReplicationTests(ITestOutputHelper output) : ReplicationTestBase(output)
{
    private const string Games = "from index 'Games' as g";
    private const string Join = $"{Games} {VipJoin} order by id() limit 0, 1000";

    private static string[] Ids(string prefix, int from, int to) => Enumerable.Range(from, to - from).Select(i => $"{prefix}{i:D3}").ToArray();

    // external replication is a feature of the license
    [RavenFact(RavenTestCategory.Indexes | RavenTestCategory.Querying | RavenTestCategory.Replication, LicenseRequired = true)]
    public async Task DocumentsThatArriveByExternalReplicationAreJoinedExactlyIncludingTheirUpdatesAndDeletes()
    {
        using var source = GetDocumentStore();
        using CompatDb db = await CompatDb.CreateAsync(this, CompatMode.Expanded);
        await SetupReplicationAsync(source, db.Store);

        // a vip user and a plain one, each with 30 games, all new documents of the source
        using (var session = source.OpenSession())
        {
            session.Store(new CompatUser { Id = "users/r1", Name = "r1", Kind = "vip", Number = 10 });
            session.Store(new CompatUser { Id = "users/r2", Name = "r2", Kind = "plain", Number = 11 });
            for (int i = 0; i < 60; i++)
                session.Store(new CompatGame { Id = $"games/r{i:D3}", Name = "replicated", OwnerId = i % 2 == 0 ? "users/r1" : "users/r2", Rank = 100 + i });

            session.SaveChanges();
        }

        Assert.NotNull(WaitForDocumentToReplicate<CompatGame>(db.Store, "games/r059", 15_000));
        string[] strongAndWeak = ["games/000", "games/001", "games/004", "games/005", "games/008", "games/009"];
        string[] first = strongAndWeak.Concat(Enumerable.Range(0, 30).Select(i => $"games/r{i * 2:D3}")).OrderBy(x => x, System.StringComparer.Ordinal).ToArray();
        await db.AssertJoinExactAsync("replicated", Join, $"{Games} where g.OwnerId in ('users/strong', 'users/weak', 'users/r1') order by id() limit 0, 1000", first);

        // the source turns r1 plain and r3 vip, moves games to r3, and deletes ten games
        using (var session = source.OpenSession())
        {
            session.Store(new CompatUser { Id = "users/r1", Name = "r1", Kind = "plain", Number = 10 });
            session.Store(new CompatUser { Id = "users/r3", Name = "r3", Kind = "vip", Number = 12 });
            for (int i = 1; i < 20; i += 2)
                session.Store(new CompatGame { Id = $"games/r{i:D3}", Name = "replicated", OwnerId = "users/r3", Rank = 100 + i });

            for (int i = 40; i < 50; i++)
                session.Delete($"games/r{i:D3}");

            session.Store(new CompatGame { Id = "games/zzz", Name = "last", OwnerId = "users/r3", Rank = 1 });
            session.SaveChanges();
        }

        Assert.NotNull(WaitForDocumentToReplicate<CompatGame>(db.Store, "games/zzz", 15_000));
        Assert.True(WaitForDocumentDeletion(db.Store, "games/r049"));
        string[] second = strongAndWeak.Concat(Enumerable.Range(1, 10).Select(i => $"games/r{i * 2 - 1:D3}")).Append("games/zzz").OrderBy(x => x, System.StringComparer.Ordinal).ToArray();
        await db.AssertJoinExactAsync("updated and deleted", Join, $"{Games} where g.OwnerId in ('users/strong', 'users/weak', 'users/r3') order by id() limit 0, 1000", second);
    }

    [RavenFact(RavenTestCategory.Indexes | RavenTestCategory.Querying | RavenTestCategory.Replication, LicenseRequired = true)]
    public async Task AConflictThatReplicationMakesOnAnOuterDocumentIsHandledLikeAConflictOfTheStorage()
    {
        using var source = GetDocumentStore();
        using CompatDb db = await CompatDb.CreateAsync(this, CompatMode.Expanded);

        // both databases have their own version of the game before they are connected
        using (var session = source.OpenSession())
        {
            session.Store(new CompatGame { Id = "games/c1", Name = "source", OwnerId = "users/other", Rank = 99 });
            session.SaveChanges();
        }

        using (var session = db.Store.OpenSession())
        {
            session.Store(new CompatGame { Id = "games/c1", Name = "target", OwnerId = "users/strong", Rank = 99 });
            session.SaveChanges();
        }

        await SetupReplicationAsync(source, db.Store);
        await Replication.WaitForConflict(db.Store, "games/c1");

        Assert.Equal(2, WaitUntilHasConflict(db.Store, "games/c1").Length);
        var exception = Assert.ThrowsAny<System.Exception>(() => db.Run(Join));
        Assert.True(exception is Raven.Client.Exceptions.Documents.DocumentConflictException, exception.ToString());

        string[] withoutIt = ["games/000", "games/001", "games/004", "games/005", "games/008", "games/009"];
        await db.AssertJoinExactAsync("conflict", $"{Games} {VipJoin} where g.Rank != 99 order by id() limit 0, 1000", $"{Games} where {VipOwners} and g.Rank != 99 order by id() limit 0, 1000", withoutIt);

        // the put that resolves it
        using (var session = db.Store.OpenSession())
        {
            session.Store(new CompatGame { Id = "games/c1", Name = "resolved", OwnerId = "users/weak", Rank = 99 });
            session.SaveChanges();
        }

        await db.AssertJoinExactAsync("resolved", Join, $"{Games} where {VipOwners} order by id() limit 0, 1000", withoutIt.Append("games/c1").OrderBy(x => x, System.StringComparer.Ordinal).ToArray());
    }
}
