using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corax;
using Corax.Mappings;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;
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
    public class ScoreAddMemberMatchTests : StorageTest
    {
        private const int IdField = 0, AllField = 1, MidField = 2, FewField = 3, OneField = 4;
        private const int Entries = 3000;
        private const float Add = 0.5f;

        public ScoreAddMemberMatchTests(ITestOutputHelper output) : base(output)
        {
        }

        // Adds the same score to every entry it is asked to score
        private struct ConstantMembers<TInner> : IQueryMatch
            where TInner : IQueryMatch
        {
            private TInner _inner;

            public ConstantMembers(TInner inner)
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

            public void Score(Span<long> matches, Span<float> scores, float boostFactor)
            {
                for (int i = 0; i < matches.Length; i++)
                    scores[i] += Add * boostFactor;
            }

            public QueryInspectionNode Inspect() => _inner.Inspect();
        }

        private static readonly (string Field, string Term)[] MemberTerms =
        [
            ("All", "x"),
            ("Mid", "x"),
            ("Few", "x"),
            ("One", "x"),
            ("All", "none")
        ];

        private static List<long> Members(IndexSearcher searcher, string field, string term)
        {
            var match = searcher.TermQuery(searcher.FieldMetadataBuilder(field), term);
            var ids = new List<long>();
            var buffer = new long[512];
            int read;
            while ((read = match.Fill(buffer)) > 0)
                ids.AddRange(buffer.Take(read));
            return ids;
        }

        [RavenFact(RavenTestCategory.Corax)]
        public void OnlyTheMembersGetTheScoreWhateverTheOrderOfTheMatches()
        {
            Index();
            using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
            List<long> every = Members(searcher, "All", "x");
            Assert.Equal(Entries, every.Count);

            foreach ((string field, string term) in MemberTerms)
            {
                var memberSet = Members(searcher, field, term).ToHashSet();
                var shapes = new Dictionary<string, long[]>
                {
                    ["ascending"] = every.ToArray(),
                    ["descending"] = Enumerable.Reverse(every).ToArray(),
                    ["repeated"] = every.SelectMany(x => new[] { x, x }).ToArray(),
                    ["shuffled"] = every.OrderBy(x => (x * 7919) % 104729).ToArray(),
                    ["ascendingWithGaps"] = every.Where(x => x % 3 != 0).ToArray(),
                    ["first"] = every.Take(1).ToArray(),
                    ["none"] = []
                };

                foreach ((string shape, long[] ids) in shapes)
                {
                    foreach (int batch in new[] { 1, 7, 513, Entries * 2 })
                    {
                        var match = new ScoreAddMemberMatch<IQueryMatch, IQueryMatch>(searcher.Allocator, searcher.AllEntries(),
                            new ConstantMembers<IQueryMatch>(searcher.TermQuery(searcher.FieldMetadataBuilder(field), term)));

                        for (int from = 0; from < ids.Length; from += batch)
                        {
                            long[] part = ids.Skip(from).Take(batch).ToArray();
                            var scores = new float[part.Length];
                            Array.Fill(scores, 1f);
                            match.Score(part, scores, 2f);

                            for (int i = 0; i < part.Length; i++)
                            {
                                float expected = memberSet.Contains(part[i]) ? 1f + Add * 2f : 1f;
                                Assert.True(expected == scores[i], $"{field}:{term}, {shape}, batch {batch}, entry {part[i]}: {scores[i]} instead of {expected}");
                            }
                        }
                    }
                }
            }
        }

        [RavenFact(RavenTestCategory.Corax)]
        public void MatchesAndCountsAreThoseOfTheInnerMatch()
        {
            Index();
            using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
            var match = new ScoreAddMemberMatch<IQueryMatch, IQueryMatch>(searcher.Allocator, searcher.TermQuery(searcher.FieldMetadataBuilder("Mid"), "x"),
                new ConstantMembers<IQueryMatch>(searcher.TermQuery(searcher.FieldMetadataBuilder("Few"), "x")));

            List<long> expected = Members(searcher, "Mid", "x");
            Assert.Equal(expected.Count, match.Count);
            Assert.Equal(QueryCountConfidence.High, match.Confidence);

            var buffer = new long[512];
            var read = new List<long>();
            int count;
            while ((count = match.Fill(buffer)) > 0)
                read.AddRange(buffer.Take(count));

            Assert.Equal(expected, read);
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
