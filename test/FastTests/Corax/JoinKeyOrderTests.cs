using System;
using System.Linq;
using System.Text;
using Corax.Querying;
using Sparrow.Server;
using Sparrow.Threading;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Corax;

public class JoinKeyOrderTests(ITestOutputHelper output) : NoDisposalNeeded(output)
{
    private static byte[][] RandomKeys(Random random, int count, string kind)
    {
        var keys = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            switch (kind)
            {
                case "prefix":
                    keys[i] = Encoding.UTF8.GetBytes($"conversationcontexts/{random.Next(100000)}-a");
                    break;
                case "long-shared": // the keys differ after the 8 byte window
                    keys[i] = Encoding.UTF8.GetBytes($"collection/abcdefgh{random.Next(50)}x{random.Next(3)}");
                    break;
                case "bytes": // zeros, high bytes, different lengths, no common prefix
                    keys[i] = new byte[random.Next(0, 14)];
                    random.NextBytes(keys[i]);
                    for (var j = 0; j < keys[i].Length; j++)
                        keys[i][j] = (byte)(keys[i][j] % 3 == 0 ? 0 : keys[i][j]);
                    break;
                default: // a key that is the prefix of others
                    keys[i] = Encoding.UTF8.GetBytes(new string('a', random.Next(0, 12)));
                    break;
            }
        }

        return keys;
    }

    [RavenTheory(RavenTestCategory.Corax)]
    [InlineData("prefix", 1000)]
    [InlineData("long-shared", 1000)]
    [InlineData("bytes", 2000)]
    [InlineData("nested", 500)]
    [InlineData("prefix", 1)]
    [InlineData("prefix", 2)]
    public void TheOrderOfTheKeysIsTheOrderOfTheirBytes(string kind, int count)
    {
        var random = new Random(count * 31 + kind.Length);
        var keys = RandomKeys(random, count, kind);
        using var allocator = new ByteStringContext(SharedMultipleUseFlag.None);
        using var scores = new JoinTermScores(allocator);
        foreach (var key in keys)
            scores.Add(key, 1f);

        var order = scores.SortedOrder().ToArray();
        Assert.Equal(Enumerable.Range(0, count), order.OrderBy(x => x));
        for (var i = 1; i < order.Length; i++)
            Assert.True(scores.Term(order[i - 1]).SequenceCompareTo(scores.Term(order[i])) <= 0, $"key {i} is before its predecessor");
    }

    [RavenFact(RavenTestCategory.Corax)]
    public void TheOrderOfNoKeysIsEmpty()
    {
        using var allocator = new ByteStringContext(SharedMultipleUseFlag.None);
        using var scores = new JoinTermScores(allocator);
        Assert.True(scores.SortedOrder().IsEmpty);
    }
}
