using System;
using System.Collections.Generic;
using System.Linq;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Query;

internal sealed record QueryRow(string Id, double Score);

internal sealed record QueryAnswer(List<QueryRow> Rows, long Total, long ResultEtag, bool IsStale, Dictionary<string, long> Counters)
{
    public string Describe(int take = 6) => $"total {Total}, rows {Rows.Count}: " + string.Join(", ", Rows.Take(take).Select(x => $"{x.Id}:{x.Score:G9}"));
}

/// <summary>Runs the queries twice, once as the join index answers them and once with the join index switched off (the join of the server), and compares what comes back.</summary>
internal sealed class JoinIndexQueryOracle(JoinIndexHarness harness, string joinName = JoinIndexHarness.JoinName, string outerIndex = JoinIndexHarness.OuterName, string innerIndex = JoinIndexHarness.InnerName)
{
    public const string Outer = "from index 'Conversations/ByContext' as m";

    public static string Join(string sub, string tail = "") => $"{Outer} join (from index 'Contexts/Search'{sub}) as c on m.ContextId = id(c){tail}";

    public static string LeftJoin(string sub, string tail = "") => $"{Outer} left join (from index 'Contexts/Search'{sub}) as c on m.ContextId = id(c){tail}";

    /// <summary>The same query as <see cref="Join"/> on the indexes of this oracle.</summary>
    public string JoinOf(string sub, string tail = "") => $"from index '{outerIndex}' as m join (from index '{innerIndex}'{sub}) as c on m.ContextId = id(c){tail}";

    public JoinIndexStats Info => harness.Stats(joinName).Join;

    public TimeSpan Wait => harness.Join(joinName).QueryOptions.Wait;

    public TimeSpan WaitForNonStale => harness.Join(joinName).QueryOptions.WaitForNonStale;

    /// <summary>Other join indexes that read the same join: the join of the server is read only when none of them answers it.</summary>
    public string[] AlsoForced { get; init; } = [];

    public void ForceFallback(bool value)
    {
        foreach (string name in AlsoForced.Prepend(joinName))
            harness.Join(name).QueryHooks = value ? new JoinIndexQueryHooks { ForceFallback = true } : null;
    }

    public QueryAnswer Run(string rql, IReadOnlyDictionary<string, object> parameters = null, bool waitForNonStale = false)
    {
        using IDocumentSession session = harness.Store.OpenSession(new SessionOptions { NoCaching = true });
        IRawDocumentQuery<JoinIndexHarness.Conversation> query = session.Advanced.RawQuery<JoinIndexHarness.Conversation>(rql + " include timings()");
        foreach ((string name, object value) in parameters ?? new Dictionary<string, object>())
            query.AddParameter(name, value);

        if (waitForNonStale)
            query.WaitForNonStaleResults(TimeSpan.FromSeconds(30));

        List<JoinIndexHarness.Conversation> documents = query.Timings(out QueryTimings timings).Statistics(out QueryStatistics stats).ToList();
        var rows = new List<QueryRow>(documents.Count);
        foreach (JoinIndexHarness.Conversation document in documents)
        {
            IMetadataDictionary metadata;
            try
            {
                metadata = session.Advanced.GetMetadataFor(document);
            }
            catch (InvalidOperationException)
            {
                // a projection of fields is not tracked by the session
                rows.Add(new QueryRow("title:" + document.Title, double.NaN));
                continue;
            }

            rows.Add(new QueryRow(metadata.TryGetValue(Constants.Documents.Metadata.Id, out object id) ? (string)id : "title:" + document.Title, metadata.TryGetValue(Constants.Documents.Metadata.IndexScore, out object score) ? Convert.ToDouble(score) : double.NaN));
        }

        return new QueryAnswer(rows, stats.TotalResults, stats.ResultEtag ?? 0, stats.IsStale, CountersOf(timings));
    }

    /// <summary>The counters of the timings of the query by the path of the scope, for example Join/c/JoinIndexServed.</summary>
    public static Dictionary<string, long> CountersOf(QueryTimings timings)
    {
        var result = new Dictionary<string, long>();
        Collect(timings, "", result);
        return result;

        static void Collect(QueryTimings timings, string path, Dictionary<string, long> result)
        {
            foreach ((string name, long value) in timings.Counters ?? new Dictionary<string, long>())
                result[path + name] = value;

            foreach ((string name, QueryTimings child) in timings.Timings ?? new Dictionary<string, QueryTimings>())
                Collect(child, path + name + "/", result);
        }
    }

    /// <summary>The answer of the join index and the answer of the join of the server, which have to be the same. When the indexes moved in between, only what is the same can be compared.</summary>
    public (QueryAnswer Served, QueryAnswer Scan) Both(string rql, IReadOnlyDictionary<string, object> parameters = null)
    {
        ForceFallback(false);
        QueryAnswer served = Run(rql, parameters);
        ForceFallback(true);
        try
        {
            return (served, Run(rql, parameters));
        }
        finally
        {
            ForceFallback(false);
        }
    }

    public static void AssertSame(string rql, QueryAnswer served, QueryAnswer scan)
    {
        Assert.True(scan.Total == served.Total, $"total {served.Total} instead of {scan.Total}: {rql}{Environment.NewLine}only served: {string.Join(", ", served.Rows.Select(x => x.Id).Except(scan.Rows.Select(x => x.Id)).Take(8))}; only scan: {string.Join(", ", scan.Rows.Select(x => x.Id).Except(served.Rows.Select(x => x.Id)).Take(8))}");
        Assert.True(scan.Rows.Select(x => x.Id).SequenceEqual(served.Rows.Select(x => x.Id)), $"documents differ: {rql}{Environment.NewLine}scan   {scan.Describe()}{Environment.NewLine}served {served.Describe()}");
        Assert.True(scan.Rows.Select(x => x.Score).SequenceEqual(served.Rows.Select(x => x.Score)), $"@index-score differs: {rql}{Environment.NewLine}scan   {scan.Describe()}{Environment.NewLine}served {served.Describe()}");
    }

    /// <summary>Compares the two answers of a query and checks that the join index served it (or, with <paramref name="expectServed"/> false, that it did not).</summary>
    public void Check(string rql, IReadOnlyDictionary<string, object> parameters = null, bool expectServed = true)
    {
        long servedBefore = Info.Served;
        (QueryAnswer served, QueryAnswer scan) = Both(rql, parameters);
        long servedNow = Info.Served;
        AssertSame(rql, served, scan);

        if (expectServed)
            Assert.True(servedNow > servedBefore, $"not served by the join index: {rql}. Fallbacks: {string.Join(", ", Info.Fallbacks)}");
        else
            Assert.True(servedNow == servedBefore, $"served by the join index although it should not be: {rql}");
    }

    public long FallbacksOf(string reason) => Info.Fallbacks.GetValueOrDefault(reason);
}
