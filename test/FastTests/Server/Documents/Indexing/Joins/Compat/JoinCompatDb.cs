using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide;
using Raven.Server.Config;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Sparrow;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>How the join index that serves the join of a compatibility test is made.</summary>
public enum CompatMode
{
    /// <summary>A join index that is put, storage mode Expanded.</summary>
    Expanded,

    /// <summary>No join index: every join is the join of the server.</summary>
    None
}

public sealed class CompatUser
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Kind { get; set; }
    public long Number { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public sealed class CompatGame
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string OwnerId { get; set; }
    public string RelatedId { get; set; }
    public string PublisherId { get; set; }
    public string Country { get; set; }
    public long Rank { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public float[] Embedding { get; set; }
}

public sealed class CompatPublisher
{
    public string Id { get; set; }
    public string Name { get; set; }
}

/// <summary>
/// The data of the RQL feature matrix (users and games) in a database that has a join index on the join of the matrix: the games are joined with the users of the kind 'vip' by the id of the user.
/// What the join index serves is read from the statistics of the join index, so a test can tell a served join from one that was read as usual.
/// </summary>
public sealed class CompatDb : IDisposable
{
    public const string Games = "Games";
    public const string Users = "Users";
    public const string ManualJoin = "Games/WithVipOwner";

    /// <summary>The join of the matrix: the games of the owners that are vip (users/strong and users/weak).</summary>
    public const string VipJoin = "join (from index 'Users' where Kind = 'vip') as u on g.OwnerId = id(u)";

    /// <summary>The query without the join that returns the same games.</summary>
    public const string VipOwners = "g.OwnerId in ('users/strong', 'users/weak')";

    private readonly RavenTestBase _test;

    public DocumentStore Store { get; }

    public DocumentDatabase Database { get; private set; }

    public CompatMode Mode { get; }

    public string JoinName => ManualJoin;

    private CompatDb(RavenTestBase test, DocumentStore store, CompatMode mode)
    {
        _test = test;
        Store = store;
        Mode = mode;
    }

    public static RavenTestBase.Options Options(CompatMode mode, Action<DatabaseRecord> modify = null, bool runInMemory = true)
    {
        var options = RavenTestBase.Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.RunInMemory = runInMemory;
        options.ModifyDatabaseRecord += record =>
        {
            record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
            modify?.Invoke(record);
        };

        return options;
    }

    public static async Task<CompatDb> CreateAsync(RavenTestBase test, CompatMode mode, Action<DatabaseRecord> modify = null, bool populate = true, bool runInMemory = true)
    {
        DocumentStore store = test.GetDocumentStore(Options(mode, modify, runInMemory));
        var db = new CompatDb(test, store, mode);
        db.Database = await test.GetDatabase(store.Database);
        if (populate)
            await db.PopulateAsync();

        return db;
    }

    public async Task PopulateAsync()
    {
        await new UsersIndex().ExecuteAsync(Store);
        await new GamesIndex().ExecuteAsync(Store);
        if (Mode == CompatMode.Expanded)
            await Store.Maintenance.SendAsync(new PutIndexesOperation(JoinDefinition(ManualJoin)));

        using (IDocumentSession session = Store.OpenSession())
        {
            session.Store(new CompatUser { Id = "users/strong", Name = "chris bob", Kind = "vip", Number = 1, Latitude = 52.5, Longitude = 5 });
            session.Store(new CompatUser { Id = "users/weak", Name = "chris", Kind = "vip", Number = 2, Latitude = 10, Longitude = 10 });
            session.Store(new CompatUser { Id = "users/other", Name = "alice", Kind = "plain", Number = 3, Latitude = 52.4, Longitude = 5.1 });
            session.Store(new CompatPublisher { Id = "publishers/1", Name = "acme" });
            session.Store(new CompatPublisher { Id = "publishers/2", Name = "globex" });

            var names = new[] { "zelda adventure", "mario kart", "zelda breath" };
            var owners = new[] { "users/strong", "users/weak", "users/other", "users/missing" };
            for (var i = 0; i < 12; i++)
            {
                session.Store(new CompatGame
                {
                    Id = $"games/{i:D3}",
                    Name = names[i % names.Length],
                    OwnerId = owners[i % owners.Length],
                    RelatedId = $"games/{(i + 1) % 12:D3}",
                    PublisherId = i % 2 == 0 ? "publishers/1" : "publishers/2",
                    Country = i % 3 == 0 ? null : i % 2 == 0 ? "nl" : "be",
                    Rank = i,
                    Latitude = 52 + i * 0.1,
                    Longitude = 5
                });
            }

            session.SaveChanges();
        }

        _test.Indexes.WaitForIndexing(Store);
        if (Mode == CompatMode.Expanded)
            await WaitForTheJoinAsync();
    }

    public static IndexDefinition JoinDefinition(string name)
    {
        var definition = new IndexDefinition
        {
            Name = name,
            Join = new JoinDefinition { OuterIndex = Games, OuterField = "OwnerId", InnerIndex = Users }
                .AddField("Kind")
                .AddField("Number", JoinFieldKind.Long)
        };

        return definition;
    }

    /// <summary>The join index of the mode is current once the indexes are.</summary>
    public async Task WaitForTheJoinAsync()
    {
        for (int i = 0; i < 400; i++)
        {
            if (JoinIsCurrent())
                return;

            await Task.Delay(50);
        }

        Assert.True(JoinIsCurrent(), "the join index did not become current");
    }

    private bool JoinIsCurrent()
    {
        IndexStats stats = Stats();
        return stats.IsStale == false && stats.Join.PendingReason == null && stats.State == IndexState.Normal && stats.Join.Builds > 0;
    }

    public IndexStats Stats() => Store.Maintenance.Send(new GetIndexStatisticsOperation(JoinName));

    public JoinIndexStats Join => Stats().Join;

    /// <summary>Runs a query, returns the ids of the entities in the order they came and the total.</summary>
    public (List<string> Ids, long Total) Run(string rql, Action<IRawDocumentQuery<CompatGame>> configure = null)
    {
        using IDocumentSession session = Store.OpenSession(new SessionOptions { NoCaching = true });
        IRawDocumentQuery<CompatGame> query = session.Advanced.RawQuery<CompatGame>(rql).Statistics(out QueryStatistics stats);
        configure?.Invoke(query);
        List<CompatGame> games = query.ToList();
        return (games.Select(x => session.Advanced.GetDocumentId(x)).ToList(), stats.TotalResults);
    }

    /// <summary>What the join index did with the joins read between two calls: how many it served and the reasons of the ones it did not.</summary>
    public readonly record struct Outcome(long Served, long Fallbacks, Dictionary<string, long> Reasons)
    {
        public string Describe() => $"served {Served}, fallbacks {Fallbacks}: {string.Join(", ", Reasons.Select(x => $"{x.Key}={x.Value}"))}";
    }

    public JoinIndexStats Snapshot() => Join;

    public static Outcome Since(JoinIndexStats before, JoinIndexStats after)
    {
        var reasons = new Dictionary<string, long>();
        foreach ((string reason, long count) in after.Fallbacks)
        {
            long delta = count - before.Fallbacks.GetValueOrDefault(reason);
            if (delta > 0)
                reasons[reason] = delta;
        }

        return new Outcome(after.Served - before.Served, after.FallbackTotal - before.FallbackTotal, reasons);
    }

    public bool IsStale(string index) => Store.Maintenance.Send(new GetIndexStatisticsOperation(index)).IsStale;

    /// <summary>
    /// Waits for the indexes, then reads the join three ways: as the join index serves it, as the server joins (the join index forced to fall back) and as the query without the join that <paramref name="oracle"/> is.
    /// The three are equal, equal to <paramref name="expected"/>, and the join index served the first read.
    /// </summary>
    public async Task AssertJoinExactAsync(string step, string join, string oracle, string[] expected)
    {
        for (int i = 0; i < 1000 && (IsStale(Games) || IsStale(Users)); i++)
            await Task.Delay(20);

        Assert.True(IsStale(Games) == false && IsStale(Users) == false, step + ": the indexes are current");
        await WaitForTheJoinAsync();
        JoinIndexStats before = Snapshot();
        (List<string> served, long servedTotal) = Run(join);
        Outcome outcome = Since(before, Snapshot());
        JoinIndex joinIndex = (JoinIndex)Database.IndexStore.GetIndex(JoinName);
        joinIndex.QueryHooks = new JoinIndexQueryHooks { ForceFallback = true };
        (List<string> scan, long scanTotal) = Run(join);
        joinIndex.QueryHooks = null;
        (List<string> plain, long plainTotal) = Run(oracle);

        Assert.True(expected.SequenceEqual(served), $"{step}: {string.Join(",", served)} instead of {string.Join(",", expected)}");
        Assert.Equal(scan, served);
        Assert.Equal(plain, served);
        Assert.Equal(scanTotal, servedTotal);
        Assert.Equal(plainTotal, servedTotal);
        Assert.True(outcome.Served == 1, $"{step}: not served: {outcome.Describe()}");
    }

    public void Dispose() => Store.Dispose();

    public sealed class UsersIndex : AbstractIndexCreationTask<CompatUser>
    {
        public UsersIndex()
        {
            Map = users => from u in users select new { u.Name, u.Kind, u.Number, Location = CreateSpatialField(u.Latitude, u.Longitude) };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => Users;
    }

    public sealed class GamesIndex : AbstractIndexCreationTask<CompatGame>
    {
        public GamesIndex()
        {
            Map = games => from g in games select new { g.Name, g.OwnerId, g.RelatedId, g.PublisherId, g.Country, g.Rank, Location = CreateSpatialField(g.Latitude, g.Longitude) };
            Index(x => x.Name, FieldIndexing.Search);
            Index(x => x.Country, FieldIndexing.Search);
        }

        public override string IndexName => Games;
    }
}
