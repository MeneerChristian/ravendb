using System;
using System.Threading.Tasks;
using Raven.Client.Documents.Operations;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Sparrow;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>The caches of the Indirect join indexes of every database are one registry of the process, so their budget is a setting of the server: two databases of one server have the same budget, and a database cannot set its own.</summary>
public class JoinResidentBudgetScopeTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Key = "Join.ResidentCacheMaxMemoryInMb";

    [RavenFact(RavenTestCategory.Indexes | RavenTestCategory.Configuration)]
    public async Task TwoDatabasesWithIndirectJoinsShareTheBudgetOfTheServer()
    {
        using CompatDb first = await CompatDb.CreateAsync(this, CompatMode.Indirect);
        using CompatDb second = await CompatDb.CreateAsync(this, CompatMode.Indirect);
        long serverBudget = first.Database.ServerStore.Configuration.Join.ResidentCacheMaxMemory.GetValue(SizeUnit.Bytes);

        // the budget is set when a join index makes its cache, in a database that has none of its own
        Assert.Equal(serverBudget, first.Database.Configuration.Join.ResidentCacheMaxMemory.GetValue(SizeUnit.Bytes));
        Assert.Equal(serverBudget, second.Database.Configuration.Join.ResidentCacheMaxMemory.GetValue(SizeUnit.Bytes));
        Assert.True(serverBudget > 0);

        // a refresh in each database makes the cache of its join index
        foreach (CompatDb db in new[] { first, second })
        {
            using (var session = db.Store.OpenSession())
            {
                session.Store(new CompatUser { Id = "users/more", Name = "more", Kind = "vip", Number = 9 });
                session.Store(new CompatGame { Id = "games/more", Name = "zelda", OwnerId = "users/more", Rank = 99 });
                session.SaveChanges();
            }

            await db.WaitForTheJoinAsync();
            Assert.Equal(7, db.Run($"from index 'Games' as g {CompatDb.VipJoin}").Total);
            Assert.True(db.Database.IndexStore.GetIndex(db.JoinName) != null);
            Assert.Equal(serverBudget, ResidentCacheRegistry.Instance.BudgetBytes);
        }
    }

    [RavenFact(RavenTestCategory.Indexes | RavenTestCategory.Configuration)]
    public async Task ADatabaseThatSetsTheBudgetOfTheCachesDoesNotLoad()
    {
        // the setting of a database would be the budget of the caches of every database
        Exception e = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using CompatDb db = await CompatDb.CreateAsync(this, CompatMode.Indirect, record => record.Settings[Key] = "0");
            await db.Store.Maintenance.SendAsync(new GetStatisticsOperation());
        });

        Assert.Contains(Key, e.ToString());
    }
}
