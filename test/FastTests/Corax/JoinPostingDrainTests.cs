using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Corax;
using Corax.Analyzers;
using Corax.Indexing;
using Corax.Mappings;
using Corax.Pipeline;
using Corax.Querying;
using Corax.Utils;
using FastTests.Voron;
using Sparrow.Server;
using Sparrow.Threading;
using Sparrow.Utils;
using Tests.Infrastructure;
using Voron;
using Voron.Data.Containers;
using Xunit;
using IndexSearcher = Corax.Querying.IndexSearcher;
using IndexWriter = Corax.Indexing.IndexWriter;

namespace FastTests.Corax;

// The ScoreMap of a JOIN reads the postings of a key directly
public class JoinPostingDrainTests : StorageTest
{
    private const int Docs = 60_000;
    private const int IdField = 0, OwnerField = 1, LowerField = 2, TagsField = 3, BodyField = 4;
    private const int Words = 30, RareWords = 400;

    private static readonly JoinKeyLookupStrategy[] Strategies = [JoinKeyLookupStrategy.Cursor, JoinKeyLookupStrategy.Sweep, JoinKeyLookupStrategy.Auto];
    private static readonly string[] Fields = ["Owner", "Lower", "Tags", "Body"];
    private static readonly double[] Densities = [0.001, 0.01, 0.1, 0.3, 1.0];

    private readonly ByteStringContext _analyzers = new(SharedMultipleUseFlag.None);
    private readonly List<string> _owners = new();

    public JoinPostingDrainTests(ITestOutputHelper output) : base(output)
    {
        Index();
    }

    public override async ValueTask DisposeAsync()
    {
        _analyzers.Dispose();
        await base.DisposeAsync();
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void TheDataHasEveryKindOfPostings()
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var kinds = new Dictionary<string, HashSet<string>>();
        using (var tx = Env.ReadTransaction())
        {
            foreach (var field in Fields)
            {
                var set = kinds[field] = new HashSet<string>();
                var it = tx.ReadTree(Constants.IndexWriter.FieldsSlice).CompactTreeFor(field).Iterate();
                it.Reset();
                while (it.MoveNext(out _, out var containerId, out _))
                {
                    if ((containerId & (long)TermIdMask.PostingList) != 0)
                        set.Add("large");
                    else if ((containerId & (long)TermIdMask.SmallPostingList) != 0)
                        set.Add("small");
                    else
                        set.Add("single");
                }
            }
        }

        foreach (var field in new[] { "Owner", "Tags", "Body" })
            Assert.True(kinds[field].SetEquals(["large", "single", "small"]), $"{field}: {string.Join(",", kinds[field].Order())}");

        // a large list of a Search field holds entries with frequency bits, and so does a small one
        var tree = searcher.GetFieldMetadata("Body");
        var largeFrequencies = false;
        var smallFrequencies = false;
        using (var tx = Env.ReadTransaction())
        {
            var terms = tx.ReadTree(Constants.IndexWriter.FieldsSlice).CompactTreeFor("Body");
            Assert.True(terms.TryGetValue("word1"u8, out var large));
            var list = searcher.GetPostingList(large);
            Assert.True(list.State.NumberOfEntries > 2_000, "more than a page of ids");
            var it = list.Iterate();
            var buffer = new long[4096];
            while (it.Fill(buffer, out var read) && read > 0)
                largeFrequencies |= buffer.AsSpan(0, read).ToArray().Any(v => ((v >> 2) & 0xFF) != 0);

            for (var i = 0; i < RareWords && smallFrequencies == false; i++)
            {
                if (terms.TryGetValue(Encoding.UTF8.GetBytes($"rare{i}"), out var rare) && (rare & (long)TermIdMask.SmallPostingList) != 0)
                {
                    Container.Get(tx.LowLevelTransaction, EntryIdEncodings.GetContainerId(rare), out var item);
                    smallFrequencies = item.Length > 0;
                }
            }
        }

        Assert.True(largeFrequencies, "the large lists have entries with frequency bits");
        Assert.True(smallFrequencies);
        Assert.NotNull(tree);
    }

    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData("Owner")]
    [InlineData("Lower")]
    [InlineData("Tags")]
    [InlineData("Body")]
    public void ScoreMapOfRawKeysIsTheSameAsTheOldDrain(string field)
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata(field);
        var universe = RawKeys(field);
        var random = new Random(field.GetHashCode());

        foreach (var density in Densities)
        {
            using var keys = Subset(searcher, universe, density, random);
            CompareScoreMaps(searcher, metadata, keys, termsAreIndexedTerms: false, $"{field} raw keys at {density:P1}");
        }
    }

    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData("Owner")]
    [InlineData("Lower")]
    [InlineData("Tags")]
    [InlineData("Body")]
    public void ScoreMapOfIndexedTermsIsTheSameAsTheOldDrain(string field)
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata(field);
        var universe = TermsOfTree(field).Concat(Enumerable.Range(0, 300).Select(i => Encoding.UTF8.GetBytes($"missing/{i}"))).ToList();
        var random = new Random(field.GetHashCode() + 1);

        foreach (var density in Densities)
        {
            using var keys = Subset(searcher, universe, density, random);
            CompareScoreMaps(searcher, metadata, keys, termsAreIndexedTerms: true, $"{field} indexed terms at {density:P1}");
        }
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void LargeListsUnderOneKeyAreDrainedWhole()
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata("Owner");
        using var keys = new JoinTermScores(searcher.Allocator);
        keys.Add("users/hot0"u8.ToArray(), 1.5f);
        keys.Add("users/hot1"u8.ToArray(), 2.25f);

        CompareScoreMaps(searcher, metadata, keys, termsAreIndexedTerms: false, "hot owners");
        var (ids, _) = Run(searcher, metadata, keys, false);
        var expected = _hotCounts[0] + _hotCounts[1];
        Assert.True(expected > 4096, "more entries than one buffer");
        Assert.Equal(expected, ids.Length);
    }

    // the collector is sized for the entries under the keys: it holds no dead block of a smaller size
    [RavenFact(RavenTestCategory.Corax)]
    public void ScoreMapOfLargeListsDoesNotHoldTheBlocksOfADoublingCollector()
    {
        var entries = _hotCounts[0] + _hotCounts[1];
        long held = -1;
        var thread = new Thread(() =>
        {
            using var searcher = new IndexSearcher(Env, CreateKnownFields(new ByteStringContext(SharedMultipleUseFlag.None)));
            var metadata = searcher.GetFieldMetadata("Owner");
            using var keys = new JoinTermScores(searcher.Allocator);
            keys.Add("users/hot0"u8.ToArray(), 1.5f);
            keys.Add("users/hot1"u8.ToArray(), 2.25f);

            var before = NativeMemory.CurrentThreadStats.TotalAllocated;
            _ = searcher.ScoreMap(metadata, keys);
            held = NativeMemory.CurrentThreadStats.TotalAllocated - before;
        });
        thread.Start();
        thread.Join();

        // an id and a score per entry, each list in a segment that is a power of two
        Assert.InRange(held, 1, 2 * 12L * entries + 64 * 1024);
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void MissingKeysAndEmptyKeyListsGiveAnEmptyMap()
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata("Owner");
        using var none = new JoinTermScores(searcher.Allocator);
        using var missing = new JoinTermScores(searcher.Allocator);
        for (var i = 0; i < 500; i++)
            missing.Add(Encoding.UTF8.GetBytes($"nobody/{i}"), 1f);

        CompareScoreMaps(searcher, metadata, none, false, "no keys");
        CompareScoreMaps(searcher, metadata, missing, false, "missing keys");
        Assert.Empty(Run(searcher, metadata, missing, false).Ids);
        Assert.Empty(Run(searcher, metadata, none, false).Ids);
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void ConcurrentScoreMapsAreTheSameAsTheOldDrain()
    {
        var universes = Fields.ToDictionary(x => x, RawKeys);
        var failures = new List<string>();
        var threads = Enumerable.Range(0, 4).Select(t => new Thread(() =>
        {
            try
            {
                using var searcher = new IndexSearcher(Env, CreateKnownFields(new ByteStringContext(SharedMultipleUseFlag.None)));
                var random = new Random(t);
                for (var round = 0; round < 12; round++)
                {
                    var field = Fields[(t + round) % Fields.Length];
                    var metadata = searcher.GetFieldMetadata(field);
                    using var keys = Subset(searcher, universes[field], round % 3 == 0 ? 0.5 : 0.05, random);
                    var expected = Oracle(searcher, metadata, keys, false);
                    JoinKeyLookupSettings.Forced = Strategies[(t + round) % Strategies.Length];
                    var actual = Run(searcher, metadata, keys, false);
                    if (expected.Ids.SequenceEqual(actual.Ids) == false || expected.Scores.SequenceEqual(actual.Scores) == false)
                        lock (failures)
                            failures.Add($"thread {t} round {round} {field}");
                }
            }
            catch (Exception e)
            {
                lock (failures)
                    failures.Add($"thread {t}: {e}");
            }
            finally
            {
                JoinKeyLookupSettings.Forced = JoinKeyLookupStrategy.Auto;
            }
        })).ToList();

        threads.ForEach(x => x.Start());
        threads.ForEach(x => x.Join());
        Assert.Empty(failures);
    }

    private static void CompareScoreMaps(IndexSearcher searcher, in FieldMetadata metadata, JoinTermScores keys, bool termsAreIndexedTerms, string label)
    {
        var expected = Oracle(searcher, metadata, keys, termsAreIndexedTerms);

        foreach (var strategy in Strategies)
        {
            JoinKeyLookupSettings.Forced = strategy;
            try
            {
                var actual = Run(searcher, metadata, keys, termsAreIndexedTerms);
                Assert.True(expected.Ids.SequenceEqual(actual.Ids), $"{label}: ids differ with {strategy} ({expected.Ids.Length} vs {actual.Ids.Length})");
                Assert.True(expected.Scores.SequenceEqual(actual.Scores), $"{label}: scores differ with {strategy}");
            }
            finally
            {
                JoinKeyLookupSettings.Forced = JoinKeyLookupStrategy.Auto;
            }
        }

        if (keys.Count > 0 && label.Contains("raw keys") && label.Contains("100"))
            Assert.NotEmpty(expected.Ids);
    }

    // the plain query of every key, independent of the lookups and the drain of the score map
    private static (long[] Ids, int[] Scores) Oracle(IndexSearcher searcher, in FieldMetadata metadata, JoinTermScores keys, bool termsAreIndexedTerms)
    {
        var field = termsAreIndexedTerms ? metadata.ChangeAnalyzer(FieldIndexingMode.Exact) : metadata;
        var scoresOfEntry = new Dictionary<long, List<float>>();
        var buffer = new long[4096];
        for (var k = 0; k < keys.Count; k++)
        {
            var match = searcher.TermQuery(field, Encoding.UTF8.GetString(keys.Term(k)));
            int read;
            while ((read = match.Fill(buffer)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (scoresOfEntry.TryGetValue(buffer[i], out var list) == false)
                        scoresOfEntry[buffer[i]] = list = new List<float>();
                    list.Add(keys.Score(k));
                }
            }
        }

        var ids = scoresOfEntry.Keys.Order().ToArray();
        var scores = ids.Select(id =>
        {
            var sum = 0f;
            foreach (var score in scoresOfEntry[id].Order())
                sum += score;
            return BitConverter.SingleToInt32Bits(sum);
        }).ToArray();
        return (ids, scores);
    }

    private static (long[] Ids, int[] Scores) Run(IndexSearcher searcher, in FieldMetadata metadata, JoinTermScores keys, bool termsAreIndexedTerms)
    {
        var map = searcher.ScoreMap(metadata, keys, termsAreIndexedTerms);
        var ids = new long[map.Count];
        var read = 0;
        while (read < ids.Length)
        {
            var n = map.Fill(ids.AsSpan(read));
            if (n == 0)
                break;
            read += n;
        }

        Assert.Equal(ids.Length, read);
        var scores = new float[ids.Length];
        map.Score(ids, scores, 1f);
        return (ids, scores.Select(BitConverter.SingleToInt32Bits).ToArray());
    }

    // a random subset of the keys, in a random order, with scores that make the sums depend on the order of the additions
    private static JoinTermScores Subset(IndexSearcher searcher, List<byte[]> universe, double density, Random random)
    {
        var picked = universe.Where(_ => random.NextDouble() < density).ToList();
        for (var i = picked.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (picked[i], picked[j]) = (picked[j], picked[i]);
        }

        var keys = new JoinTermScores(searcher.Allocator);
        var seen = new HashSet<string>();
        foreach (var key in picked)
        {
            if (seen.Add(Convert.ToHexString(key)))
                keys.Add(key, 0.1f + (float)random.NextDouble() * 3.7f);
        }

        return keys;
    }

    private List<byte[]> RawKeys(string field)
    {
        switch (field)
        {
            case "Body":
                return Enumerable.Range(0, Words).Select(i => $"word{i}")
                    .Concat(Enumerable.Range(0, RareWords).Select(i => $"rare{i}"))
                    .Concat(Enumerable.Range(0, Words).Select(i => $"WORD{i}"))
                    .Select(x => Encoding.UTF8.GetBytes(x)).ToList();
            case "Tags":
                return Enumerable.Range(0, 1500).Select(i => $"tag-{i}").Append("tag-hot").Concat(Enumerable.Range(0, 12).Select(i => $"tag-solo{i}")).Select(x => Encoding.UTF8.GetBytes(x)).ToList();
        }

        var keys = new List<byte[]>();
        var random = new Random(field.Length);
        foreach (var owner in _owners.Distinct())
        {
            keys.Add(Encoding.UTF8.GetBytes(owner));
            if (random.Next(4) == 0)
                keys.Add(Encoding.UTF8.GetBytes(owner.ToUpperInvariant()));
            if (random.Next(10) == 0)
                keys.Add(Encoding.UTF8.GetBytes(owner + "-missing"));
        }

        return keys;
    }

    private List<byte[]> TermsOfTree(string field)
    {
        using var tx = Env.ReadTransaction();
        var tree = tx.ReadTree(Constants.IndexWriter.FieldsSlice).CompactTreeFor(field);
        var terms = new List<byte[]>();
        var it = tree.Iterate();
        it.Reset();
        while (it.MoveNext(out var key, out _, out _))
            terms.Add(key.Decoded().ToArray());
        return terms;
    }

    private readonly int[] _hotCounts = new int[2];

    private void Index()
    {
        var random = new Random(23);
        using var knownFields = CreateKnownFields(Allocator);
        using var indexWriter = new IndexWriter(Env, knownFields, SupportedFeatures.All);
        for (var i = 0; i < Docs; i++)
        {
            var id = Encoding.UTF8.GetBytes($"games/{i}");
            using var builder = indexWriter.Index(id);
            builder.Write(IdField, null, id);

            var roll = random.Next(100);
            string owner;
            if (roll < 40)
            {
                var hot = random.Next(2);
                _hotCounts[hot]++;
                owner = $"users/hot{hot}";
            }
            else if (roll < 55)
                owner = $"users/mid{random.Next(60)}";
            else if (roll < 58)
                owner = $"Users/{random.Next(5000)}";
            else
                owner = $"users/{random.Next(20_000)}";

            _owners.Add(owner);
            builder.Write(OwnerField, null, Encoding.UTF8.GetBytes(owner));

            var mixed = new string(owner.Select(c => random.Next(3) == 0 ? char.ToUpperInvariant(c) : c).ToArray());
            builder.Write(LowerField, null, Encoding.UTF8.GetBytes(mixed));

            builder.IncrementList();
            for (var t = random.Next(1, 5); t > 0; t--)
                builder.Write(TagsField, null, Encoding.UTF8.GetBytes(random.Next(10) == 0 ? "tag-hot" : $"tag-{random.Next(1500)}"));
            if (i % 5000 == 0)
                builder.Write(TagsField, null, Encoding.UTF8.GetBytes($"tag-solo{i / 5000}"));
            builder.DecrementList();

            // few words, repeated: the entries of the lists get frequency bits
            var words = Enumerable.Range(0, random.Next(3, 9)).Select(_ => $"word{random.Next(Words)}").ToList();
            words.AddRange(words.Take(2));
            if (random.Next(30) == 0)
            {
                var rare = $"rare{random.Next(RareWords)}";
                words.Add(rare);
                words.Add(rare);
            }

            builder.Write(BodyField, null, Encoding.UTF8.GetBytes(string.Join(' ', words)));
            builder.EndWriting();
        }

        indexWriter.Commit();
    }

    private IndexFieldsMapping CreateKnownFields(ByteStringContext bsc)
    {
        Slice.From(bsc, "Id", ByteStringType.Immutable, out var idSlice);
        Slice.From(bsc, "Owner", ByteStringType.Immutable, out var ownerSlice);
        Slice.From(bsc, "Lower", ByteStringType.Immutable, out var lowerSlice);
        Slice.From(bsc, "Tags", ByteStringType.Immutable, out var tagsSlice);
        Slice.From(bsc, "Body", ByteStringType.Immutable, out var bodySlice);

        using var builder = IndexFieldsMappingBuilder.CreateForWriter(false)
            .AddBinding(IdField, idSlice)
            .AddBinding(OwnerField, ownerSlice)
            .AddBinding(LowerField, lowerSlice, Analyzer.CreateLowercaseAnalyzer(_analyzers))
            .AddBinding(TagsField, tagsSlice)
            .AddBinding(BodyField, bodySlice, Analyzer.Create(_analyzers, default(WhitespaceTokenizer), default(LowerCaseTransformer)), fieldIndexingMode: FieldIndexingMode.Search);

        return builder.Build();
    }
}
