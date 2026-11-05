using System;
using System.Net;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Compat.CompatDb;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>
/// What the server answers to the requests that Studio makes for the page of an index (its terms and its entries fields) when the index is a join index. Studio reads the status and the Message of the body,
/// so these are its contract: the terms of a join index are a 400 that says the join index is not queried, and the entries fields are an empty answer with a 200.
/// </summary>
public class JoinCompatStudioContractTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(IDocumentStore store, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{store.Urls[0]}/databases/{store.Database}{path}");
        using HttpResponseMessage response = await store.GetRequestExecutor().HttpClient.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Escaped(string name) => "name=" + Uri.EscapeDataString(name);

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData("Kind")]
    [InlineData("Number")]
    [InlineData("AFieldThatTheJoinDoesNotDeclare")]
    public async Task TheTermsOfAJoinAreABadRequestThatSaysTheJoinIsNotQueriedWhateverTheField(string field)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, CompatMode.Expanded);

        (HttpStatusCode status, string body) = await GetAsync(db.Store, $"/indexes/terms?field={field}&{Escaped(db.JoinName)}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        using JsonDocument json = JsonDocument.Parse(body);
        Assert.Equal("Raven.Client.Exceptions.BadRequestException", json.RootElement.GetProperty("Type").GetString());
        Assert.Equal($"The join index '{db.JoinName}' answers JOINs of the indexes it joins. It is not queried directly.", json.RootElement.GetProperty("Message").GetString());
        Assert.False(json.RootElement.TryGetProperty("Terms", out _), "a refusal has no terms");

        // the index that the join index joins answers as usual
        (HttpStatusCode innerStatus, string innerBody) = await GetAsync(db.Store, $"/indexes/terms?field=Kind&{Escaped(Users)}");
        Assert.Equal(HttpStatusCode.OK, innerStatus);
        using JsonDocument inner = JsonDocument.Parse(innerBody);
        Assert.Equal(["plain", "vip"], inner.RootElement.GetProperty("Terms").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheEntriesFieldsOfAJoinAreItsDeclaredFieldsAsStaticTermsWhateverTheirKind()
    {
        using CompatDb db = await CompatDb.CreateAsync(this, CompatMode.Expanded);

        (HttpStatusCode status, string body) = await GetAsync(db.Store, $"/indexes/debug?op=entries-fields&{Escaped(db.JoinName)}");

        // the join index declares Kind (the default kind) and Number (a long); the answer does not tell the kinds apart and has no dynamic fields
        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument json = JsonDocument.Parse(body);
        var fields = json.RootElement.EnumerateArray().Select(x => (Name: x.GetProperty("Name").GetString(), Type: x.GetProperty("FieldType").GetString(), Value: x.GetProperty("ValueType").GetString())).OrderBy(x => x.Name).ToArray();
        Assert.Equal([("Kind", "Static", "Term"), ("Number", "Static", "Term")], fields);
    }
}
