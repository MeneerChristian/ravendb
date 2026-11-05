using Corax.Indexing;
using Raven.Client.Documents.Indexes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FastTests;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Utils;
using Tests.Infrastructure;
using Voron.Data.Containers;
using Voron.Util;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Storage;

public class JoinWriterTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    internal static long[] ReadList(JoinReader reader, long handle)
    {
        var output = new ContextBoundNativeList<long>(reader.Transaction.Allocator);
        try
        {
            reader.ReadList(handle, ref output);
            Assert.Equal(output.Count, reader.CountOf(handle));
            return output.ToSpan().ToArray();
        }
        finally
        {
            output.Dispose();
        }
    }

    internal static long[] ReadTerm(JoinReader reader, int field, string term)
    {
        return reader.TryGetTermList(field, Bytes(term), out long handle) ? ReadList(reader, handle) : null;
    }

    private static long[] InnerTerms(JoinReader reader, int field, long innerEntryId)
    {
        var output = new ContextBoundNativeList<long>(reader.Transaction.Allocator);
        try
        {
            return reader.TryGetInnerTerms(field, innerEntryId, ref output) ? output.ToSpan().ToArray() : null;
        }
        finally
        {
            output.Dispose();
        }
    }

    private static JoinIndexGeneration Generation(long marker) => new(0, Guid.NewGuid(), new JoinCursors(new[] { ("Messages", marker, marker) }), Guid.NewGuid(), JoinCursors.Empty, marker, 0);

    [RavenFact(RavenTestCategory.Voron)]
    public void TheQueueOfListChangesHoldsMemoryForAboutItsOwnSize()
    {
        const int queued = 4 * 1024 * 1024 - 1;
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();
        using JoinStorageHarness.Write write = storage.BeginWrite();
        JoinWriter writer = write.Writer;
        writer.FlushThreshold = queued + 1; // the default of a refresh, no flush before the last change

        long before = NativeMemory.CurrentThreadStats.TotalAllocated;
        for (int i = 1; i <= queued; i++)
            writer.AddToAtom(8, i * 4L);

        long held = NativeMemory.CurrentThreadStats.TotalAllocated - before;
        Assert.Equal(0, writer.Flushes);

        // a change is 24 bytes: 96 MB in a buffer of 128 MB; the buffers of the sizes before it must not add as much again
        Assert.True(held < 200L * 1024 * 1024, $"the queue of {queued} changes holds {held / (1024 * 1024)} MB");
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void NewFileIsEmptyAndHasTheInitialGeneration()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();

        Assert.Equal(0, storage.Published.Serial);

        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
        Assert.Equal(0, snapshot.Generation.Serial);
        Assert.False(snapshot.TryGetOuterDocument(1, out _));
        Assert.False(snapshot.TryGetInnerDocument(1, out _));
        Assert.False(snapshot.TryGetJoined(4, out _));
        Assert.False(snapshot.TryGetTermList(0, Bytes("x"), out _));
        Assert.Equal(0, snapshot.SlotHandle(JoinReader.AnySlot));
        Assert.Equal(0, snapshot.NumberOfOuterDocuments);
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void ListsThatWereWrittenForAnotherDefinitionAreRefused()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory(JoinStorageHarness.CreateSchema(fields: 2, groupings: 1, hash: 1));

        var exception = Assert.Throws<InvalidDataException>(() => storage.BeginWrite(JoinStorageHarness.CreateSchema(fields: 2, groupings: 1, hash: 2)));
        Assert.Contains("has to be reset", exception.Message);

        // the refused attempt left nothing open: the lists are still written with their own schema
        using (JoinStorageHarness.Write write = storage.BeginWrite())
            write.Commit(Generation(1));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void DocumentRowsFindTheEntryAndReportTheEntryTheyReplace()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();
        long hash = storage.HashDocumentId(Bytes("contexts/1"));

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            Assert.Equal(0, writer.SetOuterDocument(hash, 100));
            Assert.Equal(100, writer.SetOuterDocument(hash, 200));
            Assert.Equal(0, writer.SetInnerDocument(hash, 300));
            Assert.Equal(0, writer.SetJoined(200, 300));
            write.Commit(Generation(1));
        }

        using (JoinIndexSnapshot snapshot = storage.OpenSnapshot())
        {
            Assert.True(snapshot.TryGetOuterDocument(hash, out long outer));
            Assert.Equal(200, outer);
            Assert.True(snapshot.TryGetInnerDocument(hash, out long inner));
            Assert.Equal(300, inner);
            Assert.True(snapshot.TryGetJoined(200, out long joined));
            Assert.Equal(300, joined);
            Assert.Equal(1, snapshot.NumberOfOuterDocuments);
            Assert.Equal(1, snapshot.NumberOfJoined);
        }

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            Assert.True(writer.RemoveOuterDocument(hash));
            Assert.False(writer.RemoveOuterDocument(hash));
            Assert.True(writer.RemoveJoined(200));
            Assert.True(writer.RemoveInnerDocument(hash));
            write.Commit(Generation(2));
        }

        using JoinIndexSnapshot after = storage.OpenSnapshot();
        Assert.False(after.TryGetOuterDocument(hash, out _));
        Assert.False(after.TryGetInnerDocument(hash, out _));
        Assert.False(after.TryGetJoined(200, out _));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void HashOfADocumentIdDependsOnTheSeedOfTheFile()
    {
        using JoinStorageHarness first = JoinStorageHarness.OpenInMemory();
        using JoinStorageHarness second = JoinStorageHarness.OpenInMemory();

        Assert.NotEqual(first.Seed, second.Seed);
        Assert.Equal(first.HashDocumentId(Bytes("a/1")), first.HashDocumentId(Bytes("a/1")));
        Assert.NotEqual(first.HashDocumentId(Bytes("a/1")), second.HashDocumentId(Bytes("a/1")));
        Assert.NotEqual(first.HashDocumentId(Bytes("a/1")), first.HashDocumentId(Bytes("a/2")));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void TermsGetOneStableReferenceAndAreBounded()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();

        long first;
        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            first = writer.GetOrAddTerm(0, Bytes("netherlands"));
            Assert.Equal(first, writer.GetOrAddTerm(0, Bytes("netherlands")));
            Assert.NotEqual(first, writer.GetOrAddTerm(0, Bytes("belgium")));
            Assert.Throws<ArgumentException>(() => writer.GetOrAddTerm(0, new byte[JoinLimits.MaxTermLength + 1]));
            writer.GetOrAddTerm(0, new byte[JoinLimits.MaxTermLength].Select(_ => (byte)'x').ToArray());
            write.Commit(Generation(1));
        }

        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
        Assert.True(snapshot.TryGetTerm(0, Bytes("netherlands"), out long reference, out long handle));
        Assert.Equal(first, reference);
        Assert.Equal(ListStore.EmptyHandle, handle);
        Assert.False(snapshot.TryGetTerm(1, Bytes("netherlands"), out _, out _));
        Assert.Equal(3, snapshot.NumberOfTerms(0));

        Span<byte> copy = stackalloc byte[JoinLimits.MaxTermLength];
        int length = snapshot.CopyTerm(0, first, copy);
        Assert.Equal("netherlands", Encoding.UTF8.GetString(copy.Slice(0, length)));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void ListsOfTermsAreVisibleAfterTheCommitAndNotBefore()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();

        using JoinIndexSnapshot before = storage.OpenSnapshot();
        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            long nl = writer.GetOrAddTerm(0, Bytes("nl"));
            long be = writer.GetOrAddTerm(0, Bytes("be"));
            writer.AddToTerm(0, nl, 40);
            writer.AddToTerm(0, nl, 8);
            writer.AddToTerm(0, be, 16);
            writer.AddToTerm(1, writer.GetOrAddTerm(1, Bytes("nl")), 24);

            Assert.Null(ReadTerm(before, 0, "nl"));
            JoinIndexGeneration published = write.Commit(Generation(5));
            Assert.Equal(1, published.Serial);
        }

        Assert.Null(ReadTerm(before, 0, "nl"));
        Assert.Equal(0, before.Generation.Serial);

        using JoinIndexSnapshot after = storage.OpenSnapshot();
        Assert.Equal(new long[] { 8, 40 }, ReadTerm(after, 0, "nl"));
        Assert.Equal(new long[] { 16 }, ReadTerm(after, 0, "be"));
        Assert.Equal(new long[] { 24 }, ReadTerm(after, 1, "nl"));
        Assert.Equal(1, after.Generation.Serial);
        Assert.Equal(5, after.Generation.InnerWatermark);
        Assert.Equal(after.Generation, storage.Published);
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void WriterThatIsNotCommittedChangesNothing()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.AddToTerm(0, writer.GetOrAddTerm(0, Bytes("nl")), 40);
            writer.AddToAny(40);
            writer.SetOuterDocument(1, 40);
            writer.Flush();
        }

        Assert.Equal(0, storage.Published.Serial);
        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
        Assert.Null(ReadTerm(snapshot, 0, "nl"));
        Assert.Equal(0, snapshot.SlotHandle(JoinReader.AnySlot));
        Assert.False(snapshot.TryGetOuterDocument(1, out _));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void ChangesOfOneEntryInOneListCountInTheOrderTheyWereMade()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            long term = writer.GetOrAddTerm(0, Bytes("t"));
            writer.AddToTerm(0, term, 4);
            writer.AddToTerm(0, term, 8);
            writer.AddToTerm(0, term, 12);
            writer.AddToTerm(0, term, 16);
            write.Commit(Generation(1));
        }

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            long term = writer.GetOrAddTerm(0, Bytes("t"));
            writer.AddToTerm(0, term, 20);
            writer.RemoveFromTerm(0, term, 20);

            writer.RemoveFromTerm(0, term, 4);
            writer.AddToTerm(0, term, 4);

            writer.AddToTerm(0, term, 24);
            writer.RemoveFromTerm(0, term, 24);
            writer.AddToTerm(0, term, 24);

            writer.RemoveFromTerm(0, term, 8);
            writer.AddToTerm(0, term, 8);
            writer.RemoveFromTerm(0, term, 8);

            writer.RemoveFromTerm(0, term, 12);
            write.Commit(Generation(2));
        }

        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
        Assert.Equal(new long[] { 4, 16, 24 }, ReadTerm(snapshot, 0, "t"));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void AnyListAndGroupingsAreKeptSeparately()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory(JoinStorageHarness.CreateSchema(fields: 1, groupings: 2));

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.AddToAny(4);
            writer.AddToAny(8);
            writer.AddToGrouping(0, 4);
            writer.AddToGrouping(1, 8);
            writer.AddToGrouping(1, 12);
            writer.AddGroupingMember(0, 400);
            writer.AddGroupingMember(1, 400);
            writer.AddGroupingMember(1, 404);
            Assert.True(writer.IsGroupingMember(1, 404));
            Assert.False(writer.IsGroupingMember(0, 404));
            write.Commit(Generation(1));
        }

        using (JoinIndexSnapshot snapshot = storage.OpenSnapshot())
        {
            Assert.Equal(new long[] { 4, 8 }, ReadList(snapshot, snapshot.SlotHandle(JoinReader.AnySlot)));
            Assert.Equal(new long[] { 4 }, ReadList(snapshot, snapshot.SlotHandle(JoinReader.GroupingListSlot(0))));
            Assert.Equal(new long[] { 8, 12 }, ReadList(snapshot, snapshot.SlotHandle(JoinReader.GroupingListSlot(1))));
            Assert.Equal(new long[] { 400 }, ReadList(snapshot, snapshot.SlotHandle(snapshot.GroupingMembersSlot(0))));
            Assert.Equal(new long[] { 400, 404 }, ReadList(snapshot, snapshot.SlotHandle(snapshot.GroupingMembersSlot(1))));
        }

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.RemoveFromAny(4);
            writer.RemoveFromGrouping(1, 8);
            writer.RemoveGroupingMember(1, 400);
            Assert.False(writer.IsGroupingMember(1, 400));
            write.Commit(Generation(2));
        }

        using JoinIndexSnapshot after = storage.OpenSnapshot();
        Assert.Equal(new long[] { 8 }, ReadList(after, after.SlotHandle(JoinReader.AnySlot)));
        Assert.Equal(new long[] { 12 }, ReadList(after, after.SlotHandle(JoinReader.GroupingListSlot(1))));
        Assert.Equal(new long[] { 404 }, ReadList(after, after.SlotHandle(after.GroupingMembersSlot(1))));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void InnerEntryRemembersItsTermsForSingleAndMultiValuedFields()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory(JoinStorageHarness.CreateSchema(fields: 2, groupings: 0, multiValued: new[] { false, true }));

        long a, b, c;
        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            a = writer.GetOrAddTerm(0, Bytes("a"));
            b = writer.GetOrAddTerm(1, Bytes("b"));
            c = writer.GetOrAddTerm(1, Bytes("c"));

            writer.SetInnerTerms(0, 400, new[] { a });
            writer.SetInnerTerms(1, 400, new[] { b, c });
            writer.SetInnerTerms(1, 404, new[] { c });
            Assert.Throws<ArgumentException>(() => writer.SetInnerTerms(0, 408, new[] { a, a }));
            Assert.Throws<ArgumentException>(() => writer.SetInnerTerms(1, 408, new long[JoinLimits.MaxTermsPerEntry + 1]));
            write.Commit(Generation(1));
        }

        using (JoinIndexSnapshot snapshot = storage.OpenSnapshot())
        {
            Assert.Equal(new[] { a }, InnerTerms(snapshot, 0, 400));
            Assert.Equal(new[] { b, c }, InnerTerms(snapshot, 1, 400));
            Assert.Equal(new[] { c }, InnerTerms(snapshot, 1, 404));
            Assert.Null(InnerTerms(snapshot, 0, 404));
            Assert.Equal(1, snapshot.NumberOfInnerEntriesWithTerms(0));
            Assert.Equal(2, snapshot.NumberOfInnerEntriesWithTerms(1));
        }

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.SetInnerTerms(1, 400, new[] { c });
            writer.SetInnerTerms(1, 404, Array.Empty<long>());
            writer.SetInnerTerms(0, 400, Array.Empty<long>());
            write.Commit(Generation(2));
        }

        using JoinIndexSnapshot after = storage.OpenSnapshot();
        Assert.Equal(new[] { c }, InnerTerms(after, 1, 400));
        Assert.Null(InnerTerms(after, 1, 404));
        Assert.Null(InnerTerms(after, 0, 400));
        Assert.Equal(1, after.NumberOfInnerEntriesWithTerms(1));
        Assert.Equal(0, after.NumberOfInnerEntriesWithTerms(0));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void ReplacedTermsOfAnInnerEntryDoNotStayBehind()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory(JoinStorageHarness.CreateSchema(fields: 1, groupings: 0, multiValued: new[] { true }));

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            long[] terms = Enumerable.Range(0, 4).Select(i => writer.GetOrAddTerm(0, Bytes("t" + i))).ToArray();
            for (int round = 0; round < 500; round++)
            {
                writer.SetInnerTerms(0, 400, terms.Take(2 + round % 3).ToArray());
                writer.SetInnerTerms(0, 404, terms.Skip(1).Take(2 + round % 3).ToArray());
            }

            writer.SetInnerTerms(0, 404, Array.Empty<long>());
            write.Commit(Generation(1));
        }

        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
        Assert.Single(Container.GetAllIds(snapshot.Transaction.LowLevelTransaction, snapshot.Transaction.OpenContainer(JoinStorageFormat.Lists)));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void ChangesAreAppliedInBatchesWhenTheQueueIsFull()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.FlushThreshold = 100;
            long term = writer.GetOrAddTerm(0, Bytes("hub"));
            for (int i = 1; i <= 1000; i++)
                writer.AddToTerm(0, term, i * 4);

            Assert.Equal(10, writer.Flushes);
            write.Commit(Generation(1));
        }

        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
        Assert.Equal(Enumerable.Range(1, 1000).Select(i => i * 4L).ToArray(), ReadTerm(snapshot, 0, "hub"));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void LargeListIsAPostingListThatCoraxCanOpen()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            long term = writer.GetOrAddTerm(0, Bytes("hub"));
            for (int i = 1; i <= 50_000; i++)
                writer.AddToTerm(0, term, i * 4);

            write.Commit(Generation(1));
        }

        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
        Assert.True(snapshot.TryGetTermList(0, Bytes("hub"), out long handle));
        Assert.Equal(TermIdMask.PostingList, ListStore.KindOf(handle));
        Assert.Equal(50_000, snapshot.CountOf(handle));

        using var list = snapshot.OpenPostingList(handle);
        Assert.Equal(50_000, list.State.NumberOfEntries);
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void SnapshotKeepsTheListsAndTheGenerationItWasOpenedWith()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();
        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.AddToTerm(0, writer.GetOrAddTerm(0, Bytes("t")), 4);
            write.Commit(Generation(1));
        }

        using JoinIndexSnapshot old = storage.OpenSnapshot();
        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.AddToTerm(0, writer.GetOrAddTerm(0, Bytes("t")), 8);
            writer.RemoveFromTerm(0, writer.GetOrAddTerm(0, Bytes("t")), 4);
            write.Commit(Generation(2));
        }

        using JoinIndexSnapshot current = storage.OpenSnapshot();
        Assert.Equal(new long[] { 4 }, ReadTerm(old, 0, "t"));
        Assert.Equal(1, old.Generation.Serial);
        Assert.Equal(new long[] { 8 }, ReadTerm(current, 0, "t"));
        Assert.Equal(2, current.Generation.Serial);
    }
}
