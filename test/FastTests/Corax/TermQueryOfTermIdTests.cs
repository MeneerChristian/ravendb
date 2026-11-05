using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Corax;
using Corax.Mappings;
using Corax.Querying;
using Corax.Querying.Matches;
using FastTests.Voron;
using Sparrow.Server;
using Sparrow.Threading;
using Tests.Infrastructure;
using Voron;
using Voron.Data.CompactTrees;
using Xunit;
using IndexSearcher = Corax.Querying.IndexSearcher;
using IndexWriter = Corax.Indexing.IndexWriter;

namespace FastTests.Corax;

// A caller that looked a term up in the tree of its field reads the posting list by the value it found, without looking the term up again
public class TermQueryOfTermIdTests : StorageTest
{
    private const int IdField = 0, KeyField = 1;

    // the number of entries of the lists of the keys: a single entry, a small list, a list of a posting list tree
    private static readonly int[] Sizes = [1, 1, 2, 3, 7, 40, 300, 2_000, 12_000];

    private readonly ByteStringContext _analyzers = new(SharedMultipleUseFlag.None);
    private readonly List<(string Key, List<long> Docs)> _keys = new();

    public TermQueryOfTermIdTests(ITestOutputHelper output) : base(output)
    {
        Index();
    }

    public override async ValueTask DisposeAsync()
    {
        _analyzers.Dispose();
        await base.DisposeAsync();
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void TheListOfATermIsTheSameWhetherTheTermIsLookedUpAgainOrNot()
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        FieldMetadata metadata = searcher.GetFieldMetadata("Key");
        CompactTree tree = searcher.GetTermsFor(metadata.FieldName);
        Assert.NotNull(tree);

        var buffer = new long[512];
        for (int k = 0; k < _keys.Count; k++)
        {
            (string key, List<long> docs) = _keys[k];
            byte[] bytes = Encoding.UTF8.GetBytes(key);
            Assert.True(tree.TryGetValue(bytes, out long termId), key);

            TermMatch byId = searcher.TermQueryOfTermId(termId);
            Slice.From(Allocator, bytes, ByteStringType.Immutable, out Slice slice);
            TermMatch byTerm = searcher.TermQuery(metadata, slice, tree);
            Assert.Equal(byTerm.Count, byId.Count);
            Assert.Equal(docs.Count, byId.Count);

            var fromId = new List<long>();
            int read;
            while ((read = byId.Fill(buffer)) > 0)
                fromId.AddRange(buffer.AsSpan(0, read).ToArray());

            var fromTerm = new List<long>();
            while ((read = byTerm.Fill(buffer)) > 0)
                fromTerm.AddRange(buffer.AsSpan(0, read).ToArray());

            Assert.Equal(fromTerm, fromId);
            Assert.Equal(docs.Count, fromId.Count);
        }

        Assert.False(tree.TryGetValue(Encoding.UTF8.GetBytes("missing"), out _));
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void TheCostOfLookingTheTermUpAgainIsReported()
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        FieldMetadata metadata = searcher.GetFieldMetadata("Key");
        CompactTree tree = searcher.GetTermsFor(metadata.FieldName);
        byte[][] terms = _keys.ConvertAll(x => Encoding.UTF8.GetBytes(x.Key)).ToArray();

        // reported, not asserted: a timing is not a test
        long once = Measure(() =>
        {
            foreach (byte[] term in terms)
            {
                if (tree.TryGetValue(term, out long id))
                    _ = searcher.TermQueryOfTermId(id).Count;
            }
        });

        long twice = Measure(() =>
        {
            foreach (byte[] term in terms)
            {
                if (tree.TryGetValue(term, out _) == false)
                    continue;

                Slice.From(Allocator, term, ByteStringType.Immutable, out Slice slice);
                _ = searcher.TermQuery(metadata, slice, tree).Count;
                slice.Release(Allocator);
            }
        });

        Output.WriteLine($"{terms.Length} terms, count of the list: by the id of the term {once / 1000.0:F1} us per round, looking it up again {twice / 1000.0:F1} us per round");
    }

    private static long Measure(Action work)
    {
        for (int i = 0; i < 2_000; i++)
            work();

        var watch = Stopwatch.StartNew();
        const int rounds = 20_000;
        for (int i = 0; i < rounds; i++)
            work();

        return watch.ElapsedTicks * (1_000_000_000L / Stopwatch.Frequency) / rounds;
    }

    private void Index()
    {
        using var knownFields = CreateKnownFields(Allocator);
        using var indexWriter = new IndexWriter(Env, knownFields, SupportedFeatures.All);
        var lists = new List<long>[Sizes.Length];
        var docOf = new List<(long Doc, int Key)>();
        for (int k = 0; k < Sizes.Length; k++)
        {
            lists[k] = new List<long>();
            for (int d = 0; d < Sizes[k]; d++)
                docOf.Add((docOf.Count, k));
        }

        // the entries are not written in the order of the keys
        var random = new Random(3);
        for (int i = docOf.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (docOf[i], docOf[j]) = (docOf[j], docOf[i]);
        }

        for (int i = 0; i < docOf.Count; i++)
        {
            var id = Encoding.UTF8.GetBytes($"docs/{i}");
            using var builder = indexWriter.Index(id);
            builder.Write(IdField, null, id);
            builder.Write(KeyField, null, Encoding.UTF8.GetBytes($"key/{docOf[i].Key}"));
            builder.EndWriting();
        }

        indexWriter.Commit();
        for (int k = 0; k < Sizes.Length; k++)
            _keys.Add(($"key/{k}", new List<long>(new long[Sizes[k]])));
    }

    private IndexFieldsMapping CreateKnownFields(ByteStringContext bsc)
    {
        Slice.From(bsc, "Id", ByteStringType.Immutable, out var idSlice);
        Slice.From(bsc, "Key", ByteStringType.Immutable, out var keySlice);

        using var builder = IndexFieldsMappingBuilder.CreateForWriter(false)
            .AddBinding(IdField, idSlice)
            .AddBinding(KeyField, keySlice);
        return builder.Build();
    }
}
