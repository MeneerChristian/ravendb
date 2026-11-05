using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Server;
using Sparrow.Threading;
using Voron.Data.CompactTrees;
using Voron.Util;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>
/// The invariant of a join index of a join on a field: the lists, the key of every outer entry and of every inner document, the number of the entries of each group that have each atom
/// and the atoms of each key are what a scan of the two index snapshots gives. Entries are read one by one, not by the walk of the terms of the build.
/// </summary>
internal static class FieldJoinInvariantChecker
{
    private sealed class Group
    {
        public int Size;
        public readonly Dictionary<(int Kind, int Field, string Term), int> Atoms = new();
    }

    public static void Compare(JoinIndexSnapshot joinIndex, JoinPlan plan, IndexView outer, IndexView inner, JoinIndexInvariantChecker.SeenTerms seen, JoinIndexInvariantChecker.Result result)
    {
        using var allocator = new ByteStringContext(SharedMultipleUseFlag.None);
        using var projector = new InnerProjector(plan, allocator);
        using var keyReader = new OuterKeyReader(plan, allocator);
        projector.Bind(inner);
        keyReader.Bind(outer);

        // kind 0 = any, 1 = a term of a declared field, 2 = a grouping (the term is the index of the grouping)
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        var members = Enumerable.Range(0, plan.GroupingFields.Length).Select(_ => new List<long>()).ToArray();
        var innerKeyOf = new Dictionary<long, string[]>();
        long innerDocuments = 0;

        CompactTree innerIds = inner.TermsOf(plan.IdField);
        if (innerIds != null)
        {
            var iterator = innerIds.Iterate();
            iterator.Reset();
            using var scope = new CompactKeyCacheScope(inner.Snapshot.Transaction.LowLevelTransaction);
            while (iterator.MoveNext(scope.Key, out long value, out _))
            {
                innerDocuments++;
                long entry = ListStore.Decode(value);
                long documentKey = joinIndex.InnerKeyOf(scope.Key.Decoded());
                projector.Project(inner, entry);
                for (int g = 0; g < members.Length; g++)
                {
                    if ((projector.Membership & (1 << g)) != 0)
                        members[g].Add(documentKey);
                }

                if (projector.HasKey == false)
                    continue;

                // an entry with several keys is in the group of each of them
                string[] keys = Enumerable.Range(0, projector.KeyCount).Select(i => Encoding.UTF8.GetString(projector.KeyAt(i))).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                innerKeyOf[documentKey] = keys;
                foreach (string key in keys)
                {
                    seen.Keys.Add(key);
                    if (groups.TryGetValue(key, out Group group) == false)
                        groups[key] = group = new Group();

                    group.Size++;
                    Increment(group, (0, 0, ""));
                    for (int t = 0; t < projector.Count; t++)
                        Increment(group, (1, projector.FieldOf(t), Encoding.UTF8.GetString(projector.TermOf(t))));

                    for (int g = 0; g < members.Length; g++)
                    {
                        if ((projector.Membership & (1 << g)) != 0)
                            Increment(group, (2, g, ""));
                    }
                }
            }
        }

        result.InnerEntries = innerDocuments;

        var termLists = new Dictionary<(int, string), List<long>>();
        var any = new List<long>();
        var severalInner = new List<long>();
        var groupings = Enumerable.Range(0, plan.GroupingFields.Length).Select(_ => new List<long>()).ToArray();
        var outerKeyOf = new Dictionary<long, string[]>();
        long severalKeys = 0;
        long outerDocuments = 0;

        CompactTree outerIds = outer.TermsOf(plan.IdField);
        if (outerIds != null)
        {
            var iterator = outerIds.Iterate();
            iterator.Reset();
            using var scope = new CompactKeyCacheScope(outer.Snapshot.Transaction.LowLevelTransaction);
            while (iterator.MoveNext(scope.Key, out long value, out _))
            {
                outerDocuments++;
                long entry = ListStore.Decode(value);
                if (keyReader.Read(outer, entry, joinIndex) == false)
                    continue;

                var terms = new List<string>();
                for (int i = 0; i < keyReader.TermCount; i++)
                {
                    if (keyReader.TermAt(i).Length <= JoinLimits.MaxTermLength)
                        terms.Add(Encoding.UTF8.GetString(keyReader.TermAt(i)));
                }

                string[] keys = terms.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                if (keys.Length == 0)
                    continue;

                outerKeyOf[entry] = keys;
                int withGroup = 0;
                bool bigGroup = false;
                var atoms = new HashSet<(int Kind, int Field, string Term)>();
                foreach (string key in keys)
                {
                    seen.Keys.Add(key);
                    if (groups.TryGetValue(key, out Group group) == false)
                        continue;

                    withGroup++;
                    bigGroup |= group.Size >= 2;
                    foreach (((int kind, int field, string text), int count) in group.Atoms)
                    {
                        if (count > 0)
                            atoms.Add((kind, field, text));
                    }
                }

                if (withGroup >= 2)
                    severalKeys++;

                if (withGroup == 0)
                    continue;

                any.Add(entry);

                // joined with several inner entries: a key has a group of two or more, or two or more keys have a group
                if (bigGroup || withGroup >= 2)
                    severalInner.Add(entry);

                foreach ((int kind, int field, string text) in atoms)
                {
                    if (kind == 1)
                    {
                        if (termLists.TryGetValue((field, text), out List<long> list) == false)
                            termLists[(field, text)] = list = new List<long>();

                        list.Add(entry);
                        seen.ByField[field].Add(text);
                    }
                    else if (kind == 2)
                    {
                        groupings[field].Add(entry);
                    }
                }
            }
        }

        result.OuterEntries = outerDocuments;
        result.Joined = any.Count;

        for (int g = 0; g < members.Length; g++)
            Same($"members of grouping {g}", members[g].OrderBy(x => x).ToArray(), JoinIndexInvariantChecker.ReadList(joinIndex, joinIndex.SlotHandle(joinIndex.GroupingMembersSlot(g))), result);

        for (int field = 0; field < plan.Fields.Length; field++)
        {
            foreach (string term in seen.ByField[field])
            {
                long[] expected = termLists.TryGetValue((field, term), out List<long> list) ? list.OrderBy(x => x).ToArray() : [];
                long[] actual = JoinIndexInvariantChecker.ReadTerm(joinIndex, field, term) ?? [];
                Same($"field {plan.Definition.Fields[field].Field} term '{term}'", expected, actual, result);
            }
        }

        Same("any", any.OrderBy(x => x).ToArray(), JoinIndexInvariantChecker.ReadList(joinIndex, joinIndex.SlotHandle(JoinReader.AnySlot)), result);
        Same("outer entries joined with several inner entries", severalInner.OrderBy(x => x).ToArray(), JoinIndexInvariantChecker.ReadList(joinIndex, joinIndex.SlotHandle(joinIndex.SeveralInnerSlot)), result);
        for (int g = 0; g < groupings.Length; g++)
            Same($"grouping {g}", groupings[g].OrderBy(x => x).ToArray(), JoinIndexInvariantChecker.ReadList(joinIndex, joinIndex.SlotHandle(JoinReader.GroupingListSlot(g))), result);

        // the keys of every outer entry that has some, and no row for the others
        var stored = new ContextBoundNativeList<long>(allocator, 4);
        foreach ((long entry, string[] keys) in outerKeyOf.OrderBy(x => x.Key))
        {
            stored.Count = 0;
            bool has = joinIndex.TryGetOuterKeys(entry, ref stored);
            string actual = has ? string.Join(",", stored.ToSpan().ToArray()) : "none";
            long[] refs = KeyRefs(joinIndex, keys);
            if (has == false || refs == null || stored.ToSpan().ToArray().SequenceEqual(refs) == false)
                result.Mismatches.Add($"outer entry {entry} has the keys '{string.Join("','", keys)}': the join index holds {actual}, key references {(refs == null ? "unknown" : string.Join(",", refs))}");
        }

        if (joinIndex.NumberOfJoined != outerKeyOf.Count)
            result.Mismatches.Add($"outer entries with a key: expected {outerKeyOf.Count}, join index has {joinIndex.NumberOfJoined}");

        // the entries with two or more keys, whether the keys have an inner entry or not
        Same("entries with several keys", outerKeyOf.Where(x => x.Value.Length >= 2).Select(x => x.Key).OrderBy(x => x).ToArray(), JoinIndexInvariantChecker.ReadList(joinIndex, joinIndex.SlotHandle(joinIndex.MultiKeySlot)), result);

        // the keys of every inner document that has some
        foreach ((long documentKey, string[] keys) in innerKeyOf)
        {
            stored.Count = 0;
            bool has = joinIndex.TryGetInnerKeys(documentKey, ref stored);
            string actual = has ? string.Join(",", stored.ToSpan().ToArray()) : "none";
            long[] refs = KeyRefs(joinIndex, keys);
            if (has == false || refs == null || stored.ToSpan().ToArray().SequenceEqual(refs) == false)
                result.Mismatches.Add($"inner document {documentKey} has the keys '{string.Join("','", keys)}': the join index holds {actual}, key references {(refs == null ? "unknown" : string.Join(",", refs))}");
        }

        if (joinIndex.NumberOfInnerDocumentsWithKey != innerKeyOf.Count)
            result.Mismatches.Add($"inner documents with a key: expected {innerKeyOf.Count}, join index has {joinIndex.NumberOfInnerDocumentsWithKey}");

        // the counts and the atoms of every key, also of the keys that lost their group
        long pairs = 0;
        var codes = new ContextBoundNativeList<long>(allocator, 16);
        foreach (string key in seen.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            groups.TryGetValue(key, out Group group);
            bool known = joinIndex.TryGetKey(Encoding.UTF8.GetBytes(key), out long keyRef);
            var expected = new SortedDictionary<long, int>();
            if (group != null)
            {
                if (known == false)
                {
                    result.Mismatches.Add($"key '{key}' is not in the dictionary of the keys");
                    continue;
                }

                foreach (((int kind, int field, string text), int count) in group.Atoms)
                {
                    long code;
                    if (kind == 0)
                    {
                        code = JoinReader.CodeOfAny();
                    }
                    else if (kind == 1)
                    {
                        if (joinIndex.TryGetTerm(field, Encoding.UTF8.GetBytes(text), out long termRef, out _) == false)
                        {
                            result.Mismatches.Add($"key '{key}': the term '{text}' of the field {field} is not in the file");
                            continue;
                        }

                        code = JoinReader.CodeOfTerm(field, termRef);
                    }
                    else
                    {
                        code = JoinReader.CodeOfGrouping(field);
                    }

                    expected[code] = count;
                }
            }

            pairs += expected.Count;
            if (known == false)
                continue;

            // a group of several entries has the atom of the list of the outer entries that need to be checked entry by entry, without a count, after the others
            List<long> expectedAtoms = expected.Keys.ToList();
            if (group is { Size: >= 2 })
                expectedAtoms.Add(joinIndex.CodeOfSeveralInner);

            codes.Count = 0;
            joinIndex.ReadKeyAtoms(keyRef, ref codes);
            Same($"atoms of the key '{key}'", expectedAtoms.ToArray(), codes.ToSpan().ToArray(), result);
            foreach ((long code, int count) in expected)
            {
                int actual = joinIndex.GetPairCount(keyRef, code);
                if (actual != count)
                    result.Mismatches.Add($"key '{key}' atom code {code}: expected the count {count}, join index has {actual}");
            }
        }

        if (joinIndex.NumberOfPairCounts != pairs)
            result.Mismatches.Add($"counted pairs of a key and an atom: expected {pairs}, join index has {joinIndex.NumberOfPairCounts}");

        long several = groups.Values.Count(x => x.Size >= 2);
        if (joinIndex.RowsWithSeveralJoined != several)
            result.Mismatches.Add($"keys with a group of two or more inner entries: expected {several}, join index has {joinIndex.RowsWithSeveralJoined}");

        if (joinIndex.RowsWithSeveralKeys != severalKeys)
            result.Mismatches.Add($"outer entries with two or more keys that have an inner entry: expected {severalKeys}, join index has {joinIndex.RowsWithSeveralKeys}");

        if (joinIndex.NumberOfOuterDocuments != outerDocuments)
            result.Mismatches.Add($"outer documents: expected {outerDocuments}, join index has {joinIndex.NumberOfOuterDocuments}");

        if (joinIndex.NumberOfInnerDocuments != innerDocuments)
            result.Mismatches.Add($"inner documents: expected {innerDocuments}, join index has {joinIndex.NumberOfInnerDocuments}");

        stored.Dispose();
        codes.Dispose();
    }

    /// <summary>The references of the keys (sorted ascending, as the file keeps them), null when one is not in the dictionary of the keys.</summary>
    private static long[] KeyRefs(JoinIndexSnapshot joinIndex, string[] keys)
    {
        var refs = new long[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            if (joinIndex.TryGetKey(Encoding.UTF8.GetBytes(keys[i]), out refs[i]) == false)
                return null;
        }

        Array.Sort(refs);
        return refs;
    }

    private static void Increment(Group group, (int, int, string) atom) => group.Atoms[atom] = group.Atoms.GetValueOrDefault(atom) + 1;

    private static void Same(string what, long[] expected, long[] actual, JoinIndexInvariantChecker.Result result)
    {
        if (expected.SequenceEqual(actual) == false)
            result.Mismatches.Add($"{what}: expected {Describe(expected)}, join index has {Describe(actual)}");
    }

    private static string Describe(long[] ids) => ids.Length <= 12 ? $"[{string.Join(",", ids)}]" : $"{ids.Length} entries [{string.Join(",", ids.Take(6))},...]";
}
