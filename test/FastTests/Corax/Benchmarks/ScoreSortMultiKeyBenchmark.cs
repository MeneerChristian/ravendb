using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Raven.Client.Documents.Indexes;
using Raven.Client.Exceptions;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// Cost of multi-key sorts that start with score(), with and without a join, on Zipf text
public class ScoreSortMultiKeyBenchmark(ITestOutputHelper output) : RavenTestBase(output)
{
    private const int Vocabulary = 5000;
    private const string OuterSearch = "w1 w4 w12 w30 w90 w300";
    private const string InnerSearch = "w2 w6 w15 w40 w120 w400";
    private static readonly string[] Countries = ["nl", "de", "fr", "be", "uk", "es", "it"];

    [JoinBenchmarkFact("RAVEN_SCORESORT_BENCH", RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void Measure()
    {
        var count = int.Parse(Environment.GetEnvironmentVariable("RAVEN_SCORESORT_BENCH"));
        var runs = int.Parse(Environment.GetEnvironmentVariable("RAVEN_SCORESORT_RUNS") ?? "9");
        var withJoin = Environment.GetEnvironmentVariable("RAVEN_SCORESORT_JOIN") != "0";
        var orders = (Environment.GetEnvironmentVariable("RAVEN_SCORESORT_ORDERS") ??
                      "score();score(), Rank as long;Rank as long, score();Country, Rank as long;Tag, Rank as long;Rank as long, Tag;score(), Tag;score(), Rank as long desc").Split(';');

        using var store = GetDocumentStore(Options.ForSearchEngine(RavenSearchEngineMode.Corax));
        new UsersIndex().Execute(store);
        new GamesIndex().Execute(store);
        var generator = new Generator(7);
        using (var bulk = store.BulkInsert())
        {
            for (var i = 0; i < count; i++)
                bulk.Store(generator.Doc<User>(i, count, "users"), $"users/{i}");
            for (var i = 0; i < count; i++)
                bulk.Store(generator.Doc<Game>(i, count, "games"), $"games/{i}");
        }

        Indexes.WaitForIndexing(store, timeout: TimeSpan.FromMinutes(60));

        var report = new StringBuilder();
        report.AppendLine($"## {count} games / {count} users, median of {runs} runs after 3 warm-up, server DurationInMs");
        report.AppendLine("| join | outer where | order by | limit | total | ms |");
        report.AppendLine("|---|---|---|---|---|---|");
        var totals = new Dictionary<string, long>();
        foreach (var join in withJoin ? new[] { false, true } : new[] { false })
        foreach (var searchWhere in new[] { true, false })
        foreach (var order in orders)
        foreach (var take in new[] { 20, 1000 })
        {
            string rql;
            if (join)
            {
                var o = string.Join(", ", order.Split(", ").Select(x => x.StartsWith("score", StringComparison.Ordinal) ? x : "g." + x));
                var where = searchWhere ? $"where search(g.Name, '{OuterSearch}')" : "";
                rql = $"from index 'Games' as g join (from index 'Users' where search(Name, '{InnerSearch}')) as u on g.OwnerId = id(u) {where} order by {o} limit 0, {take}";
            }
            else
            {
                var where = searchWhere ? $"where search(Name, '{OuterSearch}')" : "";
                rql = $"from index 'Games' {where} order by {order} limit 0, {take}";
            }

            var ms = new List<long>();
            long total = 0;
            try
            {
                for (var i = 0; i < 3 + runs; i++)
                {
                    using var session = store.OpenSession();
                    session.Advanced.RawQuery<Doc>(rql).NoCaching().Statistics(out var stats).ToList();
                    total = stats.TotalResults;
                    if (i >= 3)
                        ms.Add(stats.DurationInMs);
                }
            }
            catch (RavenException)
            {
                report.AppendLine($"| {(join ? "id join" : "no")} | {(searchWhere ? "search" : "none")} | {order} | {take} | not supported | - |");
                continue;
            }

            // the total counts every match, whatever the page size or the order
            var shape = $"{join}|{searchWhere}";
            if (totals.TryGetValue(shape, out var known))
                Assert.Equal(known, total);
            else
                totals[shape] = total;

            ms.Sort();
            report.AppendLine($"| {(join ? "id join" : "no")} | {(searchWhere ? "search" : "none")} | {order} | {take} | {total:N0} | {ms[ms.Count / 2]} |");
        }

        output.WriteLine(report.ToString());
        var path = Environment.GetEnvironmentVariable("RAVEN_SCORESORT_REPORT");
        if (string.IsNullOrEmpty(path) == false)
            File.AppendAllText(path, report + Environment.NewLine);
    }

    private sealed class Generator
    {
        private readonly Random _random;
        private readonly double[] _words = Cumulative(Vocabulary);
        private readonly double[] _tags = Cumulative(300);

        public Generator(int seed) => _random = new Random(seed);

        private static double[] Cumulative(int size)
        {
            var result = new double[size];
            var sum = 0.0;
            for (var i = 0; i < size; i++)
            {
                sum += 1.0 / (i + 1);
                result[i] = sum;
            }

            return result;
        }

        private int Zipf(double[] cumulative)
        {
            var index = Array.BinarySearch(cumulative, _random.NextDouble() * cumulative[^1]);
            if (index < 0)
                index = ~index;
            return Math.Min(index, cumulative.Length - 1);
        }

        private string Text()
        {
            var length = 1 + (int)(11 * Math.Pow(_random.NextDouble(), 1.7));
            var words = new List<string>();
            for (var i = 0; i < length; i++)
                words.Add($"w{Zipf(_words) + 1}");
            if (length >= 2 && _random.Next(100) < 30)
                words[_random.Next(length)] = words[_random.Next(length)];
            return string.Join(' ', words);
        }

        public T Doc<T>(int i, int users, string prefix) where T : Doc, new() => new()
        {
            Id = $"{prefix}/{i}",
            Name = Text(),
            OwnerId = $"users/{_random.Next(users)}",
            Tag = _random.Next(100) < 5 ? null : $"t{Zipf(_tags)}",
            Country = Countries[_random.Next(Countries.Length)],
            Rank = _random.Next(1000)
        };
    }

    private class Doc
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public string Tag { get; set; }
        public string Country { get; set; }
        public int Rank { get; set; }
    }

    private sealed class User : Doc;

    private sealed class Game : Doc;

    private sealed class UsersIndex : AbstractIndexCreationTask<User>
    {
        public UsersIndex()
        {
            Map = docs => from d in docs select new { d.Name, d.Tag, d.Country, d.Rank };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Users";
    }

    private sealed class GamesIndex : AbstractIndexCreationTask<Game>
    {
        public GamesIndex()
        {
            Map = docs => from d in docs select new { d.Name, d.OwnerId, d.Tag, d.Country, d.Rank };
            Index(x => x.Name, FieldIndexing.Search);
        }

        public override string IndexName => "Games";
    }
}
