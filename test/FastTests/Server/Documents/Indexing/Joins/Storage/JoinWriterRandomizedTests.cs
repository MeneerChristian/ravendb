using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corax.Indexing;
using FastTests;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Tests.Infrastructure;
using Voron.Util;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Storage;

/// <summary>Drives the structures the way the refresher will: moving pairs, flipping the terms of inner entries, removing entries.</summary>
public class JoinWriterRandomizedTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private const int Fields = 3;
    private const int Groupings = 2;
    private const int Outers = 8000;
    private const int Inners = 300;

    private static long OuterId(int i) => (i + 1) * 4096L;

    private static long InnerId(int i) => 1_000_000 + (i + 1) * 4L;

    private sealed class Model
    {
        public readonly Dictionary<long, long> Pair = new();
        public readonly Dictionary<long, string[]>[] Terms = Enumerable.Range(0, Fields).Select(_ => new Dictionary<long, string[]>()).ToArray();
        public readonly HashSet<long>[] Members = Enumerable.Range(0, Groupings).Select(_ => new HashSet<long>()).ToArray();

        public SortedSet<long> TermList(int field, string term)
        {
            var list = new SortedSet<long>();
            foreach ((long outer, long inner) in Pair)
            {
                if (Terms[field].TryGetValue(inner, out string[] terms) && terms.Contains(term))
                    list.Add(outer);
            }

            return list;
        }
    }

    private sealed class Driver(JoinStorageHarness storage, Model model, Random random)
    {
        private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

        private string RandomTerm(int field)
        {
            return field switch
            {
                0 => random.Next(10) < 6 ? "hub" : "t" + random.Next(20),
                1 => "v" + random.Next(300),
                _ => "w" + random.Next(60)
            };
        }

        private string[] RandomTerms(int field)
        {
            if (field < 2)
                return new[] { RandomTerm(field) };

            return Enumerable.Range(0, random.Next(0, 4)).Select(_ => RandomTerm(field)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }

        private void RemovePair(JoinWriter writer, long outer, long inner)
        {
            for (int field = 0; field < Fields; field++)
            {
                if (model.Terms[field].TryGetValue(inner, out string[] terms) == false)
                    continue;

                foreach (string term in terms)
                    writer.RemoveFromTerm(field, writer.GetOrAddTerm(field, Bytes(term)), outer);
            }

            writer.RemoveFromAny(outer);
            for (int g = 0; g < Groupings; g++)
            {
                if (model.Members[g].Contains(inner))
                    writer.RemoveFromGrouping(g, outer);
            }

            writer.RemoveJoined(outer);
            model.Pair.Remove(outer);
        }

        private void AddPair(JoinWriter writer, long outer, long inner)
        {
            for (int field = 0; field < Fields; field++)
            {
                if (model.Terms[field].TryGetValue(inner, out string[] terms) == false)
                    continue;

                foreach (string term in terms)
                    writer.AddToTerm(field, writer.GetOrAddTerm(field, Bytes(term)), outer);
            }

            writer.AddToAny(outer);
            for (int g = 0; g < Groupings; g++)
            {
                if (model.Members[g].Contains(inner))
                    writer.AddToGrouping(g, outer);
            }

            writer.SetJoined(outer, inner);
            model.Pair[outer] = inner;
        }

        private void MoveOuter(JoinWriter writer, long outer, long? inner)
        {
            if (model.Pair.TryGetValue(outer, out long old))
                RemovePair(writer, outer, old);

            if (inner.HasValue)
                AddPair(writer, outer, inner.Value);
        }

        private IEnumerable<long> OutersOf(long inner) => model.Pair.Where(x => x.Value == inner).Select(x => x.Key).ToArray();

        private void FlipInner(JoinWriter writer, long inner, string[][] newTerms, bool[] newMembers)
        {
            long[] outers = OutersOf(inner).ToArray();

            // the old terms come from the file, not from the model
            for (int field = 0; field < Fields; field++)
            {
                var stored = new ContextBoundNativeList<long>(writer.Transaction.Allocator);
                try
                {
                    bool had = writer.TryGetInnerTerms(field, inner, ref stored);
                    Assert.Equal(model.Terms[field].ContainsKey(inner), had);
                    foreach (long termRef in stored.ToSpan())
                        foreach (long outer in outers)
                            writer.RemoveFromTerm(field, termRef, outer);
                }
                finally
                {
                    stored.Dispose();
                }

                var refs = newTerms[field].Select(t => writer.GetOrAddTerm(field, Bytes(t))).OrderBy(x => x).ToArray();
                foreach (long termRef in refs)
                    foreach (long outer in outers)
                        writer.AddToTerm(field, termRef, outer);

                writer.SetInnerTerms(field, inner, refs);
                if (newTerms[field].Length == 0)
                    model.Terms[field].Remove(inner);
                else
                    model.Terms[field][inner] = newTerms[field];
            }

            for (int g = 0; g < Groupings; g++)
            {
                bool was = writer.IsGroupingMember(g, inner);
                Assert.Equal(model.Members[g].Contains(inner), was);
                if (was == newMembers[g])
                    continue;

                foreach (long outer in outers)
                {
                    if (newMembers[g])
                        writer.AddToGrouping(g, outer);
                    else
                        writer.RemoveFromGrouping(g, outer);
                }

                if (newMembers[g])
                {
                    writer.AddGroupingMember(g, inner);
                    model.Members[g].Add(inner);
                }
                else
                {
                    writer.RemoveGroupingMember(g, inner);
                    model.Members[g].Remove(inner);
                }
            }
        }

        public void Round(int operations)
        {
            using JoinStorageHarness.Write write = storage.BeginWrite();
            JoinWriter writer = write.Writer;
            writer.FlushThreshold = random.Next(3) == 0 ? 50 : random.Next(200, 5000);

            for (int i = 0; i < operations; i++)
            {
                switch (random.Next(10))
                {
                    case 0:
                    case 1:
                        {
                            long inner = InnerId(random.Next(Inners));
                            string[][] terms = Enumerable.Range(0, Fields).Select(f => random.Next(8) == 0 ? Array.Empty<string>() : RandomTerms(f)).ToArray();
                            FlipInner(writer, inner, terms, Enumerable.Range(0, Groupings).Select(_ => random.Next(3) == 0).ToArray());
                            break;
                        }
                    case 2:
                        MoveOuter(writer, OuterId(random.Next(Outers)), null);
                        break;
                    default:
                        MoveOuter(writer, OuterId(random.Next(Outers)), InnerId(random.Next(Inners)));
                        break;
                }
            }

            write.Commit(new JoinIndexGeneration(0, Guid.Empty, JoinCursors.Empty, Guid.Empty, JoinCursors.Empty, 0, 0));
        }

        public HashSet<TermIdMask> Verify()
        {
            var kinds = new HashSet<TermIdMask>();
            using JoinIndexSnapshot snapshot = storage.OpenSnapshot();

            for (int field = 0; field < Fields; field++)
            {
                var terms = model.Terms[field].Values.SelectMany(x => x).Distinct().ToList();
                foreach (string term in terms)
                {
                    SortedSet<long> expected = model.TermList(field, term);
                    long[] actual = JoinWriterTests.ReadTerm(snapshot, field, term) ?? Array.Empty<long>();
                    Assert.Equal(expected.ToArray(), actual);
                    if (snapshot.TryGetTermList(field, Bytes(term), out long handle))
                        kinds.Add(ListStore.KindOf(handle));
                }

                foreach ((long inner, string[] expectedTerms) in model.Terms[field])
                {
                    var stored = new ContextBoundNativeList<long>(snapshot.Transaction.Allocator);
                    Assert.True(snapshot.TryGetInnerTerms(field, inner, ref stored));
                    var names = stored.ToSpan().ToArray().Select(r =>
                    {
                        Span<byte> buffer = stackalloc byte[128];
                        int length = snapshot.CopyTerm(field, r, buffer);
                        return Encoding.UTF8.GetString(buffer.Slice(0, length));
                    }).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    stored.Dispose();
                    Assert.Equal(expectedTerms, names);
                }
            }

            Assert.Equal(model.Pair.Keys.OrderBy(x => x).ToArray(), JoinWriterTests.ReadList(snapshot, snapshot.SlotHandle(JoinReader.AnySlot)));
            for (int g = 0; g < Groupings; g++)
            {
                long[] expected = model.Pair.Where(x => model.Members[g].Contains(x.Value)).Select(x => x.Key).OrderBy(x => x).ToArray();
                Assert.Equal(expected, JoinWriterTests.ReadList(snapshot, snapshot.SlotHandle(JoinReader.GroupingListSlot(g))));
                Assert.Equal(model.Members[g].OrderBy(x => x).ToArray(), JoinWriterTests.ReadList(snapshot, snapshot.SlotHandle(snapshot.GroupingMembersSlot(g))));
            }

            Assert.Equal(model.Pair.Count, snapshot.NumberOfJoined);
            foreach ((long outer, long inner) in model.Pair.Take(200))
            {
                Assert.True(snapshot.TryGetJoined(outer, out long stored));
                Assert.Equal(inner, stored);
            }

            return kinds;
        }
    }

    [RavenTheory(RavenTestCategory.Voron)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(12)]
    public void StructuresMatchTheModelAfterRandomMovesFlipsAndRemovals(int seed)
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory(JoinStorageHarness.CreateSchema(Fields, Groupings, multiValued: new[] { false, false, true }));
        var model = new Model();
        var driver = new Driver(storage, model, new Random(seed));
        var kinds = new HashSet<TermIdMask>();

        for (int round = 0; round < 12; round++)
        {
            driver.Round(round == 0 ? 6000 : 400);
            kinds.UnionWith(driver.Verify());
        }

        Assert.Contains(TermIdMask.PostingList, kinds);
        Assert.Contains(TermIdMask.SmallPostingList, kinds);
        Assert.Equal(12, storage.Published.Serial);
    }
}
