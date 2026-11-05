using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Raven.Server.ServerWide.Context;
using Voron.Data.BTrees;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

/// <summary>
/// What an Indirect join index does for the writes that change nothing about a key: an outer entry that keeps its keys is known without looking them up in the dictionary of the keys, an inner document keeps
/// the key that is its id, and the atoms that a refresh moves for a key are one update of its list. Every test also compares the file with the indexes after the change.
/// </summary>
public class JoinIndirectWriteWorkTests(ITestOutputHelper output) : RavenTestBase(output), IIndirectModeTest
{
    private static long FingerprintsOf(JoinIndexHarness h)
    {
        Assert.True(h.Join().TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
        using (snapshot)
            return snapshot.NumberOfOuterFingerprints;
    }

    private static string[] Keys(int i, int shift = 0) => [$"k{(i + shift) % 11}", $"k{(i + 3 + shift) % 11}", $"k{(i + 7 + shift) % 11}"];

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnOuterEntryThatKeepsItsKeysIsKnownWithoutLookingThemUpAndAnotherSetIsNot()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, arrays: true);
        for (int i = 0; i < 11; i++)
            h.PutCodedContextKeys(i, [$"k{i}"], country: i % 2 == 0 ? "nl" : "de");

        for (int i = 0; i < 40; i++)
            h.PutConversationKeys(i, Keys(i), "t0");

        await h.AssertCurrentAsync();
        long before = h.Join().Counters.KeyLookups;

        // the build of the file wrote the fingerprint of the keys of every row: the first change of a row that keeps its keys looks nothing up
        for (int i = 0; i < 40; i++)
            h.PutConversationKeys(i, Keys(i), "t0b");

        await h.AssertCurrentAsync();
        Assert.Equal(0, h.Join().Counters.KeyLookups - before);
        before = h.Join().Counters.KeyLookups;

        // the keys stay as a set: the same order, another order, with a repeat
        for (int i = 0; i < 40; i++)
        {
            string[] keys = Keys(i);
            h.PutConversationKeys(i, i % 3 == 0 ? keys : i % 3 == 1 ? [keys[2], keys[0], keys[1]] : [keys[1], keys[1], keys[2], keys[0]], "t1");
        }

        await h.AssertCurrentAsync();
        Assert.Equal(0, h.Join().Counters.KeyLookups - before);

        // one key of three is another one, a key that the file does not know, and a key less
        h.PutConversationKeys(0, [Keys(0)[0], Keys(0)[1], "k10"], "t2");
        h.PutConversationKeys(1, [Keys(1)[0], Keys(1)[1], "unknown"], "t2");
        h.PutConversationKeys(2, [Keys(2)[0], Keys(2)[1]], "t2");
        h.PutConversationKeys(3, [Keys(3)[0]], "t2");
        await h.AssertCurrentAsync();
        Assert.True(h.Join().Counters.KeyLookups - before >= 4);

        // and each of those again with the keys it has now: nothing to look up for the ones with two or more keys
        long middle = h.Join().Counters.KeyLookups;
        h.PutConversationKeys(0, [Keys(0)[0], Keys(0)[1], "k10"], "t3");
        h.PutConversationKeys(1, ["unknown", Keys(1)[1], Keys(1)[0]], "t3");
        h.PutConversationKeys(2, [Keys(2)[1], Keys(2)[0]], "t3");
        await h.AssertCurrentAsync();
        Assert.Equal(0, h.Join().Counters.KeyLookups - middle);

        // whichever key of three is another one, the row follows (the fingerprint is of all of them)
        for (int j = 0; j < 3; j++)
        {
            for (int i = 10; i < 20; i++)
            {
                string[] keys = Keys(i);
                keys[j] = $"k{(i + j) % 5}x";
                h.PutConversationKeys(i, keys, "t6");
            }

            await h.AssertCurrentAsync();
        }

        // a second key appears and disappears again for an entry that had one
        h.PutConversationKeys(3, [Keys(3)[0], "k5"], "t4");
        await h.AssertCurrentAsync();
        h.PutConversationKeys(3, [Keys(3)[0]], "t5");
        await h.AssertCurrentAsync();
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnOuterEntryOfOneKeyThatKeepsItIsKnownWithoutLookingItUpFromTheBuildOnAndTheRowFollowsAChange()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this);
        for (int i = 0; i < 11; i++)
            h.PutCodedContext(i, $"k{i}", i % 2 == 0 ? "nl" : "de");

        for (int i = 0; i < 60; i++)
            h.PutConversation(i, $"k{i % 11}", "t0");

        await h.AssertCurrentAsync();
        long before = h.Join().Counters.KeyLookups;
        Assert.Equal(60, FingerprintsOf(h));

        // the first change after the build, and the next one: the key stays, the other fields change
        for (int round = 1; round <= 2; round++)
        {
            for (int i = 0; i < 60; i++)
                h.PutConversation(i, $"k{i % 11}", "t" + round);

            await h.AssertCurrentAsync();
            Assert.Equal(0, h.Join().Counters.KeyLookups - before);
        }

        // a key that is another one, a key that the file does not know, none (the row and its fingerprint leave), one again, two, one again
        h.PutConversation(0, "k5", "t3");
        h.PutConversation(1, "unknown", "t3");
        h.PutConversationKeys(2, [], "t3");
        await h.AssertCurrentAsync();
        h.PutConversation(2, "k4", "t4");
        h.PutConversationKeys(3, ["k1", "k2"], "t4");
        await h.AssertCurrentAsync();
        h.PutConversation(3, "k7", "t5");
        h.PutConversationKeys(4, ["k1", "k2"], "t5");
        h.Delete("conversations/5");
        await h.AssertCurrentAsync();

        // and the new keys are known again without a look-up
        before = h.Join().Counters.KeyLookups;
        h.PutConversation(0, "k5", "t6");
        h.PutConversation(1, "unknown", "t6");
        h.PutConversation(2, "k4", "t6");
        h.PutConversation(3, "k7", "t6");
        h.PutConversationKeys(4, ["k2", "k1"], "t6");
        await h.AssertCurrentAsync();
        Assert.Equal(0, h.Join().Counters.KeyLookups - before);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnIndirectFileThatWasBuiltBeforeTheFingerprintsOfEveryRowIsBuiltAgainAndThenHasThem()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, arrays: true);
        for (int i = 0; i < 11; i++)
            h.PutCodedContextKeys(i, [$"k{i}"], country: i % 2 == 0 ? "nl" : "de");

        for (int i = 0; i < 40; i++)
        {
            if (i % 2 == 0)
                h.PutConversationKeys(i, [$"k{i % 11}"], "t0");
            else
                h.PutConversationKeys(i, Keys(i), "t0");
        }

        await h.AssertCurrentAsync();
        JoinIndex before = h.Join();
        Assert.Equal(40, FingerprintsOf(h));

        // the schema row of a file of the older build: the hash of its schema was another one (its text did not have the fingerprints of the rows)
        long current;
        using (before._contextPool.AllocateOperationContext(out TransactionOperationContext context))
        using (var tx = context.OpenWriteTransaction())
        {
            Tree meta = tx.InnerTransaction.ReadTree(JoinNames.Meta);
            current = JoinFileRows.ReadInt64(meta, JoinNames.SchemaRow);
            meta.Add(JoinNames.SchemaRow, -5737771713680885206L);
            tx.Commit();
        }

        Assert.NotEqual(-5737771713680885206L, current);
        h.PutConversationKeys(0, ["k3"], "t1");
        for (int i = 0; i < 1000 && ReferenceEquals(h.Join(), before); i++)
            await Task.Delay(20);

        Assert.False(ReferenceEquals(h.Join(), before), "the join index was not reset");
        await h.AssertCurrentAsync();
        Assert.Contains(nameof(JoinRebuildReason.FormatChanged), before.Counters.RebuildsByReason.Keys);
        Assert.Equal(40, FingerprintsOf(h));

        // and the rebuilt file knows what it keeps
        long lookups = h.Join().Counters.KeyLookups;
        for (int i = 0; i < 40; i++)
            h.PutConversationKeys(i, i % 2 == 0 ? [i == 0 ? "k3" : $"k{i % 11}"] : Keys(i), "t2");

        await h.AssertCurrentAsync();
        Assert.Equal(0, h.Join().Counters.KeyLookups - lookups);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AnInnerDocumentThatChangesItsAtomsKeepsItsKeyAndTheAtomsOfTheKeyAreOneUpdateOfItsList()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        for (int i = 0; i < 30; i++)
            h.PutContext(i, "nl", "n" + i % 4, i, true, "w" + i);

        for (int i = 0; i < 60; i++)
            h.PutConversation(i, "contexts/" + i % 30);

        await h.AssertCurrentAsync();
        long lookups = h.Join().Counters.KeyLookups;
        long updates = h.Join().Counters.KeyAtomUpdates;

        // the country and the rank atoms of the group of every key change: two atoms leave and two come, for each of 30 keys
        for (int i = 0; i < 30; i++)
            h.PutContext(i, "de", "n" + i % 4, i, true, "w" + i);

        await h.AssertCurrentAsync();
        Assert.Equal(0, h.Join().Counters.KeyLookups - lookups);
        long changed = h.Join().Counters.KeyAtomUpdates - updates;
        Assert.InRange(changed, 30, 30);

        // a document that is deleted and put again has a key that has to be found again, and a new one gets its key
        h.Delete("contexts/4");
        h.PutContext(4, "fr", "n8", 5, true, "again");
        h.PutContext(31, "nl", "n1", 7, true, "late");
        await h.AssertCurrentAsync();
    }

    private static long Print(string[] terms, out int distinct)
    {
        long[] hashes = terms.Select(x => OuterKeyFingerprint.HashOf(Encoding.UTF8.GetBytes(x))).ToArray();
        distinct = OuterKeyFingerprint.Of(hashes, out long fingerprint);
        return fingerprint;
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheFingerprintOfTheKeysOfAnEntryIsOfTheSetAndOfEveryKeyInIt()
    {
        long abc = Print(["a", "b", "c"], out int distinct);
        Assert.Equal(3, distinct);
        Assert.Equal(abc, Print(["c", "a", "b"], out _));
        Assert.Equal(abc, Print(["b", "b", "c", "a", "a"], out distinct));
        Assert.Equal(3, distinct);
        Assert.NotEqual(0, abc);
        Assert.Equal(1, abc & 1);

        // one key of the set another one: whichever it is, and a key more or less
        var seen = new System.Collections.Generic.HashSet<long> { abc };
        for (int size = 2; size <= 6; size++)
        {
            string[] terms = Enumerable.Range(0, size).Select(x => $"key-{size}-{x}").ToArray();
            long original = Print(terms, out _);
            for (int i = 0; i < size; i++)
            {
                string[] other = (string[])terms.Clone();
                other[i] = "another-" + i;
                Assert.True(seen.Add(Print(other, out _)), $"set of {size}, key {i} changed");
            }

            Assert.True(seen.Add(original));
            Assert.True(seen.Add(Print(terms.Append("more").ToArray(), out _)));
            Assert.True(seen.Add(Print(terms.Skip(1).ToArray(), out _)));
            Assert.True(seen.Add(Print(terms.Take(size - 1).ToArray(), out _)));
        }
    }
}
