using System;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide.Operations;
using Raven.Server;
using Raven.Server.Rachis;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Commands.Indexes;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins;

public class JoinIndexPutTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Join = "Conversations/WithContext";

    private static IndexDefinition OuterIndex(string engine = null)
    {
        var definition = new IndexDefinition
        {
            Name = "Conversations/ByContext",
            Maps = { "from c in docs.Conversations select new { c.ContextId, c.Title }" }
        };

        if (engine != null)
            definition.Configuration["Indexing.Static.SearchEngineType"] = engine;

        return definition;
    }

    private static IndexDefinition InnerIndex()
    {
        return new IndexDefinition
        {
            Name = "Contexts/Search",
            Maps = { "from c in docs.Contexts select new { c.Country, c.Name, c.Rank, c.IsActive, c.Title }" },
            Fields = { ["Title"] = new IndexFieldOptions { Indexing = FieldIndexing.Search } }
        };
    }

    private static IndexDefinition JoinIndex(string name = Join)
    {
        return new IndexDefinition
        {
            Name = name,
            Join = new JoinDefinition { OuterIndex = "Conversations/ByContext", OuterField = "ContextId", InnerIndex = "Contexts/Search" }
                .AddField("Country")
                .AddField("Rank", JoinFieldKind.Long)
                .AddField("Title", JoinFieldKind.Text)
                .AddGrouping("IsActive", "true")
        };
    }

    private DocumentStore Store()
    {
        return GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
    }

    private static IndexDefinition FromRecord(DocumentStore store, string name = Join)
    {
        return store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database)).Indexes[name];
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinIsPutTogetherWithTheIndexesItReads()
    {
        using DocumentStore store = Store();

        await store.Maintenance.SendAsync(new PutIndexesOperation(OuterIndex(), InnerIndex(), JoinIndex()));

        IndexDefinition stored = FromRecord(store);
        Assert.Equal(IndexType.Join, stored.Type);
        Assert.Empty(stored.Maps);
        Assert.True(JoinDefinition.IsSameJoin(JoinIndex().Join, stored.Join));
        Assert.Equal(new[] { "Conversations/ByContext", "Conversations/WithContext", "Contexts/Search" }.Order(), store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database)).Indexes.Keys.Order());
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinThatIsFirstInTheBatchReadsTheIndexesThatComeAfterIt()
    {
        using DocumentStore store = Store();

        await store.Maintenance.SendAsync(new PutIndexesOperation(OuterIndex(), InnerIndex(), JoinIndex("A/Join")));
        await store.Maintenance.SendAsync(new PutIndexesOperation(OuterIndex(), JoinIndex("B/Join"), InnerIndex()));

        Assert.NotNull(FromRecord(store, "A/Join"));
        Assert.NotNull(FromRecord(store, "B/Join"));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinThatReadsAnIndexThatDoesNotExistIsRefusedWithTheName()
    {
        using DocumentStore store = Store();
        await store.Maintenance.SendAsync(new PutIndexesOperation(OuterIndex()));

        var exception = await Assert.ThrowsAnyAsync<RavenException>(() => store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndex())));

        Assert.Contains("'Contexts/Search' does not exist", exception.Message);
        Assert.False(store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database)).Indexes.ContainsKey(Join));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinThatReadsALuceneIndexIsRefused()
    {
        using DocumentStore store = Store();
        await store.Maintenance.SendAsync(new PutIndexesOperation(OuterIndex("Lucene"), InnerIndex()));

        var exception = await Assert.ThrowsAnyAsync<RavenException>(() => store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndex())));

        Assert.Contains("a join index needs Corax", exception.Message);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinIndexWithMapsIsRefused()
    {
        using DocumentStore store = Store();
        await store.Maintenance.SendAsync(new PutIndexesOperation(OuterIndex(), InnerIndex()));
        IndexDefinition definition = JoinIndex();
        definition.Maps.Add("from c in docs.Conversations select new { c.Title }");

        var exception = await Assert.ThrowsAnyAsync<RavenException>(() => store.Maintenance.SendAsync(new PutIndexesOperation(definition)));

        Assert.Contains("defined by its Join section only", exception.Message);
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData("//bad")]
    [InlineData("has space")]
    [InlineData("Conversations//WithContext")]
    [InlineData("Auto/Conversations/WithContext")]
    public async Task TheNameOfAJoinFollowsTheRulesOfTheNameOfAnIndex(string name)
    {
        using DocumentStore store = Store();
        await store.Maintenance.SendAsync(new PutIndexesOperation(OuterIndex(), InnerIndex()));

        await Assert.ThrowsAnyAsync<RavenException>(() => store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndex(name))));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ANameThatDiffersOnlyInCaseFromAnIndexIsRefused()
    {
        using DocumentStore store = Store();
        await store.Maintenance.SendAsync(new PutIndexesOperation(OuterIndex(), InnerIndex(), JoinIndex()));

        await Assert.ThrowsAnyAsync<RavenException>(() => store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndex(Join.ToLowerInvariant()))));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ASlashIsAllowedInTheNameOfAJoin()
    {
        using DocumentStore store = Store();

        await store.Maintenance.SendAsync(new PutIndexesOperation(OuterIndex(), InnerIndex(), JoinIndex("Conversations/With/Context")));

        Assert.Equal(IndexType.Join, FromRecord(store, "Conversations/With/Context").Type);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task APutWithAnotherJoinReplacesTheDefinitionAndTheSameJoinChangesNothing()
    {
        using DocumentStore store = Store();
        await store.Maintenance.SendAsync(new PutIndexesOperation(OuterIndex(), InnerIndex(), JoinIndex()));
        long before = store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database)).Indexes[Join].ClusterState.LastIndex;

        await store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndex()));
        Assert.Equal(before, FromRecord(store).ClusterState.LastIndex);

        IndexDefinition changed = JoinIndex();
        changed.Join.AddField("Name");
        await store.Maintenance.SendAsync(new PutIndexesOperation(changed));

        Assert.Equal(new[] { "Country", "Rank", "Title", "Name" }, FromRecord(store).Join.Fields.Select(x => x.Field));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheCreationTaskCreatesTheJoinAfterTheIndexesWhateverTheOrder()
    {
        using DocumentStore store = Store();
        var tasks = new IAbstractIndexCreationTask[] { new JoinIndexPutTests_WithContext(), new Contexts_Search(), new Conversations_ByContext() };

        await IndexCreation.CreateIndexesAsync(tasks, store);

        IndexDefinition stored = FromRecord(store, "JoinIndexPutTests/WithContext");
        Assert.Equal(IndexType.Join, stored.Type);
        Assert.Equal("Conversations/ByContext", stored.Join.OuterIndex);
    }

    [RavenFact(RavenTestCategory.Cluster)]
    public async Task TheLeaderRefusesAJoinWhileTheClusterContainsANodeThatDoesNotKnowTheCommand()
    {
        using RavenServer ownServer = GetNewServer();
        Options options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.Server = ownServer;
        using DocumentStore store = GetDocumentStore(options);
        await store.Maintenance.SendAsync(new PutIndexesOperation(OuterIndex(), InnerIndex()));
        ClusterCommandsVersionManager versions = ownServer.ServerStore.Engine.CommandsVersionManager;
        int current = versions.CurrentClusterMinimalVersion;
        int sharedVersion = Server.ServerStore.Engine.CommandsVersionManager.CurrentClusterMinimalVersion;

        try
        {
            versions.SetClusterVersion(ClusterCommandsVersionManager.ClusterCommandsVersions[nameof(PutJoinIndexCommand)] - 1);

            Assert.Equal(sharedVersion, Server.ServerStore.Engine.CommandsVersionManager.CurrentClusterMinimalVersion);

            var exception = await Assert.ThrowsAnyAsync<Exception>(() => store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndex())));
            Assert.Contains(nameof(PutJoinIndexCommand), exception.ToString());
            Assert.False(store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database)).Indexes.ContainsKey(Join));
        }
        finally
        {
            versions.SetClusterVersion(current);
        }

        await store.Maintenance.SendAsync(new PutIndexesOperation(JoinIndex()));
        Assert.NotNull(FromRecord(store));
    }

    private sealed class Conversation
    {
        public string ContextId { get; set; }
    }

    private sealed class Conversations_ByContext : AbstractIndexCreationTask<Conversation>
    {
        public Conversations_ByContext()
        {
            Map = conversations => from c in conversations select new { c.ContextId };
        }
    }

    private sealed class Context
    {
        public string Country { get; set; }
    }

    private sealed class Contexts_Search : AbstractIndexCreationTask<Context>
    {
        public Contexts_Search()
        {
            Map = contexts => from c in contexts select new { c.Country };
        }
    }

    private sealed class JoinIndexPutTests_WithContext : AbstractJoinCreationTask<Conversation, Context>
    {
        public JoinIndexPutTests_WithContext()
        {
            UseOuterIndex<Conversations_ByContext>();
            UseInnerIndex<Contexts_Search>();
            JoinOn(x => x.ContextId);
            AddField(x => x.Country);
        }
    }
}
