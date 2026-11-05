using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Utils;
using Tests.Infrastructure;
using Voron.Util;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

/// <summary>The lists of the keys that the cache is filled from: the count of a list is its header (a single entry, a small and a large posting list), and counting every key leaves nothing held in the allocator of the snapshot.</summary>
public class JoinOuterKeyListsTests(ITestOutputHelper output) : RavenTestBase(output), IIndirectModeTest
{
    private const int Keys = 3_000;

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheCountOfAKeyIsTheSizeOfItsListAndCountingHoldsNoMemoryOfTheSnapshot()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this);
        await using (var bulk = h.Store.BulkInsert())
        {
            for (int i = 0; i < Keys; i++)
                await bulk.StoreAsync(new JoinIndexHarness.Context { Code = "k" + i, Country = "nl", Name = "n" + i % 7, IsActive = true, Title = "t" }, $"contexts/{i}");

            // key 0: a posting list, every tenth key: one entry, the others: small posting lists
            int conversation = 0;
            for (int i = 0; i < Keys; i++)
            {
                int rows = i == 0 ? 2_500 : i % 10 == 0 ? 1 : 30;
                for (int r = 0; r < rows; r++)
                    await bulk.StoreAsync(new JoinIndexHarness.Conversation { ContextId = "k" + i, Title = "t" }, $"conversations/{conversation++}");
            }
        }

        await h.AssertCurrentAsync();
        JoinIndex joinIndex = h.Join();
        Assert.True(h.Database.IndexStore.GetIndex(JoinIndexHarness.OuterName).TryOpenSnapshot(out IndexSnapshot outer));
        using (outer)
        {
            Assert.True(joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot file));
            using (file)
            {
                var keys = new ContextBoundNativeList<long>(outer.Searcher.Allocator);
                file.ReadAtomKeys(JoinReader.AtomOfAny(), ref keys);
                Assert.Equal(Keys, keys.Count);

                var source = new JoinOuterKeyLists(outer.Searcher, outer.Searcher.FieldMetadataBuilder("ContextId"), file, outer.Searcher.GetTermsFor(joinIndex.Plan.OuterKeyField));
                var counts = new Dictionary<long, long>();
                long single = 0, small = 0, large = 0;
                long before = NativeMemory.CurrentThreadStats.TotalAllocated;
                foreach (long keyRef in keys.ToSpan())
                {
                    long count = source.CountOf(keyRef);
                    counts[keyRef] = count;
                    if (count == 1)
                        single++;
                    else if (count == 30)
                        small++;
                    else if (count == 2_500)
                        large++;
                }

                long held = NativeMemory.CurrentThreadStats.TotalAllocated - before;
                Assert.Equal((299L, Keys - 300L, 1L), (single, small, large));

                // before the count was read from the header every key left about 8 KB in the allocator of the snapshot, about 22 MB for these keys
                Assert.True(held < 2 * 1024 * 1024, $"counting {Keys} keys held {held} bytes of native memory");

                foreach (long keyRef in keys.ToSpan().ToArray().Take(200))
                    Assert.Equal(counts[keyRef], source.Read(keyRef).Length);

                source.Dispose();
                keys.Dispose();
            }
        }
    }
}
