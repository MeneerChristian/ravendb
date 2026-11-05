using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Corax;
using Corax.Mappings;
using FastTests.Voron;
using Raven.Server.Documents.Indexes.Persistence.Lucene;
using Raven.Server.Documents.Indexes.Persistence.Lucene.Analyzers;
using Sparrow;
using Tests.Infrastructure;
using Xunit;
using IndexSearcher = Corax.Querying.IndexSearcher;
using IndexWriter = Corax.Indexing.IndexWriter;

namespace FastTests.Corax.Ranking;

public class Bm25UnsortedMatchesTests : StorageTest
{
    private const int IdIndex = 0, ContentIndex = 1;
    private readonly IndexFieldsMapping _mapping;

    public Bm25UnsortedMatchesTests(ITestOutputHelper output) : base(output)
    {
        var analyzer = LuceneAnalyzerAdapter.Create(new RavenStandardAnalyzer(Lucene.Net.Util.Version.LUCENE_30), forQuerying: false);
        _mapping = IndexFieldsMappingBuilder.CreateForWriter(false)
            .AddBinding(IdIndex, "Id")
            .AddBinding(ContentIndex, "Content", analyzer)
            .Build();
    }

    // more entries than the term holds, so that both shapes of the scoring are used
    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData(12, 3)]
    [InlineData(12, 40)]
    [InlineData(200, 40)]
    public void ScoresDoNotDependOnTheOrderOrTheRepetitionOfTheMatches(int termEntries, int extraEntries)
    {
        var contents = new List<string>();
        for (var i = 0; i < termEntries; i++)
            contents.Add(string.Join(' ', Enumerable.Repeat("maciej", 1 + i % 4)));
        for (var i = 0; i < extraEntries; i++)
            contents.Add("jan");
        IndexEntries(contents);

        using var searcher = new IndexSearcher(Env, _mapping);
        var sorted = Matching(searcher, "maciej");
        Assert.Equal(termEntries, sorted.Length);

        // the entries of the other term: they match nothing of "maciej" and sit between its entries
        var stranger = Matching(searcher, "jan");

        var expected = new Dictionary<long, float>();
        foreach (var id in sorted)
        {
            var score = new float[1];
            Scored(searcher, [id], score);
            Assert.True(score[0] > 0);
            expected[id] = score[0];
        }

        var rng = new Random(7);
        var shapes = new Dictionary<string, long[]>
        {
            ["reversed"] = sorted.Reverse().ToArray(),
            ["shuffled"] = sorted.OrderBy(_ => rng.Next()).ToArray(),
            ["duplicated"] = sorted.SelectMany(x => new[] { x, x }).ToArray(),
            ["with strangers"] = sorted.Concat(stranger).OrderBy(_ => rng.Next()).ToArray(),
            ["subset"] = sorted.Where((_, i) => i % 3 == 0).Reverse().ToArray(),
        };

        foreach (var (name, matches) in shapes)
        {
            var scores = new float[matches.Length];
            Scored(searcher, matches, scores);
            for (var i = 0; i < matches.Length; i++)
                Assert.True(expected.GetValueOrDefault(matches[i]) == scores[i], $"{name}: match {i} ({matches[i]}) scored {scores[i]}, expected {expected.GetValueOrDefault(matches[i])}");
        }
    }

    // the relevance of a term lives until its matches are drained, so every scoring starts from a term that has been read once
    private void Scored(IndexSearcher searcher, long[] matches, float[] scores)
    {
        var term = searcher.TermQuery(_mapping.GetByFieldId(ContentIndex).Metadata.ChangeScoringMode(true), "maciej");
        term.Fill(new long[256]);
        term.Score(matches, scores, 1f);
    }

    private long[] Matching(IndexSearcher searcher, string text)
    {
        var term = searcher.TermQuery(_mapping.GetByFieldId(ContentIndex).Metadata.ChangeScoringMode(true), text);
        var ids = new long[256];
        var read = term.Fill(ids);
        var result = ids.AsSpan(0, read).ToArray();
        Array.Sort(result);
        return result;
    }

    private void IndexEntries(List<string> contents)
    {
        using var writer = new IndexWriter(Env, _mapping, SupportedFeatures.All);
        for (var i = 0; i < contents.Count; i++)
        {
            using var builder = writer.Index(i.ToString(CultureInfo.InvariantCulture));
            builder.Write(IdIndex, Encodings.Utf8.GetBytes(i.ToString(CultureInfo.InvariantCulture)), i, i);
            builder.Write(ContentIndex, Encodings.Utf8.GetBytes(contents[i]));
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
