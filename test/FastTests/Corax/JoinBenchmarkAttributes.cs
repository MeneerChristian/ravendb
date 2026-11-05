using System;
using Tests.Infrastructure;

namespace FastTests.Corax;

// A benchmark: reported as skipped unless its environment variable is set
public sealed class JoinBenchmarkFactAttribute : RavenFactAttribute
{
    public JoinBenchmarkFactAttribute(string environmentVariable, RavenTestCategory category) : base(category)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrEmpty(value) || value == "0")
            Skip = $"Benchmark: set {environmentVariable} to run it.";
    }
}

public sealed class JoinBenchmarkTheoryAttribute : RavenTheoryAttribute
{
    public JoinBenchmarkTheoryAttribute(string environmentVariable, RavenTestCategory category) : base(category)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrEmpty(value) || value == "0")
            Skip = $"Benchmark: set {environmentVariable} to run it.";
    }
}
