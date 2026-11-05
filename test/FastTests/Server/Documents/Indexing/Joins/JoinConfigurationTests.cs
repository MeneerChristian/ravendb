using System;
using System.Linq;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config;
using Raven.Server.Config.Attributes;
using Raven.Server.Config.Categories;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Raven.Server.ServerWide;
using Sparrow;
using Sparrow.Server.LowMemory;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins;

public class JoinConfigurationTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    private static RavenConfiguration Create(params (string Key, string Value)[] settings)
    {
        RavenConfiguration configuration = RavenConfiguration.CreateForTesting("db", ResourceType.Database);
        foreach ((string key, string value) in settings)
            configuration.SetSetting(key, value);

        return configuration.Initialize();
    }

    [RavenFact(RavenTestCategory.Configuration | RavenTestCategory.Indexes)]
    public void EveryKeyHasItsDefault()
    {
        RavenConfiguration configuration = Create();
        JoinOptions options = JoinOptions.From(configuration);
        JoinQueryOptions queryOptions = JoinQueryOptions.From(configuration);

        Assert.Equal(50_000, options.MaxIdsPerCommit);
        Assert.Equal(1_000_000, options.MaxUnindexedIdsPerCommit);
        Assert.Equal(30, options.MaxInnerListChangesPerOuterEntry);
        Assert.Equal(4 * 1024 * 1024, options.MaxQueuedListChanges);
        Assert.Equal((int)Math.Clamp(MemoryInformation.TotalPhysicalMemory.GetValue(SizeUnit.Bytes) / 16 / 128, 1024, 1024 * 1024), options.MaxQueuedListChangesInBuild);
        Assert.Equal(1, options.MinAtomEntriesForDirectWrite);
        Assert.Equal(0.5, options.MaxEmptyTermRatio);
        Assert.Equal(100_000, options.MinTermsForEmptyRatio);
        Assert.Equal(TimeSpan.Zero, options.RefreshWindow);
        Assert.Equal(100_000, options.MaxResidualRows);
        Assert.Equal(20_000, options.MaxResidualKeys);
        Assert.Equal(JoinStorageMode.Auto, options.StorageModeDefault);
        Assert.Equal(30, options.AutoIndirectMinRowsPerKey);
        Assert.False(options.IndirectEnabled);
        long expectedCache = Math.Min(256L * 1024 * 1024, MemoryInformation.TotalPhysicalMemory.GetValue(SizeUnit.Bytes) / 16);
        Assert.Equal(expectedCache, configuration.Join.ResidentCacheMaxMemory.GetValue(SizeUnit.Bytes));
        Assert.Equal(expectedCache, options.ResidentCacheMaxBytes);
        Assert.Equal(TimeSpan.FromMilliseconds(100), queryOptions.Wait);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), queryOptions.WaitForNonStale);

        Assert.False(configuration.Join.AutoEnabled);
        Size total = MemoryInformation.TotalPhysicalMemory;
        Assert.Equal(Math.Min(2560L * 1024 * 1024, total.GetValue(SizeUnit.Bytes) / 8), configuration.Join.AutoMinimumAvailableMemory.GetValue(SizeUnit.Bytes));
        Assert.Equal(Math.Max(1024L, Math.Min(1024 * 1024 * 128L, total.GetValue(SizeUnit.Bytes) / 128) / 128), (long)options.MinQueuedListChanges);
        Assert.Equal(Math.Min(512L * 1024 * 1024, MemoryInformation.TotalPhysicalMemory.GetValue(SizeUnit.Bytes) / 2), configuration.Join.AutoMaxBuildMemory.GetValue(SizeUnit.Bytes));
        Assert.Equal(192, configuration.Join.AutoBuildMemoryBase.GetValue(SizeUnit.Megabytes));
        Assert.Equal(8, configuration.Join.AutoBuildMemoryPerEntry);
        Assert.Equal(TimeSpan.FromMilliseconds(15000), queryOptions.AutoBuildWaitForNonStale);
    }

    [RavenFact(RavenTestCategory.Configuration | RavenTestCategory.Indexes)]
    public void EveryKeyCanBeOverriddenInTheSettingsOfTheDatabase()
    {
        RavenConfiguration configuration = Create(
            ("Join.MaxIdsPerCommit", "11"),
            ("Join.MaxUnindexedIdsPerCommit", "12"),
            ("Join.MaxInnerListChangesPerOuterEntry", "18"),
            ("Join.MaxQueuedListChanges", "2048"),
            ("Join.MaxQueuedListChangesInBuild", "3072"),
            ("Join.MinQueuedListChanges", "4096"),
            ("Join.MinAtomEntriesForDirectWrite", "77"),
            ("Join.MaxEmptyTermRatio", "0.25"),
            ("Join.MinTermsForEmptyRatio", "13"),
            ("Join.RefreshWindowInMs", "14"),
            ("Join.MaxResidualRows", "15"),
            ("Join.MaxResidualKeys", "26"),
            ("Join.StorageModeDefault", "Indirect"),
            ("Join.AutoIndirectMinRowsPerKey", "7"),
            ("Join.IndirectEnabled", "true"),
            ("Join.QueryWaitInMs", "16"),
            ("Join.QueryWaitForNonStaleInMs", "17"),
            ("Join.AutoEnabled", "true"),
            ("Join.AutoMinimumAvailableMemoryInMb", "21"),
            ("Join.AutoMaxBuildMemoryInMb", "22"),
            ("Join.AutoBuildMemoryBaseInMb", "23"),
            ("Join.AutoBuildMemoryPerEntryInBytes", "24"),
            ("Join.AutoBuildWaitForNonStaleInMs", "25"));

        JoinOptions options = JoinOptions.From(configuration);
        JoinQueryOptions queryOptions = JoinQueryOptions.From(configuration);

        Assert.Equal(11, options.MaxIdsPerCommit);
        Assert.Equal(12, options.MaxUnindexedIdsPerCommit);
        Assert.Equal(18, options.MaxInnerListChangesPerOuterEntry);
        Assert.Equal(2048, options.MaxQueuedListChanges);
        Assert.Equal(3072, options.MaxQueuedListChangesInBuild);
        Assert.Equal(77, options.MinAtomEntriesForDirectWrite);
        Assert.Equal(0.25, options.MaxEmptyTermRatio);
        Assert.Equal(13, options.MinTermsForEmptyRatio);
        Assert.Equal(TimeSpan.FromMilliseconds(14), options.RefreshWindow);
        Assert.Equal(15, options.MaxResidualRows);
        Assert.Equal(26, options.MaxResidualKeys);
        Assert.Equal(JoinStorageMode.Indirect, options.StorageModeDefault);
        Assert.Equal(7, options.AutoIndirectMinRowsPerKey);
        Assert.True(options.IndirectEnabled);
        Assert.Equal(4096, options.MinQueuedListChanges);
        Assert.Equal(TimeSpan.FromMilliseconds(16), queryOptions.Wait);
        Assert.Equal(TimeSpan.FromMilliseconds(17), queryOptions.WaitForNonStale);

        Assert.True(configuration.Join.AutoEnabled);
        Assert.Equal(21, configuration.Join.AutoMinimumAvailableMemory.GetValue(SizeUnit.Megabytes));
        Assert.Equal(22, configuration.Join.AutoMaxBuildMemory.GetValue(SizeUnit.Megabytes));
        Assert.Equal(23, configuration.Join.AutoBuildMemoryBase.GetValue(SizeUnit.Megabytes));
        Assert.Equal(24, configuration.Join.AutoBuildMemoryPerEntry);
        Assert.Equal(TimeSpan.FromMilliseconds(25), queryOptions.AutoBuildWaitForNonStale);
    }

    [RavenFact(RavenTestCategory.Configuration | RavenTestCategory.Indexes)]
    public void TheBudgetOfTheResidentCachesIsOfTheServerAndNoDatabaseCanChangeIt()
    {
        // the caches of all databases are one registry in the process: a value for one database would be the budget of all of them
        const string key = "Join.ResidentCacheMaxMemoryInMb";
        RavenConfiguration server = RavenConfiguration.CreateForTesting("srv", ResourceType.Server);
        server.SetSetting(key, "64");
        server.Initialize();

        RavenConfiguration first = RavenConfiguration.CreateForDatabase(server, "first").Initialize();
        RavenConfiguration second = RavenConfiguration.CreateForDatabase(server, "second").Initialize();
        Assert.Equal(64L * 1024 * 1024, JoinOptions.From(first).ResidentCacheMaxBytes);
        Assert.Equal(64L * 1024 * 1024, JoinOptions.From(second).ResidentCacheMaxBytes);

        RavenConfiguration third = RavenConfiguration.CreateForDatabase(server, "third");
        third.SetSetting(key, "27");
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => third.Initialize());
        Assert.Contains(key, e.Message);
    }

    [RavenTheory(RavenTestCategory.Configuration | RavenTestCategory.Indexes)]
    [InlineData(512, 64, 32_768, 262_144, 32, 256)]
    [InlineData(1024, 128, 65_536, 524_288, 64, 512)]
    [InlineData(4096, 512, 262_144, 1_048_576, 256, 512)]
    [InlineData(8 * 1024, 1024, 524_288, 1_048_576, 256, 512)]
    [InlineData(16 * 1024, 2048, 1_048_576, 1_048_576, 256, 512)]
    [InlineData(64 * 1024, 2560, 1_048_576, 1_048_576, 256, 512)]
    public void TheMemoryDefaultsFollowTheMemoryOfTheMachine(int totalMb, int expectedMinimumAvailableMb, int expectedMinQueuedChanges, int expectedBuildQueue, int expectedResidentCacheMb, int expectedAutoMaxBuildMb)
    {
        var configuration = new JoinConfiguration(new Size(totalMb, SizeUnit.Megabytes));
        Assert.Equal(expectedMinimumAvailableMb, configuration.AutoMinimumAvailableMemory.GetValue(SizeUnit.Megabytes));
        Assert.Equal(expectedMinQueuedChanges, configuration.MinQueuedListChanges);
        Assert.Equal(expectedBuildQueue, configuration.MaxQueuedListChangesInBuild);
        Assert.Equal(expectedResidentCacheMb, configuration.ResidentCacheMaxMemory.GetValue(SizeUnit.Megabytes));
        Assert.Equal(expectedAutoMaxBuildMb, configuration.AutoMaxBuildMemory.GetValue(SizeUnit.Megabytes));
    }

    [RavenTheory(RavenTestCategory.Configuration | RavenTestCategory.Indexes)]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(8 * 1024)]
    [InlineData(32 * 1024)]
    [InlineData(256 * 1024)]
    public void TheFloorOfTheGateIsAboveTheLowMemoryLimitOfTheServerOfTheSameMachine(int totalMb)
    {
        var total = new Size(totalMb, SizeUnit.Megabytes);

        // what MemoryConfiguration makes of the machine
        Size lowMemoryLimit = Size.Min(new Size(2, SizeUnit.Gigabytes), total / 10);
        Size floor = new JoinConfiguration(total).AutoMinimumAvailableMemory;
        Assert.True(floor.GetValue(SizeUnit.Bytes) > lowMemoryLimit.GetValue(SizeUnit.Bytes), $"{floor} is not above {lowMemoryLimit}");
        Assert.True(Math.Abs(lowMemoryLimit.GetValue(SizeUnit.Bytes) * 5 / 4 - floor.GetValue(SizeUnit.Bytes)) <= 1);
    }

    [RavenFact(RavenTestCategory.Configuration | RavenTestCategory.Indexes)]
    public void EveryKeyIsDescribedAndKeepsItsName()
    {
        string[] expected =
        [
            "Join.MaxIdsPerCommit", "Join.MaxUnindexedIdsPerCommit", "Join.MaxInnerListChangesPerOuterEntry", "Join.MaxQueuedListChanges", "Join.MaxQueuedListChangesInBuild", "Join.MinQueuedListChanges", "Join.MinAtomEntriesForDirectWrite", "Join.MaxEmptyTermRatio", "Join.MinTermsForEmptyRatio",
            "Join.RefreshWindowInMs", "Join.MaxResidualRows", "Join.MaxResidualKeys", "Join.StorageModeDefault", "Join.AutoIndirectMinRowsPerKey", "Join.IndirectEnabled", "Join.ResidentCacheMaxMemoryInMb", "Join.QueryWaitInMs", "Join.QueryWaitForNonStaleInMs",
            "Join.AutoEnabled", "Join.AutoMinimumAvailableMemoryInMb", "Join.AutoMaxBuildMemoryInMb", "Join.AutoBuildMemoryBaseInMb",
            "Join.AutoBuildMemoryPerEntryInBytes", "Join.AutoBuildWaitForNonStaleInMs"
        ];

        RavenConfiguration configuration = Create();
        var entries = RavenConfiguration.GetAllConfigurationEntries(configuration)
            .Where(x => x.Keys.Any(k => k.StartsWith("Join.", StringComparison.Ordinal)))
            .ToList();

        Assert.Equal(expected.OrderBy(x => x), entries.SelectMany(x => x.Keys).OrderBy(x => x));
        Assert.All(entries, x =>
        {
            Assert.False(string.IsNullOrWhiteSpace(x.Description));
            Assert.NotNull(x.DefaultValue);
            // the caches of all databases are one registry of the process
            Assert.Equal(x.Keys.Contains("Join.ResidentCacheMaxMemoryInMb") ? ConfigurationEntryScope.ServerWideOnly : ConfigurationEntryScope.ServerWideOrPerDatabase, x.Scope);
        });
    }

    private static JoinDefinition Definition(JoinStorageMode mode, string innerField = "Code", string outerField = "ContextId")
    {
        return new JoinDefinition { OuterIndex = "O", OuterField = outerField, InnerField = innerField, InnerIndex = "I", StorageMode = mode };
    }

    [RavenTheory(RavenTestCategory.Configuration | RavenTestCategory.Indexes)]
    // the default of the node is Expanded: the definition that names nothing is built as before
    [InlineData(JoinStorageMode.Auto, JoinStorageMode.Expanded, false, JoinStorageMode.Expanded)]
    [InlineData(JoinStorageMode.Auto, JoinStorageMode.Expanded, true, JoinStorageMode.Expanded)]
    // Indirect as the default needs the switch, and it is read as Expanded without it
    [InlineData(JoinStorageMode.Auto, JoinStorageMode.Indirect, false, JoinStorageMode.Expanded)]
    [InlineData(JoinStorageMode.Auto, JoinStorageMode.Indirect, true, JoinStorageMode.Indirect)]
    // the selection by the shape of the data is not there yet: Auto as the default is Expanded
    [InlineData(JoinStorageMode.Auto, JoinStorageMode.Auto, true, JoinStorageMode.Expanded)]
    // a mode in the definition is the mode
    [InlineData(JoinStorageMode.Expanded, JoinStorageMode.Indirect, true, JoinStorageMode.Expanded)]
    [InlineData(JoinStorageMode.Indirect, JoinStorageMode.Expanded, true, JoinStorageMode.Indirect)]
    public void TheModeOfAJoinThatIsBuiltIsTheOneOfTheDefinitionOrTheDefaultOfTheNode(JoinStorageMode definition, JoinStorageMode nodeDefault, bool indirectEnabled, JoinStorageMode expected)
    {
        var options = new JoinOptions { StorageModeDefault = nodeDefault, IndirectEnabled = indirectEnabled };

        Assert.Equal(expected, options.ResolveMode(Definition(definition)));
    }

    [RavenTheory(RavenTestCategory.Configuration | RavenTestCategory.Indexes)]
    [InlineData("Code", "ContextId")]
    [InlineData(null, "ContextId")]
    [InlineData("Code", "id()")]
    public void AJoinOnTheIdOfTheOuterDocumentIsNeverBuiltInTheIndirectModeByTheDefault(string innerField, string outerField)
    {
        var options = new JoinOptions { StorageModeDefault = JoinStorageMode.Indirect, IndirectEnabled = true };

        JoinStorageMode mode = options.ResolveMode(Definition(JoinStorageMode.Auto, innerField, outerField));

        Assert.Equal(outerField != "id()" ? JoinStorageMode.Indirect : JoinStorageMode.Expanded, mode);
    }
}
