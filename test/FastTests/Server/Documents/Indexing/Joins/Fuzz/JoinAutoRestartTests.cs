using System.Collections.Generic;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Auto;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Sparrow;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Fuzz;

/// <summary>A join index whose mode Auto resolved to Indirect when its file was built is still Indirect, for the plan and for the file, when the database is started again.</summary>
public class JoinAutoRestartTests(ITestOutputHelper output) : RavenTestBase(output)
{
    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFileThatAutoBuiltIndirectIsIndirectForThePlanAfterARestart(bool idJoin)
    {
        var settings = new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Join.IndirectEnabled)] = true.ToString(),
            [RavenConfiguration.GetKey(x => x.Join.AutoMinimumAvailableMemory)] = "100"
        };

        using JoinIndexHarness h = idJoin
            ? await JoinIndexHarness.CreateAsync(this, settings, putJoin: false, runInMemory: false)
            : await JoinIndexHarness.CreateFieldJoinAsync(this, settings, putJoin: false, runInMemory: false);
        SetInputs(h);
        for (int k = 0; k < 3; k++)
        {
            if (idJoin)
                h.PutContext(k, "nl", "n" + k, k, true, "w" + k);
            else
                h.PutCodedContext(k, $"k{k}", "nl", "n" + k, k, true, "w" + k);
        }

        for (int i = 0; i < 120; i++)
            h.PutConversation(i, idJoin ? $"contexts/{i % 3}" : $"k{i % 3}", "t" + i % 3);

        await h.PutJoinAsync(idJoin ? JoinIndexHarness.JoinIndexDefinition() : JoinIndexHarness.FieldJoinIndexDefinition());
        await h.AssertCurrentAsync();
        Assert.Equal(JoinStorageMode.Indirect, h.Join().Plan.BuildMode);

        await h.Store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(h.Store.Database, disable: true));
        await h.Store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(h.Store.Database, disable: false));
        await h.AttachAsync();
        SetInputs(h);
        h.PutConversation(1_000, idJoin ? "contexts/0" : "k0", "after");
        await h.AssertCurrentAsync();

        Assert.True(h.Join().TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
        using (snapshot)
            Assert.Equal(JoinStorageMode.Indirect, snapshot.Schema.Mode);

        Assert.Equal(JoinStorageMode.Indirect, h.Join().Plan.BuildMode);
    }

    private static void SetInputs(JoinIndexHarness h)
    {
        AutoJoinManager hooks = h.Database.IndexStore.AutoJoins;
        hooks.AvailableMemoryForTesting = () => new Size(64L << 30, SizeUnit.Bytes);
        hooks.ResidentInputsForTesting = () => new JoinOptions.ResidentInputs(1L << 30, false);
    }
}
