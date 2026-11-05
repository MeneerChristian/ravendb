using System;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Raven.Server.ServerWide.Context;
using Tests.Infrastructure;
using Voron.Data.BTrees;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>The format of the file of a join index: what an index that was built before the storage modes keeps, and what a file of another format does.</summary>
public class JoinIndexFormatTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static (JoinStorageMode? Mode, long Format, int GenerationFormat) ReadFormat(JoinIndex joinIndex)
    {
        using (joinIndex._contextPool.AllocateOperationContext(out TransactionOperationContext context))
        using (context.OpenReadTransaction())
        {
            var tx = context.Transaction.InnerTransaction;
            Tree meta = tx.ReadTree(JoinNames.Meta);
            JoinIndexGeneration generation = JoinFileRows.ReadGeneration(meta, null);
            return (JoinFileRows.ReadMode(tx), JoinFileRows.ReadInt64(meta, JoinNames.FormatRow), generation.Format);
        }
    }

    private static void WriteFormatRow(JoinIndex joinIndex, long format)
    {
        using (joinIndex._contextPool.AllocateOperationContext(out TransactionOperationContext context))
        using (var tx = context.OpenWriteTransaction())
        {
            tx.InnerTransaction.ReadTree(JoinNames.Meta).Add(JoinNames.FormatRow, format);
            tx.Commit();
        }
    }

    private static async Task WaitForRebuildAsync(JoinIndexHarness h, JoinIndex before)
    {
        for (int i = 0; i < 1000 && (h.Join() is not { } now || ReferenceEquals(now, before)); i++)
            await Task.Delay(20);

        Assert.False(ReferenceEquals(h.Join(), before), "the join index was not reset");
    }

    private static async Task SeedAsync(JoinIndexHarness h)
    {
        h.PutCodedContext(1, "k1", "nl", "Alpha", 10, true, "red fox");
        h.PutCodedContext(2, "k2", "de", "Beta", 20, false, "blue whale");
        h.PutConversation(1, "k1");
        h.PutConversation(2, "k2");
        await h.AssertCurrentAsync();
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AJoinOfTheDefaultModeKeepsTheFormatOfTheBuildsBeforeTheModes(bool fieldJoin)
    {
        using JoinIndexHarness h = fieldJoin ? await JoinIndexHarness.CreateFieldJoinAsync(this) : await JoinIndexHarness.CreateAsync(this);
        if (fieldJoin)
            await SeedAsync(h);
        else
        {
            h.PutContext(1);
            h.PutConversation(1, "contexts/1");
            await h.AssertCurrentAsync();
        }

        JoinIndex joinIndex = h.Join();
        (JoinStorageMode? mode, long format, int generationFormat) = ReadFormat(joinIndex);

        Assert.Equal(JoinStorageMode.Expanded, mode);
        Assert.Equal(3, format);
        Assert.Equal(2, generationFormat);
        Assert.Equal(JoinStorageMode.Expanded, joinIndex.Plan.BuildMode);
        Assert.Equal(JoinSchema.From(joinIndex.Join).Hash, joinIndex.Plan.Schema.Hash);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AFileThatExistsIsOpenedAgainWithoutABuild()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this);
        await SeedAsync(h);
        JoinIndex joinIndex = h.Join();

        await h.Store.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Indexes.StopIndexOperation(JoinIndexHarness.JoinName));
        h.PutConversation(3, "k1");
        await h.Store.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Indexes.StartIndexOperation(JoinIndexHarness.JoinName));
        await h.AssertCurrentAsync();

        Assert.True(ReferenceEquals(h.Join(), joinIndex), "the join index was reset");
        Assert.Equal(1, joinIndex.Counters.Builds);
        Assert.Empty(joinIndex.Counters.RebuildsByReason.Keys.Where(x => x != nameof(JoinRebuildReason.Initial)));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AFileOfAnotherFormatIsBuiltAgainInsteadOfBeingReadAsWhatItIsNot()
    {
        // a definition that names Expanded does not take the mode of a file
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this, putJoin: false);
        IndexDefinition definition = JoinIndexHarness.FieldJoinIndexDefinition();
        definition.Join.StorageMode = JoinStorageMode.Expanded;
        await h.PutJoinAsync(definition);
        await SeedAsync(h);
        JoinIndex before = h.Join();

        // the format row of a file that holds keys in its atom lists
        WriteFormatRow(before, JoinStorageFormat.IndirectVersion);
        h.PutConversation(3, "k2");

        await WaitForRebuildAsync(h, before);
        await h.AssertCurrentAsync();

        Assert.Contains(nameof(JoinRebuildReason.FormatChanged), before.Counters.RebuildsByReason.Keys);
        (JoinStorageMode? mode, long format, _) = ReadFormat(h.Join());
        Assert.Equal(JoinStorageMode.Expanded, mode);
        Assert.Equal(JoinStorageFormat.Version, format);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AFormatThatThisBuildDoesNotKnowIsBuiltAgainToo()
    {
        using JoinIndexHarness h = await JoinIndexHarness.CreateFieldJoinAsync(this);
        await SeedAsync(h);
        JoinIndex before = h.Join();

        WriteFormatRow(before, 99);
        h.PutConversation(3, "k2");

        await WaitForRebuildAsync(h, before);
        await h.AssertCurrentAsync();
        Assert.Contains(nameof(JoinRebuildReason.FormatChanged), before.Counters.RebuildsByReason.Keys);
    }
}
