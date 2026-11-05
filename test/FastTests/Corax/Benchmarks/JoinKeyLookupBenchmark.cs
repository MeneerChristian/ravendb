using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Corax;
using Corax.Mappings;
using Corax.Querying;
using FastTests.Voron;
using Sparrow.Server;
using Sparrow.Threading;
using Tests.Infrastructure;
using Voron;
using Xunit;
using IndexSearcher = Corax.Querying.IndexSearcher;
using IndexWriter = Corax.Indexing.IndexWriter;

namespace FastTests.Corax;

// Opt-in: RAVEN_JOIN_LOOKUP=&lt;games&gt
public class JoinKeyLookupBenchmark : StorageTest
{
    private const int IdField = 0, OwnerField = 1;
    private static readonly string DataPath = Path.Combine(Path.GetTempPath(), "jl-bench-data");

    private static readonly JoinKeyLookupStrategy[] Strategies = [JoinKeyLookupStrategy.PerKey, JoinKeyLookupStrategy.Cursor, JoinKeyLookupStrategy.Sweep, JoinKeyLookupStrategy.Auto];

    private readonly ITestOutputHelper _output;

    public JoinKeyLookupBenchmark(ITestOutputHelper output) : base(CreateOptions(), output)
    {
        _output = output;
    }

    private static StorageEnvironmentOptions CreateOptions()
    {
        var env = Environment.GetEnvironmentVariable("RAVEN_JOIN_LOOKUP");
        if (string.IsNullOrEmpty(env))
            return StorageEnvironmentOptions.CreateMemoryOnlyForTests();

        var path = DataPath + "-" + env;
        if (Directory.Exists(path))
            Directory.Delete(path, true);
        return StorageEnvironmentOptions.ForPathForTests(path);
    }

    [JoinBenchmarkFact("RAVEN_JOIN_LOOKUP", RavenTestCategory.Corax)]
    public void Run()
    {
        var env = Environment.GetEnvironmentVariable("RAVEN_JOIN_LOOKUP");
        var users = int.Parse(env);
        var reps = int.Parse(Environment.GetEnvironmentVariable("RAVEN_JOIN_LOOKUP_REPS") ?? "11");
        var random = new Random(7);
        var owners = new string[users];
        for (var i = 0; i < users; i++)
            owners[i] = random.Next(25) == 0 ? $"users/missing{i}" : $"users/{random.Next(users)}";

        Index(owners);
        var report = new StringBuilder();
        long treeTerms;
        using (var tx = Env.ReadTransaction())
            treeTerms = tx.ReadTree(Constants.IndexWriter.FieldsSlice).CompactTreeFor("Owner").NumberOfEntries;
        report.AppendLine($"=== {users} games, tree terms {treeTerms}, {reps} reps ({DateTime.Now:yyyy-MM-dd HH:mm}) ===");

        // keys as a fraction of the terms of the tree
        foreach (var density in new[] { 0.001, 0.005, 0.01, 0.02, 0.04, 0.0625, 0.0833, 0.125, 0.1667, 0.25, 0.5, 1.0 })
        {
            var count = (int)(treeTerms * density);
            var keys = Shuffle(Enumerable.Range(0, users).OrderBy(_ => random.Next()).Take(count).Select(i => $"users/{i}").ToList(), random);
            RunSet(report, $"density {density:P2} of the tree terms", keys, reps);
        }

        _output.WriteLine(report.ToString());
        File.AppendAllText(Environment.GetEnvironmentVariable("RAVEN_JOIN_LOOKUP_OUT") ?? Path.Combine(Path.GetTempPath(), $"jl-bench-{users}.txt"), report + Environment.NewLine);
    }

    private void RunSet(StringBuilder report, string label, List<string> keyStrings, int reps)
    {
        var ticks = Strategies.ToDictionary(s => s, _ => new List<double[]>());
        long reference = -1;
        for (var round = -2; round < reps; round++)
        {
            for (var j = 0; j < Strategies.Length; j++)
            {
                var strategy = Strategies[(j + Math.Max(round, 0)) % Strategies.Length];
                JoinKeyLookupSettings.Forced = strategy;
                try
                {
                    using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
                    var keys = new JoinTermScores(searcher.Allocator);
                    foreach (var key in keyStrings)
                        keys.Add(Encoding.UTF8.GetBytes(key), 1f + key.Length % 3 * 0.5f);

                    var field = searcher.GetFieldMetadata("Owner");
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var sw = Stopwatch.StartNew();
                    var map = searcher.ScoreMap(field, keys, Environment.GetEnvironmentVariable("RAVEN_JOIN_LOOKUP_TERMS") != null);
                    sw.Stop();
                    var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    keys.Dispose();

                    var entries = map.Count;
                    if (reference < 0)
                        reference = entries;
                    Assert.Equal(reference, entries);
                    if (round < 0)
                        continue;
                    ticks[strategy].Add([sw.Elapsed.TotalMilliseconds, bytes]);
                }
                finally
                {
                    JoinKeyLookupSettings.Forced = JoinKeyLookupStrategy.Auto;
                }
            }
        }

        report.AppendLine($"--- {label}: {keyStrings.Count} keys, {reference} entries");
        report.AppendLine("  strategy   total ms  ns/key  vs PerKey   managed KB");
        var baseline = Median(ticks[JoinKeyLookupStrategy.PerKey], 0);
        foreach (var strategy in Strategies)
        {
            var t = ticks[strategy];
            var total = Median(t, 0);
            report.AppendLine($"  {strategy,-9} {total,8:F1} {total * 1e6 / Math.Max(1, keyStrings.Count),7:F0}  {(total - baseline) / baseline * 100,+7:F0}%  {Median(t, 1) / 1024,10:F1}");
        }
    }

    private static double Median(List<double[]> values, int column)
    {
        var sorted = values.Select(v => v[column]).OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }

    private static List<string> Shuffle(List<string> list, Random random)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    private void Index(string[] owners)
    {
        using var bsc = new ByteStringContext(SharedMultipleUseFlag.None);
        using var knownFields = CreateKnownFields(bsc);
        using var indexWriter = new IndexWriter(Env, knownFields, SupportedFeatures.All);
        for (var i = 0; i < owners.Length; i++)
        {
            var id = Encoding.UTF8.GetBytes($"games/{i}");
            using var builder = indexWriter.Index(id);
            builder.Write(IdField, null, id);
            builder.Write(OwnerField, null, Encoding.UTF8.GetBytes(owners[i]));
            builder.EndWriting();
        }

        indexWriter.Commit();
    }

    private static IndexFieldsMapping CreateKnownFields(ByteStringContext bsc)
    {
        Slice.From(bsc, "Id", ByteStringType.Immutable, out var idSlice);
        Slice.From(bsc, "Owner", ByteStringType.Immutable, out var ownerSlice);

        using var builder = IndexFieldsMappingBuilder.CreateForWriter(false)
            .AddBinding(IdField, idSlice)
            .AddBinding(OwnerField, ownerSlice);

        return builder.Build();
    }
}
