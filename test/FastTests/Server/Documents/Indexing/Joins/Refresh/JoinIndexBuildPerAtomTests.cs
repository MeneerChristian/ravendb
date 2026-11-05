using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Storage;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Server;
using Sparrow.Threading;
using Tests.Infrastructure;
using Voron.Impl;
using Voron.Util;
using Xunit;
using Index = Raven.Server.Documents.Indexes.Index;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>
/// The build writes the list of an atom at once, from the union of the outer lists of the keys that have it, when the atom has enough entries, and queues the others. Whatever the limit and the length of the
/// queue, the file must hold the same lists as a build that queues every entry, and those must be the lists that the rows of the file give.
/// </summary>
public class JoinIndexBuildPerAtomTests(ITestOutputHelper output) : RavenTestBase(output)
{
    internal const int Conversations = 700;
    internal const int Contexts = 160;

    public enum Shape
    {
        IdJoin,
        IdJoinSeveralKeys,
        Field,
        FieldSeveralKeys,
        FieldIdOfOuter
    }

    internal static async Task<JoinIndexHarness> CreateAsync(RavenTestBase test, Shape shape, Dictionary<string, string> settings = null)
    {
        switch (shape)
        {
            case Shape.IdJoin:
                return await JoinIndexHarness.CreateAsync(test, settings, putJoin: false);
            case Shape.IdJoinSeveralKeys:
                return await JoinIndexHarness.CreateAsync(test, settings, putJoin: false, indexes: [JoinIndexHarness.MultiOuterIndex(), JoinIndexHarness.InnerIndex()]);
            case Shape.Field:
                return await JoinIndexHarness.CreateFieldJoinAsync(test, settings, putJoin: false);
            case Shape.FieldSeveralKeys:
                return await JoinIndexHarness.CreateFieldJoinAsync(test, settings, putJoin: false, arrays: true);
            default:
                return await JoinIndexHarness.CreateFieldJoinAsync(test, settings, putJoin: false, outerId: true);
        }
    }

    internal static Raven.Client.Documents.Indexes.IndexDefinition JoinDefinition(Shape shape)
    {
        return shape is Shape.IdJoin or Shape.IdJoinSeveralKeys
            ? JoinIndexHarness.JoinIndexDefinition()
            : JoinIndexHarness.FieldJoinIndexDefinition(outerId: shape == Shape.FieldIdOfOuter);
    }

    /// <summary>Contexts with a few countries (large atoms) and some names of their own (atoms of one entry), conversations that name them, name nothing, name documents that do not exist or name several.</summary>
    internal static void Populate(JoinIndexHarness h, Shape shape, int seed)
    {
        var random = new Random(seed);
        bool field = shape is Shape.Field or Shape.FieldSeveralKeys or Shape.FieldIdOfOuter;
        bool several = shape is Shape.IdJoinSeveralKeys or Shape.FieldSeveralKeys;

        string[] Keys(Func<string> key) => random.Next(10) == 0 ? null : Enumerable.Range(0, random.Next(0, 4)).Select(_ => key()).ToArray();

        h.Save(s =>
        {
            for (int i = 0; i < Contexts; i++)
                s.Store(NewContext(random, shape, i), $"contexts/{i}");
        });

        h.Save(s =>
        {
            for (int i = 0; i < Conversations; i++)
            {
                var conversation = new JoinIndexHarness.Conversation { Title = "t" };
                if (shape == Shape.FieldIdOfOuter)
                {
                }
                else if (field && several)
                    conversation.ContextIds = Keys(() => JoinIndexHarness.RandomCode(random, 9));
                else if (field)
                    conversation.ContextId = JoinIndexHarness.RandomCode(random, 9);
                else if (several)
                    conversation.ContextIds = Keys(() => $"contexts/{random.Next(Contexts + 8)}");
                else
                    conversation.ContextId = random.Next(12) == 0 ? null : $"contexts/{random.Next(Contexts + 8)}";

                s.Store(conversation, $"conversations/{i}");
            }
        });
    }

    internal static JoinIndexHarness.Context NewContext(Random random, Shape shape, int i)
    {
        var context = new JoinIndexHarness.Context
        {
            Country = random.Next(3) == 0 ? "nl" : "c" + random.Next(4),
            Name = random.Next(4) == 0 ? "u" + i : "n" + random.Next(5),
            Rank = random.Next(30),
            IsActive = random.Next(3) != 0,
            Title = string.Join(' ', Enumerable.Range(0, random.Next(0, 4)).Select(_ => "w" + random.Next(6)))
        };

        if (shape == Shape.FieldIdOfOuter)
            context.Code = random.Next(5) == 0 ? null : $"conversations/{random.Next(Conversations)}";
        else if (shape == Shape.FieldSeveralKeys)
            context.Codes = random.Next(10) == 0 ? null : Enumerable.Range(0, random.Next(0, 4)).Select(_ => JoinIndexHarness.RandomCode(random, 9)).ToArray();
        else if (shape == Shape.Field)
            context.Code = JoinIndexHarness.RandomCode(random, 9);

        return context;
    }

    /// <summary>Half of the contexts get other values and keys, some are deleted and some are new: the changes of the inner side only.</summary>
    internal static void ChangeContexts(JoinIndexHarness h, Shape shape, int seed)
    {
        var random = new Random(seed);
        h.Save(s =>
        {
            for (int i = 0; i < Contexts + 20; i++)
            {
                int kind = random.Next(10);
                if (kind < 5)
                    s.Store(NewContext(random, shape, i), $"contexts/{i}");
                else if (kind == 5)
                    s.Delete($"contexts/{i}");
            }
        });
    }

    private sealed record Built(List<string> Dump, long DirectAtoms, long QueuedAtoms, bool PairsOverflowed, int Flushes, JoinFileConsistencyChecker.Result Check);

    /// <summary>Builds a file of its own from the two snapshots of the indexes and describes everything in it that does not depend on the seed of the file.</summary>
    private static Built BuildInto(JoinIndex joinIndex, Index outer, Index inner, int flushThreshold, int minAtomEntries)
    {
        JoinPlan plan = joinIndex.Plan;
        Assert.True(joinIndex.TryOpenViews(outer, inner, plan, out IndexView outerView, out IndexView innerView));
        using (outerView)
        using (innerView)
        using (JoinStorageHarness storage = JoinStorageHarness.OpenInMemory(plan.Schema))
        {
            long direct, queued;
            bool overflowed;
            int flushes;
            using (Transaction tx = storage.BeginWriteTransaction())
            {
                var file = new JoinIndexFile(tx, plan.Schema);
                using var builder = new JoinBuilder(file, plan, outerView, innerView, CancellationToken.None, flushThreshold, minAtomEntries);
                builder.Run();
                direct = builder.DirectAtoms;
                queued = builder.QueuedAtoms;
                overflowed = builder.PairsOverflowed;
                flushes = builder.ListFlushes;
                tx.Commit();
            }

            using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
            JoinFileConsistencyChecker.Result check = JoinFileConsistencyChecker.Check(snapshot, plan, Conversations, Contexts);
            return new Built(Dump(snapshot, plan), direct, queued, overflowed, flushes, check);
        }
    }

    /// <summary>The lists of every atom by the text of the term, the slots, and the keys of every outer entry by their text (the references and the hashes depend on the seed of the file).</summary>
    private static List<string> Dump(JoinIndexSnapshot file, JoinPlan plan)
    {
        using var allocator = new ByteStringContext(SharedMultipleUseFlag.None);
        var list = new ContextBoundNativeList<long>(allocator, 16);
        var refs = new ContextBoundNativeList<long>(allocator, 16);
        var lines = new List<string>();
        var buffer = new byte[2048];

        string Ids(long handle)
        {
            list.Count = 0;
            file.ReadList(handle, ref list);
            return string.Join(',', list.ToSpan().ToArray());
        }

        lines.Add("any " + Ids(file.SlotHandle(JoinReader.AnySlot)));
        for (int g = 0; g < plan.GroupingFields.Length; g++)
            lines.Add($"grouping {g} " + Ids(file.SlotHandle(JoinReader.GroupingListSlot(g))));

        lines.Add("several keys " + Ids(file.SlotHandle(file.MultiKeySlot)));
        if (plan.IsFieldJoin)
            lines.Add("several inner " + Ids(file.SlotHandle(file.SeveralInnerSlot)));

        lines.Add($"counters {file.RowsWithSeveralJoined} {file.RowsWithSeveralKeys} {file.NumberOfJoined} {file.NumberOfOuterDocuments} {file.NumberOfInnerDocuments}");

        var seen = new HashSet<(int, long)>();
        for (int x = 0; x < Contexts; x++)
        {
            long key = file.InnerKeyOf(Encoding.UTF8.GetBytes($"contexts/{x}"));
            if (file.TryGetInnerDocument(key, out _) == false)
                continue;

            for (int f = 0; f < plan.Fields.Length; f++)
            {
                refs.Count = 0;
                file.TryGetInnerTerms(f, key, ref refs);
                foreach (long termRef in refs.ToSpan().ToArray())
                {
                    if (seen.Add((f, termRef)) == false)
                        continue;

                    int length = file.CopyTerm(f, termRef, buffer);
                    lines.Add($"term {f} {Convert.ToHexString(buffer, 0, length)} " + Ids(file.GetListHandle(f, termRef)));
                }
            }
        }

        for (int c = 0; c < Conversations; c++)
        {
            if (file.TryGetOuterDocument(JoinFileRows.HashDocumentId(Encoding.UTF8.GetBytes($"conversations/{c}"), file.Seed), out long entry) == false)
                continue;

            refs.Count = 0;
            file.TryGetOuterKeys(entry, ref refs);
            string keys = plan.IsFieldJoin
                ? string.Join('|', refs.ToSpan().ToArray().Select(k => Convert.ToHexString(buffer, 0, file.CopyKey(k, buffer))).OrderBy(x => x, StringComparer.Ordinal))
                : refs.Count.ToString();
            lines.Add($"outer {entry} {keys}");
        }

        list.Dispose();
        refs.Dispose();
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    internal static async Task<(JoinIndexHarness H, Index Outer, Index Inner)> PrepareAsync(RavenTestBase test, Shape shape, int seed, Dictionary<string, string> settings = null)
    {
        JoinIndexHarness h = await CreateAsync(test, shape, settings);
        Populate(h, shape, seed);
        await h.PutJoinAsync(JoinDefinition(shape));
        await h.AssertCurrentAsync();
        return (h, h.Database.IndexStore.GetIndex(JoinIndexHarness.OuterName), h.Database.IndexStore.GetIndex(JoinIndexHarness.InnerName));
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(Shape.IdJoin, 7)]
    [InlineData(Shape.IdJoinSeveralKeys, 7)]
    [InlineData(Shape.Field, 7)]
    [InlineData(Shape.FieldSeveralKeys, 7)]
    [InlineData(Shape.FieldIdOfOuter, 7)]
    [InlineData(Shape.IdJoin, 8)]
    [InlineData(Shape.IdJoinSeveralKeys, 8)]
    [InlineData(Shape.Field, 8)]
    [InlineData(Shape.FieldSeveralKeys, 8)]
    [InlineData(Shape.FieldIdOfOuter, 8)]
    [InlineData(Shape.Field, 9)]
    [InlineData(Shape.FieldSeveralKeys, 9)]
    [InlineData(Shape.IdJoinSeveralKeys, 9)]
    public async Task EveryLimitAndQueueLengthGivesTheListsOfTheQueueBuild(Shape shape, int seed)
    {
        (JoinIndexHarness h, Index outer, Index inner) = await PrepareAsync(this, shape, seed);
        using (h)
        {
            JoinIndex joinIndex = h.Join();
            Built queueOnly = BuildInto(joinIndex, outer, inner, 1024 * 1024, minAtomEntries: 0);
            Assert.True(queueOnly.Check.Ok, queueOnly.Check.ToString());
            Assert.Equal(0, queueOnly.DirectAtoms);
            Assert.Equal(0, queueOnly.QueuedAtoms);
            Assert.True(queueOnly.Check.Joined > 100, queueOnly.Check.ToString());

            bool mixed = false;
            foreach (int flush in new[] { 7, 150, 1024 * 1024 })
            {
                foreach (int limit in new[] { 1, 2, 3, 20, 60, 120, 250, 400, 1_000_000_000 })
                {
                    Built built = BuildInto(joinIndex, outer, inner, flush, limit);
                    string label = $"{shape} flush {flush} limit {limit}";
                    Assert.True(built.Check.Ok, label + Environment.NewLine + built.Check);
                    Assert.True(queueOnly.Dump.SequenceEqual(built.Dump), label + ": " + string.Join(" | ", queueOnly.Dump.Except(built.Dump).Take(3)) + " <> " + string.Join(" | ", built.Dump.Except(queueOnly.Dump).Take(3)));

                    // the atoms are written at once or queued by their size: 1 writes them all at once, a limit above every atom queues them all (a queue that is shorter than the pairs queues them too)
                    if (limit == 1 && flush == 1024 * 1024)
                        Assert.True(built.DirectAtoms > 0 && built.QueuedAtoms == 0, label + $": {built.DirectAtoms} direct, {built.QueuedAtoms} queued");

                    if (limit == 1_000_000_000 && flush == 1024 * 1024)
                        Assert.True(built.DirectAtoms == 0 && built.QueuedAtoms > 0, label + $": {built.DirectAtoms} direct, {built.QueuedAtoms} queued");

                    if (flush == 1024 * 1024 && built.DirectAtoms > 0 && built.QueuedAtoms > 0)
                        mixed = true;
                }
            }

            Assert.True(mixed, "no limit split the atoms of the data in large and small ones");
        }
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(Shape.Field)]
    [InlineData(Shape.IdJoin)]
    public async Task MorePairsThanTheQueueHoldsAreQueuedAndTheListsAreTheSame(Shape shape)
    {
        (JoinIndexHarness h, Index outer, Index inner) = await PrepareAsync(this, shape, seed: 11);
        using (h)
        {
            JoinIndex joinIndex = h.Join();
            Built queueOnly = BuildInto(joinIndex, outer, inner, 1024 * 1024, minAtomEntries: 0);

            // a queue of 5 holds 5 pairs: the pairs before the fifth are queued and so is everything after them
            Built overflowed = BuildInto(joinIndex, outer, inner, 5, minAtomEntries: 1);
            Assert.True(overflowed.PairsOverflowed);
            Assert.Equal(0, overflowed.DirectAtoms);
            Assert.True(overflowed.Check.Ok, overflowed.Check.ToString());
            Assert.True(queueOnly.Dump.SequenceEqual(overflowed.Dump));

            Built roomy = BuildInto(joinIndex, outer, inner, 1024 * 1024, minAtomEntries: 1);
            Assert.False(roomy.PairsOverflowed);
            Assert.True(roomy.DirectAtoms > 0);
        }
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(Shape.Field)]
    [InlineData(Shape.IdJoin)]
    public async Task AWriteAtOnceBuildHasFewerFlushesThanTheQueueBuild(Shape shape)
    {
        (JoinIndexHarness h, Index outer, Index inner) = await PrepareAsync(this, shape, seed: 5);
        using (h)
        {
            JoinIndex joinIndex = h.Join();
            Built queueOnly = BuildInto(joinIndex, outer, inner, 300, minAtomEntries: 0);
            Built direct = BuildInto(joinIndex, outer, inner, 100_000, minAtomEntries: 1);
            Assert.True(queueOnly.Flushes >= 5, $"{queueOnly.Flushes} flushes");
            Assert.True(direct.Flushes < queueOnly.Flushes, $"{direct.Flushes} flushes against {queueOnly.Flushes}");
            Assert.True(queueOnly.Dump.SequenceEqual(direct.Dump));
        }
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(Shape.Field, 0)]
    [InlineData(Shape.Field, 1)]
    [InlineData(Shape.Field, 30)]
    [InlineData(Shape.Field, 100_000_000)]
    [InlineData(Shape.FieldSeveralKeys, 0)]
    [InlineData(Shape.FieldSeveralKeys, 1)]
    [InlineData(Shape.FieldSeveralKeys, 30)]
    [InlineData(Shape.FieldIdOfOuter, 0)]
    [InlineData(Shape.FieldIdOfOuter, 1)]
    [InlineData(Shape.Field, 1, 22)]
    [InlineData(Shape.Field, 30, 23)]
    [InlineData(Shape.FieldSeveralKeys, 1, 24)]
    [InlineData(Shape.FieldSeveralKeys, 30, 25)]
    public async Task ChangesOfTheInnerSideMoveTheEntriesOfAnAtomByOneUpdateAndTheListsStayExact(Shape shape, int limit, int seed = 21)
    {
        var settings = new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.MinAtomEntriesForDirectWrite)] = limit.ToString(),
            [RavenConfiguration.GetKey(x => x.Join.MaxInnerListChangesPerOuterEntry)] = "0"
        };
        (JoinIndexHarness h, _, _) = await PrepareAsync(this, shape, seed, settings);
        using (h)
        {
            JoinCounters counters = h.Join().Counters;
            for (int round = 0; round < 5; round++)
            {
                ChangeContexts(h, shape, seed * 100 + round);
                await h.AssertCurrentAsync();
                JoinFileConsistencyChecker.Result check = JoinFileConsistencyChecker.Check(h.Join(), Conversations, Contexts + 20);
                Assert.True(check.Ok, check.ToString());
            }

            Assert.Equal(1, counters.Builds);
            if (limit == 0 || limit == 100_000_000)
                Assert.Equal(0, counters.AtomListsUpdatedAtOnce);
            else
                Assert.True(counters.AtomListsUpdatedAtOnce > 0, "no list was updated at once");

            if (limit != 1)
                Assert.True(counters.AtomListsQueued > 0, "no list was changed by queued changes");
        }
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(Shape.IdJoin, 1)]
    [InlineData(Shape.IdJoinSeveralKeys, 1)]
    [InlineData(Shape.Field, 1)]
    [InlineData(Shape.FieldSeveralKeys, 1)]
    [InlineData(Shape.FieldIdOfOuter, 1)]
    [InlineData(Shape.Field, 0)]
    [InlineData(Shape.FieldSeveralKeys, 25)]
    public async Task AJoinThatTheServerBuildsWithTheLimitIsExactAndStaysExactAfterChanges(Shape shape, int limit)
    {
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.MinAtomEntriesForDirectWrite)] = limit.ToString() };
        (JoinIndexHarness h, _, _) = await PrepareAsync(this, shape, seed: 3, settings);
        using (h)
        {
            Assert.Equal(limit, h.Join().Options.MinAtomEntriesForDirectWrite);
            Assert.Equal(1, h.Join().Counters.Builds);
            if (limit > 0)
                Assert.True(h.Join().Counters.LastBuildDirectAtoms > 0);
            else
                Assert.Equal(0, h.Join().Counters.LastBuildDirectAtoms);

            Assert.True(JoinFileConsistencyChecker.Check(h.Join(), Conversations, Contexts).Ok);

            // the lists that were written at once are changed by the refreshes like any other: every document is written again with other values
            for (int round = 0; round < 3; round++)
            {
                Populate(h, shape, 100 + round);
                await h.AssertCurrentAsync();
                Assert.True(JoinFileConsistencyChecker.Check(h.Join(), Conversations, Contexts).Ok);
            }

            Assert.Equal(1, h.Join().Counters.Builds);
        }
    }
}
