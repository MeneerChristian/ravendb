using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>
/// The storage mode Auto of a join index on a field: the file is built Indirect when the outer entries share their keys (30 or more rows per key), the resident cache that it needs fits, and the server has memory;
/// else Expanded. Not an Indirect mode test: the mode of the definition stays Auto.
/// </summary>
public class JoinAutoModeTests(ITestOutputHelper output) : RavenTestBase(output)
{
    private static JoinOptions Options(int minRows = 30, long cache = 1L << 30, long floor = 0, bool indirectEnabled = true, JoinStorageMode storageDefault = JoinStorageMode.Auto)
    {
        return new JoinOptions { IndirectEnabled = indirectEnabled, StorageModeDefault = storageDefault, AutoIndirectMinRowsPerKey = minRows, ResidentCacheMaxBytes = cache, AutoModeMinimumAvailableBytes = floor };
    }

    private static JoinDefinition Definition(JoinStorageMode mode = JoinStorageMode.Auto, string innerField = "Code") => new() { OuterIndex = "O", OuterField = "ContextId", InnerField = innerField, InnerIndex = "I", StorageMode = mode };

    private static JoinOptions.ModeFacts Facts(long entries, long keys, long available = 1L << 40, long cacheFree = 1L << 30, bool low = false) => new(entries, keys, available, cacheFree, low);

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(29, 1_000, JoinStorageMode.Expanded)]
    [InlineData(29.99, 100_000, JoinStorageMode.Expanded)]
    [InlineData(30, 1_000, JoinStorageMode.Indirect)]
    [InlineData(30.01, 100_000, JoinStorageMode.Indirect)]
    [InlineData(100, 1_000, JoinStorageMode.Indirect)]
    [InlineData(1, 1_000, JoinStorageMode.Expanded)]
    [InlineData(10, 1_000, JoinStorageMode.Expanded)]
    public void TheRowsPerKeyDecideAtTheThreshold(double rowsPerKey, long keys, JoinStorageMode expected)
    {
        JoinStorageMode mode = Options().ResolveBuildMode(Definition(), Facts((long)Math.Round(rowsPerKey * keys), keys), out string reason);
        Assert.Equal(expected, mode);
        Assert.NotEmpty(reason);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheThresholdIsASetting()
    {
        Assert.Equal(JoinStorageMode.Indirect, Options(minRows: 10).ResolveBuildMode(Definition(), Facts(10_000, 1_000), out _));
        Assert.Equal(JoinStorageMode.Expanded, Options(minRows: 11).ResolveBuildMode(Definition(), Facts(10_000, 1_000), out _));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void TheCacheThatDoesNotFitTheBudgetThatIsLeftBuildsExpandedHoweverManyRowsAKeyHas()
    {
        // 1,000 keys of 100 rows: 69 x 1,000 + 4.5 x 100,000 = 519,000 bytes
        long estimate = JoinOptions.ResidentEstimateOf(100_000, 1_000);
        Assert.Equal(519_000, estimate);
        Assert.Equal(JoinStorageMode.Indirect, Options().ResolveBuildMode(Definition(), Facts(100_000, 1_000, cacheFree: estimate), out _));

        JoinStorageMode mode = Options().ResolveBuildMode(Definition(), Facts(100_000, 1_000, cacheFree: estimate - 1), out string reason);
        Assert.Equal(JoinStorageMode.Expanded, mode);
        Assert.Contains("resident cache", reason);

        // the budget of this join index is smaller than what is left of the process budget, and zero is no cache
        Assert.Equal(JoinStorageMode.Expanded, Options(cache: estimate - 1).ResolveBuildMode(Definition(), Facts(100_000, 1_000), out _));
        Assert.Equal(JoinStorageMode.Indirect, Options(cache: estimate).ResolveBuildMode(Definition(), Facts(100_000, 1_000), out _));
        Assert.Equal(JoinStorageMode.Expanded, Options(cache: 0).ResolveBuildMode(Definition(), Facts(100_000, 1_000), out _));
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void LowMemoryAndTooLittleAvailableMemoryBuildExpanded()
    {
        Assert.Equal(JoinStorageMode.Expanded, Options().ResolveBuildMode(Definition(), Facts(100_000, 1_000, low: true), out string reason));
        Assert.Contains("low memory", reason);

        long estimate = JoinOptions.ResidentEstimateOf(100_000, 1_000);
        JoinOptions withFloor = Options(floor: 1_000_000);
        Assert.Equal(JoinStorageMode.Indirect, withFloor.ResolveBuildMode(Definition(), Facts(100_000, 1_000, available: estimate + 1_000_000), out _));
        Assert.Equal(JoinStorageMode.Expanded, withFloor.ResolveBuildMode(Definition(), Facts(100_000, 1_000, available: estimate + 999_999), out reason));
        Assert.Contains("available", reason);
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void OnlyAJoinThatLeavesTheModeToTheServerAndIsNotOnTheIdOfTheOuterDocumentChoosesAtTheBuild()
    {
        Facts_(Options(), Definition(), JoinStorageMode.Indirect);

        // Indirect is not released: without the switch the rule never picks it
        Facts_(Options(indirectEnabled: false), Definition(), JoinStorageMode.Expanded);
        // the definition names the mode
        Facts_(Options(), Definition(JoinStorageMode.Expanded), JoinStorageMode.Expanded);
        Facts_(Options(), Definition(JoinStorageMode.Indirect), JoinStorageMode.Indirect);
        // a join on the id of the inner documents has the same rule, a join on the id of the outer document has none (it is never Indirect)
        Facts_(Options(), Definition(innerField: null), JoinStorageMode.Indirect);
        Facts_(Options(), new JoinDefinition { OuterIndex = "O", OuterField = JoinDefinition.OuterIdKey, InnerField = "Code", InnerIndex = "I" }, JoinStorageMode.Expanded);
        // the setting of the server names the mode
        Facts_(Options(storageDefault: JoinStorageMode.Expanded), Definition(), JoinStorageMode.Expanded);
        Facts_(Options(storageDefault: JoinStorageMode.Indirect), Definition(), JoinStorageMode.Indirect);

        Assert.True(Options().ChoosesModeAtBuild(Definition()));
        Assert.False(Options(storageDefault: JoinStorageMode.Indirect).ChoosesModeAtBuild(Definition()));
        Assert.False(Options().ChoosesModeAtBuild(Definition(JoinStorageMode.Indirect)));

        static void Facts_(JoinOptions options, JoinDefinition definition, JoinStorageMode expected)
        {
            Assert.Equal(expected, options.ResolveBuildMode(definition, Facts(100_000, 1_000), out _));
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public void NoKeysAreExpanded()
    {
        Assert.Equal(JoinStorageMode.Expanded, Options().ResolveBuildMode(Definition(), Facts(0, 0), out _));
        Assert.Equal(JoinStorageMode.Expanded, Options().ResolveBuildMode(Definition(), Facts(1_000, 0), out _));
    }

    private static async Task<(JoinStorageMode Mode, JoinIndex Join)> BuildAsync(JoinAutoModeTests test, int keys, int rowsPerKey, bool indirectEnabled = true, string cacheMb = null, bool idJoin = false)
    {
        if (cacheMb != null)
            test.DoNotReuseServer(new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.ResidentCacheMaxMemory)] = cacheMb });

        var settings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Join.IndirectEnabled)] = indirectEnabled.ToString(), [RavenConfiguration.GetKey(x => x.Join.AutoMinimumAvailableMemory)] = "0" };
        JoinIndexHarness h = idJoin ? await JoinIndexHarness.CreateAsync(test, settings, putJoin: false) : await JoinIndexHarness.CreateFieldJoinAsync(test, settings, putJoin: false);
        test._harness = h;
        for (int k = 0; k < keys; k++)
        {
            if (idJoin)
                h.PutContext(k, k % 2 == 0 ? "nl" : "de", "n" + k % 4, k, true, "w" + k);
            else
                h.PutCodedContext(k, $"k{k}", k % 2 == 0 ? "nl" : "de");
        }

        for (int i = 0; i < keys * rowsPerKey; i++)
            h.PutConversation(i, idJoin ? $"contexts/{i % keys}" : $"k{i % keys}", "t" + i % 3);

        await h.PutJoinAsync(idJoin ? JoinIndexHarness.JoinIndexDefinition() : JoinIndexHarness.FieldJoinIndexDefinition());
        await h.AssertCurrentAsync();
        JoinIndex joinIndex = h.Join();
        Assert.True(joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
        using (snapshot)
            return (snapshot.Schema.Mode, joinIndex);
    }

    private JoinIndexHarness _harness;

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(29, JoinStorageMode.Expanded, false)]
    [InlineData(30, JoinStorageMode.Indirect, false)]
    [InlineData(31, JoinStorageMode.Indirect, false)]
    [InlineData(29, JoinStorageMode.Expanded, true)]
    [InlineData(30, JoinStorageMode.Indirect, true)]
    [InlineData(31, JoinStorageMode.Indirect, true)]
    public async Task ABuildOfTheIndexesAtTheThresholdHasTheModeOfTheRule(int rowsPerKey, JoinStorageMode expected, bool idJoin)
    {
        (JoinStorageMode mode, JoinIndex joinIndex) = await BuildAsync(this, keys: 3, rowsPerKey, idJoin: idJoin);
        using (_harness)
        {
            Assert.Equal(expected, mode);
            Assert.Equal(expected == JoinStorageMode.Indirect ? 1 : 0, joinIndex.Counters.AutoModeIndirect);
            Assert.Equal(expected == JoinStorageMode.Expanded ? 1 : 0, joinIndex.Counters.AutoModeExpanded);
            Assert.Contains($"{rowsPerKey}.0 rows per key", joinIndex.Counters.LastAutoModeReason);

            // the join index answers as the indexes say whatever the mode, and keeps the mode of its file while the data changes
            for (int i = 0; i < 12; i++)
                _harness.PutConversation(10_000 + i, idJoin ? "contexts/0" : "k0", "late");

            await _harness.AssertCurrentAsync();
            Assert.True(joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot snapshot));
            using (snapshot)
                Assert.Equal(expected, snapshot.Schema.Mode);
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinThatHasManyRowsPerKeyIsExpandedWhenIndirectIsNotEnabled()
    {
        (JoinStorageMode mode, JoinIndex joinIndex) = await BuildAsync(this, keys: 3, rowsPerKey: 40, indirectEnabled: false);
        using (_harness)
        {
            Assert.Equal(JoinStorageMode.Expanded, mode);
            Assert.Equal(0, joinIndex.Counters.AutoModeIndirect + joinIndex.Counters.AutoModeExpanded);
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task AJoinWhoseResidentCacheDoesNotFitTheBudgetOfTheServerIsExpandedWhateverItsRowsPerKey()
    {
        // a budget of 0 is a server without caches: the cache of the join index does not fit
        (JoinStorageMode mode, JoinIndex joinIndex) = await BuildAsync(this, keys: 3, rowsPerKey: 40, cacheMb: "0");
        using (_harness)
        {
            Assert.Equal(JoinStorageMode.Expanded, mode);
            Assert.Equal(1, joinIndex.Counters.AutoModeExpanded);
            Assert.Contains("resident cache", joinIndex.Counters.LastAutoModeReason);
        }
    }

    [RavenTheory(RavenTestCategory.Indexes)]
    [InlineData(29, JoinStorageMode.Expanded)]
    [InlineData(30, JoinStorageMode.Indirect)]
    [InlineData(40, JoinStorageMode.Indirect)]
    public async Task TheStatsSayWhatTheRuleOfTheAutoModeChose(int rowsPerKey, JoinStorageMode expected)
    {
        await BuildAsync(this, keys: 3, rowsPerKey);
        using (_harness)
        {
            JoinIndexStats joinIndex = _harness.Stats().Join;
            Assert.Equal(JoinStorageMode.Auto, joinIndex.StorageMode);
            Assert.Equal(expected, joinIndex.ResolvedStorageMode);
            Assert.Equal(rowsPerKey, joinIndex.RowsPerKey.Value, 6);
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheStatsOfAJoinWhoseDefinitionNamesTheModeSayTheModeAndNoRowsPerKey()
    {
        (_, _) = await BuildAsync(this, keys: 3, rowsPerKey: 40);
        using (_harness)
        {
            IndexDefinition definition = JoinIndexHarness.FieldJoinIndexDefinition();
            definition.Join.StorageMode = JoinStorageMode.Expanded;
            _harness.Store.Maintenance.Send(new PutIndexesOperation(definition));
            await _harness.AssertCurrentAsync();

            JoinIndexStats joinIndex = _harness.Stats().Join;
            Assert.Equal(JoinStorageMode.Expanded, joinIndex.StorageMode);
            Assert.Equal(JoinStorageMode.Expanded, joinIndex.ResolvedStorageMode);
            Assert.Null(joinIndex.RowsPerKey);
        }
    }

    [RavenFact(RavenTestCategory.Indexes)]
    public async Task TheStatsFollowTheModeOfTheGenerationAfterAReset()
    {
        await BuildAsync(this, keys: 3, rowsPerKey: 40);
        using (_harness)
        {
            Assert.Equal(JoinStorageMode.Indirect, _harness.Stats().Join.ResolvedStorageMode);

            // the rows per key fall below the threshold; the mode stays with the generation until the lists are built again
            for (int i = 0; i < 300; i++)
                _harness.PutConversation(100_000 + i, $"x{i}");

            await _harness.AssertCurrentAsync();
            Assert.Equal(JoinStorageMode.Indirect, _harness.Stats().Join.ResolvedStorageMode);

            _harness.Store.Maintenance.Send(new ResetIndexOperation(JoinIndexHarness.JoinName));
            await _harness.AssertCurrentAsync();

            JoinIndexStats joinIndex = _harness.Stats().Join;
            Assert.Equal(JoinStorageMode.Auto, joinIndex.StorageMode);
            Assert.Equal(JoinStorageMode.Expanded, joinIndex.ResolvedStorageMode);
            Assert.InRange(joinIndex.RowsPerKey.Value, 1, 29);
        }
    }
}
