using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Query.JoinIndexQueryOracle;

namespace FastTests.Server.Documents.Indexing.Joins.RedTeam;

/// <summary>Helpers of the adversarial tests of the join index: raw documents, error-state helpers and the comparison of a served answer with the join of the server.</summary>
public abstract class JoinRedTeamBase(ITestOutputHelper output) : RavenTestBase(output)
{
    protected static Dictionary<string, string> ScoreSettings(params (string Key, string Value)[] more)
    {
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString() };
        foreach ((string key, string value) in more)
            settings[key] = value;

        return settings;
    }

    protected static string FieldJoin(string sub, string tail = "") => $"{Outer} join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code{tail}";

    protected static string FieldLeftJoin(string sub, string tail = "") => $"{Outer} left join (from index 'Contexts/Search'{sub}) as c on m.ContextId = c.Code{tail}";

    /// <summary>Stores a document of any shape in a collection.</summary>
    protected static void PutRaw(JoinIndexHarness h, string id, string collection, object document)
    {
        h.Save(s =>
        {
            s.Store(document, id);
            s.Advanced.GetMetadataFor(document)[Constants.Documents.Metadata.Collection] = collection;
        });
    }

    /// <summary>The answer of the join index and of the join of the server must be the same; returns whether the join index served it.</summary>
    internal static bool Same(JoinIndexQueryOracle oracle, string rql)
    {
        long served = oracle.Info.Served;
        (QueryAnswer a, QueryAnswer b) = oracle.Both(rql);
        AssertSame(rql, a, b);
        return oracle.Info.Served > served;
    }

    /// <summary>The state of the index, null while it does not exist (a join index that is reset is deleted and made again).</summary>
    protected static Raven.Client.Documents.Indexes.IndexState? StateOf(JoinIndexHarness h, string name = JoinIndexHarness.JoinName)
    {
        try
        {
            return h.Stats(name).State;
        }
        catch (Exception e) when (e is InvalidDataException or IndexDoesNotExistException)
        {
            return null;
        }
    }

    /// <summary>Runs the operation again while the index does not exist: a join index that the server resets (an upstream index was reset or replaced) is deleted and made again on another thread, and is missing for a moment or disposed under a call that found it before.</summary>
    protected static async Task RetryWhileMissingAsync(Action operation, int seconds = 30)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception e) when (i < seconds * 50 && (e is IndexDoesNotExistException || e is Raven.Client.Exceptions.RavenException && e.ToString().Contains("was already disposed")))
            {
                await Task.Delay(20);
            }
        }
    }

    protected static async Task WaitForStateAsync(JoinIndexHarness h, Raven.Client.Documents.Indexes.IndexState state, string name = JoinIndexHarness.JoinName, int seconds = 20)
    {
        for (int i = 0; i < seconds * 20 && StateOf(h, name) != state; i++)
            await Task.Delay(50);

        Raven.Client.Documents.Indexes.IndexState? last = StateOf(h, name);
        Assert.True(last == state, $"'{name}' is {last?.ToString() ?? "missing"} instead of {state}; indexes: " + string.Join(", ", h.Store.Maintenance.Send(new GetIndexesStatisticsOperation()).Select(x => x.Name + ":" + x.State)));
    }

    /// <summary>Waits until the indexes that the join index reads are not stale; the join index itself may be in the error state.</summary>
    protected static async Task WaitForUpstreamAsync(JoinIndexHarness h, int seconds = 20)
    {
        for (int i = 0; i < seconds * 50; i++)
        {
            if (h.Store.Maintenance.Send(new GetIndexesStatisticsOperation()).Where(x => x.Name != JoinIndexHarness.JoinName).All(x => x.IsStale == false))
                return;

            await Task.Delay(20);
        }

        Assert.Fail("the upstream indexes stay stale");
    }

    protected static void Enable(JoinIndexHarness h, string name = JoinIndexHarness.JoinName) => RetryWhileMissingAsync(() => h.Store.Maintenance.Send(new EnableIndexOperation(name, false))).GetAwaiter().GetResult();

    protected static string[] Subs =>
    [
        "",
        " where Country = 'nl'",
        " where Country != 'nl'",
        " where IsActive = true",
        " where Country = 'nl' and IsActive = true",
        " where Rank > 3"
    ];
}
