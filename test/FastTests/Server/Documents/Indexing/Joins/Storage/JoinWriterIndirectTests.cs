using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Storage;
using FastTests;
using Tests.Infrastructure;
using Voron.Data.BTrees;
using Voron.Util;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Storage;

/// <summary>The Indirect mode of the file: the atom lists hold the references of keys, the rows of the outer entries name keys, and a generation of the format 3 is written.</summary>
public class JoinWriterIndirectTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const int Keys = 40;
    private const int Terms = 6;
    private const int Groupings = 2;

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static JoinStorageHarness Open(JoinStorageMode mode = JoinStorageMode.Indirect, int groupings = Groupings)
    {
        return JoinStorageHarness.OpenInMemory(JoinStorageHarness.CreateSchema(fields: 1, groupings: groupings, mode: mode, isFieldJoin: true));
    }

    private static JoinIndexGeneration Generation(long marker, JoinStorageMode mode = JoinStorageMode.Indirect, bool innerPartial = false, bool outerPartial = false)
    {
        var cursors = new JoinCursors(new[] { ("Messages", marker, marker) });
        return mode == JoinStorageMode.Indirect
            ? new JoinIndexGeneration(0, Guid.NewGuid(), cursors, Guid.NewGuid(), JoinCursors.Empty, marker, 0, mode, innerPartial, outerPartial)
            : new JoinIndexGeneration(0, Guid.NewGuid(), cursors, Guid.NewGuid(), JoinCursors.Empty, marker, 0);
    }

    internal static long[] AtomKeys(JoinReader reader, long atom)
    {
        var keys = new ContextBoundNativeList<long>(reader.Transaction.Allocator);
        try
        {
            reader.ReadAtomKeys(atom, ref keys);
            Assert.Equal(keys.Count, reader.CountOfAtomKeys(atom));
            return keys.ToSpan().ToArray();
        }
        finally
        {
            keys.Dispose();
        }
    }

    private static long[] KeyAtoms(JoinReader reader, long keyRef)
    {
        var codes = new ContextBoundNativeList<long>(reader.Transaction.Allocator);
        try
        {
            reader.ReadKeyAtoms(keyRef, ref codes);
            return codes.ToSpan().ToArray();
        }
        finally
        {
            codes.Dispose();
        }
    }

    private static long[] OuterKeysOf(JoinReader reader, long outer)
    {
        var keys = new ContextBoundNativeList<long>(reader.Transaction.Allocator);
        try
        {
            return reader.TryGetOuterKeys(outer, ref keys) ? keys.ToSpan().ToArray() : null;
        }
        finally
        {
            keys.Dispose();
        }
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void TheAtomListsHoldTheKeysAndAreReadByTheAtom()
    {
        using JoinStorageHarness storage = Open();

        long nl, be, k1, k2, k3;
        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            k1 = writer.GetOrAddKey(Bytes("k1"));
            k2 = writer.GetOrAddKey(Bytes("k2"));
            k3 = writer.GetOrAddKey(Bytes("k3"));
            nl = JoinReader.AtomOfTerm(0, writer.GetOrAddTerm(0, Bytes("nl")));
            be = JoinReader.AtomOfTerm(0, writer.GetOrAddTerm(0, Bytes("be")));

            writer.AddKeyToAtom(nl, k3);
            writer.AddKeyToAtom(nl, k1);
            writer.AddKeyToAtom(be, k2);
            writer.AddKeyToAtom(JoinReader.AtomOfAny(), k1);
            writer.AddKeyToAtom(JoinReader.AtomOfAny(), k2);
            writer.AddKeyToAtom(JoinReader.AtomOfGrouping(1), k2);
            writer.AddKeyToAtom(writer.AtomOfSeveralInner, k2);
            write.Commit(Generation(1));
        }

        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
        Assert.Equal(new[] { k1, k3 }.Order().ToArray(), AtomKeys(snapshot, nl));
        Assert.Equal(new[] { k2 }, AtomKeys(snapshot, be));
        Assert.Equal(new[] { k1, k2 }.Order().ToArray(), AtomKeys(snapshot, JoinReader.AtomOfAny()));
        Assert.Empty(AtomKeys(snapshot, JoinReader.AtomOfGrouping(0)));
        Assert.Equal(new[] { k2 }, AtomKeys(snapshot, JoinReader.AtomOfGrouping(1)));
        Assert.Equal(new[] { k2 }, AtomKeys(snapshot, snapshot.AtomOfSeveralInner));
        Assert.Equal(snapshot.SlotHandle(snapshot.SeveralInnerSlot), snapshot.AtomHandle(snapshot.AtomOfSeveralInner));
        Assert.Equal(ListStore.EmptyHandle, snapshot.AtomHandle(JoinReader.AtomOfTerm(0, 12345)));
        Assert.Empty(AtomKeys(snapshot, JoinReader.AtomOfTerm(0, 12345)));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void TheRowsOfTheOuterEntriesAreNotListedAndTheRowsWithSeveralKeysAreCountedAtTheCrossing()
    {
        using JoinStorageHarness storage = Open();

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.SetOuterKeys(100, [5]);
            writer.SetOuterKeys(104, [5, 9]);
            writer.SetOuterKeys(108, [1, 2, 3]);
            writer.SetOuterKeys(104, [5, 9, 10]);
            write.Commit(Generation(1));
        }

        using (JoinIndexSnapshot snapshot = storage.OpenSnapshot())
        {
            Assert.Equal(new long[] { 5, 9, 10 }, OuterKeysOf(snapshot, 104));
            Assert.Equal(new long[] { 1, 2, 3 }, OuterKeysOf(snapshot, 108));
            Assert.Equal(2, snapshot.RowsWithSeveralKeys);

            // the list of the rows with several keys of an Expanded file is not written
            Assert.Equal(ListStore.EmptyHandle, snapshot.SlotHandle(snapshot.MultiKeySlot));
        }

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.SetOuterKeys(104, [9]);
            writer.SetOuterKeys(108, default);
            writer.SetOuterKeys(100, [5, 6]);
            write.Commit(Generation(2));
        }

        using JoinIndexSnapshot after = storage.OpenSnapshot();
        Assert.Equal(1, after.RowsWithSeveralKeys);
        Assert.Equal(ListStore.EmptyHandle, after.SlotHandle(after.MultiKeySlot));
        Assert.Null(OuterKeysOf(after, 108));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void AddOuterKeyCountsTheRowAtTheCrossingToo()
    {
        using JoinStorageHarness storage = Open();

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.AddOuterKey(100, 7);
            writer.AddOuterKey(100, 7);
            writer.AddOuterKey(100, 3);
            writer.AddOuterKey(100, 9);
            writer.AddOuterKey(104, 3);
            write.Commit(Generation(1));
        }

        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
        Assert.Equal(new long[] { 3, 7, 9 }, OuterKeysOf(snapshot, 100));
        Assert.Equal(1, snapshot.RowsWithSeveralKeys);
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void AddOuterKeysAndAddInnerKeysMergeTheKeysIntoTheRowAndWriteItOnce()
    {
        using JoinStorageHarness storage = Open();

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.AddOuterKeys(100, [3, 7]);
            Assert.Equal(1, writer.KeyRowWrites);
            writer.AddOuterKeys(100, [7, 9]);
            writer.AddOuterKeys(100, [3, 9]);
            writer.AddOuterKeys(100, default);
            writer.AddOuterKeys(100, [1, 8, 20]);
            Assert.Equal(3, writer.KeyRowWrites);
            writer.AddOuterKeys(104, [5]);
            writer.AddOuterKeys(104, [5]);
            Assert.Equal(4, writer.KeyRowWrites);

            writer.AddInnerKeys(40, [2, 4]);
            writer.AddInnerKeys(40, [3, 4]);
            writer.AddInnerKeys(44, [6]);
            Assert.Equal(7, writer.KeyRowWrites);
            write.Commit(Generation(1));
        }

        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
        Assert.Equal(new long[] { 1, 3, 7, 8, 9, 20 }, OuterKeysOf(snapshot, 100));
        Assert.Equal(new long[] { 5 }, OuterKeysOf(snapshot, 104));
        Assert.Equal(1, snapshot.RowsWithSeveralKeys);
        Assert.Equal(new long[] { 2, 3, 4 }, InnerKeysOf(snapshot, 40));
        Assert.Equal(new long[] { 6 }, InnerKeysOf(snapshot, 44));
    }

    private static long[] InnerKeysOf(JoinReader reader, long inner)
    {
        var keys = new ContextBoundNativeList<long>(reader.Transaction.Allocator);
        try
        {
            return reader.TryGetInnerKeys(inner, ref keys) ? keys.ToSpan().ToArray() : null;
        }
        finally
        {
            keys.Dispose();
        }
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void AnExpandedFileKeepsTheListOfTheRowsWithSeveralKeysAndTheCountOfTheRefresh()
    {
        using JoinStorageHarness storage = Open(JoinStorageMode.Expanded);

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.SetOuterKeys(104, [5, 9]);
            writer.AddRowsWithSeveralKeys(3);
            write.Commit(Generation(1, JoinStorageMode.Expanded));
        }

        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
        Assert.Equal(new long[] { 104 }, JoinWriterTests.ReadList(snapshot, snapshot.SlotHandle(snapshot.MultiKeySlot)));
        Assert.Equal(3, snapshot.RowsWithSeveralKeys);
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void EachModeRefusesTheCallsOfTheOther()
    {
        using (JoinStorageHarness indirect = Open())
        using (JoinStorageHarness.Write write = indirect.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            Assert.Throws<InvalidOperationException>(() => writer.AddToAtom(JoinReader.AtomOfAny(), 4));
            Assert.Throws<InvalidOperationException>(() => writer.RemoveFromAtom(JoinReader.AtomOfAny(), 4));
            Assert.Throws<InvalidOperationException>(() => writer.AddToAny(4));
            Assert.Throws<InvalidOperationException>(() => writer.AddToGrouping(0, 4));
            Assert.Throws<InvalidOperationException>(() => writer.AddToTerm(0, 8, 4));
            Assert.Throws<InvalidOperationException>(() => writer.AddRowsWithSeveralKeys(1));
            Assert.Throws<InvalidOperationException>(() => write.Commit(Generation(1, JoinStorageMode.Expanded)));

            // the members of a grouping are inner documents in both modes
            writer.AddGroupingMember(0, 400);
            Assert.True(writer.IsGroupingMember(0, 400));
        }

        using (JoinStorageHarness expanded = Open(JoinStorageMode.Expanded))
        using (JoinStorageHarness.Write write = expanded.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            Assert.Throws<InvalidOperationException>(() => writer.AddKeyToAtom(JoinReader.AtomOfAny(), 4));
            Assert.Throws<InvalidOperationException>(() => writer.RemoveKeyFromAtom(JoinReader.AtomOfAny(), 4));
            Assert.Throws<InvalidOperationException>(() => write.Commit(Generation(1)));

            var keys = new ContextBoundNativeList<long>(writer.Transaction.Allocator);
            try
            {
                Assert.Throws<InvalidOperationException>(() => writer.ReadAtomKeys(JoinReader.AtomOfAny(), ref keys));
                Assert.Throws<InvalidOperationException>(() => writer.CountOfAtomKeys(JoinReader.AtomOfAny()));
            }
            finally
            {
                keys.Dispose();
            }
        }
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void AKeyReferenceBeyond48BitsIsRefused()
    {
        JoinReader.CheckKeyRef(1);
        JoinReader.CheckKeyRef(JoinReader.MaxKeyRef);
        Assert.Equal((1L << 48) - 1, JoinReader.MaxKeyRef);
        Assert.Throws<JoinUnsupportedException>(() => JoinReader.CheckKeyRef(JoinReader.MaxKeyRef + 1));
        Assert.Throws<JoinUnsupportedException>(() => JoinReader.CheckKeyRef(-1));
        Assert.Throws<JoinUnsupportedException>(() => JoinReader.CheckKeyRef(long.MaxValue));

        using JoinStorageHarness storage = Open();
        using JoinStorageHarness.Write write = storage.BeginWrite();
        write.Writer.AddKeyToAtom(JoinReader.AtomOfAny(), JoinReader.MaxKeyRef);
        var exception = Assert.Throws<JoinUnsupportedException>(() => write.Writer.AddKeyToAtom(JoinReader.AtomOfAny(), JoinReader.MaxKeyRef + 1));
        Assert.Contains("too many", exception.Message);
    }

    [RavenTheory(RavenTestCategory.Voron)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AnIndirectFileHoldsAGenerationOfTheFormat3(bool innerPartial, bool outerPartial)
    {
        using JoinStorageHarness storage = Open();

        using (JoinStorageHarness.Write write = storage.BeginWrite())
            write.Commit(Generation(1, innerPartial: innerPartial, outerPartial: outerPartial));

        Assert.Equal(JoinStorageMode.Indirect, storage.Published.Mode);
        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
        Assert.Equal(JoinStorageMode.Indirect, snapshot.Generation.Mode);
        Assert.Equal(1, snapshot.Generation.Serial);
        Assert.Equal(innerPartial, snapshot.Generation.InnerPartial);
        Assert.Equal(outerPartial, snapshot.Generation.OuterPartial);

        Tree meta = snapshot.Transaction.ReadTree(JoinNames.Meta);
        Assert.True(meta.TryRead(JoinNames.GenerationRow, out global::Voron.ValueReader reader));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(reader.AsSpan()));
    }

    private sealed class Model(int seed)
    {
        public readonly Random Random = new(seed);
        public readonly Dictionary<long, SortedSet<long>> CodesOfKey = new();
        public readonly Dictionary<long, long[]> OuterKeys = new();
    }

    private static void CheckListsAreTheInverseOfTheAtomsOfTheKeys(JoinReader reader, Model model, long[] keyRefs, long[] atoms)
    {
        foreach (long atom in atoms)
        {
            long code = JoinReader.CodeOfAtom(atom);
            long[] expected = model.CodesOfKey.Where(x => x.Value.Contains(code)).Select(x => x.Key).Order().ToArray();
            Assert.Equal(expected, AtomKeys(reader, atom));
        }

        foreach (long keyRef in keyRefs)
            Assert.Equal(model.CodesOfKey.TryGetValue(keyRef, out SortedSet<long> codes) ? codes.ToArray() : [], KeyAtoms(reader, keyRef));
    }

    [RavenTheory(RavenTestCategory.Voron)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void RandomChangesKeepEveryAtomListTheInverseOfTheAtomsOfTheKeysAndTheRowsWithSeveralKeysExact(int seed)
    {
        using JoinStorageHarness storage = Open();
        var model = new Model(seed);
        Random random = model.Random;

        long[] keyRefs;
        long[] atoms;
        using (JoinStorageHarness.Write setup = storage.BeginWrite())
        {
            JoinWriter writer = setup.Writer;
            keyRefs = Enumerable.Range(0, Keys).Select(i => writer.GetOrAddKey(Bytes("key" + i))).ToArray();
            var list = new List<long> { JoinReader.AtomOfAny(), writer.AtomOfSeveralInner };
            list.AddRange(Enumerable.Range(0, Terms).Select(i => JoinReader.AtomOfTerm(0, writer.GetOrAddTerm(0, Bytes("t" + i)))));
            list.AddRange(Enumerable.Range(0, Groupings).Select(JoinReader.AtomOfGrouping));
            atoms = list.ToArray();
            setup.Commit(Generation(0));
        }

        for (int commit = 1; commit <= 12; commit++)
        {
            using (JoinStorageHarness.Write write = storage.BeginWrite())
            {
                JoinWriter writer = write.Writer;
                writer.FlushThreshold = random.Next(2) == 0 ? 8 : JoinWriter.DefaultFlushThreshold;

                int operations = random.Next(20, 120);
                for (int i = 0; i < operations; i++)
                {
                    if (random.Next(10) < 7)
                    {
                        // a transition of a pair of a key and an atom, the way the refresh makes it: the atoms of the key and the list of the atom
                        long keyRef = keyRefs[random.Next(random.Next(4) == 0 ? 3 : Keys)];
                        long atom = atoms[random.Next(atoms.Length)];
                        long code = JoinReader.CodeOfAtom(atom);
                        if (model.CodesOfKey.TryGetValue(keyRef, out SortedSet<long> codes) == false)
                            model.CodesOfKey[keyRef] = codes = new SortedSet<long>();

                        if (codes.Add(code))
                        {
                            writer.AddKeyAtom(keyRef, code);
                            writer.AddKeyToAtom(atom, keyRef);
                        }
                        else
                        {
                            codes.Remove(code);
                            writer.RemoveKeyAtom(keyRef, code);
                            writer.RemoveKeyFromAtom(atom, keyRef);
                        }
                    }
                    else
                    {
                        long outer = (random.Next(30) + 1) * 4L;
                        long[] rowKeys = Enumerable.Range(0, random.Next(0, 4)).Select(_ => keyRefs[random.Next(Keys)]).Distinct().Order().ToArray();
                        writer.SetOuterKeys(outer, rowKeys);
                        if (rowKeys.Length == 0)
                            model.OuterKeys.Remove(outer);
                        else
                            model.OuterKeys[outer] = rowKeys;
                    }
                }

                if (random.Next(3) == 0)
                    writer.Flush();

                write.Commit(Generation(commit));
            }

            using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
            CheckListsAreTheInverseOfTheAtomsOfTheKeys(snapshot, model, keyRefs, atoms);
            Assert.Equal(model.OuterKeys.Count(x => x.Value.Length >= 2), snapshot.RowsWithSeveralKeys);
            Assert.Equal(model.OuterKeys.Count, snapshot.NumberOfJoined);
            foreach ((long outer, long[] rowKeys) in model.OuterKeys)
                Assert.Equal(rowKeys, OuterKeysOf(snapshot, outer));

            Assert.Equal(ListStore.EmptyHandle, snapshot.SlotHandle(snapshot.MultiKeySlot));
        }
    }
    [RavenFact(RavenTestCategory.Voron)]
    public void AFileWhoseKeysAreTheIdsOfTheInnerDocumentsKeepsNoCountsAndAKeyHasAGroupWhenItHasAtoms()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory(JoinSchema.Create(1, Groupings, 11, null, isFieldJoin: true, mode: JoinStorageMode.Indirect, keysAreInnerIds: true));
        long code = JoinReader.CodeOfAny();
        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            long withGroup = writer.GetOrAddKey(Bytes("contexts/1"));
            long without = writer.GetOrAddKey(Bytes("contexts/2"));
            writer.AddKeyAtom(withGroup, code);
            Assert.Throws<InvalidOperationException>(() => writer.AddPairCount(withGroup, code, 1));
            Assert.Throws<InvalidOperationException>(() => writer.GetPairCount(withGroup, code));
            Assert.True(writer.KeyHasGroup(withGroup));
            Assert.False(writer.KeyHasGroup(without));
            write.Commit(Generation(1));
        }

        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
        Assert.Equal(0, snapshot.NumberOfPairCounts);
        Assert.True(snapshot.TryGetKey(Bytes("contexts/1"), out long key));
        Assert.True(snapshot.KeyHasGroup(key));
        Assert.True(snapshot.TryGetKey(Bytes("contexts/2"), out long other));
        Assert.False(snapshot.KeyHasGroup(other));
    }
}
