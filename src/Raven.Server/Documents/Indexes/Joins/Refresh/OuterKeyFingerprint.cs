using System;
using System.Runtime.InteropServices;
using Sparrow;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>The fingerprint of the set of the keys of an outer entry of an Indirect file: the same for the same terms in any order and with repeats, another one for another set.</summary>
internal static class OuterKeyFingerprint
{
    public static long HashOf(ReadOnlySpan<byte> term) => unchecked((long)Hashing.XXHash64.CalculateInline(term));

    /// <summary>Sorts the hashes of the terms (in place), drops the repeats and returns the number of distinct ones. The fingerprint is the hash of the distinct hashes and is never 0.</summary>
    public static int Of(Span<long> hashes, out long fingerprint)
    {
        hashes.Sort();
        int distinct = 0;
        for (int i = 0; i < hashes.Length; i++)
        {
            if (distinct > 0 && hashes[distinct - 1] == hashes[i])
                continue;

            hashes[distinct++] = hashes[i];
        }

        fingerprint = unchecked((long)Hashing.XXHash64.CalculateInline(MemoryMarshal.AsBytes(hashes.Slice(0, distinct)))) | 1;
        return distinct;
    }
}
