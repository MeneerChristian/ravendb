using Corax.Querying;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

public class JoinTermIdTableTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData(1 << 10)]
    [InlineData(1 << 24)]
    [InlineData(1 << 27)]
    public void TheSlotsOfTermIdsSpreadOverTheWholeTable(int capacity)
    {
        var mask = capacity - 1;
        var buckets = new int[16];
        const int sample = 400_000;
        var maxSlot = 0;
        for (long termId = 1; termId <= sample; termId++)
        {
            var slot = IndexSearcher.TermIdBestTable.Slot(termId * 64, mask);
            Assert.InRange(slot, 0, mask);
            buckets[(int)((long)slot * 16 / capacity)]++;
            if (slot > maxSlot)
                maxSlot = slot;
        }

        // every sixteenth of the table gets its share of the ids (container ids step by more than 1, as the sample does)
        foreach (var count in buckets)
            Assert.InRange(count, sample / 16 / 2, sample / 16 * 2);
        Assert.True(maxSlot > capacity / 2, $"the largest slot is {maxSlot} of {capacity}");
    }
}
