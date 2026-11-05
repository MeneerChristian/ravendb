using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Storage;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
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
using Shape = FastTests.Server.Documents.Indexing.Joins.Refresh.JoinIndexBuildPerAtomTests.Shape;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>
/// The build of an Indirect file: the atom lists hold the keys whose group has the atom (the inverse of the atoms of the keys), whatever the queue, and the outer entries are in no list.
/// </summary>
public class JoinIndexIndirectBuildTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const int Conversations = JoinIndexBuildPerAtomTests.Conversations;
    private const int Contexts = JoinIndexBuildPerAtomTests.Contexts;

    private sealed record Built(List<string> Dump, long DirectAtoms, bool PairsOverflowed, int Flushes, long ListMembers, JoinFileConsistencyChecker.Result Check, JoinIndexInvariantChecker.Result Invariant, int KeyRowWrites);

    private static Built BuildInto(JoinIndex joinIndex, Index outer, Index inner, JoinStorageMode mode, int flushThreshold, int minAtomEntries)
    {
        using var plan = new JoinPlan(joinIndex.Join, mode);
        Assert.True(joinIndex.TryOpenViews(outer, inner, plan, out IndexView outerView, out IndexView innerView));
        using (outerView)
        using (innerView)
        using (JoinStorageHarness storage = JoinStorageHarness.OpenInMemory(plan.Schema))
        {
            long direct;
            bool overflowed;
            int flushes;
            int rowWrites;
            JoinIndexGeneration generation;
            using (Transaction tx = storage.BeginWriteTransaction())
            {
                var file = new JoinIndexFile(tx, plan.Schema);
                using var builder = new JoinBuilder(file, plan, outerView, innerView, CancellationToken.None, flushThreshold, minAtomEntries);
                generation = builder.Run();
                direct = builder.DirectAtoms;
                overflowed = builder.PairsOverflowed;
                flushes = builder.ListFlushes;
                rowWrites = builder.KeyRowWrites;
                tx.Commit();
            }

            using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
            Assert.Equal(mode, snapshot.Generation.Mode);
            Assert.Equal(mode, generation.Mode);
            Assert.False(snapshot.Generation.IsPartial);

            var invariant = new JoinIndexInvariantChecker.Result { Compared = true, Generation = snapshot.Generation.ToString() };
            FieldJoinInvariantChecker.Compare(snapshot, plan, outerView, innerView, new JoinIndexInvariantChecker.SeenTerms(plan.Fields.Length), invariant);
            JoinFileConsistencyChecker.Result check = JoinFileConsistencyChecker.Check(snapshot, plan, Conversations, Contexts);
            (List<string> dump, long members) = Dump(snapshot, plan);
            return new Built(dump, direct, overflowed, flushes, members, check, invariant, rowWrites);
        }
    }

    private static string Hex(JoinReader file, long keyRef)
    {
        var buffer = new byte[JoinLimits.MaxTermLength];
        return Convert.ToHexString(buffer, 0, file.CopyKey(keyRef, buffer));
    }

    /// <summary>The lists by the text of the keys and of the terms (the references depend on the order of the writes), and the number of the members of all lists.</summary>
    private static (List<string> Lines, long Members) Dump(JoinIndexSnapshot file, JoinPlan plan)
    {
        using var allocator = new ByteStringContext(SharedMultipleUseFlag.None);
        var refs = new ContextBoundNativeList<long>(allocator, 16);
        var lines = new List<string>();
        var buffer = new byte[2048];
        long members = 0;

        string List(long atom)
        {
            long[] values = file.Schema.IsIndirect
                ? JoinWriterIndirectTests.AtomKeys(file, atom)
                : JoinIndexInvariantChecker.ReadList(file, file.AtomHandle(atom));
            members += values.Length;
            return file.Schema.IsIndirect
                ? string.Join(',', values.Select(k => Hex(file, k)).OrderBy(x => x, StringComparer.Ordinal))
                : string.Join(',', values);
        }

        lines.Add("any " + List(JoinReader.AtomOfAny()));
        for (int g = 0; g < plan.GroupingFields.Length; g++)
            lines.Add($"grouping {g} " + List(JoinReader.AtomOfGrouping(g)));

        lines.Add("several keys " + string.Join(',', JoinIndexInvariantChecker.ReadList(file, file.SlotHandle(file.MultiKeySlot))));
        lines.Add("several inner " + List(file.AtomOfSeveralInner));
        lines.Add($"counters {file.RowsWithSeveralJoined} {file.RowsWithSeveralKeys} {file.NumberOfJoined} {file.NumberOfOuterDocuments} {file.NumberOfInnerDocuments}");

        var seen = new HashSet<(int, long)>();
        for (int x = 0; x < Contexts; x++)
        {
            long key = file.InnerKeyOf(Encoding.UTF8.GetBytes($"contexts/{x}"));
            if (file.TryGetInnerDocument(key, out _) == false)
                continue;

            refs.Count = 0;
            file.TryGetInnerKeys(key, ref refs);
            lines.Add($"inner {x} " + string.Join('|', refs.ToSpan().ToArray().Select(k => Hex(file, k)).OrderBy(x => x, StringComparer.Ordinal)));

            for (int f = 0; f < plan.Fields.Length; f++)
            {
                refs.Count = 0;
                file.TryGetInnerTerms(f, key, ref refs);
                foreach (long termRef in refs.ToSpan().ToArray())
                {
                    if (seen.Add((f, termRef)) == false)
                        continue;

                    int length = file.CopyTerm(f, termRef, buffer);
                    lines.Add($"term {f} {Convert.ToHexString(buffer, 0, length)} " + List(JoinReader.AtomOfTerm(f, termRef)));
                }
            }
        }

        for (int c = 0; c < Conversations; c++)
        {
            if (file.TryGetOuterDocument(JoinFileRows.HashDocumentId(Encoding.UTF8.GetBytes($"conversations/{c}"), file.Seed), out long entry) == false)
                continue;

            refs.Count = 0;
            file.TryGetOuterKeys(entry, ref refs);
            lines.Add($"outer {entry} " + string.Join('|', refs.ToSpan().ToArray().Select(k => Hex(file, k)).OrderBy(x => x, StringComparer.Ordinal)));
        }

        refs.Dispose();
        lines.Sort(StringComparer.Ordinal);
        return (lines, members);
    }

    private static async Task<(JoinIndexHarness H, Index Outer, Index Inner)> PrepareAsync(RavenTestBase test, Shape shape, int seed)
    {
        // the file is built from the indexes by a plan of the mode: the join index itself is an Expanded one
        return await JoinIndexBuildPerAtomTests.PrepareAsync(test, shape, seed);
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(Shape.Field, 7)]
    [InlineData(Shape.Field, 8)]
    [InlineData(Shape.Field, 9)]
    [InlineData(Shape.FieldSeveralKeys, 7)]
    [InlineData(Shape.FieldSeveralKeys, 8)]
    [InlineData(Shape.FieldSeveralKeys, 9)]
    public async Task AnIndirectBuildHoldsTheKeysInTheListsAndNoOuterEntryAndPassesTheCheckers(Shape shape, int seed)
    {
        (JoinIndexHarness h, Index outer, Index inner) = await PrepareAsync(this, shape, seed);
        using (h)
        {
            Built built = BuildInto(h.Join(), outer, inner, JoinStorageMode.Indirect, 1024 * 1024, minAtomEntries: 1);

            Assert.True(built.Invariant.Ok, built.Invariant.ToString());
            Assert.True(built.Check.Ok, built.Check.ToString());
            Assert.True(built.Check.Joined > 100, built.Check.ToString());
            Assert.True(built.DirectAtoms > 0);
            Assert.False(built.PairsOverflowed);
            Assert.Contains("several keys ", built.Dump);
            Assert.Contains(built.Dump, x => x.StartsWith("several inner ", StringComparison.Ordinal) && x.Length > "several inner ".Length);
        }
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(Shape.Field, 7)]
    [InlineData(Shape.FieldSeveralKeys, 8)]
    [InlineData(Shape.Field, 11)]
    public async Task EveryQueueLengthGivesTheListsOfTheDirectBuild(Shape shape, int seed)
    {
        (JoinIndexHarness h, Index outer, Index inner) = await PrepareAsync(this, shape, seed);
        using (h)
        {
            JoinIndex joinIndex = h.Join();
            Built roomy = BuildInto(joinIndex, outer, inner, JoinStorageMode.Indirect, 1024 * 1024, minAtomEntries: 1);
            Assert.True(roomy.Check.Ok, roomy.Check.ToString());
            Assert.False(roomy.PairsOverflowed);

            foreach (int flush in new[] { 5, 7, 150, 1000 })
            {
                Built built = BuildInto(joinIndex, outer, inner, JoinStorageMode.Indirect, flush, minAtomEntries: 1);
                Assert.True(built.Invariant.Ok, $"flush {flush}: {built.Invariant}");
                Assert.True(built.Check.Ok, $"flush {flush}: {built.Check}");
                Assert.True(roomy.Dump.SequenceEqual(built.Dump), $"flush {flush}: " + string.Join(" | ", roomy.Dump.Except(built.Dump).Take(3)) + " <> " + string.Join(" | ", built.Dump.Except(roomy.Dump).Take(3)));
            }

            // a queue shorter than the pairs queues them all: nothing is written at once
            Built overflowed = BuildInto(joinIndex, outer, inner, JoinStorageMode.Indirect, 5, minAtomEntries: 1);
            Assert.True(overflowed.PairsOverflowed);
            Assert.Equal(0, overflowed.DirectAtoms);
            Assert.True(roomy.Flushes < overflowed.Flushes, $"{roomy.Flushes} flushes against {overflowed.Flushes}");
        }
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(Shape.Field, 7)]
    [InlineData(Shape.FieldSeveralKeys, 8)]
    public async Task EveryRowOfKeysIsWrittenOnceWhenTheQueueHoldsAllPairsAndTheRowsAreTheSameWhateverTheQueue(Shape shape, int seed)
    {
        (JoinIndexHarness h, Index outer, Index inner) = await PrepareAsync(this, shape, seed);
        using (h)
        {
            JoinIndex joinIndex = h.Join();
            Built roomy = BuildInto(joinIndex, outer, inner, JoinStorageMode.Indirect, 1024 * 1024, minAtomEntries: 1);
            Assert.True(roomy.Check.Ok, roomy.Check.ToString());

            int rows = roomy.Dump.Count(x => (x.StartsWith("outer ", StringComparison.Ordinal) || x.StartsWith("inner ", StringComparison.Ordinal)) && x.Split(' ', 3)[2].Length > 0);
            Assert.True(rows > 100, $"{rows} rows");
            Assert.Equal(rows, roomy.KeyRowWrites);

            // a queue that holds a few pairs writes the rows in pieces: more writes, the same rows
            Built small = BuildInto(joinIndex, outer, inner, JoinStorageMode.Indirect, 7, minAtomEntries: 1);
            Assert.True(small.Check.Ok, small.Check.ToString());
            Assert.True(small.KeyRowWrites >= rows);
            Assert.Equal(roomy.Dump.Where(x => x.StartsWith("outer ", StringComparison.Ordinal) || x.StartsWith("inner ", StringComparison.Ordinal)),
                small.Dump.Where(x => x.StartsWith("outer ", StringComparison.Ordinal) || x.StartsWith("inner ", StringComparison.Ordinal)));
        }
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(Shape.Field, 7)]
    [InlineData(Shape.FieldSeveralKeys, 8)]
    public async Task TheIndirectFileHoldsFewerListMembersThanTheExpandedFileAndTheSameRows(Shape shape, int seed)
    {
        (JoinIndexHarness h, Index outer, Index inner) = await PrepareAsync(this, shape, seed);
        using (h)
        {
            JoinIndex joinIndex = h.Join();
            Built expanded = BuildInto(joinIndex, outer, inner, JoinStorageMode.Expanded, 1024 * 1024, minAtomEntries: 1);
            Built indirect = BuildInto(joinIndex, outer, inner, JoinStorageMode.Indirect, 1024 * 1024, minAtomEntries: 1);

            Assert.True(expanded.Check.Ok && indirect.Check.Ok);
            Assert.True(indirect.ListMembers * 5 < expanded.ListMembers, $"{indirect.ListMembers} members of the lists against {expanded.ListMembers}");

            // the rows of the outer entries are the same in both files
            Assert.Equal(expanded.Dump.Where(x => x.StartsWith("outer ", StringComparison.Ordinal)), indirect.Dump.Where(x => x.StartsWith("outer ", StringComparison.Ordinal)));
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnIndirectJoinIsBuiltInTheFormat3WithTheKeysInItsListsAndPassesTheInvariantCheck()
    {
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.IndirectEnabled)] = "true" };
        using JoinIndexHarness h = await JoinIndexBuildPerAtomTests.CreateAsync(this, Shape.Field, settings);
        JoinIndexBuildPerAtomTests.Populate(h, Shape.Field, 5);

        IndexDefinition definition = JoinIndexHarness.FieldJoinIndexDefinition();
        definition.Join.StorageMode = JoinStorageMode.Indirect;
        await h.PutJoinAsync(definition);
        await h.AssertCurrentAsync();

        JoinIndex joinIndex = h.Join();
        Assert.Equal(JoinStorageMode.Indirect, joinIndex.Plan.BuildMode);
        Assert.Equal(1, joinIndex.Counters.Builds);
        Assert.True(joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
        using (snapshot)
        {
            Assert.True(snapshot.Schema.IsIndirect);
            Assert.Equal(JoinStorageMode.Indirect, snapshot.Generation.Mode);
            Assert.Equal(JoinStorageMode.Indirect, JoinFileRows.ReadMode(snapshot.Transaction));
            Assert.Equal(3, snapshot.Generation.Format);
            Assert.Equal(JoinIndexGeneration.Deserialize(snapshot.Generation.Serialize()), snapshot.Generation);
        }

        JoinFileConsistencyChecker.Result check = JoinFileConsistencyChecker.Check(joinIndex, Conversations, Contexts);
        Assert.True(check.Ok, check.ToString());
        Assert.True(check.Joined > 100);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheServerRefusesTheModeIndirectWhileItIsNotEnabled()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, putJoin: false);
        IndexDefinition definition = JoinIndexHarness.FieldJoinIndexDefinition();
        definition.Join.StorageMode = JoinStorageMode.Indirect;

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () => await h.PutJoinAsync(definition));
        Assert.Contains("Join.IndirectEnabled", exception.ToString());
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task ADefinitionThatNamesNoModeIsBuiltExpandedWhateverTheNodeDefaultWhileIndirectIsNotEnabled()
    {
        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.StorageModeDefault)] = nameof(JoinStorageMode.Indirect) };
        using JoinIndexHarness h = await JoinIndexBuildPerAtomTests.CreateAsync(this, Shape.Field, settings);
        JoinIndexBuildPerAtomTests.Populate(h, Shape.Field, 5);
        await h.PutJoinAsync(JoinIndexHarness.FieldJoinIndexDefinition());
        await h.AssertCurrentAsync();

        Assert.Equal(JoinStorageMode.Expanded, h.Join().Plan.BuildMode);
        Assert.True(h.Join().TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
        using (snapshot)
            Assert.Equal(JoinStorageMode.Expanded, snapshot.Generation.Mode);
    }
}
