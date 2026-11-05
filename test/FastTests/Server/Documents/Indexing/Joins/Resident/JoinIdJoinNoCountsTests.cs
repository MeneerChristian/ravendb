using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Server;
using Tests.Infrastructure;
using Voron.Util;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

/// <summary>
/// The group of a key of an Indirect join on the id is one inner document, so the file keeps no counts of pairs of a key and an atom: the atoms of the key are the atoms of its document, and the
/// invariant checker compares them with the inner index after every change (a change of the atoms, a delete, a re-create, a new document).
/// </summary>
public class JoinIdJoinNoCountsTests(ITestOutputHelper output) : RavenTestBase(output), IIndirectModeTest
{
    private static (long Counts, bool KeysAreIds, int Atoms) Look(JoinIndexHarness h, string key)
    {
        Assert.True(h.Join().TryOpenJoinSnapshot(out JoinIndexSnapshot file));
        using (file)
        {
            var codes = new ContextBoundNativeList<long>(file.Transaction.Allocator);
            try
            {
                int atoms = 0;
                if (file.TryGetKey(System.Text.Encoding.UTF8.GetBytes(key), out long keyRef))
                {
                    file.ReadKeyAtoms(keyRef, ref codes);
                    atoms = codes.Count;
                }

                return (file.NumberOfPairCounts, file.Schema.KeysAreInnerIds, atoms);
            }
            finally
            {
                codes.Dispose();
            }
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheFileKeepsNoCountsWhateverTheInnerDocumentsDoAndStaysEqualToTheIndexes()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(this);
        for (int i = 0; i < 30; i++)
            h.PutContext(i, i % 2 == 0 ? "nl" : "de", "n" + i % 4, i, i % 3 != 0, "w" + i);

        for (int i = 0; i < 90; i++)
            h.PutConversation(i, "contexts/" + i % 35);

        await h.AssertCurrentAsync();
        (long counts, bool keysAreIds, int atoms) = Look(h, "contexts/3");
        Assert.True(keysAreIds);
        Assert.Equal(0, counts);
        Assert.True(atoms > 0);

        h.PutContext(3, "us", "n9", 99, false, "other");
        h.Delete("contexts/4");
        h.PutContext(4, "fr", "n8", 5, true, "again");
        h.Delete("contexts/5");
        h.PutContext(31, "nl", "n1", 7, true, "late");
        h.PutContext(33, "nl", "n1", 7, true, "late");
        await h.AssertCurrentAsync();

        Assert.Equal((0L, true), (Look(h, "contexts/3").Counts, Look(h, "contexts/3").KeysAreIds));
        Assert.True(Look(h, "contexts/3").Atoms > 0);
        Assert.True(Look(h, "contexts/4").Atoms > 0);
        Assert.Equal(0, Look(h, "contexts/5").Atoms);
        Assert.True(Look(h, "contexts/31").Atoms > 0);
        Assert.Equal(0, Look(h, "contexts/34").Atoms);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinOnAFieldStillKeepsItsCounts()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this);
        for (int i = 0; i < 6; i++)
            h.PutCodedContext(i, "k" + i % 3, "nl", "n" + i % 2, i, true, "w" + i);

        await h.AssertCurrentAsync();
        (long counts, bool keysAreIds, _) = Look(h, "k1");
        Assert.False(keysAreIds);
        Assert.True(counts > 0);
    }
}
