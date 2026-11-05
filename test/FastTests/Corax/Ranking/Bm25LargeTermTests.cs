using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Corax;
using Corax.Mappings;
using Corax.Utils;
using FastTests.Voron;
using Raven.Server.Documents.Indexes.Persistence.Lucene;
using Raven.Server.Documents.Indexes.Persistence.Lucene.Analyzers;
using Sparrow;
using Sparrow.Utils;
using Tests.Infrastructure;
using Xunit;
using IndexSearcher = Corax.Querying.IndexSearcher;
using IndexWriter = Corax.Indexing.IndexWriter;

namespace FastTests.Corax.Ranking;

public class Bm25LargeTermTests : StorageTest
{
    private const int IdIndex = 0, ContentIndex = 1;
    private readonly IndexFieldsMapping _mapping;

    public Bm25LargeTermTests(ITestOutputHelper output) : base(output)
    {
        var analyzer = LuceneAnalyzerAdapter.Create(new RavenStandardAnalyzer(Lucene.Net.Util.Version.LUCENE_30), forQuerying: false);
        _mapping = IndexFieldsMappingBuilder.CreateForWriter(false)
            .AddBinding(IdIndex, "Id")
            .AddBinding(ContentIndex, "Content", analyzer)
            .Build();
    }

    // 20,000 entries: more than the relevance buffer holds, fewer than the old limit of it
    private const int LargeTermEntries = 20_000;

    [RavenFact(RavenTestCategory.Corax)]
    public void ScoresOfATermLargerThanTheBufferDoNotDependOnTheOrderOrTheRepetitionOfTheMatches()
    {
        IndexEntries(LargeTermEntries, 50);

        using var searcher = new IndexSearcher(Env, _mapping);
        var sorted = Matching(searcher, "maciej");
        Assert.Equal(LargeTermEntries, sorted.Length);

        var expected = new Dictionary<long, float>();
        foreach (var id in sorted)
        {
            var score = new float[1];
            Scored(searcher, [id], score);
            Assert.True(score[0] > 0);
            expected[id] = score[0];
        }

        // the more often the word is in the entry the higher the score
        var byRepeats = sorted.GroupBy(x => expected[x]).Select(x => x.Key).OrderBy(x => x).ToArray();
        Assert.Equal(4, byRepeats.Length);

        var rng = new Random(7);
        var shapes = new Dictionary<string, long[]>
        {
            ["reversed"] = sorted.Reverse().ToArray(),
            ["shuffled"] = sorted.OrderBy(_ => rng.Next()).ToArray(),
            ["duplicated"] = sorted.Take(5000).SelectMany(x => new[] { x, x }).ToArray(),
            ["subset"] = sorted.Where((_, i) => i % 7 == 0).Reverse().ToArray(),
        };

        foreach (var (name, matches) in shapes)
        {
            var scores = new float[matches.Length];
            Scored(searcher, matches, scores);
            for (var i = 0; i < matches.Length; i++)
                Assert.True(expected[matches[i]] == scores[i], $"{name}: match {i} ({matches[i]}) scored {scores[i]}, expected {expected[matches[i]]}");
        }
    }

    // the buffers of a relevance that stays in the caches of the allocator are what made the memory of concurrent scored queries pile up
    [RavenFact(RavenTestCategory.Corax)]
    public void TheRelevanceOfALargeTermDoesNotHoldABufferOfItsSize()
    {
        IndexEntries(LargeTermEntries * 2, 50);

        // a new thread: its allocator caches are empty, so every segment the scoring takes is counted
        long held = -1;
        var thread = new Thread(() =>
        {
            using var searcher = new IndexSearcher(Env, _mapping);
            var before = NativeMemory.CurrentThreadStats.TotalAllocated;
            var term = searcher.TermQuery(_mapping.GetByFieldId(ContentIndex).Metadata.ChangeScoringMode(true), "maciej");
            held = NativeMemory.CurrentThreadStats.TotalAllocated - before;
            term.Fill(new long[256]);
            term.Score(new long[] { 0 }, new float[1], 1f);
        });
        thread.Start();
        thread.Join();

        // 40,000 entries took 400 KB (a 512 KB segment) when the whole term was stored
        Assert.InRange(held, 0, 300 * 1024);
    }

    // the terms between the old and the new limit of the buffer are scored from the posting list now: the scores have to be the same bit for bit
    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData(20_000)]
    [InlineData(50_000)]
    [InlineData(100_000)]
    public void ScoresFromThePostingListAreTheScoresOfTheBuffer(int termEntries)
    {
        IndexEntries(termEntries, 1_000);

        var rng = new Random(11);
        long[] sample = null;
        var results = new Dictionary<int, (long[] Ids, float[] Scores)[]>();
        foreach (var limit in new[] { 1024 * 1024, 128 * 1024 })
        {
            using var searcher = new IndexSearcher(Env, _mapping);
            searcher.SetTestingConfiguration(new CoraxTestingConfiguration { Bm25MaxSizeOfStorage = limit });
            var all = Matching(searcher, "maciej");
            Assert.Equal(termEntries, all.Length);
            sample ??= all.Where((_, i) => i % 5 == 0).OrderBy(_ => rng.Next()).ToArray();
            var strangers = Matching(searcher, "jan");

            var shapes = new[] { all, sample, all.Concat(strangers).OrderBy(x => x).ToArray(), all.Take(10).ToArray() };
            results[limit] = shapes.Select(ids => (ids, ScoredFully(searcher, ids))).ToArray();
        }

        var buffer = results[1024 * 1024];
        var posting = results[128 * 1024];
        for (var shape = 0; shape < buffer.Length; shape++)
        {
            Assert.Equal(buffer[shape].Ids.Length, posting[shape].Ids.Length);
            for (var i = 0; i < buffer[shape].Ids.Length; i++)
                Assert.True(BitConverter.SingleToInt32Bits(buffer[shape].Scores[i]) == BitConverter.SingleToInt32Bits(posting[shape].Scores[i]),
                    $"shape {shape}, match {i}: the buffer scored {buffer[shape].Scores[i]}, the posting list {posting[shape].Scores[i]}");
        }
    }

    // the term is read to its end first: the scores of the buffer are those of the entries it has seen
    private float[] ScoredFully(IndexSearcher searcher, long[] matches)
    {
        var term = searcher.TermQuery(_mapping.GetByFieldId(ContentIndex).Metadata.ChangeScoringMode(true), "maciej");
        var buffer = new long[4096];
        while (term.Fill(buffer) > 0)
        {
        }

        var scores = new float[matches.Length];
        term.Score(matches, scores, 1f);
        return scores;
    }

    private void Scored(IndexSearcher searcher, long[] matches, float[] scores)
    {
        var term = searcher.TermQuery(_mapping.GetByFieldId(ContentIndex).Metadata.ChangeScoringMode(true), "maciej");
        term.Fill(new long[256]);
        term.Score(matches, scores, 1f);
    }

    private long[] Matching(IndexSearcher searcher, string text)
    {
        var term = searcher.TermQuery(_mapping.GetByFieldId(ContentIndex).Metadata.ChangeScoringMode(true), text);
        var all = new List<long>();
        var buffer = new long[4096];
        int read;
        while ((read = term.Fill(buffer)) > 0)
            all.AddRange(buffer.AsSpan(0, read).ToArray());

        var result = all.ToArray();
        Array.Sort(result);
        return result;
    }

    private void IndexEntries(int termEntries, int extraEntries)
    {
        using var writer = new IndexWriter(Env, _mapping, SupportedFeatures.All);
        for (var i = 0; i < termEntries + extraEntries; i++)
        {
            var content = i < termEntries ? string.Join(' ', Enumerable.Repeat("maciej", 1 + i % 4)) : "jan";
            using var builder = writer.Index(i.ToString(CultureInfo.InvariantCulture));
            builder.Write(IdIndex, Encodings.Utf8.GetBytes(i.ToString(CultureInfo.InvariantCulture)), i, i);
            builder.Write(ContentIndex, Encodings.Utf8.GetBytes(content));
            builder.EndWriting();
        }

        writer.Commit();
    }

    public override async ValueTask DisposeAsync()
    {
        _mapping.Dispose();
        await base.DisposeAsync();
    }
}
