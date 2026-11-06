using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Corax.Mappings;
using Corax.Querying.Matches.Meta;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Server.Documents.Indexes;
using RavenIndex = Raven.Server.Documents.Indexes.Index;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Server;
using Tests.Infrastructure;
using Voron;
using Voron.Data.CompactTrees;
using Voron.Util;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

/// <summary>The union of the keys of an Indirect file for a reader that takes a page: one key is its posting list, the keys that no entry shares are read from the first entries of their resident lists, and reading more is exact.</summary>
public class JoinKeyUnionPageTests(ITestOutputHelper output) : RavenTestBase(output), IIndirectModeTest
{
    private static async Task<JoinIndexHarness> SeedAsync(RavenTestBase test, int keys, int rowsPerKey)
    {
        JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(test);
        for (int i = 0; i < keys; i++)
            h.PutCodedContext(i, "k" + i, "nl", "n" + i % 4, i, true, "w" + i);

        for (int i = 0; i < keys * rowsPerKey; i++)
            h.PutConversation(i, "k" + i % keys, "a");

        await h.AssertCurrentAsync();
        for (int i = 0; i < 1000 && (h.Join().Resident is not { } resident || resident.Cache.Epoch.IsNone); i++)
            await Task.Delay(20);

        return h;
    }

    private static long[] KeysOf(JoinIndexSnapshot file)
    {
        var keys = new ContextBoundNativeList<long>(file.Transaction.Allocator);
        try
        {
            file.ReadAtomKeys(JoinReader.AtomOfAny(), ref keys);
            return keys.ToSpan().ToArray();
        }
        finally
        {
            keys.Dispose();
        }
    }

    private static long[] StoreRows(IndexSnapshot snapshot, JoinIndexSnapshot file, long[] keys)
    {
        Slice.From(snapshot.Searcher.Allocator, "ContextId", ByteStringType.Immutable, out Slice field);
        CompactTree tree = snapshot.Searcher.GetTermsFor(field);
        FieldMetadata metadata = snapshot.Searcher.FieldMetadataBuilder("ContextId");
        var rows = new ContextBoundNativeList<long>(snapshot.Searcher.Allocator);
        try
        {
            foreach (long keyRef in keys)
                JoinOuterKeyLists.ReadRowsOfKey(snapshot.Searcher, metadata, file, tree, keyRef, ref rows);

            return rows.ToSpan().ToArray().Distinct().OrderBy(x => x).ToArray();
        }
        finally
        {
            rows.Dispose();
        }
    }

    private static List<long> Drain(IQueryMatch match, int chunk)
    {
        var ids = new List<long>();
        var buffer = new long[chunk];
        int count;
        while ((count = match.Fill(buffer)) > 0)
            ids.AddRange(buffer.AsSpan(0, count).ToArray());

        return ids;
    }

    private static async Task RunAsync(JoinIndexHarness h, Action<IndexSnapshot, JoinIndexSnapshot, long[], Func<long[], bool, IQueryMatch>> check)
    {
        JoinIndex joinIndex = h.Join();
        RavenIndex outerIndex = h.Database.IndexStore.GetIndex(JoinIndexHarness.OuterName);
        Assert.True(outerIndex.TryOpenSnapshot(out IndexSnapshot snapshot));
        using (snapshot)
        {
            Assert.True(joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot file));
            using (file)
            {
                long[] keys = KeysOf(file);
                IQueryMatch Create(long[] subset, bool disjoint)
                {
                    JoinResidentRead read = joinIndex.Resident.ReadOf(snapshot.EnvironmentId, snapshot.Transaction.LowLevelTransaction.Id, file.Seed, 0, snapshot.Consumed);
                    return JoinKeyUnion.Create(snapshot.Searcher, file, subset, "ContextId", read, null, disjoint);
                }

                check(snapshot, file, keys, Create);
            }
        }

        await Task.CompletedTask;
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task APageOfKeysThatNoEntrySharesIsTheFirstEntriesAndCountsAllOfThem()
    {
        using JoinIndexHarness h = await SeedAsync(this, keys: 40, rowsPerKey: 10);
        await RunAsync(h, (snapshot, file, keys, create) =>
        {
            long[] expected = StoreRows(snapshot, file, keys);
            Assert.Equal(400, expected.Length);

            IQueryMatch match = create(keys, true);
            Assert.True(match is JoinLazyKeysMatch, match.GetType().Name);
            Assert.Equal(400, match.Count);
            Assert.Equal(QueryCountConfidence.High, match.Confidence);

            var page = new long[20];
            Assert.Equal(20, match.Fill(page));
            Assert.Equal(expected.Take(20), page);
        });
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ReadingBeyondTheCopiedEntriesContinuesExactlyWhereThePageStopped()
    {
        using JoinIndexHarness h = await SeedAsync(this, keys: 6, rowsPerKey: 100);
        await RunAsync(h, (snapshot, file, keys, create) =>
        {
            long[] expected = StoreRows(snapshot, file, keys);
            Assert.Equal(600, expected.Length);

            foreach (int chunk in new[] { 1, 7, 20, 64, 65, 1024 })
            {
                IQueryMatch match = create(keys, true);
                Assert.True(match is JoinLazyKeysMatch);
                Assert.Equal(600, match.Count);
                Assert.Equal(expected, Drain(match, chunk));
            }
        });
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnAndWithAnotherMatchChecksTheWholeUnion()
    {
        using JoinIndexHarness h = await SeedAsync(this, keys: 6, rowsPerKey: 100);
        await RunAsync(h, (snapshot, file, keys, create) =>
        {
            long[] expected = StoreRows(snapshot, file, keys);
            long[] probe = expected.Where((_, i) => i % 3 == 0).Concat([expected[^1] + 1, 0, expected[0] - 1]).Distinct().OrderBy(x => x).ToArray();
            IQueryMatch match = create(keys, true);
            var buffer = new long[probe.Length];
            probe.CopyTo(buffer, 0);
            int kept = match.AndWith(buffer, probe.Length);
            Assert.Equal(probe.Where(x => expected.Contains(x)), buffer.Take(kept));
        });
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ARestartedMatchReadsTheSameEntriesFromTheFirst()
    {
        using JoinIndexHarness h = await SeedAsync(this, keys: 40, rowsPerKey: 10);
        await RunAsync(h, (snapshot, file, keys, create) =>
        {
            long[] expected = StoreRows(snapshot, file, keys);
            IQueryMatch match = create(keys, true);
            Assert.True(JoinKeyUnion.TryRestart(match, out IQueryMatch again));
            Assert.Equal(expected, Drain(match, 33));
            Assert.Equal(expected, Drain(again, 5));
            Assert.True(JoinKeyUnion.TryRestart(again, out IQueryMatch third));
            Assert.Equal(expected.Take(20), Drain(third, 20).Take(20));
        });
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task OneKeyIsItsPostingListInTheOuterIndex()
    {
        using JoinIndexHarness h = await SeedAsync(this, keys: 6, rowsPerKey: 100);
        await RunAsync(h, (snapshot, file, keys, create) =>
        {
            foreach (long key in keys)
            {
                long[] expected = StoreRows(snapshot, file, [key]);
                Assert.Equal(100, expected.Length);
                IQueryMatch match = create([key], false);
                Assert.Equal(100, match.Count);
                Assert.Equal(expected, Drain(match, 16));
            }

            Assert.Equal(0, create([], true).Count);
        });
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task KeysThatEntriesMayShareAreNotReadFromTheCopies()
    {
        using JoinIndexHarness h = await SeedAsync(this, keys: 40, rowsPerKey: 10);
        await RunAsync(h, (snapshot, file, keys, create) =>
        {
            long[] expected = StoreRows(snapshot, file, keys);
            IQueryMatch match = create(keys, false);
            Assert.False(match is JoinLazyKeysMatch);
            Assert.Equal(expected, Drain(match, 64));
        });
    }
}
