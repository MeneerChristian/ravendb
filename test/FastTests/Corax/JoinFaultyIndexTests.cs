using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Errors;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Tests.Infrastructure;
using Xunit;
using EngineType = Raven.Client.Documents.Indexes.SearchEngineType;

namespace FastTests.Corax;

public class JoinFaultyIndexTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private sealed class Game
    {
        public string Id { get; set; }
        public string OwnerId { get; set; }
    }

    private sealed class User
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    private sealed class Games : AbstractIndexCreationTask<Game>
    {
        public Games()
        {
            Map = games => from g in games select new { g.OwnerId };
            SearchEngineType = EngineType.Corax;
        }
    }

    private sealed class Users : AbstractIndexCreationTask<User>
    {
        public Users()
        {
            Map = users => from u in users select new { u.Name };
            SearchEngineType = EngineType.Corax;
        }
    }

    [RavenFact(RavenTestCategory.Indexes | RavenTestCategory.Querying)]
    public async Task AJoinWithAFaultyIndexFailsWithItsMessage()
    {
        using IDocumentStore store = GetDocumentStore(new Options
        {
            RunInMemory = false,
            ModifyDatabaseRecord = record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString()
        });
        new Games().Execute(store);
        new Users().Execute(store);

        using (IDocumentSession session = store.OpenSession())
        {
            session.Store(new User { Id = "users/1", Name = "ann" });
            session.Store(new Game { Id = "games/1", OwnerId = "users/1" });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        const string join = "from index 'Games' as g join (from index 'Users' where Name = 'ann') as u on g.OwnerId = id(u)";
        using (IDocumentSession session = store.OpenSession())
            Assert.Single(session.Advanced.RawQuery<Game>(join).ToList());

        string database = store.Database;
        var documentDatabase = await GetDatabase(database);
        string usersPath = Path.Combine(documentDatabase.Configuration.Indexing.StoragePath.FullPath, IndexDefinitionBaseServerSide.GetIndexNameSafeForFileSystem("Users"));

        Server.ServerStore.DatabasesLandlord.UnloadDirectly(database);
        IOExtensions.DeleteDirectory(Path.Combine(usersPath, "Journals"));
        using (Server.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
        {
            context.OpenReadTransaction();
            DatabaseRecord record = Server.ServerStore.Cluster.ReadDatabase(context, database, out long etag);
            record.Settings[RavenConfiguration.GetKey(x => x.Core.ThrowIfAnyIndexCannotBeOpened)] = "false";
            (long newEtag, _) = await Server.ServerStore.WriteDatabaseRecordAsync(database, record, etag, Guid.NewGuid().ToString());
            await Server.ServerStore.Cluster.WaitForIndexNotification(newEtag);
        }

        var reloaded = await GetDatabase(database);
        Assert.IsType<FaultyInMemoryIndex>(reloaded.IndexStore.GetIndex("Users"));

        using (IDocumentSession session = store.OpenSession())
        {
            Exception e = Assert.ThrowsAny<Exception>(() => { session.Advanced.RawQuery<Game>(join).ToList(); });
            Assert.IsNotType<NullReferenceException>(e);
            Assert.True(e.ToString().Contains("The index 'Users' is faulty, a query cannot join with it."), e.ToString());
        }
    }
}
