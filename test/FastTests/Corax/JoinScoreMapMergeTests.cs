using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corax;
using Corax.Mappings;
using Corax.Querying;
using FastTests.Voron;
using Sparrow.Server;
using Tests.Infrastructure;
using Voron;
using Xunit;
using IndexSearcher = Corax.Querying.IndexSearcher;
using IndexWriter = Corax.Indexing.IndexWriter;

namespace FastTests.Corax;

// An entry under several keys of a JOIN gets the sum of their scores, added in a defined order
public class JoinScoreMapMergeTests : StorageTest
{
    private const int IdField = 0, TagsField = 1;
    private const int Docs = 3000, Tags = 12;

    private readonly List<int[]> _tagsOfEntry = new();

    public JoinScoreMapMergeTests(ITestOutputHelper output) : base(output)
    {
        var random = new Random(7);
        using var knownFields = CreateKnownFields(Allocator);
        using var indexWriter = new IndexWriter(Env, knownFields, SupportedFeatures.All);
        for (var i = 0; i < Docs; i++)
        {
            var id = Encoding.UTF8.GetBytes($"docs/{i}");
            using var builder = indexWriter.Index(id);
            builder.Write(IdField, null, id);
            var tags = Enumerable.Range(0, Tags).OrderBy(_ => random.Next()).Take(random.Next(3, Tags)).ToArray();
            _tagsOfEntry.Add(tags);
            builder.IncrementList();
            foreach (var tag in tags)
                builder.Write(TagsField, null, Encoding.UTF8.GetBytes($"tag{tag:D2}"));
            builder.DecrementList();
            builder.EndWriting();
        }

        indexWriter.Commit();
    }

    // scores of very different magnitudes: the float sum depends on the order of the additions
    private static float ScoreOf(int tag) => tag % 3 == 0 ? 16_777_216f + tag * 4 : 0.37f + tag * 0.011f;

    [RavenFact(RavenTestCategory.Corax)]
    public void TheScoresOfAnEntryUnderSeveralKeysAreAddedInAscendingOrder()
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata("Tags");
        using var keys = new JoinTermScores(searcher.Allocator);
        for (var tag = Tags - 1; tag >= 0; tag--)
            keys.Add(Encoding.UTF8.GetBytes($"tag{tag:D2}"), ScoreOf(tag));

        var map = searcher.ScoreMap(metadata, keys, termsAreIndexedTerms: true);
        var ids = new long[map.Count];
        Assert.Equal(ids.Length, map.Fill(ids));
        var scores = new float[ids.Length];
        map.Score(ids, scores, 1f);

        // the oracle: the entries of every tag from plain term queries, the scores of an entry added in ascending order
        var perEntry = new Dictionary<long, List<float>>();
        for (var tag = 0; tag < Tags; tag++)
        {
            var match = searcher.TermQuery(metadata, $"tag{tag:D2}");
            var buffer = new long[1024];
            int read;
            while ((read = match.Fill(buffer)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (perEntry.TryGetValue(buffer[i], out var list) == false)
                        perEntry[buffer[i]] = list = new List<float>();
                    list.Add(ScoreOf(tag));
                }
            }
        }

        Assert.Equal(perEntry.Count, ids.Length);
        Assert.True(perEntry.Values.Any(x => x.Count > 5));
        var different = 0;
        for (var i = 0; i < ids.Length; i++)
        {
            var sum = 0f;
            foreach (var score in perEntry[ids[i]].Order())
                sum += score;

            Assert.Equal(BitConverter.SingleToInt32Bits(sum), BitConverter.SingleToInt32Bits(scores[i]));
            var unordered = 0f;
            foreach (var score in perEntry[ids[i]])
                unordered += score;
            if (unordered != sum)
                different++;
        }

        Assert.True(different > 0, "the data must make the order of the additions matter");
    }

    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData(0.37f)]
    [InlineData(0.1f)]
    [InlineData(1f)]
    public void KeysWithTheSameScoreGiveTheSumOfThatScoreOncePerKey(float score)
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata("Tags");
        using var keys = new JoinTermScores(searcher.Allocator);
        for (var tag = Tags - 1; tag >= 0; tag--)
            keys.Add(Encoding.UTF8.GetBytes($"tag{tag:D2}"), score);

        var map = searcher.ScoreMap(metadata, keys, termsAreIndexedTerms: true);
        var ids = new long[map.Count];
        Assert.Equal(ids.Length, map.Fill(ids));
        var scores = new float[ids.Length];
        map.Score(ids, scores, 1f);

        Assert.Equal(Docs, ids.Length);
        Assert.Equal(ids.Order(), ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        using var idReader = searcher.TermsReaderFor("Id");
        var sumsThatDifferFromAProduct = 0;
        for (var i = 0; i < ids.Length; i++)
        {
            var doc = int.Parse(idReader.GetTermFor(ids[i])["docs/".Length..]);
            var sum = 0f;
            for (var k = 0; k < _tagsOfEntry[doc].Length; k++)
                sum += score;

            Assert.Equal(BitConverter.SingleToInt32Bits(sum), BitConverter.SingleToInt32Bits(scores[i]));
            if (sum != score * _tagsOfEntry[doc].Length)
                sumsThatDifferFromAProduct++;
        }

        if (score == 0.37f)
            Assert.True(sumsThatDifferFromAProduct > 0, "the data must tell a sum from a product");
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void TheRankOfAnEntryUnderSeveralKeysIsTheSmallestOne()
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata("Tags");
        using var keys = new JoinTermScores(searcher.Allocator, keepRanks: true);
        for (var tag = 0; tag < Tags; tag++)
            keys.Add(Encoding.UTF8.GetBytes($"tag{tag:D2}"), ScoreOf(tag), (float)(Tags - tag));

        var map = searcher.ScoreMap(metadata, keys, termsAreIndexedTerms: true);
        var ids = new long[map.Count];
        Assert.Equal(ids.Length, map.Fill(ids));
        var ranks = new float[ids.Length];
        map.Rank(ids, ranks);

        var checkedEntries = 0;
        using var idReader = searcher.TermsReaderFor("Id");
        for (var i = 0; i < ids.Length; i++)
        {
            var doc = int.Parse(idReader.GetTermFor(ids[i])["docs/".Length..]);
            Assert.Equal((float)(Tags - _tagsOfEntry[doc].Max()), ranks[i]);
            checkedEntries++;
        }

        Assert.Equal(Docs, checkedEntries);
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void TheRankOfAnEntryUnderSeveralKeysIsTheSmallestOneWhenTheScoresAreTheRanks()
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata("Tags");
        using var keys = new JoinTermScores(searcher.Allocator) { ScoresAreRanks = true };
        for (var tag = 0; tag < Tags; tag++)
            keys.Add(Encoding.UTF8.GetBytes($"tag{tag:D2}"), Tags - tag);

        var map = searcher.ScoreMap(metadata, keys, termsAreIndexedTerms: true);
        var ids = new long[map.Count];
        Assert.Equal(ids.Length, map.Fill(ids));
        var ranks = new float[ids.Length];
        map.Rank(ids, ranks);

        using var idReader = searcher.TermsReaderFor("Id");
        for (var i = 0; i < ids.Length; i++)
        {
            var doc = int.Parse(idReader.GetTermFor(ids[i])["docs/".Length..]);
            Assert.Equal((float)(Tags - _tagsOfEntry[doc].Max()), ranks[i]);
        }

        Assert.Equal(Docs, ids.Length);
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void ScoresAndRanksDoNotDependOnTheOrderOfTheMatchesThatAreAskedFor()
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata("Tags");
        using var keys = new JoinTermScores(searcher.Allocator, keepRanks: true);
        for (var tag = 0; tag < Tags; tag += 2)
            keys.Add(Encoding.UTF8.GetBytes($"tag{tag:D2}"), ScoreOf(tag), (float)tag);

        var map = searcher.ScoreMap(metadata, keys, termsAreIndexedTerms: true);
        var ids = new long[map.Count];
        Assert.Equal(ids.Length, map.Fill(ids));

        // the entries of the map, and some that are not in it, in ascending order, descending, and shuffled
        var asked = ids.Concat(ids.Select(x => x + 1_000_000)).Order().ToArray();
        var expectedScores = new float[asked.Length];
        var expectedRanks = new float[asked.Length];
        map.Score(asked, expectedScores, 1f);
        map.Rank(asked, expectedRanks);

        var random = new Random(11);
        foreach (var order in new[] { asked.Reverse().ToArray(), asked.OrderBy(_ => random.Next()).ToArray() })
        {
            var scores = new float[order.Length];
            var ranks = new float[order.Length];
            map.Score(order, scores, 1f);
            map.Rank(order, ranks);
            for (var i = 0; i < order.Length; i++)
            {
                var at = Array.BinarySearch(asked, order[i]);
                Assert.Equal(expectedScores[at], scores[i]);
                Assert.Equal(expectedRanks[at], ranks[i]);
            }
        }

        Assert.Contains(expectedScores, x => x > 0);
        Assert.Contains(expectedScores, x => x == 0);
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
