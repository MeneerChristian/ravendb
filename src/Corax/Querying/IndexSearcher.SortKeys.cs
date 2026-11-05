using System;
using Corax.Mappings;
using Corax.Querying.Matches.SortingMatches;
using Corax.Querying.Matches.SortingMatches.Meta;
using Corax.Utils;
using Voron;
using Voron.Util;

namespace Corax.Querying;

public partial class IndexSearcher
{
    public void ReadSortKeys(in OrderMetadata order, ReadOnlySpan<long> entries, Span<long> keys)
    {
        if (entries.Length != keys.Length)
            throw new ArgumentException("Every entry needs a key.");

        if (entries.Length == 0)
            return;

        Slice treeName;
        long missing;
        switch (order.FieldType)
        {
            case MatchCompareFieldType.Sequence:
            case MatchCompareFieldType.Alphanumeric:
                treeName = order.Field.FieldName;
                missing = SortingHelpers.MissingTermId;
                break;
            case MatchCompareFieldType.Integer:
                IndexFieldsMappingBuilder.GetFieldNameForLongs(Allocator, order.Field.FieldName, out treeName);
                missing = long.MinValue;
                break;
            case MatchCompareFieldType.Floating:
                IndexFieldsMappingBuilder.GetFieldNameForDoubles(Allocator, order.Field.FieldName, out treeName);
                missing = BitConverter.DoubleToInt64Bits(double.MinValue);
                break;
            default:
                throw new NotSupportedException($"Sort keys cannot be read for the sort type {order.FieldType}.");
        }

        var lookup = EntriesToTermsReader(treeName);
        if (lookup == null) // the field has no terms: nothing tells the entries apart
        {
            keys.Clear();
            return;
        }

        // the lookup walks the tree front to back, so it needs the entries in order
        var sorted = new ContextBoundNativeList<long>(Allocator, entries.Length);
        var positions = new ContextBoundNativeList<int>(Allocator, entries.Length);
        var terms = new ContextBoundNativeList<long>(Allocator, entries.Length);
        try
        {
            sorted.AddRange(entries);
            for (var i = 0; i < entries.Length; i++)
                positions.Add(i);
            MemoryExtensions.Sort(sorted.ToSpan(), positions.ToSpan());

            terms.Count = entries.Length;
            terms.ToSpan().Clear();
            lookup.GetFor(sorted.ToSpan(), terms.ToSpan(), missing);

            if (order.FieldType is MatchCompareFieldType.Sequence or MatchCompareFieldType.Alphanumeric)
            {
                // null and non-existing values are one bucket for the sorting
                var nullTermContainerId = TryGetPostingListForNull(order.Field.FieldName, out _, out var nullId) ? nullId : SortingHelpers.InvalidTermId;
                var nonExistingTermContainerId = TryGetPostingListForNonExisting(order.Field.FieldName, out _, out var nonExistingId) ? nonExistingId : SortingHelpers.InvalidTermId;
                SortingHelpers.ReplaceNullAndNonExistingTermIds(terms.ToSpan(), nonExistingTermContainerId, nullTermContainerId, SortingHelpers.MissingTermId);
            }

            var termSpan = terms.ToSpan();
            var positionSpan = positions.ToSpan();
            for (var i = 0; i < positionSpan.Length; i++)
                keys[positionSpan[i]] = termSpan[i];
        }
        finally
        {
            sorted.Dispose();
            positions.Dispose();
            terms.Dispose();
        }
    }
}
