using System;
using Raven.Server.Config;
using Raven.Server.Config.Categories;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>What a query does when the join index is behind the indexes. Read from the configuration of the database (RavenConfiguration.Join, the keys Join.*).</summary>
public sealed class JoinQueryOptions
{
    /// <summary>How long a query waits for the join index to catch up before it reads the join as usual.</summary>
    public TimeSpan Wait { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The same for a query that asked to wait for non stale results, which accepts a longer wait.</summary>
    public TimeSpan WaitForNonStale { get; set; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>How long a query that waits for non stale results waits for an auto join index to be built, when the query has no timeout of its own.</summary>
    public TimeSpan AutoBuildWaitForNonStale { get; set; } = TimeSpan.FromMilliseconds(15000);

    public static JoinQueryOptions From(RavenConfiguration configuration)
    {
        var options = new JoinQueryOptions();
        if (configuration == null)
            return options;

        JoinConfiguration joinIndex = configuration.Join;
        options.Wait = joinIndex.QueryWait.AsTimeSpan;
        options.WaitForNonStale = joinIndex.QueryWaitForNonStale.AsTimeSpan;
        options.AutoBuildWaitForNonStale = joinIndex.AutoBuildWaitForNonStale.AsTimeSpan;
        return options;
    }
}
