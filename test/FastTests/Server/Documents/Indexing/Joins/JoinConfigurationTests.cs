using System;
using System.Linq;
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

    [RavenTheory(RavenTestCategory.Configuration | RavenTestCategory.Indexes)]
    [InlineData(512, 64, 32_768, 262_144, 256)]
    [InlineData(1024, 128, 65_536, 524_288, 512)]
    [InlineData(4096, 512, 262_144, 1_048_576, 512)]
    [InlineData(8 * 1024, 1024, 524_288, 1_048_576, 512)]
    [InlineData(16 * 1024, 2048, 1_048_576, 1_048_576, 512)]
    [InlineData(64 * 1024, 2560, 1_048_576, 1_048_576, 512)]
    public void TheMemoryDefaultsFollowTheMemoryOfTheMachine(int totalMb, int expectedMinimumAvailableMb, int expectedMinQueuedChanges, int expectedBuildQueue, int expectedAutoMaxBuildMb)
    {
        var configuration = new JoinConfiguration(new Size(totalMb, SizeUnit.Megabytes));
        Assert.Equal(expectedMinimumAvailableMb, configuration.AutoMinimumAvailableMemory.GetValue(SizeUnit.Megabytes));
        Assert.Equal(expectedMinQueuedChanges, configuration.MinQueuedListChanges);
        Assert.Equal(expectedBuildQueue, configuration.MaxQueuedListChangesInBuild);
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
            "Join.RefreshWindowInMs", "Join.MaxResidualRows", "Join.QueryWaitInMs", "Join.QueryWaitForNonStaleInMs",
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
            Assert.Equal(ConfigurationEntryScope.ServerWideOrPerDatabase, x.Scope);
        });
    }
}
