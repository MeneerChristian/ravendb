using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Fuzz;

/// <summary>
/// A seeded random workload against a join index, compared with the join of the server after every round. The seed decides everything (the thresholds of the refresh and of the cache, the size of the data, the shape of the keys,
/// the writes, the restarts and the resets), so a failure is run again with that seed alone. A round writes documents of both sides in batches that straddle the limits (the keys stay, move, are several, are none, the documents
/// are deleted and made again), restarts the database, resets the join index or one of its indexes, pauses the join index, then waits for the indexes, checks the invariant of the lists and compares the answers of the join index with those of the server.
/// <para>
/// Settings: RAVEN_JOIN_FUZZ_SEEDS (a comma separated list, replaces the fixed seeds 1 to 25), RAVEN_JOIN_FUZZ_EXTRA (this many more seeds from 1001), RAVEN_JOIN_FUZZ_ROUNDS (rounds per run, 8),
/// RAVEN_JOIN_FUZZ_MINIMIZE (0 turns the minimization of a failure off), RAVEN_JOIN_FUZZ_OUT (a directory that gets a file for every failure).
/// </para>
/// </summary>
public abstract class JoinIndexFuzzTestsBase(ITestOutputHelper output) : RavenTestBase(output)
{
    private readonly ITestOutputHelper _output = output;

    public static IEnumerable<object[]> Seeds()
    {
        string listed = Environment.GetEnvironmentVariable("RAVEN_JOIN_FUZZ_SEEDS");
        if (string.IsNullOrWhiteSpace(listed) == false)
        {
            foreach (string seed in listed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return [int.Parse(seed)];

            yield break;
        }

        for (int seed = 1; seed <= 25; seed++)
            yield return [seed];

        int extra = int.TryParse(Environment.GetEnvironmentVariable("RAVEN_JOIN_FUZZ_EXTRA"), out int more) ? more : 0;
        for (int i = 0; i < extra; i++)
            yield return [1001 + i];
    }

    private static int Rounds => int.TryParse(Environment.GetEnvironmentVariable("RAVEN_JOIN_FUZZ_ROUNDS"), out int rounds) && rounds > 0 ? rounds : 8;

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [MemberData(nameof(Seeds), MemberType = typeof(JoinIndexFuzzTestsBase))]
    public Task JoinOnTheIdOfTheInnerDocumentsEqualsTheJoinOfTheServer(int seed) => RunAsync(FuzzShape.InnerId, seed);

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [MemberData(nameof(Seeds), MemberType = typeof(JoinIndexFuzzTestsBase))]
    public Task JoinOnAFieldEqualsTheJoinOfTheServer(int seed) => RunAsync(FuzzShape.Field, seed);

    private protected async Task RunAsync(FuzzShape shape, int seed)
    {
        FuzzSettings settings = FuzzSettings.For(seed, shape, Rounds);
        _output.WriteLine($"{GetType().Name}: {settings}");
        FuzzPlan plan = FuzzPlan.Create(settings);
        var runner = new JoinFuzzRunner(this, _output);
        FuzzFailure failure = await runner.RunAsync(plan);
        if (failure == null)
            return;

        _output.WriteLine($"first difference in round {failure.Round} ({failure.Phase})");
        FuzzPlan minimal = plan.Without([], failure.Round + 1);
        FuzzFailure minimalFailure = failure;
        if (Environment.GetEnvironmentVariable("RAVEN_JOIN_FUZZ_MINIMIZE") != "0")
            (minimal, minimalFailure) = await MinimizeAsync(plan, failure);

        var report = new StringBuilder();
        report.AppendLine($"JOIN FUZZ FAILURE in {GetType().Name}: {settings}");
        report.AppendLine($"first diverging round {failure.Round}, phase {failure.Phase}:");
        report.AppendLine(failure.Message);
        report.AppendLine();
        report.AppendLine($"MINIMIZED to {minimal.OpCount} writes and {minimal.AdminCount} admin steps in {minimal.Rounds.Count} rounds (of {plan.OpCount} writes in {plan.Rounds.Count} rounds); it fails in round {minimalFailure.Round}, phase {minimalFailure.Phase}:");
        report.AppendLine(minimalFailure.Message);
        report.AppendLine();
        report.AppendLine("operations of the minimized plan:");
        report.AppendLine(minimal.Describe());
        report.AppendLine("operations of the full plan up to the first diverging round:");
        report.AppendLine(plan.Describe(failure.Round));

        string directory = Environment.GetEnvironmentVariable("RAVEN_JOIN_FUZZ_OUT");
        if (string.IsNullOrWhiteSpace(directory) == false)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, $"fuzz-failure-{GetType().Name}-{shape}-seed{seed}.txt"), report.ToString());
        }

        Assert.Fail(report.ToString());
    }

    /// <summary>Removes writes and admin steps while the run still fails (any failure counts), halving the chunk that is tried; at most 60 runs.</summary>
    private async Task<(FuzzPlan, FuzzFailure)> MinimizeAsync(FuzzPlan plan, FuzzFailure failure)
    {
        int runs = 0;
        FuzzPlan current = plan.Without([], failure.Round + 1);
        FuzzFailure currentFailure = failure;

        async Task<bool> TryAsync(HashSet<int> removed)
        {
            if (runs++ >= 60)
                return false;

            FuzzPlan candidate = current.Without(removed, int.MaxValue);
            FuzzFailure result = await new JoinFuzzRunner(this, _output).RunAsync(candidate);
            if (result == null)
                return false;

            current = candidate.Without([], result.Round + 1);
            currentFailure = result;
            return true;
        }

        for (int chunk = Math.Max(1, (current.OpCount + current.AdminCount) / 2); chunk >= 1 && runs < 60; chunk /= 2)
        {
            for (int start = 0; start < current.OpCount + current.AdminCount && runs < 60;)
            {
                int total = current.OpCount + current.AdminCount;
                var removed = Enumerable.Range(start, Math.Min(chunk, total - start)).ToHashSet();
                if (await TryAsync(removed) == false)
                    start += chunk;
            }

            if (chunk == 1)
                break;
        }

        return (current, currentFailure);
    }
}

/// <summary>The lists are Expanded: the outer entries of every key are stored. The join on the id of the outer documents has no other mode, so it runs only here.</summary>
public class JoinIndexFuzzTestsExpanded(ITestOutputHelper output) : JoinIndexFuzzTestsBase(output)
{
    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Indexes)]
    [MemberData(nameof(Seeds), MemberType = typeof(JoinIndexFuzzTestsBase))]
    public Task JoinOnTheIdOfTheOuterDocumentsEqualsTheJoinOfTheServer(int seed) => RunAsync(FuzzShape.OuterId, seed);
}

/// <summary>The lists are Indirect and the resident cache is on (every list that is read is kept; the patch limit is the one of the seed).</summary>
public class JoinIndexFuzzTestsIndirectCacheOn(ITestOutputHelper output) : JoinIndexFuzzTestsBase(output), IIndirectModeTest, IResidentModeTest
{
    public ResidentMode ResidentMode => ResidentMode.On;
}

/// <summary>The lists are Indirect and there is no cache: every query reads the lists of the store.</summary>
public class JoinIndexFuzzTestsIndirectCacheOff(ITestOutputHelper output) : JoinIndexFuzzTestsBase(output), IIndirectModeTest, IResidentModeTest
{
    public ResidentMode ResidentMode => ResidentMode.Off;
}

/// <summary>The lists are Indirect and the cache has the options of the server (fill and hold).</summary>
public class JoinIndexFuzzTestsIndirectCacheDefault(ITestOutputHelper output) : JoinIndexFuzzTestsBase(output), IIndirectModeTest, IResidentModeTest
{
    public ResidentMode ResidentMode => ResidentMode.Default;
}

/// <summary>The lists are Indirect and the cache holds a few hundred bytes: lists are evicted and refused in the middle of the workload.</summary>
public class JoinIndexFuzzTestsIndirectCacheTiny(ITestOutputHelper output) : JoinIndexFuzzTestsBase(output), IIndirectModeTest, IResidentModeTest
{
    public ResidentMode ResidentMode => ResidentMode.Tiny;
}
