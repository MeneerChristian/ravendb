using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FastTests.Server.Documents.Indexing.Joins.Refresh;

namespace FastTests.Server.Documents.Indexing.Joins.Fuzz;

internal enum FuzzShape
{
    /// <summary>ON m.ContextId = id(c): the key of the group is the id of the inner document.</summary>
    InnerId,

    /// <summary>ON m.ContextId = c.Code</summary>
    Field,

    /// <summary>ON id(m) = c.Code</summary>
    OuterId
}

internal enum FuzzKind
{
    PutOuter,
    DeleteOuter,
    PutInner,
    DeleteInner
}

internal enum FuzzAdminKind
{
    Restart,
    ResetJoin,
    ResetOuterIndex,
    ResetInnerIndex,
    PauseJoin
}

/// <summary>One write. It is complete in itself (no reference to what the database holds), so a plan can be replayed with some of its writes left out.</summary>
internal sealed record FuzzOp(FuzzKind Kind, int Number, string[] Keys, string Country, string Name, int Rank, bool Active, string Title, string Why)
{
    private static string KeysText(string[] keys) => keys == null ? "none" : "[" + string.Join(",", keys.Select(k => k == null ? "<null>" : k.Length == 0 ? "<empty>" : k)) + "]";

    public override string ToString() => Kind switch
    {
        FuzzKind.PutOuter => $"+O{Number}{KeysText(Keys)} '{Title}'({Why})",
        FuzzKind.DeleteOuter => $"-O{Number}",
        FuzzKind.PutInner => $"+I{Number}{KeysText(Keys)} {Country}/{Name}/r{Rank}/{(Active ? "act" : "off")}'{Title}'({Why})",
        FuzzKind.DeleteInner => $"-I{Number}",
        _ => throw new ArgumentOutOfRangeException()
    };
}

internal sealed record FuzzAdmin(FuzzAdminKind Kind, int AfterBatch)
{
    public override string ToString() => $"{Kind}@{AfterBatch}";
}

internal sealed record FuzzQuery(string Sub, string Tail, bool Left)
{
    public override string ToString() => $"{(Left ? "left " : "")}[{Sub.Trim()}]{Tail}";
}

internal sealed class FuzzRound
{
    public string Theme { get; init; }

    public List<List<FuzzOp>> Batches { get; init; } = [];

    public List<FuzzAdmin> Admin { get; init; } = [];

    public List<FuzzQuery> Queries { get; init; } = [];
}

/// <summary>Everything that a run is made of, derived from the seed alone: the sizes, the thresholds that the workload has to cross and the shape of the join.</summary>
internal sealed class FuzzSettings
{
    public int Seed { get; init; }

    public FuzzShape Shape { get; init; }

    /// <summary>The key fields of both sides hold arrays (the documents have several keys), else one key.</summary>
    public bool Arrays { get; init; }

    /// <summary>The database is on disk, so that it can be restarted.</summary>
    public bool Persistent { get; init; }

    public int MaxIds { get; init; }

    public int MaxUnindexedIds { get; init; }

    public int MaxInnerListChanges { get; init; }

    public int MinAtomEntriesForDirectWrite { get; init; }

    /// <summary>The limit of the resident cache for a patch of documents (<c>ResidentCacheOptions.MaxPatchDocuments</c>, no configuration key).</summary>
    public int MaxPatchDocuments { get; init; }

    public int Contexts { get; init; }

    public int Conversations { get; init; }

    public int Rounds { get; init; }

    public static FuzzSettings For(int seed, FuzzShape shape, int rounds)
    {
        // the thresholds come from a generator of their own: the writes of the plan do not depend on them
        var random = new Random(seed * 7919 + 13);
        int[] maxIds = [3, 7, 20, 1_000, 50_000];
        int[] maxUnindexed = [3, 50, 1_000_000];
        int[] innerChanges = [0, 1, 3, 30];
        int[] atomDirect = [0, 1, 6];
        int[] patchDocuments = [1, 5, 40, 5_000];
        int[] contexts = [6, 16, 40];
        int[] conversations = [12, 40, 90];
        return new FuzzSettings
        {
            Seed = seed,
            Shape = shape,
            Arrays = (seed & 1) == 1,
            Persistent = seed % 3 == 0,
            MaxIds = maxIds[random.Next(maxIds.Length)],
            MaxUnindexedIds = maxUnindexed[random.Next(maxUnindexed.Length)],
            MaxInnerListChanges = innerChanges[random.Next(innerChanges.Length)],
            MinAtomEntriesForDirectWrite = atomDirect[random.Next(atomDirect.Length)],
            MaxPatchDocuments = patchDocuments[random.Next(patchDocuments.Length)],
            Contexts = contexts[random.Next(contexts.Length)],
            Conversations = conversations[random.Next(conversations.Length)],
            Rounds = rounds
        };
    }

    public override string ToString() => $"seed={Seed} shape={Shape} arrays={Arrays} persistent={Persistent} MaxIdsPerCommit={MaxIds} MaxUnindexedIdsPerCommit={MaxUnindexedIds} " +
                                         $"MaxInnerListChangesPerOuterEntry={MaxInnerListChanges} MinAtomEntriesForDirectWrite={MinAtomEntriesForDirectWrite} MaxPatchDocuments={MaxPatchDocuments} " +
                                         $"contexts={Contexts} conversations={Conversations} rounds={Rounds}";
}

internal sealed class FuzzPlan
{
    public FuzzSettings Settings { get; init; }

    public List<FuzzRound> Rounds { get; init; } = [];

    public int OpCount => Rounds.Sum(r => r.Batches.Sum(b => b.Count));

    // order of the weights: new outer, keep outer key, move outer key, delete outer, new inner, keep inner key, move inner key, delete inner
    private static readonly (string Name, int[] Weights)[] Themes =
    [
        ("mixed", [14, 8, 10, 8, 14, 12, 8, 6]),
        ("outerOnly", [25, 15, 25, 35, 0, 0, 0, 0]),
        ("innerOnly", [0, 0, 0, 0, 25, 30, 20, 25]),
        ("innerKeepKey", [0, 0, 0, 0, 5, 90, 0, 5]),
        ("innerMoveKey", [0, 0, 0, 0, 10, 10, 75, 5]),
        ("outerKeepKey", [5, 90, 5, 0, 0, 0, 0, 0]),
        ("outerMoveKey", [5, 5, 85, 5, 0, 0, 0, 0]),
        ("wipeInner", [0, 0, 0, 0, 0, 0, 0, 100]),
        ("wipeOuter", [0, 0, 0, 100, 0, 0, 0, 0]),
        ("burst", [14, 8, 10, 8, 14, 12, 8, 6])
    ];

    public static FuzzPlan Create(FuzzSettings s)
    {
        var random = new Random(s.Seed);
        var outer = new SortedDictionary<int, FuzzOp>();
        var inner = new SortedDictionary<int, FuzzOp>();
        var previous = new Dictionary<(bool Outer, int Number), string[]>();
        var plan = new FuzzPlan { Settings = s };
        int unit = Math.Min(s.MaxIds, 30);

        for (int r = 0; r <= s.Rounds; r++)
        {
            (string name, int[] weights) = r == 0 ? ("preload", Themes[0].Weights) : Themes[random.Next(Themes.Length)];
            int count = r == 0 ? s.Contexts + s.Conversations / 2 : name switch
            {
                "wipeInner" => inner.Count,
                "wipeOuter" => outer.Count,
                "burst" => Math.Min(150, 3 * unit + 5),
                _ => Math.Min(120, new[] { 2, 5, unit - 1, unit, unit + 1, 2 * unit + 1, 4 * unit }[random.Next(7)])
            };

            int[] batchSizes = r == 0 ? [25] : [1, Math.Max(1, unit - 1), unit, unit + 1, 1_000];
            int batchSize = batchSizes[random.Next(batchSizes.Length)];
            var round = new FuzzRound { Theme = name };
            var batch = new List<FuzzOp>();
            var used = new HashSet<(bool Outer, int Number)>();

            for (int i = 0; i < count; i++)
            {
                FuzzOp op = NextOp(random, s, weights, outer, inner, previous);
                if (op == null)
                    continue;

                bool isOuter = op.Kind is FuzzKind.PutOuter or FuzzKind.DeleteOuter;
                if (batch.Count >= batchSize || used.Contains((isOuter, op.Number)))
                {
                    round.Batches.Add(batch);
                    batch = [];
                    used.Clear();
                }

                batch.Add(op);
                used.Add((isOuter, op.Number));
                Apply(op, outer, inner, previous);
            }

            if (batch.Count > 0)
                round.Batches.Add(batch);

            if (r > 0)
                AddAdmin(random, s, round);

            string[] subs = JoinFuzzQueries.Subs;
            for (int q = 0; q < 6; q++)
            {
                bool left = random.Next(5) == 0;
                string tail = random.Next(2) == 0 ? JoinFuzzQueries.UnscoredTails[random.Next(JoinFuzzQueries.UnscoredTails.Length)] : JoinFuzzQueries.ScoredTails[random.Next(JoinFuzzQueries.ScoredTails.Length)];
                round.Queries.Add(new FuzzQuery(subs[random.Next(subs.Length)], tail, left));
            }

            plan.Rounds.Add(round);
        }

        return plan;
    }

    private static void AddAdmin(Random random, FuzzSettings s, FuzzRound round)
    {
        int batches = round.Batches.Count;
        int roll = random.Next(100);
        if (roll < 18 && s.Persistent)
            round.Admin.Add(new FuzzAdmin(FuzzAdminKind.Restart, random.Next(batches + 1)));
        else if (roll < 26)
            round.Admin.Add(new FuzzAdmin(FuzzAdminKind.ResetJoin, random.Next(batches + 1)));
        else if (roll < 31)
            round.Admin.Add(new FuzzAdmin(FuzzAdminKind.ResetOuterIndex, random.Next(batches + 1)));
        else if (roll < 36)
            round.Admin.Add(new FuzzAdmin(FuzzAdminKind.ResetInnerIndex, random.Next(batches + 1)));
        else if (roll < 48)
            round.Admin.Add(new FuzzAdmin(FuzzAdminKind.PauseJoin, random.Next(batches + 1)));
    }

    private static void Apply(FuzzOp op, SortedDictionary<int, FuzzOp> outer, SortedDictionary<int, FuzzOp> inner, Dictionary<(bool Outer, int Number), string[]> previous)
    {
        if (op.Kind == FuzzKind.PutOuter && outer.TryGetValue(op.Number, out FuzzOp was) && SameKeys(was.Keys, op.Keys) == false)
            previous[(true, op.Number)] = was.Keys;
        else if (op.Kind == FuzzKind.PutInner && inner.TryGetValue(op.Number, out was) && SameKeys(was.Keys, op.Keys) == false)
            previous[(false, op.Number)] = was.Keys;

        switch (op.Kind)
        {
            case FuzzKind.PutOuter: outer[op.Number] = op; break;
            case FuzzKind.DeleteOuter: outer.Remove(op.Number); break;
            case FuzzKind.PutInner: inner[op.Number] = op; break;
            case FuzzKind.DeleteInner: inner.Remove(op.Number); break;
        }
    }

    private static bool SameKeys(string[] a, string[] b) => a == null || b == null ? a == b : a.SequenceEqual(b);

    /// <summary>
    /// The keys that a document moves to: other keys, the same keys in another order, the same number of keys with one of them replaced, a key twice, the keys it had before (a key that moves and returns), or none.
    /// </summary>
    private static string[] MovedKeys(Random random, string[] old, string[] fresh, string[] before)
    {
        switch (random.Next(7))
        {
            case 1 when old is { Length: > 1 }:
                return old.Reverse().ToArray();
            case 2 when old is { Length: > 1 } && fresh is { Length: > 0 }:
                string[] replaced = (string[])old.Clone();
                replaced[random.Next(replaced.Length)] = fresh[0];
                return replaced;
            case 3 when old is { Length: > 0 }:
                return [.. old, old[random.Next(old.Length)]];
            case 4 when before != null:
                return before;
            default:
                return fresh;
        }
    }

    private static T PickOf<T>(Random random, SortedDictionary<int, T> docs) => docs.ElementAt(random.Next(docs.Count)).Value;

    private static FuzzOp NextOp(Random random, FuzzSettings s, int[] weights, SortedDictionary<int, FuzzOp> outer, SortedDictionary<int, FuzzOp> inner, Dictionary<(bool Outer, int Number), string[]> previous)
    {
        int total = weights.Sum();
        int roll = random.Next(total);
        int which = 0;
        while (roll >= weights[which])
        {
            roll -= weights[which];
            which++;
        }

        // a change of a document that does not exist is a new document
        if (which is 1 or 2 && outer.Count == 0)
            which = 0;

        if (which is 5 or 6 && inner.Count == 0)
            which = 4;

        if (which == 3 && outer.Count == 0 || which == 7 && inner.Count == 0)
            return null;

        switch (which)
        {
            case 0:
            {
                int number = random.Next(s.Conversations);
                return NewOuter(random, s, number, "new");
            }
            case 1:
            {
                FuzzOp old = PickOf(random, outer);
                return old with { Title = TitleOf(random), Why = "keepKey" };
            }
            case 2:
            {
                FuzzOp old = PickOf(random, outer);
                return old with { Keys = MovedKeys(random, old.Keys, OuterKeys(random, s), previous.GetValueOrDefault((true, old.Number))), Why = "moveKey" };
            }
            case 3:
                return new FuzzOp(FuzzKind.DeleteOuter, PickOf(random, outer).Number, null, null, null, 0, false, null, "delete");
            case 4:
            {
                int number = random.Next(random.Next(4) == 0 ? 2 : s.Contexts);
                return NewInner(random, s, number, "new");
            }
            case 5:
            {
                FuzzOp old = PickOf(random, inner);
                FuzzOp fresh = NewInner(random, s, old.Number, "keepKey");
                return fresh with { Keys = old.Keys };
            }
            case 6:
            {
                FuzzOp old = PickOf(random, inner);
                return old with { Keys = MovedKeys(random, old.Keys, InnerKeys(random, s), previous.GetValueOrDefault((false, old.Number))), Why = s.Shape == FuzzShape.InnerId ? "keepKey(id)" : "moveKey" };
            }
            default:
                return new FuzzOp(FuzzKind.DeleteInner, PickOf(random, inner).Number, null, null, null, 0, false, null, "delete");
        }
    }

    private static string TitleOf(Random random)
    {
        int roll = random.Next(12);
        return roll < 4 ? "a" : roll < 11 ? "b" : "c";
    }

    private static FuzzOp NewOuter(Random random, FuzzSettings s, int number, string why) => new(FuzzKind.PutOuter, number, OuterKeys(random, s), null, null, 0, false, TitleOf(random), why);

    private static FuzzOp NewInner(Random random, FuzzSettings s, int number, string why)
    {
        string country = random.Next(3) == 0 ? "nl" : "c" + random.Next(4);
        string title = string.Join(' ', Enumerable.Range(0, random.Next(0, 4)).Select(_ => "w" + random.Next(6)));
        return new FuzzOp(FuzzKind.PutInner, number, InnerKeys(random, s), country, "n" + random.Next(5), random.Next(50), random.Next(3) != 0, title, why);
    }

    // the key of a conversation: the id of a context, a code, or nothing (the id of the conversation is the key)
    private static string[] OuterKeys(Random random, FuzzSettings s)
    {
        switch (s.Shape)
        {
            case FuzzShape.InnerId:
                if (s.Arrays)
                    return JoinIndexHarness.RandomKeys(random, s.Contexts);

                int context = random.Next(2) == 0 ? 0 : random.Next(s.Contexts + 4);
                return random.Next(12) == 0 ? null : [$"contexts/{context}"];
            case FuzzShape.Field:
                if (s.Arrays)
                    return JoinIndexHarness.RandomCodes(random, 4);

                string code = JoinIndexHarness.RandomCode(random);
                return code == null ? null : [code];
            default:
                return null;
        }
    }

    // the key of a context: a code, the ids of conversations, or nothing (the id of the context is the key)
    private static string[] InnerKeys(Random random, FuzzSettings s)
    {
        switch (s.Shape)
        {
            case FuzzShape.Field:
                if (s.Arrays)
                    return JoinIndexHarness.RandomCodes(random, 4);

                string code = JoinIndexHarness.RandomCode(random);
                return code == null ? null : [code];
            case FuzzShape.OuterId:
                return s.Arrays ? JoinIndexHarness.RandomConversationIds(random, 4, s.Conversations) : JoinIndexHarness.RandomConversationIds(random, 1, s.Conversations);
            default:
                return null;
        }
    }

    /// <summary>A copy without the writes (numbered across the plan in order) and the admin steps (numbered after the writes) in <paramref name="removed"/>, and with only the first <paramref name="rounds"/> rounds.</summary>
    public FuzzPlan Without(HashSet<int> removed, int rounds)
    {
        int index = 0;
        var copy = new FuzzPlan { Settings = Settings };
        foreach (FuzzRound round in Rounds.Take(rounds))
        {
            var batches = new List<List<FuzzOp>>();
            foreach (List<FuzzOp> batch in round.Batches)
            {
                var kept = new List<FuzzOp>();
                foreach (FuzzOp op in batch)
                {
                    if (removed.Contains(index++) == false)
                        kept.Add(op);
                }

                if (kept.Count > 0)
                    batches.Add(kept);
            }

            copy.Rounds.Add(new FuzzRound { Theme = round.Theme, Batches = batches, Queries = round.Queries, Admin = round.Admin.Select(a => a with { AfterBatch = Math.Min(a.AfterBatch, batches.Count) }).ToList() });
        }

        int admin = OpCount;
        foreach (FuzzRound round in copy.Rounds)
            round.Admin.RemoveAll(_ => removed.Contains(admin++));

        return copy;
    }

    public int AdminCount => Rounds.Sum(r => r.Admin.Count);

    public string Describe(int upToRound = int.MaxValue)
    {
        var text = new StringBuilder();
        for (int r = 0; r < Rounds.Count && r <= upToRound; r++)
        {
            FuzzRound round = Rounds[r];
            text.AppendLine($"round {r} [{round.Theme}] batches={string.Join("/", round.Batches.Select(b => b.Count))} admin=[{string.Join(", ", round.Admin)}]");
            for (int b = 0; b < round.Batches.Count; b++)
                text.AppendLine($"  batch {b}: {string.Join(" ", round.Batches[b])}");
        }

        return text.ToString();
    }
}

internal static class JoinFuzzQueries
{
    public static readonly string[] Subs =
    [
        "",
        " where Country = 'nl'",
        " where Country = 'c1'",
        " where Country = 'C1'",
        " where Name = 'n1'",
        " where Rank = 10",
        " where IsActive = true",
        " where IsActive != true",
        " where Country != 'nl'",
        " where Country = 'nl' and IsActive = true",
        " where Country = 'c2' and Name = 'n3' and IsActive = true",
        " where Country != 'c1' and IsActive = true",
        " where Country = 'nobody'",
        " where Rank > 20",
        " where Rank between 5 and 15",
        " where startsWith(Name, 'n')",
        " where Country = 'nl' or Country = 'c1'",
        " where Country in ('nl', 'c2', 'nobody')",
        " where Rank < 7 or Rank > 45"
    ];

    // the score of the join is not used: the query is ordered by a field of the main documents
    public static readonly string[] UnscoredTails =
    [
        " order by m.Title limit 0, 10",
        " order by m.Title desc limit 3, 7",
        " where m.Title = 'a' order by m.Title limit 0, 1000",
        " order by m.Title limit 1000, 5",
        " where m.Title = 'b' order by m.Title desc limit 0, 20"
    ];

    public static readonly string[] ScoredTails =
    [
        " limit 0, 1000",
        " order by score() limit 0, 7",
        " order by score() desc limit 3, 7",
        " where m.Title = 'a' order by score() limit 0, 5",
        " boost 2 order by score() limit 0, 20"
    ];

    /// <summary>The query that every round has to see served by the join index (it does not use the score, which a join with several keys per entry does not serve).</summary>
    public static readonly FuzzQuery Probe = new("", " order by m.Title limit 0, 1000", false);

    public static string Rql(FuzzShape shape, FuzzQuery q)
    {
        string on = shape switch
        {
            FuzzShape.InnerId => "m.ContextId = id(c)",
            FuzzShape.Field => "m.ContextId = c.Code",
            FuzzShape.OuterId => "id(m) = c.Code",
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        return $"{Query.JoinIndexQueryOracle.Outer} {(q.Left ? "left join" : "join")} (from index '{JoinIndexHarness.InnerName}'{q.Sub}) as c on {on}{q.Tail}";
    }
}
