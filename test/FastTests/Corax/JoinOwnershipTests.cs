using System;
using System.Buffers;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Corax;
using Corax.Mappings;
using Corax.Querying;
using Corax.Querying.Matches.TermProviders;
using FastTests.Voron;
using Sparrow.Server;
using Tests.Infrastructure;
using Voron;
using Xunit;
using IndexSearcher = Corax.Querying.IndexSearcher;
using IndexWriter = Corax.Indexing.IndexWriter;

namespace FastTests.Corax;

// What a JOIN lends out has one owner and is never handed to two users at once
public class JoinOwnershipTests : StorageTest
{
    private const int IdField = 0, TagsField = 1;
    private const int Docs = 400;

    public JoinOwnershipTests(ITestOutputHelper output) : base(output)
    {
        using var knownFields = CreateKnownFields(Allocator);
        using var indexWriter = new IndexWriter(Env, knownFields, SupportedFeatures.All);
        for (var i = 0; i < Docs; i++)
        {
            var id = Encoding.UTF8.GetBytes($"docs/{i}");
            using var builder = indexWriter.Index(id);
            builder.Write(IdField, null, id);
            builder.Write(TagsField, null, Encoding.UTF8.GetBytes($"tag{i % 100:D3}"));
            builder.EndWriting();
        }

        indexWriter.Commit();
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void ConcurrentReadersOfTheOrderShareOneArray()
    {
        using var keys = new JoinTermScores(Allocator);
        var random = new Random(3);
        for (var i = 0; i < 50_000; i++)
            keys.Add(Encoding.UTF8.GetBytes($"key{random.Next():D10}"), i);

        const int threads = 8;
        var start = new Barrier(threads);
        var addresses = new nint[threads];
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            start.SignalAndWait();
            var order = keys.SortedOrder();
            addresses[t] = AddressOf(order);
        })).ToList();
        workers.ForEach(x => x.Start());
        workers.ForEach(x => x.Join());

        Assert.Single(addresses.Distinct());
    }

    private static unsafe nint AddressOf(ReadOnlySpan<int> span) => (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));

    [RavenFact(RavenTestCategory.Corax)]
    public void CopiesOfATermProviderNeverWriteIntoAnArrayThatWasReturnedToThePool()
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata("Tags");
        var tree = searcher.Transaction.ReadTree(Constants.IndexWriter.FieldsSlice).CompactTreeFor("Tags");
        using var keys = new JoinTermScores(searcher.Allocator);
        for (var i = 0; i < 100; i++)
            keys.Add(Encoding.UTF8.GetBytes($"tag{i:D3}"), 1f);

        var first = new JoinKeysTermProvider(searcher, metadata, tree, keys, keysAreIndexedTerms: true);
        Assert.True(first.Next(out _, out _));
        var copy = first;

        // the first one reaches its end: whatever it rented is back in the pool, and another user takes it
        while (first.Next(out _, out _))
        {
        }

        var other = ArrayPool<byte>.Shared.Rent(256);
        other.AsSpan().Fill(0xAB);
        for (var i = 0; i < 20; i++)
            Assert.True(copy.Next(out _, out _));

        Assert.All(other, x => Assert.Equal(0xAB, x));
        ArrayPool<byte>.Shared.Return(other);
    }

    private IndexFieldsMapping CreateKnownFields(ByteStringContext bsc)
    {
        Slice.From(bsc, "Id", ByteStringType.Immutable, out var idSlice);
        Slice.From(bsc, "Tags", ByteStringType.Immutable, out var tagsSlice);

        using var builder = IndexFieldsMappingBuilder.CreateForWriter(false)
            .AddBinding(IdField, idSlice)
            .AddBinding(TagsField, tagsSlice);

        return builder.Build();
    }
}
