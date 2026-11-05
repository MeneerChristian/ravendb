using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Corax;
using Corax.Analyzers;
using Corax.Mappings;
using Corax.Querying;
using FastTests.Voron;
using Sparrow;
using Sparrow.Server;
using Sparrow.Threading;
using Tests.Infrastructure;
using Voron;
using Voron.Data.CompactTrees;
using Voron.Impl;
using Xunit;
using IndexSearcher = Corax.Querying.IndexSearcher;
using IndexWriter = Corax.Indexing.IndexWriter;

namespace FastTests.Corax;

// Keys that are kept encoded by the dictionary of another environment are looked up in a tree as they are when the table of its dictionary is the same, and decoded when it is not
public class JoinEncodedKeysCoraxTests : StorageTest
{
    private const int Docs = 30_000;
    private const int IdField = 0, OwnerField = 1, LowerField = 2;

    private static readonly JoinKeyLookupStrategy[] Strategies = [JoinKeyLookupStrategy.PerKey, JoinKeyLookupStrategy.Cursor, JoinKeyLookupStrategy.Sweep, JoinKeyLookupStrategy.Auto];

    private readonly ByteStringContext _analyzers = new(SharedMultipleUseFlag.None);
    private readonly List<string> _owners = new();
    private readonly List<IDisposable> _disposables = new();

    public JoinEncodedKeysCoraxTests(ITestOutputHelper output) : base(output)
    {
        // an index gets its dictionary before its first tree
        CreateDictionary(Env, trainOn: null);
        Index();
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
            disposable.Dispose();
        _analyzers.Dispose();
        await base.DisposeAsync();
    }

    public enum Inner
    {
        // the default table, like an index that was created on an empty database
        SameTable,

        // a table trained on other data
        OtherTable
    }

    private sealed class Samples(List<byte[]> items) : IReadOnlySpanEnumerator
    {
        private int _index;
        public int Count => items.Count;
        public void Reset() => _index = 0;

        public bool MoveNext(out ReadOnlySpan<byte> result)
        {
            if (_index >= items.Count)
            {
                result = default;
                return false;
            }

            result = items[_index++];
            return true;
        }
    }

    private static void CreateDictionary(StorageEnvironment env, List<byte[]> trainOn)
    {
        using var tx = env.WriteTransaction();
        if (trainOn == null || PersistentDictionary.TryCreate(tx.LowLevelTransaction, new Samples(trainOn), out _) == false)
            PersistentDictionary.CreateDefault(tx.LowLevelTransaction);
        tx.Commit();
    }

    // the environment of an index that joins: only its dictionary matters
    private (Transaction Transaction, long DictionaryId) InnerEnvironment(Inner inner)
    {
        var env = new StorageEnvironment(StorageEnvironmentOptions.CreateMemoryOnlyForTests());
        _disposables.Add(env);
        List<byte[]> training = null;
        if (inner == Inner.OtherTable)
        {
            var random = new Random(3);
            training = Enumerable.Range(0, 4000).Select(i => Encoding.UTF8.GetBytes($"zz-{random.Next():x}/{i % 97}/{new string((char)('a' + i % 26), 1 + i % 9)}")).ToList();
        }

        CreateDictionary(env, training);
        var transaction = env.ReadTransaction();
        _disposables.Add(transaction);
        return (transaction, CompactTree.GetDictionaryId(transaction.LowLevelTransaction));
    }

    private JoinTermScores Encode(Inner inner, IReadOnlyList<byte[]> keys, Func<int, float> score, out JoinTermScores decoded)
    {
        var (transaction, dictionaryId) = InnerEnvironment(inner);
        var llt = transaction.LowLevelTransaction;
        var encoded = JoinTermScores.CreateEncoded(Allocator, llt, dictionaryId);
        decoded = new JoinTermScores(Allocator);
        _disposables.Add(encoded);
        _disposables.Add(decoded);
        for (var i = 0; i < keys.Count; i++)
        {
            using var scope = new CompactKeyCacheScope(llt, keys[i], dictionaryId);
            var bytes = scope.Key.EncodedWith(dictionaryId, out var bits);
            encoded.AddEncoded(bytes, bits, score(i));
            decoded.Add(keys[i], score(i));
        }

        return encoded;
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void TheTableHashIsTheSameForTheSameTableOnly()
    {
        var outer = OuterTableHash();
        var same = InnerEnvironment(Inner.SameTable).Transaction.LowLevelTransaction;
        Assert.Equal(outer, PersistentDictionary.GetTableHash(same, CompactTree.GetDictionaryId(same)));
        var other = InnerEnvironment(Inner.OtherTable).Transaction.LowLevelTransaction;
        Assert.NotEqual(outer, PersistentDictionary.GetTableHash(other, CompactTree.GetDictionaryId(other)));
    }

    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData(Inner.SameTable, "Owner", true)]
    [InlineData(Inner.SameTable, "Lower", false)]
    [InlineData(Inner.OtherTable, "Owner", true)]
    [InlineData(Inner.OtherTable, "Lower", false)]
    public void ScoreMapOfEncodedKeysIsTheSameForEveryStrategy(Inner inner, string field, bool termsAreIndexedTerms)
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata(field);
        var universe = TermsOfTree(field).Concat(Enumerable.Range(0, 400).Select(i => Encoding.UTF8.GetBytes($"missing/{i}"))).ToList();
        var random = new Random(field.Length + (int)inner);

        foreach (var density in new[] { 0.001, 0.01, 0.1, 0.5, 1.0 })
        {
            var picked = Shuffle(universe.Where(_ => random.NextDouble() < density).ToList(), random);
            var scores = picked.Select(_ => 0.1f + (float)random.NextDouble() * 3.7f).ToArray();
            using var tx = Env.ReadTransaction();
            var tree = tx.ReadTree(Constants.IndexWriter.FieldsSlice).CompactTreeFor(field);
            var encoded = Encode(inner, picked, i => scores[i], out var decoded);

            Assert.Equal(inner == Inner.SameTable, searcher.CanLookUpEncodedKeys(tree, encoded, useEncodedKeys: true));
            Assert.False(searcher.CanLookUpEncodedKeys(tree, encoded, useEncodedKeys: false));

            foreach (var strategy in Strategies)
            {
                JoinKeyLookupSettings.Forced = strategy;
                try
                {
                    var expected = Run(searcher, metadata, decoded, termsAreIndexedTerms, useEncodedKeys: false);
                    var probed = Run(searcher, metadata, encoded, termsAreIndexedTerms, useEncodedKeys: true);
                    var decodedAgain = Run(searcher, metadata, encoded, termsAreIndexedTerms, useEncodedKeys: false);
                    var label = $"{field} {inner} {density:P1} {strategy}";
                    Assert.True(expected.Ids.SequenceEqual(probed.Ids) && expected.Scores.SequenceEqual(probed.Scores), $"{label}: encoded probe differs ({expected.Ids.Length} against {probed.Ids.Length})");
                    Assert.True(expected.Ids.SequenceEqual(decodedAgain.Ids) && expected.Scores.SequenceEqual(decodedAgain.Scores), $"{label}: decoded keys of an encoded list differ");
                }
                finally
                {
                    JoinKeyLookupSettings.Forced = JoinKeyLookupStrategy.Auto;
                }
            }
        }
    }

    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData(Inner.SameTable)]
    [InlineData(Inner.OtherTable)]
    public void InQueryOfEncodedKeysIsTheSameForEveryStrategy(Inner inner)
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata("Owner");
        var universe = TermsOfTree("Owner");
        var random = new Random(9);
        foreach (var density in new[] { 0.01, 0.2, 1.0 })
        {
            var picked = Shuffle(universe.Where(_ => random.NextDouble() < density).ToList(), random);
            Encode(inner, picked, _ => 1f, out var decoded);
            var encoded = Encode(inner, picked, _ => 1f, out _);
            foreach (var strategy in Strategies)
            {
                var expected = InEntries(searcher, metadata, decoded, strategy, useEncodedKeys: false);
                Assert.NotEmpty(expected);
                Assert.True(expected.SequenceEqual(InEntries(searcher, metadata, encoded, strategy, useEncodedKeys: true)), $"{inner} {density:P0} {strategy}");
            }
        }
    }

    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData(Inner.SameTable)]
    [InlineData(Inner.OtherTable)]
    public void EncodedKeysAreSortedLikeTheDecodedKeys(Inner inner)
    {
        var random = new Random(21);
        var keys = new HashSet<string>();
        // ids that share a long start, a key that is the start of another, GUIDs, and short and odd ones
        for (var i = 0; i < 6000; i++)
        {
            keys.Add($"conversationcontexts/{random.Next(100_000)}-a");
            keys.Add($"conversationcontexts/{Guid.NewGuid()}");
            keys.Add(new string((char)('a' + random.Next(3)), random.Next(1, 14)));
            keys.Add(new string(Enumerable.Range(0, random.Next(1, 30)).Select(_ => (char)random.Next(1, 0x250)).ToArray()));
        }

        keys.Add("a");
        keys.Add("aa");
        keys.Add("aaa");
        keys.Add("\u0001");
        keys.Add("ä");

        var picked = Shuffle(keys.Select(Encoding.UTF8.GetBytes).ToList(), random);
        var encoded = Encode(inner, picked, i => i, out _);

        var expected = picked.OrderBy(x => x, Comparer<byte[]>.Create((x, y) => x.AsSpan().SequenceCompareTo(y))).Select(Convert.ToHexString).ToList();
        var order = encoded.SortedOrder().ToArray();
        var actual = order.Select(i => Convert.ToHexString(encoded.Term(i))).ToList();
        Assert.Equal(expected, actual);
        Assert.Equal(picked.Count, order.Length);
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void EncodedKeysAreOrderedByTheirBytesThenTheirBits()
    {
        byte[] a = [0b1010_0000], b = [0b1010_0000, 0b1000_0000], c = [0b1010_0001];
        Assert.True(JoinTermScores.CompareEncoded(a, 3, a, 4) < 0);
        Assert.True(JoinTermScores.CompareEncoded(a, 4, a, 3) > 0);
        Assert.Equal(0, JoinTermScores.CompareEncoded(a, 4, a, 4));
        Assert.True(JoinTermScores.CompareEncoded(a, 8, b, 9) < 0);
        Assert.True(JoinTermScores.CompareEncoded(b, 9, c, 8) < 0);
        Assert.True(JoinTermScores.CompareEncoded(c, 8, b, 9) > 0);
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void EncodedKeysAreFoundByTheirDecodedTerm()
    {
        var keys = Enumerable.Range(0, 500).Select(i => Encoding.UTF8.GetBytes($"users/{i * 7}")).ToList();
        var encoded = Encode(Inner.OtherTable, keys, i => i, out _);
        foreach (var key in keys)
        {
            Assert.True(encoded.TryFind(key, out var index));
            Assert.True(encoded.Term(index).SequenceEqual(key));
            Assert.Equal(keys.FindIndex(x => x.AsSpan().SequenceEqual(key)), index);
        }

        Assert.False(encoded.TryFind("users/8"u8, out _));
    }

    // Keys that are encoded start alike for longer than the window the sort looks at, so many of them tie on it and are put in order by the whole key
    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData(Inner.SameTable)]
    [InlineData(Inner.OtherTable)]
    public void SortingEncodedKeysAllocatesNoMoreThanSortingDecodedKeys(Inner inner)
    {
        const int count = 60_000;
        var random = new Random(5);
        var keys = Enumerable.Range(0, count)
            .Select(_ => Encoding.UTF8.GetBytes($"conversationcontexts/{new Guid(random.Next(), (short)random.Next(), (short)random.Next(), 1, 2, 3, 4, 5, 6, 7, 8)}"))
            .ToList();

        long Allocated(JoinTermScores list)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            list.SortedOrder();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        long decodedBytes = 0, encodedBytes = 0;
        // the first rounds fill the pools of the arrays that the sort borrows
        for (var round = 0; round < 3; round++)
        {
            var encoded = Encode(inner, keys, _ => 1f, out var decoded);
            decodedBytes = Allocated(decoded);
            encodedBytes = Allocated(encoded);
        }

        Assert.True(encodedBytes <= decodedBytes + 64 * 1024, $"sorting {count} encoded keys allocated {encodedBytes} bytes, the decoded keys {decodedBytes}");
    }

    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData(Inner.SameTable)]
    [InlineData(Inner.OtherTable)]
    public void LookingUpEncodedKeysAllocatesNoMoreThanLookingUpDecodedKeys(Inner inner)
    {
        using var searcher = new IndexSearcher(Env, CreateKnownFields(Allocator));
        var metadata = searcher.GetFieldMetadata("Owner");
        var picked = Shuffle(TermsOfTree("Owner"), new Random(8));
        Assert.True(picked.Count > 10_000);

        long Allocated(JoinTermScores keys, bool useEncodedKeys, bool inQuery)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            if (inQuery)
            {
                var match = searcher.InQuery(metadata, keys, keysAreIndexedTerms: true, useEncodedKeys: useEncodedKeys);
                var buffer = new long[256];
                while (match.Fill(buffer) > 0)
                {
                }
            }
            else
            {
                searcher.ScoreMap(metadata, keys, termsAreIndexedTerms: true, useEncodedKeys: useEncodedKeys);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        foreach (var inQuery in new[] { false, true })
        {
            long decodedBytes = 0, encodedBytes = 0;
            for (var round = 0; round < 3; round++)
            {
                var encoded = Encode(inner, picked, _ => 1f, out var decoded);

                // the order of the keys is built by the first reader and sorting is measured by its own test
                decoded.SortedOrder();
                encoded.SortedOrder();
                decodedBytes = Allocated(decoded, useEncodedKeys: false, inQuery);
                encodedBytes = Allocated(encoded, useEncodedKeys: true, inQuery);
            }

            Assert.True(encodedBytes <= decodedBytes + 64 * 1024, $"{(inQuery ? "InQuery" : "ScoreMap")} of {picked.Count} encoded keys allocated {encodedBytes} bytes, the decoded keys {decodedBytes}");
        }
    }

    private ulong OuterTableHash()
    {
        using var tx = Env.ReadTransaction();
        return PersistentDictionary.GetTableHash(tx.LowLevelTransaction, CompactTree.GetDictionaryId(tx.LowLevelTransaction));
    }

    private static List<byte[]> Shuffle(List<byte[]> list, Random random)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    private static (long[] Ids, int[] Scores) Run(IndexSearcher searcher, in FieldMetadata metadata, JoinTermScores keys, bool termsAreIndexedTerms, bool useEncodedKeys)
    {
        var map = searcher.ScoreMap(metadata, keys, termsAreIndexedTerms, useEncodedKeys: useEncodedKeys);
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

    private static List<long> InEntries(IndexSearcher searcher, in FieldMetadata metadata, JoinTermScores keys, JoinKeyLookupStrategy strategy, bool useEncodedKeys)
    {
        JoinKeyLookupSettings.Forced = strategy;
        try
        {
            var match = searcher.InQuery(metadata, keys, keysAreIndexedTerms: true, useEncodedKeys: useEncodedKeys);
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

    private List<byte[]> TermsOfTree(string field)
    {
        using var tx = Env.ReadTransaction();
        var tree = tx.ReadTree(Constants.IndexWriter.FieldsSlice).CompactTreeFor(field);
        var terms = new List<byte[]>();
        var it = tree.Iterate();
        it.Reset();
        while (it.MoveNext(out var key, out _, out _))
        {
            var term = key.Decoded().ToArray();
            if (term.AsSpan().SequenceEqual(Constants.EmptyStringByteSpan) == false && term.AsSpan().SequenceEqual(Constants.NullValueSpan) == false)
                terms.Add(term);
        }

        return terms;
    }

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

            var h = (uint)random.Next(20_000) * 2654435761u;
            var owner = random.Next(2) == 0
                ? $"users/{random.Next(20_000)}"
                : $"users/{new Guid((int)h, (short)(h >> 7), (short)(h >> 3), 1, 2, 3, 4, 5, 6, 7, 8)}";
            builder.Write(OwnerField, null, Encoding.UTF8.GetBytes(owner));
            builder.Write(LowerField, null, Encoding.UTF8.GetBytes(owner.ToUpperInvariant()));
            _owners.Add(owner);
            builder.EndWriting();
        }

        indexWriter.Commit();
    }

    private IndexFieldsMapping CreateKnownFields(ByteStringContext bsc)
    {
        Slice.From(bsc, "Id", ByteStringType.Immutable, out var idSlice);
        Slice.From(bsc, "Owner", ByteStringType.Immutable, out var ownerSlice);
        Slice.From(bsc, "Lower", ByteStringType.Immutable, out var lowerSlice);

        using var builder = IndexFieldsMappingBuilder.CreateForWriter(false)
            .AddBinding(IdField, idSlice)
            .AddBinding(OwnerField, ownerSlice)
            .AddBinding(LowerField, lowerSlice, Analyzer.CreateLowercaseAnalyzer(_analyzers));

        return builder.Build();
    }
}
