using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Corax;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Session;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Sparrow.Utils;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Compat.CompatDb;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>
/// A whole join result read through the join index: streamed, read page by page, read again after a refresh and while another session writes, in the three storage modes of the join index and by the join of the server.
/// The data is 141,000 games of which 128,006 are owned by a vip user (8,016 keys: 16 owners of 2,000 games, which are more than the 64 entries the lazy match of the Indirect mode copies of a list, and 8,000 owners of 12 games),
/// so the join index serves it with the lazy match of the keys. The ranks are a permutation, so an order by rank has no ties and an order by publisher has ten groups of ties.
/// </summary>
public class JoinStreamAndOrderTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string FromJoin = "from index 'Games' as g " + VipJoin;
    private const string ByRank = "order by g.Rank as long";
    private const string ByPublisher = "order by g.PublisherId";

    private const int BigOwners = 16;
    private const int GamesOfBigOwner = 2000;
    private const int SmallOwners = 8000;
    private const int GamesOfSmallOwner = 12;
    private const int PlainOwners = 400;
    private const int GamesOfPlainOwner = 30;
    private const int GamesOfNoOwner = 1000;
    private const int BulkGames = BigOwners * GamesOfBigOwner + SmallOwners * GamesOfSmallOwner + PlainOwners * GamesOfPlainOwner + GamesOfNoOwner;
    private const int MatchedGames = BigOwners * GamesOfBigOwner + SmallOwners * GamesOfSmallOwner + 6;

    private enum Kind
    {
        /// <summary>An order on a field that has no ties.</summary>
        Unique,

        /// <summary>An order on a field that has ten values for the games.</summary>
        Ties,

        /// <summary>No order by: the order of the entries.</summary>
        Unordered
    }

    private sealed record Shape(string Name, Kind Kind, string Tail)
    {
        public string Rql => $"{FromJoin} {Tail}".TrimEnd();
    }

    private static readonly Shape[] Shapes =
    [
        new("rank", Kind.Unique, ByRank),
        new("publisher", Kind.Ties, ByPublisher),
        new("unordered", Kind.Unordered, "")
    ];

    private sealed record Row(string Id, int Insertion, string Owner, long Rank, string Publisher, bool Matched);

    private sealed class Data
    {
        public readonly Dictionary<string, Row> Rows = new();
        public List<Row> Matched => Rows.Values.Where(x => x.Matched).ToList();
    }

    private static bool IsVip(string owner) => owner is "users/strong" or "users/weak" || owner.StartsWith("users/big/", StringComparison.Ordinal) || owner.StartsWith("users/small/", StringComparison.Ordinal);

    private async Task<Data> LoadAsync(CompatDb db)
    {
        var data = new Data();
        string[] ownersOfTheOriginals = ["users/strong", "users/weak", "users/other", "users/missing"];
        for (int i = 0; i < 12; i++)
        {
            string owner = ownersOfTheOriginals[i % ownersOfTheOriginals.Length];
            data.Rows[$"games/{i:D3}"] = new Row($"games/{i:D3}", i, owner, i, i % 2 == 0 ? "publishers/1" : "publishers/2", IsVip(owner));
        }

        var owners = new List<string>(BulkGames);
        for (int i = 0; i < BigOwners; i++)
            owners.AddRange(Enumerable.Repeat($"users/big/{i:D2}", GamesOfBigOwner));

        for (int i = 0; i < SmallOwners; i++)
            owners.AddRange(Enumerable.Repeat($"users/small/{i:D4}", GamesOfSmallOwner));

        for (int i = 0; i < PlainOwners; i++)
            owners.AddRange(Enumerable.Repeat($"users/plain/{i:D3}", GamesOfPlainOwner));

        for (int i = 0; i < GamesOfNoOwner; i++)
            owners.Add($"users/gone/{i:D4}");

        Assert.Equal(BulkGames, owners.Count);
        var random = new Random(7);
        for (int i = owners.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (owners[i], owners[j]) = (owners[j], owners[i]);
        }

        var names = new[] { "zelda adventure", "mario kart", "zelda breath" };
        float[] embedding = Enumerable.Range(0, 24).Select(x => x / 24f).ToArray();
        using (var bulk = db.Store.BulkInsert())
        {
            for (int i = 0; i < BigOwners; i++)
                await bulk.StoreAsync(new CompatUser { Name = "big " + i, Kind = "vip", Number = i }, $"users/big/{i:D2}");

            for (int i = 0; i < SmallOwners; i++)
                await bulk.StoreAsync(new CompatUser { Name = "small " + i, Kind = "vip", Number = BigOwners + i }, $"users/small/{i:D4}");

            for (int i = 0; i < PlainOwners; i++)
                await bulk.StoreAsync(new CompatUser { Name = "plain " + i, Kind = "plain", Number = i }, $"users/plain/{i:D3}");

            for (int n = 0; n < BulkGames; n++)
            {
                string id = $"games/b{n:D6}";
                long rank = 1000 + n * 7919L % BulkGames;
                string publisher = $"publishers/{n % 10}";
                await bulk.StoreAsync(new CompatGame { Name = names[n % names.Length], OwnerId = owners[n], PublisherId = publisher, Rank = rank, Latitude = 52, Longitude = 5, Embedding = embedding }, id);
                data.Rows[id] = new Row(id, 12 + n, owners[n], rank, publisher, IsVip(owners[n]));
            }
        }

        Indexes.WaitForIndexing(db.Store, timeout: TimeSpan.FromMinutes(10));
        await WaitForTheJoinAsync(db);
        Assert.Equal(MatchedGames, data.Matched.Count);
        return data;
    }

    private static async Task WaitForTheJoinAsync(CompatDb db)
    {
        if (db.Mode == CompatMode.None)
            return;

        if (db.Mode == CompatMode.Auto)
        {
            db.Run($"{FromJoin} limit 0, 1");
            for (int i = 0; i < 6000 && db.Database.IndexStore.GetIndex(AutoJoin) == null; i++)
                await Task.Delay(50);

            Assert.NotNull(db.Database.IndexStore.GetIndex(AutoJoin));
        }

        for (int i = 0; i < 6000; i++)
        {
            IndexStats stats = db.Stats();
            if (stats.IsStale == false && stats.Join.PendingReason == null && stats.State == IndexState.Normal && stats.Join.Builds > 0)
                return;

            await Task.Delay(50);
        }

        Assert.Fail("the join index did not become current");
    }

    private static List<string> Stream(IDocumentStore store, string rql)
    {
        using IDocumentSession session = store.OpenSession(new SessionOptions { NoCaching = true });
        using IEnumerator<StreamResult<CompatGame>> stream = session.Advanced.Stream(session.Advanced.RawQuery<CompatGame>(rql));
        var ids = new List<string>(MatchedGames);
        while (stream.MoveNext())
            ids.Add(stream.Current.Id);

        return ids;
    }

    private static List<string> Pages(CompatDb db, string rql, int size)
    {
        var all = new List<string>();
        for (int offset = 0;; offset += size)
        {
            List<string> page = db.Run($"{rql} limit {offset}, {size}").Ids;
            all.AddRange(page);
            if (page.Count < size)
                return all;
        }
    }

    /// <summary>Reads as the join of the server does: the join index of the database is made to fall back, or, with no join index, the join is always the server's.</summary>
    private static T OnTheServerJoin<T>(CompatDb db, Func<T> read)
    {
        if (db.Mode == CompatMode.None)
            return read();

        var joinIndex = (JoinIndex)db.Database.IndexStore.GetIndex(db.JoinName);
        joinIndex.QueryHooks = new JoinIndexQueryHooks { ForceFallback = true };
        try
        {
            return read();
        }
        finally
        {
            joinIndex.QueryHooks = null;
        }
    }

    private static JoinIndexStats Snap(CompatDb db) => db.Mode == CompatMode.None ? null : db.Snapshot();

    private static void AssertServed(CompatDb db, JoinIndexStats before, string what, long atLeast = 1)
    {
        if (db.Mode == CompatMode.None)
            return;

        Outcome outcome = Since(before, Snap(db));
        Assert.True(outcome.Served >= atLeast && outcome.Fallbacks == 0, $"{what}: the join index did not serve it: {outcome.Describe()}");
    }

    private static string Digest(List<string> ids) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join(',', ids)))).Substring(0, 12);

    private void Record(string line)
    {
        output.WriteLine(line);
        JoinTestHelpers.AppendReport("STREAM_ORDER_REPORT", "join-stream-order.txt", line);
    }

    private static void AssertSameSet(List<string> actual, IEnumerable<string> expected, string what)
    {
        Assert.True(actual.Count == actual.Distinct().Count(), $"{what}: an id is returned twice");
        Assert.True(actual.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(x => x, StringComparer.Ordinal)), $"{what}: the set of the ids is not the set of the join ({actual.Count} ids instead of {expected.Count()})");
    }

    /// <summary>The order of the ties: the number of neighbours of one group that are not in the order that the documents were stored (the order of the entry ids).</summary>
    private static (int Groups, int Inversions) TiesAgainstInsertion(List<string> ids, Data data)
    {
        int groups = 1, inversions = 0;
        for (int i = 1; i < ids.Count; i++)
        {
            Row a = data.Rows[ids[i - 1]], b = data.Rows[ids[i]];
            Assert.True(string.CompareOrdinal(a.Publisher, b.Publisher) <= 0, $"the publishers are not in order at {i}: {a.Publisher} then {b.Publisher}");
            if (a.Publisher != b.Publisher)
                groups++;
            else if (a.Insertion > b.Insertion)
                inversions++;
        }

        return (groups, inversions);
    }

    private static int InsertionInversions(List<string> ids, Data data)
    {
        int inversions = 0;
        for (int i = 1; i < ids.Count; i++)
        {
            if (data.Rows[ids[i - 1]].Insertion > data.Rows[ids[i]].Insertion)
                inversions++;
        }

        return inversions;
    }

    private void AssertContent(Shape shape, List<string> ids, Data data, string what)
    {
        Assert.Equal(MatchedGames, ids.Count);
        AssertSameSet(ids, data.Matched.Select(x => x.Id), what);
        if (shape.Kind == Kind.Unique)
            Assert.True(data.Matched.OrderBy(x => x.Rank).Select(x => x.Id).SequenceEqual(ids), $"{what}: the rows are not in the order of the ranks");
        else if (shape.Kind == Kind.Ties)
            TiesAgainstInsertion(ids, data);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(CompatMode.Expanded)]
    [InlineData(CompatMode.Indirect)]
    [InlineData(CompatMode.Auto)]
    public async Task TheWholeJoinIsStreamedAsItIsPagedAndAsTheServerJoinsItAndTheOrderIsStable(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode);
        Data data = await LoadAsync(db);
        Assert.True(MatchedGames >= 100_000);

        var first = new Dictionary<string, List<string>>();
        foreach (Shape shape in Shapes)
        {
            JoinIndexStats before = Snap(db);
            List<string> streamed = Stream(db.Store, shape.Rql);
            AssertServed(db, before, $"[{mode} {shape.Name}] the stream");
            AssertContent(shape, streamed, data, $"[{mode} {shape.Name}] the stream");
            first[shape.Name] = streamed;

            before = Snap(db);
            List<string> pages1000 = Pages(db, shape.Rql, 1000);
            AssertServed(db, before, $"[{mode} {shape.Name}] the pages of 1000", MatchedGames / 1000);
            Assert.True(streamed.SequenceEqual(pages1000), $"[{mode} {shape.Name}] the pages of 1000 are not the stream");

            List<string> pages128 = Pages(db, shape.Rql, 128);
            Assert.True(streamed.SequenceEqual(pages128), $"[{mode} {shape.Name}] the pages of 128 are not the stream");

            // the same query twice in a row
            before = Snap(db);
            Assert.True(streamed.SequenceEqual(Stream(db.Store, shape.Rql)), $"[{mode} {shape.Name}] the second stream is in another order");
            AssertServed(db, before, $"[{mode} {shape.Name}] the second stream");

            // the join of the server of the same database: the same rows, and the same order wherever the order is defined
            List<string> serverJoin = OnTheServerJoin(db, () => Stream(db.Store, shape.Rql));
            AssertSameSet(serverJoin, streamed, $"[{mode} {shape.Name}] the server join");
            bool sameOrder = serverJoin.SequenceEqual(streamed);
            Assert.True(sameOrder, $"[{mode} {shape.Name}] the server join is in another order");

            // ties and the rows of a query that has no order by come in the order of the entries, which is the order that the documents were stored in
            string detail = "";
            if (shape.Kind == Kind.Ties)
            {
                (int groups, int inversions) = TiesAgainstInsertion(streamed, data);
                Assert.Equal(10, groups);
                Assert.True(inversions == 0, $"[{mode} {shape.Name}] {inversions} neighbours of a group of ties are not in the order that they were stored in");
                detail = $"ties: {groups} groups, {inversions} neighbours against the order of storing";
            }
            else if (shape.Kind == Kind.Unordered)
            {
                int inversions = InsertionInversions(streamed, data);
                Assert.True(inversions == 0, $"[{mode} {shape.Name}] {inversions} neighbours are not in the order that the documents were stored in");
                detail = $"entry order against the order of storing: {inversions} inversions";
            }

            Record($"STREAM_ORDER {mode} {shape.Name} | rows {streamed.Count} | digest {Digest(streamed)} | stream = pages(1000) = pages(128) = second stream | order equals the server join: {sameOrder} | {detail}");
        }

        // a refresh that changes nothing in the result: a game of an owner that is not vip is stored and deleted again
        using (IDocumentSession session = db.Store.OpenSession())
        {
            session.Store(new CompatGame { Name = "noise", OwnerId = "users/plain/000", PublisherId = "publishers/3", Rank = 5_000_000, Latitude = 52, Longitude = 5 }, "games/noise-1");
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(db.Store, timeout: TimeSpan.FromMinutes(5));
        await WaitForTheJoinAsync(db);
        using (IDocumentSession session = db.Store.OpenSession())
        {
            session.Delete("games/noise-1");
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(db.Store, timeout: TimeSpan.FromMinutes(5));
        await WaitForTheJoinAsync(db);

        foreach (Shape shape in Shapes)
        {
            JoinIndexStats before = Snap(db);
            List<string> after = Stream(db.Store, shape.Rql);
            AssertServed(db, before, $"[{mode} {shape.Name}] the stream after the refresh");
            Assert.True(first[shape.Name].SequenceEqual(after), $"[{mode} {shape.Name}] the order changed after a refresh that changed nothing");
        }
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(CompatMode.Expanded)]
    [InlineData(CompatMode.Indirect)]
    [InlineData(CompatMode.Auto)]
    public async Task AStreamWhileDocumentsOutsideTheJoinAreWrittenGivesExactlyTheResultThatWasThere(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode);
        Data data = await LoadAsync(db);
        var baseline = Shapes.ToDictionary(x => x.Name, x => Stream(db.Store, x.Rql));

        int adds = 0, deletes = 0, duringTheWrites = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task writer = Task.Run(() =>
        {
            try
            {
                var live = new Queue<string>();
                while (adds < 4000 || Volatile.Read(ref duringTheWrites) < 6)
                {
                    using IDocumentSession session = db.Store.OpenSession();
                    for (int i = 0; i < 50; i++)
                    {
                        string id = $"games/noise-{adds + i}";
                        session.Store(new CompatGame { Name = "noise", OwnerId = i % 2 == 0 ? "users/plain/001" : "users/nobody", PublisherId = "publishers/4", Rank = 5_000_000 + adds + i, Latitude = 52, Longitude = 5 }, id);
                        live.Enqueue(id);
                    }

                    for (int i = 0; i < 25 && live.Count > 100; i++)
                    {
                        session.Delete(live.Dequeue());
                        deletes++;
                    }

                    session.SaveChanges();
                    adds += 50;
                    started.TrySetResult();
                }
            }
            catch (Exception e)
            {
                started.TrySetException(e);
                throw;
            }
        });

        await started.Task;
        JoinIndexStats before = Snap(db);
        int streams = 0;
        while (writer.IsCompleted == false || streams < 3)
        {
            foreach (Shape shape in Shapes)
            {
                bool writing = writer.IsCompleted == false;
                List<string> streamed = Stream(db.Store, shape.Rql);
                Assert.True(baseline[shape.Name].SequenceEqual(streamed), $"[{mode} {shape.Name}] the stream during the writes is not the result that was there");
                if (writing)
                    Interlocked.Increment(ref duringTheWrites);
            }

            streams++;
        }

        await writer;
        Assert.True(adds >= 4000 && deletes >= 1000 && duringTheWrites >= 6);
        Outcome whileWriting = Since(before, Snap(db));
        Record($"STREAM_WRITES {mode} | streams {streams * Shapes.Length} ({duringTheWrites} while the writer wrote), written {adds}, deleted {deletes} | {whileWriting.Describe()}");

        Indexes.WaitForIndexing(db.Store, timeout: TimeSpan.FromMinutes(5));
        await WaitForTheJoinAsync(db);
        before = Snap(db);
        foreach (Shape shape in Shapes)
            Assert.True(baseline[shape.Name].SequenceEqual(Stream(db.Store, shape.Rql)), $"[{mode} {shape.Name}] the stream after the writes is not the result that was there");

        AssertServed(db, before, $"[{mode}] the streams after the writes", Shapes.Length);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [InlineData(CompatMode.Expanded)]
    [InlineData(CompatMode.Indirect)]
    [InlineData(CompatMode.Auto)]
    public async Task AWhereOnTheJoinAndPagesBeyondTheCopiedEntriesOfTheLazyMatchGiveTheRowsOfTheServerJoin(CompatMode mode)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, mode);
        Data data = await LoadAsync(db);

        var wheres = new (string Name, string Where, Func<Row, bool> Keep)[]
        {
            ("rank", "where g.Rank > 70000", x => x.Rank > 70000),
            ("publisher", "where g.PublisherId = 'publishers/3'", x => x.Publisher == "publishers/3"),
            ("both", "where g.Rank > 70000 and g.PublisherId = 'publishers/3'", x => x.Rank > 70000 && x.Publisher == "publishers/3"),
            ("none", "where g.Rank > 100000000", _ => false)
        };

        foreach ((string name, string where, Func<Row, bool> keep) in wheres)
        {
            List<Row> expectedRows = data.Matched.Where(keep).ToList();
            foreach (bool ordered in new[] { true, false })
            {
                string rql = $"{FromJoin} {where} {(ordered ? ByRank : "")}".TrimEnd();
                string what = $"[{mode} where {name} {(ordered ? "ordered" : "unordered")}]";
                JoinIndexStats before = Snap(db);
                List<string> streamed = Stream(db.Store, rql);
                AssertServed(db, before, what);
                AssertSameSet(streamed, expectedRows.Select(x => x.Id), what);
                if (ordered)
                    Assert.True(expectedRows.OrderBy(x => x.Rank).Select(x => x.Id).SequenceEqual(streamed), $"{what}: not in the order of the ranks");

                List<string> serverJoin = OnTheServerJoin(db, () => Stream(db.Store, rql));
                Assert.True(serverJoin.SequenceEqual(streamed), $"{what}: the stream is not the stream of the server join ({streamed.Count} and {serverJoin.Count} rows)");
                Assert.True(streamed.SequenceEqual(Pages(db, rql, 1000)), $"{what}: the pages are not the stream");
                Assert.True(streamed.SequenceEqual(Stream(db.Store, rql)), $"{what}: the second stream is in another order");
            }
        }

        // offsets and sizes around the 64 entries that the lazy match copies of every list, and beyond the end
        foreach (string tail in new[] { "", ByRank, "where g.Rank > 70000", "where g.Rank > 70000 " + ByRank })
        {
            string rql = $"{FromJoin} {tail}".TrimEnd();
            List<string> whole = Stream(db.Store, rql);
            foreach (int offset in new[] { 0, 1, 63, 64, 65, 127, 128, 129, 1000, 5000, 100_000, whole.Count - 1, whole.Count, whole.Count + 1 })
            {
                foreach (int size in new[] { 1, 10, 63, 64, 65, 1000 })
                {
                    JoinIndexStats before = Snap(db);
                    List<string> page = db.Run($"{rql} limit {offset}, {size}").Ids;
                    AssertServed(db, before, $"[{mode} '{tail}' limit {offset}, {size}]");
                    List<string> expected = whole.Skip(offset).Take(size).ToList();
                    Assert.True(expected.SequenceEqual(page), $"[{mode} '{tail}' limit {offset}, {size}] the page is not the same part of the stream: {string.Join(",", page.Take(5))} instead of {string.Join(",", expected.Take(5))}");
                }
            }
        }
    }

    private sealed class Peak : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        private long _native, _managed;

        public Peak()
        {
            _native = NativeMemory.TotalAllocatedMemory;
            _managed = GC.GetTotalMemory(false);
            _thread = new Thread(() =>
            {
                while (_stop == false)
                {
                    _native = Math.Max(_native, NativeMemory.TotalAllocatedMemory);
                    _managed = Math.Max(_managed, GC.GetTotalMemory(false));
                    Thread.Sleep(2);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public (long Native, long Managed) Stop()
        {
            _stop = true;
            _thread.Join();
            return (_native, _managed);
        }

        public void Dispose() => Stop();
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    public async Task TheThreeModesAndTheServerJoinStreamTheSameRowsInTheSameOrderAndNoneOfThemHoldsTheRows()
    {
        var results = new Dictionary<CompatMode, Dictionary<string, List<string>>>();
        Data data = null;
        foreach (CompatMode mode in new[] { CompatMode.None, CompatMode.Expanded, CompatMode.Indirect, CompatMode.Auto })
        {
            using CompatDb db = await CompatDb.CreateAsync(this, mode);
            data = await LoadAsync(db);
            var streams = new Dictionary<string, List<string>>();
            foreach (Shape shape in Shapes)
            {
                // the first read warms the caches: the memory is that of the second
                JoinIndexStats before = Snap(db);
                Stream(db.Store, shape.Rql);
                AssertServed(db, before, $"[{mode} {shape.Name}] the stream");
                GC.Collect();
                long nativeBefore = NativeMemory.TotalAllocatedMemory;
                long managedBefore = GC.GetTotalMemory(true);
                long allocatedBefore = GC.GetTotalAllocatedBytes(false);
                var peak = new Peak();
                List<string> ids;
                try
                {
                    ids = Stream(db.Store, shape.Rql);
                }
                finally
                {
                    (long native, long managed) = peak.Stop();
                    long allocated = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
                    Record($"STREAM_MEMORY {mode} {shape.Name} | native peak growth {(native - nativeBefore) / 1024.0 / 1024:F1} MB, managed peak growth {(managed - managedBefore) / 1024.0 / 1024:F1} MB (the ids of the client are about {MatchedGames * 70L / 1024.0 / 1024:F0} MB of it), managed allocated {allocated / 1024.0 / 1024:F0} MB");

                    // the rows are about 450 bytes as a document, 128,006 of them are some 55 MB: holding them all is over what the stream may grow
                    Assert.True(native - nativeBefore < 64L * 1024 * 1024, $"[{mode} {shape.Name}] the native memory grew by {(native - nativeBefore) / 1024.0 / 1024:F1} MB while the rows were streamed");
                }

                AssertContent(shape, ids, data, $"[{mode} {shape.Name}]");
                streams[shape.Name] = ids;
            }

            results[mode] = streams;
        }

        foreach (Shape shape in Shapes)
        {
            List<string> serverJoin = results[CompatMode.None][shape.Name];
            foreach (CompatMode mode in new[] { CompatMode.Expanded, CompatMode.Indirect, CompatMode.Auto })
            {
                bool same = serverJoin.SequenceEqual(results[mode][shape.Name]);
                Record($"STREAM_CROSS {shape.Name} | {mode} = server join (another database): {same} | digests server {Digest(serverJoin)}, {mode} {Digest(results[mode][shape.Name])}");
                Assert.True(same, $"[{shape.Name}] {mode} streams the rows in another order than the server join");
            }
        }
    }
}
