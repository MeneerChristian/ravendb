using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FastTests.Utils;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Expiration;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.ServerWide;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Errors;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Compat.CompatDb;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>What happens to the answer of a join when a document changes in a way other than a plain put, when an index of the join is paused, and when a joined index is faulty.</summary>
public class JoinCompatLifecycleTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Games = "from index 'Games' as g";
    private const string Join = $"{Games} {VipJoin} order by id() limit 0, 1000";
    private const string Oracle = $"{Games} where {VipOwners} order by id() limit 0, 1000";

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 1000 && condition() == false; i++)
            await Task.Delay(20);

        Assert.True(condition(), what);
    }

    private bool IsStale(CompatDb db, string index) => db.Store.Maintenance.Send(new GetIndexStatisticsOperation(index)).IsStale;

    /// <summary>The join as the join index answers it equals the query without the join, and the join index served it.</summary>
    private async Task AssertExactAndServedAsync(CompatDb db, string step, string oracle = Oracle)
    {
        await WaitForAsync(() => IsStale(db, "Games") == false && IsStale(db, "Users") == false, step + ": the indexes are current");
        await db.WaitForTheJoinAsync();
        var before = db.Snapshot();
        (List<string> ids, long total) = db.Run(Join);
        Outcome outcome = Since(before, db.Snapshot());
        (List<string> expected, long expectedTotal) = db.Run(oracle);
        Assert.True(expected.SequenceEqual(ids) && total == expectedTotal, $"{step}: {string.Join(",", ids)} instead of {string.Join(",", expected)}");
        Assert.True(outcome.Served == 1, $"{step}: not served: {outcome.Describe()}");
    }

    [RavenTheory(RavenTestCategory.Indexes | RavenTestCategory.Querying | RavenTestCategory.Counters | RavenTestCategory.TimeSeries | RavenTestCategory.Attachments)]
    [InlineData(CompatMode.Expanded)]
    [InlineData(CompatMode.Indirect)]
    public async Task CountersAttachmentsAndTimeSeriesLeaveTheServedJoinExact(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode);
        await AssertExactAndServedAsync(db, "start");

        // counters of an outer document
        using (var session = db.Store.OpenSession())
        {
            session.CountersFor("games/000").Increment("likes", 5);
            session.CountersFor("games/001").Increment("likes", 2);
            session.SaveChanges();
        }

        await AssertExactAndServedAsync(db, "counters");

        // an attachment on an inner document, then deleted
        using (var session = db.Store.OpenSession())
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("attachment"));
            session.Advanced.Attachments.Store("users/strong", "note.txt", stream);
            session.SaveChanges();
        }

        await AssertExactAndServedAsync(db, "attachment added");
        using (var session = db.Store.OpenSession())
        {
            session.Advanced.Attachments.Delete("users/strong", "note.txt");
            session.SaveChanges();
        }

        await AssertExactAndServedAsync(db, "attachment deleted");

        // time series on both sides
        using (var session = db.Store.OpenSession())
        {
            session.TimeSeriesFor("users/weak", "Visits").Append(DateTime.UtcNow.Date, 3);
            session.TimeSeriesFor("games/004", "Plays").Append(DateTime.UtcNow.Date, 1);
            session.SaveChanges();
        }

        await AssertExactAndServedAsync(db, "time series");
    }

    // the license of the server limits the frequency of the expiration of a database, so a server without one cannot run it
    [RavenTheory(RavenTestCategory.Indexes | RavenTestCategory.Querying | RavenTestCategory.ExpirationRefresh, LicenseRequired = true)]
    [InlineData(CompatMode.Expanded)]
    [InlineData(CompatMode.Indirect)]
    public async Task AnInnerDocumentThatExpiresLeavesTheServedJoinExact(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode);
        await AssertExactAndServedAsync(db, "start");

        // the inner document expires: its games leave the join
        await ExpirationHelper.SetupExpiration(db.Store, Server.ServerStore, new ExpirationConfiguration { Disabled = false, DeleteFrequencyInSec = 100 });
        using (var session = db.Store.OpenSession())
        {
            var weak = session.Load<CompatUser>("users/weak");
            session.Advanced.GetMetadataFor(weak)["@expires"] = DateTime.UtcNow.AddMinutes(-5).ToString("o");
            session.SaveChanges();
        }

        await db.Database.ExpiredDocumentsCleaner.CleanupExpiredDocs();
        using (var session = db.Store.OpenSession())
            Assert.Null(session.Load<CompatUser>("users/weak"));

        await WaitForAsync(() => db.Run($"{Games} where g.OwnerId = 'users/weak'").Total == 3, "the games of the user that expired are still there");
        await AssertExactAndServedAsync(db, "expired", $"{Games} where g.OwnerId = 'users/strong' order by id() limit 0, 1000");
        Assert.Equal(new[] { "games/000", "games/004", "games/008" }, db.Run(Join).Ids);
    }

    [RavenTheory(RavenTestCategory.Indexes | RavenTestCategory.Querying)]
    [InlineData(CompatMode.Expanded)]
    [InlineData(CompatMode.Indirect)]
    public async Task APausedJoinIsNotWaitedForAndTheJoinIsReadByTheServerUntilItIsStarted(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode);
        await AssertExactAndServedAsync(db, "start");

        await db.Store.Maintenance.SendAsync(new StopIndexOperation(db.JoinName));
        await WaitForAsync(() => db.Stats().Status == IndexRunningStatus.Paused, "the join index is paused");

        using (var session = db.Store.OpenSession())
        {
            session.Store(new CompatGame { Id = "games/new", Name = "zelda", OwnerId = "users/strong", Rank = 50 });
            session.Delete("games/001");
            session.SaveChanges();
        }

        await WaitForAsync(() => IsStale(db, "Games") == false, "the outer index is current");

        var before = db.Snapshot();
        long waitsBefore = before.QueryWaits;
        var clock = Stopwatch.StartNew();
        (List<string> ids, long total) = db.Run(Join);
        TimeSpan elapsed = clock.Elapsed;
        JoinIndexStats after = db.Snapshot();
        Outcome outcome = Since(before, after);
        Assert.Equal(waitsBefore, after.QueryWaits);

        (List<string> expected, long expectedTotal) = db.Run(Oracle);
        Assert.Contains("games/new", ids);
        Assert.DoesNotContain("games/001", ids);
        Assert.Equal(expected, ids);
        Assert.Equal(expectedTotal, total);
        if (mode == CompatMode.Expanded)
            Assert.True(outcome.Served == 0 && outcome.Reasons.GetValueOrDefault(JoinFallbacks.BehindTimeout) == 1, $"the join of a paused join index that is behind should be a BehindTimeout: {outcome.Describe()}");
        else
            Assert.True(outcome.Served == 1, $"an Indirect join index holds the keys of the inner index, which did not change: it serves the join of the new outer rows: {outcome.Describe()}");
        Assert.True(elapsed < TimeSpan.FromMilliseconds(1500), $"the query waited for a paused join index: {elapsed}");

        await db.Store.Maintenance.SendAsync(new StartIndexOperation(db.JoinName));
        await AssertExactAndServedAsync(db, "started again");
    }

    [RavenFact(RavenTestCategory.Indexes | RavenTestCategory.Querying)]
    public async Task APausedInnerIndexGivesTheJoinOfTheServerWhateverTheJoinDoes()
    {
        using CompatDb db = await CompatDb.CreateAsync(this, CompatMode.Expanded);
        await AssertExactAndServedAsync(db, "start");

        await db.Store.Maintenance.SendAsync(new StopIndexOperation(CompatDb.Users));
        await WaitForAsync(() => db.Store.Maintenance.Send(new GetIndexStatisticsOperation(CompatDb.Users)).Status == IndexRunningStatus.Paused, "the inner index is paused");

        using (var session = db.Store.OpenSession())
        {
            session.Store(new CompatUser { Id = "users/new", Name = "new", Kind = "vip", Number = 7 });
            session.Store(new CompatGame { Id = "games/new", Name = "zelda", OwnerId = "users/new", Rank = 50 });
            session.Store(new CompatGame { Id = "games/moved", Name = "zelda", OwnerId = "users/other", Rank = 51 });
            session.SaveChanges();
        }

        await WaitForAsync(() => IsStale(db, "Games") == false, "the outer index is current");

        // the join of the server reads the inner index as it is (the new user is not in it); the join index does not know the user either
        JoinIndex joinIndex = (JoinIndex)db.Database.IndexStore.GetIndex(db.JoinName);
        (List<string> served, long servedTotal) = db.Run(Join);
        joinIndex.QueryHooks = new JoinIndexQueryHooks { ForceFallback = true };
        (List<string> scan, long scanTotal) = db.Run(Join);
        joinIndex.QueryHooks = null;

        Assert.Equal(scan, served);
        Assert.Equal(scanTotal, servedTotal);
        Assert.DoesNotContain("games/new", served);

        await db.Store.Maintenance.SendAsync(new StartIndexOperation(CompatDb.Users));
        await AssertExactAndServedAsync(db, "inner index started again", $"{Games} where g.OwnerId in ('users/strong', 'users/weak', 'users/new') order by id() limit 0, 1000");
        Assert.Contains("games/new", db.Run(Join).Ids);
    }

    [RavenTheory(RavenTestCategory.Indexes | RavenTestCategory.Querying)]
    [InlineData(CompatMode.None)]
    [InlineData(CompatMode.Expanded)]
    public async Task AJoinWithAFaultyIndexFailsWithItsMessageAndNoNullReference(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode, runInMemory: false);
        string database = db.Store.Database;
        string usersPath = Path.Combine(db.Database.Configuration.Indexing.StoragePath.FullPath, IndexDefinitionBaseServerSide.GetIndexNameSafeForFileSystem(CompatDb.Users));

        Server.ServerStore.DatabasesLandlord.UnloadDirectly(database);
        IOExtensions.DeleteDirectory(Path.Combine(usersPath, "Journals"));
        await ModifyDatabaseSettings(database, record => record.Settings[RavenConfiguration.GetKey(x => x.Core.ThrowIfAnyIndexCannotBeOpened)] = "false");

        var reloaded = await GetDatabase(database);
        Assert.IsType<FaultyInMemoryIndex>(reloaded.IndexStore.GetIndex(CompatDb.Users));

        using var session = db.Store.OpenSession();
        var e = Assert.ThrowsAny<Exception>(() => session.Advanced.RawQuery<CompatGame>(Join).ToList());
        Assert.IsNotType<NullReferenceException>(e);
        Assert.True(e.ToString().Contains($"The index '{CompatDb.Users}' is faulty, a query cannot join with it."), e.ToString());
    }

    private async Task ModifyDatabaseSettings(string databaseName, Action<DatabaseRecord> modifySettings)
    {
        using (Server.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
        {
            context.OpenReadTransaction();
            DatabaseRecord record = Server.ServerStore.Cluster.ReadDatabase(context, databaseName, out long etag);
            modifySettings(record);
            (long newEtag, _) = await Server.ServerStore.WriteDatabaseRecordAsync(databaseName, record, etag, Guid.NewGuid().ToString());
            await Server.ServerStore.Cluster.WaitForIndexNotification(newEtag);
        }
    }
}
