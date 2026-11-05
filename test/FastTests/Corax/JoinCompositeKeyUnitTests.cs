using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corax.Querying;
using Corax.Utils;
using Sparrow.Server;
using Sparrow.Threading;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

public class JoinCompositeKeyUnitTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    private static byte[] Key(params string[] parts)
    {
        using var builder = new CompositeKeyBuilder(parts.Length);
        for (var i = 0; i < parts.Length; i++)
            builder.Add(i, Encoding.UTF8.GetBytes(parts[i]));

        return builder.Combination(0).ToArray();
    }

    private static List<byte[]> Parts(ReadOnlySpan<byte> key)
    {
        var parts = new List<byte[]>();
        var position = 0;
        while (CompositeKey.TryReadPart(key, ref position, out var part))
            parts.Add(part.ToArray());
        return parts;
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void PartsComeBackAsTheyWereEscaped()
    {
        var parts = Parts(Key("a\0", "b", "\0\0c"));
        Assert.Equal(3, parts.Count);
        Assert.Equal(new byte[] { 0x61, 0, 0xFF }, parts[0]);
        Assert.Equal(new byte[] { 0x62 }, parts[1]);
        Assert.Equal(new byte[] { 0, 0xFF, 0, 0xFF, 0x63 }, parts[2]);
    }

    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData(new byte[] { 0x61, 0x00 })]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x61, 0x00, 0x01, 0x62, 0x00 })]
    public void AZeroThatEndsTheKeyIsNotReadPastTheEnd(byte[] key)
    {
        var position = 0;
        var read = 0;
        while (CompositeKey.TryReadPart(key, ref position, out _))
            read++;

        Assert.InRange(read, 1, 2);
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void ValuesOfDifferentFieldsMayComeInAnyOrder()
    {
        using var ordered = new CompositeKeyBuilder(3);
        using var mixed = new CompositeKeyBuilder(3);
        foreach (var (field, value) in new[] { (0, "a"), (0, "b"), (1, "x"), (2, "1"), (2, "2") })
            ordered.Add(field, Encoding.UTF8.GetBytes(value));
        foreach (var (field, value) in new[] { (2, "1"), (0, "a"), (1, "x"), (2, "2"), (0, "b") })
            mixed.Add(field, Encoding.UTF8.GetBytes(value));

        Assert.Equal(4, ordered.Combinations);
        Assert.Equal(4, mixed.Combinations);
        Assert.Equal(Enumerable.Range(0, 4).Select(c => Convert.ToHexString(ordered.Combination(c))).Order(),
            Enumerable.Range(0, 4).Select(c => Convert.ToHexString(mixed.Combination(c))).Order());
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void TooManyCombinationsThrowTheTypedException()
    {
        using var builder = new CompositeKeyBuilder(2);
        for (var i = 0; i < 256; i++)
        {
            builder.Add(0, "x"u8);
            builder.Add(1, "y"u8);
        }

        Assert.Equal(CompositeKeyBuilder.MaxCombinations, builder.Combinations); // the cap itself is allowed

        builder.Add(0, "x"u8);
        var thrown = Assert.Throws<CompositeKeyLimitException>(() => builder.Combinations);
        Assert.True(thrown.Combinations > CompositeKeyBuilder.MaxCombinations);
        Assert.IsAssignableFrom<InvalidOperationException>(thrown);
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void AnEmptyFieldGivesNoKeysWhateverTheOthersHold()
    {
        using var builder = new CompositeKeyBuilder(2);
        for (var i = 0; i < 100_000; i++)
            builder.Add(0, "x"u8);

        Assert.Equal(0, builder.Combinations);
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void FoldLowercasesOnlyTheFlaggedParts()
    {
        using var context = new ByteStringContext(SharedMultipleUseFlag.None);
        using var keys = new JoinTermScores(context, keepWinners: true);
        var tuples = new[] { ("NL", "Gent", 1f, 10L), ("nl", "Gent", 3f, 11L), ("nl", "gent", 2f, 12L), ("NL", "GENT", 5f, 13L) };
        foreach (var (country, city, score, winner) in tuples)
            keys.Add(Key(country, city), score, winner);

        string[] Folded(params bool[] fold)
        {
            using var result = CompositeJoinKeys.Fold(context, keys, fold);
            return Enumerable.Range(0, result.Count).Select(i => string.Join('|', Parts(result.Term(i)).Select(Encoding.UTF8.GetString)) + "@" + result.Score(i) + "/" + result.WinnerEntryId(i)).Order(StringComparer.Ordinal).ToArray();
        }

        // the first part folds, the second does not: Gent and gent and GENT stay three tuples
        Assert.Equal(["nl|GENT@5/13", "nl|Gent@3/11", "nl|gent@2/12"], Folded(true, false));
        Assert.Equal(["NL|gent@5/13", "nl|gent@3/11"], Folded(false, true));
        Assert.Equal(["nl|gent@5/13"], Folded(true, true));
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void FoldLowercasesLikeTheInvariantLowercase()
    {
        foreach (var text in new[] { "ABC", "Utrecht", "ÉCOLE", "Öß", "ΣΑΣ", "ЖУК", "𐐀x", "ÀÉÎÕÜ" })
        {
            var source = Encoding.UTF8.GetBytes(text);
            var folded = new byte[source.Length * 3];
            var length = CompositeKey.Fold(source, folded);
            Assert.Equal(text.ToLowerInvariant(), Encoding.UTF8.GetString(folded, 0, length));
        }

        // bytes that are not UTF-8 stay as they are
        var raw = new byte[] { 0x41, 0xFF, 0x80, 0x42 };
        var output = new byte[raw.Length * 3];
        var written = CompositeKey.Fold(raw, output);
        Assert.Equal(new byte[] { 0x61, 0xFF, 0x80, 0x62 }, output.AsSpan(0, written).ToArray());
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void TurnScoresIntoRanksKeepsTheRankOfEveryKeyNextToItsRelevance()
    {
        using var context = new ByteStringContext(SharedMultipleUseFlag.None);
        using var scan = new JoinTermScores(context, keepRanks: true);
        using var relevance = new JoinTermScores(context);
        var terms = new[] { "d", "a", "c", "b" };
        for (var i = 0; i < terms.Length; i++)
            scan.Add(Encoding.UTF8.GetBytes(terms[i]), -(i + 1)); // the scan keeps the negated rank as the score
        relevance.Add("a"u8, 0.5f);
        relevance.Add("b"u8, 0.25f);
        relevance.Add("c"u8, 0.75f);

        scan.TurnScoresIntoRanks(relevance);

        for (var i = 0; i < terms.Length; i++)
        {
            Assert.Equal(i + 1, scan.Rank(i));
            var expected = terms[i] switch { "a" => 0.5f, "b" => 0.25f, "c" => 0.75f, _ => Bm25Relevance.InitialScoreValue };
            Assert.Equal(expected, scan.Score(i));
        }
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void TurnScoresIntoRanksNeedsRoomForTheRanksOfEveryKey()
    {
        using var context = new ByteStringContext(SharedMultipleUseFlag.None);
        using var relevance = new JoinTermScores(context);

        using var withoutRanks = new JoinTermScores(context);
        withoutRanks.Add("a"u8, -1f);
        Assert.Throws<InvalidOperationException>(() => withoutRanks.TurnScoresIntoRanks(relevance));

        using var misaligned = new JoinTermScores(context, keepRanks: true);
        misaligned.Add("a"u8, -1f, 7f); // a key that has a rank already: the ranks would shift against the keys
        misaligned.Add("b"u8, -2f, 8f);
        Assert.Throws<InvalidOperationException>(() => misaligned.TurnScoresIntoRanks(relevance));

        using var plain = new JoinTermScores(context);
        plain.Add("a"u8, -2f);
        plain.Add("b"u8, -1f);
        plain.TurnScoresIntoRanks(null);
        Assert.True(plain.ScoresAreRanks);
        Assert.Equal(2f, plain.Score(0));
        Assert.Equal(1f, plain.Score(1));
    }
}
