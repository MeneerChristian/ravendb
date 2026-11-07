using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Corax;
using Corax.Analyzers;
using Corax.Mappings;
using Corax.Pipeline;
using Corax.Querying;
using FastTests.Voron;
using Sparrow.Server;
using Sparrow.Threading;
using Tests.Infrastructure;
using Voron;
using Xunit;
using IndexSearcher = Corax.Querying.IndexSearcher;
using IndexWriter = Corax.Indexing.IndexWriter;

namespace FastTests.Corax;

// The lookup of the keys of a JOIN in the tree of the outer field goes on from the previous key or sweeps the tree
public class JoinKeyLookupTests : StorageTest
{
    private const int Docs = 40_000;
    private const int IdField = 0, OwnerField = 1, LowerField = 2, TagsField = 3, TextField = 4;

    private static readonly JoinKeyLookupStrategy[] Strategies = [JoinKeyLookupStrategy.Cursor, JoinKeyLookupStrategy.Sweep, JoinKeyLookupStrategy.Auto];
    private static readonly string[] Fields = ["Owner", "Lower", "Tags", "Text"];

    private readonly ByteStringContext _analyzers = new(SharedMultipleUseFlag.None);
    private readonly List<string> _owners = new();

    public JoinKeyLookupTests(ITestOutputHelper output) : base(output)
    {
        Index();
    }

    public override async ValueTask DisposeAsync()
    {
        _analyzers.Dispose();
        await base.DisposeAsync();
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void TheRuleOfTheStrategy()
    {
        Assert.Equal(JoinKeyLookupStrategy.Sweep, JoinKeyLookupSettings.Choose(1_000, 8_000));
        Assert.Equal(JoinKeyLookupStrategy.Cursor, JoinKeyLookupSettings.Choose(999, 8_000));
        Assert.Equal(JoinKeyLookupStrategy.Sweep, JoinKeyLookupSettings.Choose(1, 1));
        Assert.Equal(JoinKeyLookupStrategy.Sweep, JoinKeyLookupSettings.Choose(0, 0));
    }

    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData("Owner")]
    [InlineData("Lower")]
    [InlineData("Tags")]
    [InlineData("Text")]
    public void ScoreMapOfRawKeysIsTheSameForEveryStrategy(string field)
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata(field);
        var universe = RawKeys(field);
        var random = new Random(field.GetHashCode());

        foreach (var density in new[] { 0.001, 0.01, 0.1, 0.3, 1.0 })
        {
            using var keys = Subset(searcher, universe, density, random);
            CompareScoreMaps(searcher, metadata, keys, termsAreIndexedTerms: false, $"{field} raw keys at {density:P1}");
        }
    }

    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData("Owner")]
    [InlineData("Lower")]
    [InlineData("Tags")]
    [InlineData("Text")]
    public void ScoreMapOfIndexedTermsIsTheSameForEveryStrategy(string field)
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata(field);
        var universe = TermsOfTree(searcher, field).Concat(Enumerable.Range(0, 300).Select(i => Encoding.UTF8.GetBytes($"missing/{i}"))).ToList();
        var random = new Random(field.GetHashCode() + 1);

        foreach (var density in new[] { 0.001, 0.01, 0.1, 0.3, 1.0 })
        {
            using var keys = Subset(searcher, universe, density, random);
            CompareScoreMaps(searcher, metadata, keys, termsAreIndexedTerms: true, $"{field} indexed terms at {density:P1}");
        }
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void InQueryIsTheSameForEveryStrategy()
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var random = new Random(11);
        foreach (var field in Fields)
        {
            var metadata = searcher.GetFieldMetadata(field);
            foreach (var indexedTerms in new[] { false, true })
            {
                var universe = indexedTerms ? TermsOfTree(searcher, field) : RawKeys(field);
                foreach (var density in new[] { 0.01, 0.1, 0.5, 1.0 })
                {
                    using var keys = Subset(searcher, universe, density, random);
                    var expected = InEntries(searcher, metadata, keys, indexedTerms, JoinKeyLookupStrategy.PerKey);
                    Assert.NotEmpty(expected);
                    foreach (var strategy in Strategies)
                        Assert.True(expected.SequenceEqual(InEntries(searcher, metadata, keys, indexedTerms, strategy)), $"{field} indexedTerms={indexedTerms} {density:P0} {strategy}");
                }
            }
        }
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void InQueryKeepsItsCursorWhenTheQueryLooksUpTheSameTreeInBetween()
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata("Owner");
        var universe = RawKeys("Owner");
        var random = new Random(5);
        using var first = Subset(searcher, universe, 0.3, random);
        using var second = Subset(searcher, universe, 0.3, random);
        var expectedFirst = InEntries(searcher, metadata, first, false, JoinKeyLookupStrategy.PerKey);
        var expectedSecond = InEntries(searcher, metadata, second, false, JoinKeyLookupStrategy.PerKey);

        JoinKeyLookupSettings.Forced = JoinKeyLookupStrategy.Cursor;
        try
        {
            var a = searcher.InQuery(metadata, first, false);
            var b = searcher.InQuery(metadata, second, false);
            var gotA = new List<long>();
            var gotB = new List<long>();
            var buffer = new long[64];
            int readA, readB;
            do
            {
                readA = a.Fill(buffer);
                gotA.AddRange(buffer.AsSpan(0, readA).ToArray());
                // a lookup of another term on the tree that the IN reads, and the other IN, between two fills
                _ = searcher.TermQuery("Owner", "users/12345").Count;
                readB = b.Fill(buffer);
                gotB.AddRange(buffer.AsSpan(0, readB).ToArray());
            } while (readA + readB > 0);

            Assert.True(expectedFirst.SequenceEqual(gotA.Distinct().Order()));
            Assert.True(expectedSecond.SequenceEqual(gotB.Distinct().Order()));
        }
        finally
        {
            JoinKeyLookupSettings.Forced = JoinKeyLookupStrategy.Auto;
        }
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void MarkersAreFoundByEveryStrategy()
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata("Owner");
        using (var tx = Env.ReadTransaction())
        {
            var tree = tx.ReadTree(Constants.IndexWriter.FieldsSlice).CompactTreeFor("Owner");
            Assert.True(tree.TryGetValue(Constants.EmptyStringByteSpan, out _), "the data has an empty string");
            Assert.False(tree.TryGetValue(Constants.NullValueSpan, out _), "the entries with a null are kept apart from the terms of the field: the null key is a miss");
        }

        var keyBytes = new byte[][] { [], Constants.EmptyStringByteSpan.ToArray(), Constants.NullValueSpan.ToArray(), "users/1"u8.ToArray(), "users/2"u8.ToArray(), "nothing"u8.ToArray() };
        foreach (var order in new[] { new[] { 0, 1, 2, 3, 4, 5 }, new[] { 5, 4, 3, 2, 1, 0 }, new[] { 3, 2, 5, 0, 4, 1 } })
        {
            using var keys = new JoinTermScores(searcher.Allocator);
            var score = 1f;
            foreach (var i in order)
                keys.Add(keyBytes[i], score += 0.25f);
            CompareScoreMaps(searcher, metadata, keys, termsAreIndexedTerms: false, "markers");
            CompareScoreMaps(searcher, metadata, keys, termsAreIndexedTerms: true, "markers as terms");
        }
    }

    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void JoinFirstEntriesOfKeysThatAreNotInTheTreeIsEmpty(bool countTotal, bool membershipByKey)
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata("Owner");
        using var keys = new JoinTermScores(searcher.Allocator);
        for (var i = 0; i < 100; i++)
            keys.Add(Encoding.UTF8.GetBytes($"missing/{i}"), 1f);

        Assert.True(searcher.TryJoinFirstEntries(metadata, keys, keysAreIndexedTerms: false, useEncodedKeys: false, membershipByKey, where: null, take: 10, countTotal, out var match));
        Assert.Equal(0, match.TotalResults);
        Assert.Equal(0, match.Fill(new long[16]));
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void TheAnalyzerThatChangesTheOrderStartsAgainFromTheRoot()
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata("Lower");
        // the bytes sort the upper case first, the analyzed terms are in the opposite order
        var raw = new List<byte[]>();
        for (var i = 0; i < 4000; i++)
        {
            raw.Add(Encoding.UTF8.GetBytes($"USERS/{i * 7 % 3000}"));
            raw.Add(Encoding.UTF8.GetBytes($"Users/{i * 11 % 3000}"));
            raw.Add(Encoding.UTF8.GetBytes($"ÉCOLE/{i % 50}"));
        }

        var random = new Random(2);
        using var keys = new JoinTermScores(searcher.Allocator);
        foreach (var key in raw.DistinctBy(Convert.ToHexString))
            keys.Add(key, (float)random.NextDouble());
        CompareScoreMaps(searcher, metadata, keys, termsAreIndexedTerms: false, "upper case keys of a lower case field");
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void ConcurrentQueriesDoNotShareACursor()
    {
        var universe = RawKeys("Owner");
        var textUniverse = RawKeys("Text");
        var failures = new List<string>();
        var threads = Enumerable.Range(0, 4).Select(t => new Thread(() =>
        {
            try
            {
                JoinKeyLookupSettings.Forced = Strategies[t % Strategies.Length];
                using var searcher = new IndexSearcher(Env, CreateKnownFields(new ByteStringContext(SharedMultipleUseFlag.None)));
                var random = new Random(t);
                for (var round = 0; round < 12; round++)
                {
                    var field = round % 2 == 0 ? "Owner" : "Text";
                    var metadata = searcher.GetFieldMetadata(field);
                    using var keys = Subset(searcher, round % 2 == 0 ? universe : textUniverse, round % 3 == 0 ? 0.5 : 0.05, random);
                    JoinKeyLookupSettings.Forced = JoinKeyLookupStrategy.PerKey;
                    var expected = Run(searcher, metadata, keys, false);
                    JoinKeyLookupSettings.Forced = Strategies[(t + round) % Strategies.Length];
                    var actual = Run(searcher, metadata, keys, false);
                    if (expected.Ids.SequenceEqual(actual.Ids) == false || expected.Scores.SequenceEqual(actual.Scores) == false)
                        lock (failures)
                            failures.Add($"thread {t} round {round}");
                }
            }
            catch (Exception e)
            {
                lock (failures)
                    failures.Add($"thread {t}: {e}");
            }
        })).ToList();

        threads.ForEach(x => x.Start());
        threads.ForEach(x => x.Join());
        Assert.Empty(failures);
    }

    private void CompareScoreMaps(IndexSearcher searcher, in FieldMetadata metadata, JoinTermScores keys, bool termsAreIndexedTerms, string label)
    {
        JoinKeyLookupSettings.Forced = JoinKeyLookupStrategy.PerKey;
        var expected = Run(searcher, metadata, keys, termsAreIndexedTerms);
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

        JoinKeyLookupSettings.Forced = JoinKeyLookupStrategy.Auto;
        if (keys.Count > 0 && label.Contains("raw keys") && label.Contains("100"))
            Assert.NotEmpty(expected.Ids);
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

    private static List<long> InEntries(IndexSearcher searcher, in FieldMetadata metadata, JoinTermScores keys, bool indexedTerms, JoinKeyLookupStrategy strategy)
    {
        JoinKeyLookupSettings.Forced = strategy;
        try
        {
            var match = searcher.InQuery(metadata, keys, indexedTerms);
            var all = new List<long>();
            var buffer = new long[256];
            int read;
            while ((read = match.Fill(buffer)) > 0)
                all.AddRange(buffer.AsSpan(0, read).ToArray());
            return all.Distinct().Order().ToList();
        }
        finally
        {
            JoinKeyLookupSettings.Forced = JoinKeyLookupStrategy.Auto;
        }
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
        var keys = new List<byte[]>();
        var random = new Random(field.Length);
        foreach (var owner in _owners.Distinct())
        {
            keys.Add(Encoding.UTF8.GetBytes(owner));
            // the same id in the other cases, and an id that does not exist
            if (random.Next(4) == 0)
                keys.Add(Encoding.UTF8.GetBytes(owner.ToUpperInvariant()));
            if (random.Next(10) == 0)
                keys.Add(Encoding.UTF8.GetBytes(owner + "-missing"));
        }

        if (field == "Text")
            keys = Words().Select(x => Encoding.UTF8.GetBytes(x)).Concat(Words().Select(x => Encoding.UTF8.GetBytes(x.ToUpperInvariant()))).ToList();
        else if (field == "Tags")
            keys = Tags().Select(x => Encoding.UTF8.GetBytes(x)).Concat(keys.Take(500)).ToList();
        return keys;
    }

    private List<byte[]> TermsOfTree(IndexSearcher searcher, string field)
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

    private static IEnumerable<string> Tags() => Enumerable.Range(0, 1500).Select(i => $"tag-{i * 13 % 1500}");

    private static IEnumerable<string> Words() => Enumerable.Range(0, 800).Select(i => i % 5 == 0 ? $"Wörd{i}" : $"word{i}");

    private static string[] SpecialOwners =
    [
        "users/é", "users/ß1", "users/😀1", "users/～1", "users/1", "users/\U00010437", "users/\U0001F355\U0001F355", "users/中文1", "users/�", "Users/ÄRGER", "users/ärger"
    ];

    private void Index()
    {
        var random = new Random(17);
        using var knownFields = CreateKnownFields(Allocator);
        using var indexWriter = new IndexWriter(Env, knownFields, SupportedFeatures.All);
        for (var i = 0; i < Docs; i++)
        {
            var id = Encoding.UTF8.GetBytes($"games/{i}");
            using var builder = indexWriter.Index(id);
            builder.Write(IdField, null, id);

            var roll = random.Next(1000);
            string owner;
            if (roll < 2)
                owner = null;
            else if (roll < 4)
                owner = string.Empty;
            else if (roll < 20)
                owner = SpecialOwners[random.Next(SpecialOwners.Length)] + (random.Next(3) == 0 ? "" : random.Next(50).ToString());
            else if (roll < 60)
                owner = $"Users/{random.Next(20_000)}";
            else
                owner = $"users/{random.Next(20_000)}";

            if (owner == null)
                builder.WriteNull(OwnerField, null);
            else
            {
                builder.Write(OwnerField, null, Encoding.UTF8.GetBytes(owner));
                _owners.Add(owner);
            }

            // the same id with a random case: an analyzer that folds the case makes the keys and the terms differ in order
            var mixed = owner == null ? "x" : new string(owner.Select(c => random.Next(3) == 0 ? char.ToUpperInvariant(c) : c).ToArray());
            builder.Write(LowerField, null, Encoding.UTF8.GetBytes(string.IsNullOrEmpty(mixed) ? "x" : mixed));

            builder.IncrementList();
            for (var t = random.Next(1, 5); t > 0; t--)
                builder.Write(TagsField, null, Encoding.UTF8.GetBytes($"tag-{random.Next(1500)}"));
            builder.DecrementList();

            var words = string.Join(' ', Enumerable.Range(0, random.Next(1, 6)).Select(_ => random.Next(800)).Select(w => w % 5 == 0 ? $"Wörd{w}" : $"word{w}"));
            builder.Write(TextField, null, Encoding.UTF8.GetBytes(words));
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
        Slice.From(bsc, "Text", ByteStringType.Immutable, out var textSlice);

        using var builder = IndexFieldsMappingBuilder.CreateForWriter(false)
            .AddBinding(IdField, idSlice)
            .AddBinding(OwnerField, ownerSlice)
            .AddBinding(LowerField, lowerSlice, Analyzer.CreateLowercaseAnalyzer(_analyzers))
            .AddBinding(TagsField, tagsSlice)
            .AddBinding(TextField, textSlice, Analyzer.Create(_analyzers, default(WhitespaceTokenizer), default(LowerCaseTransformer)));

        return builder.Build();
    }
}
