using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corax;
using Corax.Mappings;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;
using Corax.Querying.Matches.SortingMatches;
using Corax.Querying.Matches.SortingMatches.Meta;
using Corax.Utils;
using FastTests.Voron;
using Sparrow.Server;
using Sparrow.Threading;
using Tests.Infrastructure;
using Voron;
using Xunit;
using IndexSearcher = Corax.Querying.IndexSearcher;
using IndexWriter = Corax.Indexing.IndexWriter;

namespace FastTests.Corax
{
    public class ConstantScoreSortTests : StorageTest
    {
        private const int IdField = 0, AllField = 1, MidField = 2, FewField = 3, OneField = 4;
        private const int Entries = 3000;
        private const float Constant = 0.75f;

        public ConstantScoreSortTests(ITestOutputHelper output) : base(output)
        {
        }

        // The same entries and the same scores, the match that declares itself constant is the only difference
        private struct ConstantMatch<TInner> : IConstantScoreMatch
            where TInner : IQueryMatch
        {
            private TInner _inner;
            private readonly QueryCountConfidence? _confidence;

            public ConstantMatch(TInner inner, QueryCountConfidence? confidence = null)
            {
                _inner = inner;
                _confidence = confidence;
            }

            public long Count => _inner.Count;
            public SkipSortingResult AttemptToSkipSorting() => _inner.AttemptToSkipSorting();
            public QueryCountConfidence Confidence => _confidence ?? _inner.Confidence;
            public bool IsBoosting => true;
            public DuplicatesOccurrence DuplicatesOccurrenceStatus => _inner.DuplicatesOccurrenceStatus;
            public int Fill(Span<long> matches) => _inner.Fill(matches);
            public int AndWith(Span<long> buffer, int matches) => _inner.AndWith(buffer, matches);
            public void Score(Span<long> matches, Span<float> scores, float boostFactor) => ConstantScore<TInner>.Add(scores, matches.Length, boostFactor);
            public QueryInspectionNode Inspect() => _inner.Inspect();
        }

        private struct PlainMatch<TInner> : IQueryMatch
            where TInner : IQueryMatch
        {
            private TInner _inner;

            public PlainMatch(TInner inner)
            {
                _inner = inner;
            }

            public long Count => _inner.Count;
            public SkipSortingResult AttemptToSkipSorting() => _inner.AttemptToSkipSorting();
            public QueryCountConfidence Confidence => _inner.Confidence;
            public bool IsBoosting => true;
            public DuplicatesOccurrence DuplicatesOccurrenceStatus => _inner.DuplicatesOccurrenceStatus;
            public int Fill(Span<long> matches) => _inner.Fill(matches);
            public int AndWith(Span<long> buffer, int matches) => _inner.AndWith(buffer, matches);
            public void Score(Span<long> matches, Span<float> scores, float boostFactor) => ConstantScore<TInner>.Add(scores, matches.Length, boostFactor);
            public QueryInspectionNode Inspect() => _inner.Inspect();
        }

        private static class ConstantScore<T>
        {
            public static void Add(Span<float> scores, int count, float boostFactor)
            {
                for (int i = 0; i < count; i++)
                    scores[i] += Constant * boostFactor;
            }
        }

        private static readonly (string Field, string Term, int Count)[] Terms =
        [
            ("All", "x", Entries),
            ("Mid", "x", Entries / 75),
            ("Few", "x", 3),
            ("One", "x", 1),
            ("All", "none", 0)
        ];

        [RavenTheory(RavenTestCategory.Corax)]
        [InlineData(true)]
        [InlineData(false)]
        public void FirstEntriesOfAConstantScoreAreTheSameAsTheSortOfAllOfThem(bool ascending)
        {
            Index();
            using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));

            foreach ((string field, string term, int count) in Terms)
            {
                foreach (int take in new[] { 1, 2, 5, 20, 100, 513, Entries, Entries + 10, int.MaxValue, Constants.IndexSearcher.TakeAll })
                {
                    foreach (int bufferSize in new[] { 7, 4096 })
                    {
                        foreach (bool withScores in new[] { true, false })
                        {
                            var sorted = Run(searcher, new PlainMatch<IQueryMatch>(searcher.TermQuery(searcher.FieldMetadataBuilder(field), term)), ascending, take, bufferSize, withScores);
                            var served = Run(searcher, new ConstantMatch<IQueryMatch>(searcher.TermQuery(searcher.FieldMetadataBuilder(field), term)), ascending, take, bufferSize, withScores);

                            string what = $"{field}:{term} ({count}), ascending {ascending}, take {take}, buffer {bufferSize}, scores {withScores}";
                            Assert.True(sorted.Total == count, $"{what}: the sort found {sorted.Total}");
                            Assert.True(sorted.Total == served.Total, $"{what}: total {served.Total} instead of {sorted.Total}");
                            Assert.True(sorted.Ids.SequenceEqual(served.Ids), $"{what}: ids differ");
                            Assert.True(sorted.Scores.SequenceEqual(served.Scores), $"{what}: scores differ");
                            if (take != Constants.IndexSearcher.TakeAll && take != int.MaxValue)
                                Assert.Equal(Math.Min(take, count), served.Ids.Count);
                        }
                    }
                }
            }
        }

        [RavenFact(RavenTestCategory.Corax)]
        public void ConstantScoreIsNotUsedWhenTheCountIsNotExact()
        {
            Index();
            using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
            var mid = searcher.FieldMetadataBuilder("Mid");

            var sorted = Run(searcher, new PlainMatch<IQueryMatch>(searcher.TermQuery(mid, "x")), true, 10, 4096, true);
            var served = Run(searcher, new ConstantMatch<IQueryMatch>(searcher.TermQuery(mid, "x"), QueryCountConfidence.Normal), true, 10, 4096, true);

            Assert.Equal(sorted.Total, served.Total);
            Assert.Equal(sorted.Ids, served.Ids);
            Assert.Equal(sorted.Scores, served.Scores);
        }

        [RavenTheory(RavenTestCategory.Corax)]
        [InlineData(true, true)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(false, false)]
        public void SortByScoreOfNoEntriesCountsTheMatches(bool ascending, bool constant)
        {
            Index();
            using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
            var mid = searcher.FieldMetadataBuilder("Mid");

            foreach (bool withScores in new[] { true, false })
            {
                var result = constant
                    ? Run(searcher, new ConstantMatch<IQueryMatch>(searcher.TermQuery(mid, "x")), ascending, 0, 4096, withScores)
                    : Run(searcher, new PlainMatch<IQueryMatch>(searcher.TermQuery(mid, "x")), ascending, 0, 4096, withScores);

                Assert.Empty(result.Ids);
                Assert.Equal(Entries / 75, result.Total);
            }
        }

        [RavenFact(RavenTestCategory.Corax)]
        public void PageScoresAreTheScoresOfTheMatch()
        {
            Index();
            using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
            var all = searcher.FieldMetadataBuilder("All");

            var served = Run(searcher, new ConstantMatch<IQueryMatch>(searcher.TermQuery(all, "x")), false, 20, 4096, true);

            Assert.Equal(20, served.Scores.Count);
            Assert.All(served.Scores, score => Assert.Equal(Bm25Relevance.InitialScoreValue + Constant, score));
        }

        private sealed record Result(long Total, List<long> Ids, List<float> Scores);

        private static Result Run<TInner>(IndexSearcher searcher, TInner inner, bool ascending, int take, int bufferSize, bool withScores)
            where TInner : IQueryMatch
        {
            SortingMatch match = searcher.OrderBy(inner, new OrderMetadata(true, MatchCompareFieldType.Score, ascending), NullsSortMode.NullsSmallest, take);
            var transfer = new SortingDataTransfer { ScoresBuffer = withScores ? new float[bufferSize] : null };
            match.SetScoreAndDistanceBuffer(transfer);

            var ids = new List<long>();
            var scores = new List<float>();
            var buffer = new long[bufferSize];
            int read;
            while ((read = match.Fill(buffer)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    ids.Add(buffer[i]);
                    if (withScores)
                        scores.Add(transfer.ScoresBuffer[i]);
                }
            }

            return new Result(match.TotalResults, ids, scores);
        }

        private void Index()
        {
            using var bsc = new ByteStringContext(SharedMultipleUseFlag.None);
            using var knownFields = CreateKnownFields(bsc);
            using var indexWriter = new IndexWriter(Env, knownFields, SupportedFeatures.All);

            for (int i = 0; i < Entries; i++)
            {
                using var builder = indexWriter.Index(Encoding.UTF8.GetBytes("entries/" + i));
                builder.Write(IdField, null, Encoding.UTF8.GetBytes("entries/" + i));
                builder.Write(AllField, null, "x"u8);
                if (i % 75 == 0)
                    builder.Write(MidField, null, "x"u8);
                if (i == 100 || i == 1500 || i == 2900)
                    builder.Write(FewField, null, "x"u8);
                if (i == 1234)
                    builder.Write(OneField, null, "x"u8);
                builder.EndWriting();
            }

            indexWriter.Commit();
        }

        private static IndexFieldsMapping CreateKnownFields(ByteStringContext bsc)
        {
            Slice.From(bsc, "Id", ByteStringType.Immutable, out Slice idSlice);
            Slice.From(bsc, "All", ByteStringType.Immutable, out Slice allSlice);
            Slice.From(bsc, "Mid", ByteStringType.Immutable, out Slice midSlice);
            Slice.From(bsc, "Few", ByteStringType.Immutable, out Slice fewSlice);
            Slice.From(bsc, "One", ByteStringType.Immutable, out Slice oneSlice);

            using var builder = IndexFieldsMappingBuilder.CreateForWriter(false)
                .AddBinding(IdField, idSlice)
                .AddBinding(AllField, allSlice)
                .AddBinding(MidField, midSlice)
                .AddBinding(FewField, fewSlice)
                .AddBinding(OneField, oneSlice);

            return builder.Build();
        }
    }
}
