using System;
using System.Collections.Generic;
using System.Linq;
using Corax.Indexing;
using FastTests;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Tests.Infrastructure;
using Voron;
using Voron.Data.Containers;
using Voron.Impl;
using Voron.Util;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Storage;

public class ListStoreTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static long[] Encode(IEnumerable<long> ids) => ids.OrderBy(x => x).Select(ListStore.Encode).ToArray();

    private static long Update(StorageEnvironment environment, long handle, IEnumerable<long> additions, IEnumerable<long> removals)
    {
        using (Transaction tx = environment.WriteTransaction())
        {
            ContainerId container = tx.OpenContainer("lists");
            using var store = new ListStore(tx.LowLevelTransaction, container);
            long updated = store.Update(handle, Encode(additions), Encode(removals));
            tx.Commit();
            return updated;
        }
    }

    private static long[] Read(StorageEnvironment environment, long handle)
    {
        using (Transaction tx = environment.ReadTransaction())
        {
            ContainerId container = tx.OpenContainer("lists");
            using var store = new ListStore(tx.LowLevelTransaction, container);
            var output = new ContextBoundNativeList<long>(tx.Allocator);
            try
            {
                store.Read(handle, ref output);
                Assert.Equal(ListStore.Count(tx.LowLevelTransaction, handle), output.Count);
                return output.ToSpan().ToArray();
            }
            finally
            {
                output.Dispose();
            }
        }
    }

    private static StorageEnvironment Create() => new(StorageEnvironmentOptions.CreateMemoryOnlyForTests());

    [RavenFact(RavenTestCategory.Voron)]
    public void ListGrowsFromSingleToSmallToLargeAndEmptiesAgain()
    {
        using StorageEnvironment environment = Create();

        long handle = Update(environment, ListStore.EmptyHandle, new long[] { 40 }, Array.Empty<long>());
        Assert.Equal(TermIdMask.Single, ListStore.KindOf(handle));
        Assert.Equal(new long[] { 40 }, Read(environment, handle));

        handle = Update(environment, handle, new long[] { 44, 48 }, Array.Empty<long>());
        Assert.Equal(TermIdMask.SmallPostingList, ListStore.KindOf(handle));
        Assert.Equal(new long[] { 40, 44, 48 }, Read(environment, handle));

        long[] many = Enumerable.Range(1, 20_000).Select(x => x * 4L + 1_000_000).ToArray();
        handle = Update(environment, handle, many, Array.Empty<long>());
        Assert.Equal(TermIdMask.PostingList, ListStore.KindOf(handle));
        Assert.Equal(new long[] { 40, 44, 48 }.Concat(many).ToArray(), Read(environment, handle));

        handle = Update(environment, handle, Array.Empty<long>(), many.Concat(new long[] { 44 }));
        Assert.Equal(new long[] { 40, 48 }, Read(environment, handle));

        handle = Update(environment, handle, Array.Empty<long>(), new long[] { 40, 48 });
        Assert.Equal(ListStore.EmptyHandle, handle);
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void SmallListShrinksToSingleAndToEmpty()
    {
        using StorageEnvironment environment = Create();
        long handle = Update(environment, ListStore.EmptyHandle, new long[] { 8, 12, 16 }, Array.Empty<long>());
        Assert.Equal(TermIdMask.SmallPostingList, ListStore.KindOf(handle));

        handle = Update(environment, handle, Array.Empty<long>(), new long[] { 8, 16 });
        Assert.Equal(TermIdMask.Single, ListStore.KindOf(handle));
        Assert.Equal(new long[] { 12 }, Read(environment, handle));

        handle = Update(environment, handle, Array.Empty<long>(), new long[] { 12 });
        Assert.Equal(ListStore.EmptyHandle, handle);
        Assert.Empty(Read(environment, handle));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void AddingAnEntryThatIsThereAndRemovingOneThatIsNotAreIgnored()
    {
        using StorageEnvironment environment = Create();
        long handle = Update(environment, ListStore.EmptyHandle, new long[] { 8, 12, 16 }, new long[] { 99 });

        handle = Update(environment, handle, new long[] { 12 }, new long[] { 100, 4 });
        Assert.Equal(new long[] { 8, 12, 16 }, Read(environment, handle));

        long single = Update(environment, ListStore.EmptyHandle, new long[] { 8 }, Array.Empty<long>());
        single = Update(environment, single, new long[] { 8 }, new long[] { 4 });
        Assert.Equal(new long[] { 8 }, Read(environment, single));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void SmallListWithTheSameLengthIsRewrittenInPlace()
    {
        using StorageEnvironment environment = Create();
        long handle = Update(environment, ListStore.EmptyHandle, new long[] { 8, 12, 16 }, Array.Empty<long>());

        long same = Update(environment, handle, new long[] { 20 }, new long[] { 8 });

        Assert.Equal(new long[] { 12, 16, 20 }, Read(environment, same));
        Assert.Equal(handle, same);
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void ReleaseFreesTheSpaceOfLargeLists()
    {
        using StorageEnvironment environment = Create();
        long handle = Update(environment, ListStore.EmptyHandle, Enumerable.Range(1, 20_000).Select(x => x * 4L), Array.Empty<long>());
        long pagesAfterBuild = environment.Stats().AllocatedDataFileSizeInBytes;

        handle = Update(environment, handle, Array.Empty<long>(), Enumerable.Range(1, 20_000).Select(x => x * 4L));
        Assert.Equal(ListStore.EmptyHandle, handle);

        long again = Update(environment, ListStore.EmptyHandle, Enumerable.Range(1, 20_000).Select(x => x * 4L), Array.Empty<long>());
        using (Transaction tx = environment.ReadTransaction())
            Assert.Equal(20_000, ListStore.Count(tx.LowLevelTransaction, again));

        Assert.True(environment.Stats().AllocatedDataFileSizeInBytes <= pagesAfterBuild + 1024 * 1024, "the pages of the emptied list were not reused");
    }

    [RavenTheory(RavenTestCategory.Voron)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(12)]
    public void RandomChangesMatchTheModel(int seed)
    {
        const int lists = 12;
        var random = new Random(seed);
        using StorageEnvironment environment = Create();

        var handles = new long[lists];
        var model = new SortedSet<long>[lists];
        for (int i = 0; i < lists; i++)
            model[i] = new SortedSet<long>();

        var kinds = new HashSet<TermIdMask>();
        for (int round = 0; round < 60; round++)
        {
            int list = random.Next(lists);
            var additions = new SortedSet<long>();
            var removals = new SortedSet<long>();

            int shape = random.Next(10);
            int addCount = shape switch { 0 => 3000 + random.Next(3000), 1 => 200, _ => random.Next(4) };
            for (int i = 0; i < addCount; i++)
            {
                long id = (random.NextInt64(1, 1L << 36)) & ~3L | 4;
                if (random.Next(4) == 0 && model[list].Count > 0)
                    id = model[list].ElementAt(random.Next(model[list].Count));

                additions.Add(id);
            }

            foreach (long existing in model[list])
            {
                if (random.Next(shape == 2 ? 2 : 6) == 0 && additions.Contains(existing) == false)
                    removals.Add(existing);
            }

            if (random.Next(5) == 0)
                removals.Add(((random.NextInt64(1, 1L << 36)) & ~3L | 4) + 1_000_000_000_000);

            additions.ExceptWith(removals);
            handles[list] = Update(environment, handles[list], additions, removals);
            model[list].UnionWith(additions);
            model[list].ExceptWith(removals);
            kinds.Add(ListStore.KindOf(handles[list]));

            if (round % 10 == 9)
            {
                for (int i = 0; i < lists; i++)
                    Assert.Equal(model[i].ToArray(), Read(environment, handles[i]));
            }
        }

        for (int i = 0; i < lists; i++)
            Assert.Equal(model[i].ToArray(), Read(environment, handles[i]));

        Assert.Contains(TermIdMask.PostingList, kinds);
    }
}
