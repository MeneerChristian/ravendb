using System;
using System.Buffers;
using System.Collections.Generic;
using Corax.Indexing;
using Corax.Mappings;
using Corax.Utils;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;
using System.Runtime.InteropServices;
using Sparrow.Server;
using Sparrow.Compression;
using Voron.Data.Containers;
using Voron.Data.PostingLists;
using Voron.Util.PFor;
using Voron;
using Voron.Data.CompactTrees;
using Voron.Util;
using Sparrow.Server.Utils.VxSort;

namespace Corax.Querying;

public partial class IndexSearcher
{
    private const int JoinPostingBufferSize = 4096;
    private const int JoinTermTableCapacity = 1024;

    public ScoreMapMatch ScoreMap(IReadOnlyList<(IQueryMatch Match, float Score)> sources)
    {
        var ids = new IdScoreCollector(Allocator);
        var buffer = ArrayPool<long>.Shared.Rent(JoinPostingBufferSize);
        try
        {
            foreach (var (match, score) in sources)
            {
                int read;
                while ((read = match.Fill(buffer)) > 0)
                {
                    for (var i = 0; i < read; i++)
                        ids.Add(buffer[i], score);
                }
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(buffer);
        }

        return BuildScoreMap(ids);
    }

    // useEncodedKeys: keys that are kept encoded are looked up as they are, when the dictionary of the tree has the table that encoded them.
    // The caller guarantees that the analyzer of the field leaves the keys unchanged; with another table the keys are decoded as before
    public ScoreMapMatch ScoreMap(in FieldMetadata field, JoinTermScores keys, bool termsAreIndexedTerms = false, float scoreFactor = 1f, bool useEncodedKeys = false)
    {
        var tree = _fieldsTree?.CompactTreeFor(field.FieldName);
        if (tree == null || keys.Count == 0)
            return BuildScoreMap(new IdScoreCollector(Allocator, 0, keys.HasRanks, keys.ScoresAreRanks)); // the field does not exist: nothing matches

        var termField = termsAreIndexedTerms ? field.ChangeAnalyzer(FieldIndexingMode.Exact) : field;
        var encoded = CanLookUpEncodedKeys(tree, keys, useEncodedKeys);
        var buffer = ArrayPool<long>.Shared.Rent(JoinPostingBufferSize);
        var drain = new PostingDrain(this, Allocator, buffer);
        var found = new ContextBoundNativeList<long>(Allocator, keys.Count);
        var foundKeys = new ContextBoundNativeList<int>(Allocator, keys.Count);
        try
        {
            // the keys are looked up in the order of their bytes
            ReadOnlySpan<int> order = keys.SortedOrder();

            // the tree is not used by anything else while the keys are looked up, so the lookup may use its cursor
            long entries = 0;
            using (var lookup = new JoinKeyLookup(this, tree, termField, termsAreIndexedTerms, keys.Count, sweepAllowed: true))
            {
                for (var k = 0; k < keys.Count; k++)
                {
                    var key = order[k];
                    var lookedUp = encoded
                        ? lookup.TryGetContainerIdEncoded(keys.EncodedTerm(key), keys.EncodedBits(key), out var containerId)
                        : lookup.TryGetContainerId(keys.Term(key), out containerId);
                    if (lookedUp == false)
                        continue;

                    found.Add(containerId);
                    foundKeys.Add(key);
                    entries += NumberOfDocumentsUnderSpecificTerm(containerId);
                }
            }

            // the collector is sized once for the entries under the keys: growing it by doubling leaves a dead block of every size behind
            var ids = new IdScoreCollector(Allocator, (int)Math.Min(entries, MaxPresizedScoreMapEntries), keys.HasRanks, keys.ScoresAreRanks);
            for (var i = 0; i < found.Count; i++)
            {
                var key = foundKeys[i];

                // the score of a join that only orders is not boosted
                var score = keys.ScoresAreRanks ? keys.Score(key) : keys.Score(key) * scoreFactor;
                var rank = keys.HasRanks ? keys.Rank(key) : 0f;
                drain.Add(found[i], score, rank, ids);
            }

            return BuildScoreMap(ids);
        }
        finally
        {
            found.Dispose();
            foundKeys.Dispose();
            drain.Dispose();
            ArrayPool<long>.Shared.Return(buffer);
        }
    }

    internal bool CanLookUpEncodedKeys(CompactTree tree, JoinTermScores keys, bool useEncodedKeys)
    {
        return useEncodedKeys && keys.IsEncoded && keys.TableHash == PersistentDictionary.GetTableHash(_transaction.LowLevelTransaction, tree.DictionaryId);
    }

    internal bool TryGetContainerIdOfJoinKey(CompactTree tree, in FieldMetadata field, ReadOnlySpan<byte> key, bool keyIsIndexedTerm, out long containerId)
    {
        // the empty string and the null are stored as markers
        if (key.Length == 0 || key.SequenceEqual(Constants.EmptyStringByteSpan))
            return tree.TryGetValue(Constants.EmptyStringByteSpan, out containerId);

        if (key.SequenceEqual(Constants.NullValueSpan))
            return tree.TryGetValue(Constants.NullValueSpan, out containerId);

        if (keyIsIndexedTerm)
            return tree.TryGetValue(key, out containerId);

        ApplyAnalyzer(field, field.Analyzer, key, out var analyzed);
        try
        {
            containerId = -1;
            return analyzed.Size != 0 && tree.TryGetValue(analyzed.AsReadOnlySpan(), out containerId);
        }
        finally
        {
            analyzed.Release(Allocator);
        }
    }

    private const int MaxPresizedScoreMapEntries = 1 << 20;

    // Reads the entry ids under the terms of a score map without a match per term
    internal interface IPostingConsumer
    {
        bool Consume(ReadOnlySpan<long> entries);
    }

    private struct PostingDrain : IDisposable
    {
        private readonly IndexSearcher _searcher;
        private readonly ByteStringContext _allocator;
        private readonly long[] _buffer;
        private PostingList _postingList;
        private FastPForBufferedReader _smallReader;

        public PostingDrain(IndexSearcher searcher, ByteStringContext allocator, long[] buffer)
        {
            _searcher = searcher;
            _allocator = allocator;
            _buffer = buffer;
            _postingList = null;
            _smallReader = default;
        }

        public unsafe void Drain<TConsumer>(long containerId, ref TConsumer consumer) where TConsumer : struct, IPostingConsumer, allows ref struct
        {
            // a term held by a single entry is stored as that entry's id
            if ((containerId & ((long)TermIdMask.PostingList | (long)TermIdMask.SmallPostingList)) == 0)
            {
                var entryId = (long)EntryIdEncodings.DecodeAndDiscardFrequency(containerId);
                consumer.Consume(MemoryMarshal.CreateReadOnlySpan(ref entryId, 1));
                return;
            }

            var buffer = _buffer;
            var llt = _searcher._transaction.LowLevelTransaction;
            if ((containerId & (long)TermIdMask.PostingList) != 0)
            {
                var setId = EntryIdEncodings.GetContainerId(containerId);
                var stateSpan = Container.GetReadOnly(llt, setId);
                ref readonly var state = ref MemoryMarshal.AsRef<PostingListState>(stateSpan);
                if (_postingList == null)
                    _postingList = new PostingList(llt, Slices.Empty, state);
                else
                    _postingList.Reopen(state);

                var it = _postingList.Iterate();
                while (it.Fill(buffer, out var read) && read > 0)
                {
                    EntryIdEncodings.DecodeAndDiscardFrequency(buffer, read);
                    if (consumer.Consume(buffer.AsSpan(0, read)) == false)
                        return;
                }
            }
            else
            {
                var smallId = EntryIdEncodings.GetContainerId(containerId);
                Container.Get(llt, smallId, out var small);
                _ = VariableSizeEncoding.Read<int>(small.Address, out var offset);
                if (_smallReader.WasInitialized == false)
                    _smallReader = new FastPForBufferedReader(_allocator);
                _smallReader.Init(small.Address + offset, small.Length - offset);

                int read;
                while ((read = ReadSmall(buffer)) > 0)
                {
                    EntryIdEncodings.DecodeAndDiscardFrequency(buffer, read);
                    if (consumer.Consume(buffer.AsSpan(0, read)) == false)
                        return;
                }
            }
        }

        public void Add(long containerId, float score, float rank, IdScoreCollector ids)
        {
            var consumer = new CollectIds { Ids = ids, Score = score, Rank = rank };
            Drain(containerId, ref consumer);
        }

        private struct CollectIds : IPostingConsumer
        {
            public IdScoreCollector Ids;
            public float Score, Rank;

            public bool Consume(ReadOnlySpan<long> entries)
            {
                foreach (var entry in entries)
                    Ids.Add(entry, Score, Rank);
                return true;
            }
        }

        private unsafe int ReadSmall(long[] buffer)
        {
            fixed (long* p = buffer)
                return _smallReader.Fill(p, buffer.Length);
        }

        public void Dispose()
        {
            if (_smallReader.WasInitialized)
                _smallReader.Dispose();
        }
    }

    private static ScoreMapMatch BuildScoreMap(IdScoreCollector collected)
    {
        var count = collected.Ids.Count;
        var ids = collected.Ids.ToSpan();
        var scores = collected.Scores.ToSpan();

        if (collected.Ranks.HasContext)
        {
            // the score and the rank of an entry travel together
            var ranks = collected.Ranks.ToSpan();
            var packed = new ContextBoundNativeList<ulong>(collected.Allocator, count);
            packed.Count = count;
            var packedSpan = packed.ToSpan();
            for (var i = 0; i < count; i++)
                packedSpan[i] = ((ulong)BitConverter.SingleToUInt32Bits(scores[i]) << 32) | BitConverter.SingleToUInt32Bits(ranks[i]);

            MemoryExtensions.Sort(ids, packedSpan);

            // the sort of the ids is not stable
            var uniqueRanked = 0;
            for (var start = 0; start < count;)
            {
                var end = start + 1;
                while (end < count && ids[end] == ids[start])
                    end++;

                if (end - start > 1)
                    packedSpan.Slice(start, end - start).Sort();

                var score = 0f;
                var rank = float.PositiveInfinity;
                for (var i = start; i < end; i++)
                {
                    score += BitConverter.UInt32BitsToSingle((uint)(packedSpan[i] >> 32));
                    rank = Math.Min(rank, BitConverter.UInt32BitsToSingle((uint)packedSpan[i]));
                }

                ids[uniqueRanked] = ids[start];
                scores[uniqueRanked] = score;
                ranks[uniqueRanked] = rank;
                uniqueRanked++;
                start = end;
            }

            packed.Dispose();
            unsafe
            {
                return new ScoreMapMatch(collected.Ids.RawItems, collected.HasScores ? collected.Scores.RawItems : null, collected.Ranks.RawItems, uniqueRanked);
            }
        }

        if (collected.HasOneScore && collected.ScoresAreRanks == false)
            return BuildScoreMapOfOneScore(collected);

        // the ids and their scores are two parallel arrays: sorting them together allocates nothing
        MemoryExtensions.Sort(ids, scores);

        // an entry can be matched several times
        var unique = 0;
        for (var start = 0; start < count;)
        {
            var end = start + 1;
            while (end < count && ids[end] == ids[start])
                end++;

            if (end - start > 1)
                scores.Slice(start, end - start).Sort();

            // the scores of an entry under several keys add up, its rank is the smallest of theirs
            var score = collected.ScoresAreRanks ? float.PositiveInfinity : 0f;
            for (var i = start; i < end; i++)
                score = collected.ScoresAreRanks ? Math.Min(score, scores[i]) : score + scores[i];

            ids[unique] = ids[start];
            scores[unique] = score;
            unique++;
            start = end;
        }

        // the match reads the collected buffers in place: they are not copied (the match keeps them, see ScoreMapMatch)
        unsafe
        {
            // the scores of a rank-only map are the ranks
            return new ScoreMapMatch(collected.Ids.RawItems, collected.Scores.RawItems, collected.ScoresAreRanks ? collected.Scores.RawItems : null, unique);
        }
    }

    private static ScoreMapMatch BuildScoreMapOfOneScore(IdScoreCollector collected)
    {
        var count = collected.Ids.Count;
        var ids = collected.Ids.ToSpan();
        var scores = collected.Scores.ToSpan();
        var one = collected.FirstScore;

        Sort.Run(ids);

        var unique = 0;
        for (var start = 0; start < count;)
        {
            var end = start + 1;
            while (end < count && ids[end] == ids[start])
                end++;

            var score = 0f;
            for (var i = start; i < end; i++)
                score += one;

            ids[unique] = ids[start];
            scores[unique] = score;
            unique++;
            start = end;
        }

        unsafe
        {
            return new ScoreMapMatch(collected.Ids.RawItems, collected.Scores.RawItems, null, unique);
        }
    }

    private sealed class IdScoreCollector
    {
        public ContextBoundNativeList<long> Ids;
        public ContextBoundNativeList<float> Scores;
        public ContextBoundNativeList<float> Ranks;
        public readonly ByteStringContext Allocator;
        public readonly bool ScoresAreRanks;

        public bool HasScores => ScoresAreRanks == false;

        public IdScoreCollector(ByteStringContext allocator, int capacity = 0, bool withRanks = false, bool scoresAreRanks = false)
        {
            Allocator = allocator;
            Ids = new ContextBoundNativeList<long>(allocator);
            Scores = new ContextBoundNativeList<float>(allocator);
            if (withRanks)
                Ranks = new ContextBoundNativeList<float>(allocator);
            if (capacity > 0)
            {
                Ids.GrowFitting(capacity + 1);
                Scores.GrowFitting(capacity + 1);
                if (withRanks)
                    Ranks.GrowFitting(capacity + 1);
            }
            ScoresAreRanks = scoresAreRanks;
        }

        private long _firstScoreBits = -1; // -1: nothing added yet
        private bool _scoresDiffer;

        // Every score added so far is the same, bit for bit
        public bool HasOneScore => _scoresDiffer == false && _firstScoreBits >= 0;
        public float FirstScore => BitConverter.UInt32BitsToSingle((uint)_firstScoreBits);

        public void Add(long id, float score, float rank = 0f)
        {
            var bits = BitConverter.SingleToUInt32Bits(score);
            if (_firstScoreBits < 0)
                _firstScoreBits = bits;
            else if (bits != _firstScoreBits)
                _scoresDiffer = true;

            JoinTermScores.Room(ref Ids, 1);
            JoinTermScores.Room(ref Scores, 1);
            Ids.Add(id);
            Scores.Add(score);
            if (Ranks.HasContext)
            {
                JoinTermScores.Room(ref Ranks, 1);
                Ranks.Add(rank);
            }
        }
    }
}
