using System.Text;
using Sparrow;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Storage;

public class JoinStoredHashTests(ITestOutputHelper output) : RavenTestBase(output)
{
    // The join index file stores these hashes (schema hash, pair keys, document id hashes): they must stay the reference XXH64.
    [RavenTheory(RavenTestCategory.Voron)]
    [InlineData("", 0UL, 0xEF46DB3751D8E999UL)]
    [InlineData("a", 0UL, 0xD24EC4F1A98C6E5BUL)]
    [InlineData("abc", 0UL, 0x44BC2CF5AD770999UL)]
    [InlineData("Nobody inspects the spammish repetition", 0UL, 0xFBCEA83C8A378BF1UL)]
    [InlineData("", 1UL, 0xD5AFBA1336A3BE4BUL)]
    public void StoredHashesMatchTheReferenceVectors(string text, ulong seed, ulong expected)
    {
        Assert.Equal(expected, Hashing.XXHash64.CalculateInline(Encoding.UTF8.GetBytes(text), seed));
    }
}
