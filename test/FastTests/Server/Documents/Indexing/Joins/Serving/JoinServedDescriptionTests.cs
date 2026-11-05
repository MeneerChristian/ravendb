using FastTests;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Serving;

/// <summary>The text that describes a served join is made when a query asks for it (the build steps), not for every query.</summary>
public class JoinServedDescriptionTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    [RavenFact(RavenTestCategory.Querying)]
    public void TheDescriptionIsMadeWhenItIsAskedForAndNotBefore()
    {
        int made = 0;
        var served = new JoinServed((_, _) => null, () =>
        {
            made++;
            return "join index 'x' generation 3";
        });

        Assert.Equal(0, made);
        Assert.False(served.EntriesOnly);
        Assert.Null(served.Counters);
        Assert.Equal(0, made);

        Assert.Equal("join index 'x' generation 3", served.Description);
        Assert.Equal(1, made);
    }

    [RavenFact(RavenTestCategory.Querying)]
    public void ANoDescriptionIsNull()
    {
        Assert.Null(new JoinServed((_, _) => null).Description);
    }
}
