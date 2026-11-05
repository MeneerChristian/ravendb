using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide.Operations;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins;

public class JoinIndexEndpointsTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Join = "Conversations/WithContext";

    private static readonly string Name = "name=" + Uri.EscapeDataString(Join);

    private async Task<DocumentStore> StoreWithAJoinAsync()
    {
        DocumentStore store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        await store.Maintenance.SendAsync(new PutIndexesOperation(
            new IndexDefinition { Name = "Conversations/ByContext", Maps = { "from c in docs.Conversations select new { c.ContextId, c.Title }" } },
            new IndexDefinition
            {
                Name = "Contexts/Search",
                Maps = { "from c in docs.Contexts select new { c.Country, c.Name, c.Rank, c.IsActive, c.Title }" },
                Fields = { ["Title"] = new IndexFieldOptions { Indexing = FieldIndexing.Search } }
            },
            new IndexDefinition
            {
                Name = Join,
                Join = new JoinDefinition { OuterIndex = "Conversations/ByContext", OuterField = "ContextId", InnerIndex = "Contexts/Search" }
                    .AddField("Country").AddField("Rank", JoinFieldKind.Long).AddField("Title", JoinFieldKind.Text).AddGrouping("IsActive", "true")
            }));
        return store;
    }

    private static async Task<(HttpStatusCode Status, string Body)> CallAsync(DocumentStore store, HttpMethod method, string path, string body = null)
    {
        var request = new HttpRequestMessage(method, $"{store.Urls[0]}/databases/{store.Database}{path}");
        if (body != null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await store.GetRequestExecutor().HttpClient.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public static IEnumerable<object[]> ReadEndpoints()
    {
        yield return ["/indexes"];
        yield return ["/indexes?" + Name];
        yield return ["/indexes/stats"];
        yield return ["/indexes/stats?" + Name];
        yield return ["/indexes/staleness?" + Name];
        yield return ["/indexes/progress"];
        yield return ["/indexes/status"];
        yield return ["/indexes/errors"];
        yield return ["/indexes/errors?" + Name];
        yield return ["/indexes/performance"];
        yield return ["/indexes/performance?" + Name];
        yield return ["/indexes/total-time?" + Name];
        yield return ["/indexes/history?" + Name];
        yield return ["/indexes/debug?op=entries-fields&" + Name];
        yield return ["/indexes/debug/metadata?" + Name];
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [MemberData(nameof(ReadEndpoints))]
    public async Task TheEndpointsThatDescribeIndexesAnswerForAJoin(string path)
    {
        using DocumentStore store = await StoreWithAJoinAsync();

        (HttpStatusCode status, string body) = await CallAsync(store, HttpMethod.Get, path);

        Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {body}");
    }

    public static IEnumerable<object[]> RefusingRequests()
    {
        yield return ["GET", "/queries?query=" + Uri.EscapeDataString("from index '" + Join + "'"), null];
        yield return ["POST", "/queries", "{\"Query\":\"from index '" + Join + "'\"}"];
        yield return ["GET", "/queries?debug=entries&query=" + Uri.EscapeDataString("from index '" + Join + "'"), null];
        yield return ["GET", "/queries?query=" + Uri.EscapeDataString("from index '" + Join + "' select facet(Country)"), null];
        yield return ["GET", "/queries?query=" + Uri.EscapeDataString("from index '" + Join + "' where Country = 'nl'"), null];
        yield return ["POST", "/queries", "{\"Query\":\"from index '" + Join + "' select suggest(Country, 'nl')\"}"];
        yield return ["GET", "/indexes/terms?field=Country&" + Name, null];
        yield return ["GET", "/indexes/source?" + Name, null];
        yield return ["GET", "/indexes/c-sharp-index-definition?" + Name, null];
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [MemberData(nameof(RefusingRequests))]
    public async Task TheEndpointsThatReadEntriesOrSourceRefuseAJoinWithAMessage(string method, string path, string body)
    {
        using DocumentStore store = await StoreWithAJoinAsync();

        (HttpStatusCode status, string text) = await CallAsync(store, new HttpMethod(method), path, body);

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.DoesNotContain("NullReferenceException", text);
        Assert.DoesNotContain("InvalidCastException", text);
        Assert.Contains("join", text, StringComparison.OrdinalIgnoreCase);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public async Task AQueryOfAJoinAndAJoinThatReadsAJoinAreRefused()
    {
        using DocumentStore store = await StoreWithAJoinAsync();
        using IAsyncDocumentSession session = store.OpenAsyncSession();

        var direct = await Assert.ThrowsAnyAsync<RavenException>(() => session.Advanced.AsyncRawQuery<object>("from index 'Conversations/WithContext'").ToListAsync());
        Assert.Contains("not queried directly", direct.Message);

        var joined = await Assert.ThrowsAnyAsync<RavenException>(() => session.Advanced.AsyncRawQuery<object>(
            "from index 'Conversations/ByContext' as c join (from index 'Conversations/WithContext') as j on c.ContextId = id(j)").ToListAsync());
        Assert.Contains("join index 'Conversations/WithContext'", joined.Message);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheIndexApiDescribesAJoin()
    {
        using DocumentStore store = await StoreWithAJoinAsync();

        IndexDefinition definition = store.Maintenance.Send(new GetIndexOperation(Join));
        Assert.Equal(IndexType.Join, definition.Type);
        Assert.Empty(definition.Maps);
        Assert.Equal("ContextId", definition.Join.OuterField);
        Assert.Equal(new[] { "Country", "Rank", "Title" }, definition.Join.Fields.Select(x => x.Field));

        Assert.Contains(store.Maintenance.Send(new GetIndexesOperation(0, 10)), x => x.Name == Join && x.Type == IndexType.Join);
        Assert.Contains(Join, store.Maintenance.Send(new GetIndexNamesOperation(0, 10)));

        IndexStats stats = store.Maintenance.Send(new GetIndexStatisticsOperation(Join));
        Assert.Equal(IndexType.Join, stats.Type);
        Assert.Equal(IndexSourceType.Documents, stats.SourceType);
        Assert.Equal(SearchEngineType.Corax, stats.SearchEngineType);
        Assert.Equal(IndexState.Normal, stats.State);
        Assert.Equal(new[] { "Contexts", "Conversations" }, stats.Collections.Keys.Order());
        Assert.Contains(store.Maintenance.Send(new GetIndexesStatisticsOperation()), x => x.Name == Join);
        Assert.Empty(store.Maintenance.Send(new GetIndexErrorsOperation([Join])).Single().Errors);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task StatePriorityAndLockModeAreTheOnesOfAnyIndex()
    {
        using DocumentStore store = await StoreWithAJoinAsync();

        await store.Maintenance.SendAsync(new DisableIndexOperation(Join));
        Assert.Equal(IndexState.Disabled, store.Maintenance.Send(new GetIndexStatisticsOperation(Join)).State);
        Assert.Equal(IndexRunningStatus.Disabled, store.Maintenance.Send(new GetIndexStatisticsOperation(Join)).Status);

        await store.Maintenance.SendAsync(new EnableIndexOperation(Join));
        Assert.Equal(IndexState.Normal, store.Maintenance.Send(new GetIndexStatisticsOperation(Join)).State);

        await store.Maintenance.SendAsync(new SetIndexesPriorityOperation(Join, IndexPriority.Low));
        await store.Maintenance.SendAsync(new SetIndexesLockOperation(Join, IndexLockMode.LockedIgnore));
        IndexStats stats = store.Maintenance.Send(new GetIndexStatisticsOperation(Join));
        Assert.Equal(IndexPriority.Low, stats.Priority);
        Assert.Equal(IndexLockMode.LockedIgnore, stats.LockMode);

        await store.Maintenance.SendAsync(new StopIndexOperation(Join));
        Assert.Equal(IndexRunningStatus.Paused, store.Maintenance.Send(new GetIndexStatisticsOperation(Join)).Status);
        await store.Maintenance.SendAsync(new StartIndexOperation(Join));
        Assert.Equal(IndexRunningStatus.Running, store.Maintenance.Send(new GetIndexStatisticsOperation(Join)).Status);

        IndexDefinition definition = store.Maintenance.Send(new GetIndexOperation(Join));
        Assert.Equal(IndexPriority.Low, definition.Priority);
        Assert.Equal(IndexLockMode.LockedIgnore, definition.LockMode);
        Assert.NotNull(definition.Join);
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(IndexResetMode.InPlace)]
    [InlineData(IndexResetMode.SideBySide)]
    public async Task AJoinCanBeReset(IndexResetMode mode)
    {
        using DocumentStore store = await StoreWithAJoinAsync();

        await store.Maintenance.SendAsync(new ResetIndexOperation(Join, mode));

        await WaitForValueAsync(() => Task.FromResult(store.Maintenance.Send(new GetIndexNamesOperation(0, 10)).Contains("ReplacementOf/" + Join)), false);
        IndexStats stats = store.Maintenance.Send(new GetIndexStatisticsOperation(Join));
        Assert.Equal(IndexType.Join, stats.Type);
        Assert.Equal(new[] { "Contexts", "Conversations" }, stats.Collections.Keys.Order());
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinIsDeletedLikeAnyIndexAndTheIndexesItJoinsAreNotAffected()
    {
        using DocumentStore store = await StoreWithAJoinAsync();

        await store.Maintenance.SendAsync(new DeleteIndexOperation(Join));

        Assert.DoesNotContain(Join, store.Maintenance.Send(new GetIndexNamesOperation(0, 10)));
        Assert.Equal(2, store.Maintenance.Send(new GetIndexNamesOperation(0, 10)).Length);
        Assert.Null(store.Maintenance.Send(new GetIndexOperation(Join)));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinStaysWhenAnIndexItJoinsIsDeleted()
    {
        using DocumentStore store = await StoreWithAJoinAsync();

        await store.Maintenance.SendAsync(new DeleteIndexOperation("Contexts/Search"));

        Assert.Equal(IndexType.Join, store.Maintenance.Send(new GetIndexStatisticsOperation(Join)).Type);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ADefinitionThatChangesTheJoinReplacesTheJoinSideBySide()
    {
        using DocumentStore store = await StoreWithAJoinAsync();
        IndexDefinition changed = store.Maintenance.Send(new GetIndexOperation(Join));
        changed.Join.AddField("NotAField");
        Assert.ThrowsAny<RavenException>(() => store.Maintenance.Send(new PutIndexesOperation(changed)));

        changed = store.Maintenance.Send(new GetIndexOperation(Join));
        changed.Join.Groupings.Clear();
        await store.Maintenance.SendAsync(new PutIndexesOperation(changed));

        Assert.True(await WaitForValueAsync(() => Task.FromResult(store.Maintenance.Send(new GetIndexOperation(Join)).Join.Groupings.Count == 0), true));
        Assert.DoesNotContain("ReplacementOf/" + Join, store.Maintenance.Send(new GetIndexNamesOperation(0, 10)));
        IndexDefinition current = store.Maintenance.Send(new GetIndexOperation(Join));
        Assert.Empty(current.Join.Groupings);
        Assert.Equal(IndexType.Join, store.Maintenance.Send(new GetIndexStatisticsOperation(Join)).Type);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinIsLoadedAgainWithItsCollectionsWhenTheDatabaseIsLoadedAgain()
    {
        var options = Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.RunInMemory = false;
        using DocumentStore store = GetDocumentStore(options);
        await store.Maintenance.SendAsync(new PutIndexesOperation(
            new IndexDefinition { Name = "Conversations/ByContext", Maps = { "from c in docs.Conversations select new { c.ContextId }" } },
            new IndexDefinition { Name = "Contexts/Search", Maps = { "from c in docs.Contexts select new { c.Country }" } },
            new IndexDefinition { Name = Join, Join = new JoinDefinition { OuterIndex = "Conversations/ByContext", OuterField = "ContextId", InnerIndex = "Contexts/Search" }.AddField("Country") }));

        await store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(store.Database, disable: true));
        await store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(store.Database, disable: false));

        IndexStats stats = store.Maintenance.Send(new GetIndexStatisticsOperation(Join));
        Assert.Equal(IndexType.Join, stats.Type);
        Assert.Equal(new[] { "Contexts", "Conversations" }, stats.Collections.Keys.Order());
        Assert.Equal(IndexState.Normal, stats.State);
    }
}
