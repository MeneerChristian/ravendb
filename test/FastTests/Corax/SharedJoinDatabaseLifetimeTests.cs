using System.IO;
using System.Linq;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// Every test of a class gets its own instance of the class
public class SharedJoinDatabaseLifetimeTests(ITestOutputHelper output, SharedJoinDatabase shared) : RavenTestBase(output), IClassFixture<SharedJoinDatabase>
{
    private sealed class Item
    {
        public string Id { get; set; }
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void FirstTest_SharedDatabaseSurvives() => Run();

    [RavenFact(RavenTestCategory.Querying)]
    public void SecondTest_SharedDatabaseSurvives() => Run();

    [RavenFact(RavenTestCategory.Querying)]
    public void ThirdTest_SharedDatabaseSurvives() => Run();

    private void Run()
    {
        using var store = shared.Get("lifetime", Options.ForSearchEngine(RavenSearchEngineMode.Corax), o => GetDocumentStore(o), s =>
        {
            using var session = s.OpenSession();
            session.Store(new Item { Id = "items/1" });
            session.SaveChanges();
        });

        var record = store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database));
        var dataDirectory = record.Settings[RavenConfiguration.GetKey(x => x.Core.DataDirectory)];
        Assert.True(Directory.Exists(dataDirectory), $"the data directory of the shared database is gone: {dataDirectory}");

        using (var session = store.OpenSession())
        {
            session.Store(new Item());
            session.SaveChanges();
        }

        using (var session = store.OpenSession())
            Assert.NotEmpty(session.Query<Item>().Customize(x => x.WaitForNonStaleResults()).ToList());
    }
}
