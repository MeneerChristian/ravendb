using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Corax;
using Corax.Indexing;
using Corax.Mappings;
using Corax.Querying;
using Corax.Querying.Matches.Meta;
using FastTests.Server.Documents.Indexing.Joins.Storage;
using FastTests.Voron;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Server;
using Sparrow.Utils;
using Tests.Infrastructure;
using Voron;
using Xunit;
using IndexSearcher = Corax.Querying.IndexSearcher;
using IndexWriter = Corax.Indexing.IndexWriter;

namespace FastTests.Server.Documents.Indexing.Joins.Serving;

/// <summary>The union of the lists of a range or a prefix is read like any other match of Corax: ascending, without repeats, with an exact count, and it takes the bitmap path of an AND.</summary>
public class JoinTermUnionTests : StorageTest
{
    private const int Entries = 600;
    private const int NumberField = 0, TextField = 1;
    private static readonly string[] Words = ["a", "ab", "abc", "abd", "b", "ba", "bb", "c", "ca", "cab"];

    public JoinTermUnionTests(ITestOutputHelper output) : base(output)
    {
        using IndexFieldsMapping knownFields = KnownFields();
        using var writer = new IndexWriter(Env, knownFields, SupportedFeatures.All);
        for (int i = 0; i < Entries; i++)
        {
            byte[] id = Encoding.UTF8.GetBytes($"docs/{i}");
            using var builder = writer.Index(id);
            builder.Write(0, null, id);
            builder.EndWriting();
        }

        writer.Commit();
    }

    private IndexFieldsMapping KnownFields()
    {
        Slice.From(Allocator, "Id", ByteStringType.Immutable, out Slice idSlice);
        using var builder = IndexFieldsMappingBuilder.CreateForWriter(false).AddBinding(0, idSlice);
        return builder.Build();
    }

    private sealed class Model
    {
        public readonly Dictionary<long, HashSet<long>> Numbers = new();
        public readonly Dictionary<string, HashSet<long>> Texts = new();
        public readonly HashSet<long> All = new();
    }

    private static JoinIndexGeneration Generation() => new(0, Guid.NewGuid(), new JoinCursors(new[] { ("Messages", 1L, 1L) }), Guid.NewGuid(), JoinCursors.Empty, 1, 0);

    private static Model Fill(JoinStorageHarness storage, Random random, long lastEntryId, int numbers)
    {
        var model = new Model();
        using JoinStorageHarness.Write write = storage.BeginWrite();
        JoinWriter writer = write.Writer;
        for (long id = 1; id <= lastEntryId; id++)
        {
            if (random.Next(10) == 0)
                continue;

            model.All.Add(id);
            writer.AddToAny(id);

            // several terms per entry: the lists of a range repeat entries
            for (int times = random.Next(1, 4); times > 0; times--)
            {
                long number = random.Next(numbers) - 5;
                if (model.Numbers.TryGetValue(number, out HashSet<long> set) == false)
                    model.Numbers[number] = set = new HashSet<long>();

                if (set.Add(id))
                    writer.AddToTerm(NumberField, writer.GetOrAddTerm(NumberField, Encoding.UTF8.GetBytes(number.ToString(CultureInfo.InvariantCulture))), id);

                string word = Words[random.Next(Words.Length)];
                if (model.Texts.TryGetValue(word, out HashSet<long> texts) == false)
                    model.Texts[word] = texts = new HashSet<long>();

                if (texts.Add(id))
                    writer.AddToTerm(TextField, writer.GetOrAddTerm(TextField, Encoding.UTF8.GetBytes(word)), id);
            }
        }

        write.Commit(Generation());
        return model;
    }

    private static long[] Drain(IQueryMatch match, int chunk)
    {
        var all = new List<long>();
        long[] buffer = new long[chunk];
        int read;
        while ((read = match.Fill(buffer)) > 0)
            all.AddRange(buffer.AsSpan(0, read).ToArray());

        return all.ToArray();
    }

    private static HashSet<long> Union<TKey>(Dictionary<TKey, HashSet<long>> lists, Func<TKey, bool> inRange)
    {
        var union = new HashSet<long>();
        foreach ((TKey key, HashSet<long> ids) in lists)
        {
            if (inRange(key))
                union.UnionWith(ids);
        }

        return union;
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void TheUnionOfARangeIsABitmapOfTheEntriesThatIsReadAscendingWithAnExactCount()
    {
        using var searcher = new IndexSearcher(Env, KnownFields());
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();
        Model model = Fill(storage, new Random(1), searcher.LastEntryId, 40);
        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();

        JoinPredicate range = JoinPredicate.OfNumbers(NumberField, true, 0, true, true, 30, false);
        IQueryMatch match = JoinMatchFactory.Create(searcher, snapshot, range);

        long[] expected = Union(model.Numbers, number => number >= 0 && number < 30).Order().ToArray();
        Assert.True(expected.Length > 100);
        Assert.Equal("JoinBitmapMatch", match.Inspect().Operation);
        Assert.Equal(expected.Length, match.Count);
        Assert.Equal(QueryCountConfidence.High, match.Confidence);
        Assert.Equal(DuplicatesOccurrence.NotPossible, match.DuplicatesOccurrenceStatus);
        Assert.Equal(SkipSortingResult.ResultsNativelySorted, match.AttemptToSkipSorting());
        Assert.Equal(expected, Drain(match, 7));
        Assert.Equal(0, match.Fill(new long[8]));
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(1, BitmapAndFillMode.Auto, false)]
    [InlineData(2, BitmapAndFillMode.Force, false)]
    [InlineData(3, BitmapAndFillMode.Off, false)]
    [InlineData(4, BitmapAndFillMode.Force, true)]
    [InlineData(5, BitmapAndFillMode.Force, true)]
    public void RangesAndPrefixesCombinedWithAndOrNotAreTheSetsOfTheModel(int seed, BitmapAndFillMode mode, bool bitmapDoesNotFit)
    {
        using var searcher = new IndexSearcher(Env, KnownFields());
        searcher.BitmapAndFillMode = mode;
        if (bitmapDoesNotFit)
            searcher.MaxMemoizationSizeInBytes = (searcher.LastEntryId + 1 + 63) / 64 * sizeof(ulong) - 1;

        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();
        var random = new Random(seed);
        Model model = Fill(storage, random, searcher.LastEntryId, 40);
        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();

        for (int round = 0; round < 60; round++)
        {
            bool hasLower = random.Next(4) != 0, hasUpper = random.Next(4) != 0;
            long lower = random.Next(-8, 40), upper = random.Next(-8, 40);
            bool lowerInclusive = random.Next(2) == 0, upperInclusive = random.Next(2) == 0;
            JoinPredicate range = JoinPredicate.OfNumbers(NumberField, hasLower, lower, lowerInclusive, hasUpper, upper, upperInclusive);
            HashSet<long> inRange = Union(model.Numbers, n => (hasLower == false || n > lower || (lowerInclusive && n == lower)) && (hasUpper == false || n < upper || (upperInclusive && n == upper)));

            string prefix = new[] { "a", "ab", "abc", "b", "c", "ca", "zz" }[random.Next(7)];
            JoinPredicate startsWith = JoinPredicate.OfPrefix(TextField, Encoding.UTF8.GetBytes(prefix));
            HashSet<long> prefixed = Union(model.Texts, w => w.StartsWith(prefix, StringComparison.Ordinal));

            string textLower = Words[random.Next(Words.Length)], textUpper = Words[random.Next(Words.Length)];
            JoinPredicate textRange = JoinPredicate.OfTerms(TextField, Encoding.UTF8.GetBytes(textLower), lowerInclusive, Encoding.UTF8.GetBytes(textUpper), upperInclusive);
            HashSet<long> textInRange = Union(model.Texts, w =>
            {
                int low = string.CompareOrdinal(w, textLower), high = string.CompareOrdinal(w, textUpper);
                return (low > 0 || (lowerInclusive && low == 0)) && (high < 0 || (upperInclusive && high == 0));
            });

            string word = Words[random.Next(Words.Length)];
            JoinPredicate term = JoinPredicate.OfTerm(TextField, Encoding.UTF8.GetBytes(word));
            HashSet<long> ofTerm = model.Texts.TryGetValue(word, out HashSet<long> found) ? found : new HashSet<long>();

            Check(searcher, snapshot, range, inRange);
            Check(searcher, snapshot, startsWith, prefixed);
            Check(searcher, snapshot, textRange, textInRange);
            Check(searcher, snapshot, JoinPredicate.And(range, term), inRange.Intersect(ofTerm).ToHashSet());
            Check(searcher, snapshot, JoinPredicate.And(startsWith, range), prefixed.Intersect(inRange).ToHashSet());
            Check(searcher, snapshot, JoinPredicate.Or(range, startsWith), inRange.Union(prefixed).ToHashSet());
            Check(searcher, snapshot, JoinPredicate.Not(range), model.All.Except(inRange).ToHashSet());
            Check(searcher, snapshot, JoinPredicate.And(JoinPredicate.Not(startsWith), range), inRange.Except(prefixed).ToHashSet());
            Check(searcher, snapshot, JoinPredicate.And(JoinPredicate.Or(range, term), JoinPredicate.Not(textRange)), inRange.Union(ofTerm).Except(textInRange).ToHashSet());
        }
    }

    private static void Check(IndexSearcher searcher, JoinIndexSnapshot snapshot, JoinPredicate predicate, HashSet<long> expected)
    {
        long[] sorted = expected.Order().ToArray();
        IQueryMatch drained = JoinMatchFactory.Create(searcher, snapshot, predicate);
        bool repeatsAllowed = drained.DuplicatesOccurrenceStatus != DuplicatesOccurrence.NotPossible;
        long[] actual = Drain(drained, 64);
        if (repeatsAllowed)
            actual = actual.Distinct().Order().ToArray();

        Assert.True(sorted.SequenceEqual(actual), $"{Describe(predicate)}: expected {sorted.Length} ids, got {actual.Length}; missing [{string.Join(",", sorted.Except(actual).Take(10))}] extra [{string.Join(",", actual.Except(sorted).Take(10))}] repeated {actual.Length - actual.Distinct().Count()}");

        // the entries that a filtered side leaves: AndWith keeps exactly those that are in the union
        IQueryMatch match = JoinMatchFactory.Create(searcher, snapshot, predicate);
        long[] candidates = Enumerable.Range(1, (int)searcher.LastEntryId).Where(i => i % 3 != 0).Select(i => (long)i).ToArray();
        int kept = match.AndWith(candidates, candidates.Length);
        Assert.Equal(Enumerable.Range(1, (int)searcher.LastEntryId).Where(i => i % 3 != 0).Select(i => (long)i).Where(expected.Contains).ToArray(), candidates.AsSpan(0, kept).ToArray());
    }

    private static string Describe(JoinPredicate predicate)
    {
        return predicate.Left == null
            ? predicate.Kind.ToString()
            : $"{predicate.Kind}({Describe(predicate.Left)}{(predicate.Right == null ? "" : ", " + Describe(predicate.Right))})";
    }

    [RavenFact(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    public void ARangeThatMatchesNothingAndOneThatMatchesEverythingAreExact()
    {
        using var searcher = new IndexSearcher(Env, KnownFields());
        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();
        Model model = Fill(storage, new Random(9), searcher.LastEntryId, 20);
        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();

        IQueryMatch none = JoinMatchFactory.Create(searcher, snapshot, JoinPredicate.OfNumbers(NumberField, true, 1000, true, false, 0, false));
        Assert.Equal(0, none.Count);
        Assert.Empty(Drain(none, 16));
        Assert.Equal(0, none.AndWith(new long[] { 1, 2, 3 }, 3));

        IQueryMatch every = JoinMatchFactory.Create(searcher, snapshot, JoinPredicate.OfNumbers(NumberField, false, 0, false, false, 0, false));
        Assert.Equal(model.All.Count, every.Count);
        Assert.Equal(model.All.Order().ToArray(), Drain(every, 100));
    }

    private void GrowTheIdSpace(int entries)
    {
        using IndexFieldsMapping knownFields = KnownFields();
        using var writer = new IndexWriter(Env, knownFields, SupportedFeatures.All);
        for (int i = 0; i < entries; i++)
        {
            byte[] id = Encoding.UTF8.GetBytes($"more/{i}");
            using var builder = writer.Index(id);
            builder.Write(0, null, id);
            builder.EndWriting();
        }

        writer.Commit();
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [InlineData(1)]
    [InlineData(300)]
    public void AFewIdsInALargeIdSpaceDoNotCostABitmapOfTheIdSpace(int idsInRange)
    {
        GrowTheIdSpace(1_200_000);
        using var searcher = new IndexSearcher(Env, KnownFields());
        long bitmapBytes = (searcher.LastEntryId + 1 + 63) / 64 * sizeof(ulong);
        Output.WriteLine($"LastEntryId {searcher.LastEntryId}, bitmap {bitmapBytes} bytes");
        Assert.True(bitmapBytes > 128 * 1024);

        using JoinStorageHarness storage = JoinStorageHarness.OpenInMemory();
        var expected = new List<long>();
        using (JoinStorageHarness.Write write = storage.BeginWrite())
        {
            for (int i = 0; i < idsInRange; i++)
            {
                long id = 1 + (long)i * (searcher.LastEntryId - 2) / idsInRange;
                expected.Add(id);
                write.Writer.AddToAny(id);
                write.Writer.AddToTerm(NumberField, write.Writer.GetOrAddTerm(NumberField, Encoding.UTF8.GetBytes((i % 5).ToString(CultureInfo.InvariantCulture))), id);
            }

            write.Commit(Generation());
        }

        using JoinIndexSnapshot snapshot = storage.OpenSnapshot();
        JoinPredicate range = JoinPredicate.OfNumbers(NumberField, true, 0, true, true, 5, false);

        long before = NativeMemory.CurrentThreadStats.TotalAllocated;
        IQueryMatch match = JoinMatchFactory.Create(searcher, snapshot, range);
        long allocated = NativeMemory.CurrentThreadStats.TotalAllocated - before;

        Output.WriteLine($"allocated {allocated} bytes for {idsInRange} ids");
        Assert.True(allocated < bitmapBytes, $"{allocated} bytes allocated for {idsInRange} ids in an id space whose bitmap is {bitmapBytes} bytes");
        Assert.Equal(expected.Count, match.Count);
        Assert.Equal(expected, Drain(match, 64));
    }
}
