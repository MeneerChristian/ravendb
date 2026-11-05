using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Tests.Infrastructure;

namespace FastTests.Corax;

// What the join tests and benchmarks share
internal static class JoinTestHelpers
{
    // The stored sort term of an entry that holds several terms is its smallest one only when the entry was indexed in the same batch as all the others.
    // Stopping the indexing while the documents are stored keeps the load of the machine from cutting the batch, but the index also ends a batch early
    // on its own when the memory of the machine is low. Starts the indexing and waits for it.
    public static void StartIndexingAndWait(RavenTestBase test, IDocumentStore store)
    {
        store.Maintenance.Send(new StartIndexingOperation());
        test.Indexes.WaitForIndexing(store);
    }

    public static bool IndexedInOneBatch(IDocumentStore store, string indexName)
        => store.Maintenance.Send(new GetIndexPerformanceStatisticsOperation([indexName]))[0].Performance.Count(x => x.InputCount > 0) == 1;

    public static RavenTestBase.Options CoraxOptions()
    {
        var options = RavenTestBase.Options.ForSearchEngine(RavenSearchEngineMode.Corax);
        options.ModifyDatabaseRecord += record => record.Settings[RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString();
        return options;
    }

    public static double Median(IEnumerable<double> values) => Percentile(values, 0.5);

    public static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(x => x).ToList();
        return sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(p * sorted.Count))];
    }

    // Appends a benchmark report to the file named by the environment variable, or to a file of the temp directory
    public static void AppendReport(string environmentVariable, string defaultFileName, object report)
    {
        var path = Environment.GetEnvironmentVariable(environmentVariable) ?? Path.Combine(Path.GetTempPath(), defaultFileName);
        File.AppendAllText(path, report.ToString() + Environment.NewLine);
    }
}
