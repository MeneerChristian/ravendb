using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corax.Indexing;
using Corax.Mappings;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Serving;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Voron.Data.CompactTrees;
using Voron.Util;
using Index = Raven.Server.Documents.Indexes.Index;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>
/// The invariant of the join index: at an equal generation its lists are what a scan of the two index snapshots gives. The check opens the join index first and the indexes after it,
/// so it only compares when the generations are equal. It reads the entries one by one, not the terms of the fields as the build does, and can run next to a workload (the soak reuses it).
/// </summary>
public static class JoinIndexInvariantChecker
{
    public sealed class Result
    {
        public bool Compared { get; init; }

        public string NotComparedReason { get; init; }

        public List<string> Mismatches { get; } = new();

        public long OuterEntries { get; set; }

        public long InnerEntries { get; set; }

        public long Joined { get; set; }

        public string Generation { get; init; }

        /// <summary>The resident lists that were compared with the posting lists of the outer snapshot (-1: they could not be compared, see <see cref="ResidentNote"/>).</summary>
        public int ResidentChecked { get; set; } = -1;

        public string ResidentNote { get; set; }

        public bool Ok => Compared && Mismatches.Count == 0;

        public override string ToString() => Compared
            ? $"{(Mismatches.Count == 0 ? "OK" : "MISMATCH")} generation {Generation}: outer {OuterEntries}, inner {InnerEntries}, joined {Joined}{(Mismatches.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, Mismatches.Take(20)))}"
            : $"not compared: {NotComparedReason}";
    }

    /// <summary>The terms that the lists of a field had at some time: a list of a term that no entry has any more must be empty.</summary>
    public sealed class SeenTerms
    {
        public HashSet<string>[] ByField { get; }

        /// <summary>The keys of a join on a field that the groups had at some time: the group of a key that no inner entry has any more must hold nothing.</summary>
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

        public SeenTerms(int fields) => ByField = Enumerable.Range(0, fields).Select(_ => new HashSet<string>()).ToArray();
    }

    public static Result Check(DocumentDatabase database, JoinIndex joinIndex, SeenTerms seen)
    {
        if (joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot joinSnapshot) == false)
            return new Result { NotComparedReason = "the join index cannot be read now" };

        using (joinSnapshot)
        {
            if (joinSnapshot.Generation.OuterEnvironmentId == Guid.Empty)
                return new Result { NotComparedReason = "the lists are not built yet" };

            JoinPlan plan = joinIndex.Plan;
            Index outerIndex = database.IndexStore.GetIndex(joinIndex.Join.OuterIndex);
            Index innerIndex = database.IndexStore.GetIndex(joinIndex.Join.InnerIndex);
            if (outerIndex == null || innerIndex == null || outerIndex.TryOpenSnapshot(out IndexSnapshot outerSnapshot) == false)
                return new Result { NotComparedReason = "index unavailable" };

            using var outer = new IndexView(outerSnapshot, plan.IdField);
            if (innerIndex.TryOpenSnapshot(out IndexSnapshot innerSnapshot) == false)
                return new Result { NotComparedReason = "index unavailable" };

            using var inner = new IndexView(innerSnapshot, plan.IdField);

            JoinIndexGeneration generation = joinSnapshot.Generation;
            if (generation.OuterEnvironmentId != outerSnapshot.EnvironmentId || generation.InnerEnvironmentId != innerSnapshot.EnvironmentId)
                return new Result { NotComparedReason = "environment differs" };

            if (generation.Outer.CompareTo(outerSnapshot.Consumed) != GenerationComparison.Equal || generation.Inner.CompareTo(innerSnapshot.Consumed) != GenerationComparison.Equal)
                return new Result { NotComparedReason = $"generation differs: {generation}" };

            var result = new Result { Compared = true, Generation = generation.ToString() };
            Compare(joinSnapshot, plan, outer, inner, seen, result);
            CompareResident(joinIndex, joinSnapshot, plan, outer, outerSnapshot, result);
            return result;
        }
    }

    /// <summary>With an epoch that is the outer snapshot of the check, every resident list is the posting list of its key in that snapshot; without an epoch nothing is resident.</summary>
    private static unsafe void CompareResident(JoinIndex joinIndex, JoinIndexSnapshot joinSnapshot, JoinPlan plan, IndexView outer, IndexSnapshot outerSnapshot, Result result)
    {
        JoinResident resident = joinIndex.Resident;
        if (resident == null || plan.IsFieldJoin == false || joinSnapshot.Schema.IsIndirect == false)
        {
            result.ResidentNote = "no cache";
            return;
        }

        ResidentKeyListCache cache = resident.Cache;
        ResidentEpoch epoch = cache.Epoch;
        if (epoch.IsNone)
        {
            result.ResidentNote = "no epoch";
            if (cache.ResidentKeys != 0)
                result.Mismatches.Add($"the cache has no epoch and holds {cache.ResidentKeys} lists");

            result.ResidentChecked = 0;
            return;
        }

        // the check is a reader of this snapshot: when the outer index committed without moving what it consumed, the epoch follows it, as it does for the reader of a query
        long transaction = outerSnapshot.Transaction.LowLevelTransaction.Id;
        resident.Follow(resident.ReadOf(outerSnapshot.EnvironmentId, transaction, joinSnapshot.Seed, 0, outerSnapshot.Consumed), outer.Searcher.LastEntryId);
        epoch = cache.Epoch;
        if (epoch.EnvironmentId != JoinResident.ScopeOf(outerSnapshot.EnvironmentId, joinSnapshot.Seed) || epoch.TransactionId != transaction)
        {
            result.ResidentNote = $"epoch is another snapshot ({epoch.TransactionId}, this {transaction})";
            return;
        }

        CompactTree tree = outer.TermsOf(plan.OuterKeyField);
        FieldMetadata metadata = outer.Searcher.FieldMetadataBuilder(plan.Definition.OuterField);
        var rows = new ContextBoundNativeList<long>(outer.Searcher.Allocator);
        try
        {
            int checks = 0;
            foreach (long keyRef in cache.GetResidentKeys())
            {
                rows.Count = 0;
                if (tree != null)
                    JoinOuterKeyLists.ReadRowsOfKey(outer.Searcher, metadata, joinSnapshot, tree, keyRef, ref rows);

                long[] expected = rows.ToSpan().ToArray();
                if (cache.TryGetList(keyRef, out long[] actual) == false)
                    continue;

                if (expected.SequenceEqual(actual) == false)
                    result.Mismatches.Add($"resident list of key {keyRef} [{string.Join(",", actual.Take(12))}] is not the posting list [{string.Join(",", expected.Take(12))}] of the snapshot {transaction}");

                checks++;
            }

            result.ResidentChecked = checks;
            result.ResidentNote = "compared";
        }
        finally
        {
            rows.Dispose();
        }
    }

    internal static long[] ReadList(JoinReader reader, long handle)
    {
        var output = new ContextBoundNativeList<long>(reader.Transaction.Allocator);
        try
        {
            reader.ReadList(handle, ref output);
            return output.ToSpan().ToArray();
        }
        finally
        {
            output.Dispose();
        }
    }

    internal static long[] ReadTerm(JoinReader reader, int field, string term)
    {
        return reader.TryGetTermList(field, Encoding.UTF8.GetBytes(term), out long handle) ? ReadList(reader, handle) : null;
    }

    private static void Compare(JoinIndexSnapshot joinSnapshot, JoinPlan plan, IndexView outer, IndexView inner, SeenTerms seen, Result result)
    {
        if (plan.IsFieldJoin)
        {
            FieldJoinInvariantChecker.Compare(joinSnapshot, plan, outer, inner, seen, result);
            return;
        }

        using var allocator = new global::Sparrow.Server.ByteStringContext(global::Sparrow.Threading.SharedMultipleUseFlag.None);
        using var projector = new InnerProjector(plan, allocator);
        using var keyReader = new OuterKeyReader(plan, allocator);
        projector.Bind(inner);
        keyReader.Bind(outer);

        var termLists = new Dictionary<(int, string), List<long>>();
        var any = new List<long>();
        var groupings = Enumerable.Range(0, plan.GroupingFields.Length).Select(_ => new List<long>()).ToArray();
        var keysOf = new Dictionary<long, long[]>();
        long severalJoined = 0;

        result.InnerEntries = CountEntries(inner, plan);
        result.OuterEntries = 0;

        CompactTree ids = outer.TermsOf(plan.IdField);
        if (ids != null)
        {
            var iterator = ids.Iterate();
            iterator.Reset();
            using var scope = new CompactKeyCacheScope(outer.Snapshot.Transaction.LowLevelTransaction);
            while (iterator.MoveNext(scope.Key, out long value, out _))
            {
                long entry = ListStore.Decode(value);
                result.OuterEntries++;
                if (keyReader.Read(outer, entry, joinSnapshot) == false)
                    continue;

                keysOf[entry] = keyReader.Keys.ToArray();

                // an entry is in the list of an atom when any of the documents it names has the atom
                var joinedKeys = new HashSet<long>();
                var rowTerms = new HashSet<(int, string)>();
                int membership = 0;
                for (int i = 0; i < keyReader.TermCount; i++)
                {
                    ReadOnlySpan<byte> keyTerm = keyReader.TermAt(i);
                    if (inner.TryFindEntry(keyTerm, out long innerEntry, out _) == false)
                        continue;

                    if (joinedKeys.Add(joinSnapshot.InnerKeyOf(keyTerm)) == false)
                        continue;

                    projector.Project(inner, innerEntry);
                    for (int t = 0; t < projector.Count; t++)
                        rowTerms.Add((projector.FieldOf(t), Encoding.UTF8.GetString(projector.TermOf(t))));

                    membership |= projector.Membership;
                }

                if (joinedKeys.Count == 0)
                    continue;

                if (joinedKeys.Count >= 2)
                    severalJoined++;

                any.Add(entry);
                foreach ((int field, string term) in rowTerms)
                {
                    if (termLists.TryGetValue((field, term), out List<long> list) == false)
                        termLists[(field, term)] = list = new List<long>();

                    list.Add(entry);
                    seen.ByField[field].Add(term);
                }

                for (int g = 0; g < groupings.Length; g++)
                {
                    if ((membership & (1 << g)) != 0)
                        groupings[g].Add(entry);
                }
            }
        }

        result.Joined = any.Count;

        // the members of the groupings among the inner entries
        var members = Enumerable.Range(0, groupings.Length).Select(_ => new List<long>()).ToArray();
        CompactTree innerIds = inner.TermsOf(plan.IdField);
        if (innerIds != null)
        {
            var innerIterator = innerIds.Iterate();
            innerIterator.Reset();
            using var innerScope = new CompactKeyCacheScope(inner.Snapshot.Transaction.LowLevelTransaction);
            while (innerIterator.MoveNext(innerScope.Key, out long innerValue, out _))
            {
                long innerEntry = ListStore.Decode(innerValue);
                projector.Project(inner, innerEntry);
                long innerKey = joinSnapshot.InnerKeyOf(innerScope.Key.Decoded());
                for (int g = 0; g < groupings.Length; g++)
                {
                    if ((projector.Membership & (1 << g)) != 0)
                        members[g].Add(innerKey);
                }
            }
        }

        for (int g = 0; g < groupings.Length; g++)
            Compare($"members of grouping {g}", members[g].OrderBy(x => x).ToArray(), ReadList(joinSnapshot, joinSnapshot.SlotHandle(joinSnapshot.GroupingMembersSlot(g))), result);

        for (int field = 0; field < plan.Fields.Length; field++)
        {
            foreach (string term in seen.ByField[field])
            {
                long[] expected = termLists.TryGetValue((field, term), out List<long> list) ? list.OrderBy(x => x).ToArray() : Array.Empty<long>();
                long[] actual = ReadTerm(joinSnapshot, field, term) ?? Array.Empty<long>();
                if (expected.SequenceEqual(actual) == false)
                    result.Mismatches.Add($"field {plan.Definition.Fields[field].Field} term '{term}': expected {Describe(expected)}, join index has {Describe(actual)}");
            }
        }

        long[] anyActual = ReadList(joinSnapshot, joinSnapshot.SlotHandle(JoinReader.AnySlot));
        Compare("any", any.OrderBy(x => x).ToArray(), anyActual, result);
        foreach (long extra in anyActual.Except(any).Take(5))
            result.Mismatches.Add($"stale entry {extra} in any: the outer index has it for '{outer.DescribeEntry(extra) ?? "(no document)"}', keys {Describe(keysOf.GetValueOrDefault(extra) ?? Array.Empty<long>())}");

        for (int g = 0; g < groupings.Length; g++)
        {
            long[] actualGrouping = ReadList(joinSnapshot, joinSnapshot.SlotHandle(JoinReader.GroupingListSlot(g)));
            Compare($"grouping {g}", groupings[g].OrderBy(x => x).ToArray(), actualGrouping, result);
            foreach (long extra in actualGrouping.Except(groupings[g]).Take(5))
                result.Mismatches.Add($"stale entry {extra} in grouping {g}: outer '{outer.DescribeEntry(extra) ?? "(none)"}', keys {Describe(keysOf.GetValueOrDefault(extra) ?? Array.Empty<long>())}");
        }

        // the keys of every entry that names a document, whether the document exists or not
        var actualKeys = new ContextBoundNativeList<long>(allocator, 16);
        foreach ((long entry, long[] expectedKeys) in keysOf.OrderBy(x => x.Key))
        {
            actualKeys.Count = 0;
            joinSnapshot.TryGetOuterKeys(entry, ref actualKeys);
            if (expectedKeys.SequenceEqual(actualKeys.ToSpan().ToArray()) == false)
                result.Mismatches.Add($"outer entry {entry} names the keys {Describe(expectedKeys)}, join index has {Describe(actualKeys.ToSpan().ToArray())}");
        }

        actualKeys.Dispose();

        Compare("entries with several keys", keysOf.Where(x => x.Value.Length >= 2).Select(x => x.Key).OrderBy(x => x).ToArray(), ReadList(joinSnapshot, joinSnapshot.SlotHandle(joinSnapshot.MultiKeySlot)), result);

        if (joinSnapshot.RowsWithSeveralJoined != severalJoined)
            result.Mismatches.Add($"entries joined with several documents: expected {severalJoined}, join index has {joinSnapshot.RowsWithSeveralJoined}");

        if (joinSnapshot.NumberOfJoined != keysOf.Count)
            result.Mismatches.Add($"entries with keys: expected {keysOf.Count}, join index has {joinSnapshot.NumberOfJoined}");

        if (joinSnapshot.NumberOfOuterDocuments != result.OuterEntries)
            result.Mismatches.Add($"outer documents: expected {result.OuterEntries}, join index has {joinSnapshot.NumberOfOuterDocuments}");

        if (joinSnapshot.NumberOfInnerDocuments != result.InnerEntries)
            result.Mismatches.Add($"inner documents: expected {result.InnerEntries}, join index has {joinSnapshot.NumberOfInnerDocuments}");
    }

    private static long CountEntries(IndexView view, JoinPlan plan)
    {
        CompactTree ids = view.TermsOf(plan.IdField);
        return ids?.NumberOfEntries ?? 0;
    }

    private static void Compare(string what, long[] expected, long[] actual, Result result)
    {
        if (expected.SequenceEqual(actual) == false)
            result.Mismatches.Add($"{what}: expected {Describe(expected)}, join index has {Describe(actual)}");
    }

    private static string Describe(long[] ids) => ids.Length <= 12 ? $"[{string.Join(",", ids)}]" : $"{ids.Length} entries [{string.Join(",", ids.Take(6))},...]";
}
