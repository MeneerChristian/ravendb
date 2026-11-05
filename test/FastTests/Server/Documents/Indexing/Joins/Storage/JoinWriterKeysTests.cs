using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Tests.Infrastructure;
using Voron.Util;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Storage;

/// <summary>The keys of the inner documents that an outer entry names: one key or several, the list of the entries that have several, and the number of entries joined with several documents.</summary>
public class JoinWriterKeysTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static JoinIndexGeneration Generation(long marker) => new(0, Guid.NewGuid(), new JoinCursors(new[] { ("Messages", marker, marker) }), Guid.NewGuid(), JoinCursors.Empty, marker, 0);

    private static long[] KeysOf(JoinReader reader, long entry)
    {
        var keys = new ContextBoundNativeList<long>(reader.Transaction.Allocator);
        try
        {
            return reader.TryGetOuterKeys(entry, ref keys) ? keys.ToSpan().ToArray() : null;
        }
        finally
        {
            keys.Dispose();
        }
    }

    private static long[] MultiKeyEntries(JoinReader reader) => JoinWriterTests.ReadList(reader, reader.SlotHandle(reader.MultiKeySlot));

    [RavenFact(RavenTestCategory.Voron)]
    public void KeysOfAnEntryAreReadBackAndReplaced()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();
        long[] many = Enumerable.Range(1, JoinLimits.MaxKeysPerEntry).Select(x => (long)x * 7919).ToArray();

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.SetOuterKeys(100, [5]);
            writer.SetOuterKeys(104, [5, 9]);
            writer.SetOuterKeys(108, many);
            writer.SetOuterKeys(112, [1, 2, 3]);
            write.Commit(Generation(1));
        }

        using (JoinIndexSnapshot snapshot = storage.OpenSnapshot())
        {
            Assert.Equal(new long[] { 5 }, KeysOf(snapshot, 100));
            Assert.Equal(new long[] { 5, 9 }, KeysOf(snapshot, 104));
            Assert.Equal(many, KeysOf(snapshot, 108));
            Assert.Equal(new long[] { 1, 2, 3 }, KeysOf(snapshot, 112));
            Assert.Null(KeysOf(snapshot, 116));
            Assert.Equal(4, snapshot.NumberOfJoined);
            Assert.Equal(new long[] { 104, 108, 112 }, MultiKeyEntries(snapshot));
            Assert.True(snapshot.TryGetJoined(104, out long first));
            Assert.Equal(5, first);
        }

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.SetOuterKeys(100, [5, 6]);
            writer.SetOuterKeys(104, [9]);
            writer.SetOuterKeys(108, [4, 8, 15]);
            writer.SetOuterKeys(112, default);
            write.Commit(Generation(2));
        }

        using (JoinIndexSnapshot snapshot = storage.OpenSnapshot())
        {
            Assert.Equal(new long[] { 5, 6 }, KeysOf(snapshot, 100));
            Assert.Equal(new long[] { 9 }, KeysOf(snapshot, 104));
            Assert.Equal(new long[] { 4, 8, 15 }, KeysOf(snapshot, 108));
            Assert.Null(KeysOf(snapshot, 112));
            Assert.Equal(3, snapshot.NumberOfJoined);
            Assert.Equal(new long[] { 100, 108 }, MultiKeyEntries(snapshot));
        }
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void KeysAreAddedOneByOneInOrder()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();
        var random = new Random(3);
        var model = new Dictionary<long, SortedSet<long>>();

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            for (int i = 0; i < 400; i++)
            {
                long entry = 4 * random.Next(10);
                long key = random.Next(30);
                write.Writer.AddOuterKey(entry, key);
                if (model.TryGetValue(entry, out SortedSet<long> keys) == false)
                    model[entry] = keys = [];

                keys.Add(key);
            }

            write.Commit(Generation(1));
        }

        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
        foreach ((long entry, SortedSet<long> keys) in model)
            Assert.Equal(keys.ToArray(), KeysOf(snapshot, entry));

        Assert.Equal(model.Count, snapshot.NumberOfJoined);
        Assert.Equal(model.Where(x => x.Value.Count >= 2).Select(x => x.Key).OrderBy(x => x).ToArray(), MultiKeyEntries(snapshot));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void MoreKeysThanTheLimitAreRefused()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();
        using JoinStorageHarness.Write write = storage.BeginWrite();
        long[] tooMany = Enumerable.Range(1, JoinLimits.MaxKeysPerEntry + 1).Select(x => (long)x).ToArray();
        Assert.Throws<ArgumentException>(() => write.Writer.SetOuterKeys(4, tooMany));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void TheNumberOfEntriesJoinedWithSeveralDocumentsIsWrittenWithTheGeneration()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();

        using (JoinIndexSnapshot snapshot = storage.OpenSnapshot())
            Assert.Equal(0, snapshot.RowsWithSeveralJoined);

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            write.Writer.AddRowsWithSeveralJoined(3);
            write.Writer.AddRowsWithSeveralJoined(-1);
            write.Commit(Generation(1));
        }

        using (JoinIndexSnapshot snapshot = storage.OpenSnapshot())
            Assert.Equal(2, snapshot.RowsWithSeveralJoined);

        // a writer that is not committed changes nothing
        using (JoinStorageHarness.Write write = storage.BeginWrite())
            write.Writer.AddRowsWithSeveralJoined(5);

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            write.Writer.AddRowsWithSeveralJoined(-2);
            write.Commit(Generation(2));
        }

        using (JoinIndexSnapshot snapshot = storage.OpenSnapshot())
            Assert.Equal(0, snapshot.RowsWithSeveralJoined);
    }

    private static long[] InnerKeysOf(JoinReader reader, long document)
    {
        var keys = new ContextBoundNativeList<long>(reader.Transaction.Allocator);
        try
        {
            return reader.TryGetInnerKeys(document, ref keys) ? keys.ToSpan().ToArray() : null;
        }
        finally
        {
            keys.Dispose();
        }
    }

    private static JoinSchema FieldSchema() => JoinSchema.Create(2, 1, 9, null, isFieldJoin: true);

    [RavenFact(RavenTestCategory.Voron)]
    public void KeysOfAnInnerDocumentAreReadBackReplacedAndAddedOneByOne()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory(FieldSchema());
        long[] many = Enumerable.Range(1, JoinLimits.MaxKeysPerEntry).Select(x => (long)x * 7919).ToArray();

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.SetInnerKeys(10, [5]);
            writer.SetInnerKeys(11, [5, 9]);
            writer.SetInnerKeys(12, many);
            writer.AddInnerKey(13, 7);
            writer.AddInnerKey(13, 3);
            writer.AddInnerKey(13, 7);
            write.Commit(Generation(1));
        }

        using (JoinIndexSnapshot snapshot = storage.OpenSnapshot())
        {
            Assert.Equal(new long[] { 5 }, InnerKeysOf(snapshot, 10));
            Assert.Equal(new long[] { 5, 9 }, InnerKeysOf(snapshot, 11));
            Assert.Equal(many, InnerKeysOf(snapshot, 12));
            Assert.Equal(new long[] { 3, 7 }, InnerKeysOf(snapshot, 13));
            Assert.Null(InnerKeysOf(snapshot, 14));
            Assert.Equal(4, snapshot.NumberOfInnerDocumentsWithKey);
        }

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            JoinWriter writer = write.Writer;
            writer.SetInnerKeys(10, [5, 6]);
            writer.SetInnerKeys(11, [9]);
            writer.SetInnerKeys(12, [4, 8, 15]);
            writer.SetInnerKeys(13, default);
            write.Commit(Generation(2));
        }

        using (JoinIndexSnapshot snapshot = storage.OpenSnapshot())
        {
            Assert.Equal(new long[] { 5, 6 }, InnerKeysOf(snapshot, 10));
            Assert.Equal(new long[] { 9 }, InnerKeysOf(snapshot, 11));
            Assert.Equal(new long[] { 4, 8, 15 }, InnerKeysOf(snapshot, 12));
            Assert.Null(InnerKeysOf(snapshot, 13));
            Assert.Equal(3, snapshot.NumberOfInnerDocumentsWithKey);
        }
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void MoreInnerKeysThanTheLimitAreRefused()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory(FieldSchema());
        using JoinStorageHarness.Write write = storage.BeginWrite();
        long[] tooMany = Enumerable.Range(1, JoinLimits.MaxKeysPerEntry + 1).Select(x => (long)x).ToArray();
        Assert.Throws<JoinUnsupportedException>(() => write.Writer.SetInnerKeys(4, tooMany));
    }

    [RavenFact(RavenTestCategory.Voron)]
    public void TheNumberOfEntriesWithSeveralKeysThatHaveAGroupIsWrittenWithTheGeneration()
    {
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory(FieldSchema());

        using (JoinIndexSnapshot snapshot = storage.OpenSnapshot())
            Assert.Equal(0, snapshot.RowsWithSeveralKeys);

        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            write.Writer.AddRowsWithSeveralKeys(4);
            write.Writer.AddRowsWithSeveralKeys(-1);
            write.Commit(Generation(1));
        }

        using (JoinIndexSnapshot snapshot = storage.OpenSnapshot())
        {
            Assert.Equal(3, snapshot.RowsWithSeveralKeys);
            Assert.Equal(0, snapshot.RowsWithSeveralJoined);
        }
    }
}
