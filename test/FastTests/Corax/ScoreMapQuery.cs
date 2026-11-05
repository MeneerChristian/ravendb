using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corax;
using Corax.Mappings;
using Corax.Utils;
using Corax.Querying.Matches.Meta;
using Corax.Querying.Matches.SortingMatches.Meta;
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
    public class ScoreMapQuery : StorageTest
    {
        private const int IdField = 0, OwnerField = 1, NameField = 2;

        // game id -> (owner, name)
        private readonly List<(string Id, string Owner, string Name)> _games = new()
        {
            ("games/1", "users/1", "zelda"),
            ("games/2", "users/1", "mario"),
            ("games/3", "users/2", "zelda"),
            ("games/4", "users/2", "mario"),
            ("games/5", "users/3", "zelda"),
        };

        public ScoreMapQuery(ITestOutputHelper output) : base(output) { }

        [RavenFact(RavenTestCategory.Corax)]
        public void ScoreMapAddsMappedScoreToMatchedEntries()
        {
            IndexGames();
            using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));

            var owner = searcher.FieldMetadataBuilder("Owner");
            var map = searcher.ScoreMap(new List<(IQueryMatch, float)>
            {
                (searcher.TermQuery(owner, "users/1"), 5f),
                (searcher.TermQuery(owner, "users/2"), 2f),
            });

            Assert.Equal(4, map.Count);

            var ids = new long[16];
            var read = map.Fill(ids);
            Array.Resize(ref ids, read);
            Assert.Equal(4, read);

            var scores = new float[read];
                        map.Score(ids, scores, 1f);

            var byGame = Enumerable.Range(0, read).ToDictionary(i => TermFor(searcher, ids[i]), i => scores[i]);
            Assert.Equal(5f, byGame["games/1"]);
            Assert.Equal(5f, byGame["games/2"]);
            Assert.Equal(2f, byGame["games/3"]);
            Assert.Equal(2f, byGame["games/4"]);
            Assert.False(byGame.ContainsKey("games/5"));
        }

        [RavenFact(RavenTestCategory.Corax)]
        public void ScoreMapSumsScoresWhenEntryIsInSeveralSources()
        {
            IndexGames();
            using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));

            var owner = searcher.FieldMetadataBuilder("Owner");
            var name = searcher.FieldMetadataBuilder("Name");
            var map = searcher.ScoreMap(new List<(IQueryMatch, float)>
            {
                (searcher.TermQuery(owner, "users/1"), 3f),
                (searcher.TermQuery(name, "zelda"), 1.5f),
            });

            var ids = new long[16];
            var read = map.Fill(ids);
            Array.Resize(ref ids, read);
            var scores = new float[read];
                        map.Score(ids, scores, 1f);

            var byGame = Enumerable.Range(0, read).ToDictionary(i => TermFor(searcher, ids[i]), i => scores[i]);
            Assert.Equal(4.5f, byGame["games/1"]); // owner + name
            Assert.Equal(3f, byGame["games/2"]);
            Assert.Equal(1.5f, byGame["games/3"]);
            Assert.Equal(1.5f, byGame["games/5"]);
        }

        [RavenFact(RavenTestCategory.Corax)]
        public void ScoreMapAndWithKeepsOnlyMappedEntries()
        {
            IndexGames();
            using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));

            var owner = searcher.FieldMetadataBuilder("Owner");
            var name = searcher.FieldMetadataBuilder("Name");
            var map = searcher.ScoreMap(new List<(IQueryMatch, float)> { (searcher.TermQuery(owner, "users/2"), 4f) });

            var zelda = searcher.TermQuery(name, "zelda");
            var ids = new long[16];
            var read = zelda.Fill(ids);
            read = map.AndWith(ids, read);

            Assert.Equal(1, read);
            Assert.Equal("games/3", TermFor(searcher, ids[0]));
        }

        [RavenFact(RavenTestCategory.Corax)]
        public void ScoreGivesTheScoreOfEveryMatchWhateverTheirOrder()
        {
            IndexGames();
            using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));

            var owner = searcher.FieldMetadataBuilder("Owner");
            var map = searcher.ScoreMap(new List<(IQueryMatch, float)>
            {
                (searcher.TermQuery(owner, "users/1"), 5f),
                (searcher.TermQuery(owner, "users/2"), 2f),
            });

            var sorted = new long[16];
            var read = map.Fill(sorted);
            Array.Resize(ref sorted, read);
            var expected = new float[read];
            map.Score(sorted, expected, 1f);
            var byId = sorted.Zip(expected).ToDictionary(x => x.First, x => x.Second);

            foreach (var matches in new[] { sorted.Reverse().ToArray(), sorted.Concat(sorted).ToArray(), sorted.Concat(sorted.Reverse()).ToArray() })
            {
                var scores = new float[matches.Length];
                map.Score(matches, scores, 1f);
                for (var i = 0; i < matches.Length; i++)
                    Assert.Equal(byId[matches[i]], scores[i]);
            }
        }

        [RavenFact(RavenTestCategory.Corax)]
        public void GameSearchScoreAndMapScoreAreSummedWhenOrderingByScore()
        {
            IndexGames();
            using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));

            var owner = searcher.FieldMetadataBuilder("Owner");
            var name = searcher.FieldMetadataBuilder("Name", hasBoost: true);

            // zelda is owned by users/1 (games/1), users/2 (games/3) and users/3 (games/5); users/3 has the best user score
            var map = searcher.ScoreMap(new List<(IQueryMatch, float)>
            {
                (searcher.TermQuery(owner, "users/1"), 1f),
                (searcher.TermQuery(owner, "users/2"), 10f),
                (searcher.TermQuery(owner, "users/3"), 100f),
            });

            IQueryMatch match = searcher.And(searcher.TermQuery(name, "zelda"), map);
            match = searcher.OrderBy(match, new OrderMetadata(true, MatchCompareFieldType.Score), defaultNullsSortMode: NullsSortMode.NullsSmallest);

            var ids = new long[16];
            var read = match.Fill(ids);

            Assert.Equal(new[] { "games/5", "games/3", "games/1" }, Enumerable.Range(0, read).Select(i => TermFor(searcher, ids[i])).ToArray());
        }

        private static string TermFor(IndexSearcher searcher, long id) =>
            searcher.TermsReaderFor(searcher.GetFirstIndexedFiledName()).GetTermFor(id);

        private void IndexGames()
        {
            using var bsc = new ByteStringContext(SharedMultipleUseFlag.None);
            using var knownFields = CreateKnownFields(bsc);
            using var indexWriter = new IndexWriter(Env, knownFields, SupportedFeatures.All);

            foreach (var (id, owner, name) in _games)
            {
                using var builder = indexWriter.Index(Encoding.UTF8.GetBytes(id));
                builder.Write(IdField, null, Encoding.UTF8.GetBytes(id));
                builder.Write(OwnerField, null, Encoding.UTF8.GetBytes(owner));
                builder.Write(NameField, null, Encoding.UTF8.GetBytes(name));
                builder.EndWriting();
            }

            indexWriter.Commit();
        }

        private IndexFieldsMapping CreateKnownFields(ByteStringContext bsc)
        {
            Slice.From(bsc, "Id", ByteStringType.Immutable, out Slice idSlice);
            Slice.From(bsc, "Owner", ByteStringType.Immutable, out Slice ownerSlice);
            Slice.From(bsc, "Name", ByteStringType.Immutable, out Slice nameSlice);

            using var builder = IndexFieldsMappingBuilder.CreateForWriter(false)
                .AddBinding(IdField, idSlice)
                .AddBinding(OwnerField, ownerSlice)
                .AddBinding(NameField, nameSlice);

            return builder.Build();
        }
    }
}
