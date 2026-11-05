using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Corax.Querying;
using Raven.Client;
using Raven.Client.Documents;
using Sparrow.Threading;
using Sparrow.Server;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

// The pooled arrays of a JOIN belong to the join until it is disposed
public class JoinPoolLifetimeTests(ITestOutputHelper output, SharedJoinDatabase shared) : RavenTestBase(output), IClassFixture<SharedJoinDatabase>
{
    private const string Everything = "limit 0, 1000000";

    private static void Poison<T>(Random random, int size, Func<int, T> garbage)
    {
        // many arrays at once, so that the pool cannot answer them from the arrays that this thread returned last
        var held = new List<T[]>();
        for (var i = 0; i < (size <= 1 << 16 ? 24 : 3); i++)
        {
            var array = ArrayPool<T>.Shared.Rent(size);
            for (var j = 0; j < array.Length; j++)
                array[j] = garbage(random.Next());
            held.Add(array);
        }

        foreach (var array in held)
            ArrayPool<T>.Shared.Return(array);
    }

    private static void PoisonEverySize(Random random, int from, int to)
    {
        for (var size = from; size <= to; size = size < 16 ? 16 : size * 2)
        {
            Poison(random, size, x => x % 7 - 3);
            Poison(random, size, x => (long)x * 31 - 5);
            Poison(random, size, x => (float)(x % 5) - 1);
            Poison(random, size, x => (byte)x);
        }
    }

    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(100)]
    [InlineData(2_000)]
    [InlineData(70_000)]
    public void TheOrderOfTheKeysIsNotHandedToAnyoneElseWhileTheyAreInUse(int count)
    {
        using var allocator = new ByteStringContext(SharedMultipleUseFlag.None);
        using var keys = new JoinTermScores(allocator);
        var random = new Random(count);
        var strings = Enumerable.Range(0, count).Select(i => $"users/{random.Next(10_000_000)}-{i}").ToList();
        foreach (var key in strings)
            keys.Add(Encoding.UTF8.GetBytes(key), (float)random.NextDouble());

        var held = keys.SortedOrder(); // a caller keeps the span while it works on the keys (the score map does for the whole loop of its lookups)
        var expected = held.ToArray();

        for (var round = 0; round < 4; round++)
        {
            // the thread of the query rents and overwrites arrays of every size between two uses of the order
            PoisonEverySize(random, 1, 1 << 18);
            Assert.True(held.SequenceEqual(expected), $"round {round}: the order of the keys was overwritten");

            // and so do other threads
            Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, worker => PoisonEverySize(new Random(worker + round), 1, 1 << 18));
            Assert.True(held.SequenceEqual(expected), $"round {round}: the order of the keys was overwritten by another thread");

            // a lookup after that finds every key, and asks the keys again for the order
            Assert.True(keys.SortedOrder().SequenceEqual(expected));
            foreach (var key in strings.Where((_, i) => i % 3 == round % 3))
                Assert.True(keys.TryFind(Encoding.UTF8.GetBytes(key), out var index) && Encoding.UTF8.GetString(keys.Term(index)) == key, key);

            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        }
    }


    private IDocumentStore Seed() => shared.Get("pool-lifetime", Options.ForSearchEngine(RavenSearchEngineMode.Corax, includeScoresAndDistances: true), o => GetDocumentStore(o),
        store => JoinArrayKeyTests.SeedData(store, s => Indexes.WaitForIndexing(s)));

    private const string Outer = "from index 'ArrGames' as g";
    private const string Products = "(from index 'ArrProducts' where search(Name, 'word3 word5')) as p";
    private const string ProductsOfTheLowerHalf = "(from index 'ArrProducts' where Rank < 100 and search(Name, 'word3 word5 word2')) as q";

    private static readonly (string Name, string Rql)[] Queries =
    [
        ("id join (list), scores", $"{Outer} join {Products} on g.ProductIds = id(p) where search(g.Name, 'g1 g4 common') order by score() {Everything}"),
        ("id join (list), left, boost", $"{Outer} left join {Products} on g.ProductIds = id(p) boost 2 where search(g.Name, 'g1 g4 common') order by score() {Everything}"),
        ("id join (list), no scores", $"{Outer} join {Products} on g.ProductIds = id(p) where g.Size < 6 {Everything}"),
        ("id join (list), ordered by a field", $"{Outer} join {Products} on g.ProductIds = id(p) order by g.Size desc, g.Name {Everything}"),
        ("id join (list), projection", $"{Outer} left join {Products} on g.ProductIds = id(p) load g.ProductIds as p[] select g.Name as GameName, p.Name as ProductNames {Everything}"),
        ("field join, tags = codes", $"{Outer} join {Products} on g.Tags = p.Codes order by score() {Everything}"),
        ("field join, tags = bucket, no scores", $"{Outer} join {Products} on g.Tags = p.Bucket {Everything}"),
        ("field join, bucket = codes, projection", $"{Outer} left join {Products} on g.Bucket = p.Codes select g.Name as GameName, p.Name as ProductName {Everything}"),
        ("two joins, AND", $"{Outer} join {Products} on g.ProductIds = id(p) join {ProductsOfTheLowerHalf} on g.Tags = q.Codes order by score() {Everything}"),
        ("two joins, OR", $"{Outer} join {Products} on g.ProductIds = id(p) join {ProductsOfTheLowerHalf} on g.Tags = q.Codes where matches(p) or matches(q) order by score() {Everything}"),
        ("two joins on ids, OR", $"{Outer} join {Products} on g.ProductIds = id(p) join (from index 'ArrProducts' where Rank < 100) as r on g.ProductIds = id(r) where matches(p) or matches(r) order by score() {Everything}"),
    ];

    private sealed record Row(string Id, double Score, string Extra);

    private static List<Row> Run(IDocumentStore store, string rql)
    {
        using var session = store.OpenSession();
        if (rql.Contains(" select "))
        {
            return session.Advanced.RawQuery<Projection>(rql).ToList()
                .Select(x => new Row(x.GameName, 0, x.ProductName ?? string.Join(',', (x.ProductNames ?? []).OrderBy(n => n, StringComparer.Ordinal))))
                .OrderBy(x => x.Id, StringComparer.Ordinal).ThenBy(x => x.Extra, StringComparer.Ordinal).ToList();
        }

        var rows = new List<Row>();
        foreach (var game in session.Advanced.RawQuery<JoinArrayKeyTests.ArrGame>(rql).ToList())
        {
            var metadata = session.Advanced.GetMetadataFor(game);
            rows.Add(new Row((string)metadata[Constants.Documents.Metadata.Id], metadata.ContainsKey(Constants.Documents.Metadata.IndexScore) ? Convert.ToDouble(metadata[Constants.Documents.Metadata.IndexScore]) : 0, null));
        }

        return rows.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
    }

    private sealed class Projection
    {
        public string GameName { get; set; }
        public string ProductName { get; set; }
        public string[] ProductNames { get; set; }
    }

    private static string Differences(List<Row> expected, List<Row> actual)
    {
        if (expected.Count != actual.Count)
            return $"{actual.Count} rows instead of {expected.Count}";

        for (var i = 0; i < expected.Count; i++)
        {
            if (expected[i].Id != actual[i].Id || expected[i].Extra != actual[i].Extra || Math.Abs(expected[i].Score - actual[i].Score) > 1e-4)
                return $"row {i}: {actual[i]} instead of {expected[i]}";
        }

        return null;
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void QueriesGiveTheSameRowsWhileOtherThreadsRentAndOverwritePooledArrays()
    {
        using var store = Seed();
        var baseline = Queries.ToDictionary(x => x.Name, x => Run(store, x.Rql));
        Assert.All(baseline, x => Assert.NotEmpty(x.Value));

        using var stop = new CancellationTokenSource();
        var poisoners = Enumerable.Range(0, 3).Select(n => Task.Factory.StartNew(() =>
        {
            var random = new Random(n);
            var collections = 0;
            while (stop.IsCancellationRequested == false)
            {
                PoisonEverySize(random, 1, 1 << 20);
                if (n == 0 && ++collections % 25 == 0)
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true); // the pool gives the arrays that its threads cache back to its shared stacks
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        var failures = new List<string>();
        try
        {
            for (var round = 0; round < 6; round++)
            {
                var round1 = round;
                Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, worker =>
                {
                    var shift = (worker * 3 + round1) % Queries.Length;
                    foreach (var (name, rql) in Queries.Skip(shift).Concat(Queries.Take(shift)))
                    {
                        var difference = Differences(baseline[name], Run(store, rql));
                        if (difference != null)
                        {
                            lock (failures)
                                failures.Add($"round {round1}, worker {worker}, '{name}': {difference}");
                        }
                    }
                });
            }
        }
        finally
        {
            stop.Cancel();
            Task.WaitAll(poisoners);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(10)));
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void AStreamKeepsItsPooledArraysWhileOtherQueriesRunAndThePoolIsPoisoned()
    {
        using var store = Seed();
        var baseline = Queries.ToDictionary(x => x.Name, x => Run(store, x.Rql));

        foreach (var rql in new[] { Queries[2].Rql, Queries[3].Rql, Queries[6].Rql, Queries[9].Rql })
        {
            List<string> Stream()
            {
                using var session = store.OpenSession();
                var ids = new List<string>();
                using var stream = session.Advanced.Stream<JoinArrayKeyTests.ArrGame>(session.Advanced.RawQuery<JoinArrayKeyTests.ArrGame>(rql));
                while (stream.MoveNext())
                    ids.Add(stream.Current.Id);
                return ids;
            }

            var expected = Stream();
            Assert.NotEmpty(expected);

            using var session = store.OpenSession();
            var ids = new List<string>();
            var random = new Random(5);
            using var open = session.Advanced.Stream<JoinArrayKeyTests.ArrGame>(session.Advanced.RawQuery<JoinArrayKeyTests.ArrGame>(rql));
            while (open.MoveNext())
            {
                ids.Add(open.Current.Id);
                if (ids.Count % 50 != 1)
                    continue;

                // between two rows of the stream
                PoisonEverySize(random, 1, 1 << 18);
                var other = Queries[ids.Count / 50 % Queries.Length];
                Assert.Null(Differences(baseline[other.Name], Run(store, other.Rql)));
                Parallel.For(0, 2, worker => PoisonEverySize(new Random(worker), 1, 1 << 18));
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            }

            Assert.Equal(expected, ids);
        }
    }
}
