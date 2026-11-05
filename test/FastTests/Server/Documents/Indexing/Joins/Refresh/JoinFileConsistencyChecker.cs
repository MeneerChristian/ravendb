using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Sparrow.Server;
using Sparrow.Threading;
using Voron.Util;

namespace FastTests.Server.Documents.Indexing.Joins.Refresh;

/// <summary>
/// The lists of a join index against its own rows, for a join index that is not at the generation of the indexes (between two chunks of a refresh): every outer row of a document that the test names,
/// the inner rows it names and their terms must give exactly the lists the file has. It needs no snapshot of an index, so it holds for an intermediate state too.
/// </summary>
public static class JoinFileConsistencyChecker
{
    public sealed record Result(bool Partial, long OuterRows, long Joined, List<string> Mismatches)
    {
        public bool Ok => Mismatches.Count == 0;

        public override string ToString() => $"{(Ok ? "OK" : "MISMATCH")} partial {Partial}: {OuterRows} outer rows, {Joined} joined{(Ok ? "" : Environment.NewLine + string.Join(Environment.NewLine, Mismatches.Take(20)))}";
    }

    /// <summary>Checks the join index of the id join (<see cref="JoinIndexHarness.JoinIndexDefinition"/>) or of the join on a field over the documents conversations/0..conversations-1 and contexts/0..contexts-1.</summary>
    public static Result Check(JoinIndex joinIndex, int conversations, int contexts)
    {
        if (joinIndex.TryOpenJoinSnapshot(out JoinIndexSnapshot file) == false)
            throw new InvalidOperationException("The join index cannot be read now.");

        using (file)
            return Check(file, joinIndex.Plan, conversations, contexts);
    }

    /// <summary>The same for a file that is not the file of a join index.</summary>
    public static Result Check(JoinIndexSnapshot file, JoinPlan plan, int conversations, int contexts)
    {
        if (plan.IsFieldJoin)
            return CheckFieldJoin(file, plan, conversations, contexts);

        using var allocator = new ByteStringContext(SharedMultipleUseFlag.None);
        var keys = new ContextBoundNativeList<long>(allocator, 8);
        var refs = new ContextBoundNativeList<long>(allocator, 8);
        var mismatches = new List<string>();

        var members = new long[plan.GroupingFields.Length][];
        for (int g = 0; g < members.Length; g++)
            members[g] = Read(file, file.SlotHandle(file.GroupingMembersSlot(g)));

        var expectedAny = new List<long>();
        var expectedGroupings = Enumerable.Range(0, members.Length).Select(_ => new List<long>()).ToArray();
        var expectedTerms = new Dictionary<(int Field, long TermRef), List<long>>();
        long rows = 0;
        long joined = 0;

        for (int c = 0; c < conversations; c++)
        {
            byte[] id = Encoding.UTF8.GetBytes($"conversations/{c}");
            if (file.TryGetOuterDocument(JoinFileRows.HashDocumentId(id, file.Seed), out long entry) == false)
                continue;

            rows++;
            keys.Count = 0;
            if (file.TryGetOuterKeys(entry, ref keys) == false)
                continue;

            bool any = false;
            var terms = new HashSet<(int, long)>();
            int membership = 0;
            foreach (long key in keys.ToSpan().ToArray())
            {
                if (file.TryGetInnerDocument(key, out _) == false)
                    continue;

                any = true;
                for (int f = 0; f < plan.Fields.Length; f++)
                {
                    refs.Count = 0;
                    file.TryGetInnerTerms(f, key, ref refs);
                    foreach (long termRef in refs.ToSpan())
                        terms.Add((f, termRef));
                }

                for (int g = 0; g < members.Length; g++)
                {
                    if (Array.BinarySearch(members[g], key) >= 0)
                        membership |= 1 << g;
                }
            }

            if (any == false)
                continue;

            joined++;
            expectedAny.Add(entry);
            foreach ((int field, long termRef) in terms)
            {
                if (expectedTerms.TryGetValue((field, termRef), out List<long> list) == false)
                    expectedTerms[(field, termRef)] = list = new List<long>();

                list.Add(entry);
            }

            for (int g = 0; g < members.Length; g++)
            {
                if ((membership & (1 << g)) != 0)
                    expectedGroupings[g].Add(entry);
            }
        }

        // the terms of every inner row, so that a list of a term that no outer row has any more is compared too
        for (int x = 0; x < contexts; x++)
        {
            byte[] id = Encoding.UTF8.GetBytes($"contexts/{x}");
            long key = file.InnerKeyOf(id);
            if (file.TryGetInnerDocument(key, out _) == false)
                continue;

            for (int f = 0; f < plan.Fields.Length; f++)
            {
                refs.Count = 0;
                file.TryGetInnerTerms(f, key, ref refs);
                foreach (long termRef in refs.ToSpan())
                    expectedTerms.TryAdd((f, termRef), new List<long>());
            }
        }

        Compare("any", expectedAny, Read(file, file.SlotHandle(JoinReader.AnySlot)), mismatches);
        for (int g = 0; g < members.Length; g++)
            Compare($"grouping {g}", expectedGroupings[g], Read(file, file.SlotHandle(JoinReader.GroupingListSlot(g))), mismatches);

        foreach (((int field, long termRef), List<long> expected) in expectedTerms)
            Compare($"field {field} term {termRef}", expected, Read(file, file.GetListHandle(field, termRef)), mismatches);

        keys.Dispose();
        refs.Dispose();
        return new Result(file.Generation.IsPartial, rows, joined, mismatches);
    }

    private sealed class Group
    {
        public int Size;
        public readonly SortedDictionary<long, int> Atoms = new();
    }

    private static Result CheckFieldJoin(JoinIndexSnapshot file, JoinPlan plan, int conversations, int contexts)
    {
        using var allocator = new ByteStringContext(SharedMultipleUseFlag.None);
        var keys = new ContextBoundNativeList<long>(allocator, 8);
        var refs = new ContextBoundNativeList<long>(allocator, 8);
        var mismatches = new List<string>();

        var members = new long[plan.GroupingFields.Length][];
        for (int g = 0; g < members.Length; g++)
            members[g] = Read(file, file.SlotHandle(file.GroupingMembersSlot(g)));

        // the group of a key: the inner rows that name it and the atoms they have
        var groups = new Dictionary<long, Group>();
        long innerRows = 0;
        for (int x = 0; x < contexts; x++)
        {
            long key = file.InnerKeyOf(Encoding.UTF8.GetBytes($"contexts/{x}"));
            if (file.TryGetInnerDocument(key, out _) == false)
                continue;

            innerRows++;
            keys.Count = 0;
            if (file.TryGetInnerKeys(key, ref keys) == false)
                continue;

            var codes = new List<long> { JoinReader.CodeOfAny() };
            for (int f = 0; f < plan.Fields.Length; f++)
            {
                refs.Count = 0;
                file.TryGetInnerTerms(f, key, ref refs);
                foreach (long termRef in refs.ToSpan())
                    codes.Add(JoinReader.CodeOfTerm(f, termRef));
            }

            for (int g = 0; g < members.Length; g++)
            {
                if (Array.BinarySearch(members[g], key) >= 0)
                    codes.Add(JoinReader.CodeOfGrouping(g));
            }

            foreach (long keyRef in keys.ToSpan().ToArray())
            {
                if (groups.TryGetValue(keyRef, out Group group) == false)
                    groups[keyRef] = group = new Group();

                group.Size++;
                foreach (long code in codes)
                    group.Atoms[code] = group.Atoms.GetValueOrDefault(code) + 1;
            }
        }

        var expectedLists = new Dictionary<long, List<long>>();
        var severalInner = new List<long>();
        long rows = 0;
        long joined = 0;
        long severalKeys = 0;
        foreach (Group group in groups.Values)
        {
            foreach (long code in group.Atoms.Keys)
                expectedLists.TryAdd(code, new List<long>());
        }

        for (int c = 0; c < conversations; c++)
        {
            if (file.TryGetOuterDocument(JoinFileRows.HashDocumentId(Encoding.UTF8.GetBytes($"conversations/{c}"), file.Seed), out long entry) == false)
                continue;

            rows++;
            keys.Count = 0;
            if (file.TryGetOuterKeys(entry, ref keys) == false)
                continue;

            int withGroup = 0;
            bool bigGroup = false;
            var atoms = new HashSet<long>();
            foreach (long keyRef in keys.ToSpan().ToArray())
            {
                if (groups.TryGetValue(keyRef, out Group group) == false)
                    continue;

                withGroup++;
                bigGroup |= group.Size >= 2;
                atoms.UnionWith(group.Atoms.Keys);
            }

            if (withGroup >= 2)
                severalKeys++;

            if (withGroup == 0)
                continue;

            joined++;
            if (bigGroup || withGroup >= 2)
                severalInner.Add(entry);

            foreach (long code in atoms)
                expectedLists[code].Add(entry);
        }

        foreach ((long code, List<long> expected) in expectedLists)
        {
            long atom = JoinReader.AtomOfCode(code);
            long handle = JoinReader.IsTermAtom(atom)
                ? file.GetListHandle(JoinReader.FieldOfAtom(atom), JoinReader.TermOfAtom(atom))
                : file.SlotHandle(JoinReader.SlotOfAtom(atom));
            Compare($"atom code {code}", expected, Read(file, handle), mismatches);
        }

        for (int g = 0; g < members.Length; g++)
        {
            if (expectedLists.ContainsKey(JoinReader.CodeOfGrouping(g)) == false)
                Compare($"grouping {g}", new List<long>(), Read(file, file.SlotHandle(JoinReader.GroupingListSlot(g))), mismatches);
        }

        if (expectedLists.ContainsKey(JoinReader.CodeOfAny()) == false)
            Compare("any", new List<long>(), Read(file, file.SlotHandle(JoinReader.AnySlot)), mismatches);

        Compare("outer entries joined with several inner entries", severalInner, Read(file, file.SlotHandle(file.SeveralInnerSlot)), mismatches);

        // the atoms and the counts of every key a row names
        long pairs = 0;
        var actualCodes = new ContextBoundNativeList<long>(allocator, 16);
        foreach ((long keyRef, Group group) in groups)
        {
            pairs += group.Atoms.Count;
            var expectedAtoms = group.Atoms.Keys.ToList();
            if (group.Size >= 2)
                expectedAtoms.Add(file.CodeOfSeveralInner);

            actualCodes.Count = 0;
            file.ReadKeyAtoms(keyRef, ref actualCodes);
            if (expectedAtoms.SequenceEqual(actualCodes.ToSpan().ToArray()) == false)
                mismatches.Add($"atoms of the key {keyRef}: expected {expectedAtoms.Count}, file has {actualCodes.Count}");

            foreach ((long code, int count) in group.Atoms)
            {
                int actual = file.GetPairCount(keyRef, code);
                if (actual != count)
                    mismatches.Add($"key {keyRef} atom code {code}: expected the count {count}, file has {actual}");
            }
        }

        if (file.NumberOfPairCounts != pairs)
            mismatches.Add($"counted pairs of a key and an atom: expected {pairs}, file has {file.NumberOfPairCounts}");

        long several = groups.Values.Count(x => x.Size >= 2);
        if (file.RowsWithSeveralJoined != several)
            mismatches.Add($"keys with a group of two or more inner entries: expected {several}, file has {file.RowsWithSeveralJoined}");

        if (file.RowsWithSeveralKeys != severalKeys)
            mismatches.Add($"outer entries with two or more keys that have an inner entry: expected {severalKeys}, file has {file.RowsWithSeveralKeys}");

        keys.Dispose();
        refs.Dispose();
        actualCodes.Dispose();
        return new Result(file.Generation.IsPartial, rows, joined, mismatches);
    }

    private static long[] Read(JoinReader reader, long handle) => JoinIndexInvariantChecker.ReadList(reader, handle);

    private static void Compare(string what, List<long> expected, long[] actual, List<string> mismatches)
    {
        long[] sorted = expected.OrderBy(x => x).ToArray();
        if (sorted.SequenceEqual(actual) == false)
            mismatches.Add($"{what}: expected {sorted.Length} entries [{string.Join(",", sorted.Take(8))}], file has {actual.Length} [{string.Join(",", actual.Take(8))}]");
    }
}
