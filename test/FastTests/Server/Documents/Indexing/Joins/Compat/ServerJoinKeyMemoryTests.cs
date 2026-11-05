using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Corax;
using FastTests.Server.Documents.Indexing.Joins.Query;
using Raven.Client.Documents;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Tests.Infrastructure;
using Xunit;
using static FastTests.Server.Documents.Indexing.Joins.Compat.CompatDb;

namespace FastTests.Server.Documents.Indexing.Joins.Compat;

/// <summary>
/// What the join of the server holds while it reads a sub-query: the keys of every matched document (bytes, end and score). The bound of the keys (Query.JoinMaxKeysMemoryInMb) is tested with the plain join; here the guard is the cost per key.
/// What the keys hold is counted exactly (KeysHeldBytes: the bytes of the keys and 8 bytes kept for each), and that is asserted to the byte. The native bytes that the scan allocated (ScanHeldUnmanagedBytes) are the fresh native allocations of the thread:
/// native lists grow by doubling and the allocator hands out what it kept from an earlier read before it allocates, so they are bounded (not exact) and may be far below what the keys hold. The managed bytes the scan allocated are a constant, not a cost per key.
/// RAVEN_COMPAT_SERVERJOIN_KEYS=n,n,... runs bigger sizes and appends the numbers to the file of RAVEN_COMPAT_SERVERJOIN_OUT.
/// </summary>
public class ServerJoinKeyMemoryTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const string Rql = "from index 'Games' as g join (from index 'Users' where Kind = 'vip') as u on g.OwnerId = id(u) order by id() limit 0, 10 include timings()";

    // the bytes of the ids that the keys are: users/0 ... users/{keys - 1}
    private static long KeyBytes(int keys)
    {
        long bytes = 0;
        for (int i = 0; i < keys; i++)
            bytes += ("users/" + i).Length;

        return bytes;
    }

    // a key that is not encoded keeps its end (an int) and its score (a float) next to its bytes
    private const int KeptPerKey = sizeof(int) + sizeof(float);

    public sealed record Measure(int Keys, long Held, long HeldBytes, long NativeBytes, long ManagedBytes, bool Encoded, long Total)
    {
        public double HeldPerKey => (double)HeldBytes / Keys;
        public double NativePerKey => (double)NativeBytes / Keys;
        public double ManagedPerKey => (double)ManagedBytes / Keys;

        public override string ToString() => $"keys {Keys:N0} (held {Held:N0}, encoded {Encoded}), keys hold {HeldBytes:N0} B = {HeldPerKey:F1} B/key, fresh native allocations {NativeBytes:N0} B = {NativePerKey:F1} B/key, managed allocated {ManagedBytes:N0} B = {ManagedPerKey:F1} B/key, total results {Total}";
    }

    private async Task<Measure> MeasureAsync(int keys)
    {
        using CompatDb db = await CompatDb.CreateAsync(this, CompatMode.None, populate: false);
        await new UsersIndex().ExecuteAsync(db.Store);
        await new GamesIndex().ExecuteAsync(db.Store);
        using (var bulk = db.Store.BulkInsert())
        {
            for (int i = 0; i < keys; i++)
                await bulk.StoreAsync(new CompatUser { Name = "user " + i, Kind = "vip", Number = i }, $"users/{i}");

            for (int i = 0; i < 100; i++)
                await bulk.StoreAsync(new CompatGame { Name = "zelda", OwnerId = $"users/{(i * 7919L) % keys}", Rank = i }, $"games/{i:D3}");
        }

        Indexes.WaitForIndexing(db.Store, timeout: TimeSpan.FromMinutes(10));

        Measure last = null;
        // the first read warms the caches, the numbers are those of the last of three
        for (int i = 0; i < 3; i++)
        {
            using IDocumentSession session = db.Store.OpenSession(new SessionOptions { NoCaching = true });
            List<CompatGame> rows = session.Advanced.RawQuery<CompatGame>(Rql).Timings(out QueryTimings timings).Statistics(out QueryStatistics stats).ToList();
            Assert.Equal(10, rows.Count);
            Assert.Equal(100, stats.TotalResults);
            Dictionary<string, long> counters = JoinIndexQueryOracle.CountersOf(timings);
            last = new Measure(keys, counters["Join/u/SourceMatches"], counters["Join/u/KeysHeldBytes"], counters["Join/u/ScanHeldUnmanagedBytes"], counters["Join/u/ScanAllocatedManagedBytes"], counters["Join/u/KeysEncoded"] == 1, stats.TotalResults);
        }

        output.WriteLine(last.ToString());
        return last;
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task TheServerJoinHoldsEveryMatchedKeyAndTheCostPerKeyIsBoundedAndDoesNotGrow()
    {
        Measure small = await MeasureAsync(10_000);
        Measure big = await MeasureAsync(100_000);

        foreach (Measure m in new[] { small, big })
        {
            // exact: the join holds one key for every document the sub-query matched
            Assert.Equal(m.Keys, m.Held);

            // exact: what the keys hold is the bytes of the ids and what is kept for each of them, nothing more (the ids are not encoded in this index)
            Assert.False(m.Encoded);
            Assert.Equal(KeyBytes(m.Keys) + (long)m.Keys * KeptPerKey, m.HeldBytes);

            // the native lists double when they are full and a list is never more than twice what it holds (and a few bytes of header), so the lists of the keys hold less than 2 x the bytes of the keys; the fresh allocations that
            // a scan makes are the lists as they grew (a series that sums to less than twice the last one) and the allocator keeps the memory of the earlier reads for the next, so there is a ceiling and no floor
            Assert.True(m.NativeBytes <= 4 * m.HeldBytes + 1024 * 1024, $"the native allocations of the scan are more than the growth of the keys allows: {m}");

            // the scan allocates nothing per key on the managed heap: a constant, not more than a few bytes per key
            Assert.True(m.ManagedBytes < 512 * 1024 + 8L * m.Keys, $"the scan allocates managed memory per key: {m}");
        }

        // what a key costs does not depend on the number of keys: ten times the keys hold the same bytes per key (but the digit more of the ids) and allocate the same few managed bytes per key
        Assert.True(big.HeldPerKey < small.HeldPerKey * 1.15, $"the keys hold more per key when there are more of them: {small} / {big}");
        Assert.True(big.ManagedPerKey <= small.ManagedPerKey, $"the managed bytes per key grow with the number of keys: {small} / {big}");
    }

    [JoinBenchmarkFact("RAVEN_COMPAT_SERVERJOIN_KEYS", RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public async Task MeasureTheServerJoinAtTheSizesOfTheEnvironment()
    {
        string sizes = Environment.GetEnvironmentVariable("RAVEN_COMPAT_SERVERJOIN_KEYS");
        foreach (int keys in sizes.Split(',').Select(int.Parse))
            JoinTestHelpers.AppendReport("RAVEN_COMPAT_SERVERJOIN_OUT", "join-compat-serverjoin.txt", (await MeasureAsync(keys)).ToString());
    }
}
