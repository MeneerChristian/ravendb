using System;

namespace Corax.Querying.Matches
{
    public interface IEntryFilter
    {
        // Keeps the entries that satisfy the condition, in the same order, at the start of the span
        int Filter(Span<long> entries);
    }
}
