using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corax.Querying;
using Raven.Client.Documents;
using Raven.Client.Documents.BulkInsert;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Raven.Server.Config;
using Raven.Server.Config.Categories;
using Raven.Server.ServerWide;
using Sparrow;
using Sparrow.Server;
using Sparrow.Threading;
using Sparrow.Server.LowMemory;
using Tests.Infrastructure;
using Xunit;
using EngineType = Raven.Client.Documents.Indexes.SearchEngineType;

namespace FastTests.Corax;

public class JoinKeysMemoryLimitTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Join = "from index 'Games' as g join (from index 'Users' where Kind = 'vip') as u on g.OwnerId = id(u) order by id() limit 0, 100000";

    private sealed class Game
    {
        public string Id { get; set; }
        public string OwnerId { get; set; }
    }

    private sealed class User
    {
        public string Id { get; set; }
        public string Kind { get; set; }
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
            Map = users => from u in users select new { u.Kind };
            SearchEngineType = EngineType.Corax;
        }
    }

    private static string IdOf(int i) => $"users/{i:D6}/{new string('x', 200)}";

    private IDocumentStore Seed(int vipUsers, string limitInMb)
    {
        var server = GetNewServer(new ServerCreationOptions
        {
            CustomSettings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Queries.JoinMaxKeysMemory)] = limitInMb }
        });
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.Server = server;
        IDocumentStore store = GetDocumentStore(options);
        new Games().Execute(store);
        new Users().Execute(store);

        using (BulkInsertOperation bulk = store.BulkInsert())
        {
            for (int i = 0; i < vipUsers; i++)
                bulk.Store(new User { Id = IdOf(i), Kind = "vip" });

            for (int i = 0; i < 3; i++)
                bulk.Store(new Game { Id = $"games/{i}", OwnerId = IdOf(i) });
        }

        Indexes.WaitForIndexing(store);
        return store;
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.Configuration)]
    public void AJoinOverMoreMatchedDocumentsThanTheLimitHoldsFailsAndNamesTheSetting()
    {
        // 6,000 keys of 210 bytes and their bookkeeping are about 1.3 MB
        using IDocumentStore store = Seed(vipUsers: 6_000, limitInMb: "1");

        using IDocumentSession session = store.OpenSession();
        InvalidQueryException e = Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<Game>(Join).ToList());

        Assert.Contains("Query.JoinMaxKeysMemoryInMb", e.Message);
        Assert.Contains("'u'", e.Message);

        // the server is still serving
        Assert.Equal(6_000, session.Query<User>("Users").Count(x => x.Kind == "vip"));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.Configuration)]
    public void AJoinThatFitsTheLimitIsAnswered()
    {
        using IDocumentStore small = Seed(vipUsers: 3_000, limitInMb: "1");
        using (IDocumentSession session = small.OpenSession())
            Assert.Equal(3, session.Advanced.RawQuery<Game>(Join).ToList().Count);

        using IDocumentStore large = Seed(vipUsers: 6_000, limitInMb: "8");
        using (IDocumentSession session = large.OpenSession())
            Assert.Equal(3, session.Advanced.RawQuery<Game>(Join).ToList().Count);
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void TheKeysStopAtTheirLimitWithTheCountOfTheKeysThatWereHeld()
    {
        using var allocator = new ByteStringContext(SharedMultipleUseFlag.None);
        using var keys = new JoinTermScores(allocator) { MaxHeldBytes = 1000 };
        var term = Encoding.UTF8.GetBytes(new string('k', 92));
        var added = 0;

        // 92 bytes of the key and 8 of what is kept for it: ten fit
        var e = Assert.Throws<JoinKeysMemoryExceededException>(() =>
        {
            for (; added < 100; added++)
                keys.Add(term, 1f);
        });

        Assert.Equal(10, added);
        Assert.Equal(10, e.Keys);
        Assert.Equal(1000, e.MaxHeldBytes);
        Assert.Equal(1000, keys.HeldBytes);
    }

    [RavenTheory(RavenTestCategory.Configuration)]
    [InlineData(64, 8)]
    [InlineData(1024, 128)]
    [InlineData(2048, 256)]
    [InlineData(4096, 512)]
    [InlineData(65536, 512)]
    [InlineData(4, 1)]
    public void TheDefaultLimitIsAnEighthOfTheMemoryAndAtMost512Mb(long totalMb, long expectedMb)
    {
        var configuration = new QueryConfiguration(new Size(totalMb, SizeUnit.Megabytes));

        Assert.Equal(expectedMb, configuration.JoinMaxKeysMemory.GetValue(SizeUnit.Megabytes));
    }

    [RavenFact(RavenTestCategory.Configuration)]
    public void TheDefaultOfTheServerComesFromTheMemoryOfTheMachineAndTheSettingOverridesIt()
    {
        long expected = Math.Min(512, Math.Max(1, MemoryInformation.TotalPhysicalMemory.GetValue(SizeUnit.Megabytes) / 8));

        RavenConfiguration configuration = RavenConfiguration.CreateForTesting("db", ResourceType.Database).Initialize();
        Assert.Equal(expected, configuration.Queries.JoinMaxKeysMemory.GetValue(SizeUnit.Megabytes));

        RavenConfiguration overridden = RavenConfiguration.CreateForTesting("server", ResourceType.Server);
        overridden.SetSetting(RavenConfiguration.GetKey(x => x.Queries.JoinMaxKeysMemory), "7");
        Assert.Equal(7, overridden.Initialize().Queries.JoinMaxKeysMemory.GetValue(SizeUnit.Megabytes));
    }
}
