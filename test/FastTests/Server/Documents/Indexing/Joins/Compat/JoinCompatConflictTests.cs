using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Exceptions.Documents;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Compat.CompatDb;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>A document that is in conflict has no version to read: what the join does with it on the outer side and on the inner side, and after the conflict is resolved by a put.</summary>
public class JoinCompatConflictTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Games = "from index 'Games' as g";
    private const string Join = $"{Games} {VipJoin} order by id() limit 0, 1000";
    private const string Oracle = $"{Games} where {VipOwners} order by id() limit 0, 1000";

    /// <summary>Makes the document a conflict of two versions: the one it had and one that came from another database.</summary>
    private static void CreateConflict(CompatDb db, string id, string collection, Action<DynamicJsonValue> incoming)
    {
        using (db.Database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
        using (context.OpenWriteTransaction())
        {
            var json = new DynamicJsonValue { ["@metadata"] = new DynamicJsonValue { ["@collection"] = collection } };
            incoming(json);
            BlittableJsonReaderObject doc = context.ReadObject(json, id);
            db.Database.DocumentsStorage.ConflictsStorage.AddConflict(context, id, DateTime.UtcNow.Ticks, doc, "B:100-abcdefghijklmnopqrstuv", collection, DocumentFlags.None);
            context.Transaction.Commit();
        }
    }

    private const string OuterNotInConflict = $"{Games} {VipJoin} where g.Rank > 1 order by id() limit 0, 1000";
    private const string OuterNotInConflictOracle = $"{Games} where {VipOwners} and g.Rank > 1 order by id() limit 0, 1000";

    private static void AssertConflict(CompatDb db, string rql)
    {
        Exception e = Assert.ThrowsAny<Exception>(() => db.Run(rql));
        Assert.True(e is DocumentConflictException, e.ToString());
    }

    [RavenFact(RavenTestCategory.Indexes | RavenTestCategory.Querying | RavenTestCategory.Replication)]
    public async Task AConflictOnAnOuterDocumentIsReportedLikeTheQueryWithoutTheJoinAndItsResolutionMovesTheRow()
    {
        using CompatDb db = await CompatDb.CreateAsync(this, CompatMode.Expanded);
        string[] all = ["games/000", "games/001", "games/004", "games/005", "games/008", "games/009"];
        await db.AssertJoinExactAsync("start", Join, Oracle, all);

        // games/001 (owner users/weak) gets a second version from another database: the index entry stays, the document cannot be read until the conflict is resolved
        CreateConflict(db, "games/001", "CompatGames", j => { j["Name"] = "zelda"; j["OwnerId"] = "users/other"; j["Rank"] = 1L; });
        AssertConflict(db, Oracle);
        AssertConflict(db, Join);
        await db.AssertJoinExactAsync("outer conflict", OuterNotInConflict, OuterNotInConflictOracle, ["games/004", "games/005", "games/008", "games/009"]);

        // a put resolves it, and the document is joined by what the put says
        using (var session = db.Store.OpenSession())
        {
            session.Store(new CompatGame { Id = "games/001", Name = "zelda", OwnerId = "users/other", Rank = 1 });
            session.SaveChanges();
        }

        await db.AssertJoinExactAsync("resolved", Join, Oracle, ["games/000", "games/004", "games/005", "games/008", "games/009"]);
    }

    [RavenFact(RavenTestCategory.Indexes | RavenTestCategory.Querying | RavenTestCategory.Replication)]
    public async Task AConflictOnAnInnerDocumentLeavesTheJoinAsItWasAndItsResolutionChangesIt()
    {
        using CompatDb db = await CompatDb.CreateAsync(this, CompatMode.Expanded);
        string[] all = ["games/000", "games/001", "games/004", "games/005", "games/008", "games/009"];
        await db.AssertJoinExactAsync("start", Join, Oracle, all);

        // users/weak (vip) gets a second version that is not vip: until it is resolved the inner index still has the vip entry, so its games stay in the join
        CreateConflict(db, "users/weak", "CompatUsers", j => { j["Name"] = "chris"; j["Kind"] = "plain"; j["Number"] = 2L; });
        await db.AssertJoinExactAsync("inner conflict", Join, Oracle, all);

        using (var session = db.Store.OpenSession())
            Assert.Throws<DocumentConflictException>(() => session.Load<CompatUser>("users/weak"));

        // the put that resolves it says plain: its games leave the join
        using (var session = db.Store.OpenSession())
        {
            session.Store(new CompatUser { Id = "users/weak", Name = "chris", Kind = "plain", Number = 2 });
            session.SaveChanges();
        }

        await db.AssertJoinExactAsync("resolved", Join, $"{Games} where g.OwnerId = 'users/strong' order by id() limit 0, 1000", ["games/000", "games/004", "games/008"]);
    }
}
