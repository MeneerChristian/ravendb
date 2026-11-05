using System;
using System.Buffers;
using Corax.Querying.Matches;
using Sparrow.Server;
using Voron;
using Voron.Data.CompactTrees;

namespace Corax.Querying;

public partial class IndexSearcher
{
    public void CompositeKeysOfEntries(ReadOnlySpan<string> fields, ReadOnlySpan<long> sortedIds, ReadOnlySpan<float> scores, JoinTermScores output)
    {
        var rootPages = ArrayPool<long>.Shared.Rent(fields.Length);
        var llt = _transaction.LowLevelTransaction;
        var key = llt.AcquireCompactKey();
        try
        {
            if (RootPagesOf(fields, rootPages) == false)
                return; // no entry has a value of one of the fields

            using var builder = new CompositeKeyBuilder(fields.Length);
            Page page = default;
            for (var i = 0; i < sortedIds.Length; i++)
            {
                var combinations = ReadFields(builder, sortedIds[i], rootPages.AsSpan(0, fields.Length), ref page, key);
                for (var c = 0; c < combinations; c++)
                    output.Add(builder.Combination(c), scores[i], sortedIds[i]);
            }
        }
        finally
        {
            llt.ReleaseCompactKey(ref key);
            ArrayPool<long>.Shared.Return(rootPages);
        }
    }

    private bool RootPagesOf(ReadOnlySpan<string> fields, long[] rootPages)
    {
        var all = true;
        for (var f = 0; f < fields.Length; f++)
        {
            rootPages[f] = FieldCache.GetLookupRootPage(fields[f]);
            all &= rootPages[f] != -1;
        }

        return all;
    }

    internal int ReadFields(CompositeKeyBuilder builder, long entryId, ReadOnlySpan<long> rootPages, ref Page page, CompactKey key)
    {
        builder.Reset();
        var reader = GetEntryTermsReader(entryId, ref page, key);
        while (reader.MoveNext())
        {
            if (reader.IsNull || reader.IsNonExisting)
                continue;

            // the same field can be on several pairs
            for (var f = 0; f < rootPages.Length; f++)
            {
                if (rootPages[f] == reader.FieldRootPage)
                    builder.Add(f, reader.Current.Decoded());
            }
        }

        return builder.Combinations;
    }

    public ScoreMapMatch ScoreMapOfCompositeKeys(Matches.Meta.IQueryMatch candidates, ReadOnlySpan<string> fields, JoinTermScores keys, float scoreFactor)
    {
        var ids = new IdScoreCollector(Allocator, 0);
        var rootPages = ArrayPool<long>.Shared.Rent(fields.Length);
        var llt = _transaction.LowLevelTransaction;
        var compactKey = llt.AcquireCompactKey();
        var buffer = ArrayPool<long>.Shared.Rent(JoinPostingBufferSize);
        try
        {
            if (RootPagesOf(fields, rootPages) && keys.Count > 0)
            {
                using var builder = new CompositeKeyBuilder(fields.Length);
                Page page = default;
                int read;
                while ((read = candidates.Fill(buffer)) > 0)
                {
                    for (var i = 0; i < read; i++)
                    {
                        var combinations = ReadFields(builder, buffer[i], rootPages.AsSpan(0, fields.Length), ref page, compactKey);
                        var best = -1;
                        for (var c = 0; c < combinations; c++)
                        {
                            if (keys.TryFind(builder.Combination(c), out var found) && (best == -1 || keys.Beats(found, best)))
                                best = found;
                        }

                        if (best != -1)
                            ids.Add(buffer[i], keys.Score(best) * scoreFactor);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(buffer);
            llt.ReleaseCompactKey(ref compactKey);
            ArrayPool<long>.Shared.Return(rootPages);
        }

        return BuildScoreMap(ids);
    }
}

public static class CompositeJoinKeys
{
    private const int KeyScratchSize = 256;

    // Unique keys from rows that may repeat a key
    public static JoinTermScores MergeBest(ByteStringContext allocator, JoinTermScores rows)
    {
        var merged = new JoinTermScores(allocator, keepWinners: true);
        var order = rows.SortedOrder();
        for (var start = 0; start < order.Length;)
        {
            var best = order[start];
            var end = start + 1;
            for (; end < order.Length && rows.Term(order[end]).SequenceEqual(rows.Term(best)); end++)
            {
                if (rows.Beats(order[end], best))
                    best = order[end];
            }

            merged.Add(rows.Term(best), rows.Score(best), rows.WinnerEntryId(best));
            start = end;
        }

        return merged;
    }

    public static JoinTermScores Fold(ByteStringContext allocator, JoinTermScores keys, ReadOnlySpan<bool> foldPart)
    {
        var rows = new JoinTermScores(allocator, keepWinners: true);
        var folded = ArrayPool<byte>.Shared.Rent(KeyScratchSize);
        var scratch = ArrayPool<byte>.Shared.Rent(KeyScratchSize);
        try
        {
            for (var k = 0; k < keys.Count; k++)
            {
                var key = keys.Term(k);
                var written = 0;
                var position = 0;
                for (var part = 0; CompositeKey.TryReadPart(key, ref position, out var text); part++)
                {
                    if (part < foldPart.Length && foldPart[part])
                    {
                        CompositeKey.Reserve(ref scratch, 0, text.Length * 3);
                        text = scratch.AsSpan(0, CompositeKey.Fold(text, scratch));
                    }

                    CompositeKey.AppendEscapedPart(ref folded, ref written, text, part == 0);
                }

                rows.Add(folded.AsSpan(0, written), keys.Score(k), keys.WinnerEntryId(k));
            }

            return MergeBest(allocator, rows);
        }
        finally
        {
            rows.Dispose();
            ArrayPool<byte>.Shared.Return(folded);
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    // The keys of a join on a field with the case folded, a key for every distinct folded form: the keys that differ in case only are one key that holds the best of their scores
    public static JoinTermScores FoldDistinct(ByteStringContext allocator, JoinTermScores keys)
    {
        var rows = new JoinTermScores(allocator, keepRanks: keys.HasRanks) { ScoresAreRanks = keys.ScoresAreRanks };
        var result = new JoinTermScores(allocator, keepRanks: keys.HasRanks) { ScoresAreRanks = keys.ScoresAreRanks };
        var scratch = ArrayPool<byte>.Shared.Rent(KeyScratchSize);
        try
        {
            for (var k = 0; k < keys.Count; k++)
            {
                var key = keys.Term(k);
                CompositeKey.Reserve(ref scratch, 0, key.Length * 3);
                var folded = scratch.AsSpan(0, CompositeKey.Fold(key, scratch));
                if (keys.HasRanks)
                    rows.Add(folded, keys.Score(k), keys.Rank(k));
                else
                    rows.Add(folded, keys.Score(k));
            }

            var order = rows.SortedOrder();
            for (var start = 0; start < order.Length;)
            {
                var term = rows.Term(order[start]);
                var score = rows.Score(order[start]);
                var rank = keys.HasRanks ? rows.Rank(order[start]) : 0f;
                var end = start + 1;
                for (; end < order.Length && rows.Term(order[end]).SequenceEqual(term); end++)
                {
                    var other = rows.Score(order[end]);
                    score = keys.ScoresAreRanks ? Math.Min(score, other) : Math.Max(score, other);
                    if (keys.HasRanks)
                        rank = Math.Min(rank, rows.Rank(order[end]));
                }

                if (keys.HasRanks)
                    result.Add(term, score, rank);
                else
                    result.Add(term, score);

                start = end;
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
        finally
        {
            rows.Dispose();
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public static JoinTermScores FirstTerms(ByteStringContext allocator, JoinTermScores keys)
    {
        var rows = new JoinTermScores(allocator);
        var result = new JoinTermScores(allocator);
        var buffer = ArrayPool<byte>.Shared.Rent(KeyScratchSize);
        try
        {
            for (var k = 0; k < keys.Count; k++)
            {
                var key = keys.Term(k);
                CompositeKey.Reserve(ref buffer, 0, key.Length);
                rows.Add(buffer.AsSpan(0, CompositeKey.FirstTerm(key, buffer)), 1f);
            }

            var order = rows.SortedOrder();
            for (var i = 0; i < order.Length; i++)
            {
                if (i == 0 || rows.Term(order[i]).SequenceEqual(rows.Term(order[i - 1])) == false)
                    result.Add(rows.Term(order[i]), 1f);
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
        finally
        {
            rows.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
