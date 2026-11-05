using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;

namespace FastTests.Server.Documents.Indexing.Joins.Resident;

/// <summary>
/// The outer index as the cache sees it: documents with a row (entry id, keys) and immutable snapshots with a Voron transaction id and the posting list of every key. The entry ids follow the rule of Corax:
/// an update keeps the id only for a single id() term, otherwise the entry is new, ids of the outer environment never repeat. <see cref="IdPolicy.ReuseFreed"/> is the behaviour that the cache must also survive.
/// </summary>
public sealed class ResidentOuterModel
{
    public enum IdPolicy { KeepOnUpdate, AlwaysNew, ReuseFreed }

    public readonly record struct Row(long Entry, long[] Keys);

    public readonly record struct RowChange(long OldEntry, long[] OldKeys, long NewEntry, long[] NewKeys);

    public sealed class Snapshot
    {
        public Guid EnvironmentId;
        public long TransactionId;
        public long LastEntryId;
        public Dictionary<long, long[]> Lists = new();
        public Dictionary<long, Row> Rows = new();

        public long[] ListOf(long keyRef) => Lists.TryGetValue(keyRef, out long[] list) ? list : [];

        /// <summary>The entries of the rows that have at least one of the keys.</summary>
        public long[] RowsOfAny(ReadOnlySpan<long> keys)
        {
            var set = new SortedSet<long>();
            foreach (long key in keys)
            {
                foreach (long entry in ListOf(key))
                    set.Add(entry);
            }

            return set.ToArray();
        }

        public bool AnyRowWithSeveralOf(ReadOnlySpan<long> keys)
        {
            var seen = new HashSet<long>();
            foreach (long key in keys)
            {
                foreach (long entry in ListOf(key))
                {
                    if (seen.Add(entry) == false)
                        return true;
                }
            }

            return false;
        }
    }

    private readonly Dictionary<string, Row> _docs = new();
    private readonly SortedSet<long> _freed = [];
    private readonly IdPolicy _policy;
    private long _nextEntry;

    public ResidentOuterModel(IdPolicy policy, long firstEntry = 1)
    {
        _policy = policy;
        _nextEntry = firstEntry;
        Current = Build(10);
        History[Current.TransactionId] = Current;
    }

    public Guid EnvironmentId { get; } = Guid.NewGuid();

    public Snapshot Current { get; private set; }

    /// <summary>Every snapshot by transaction id.</summary>
    public Dictionary<long, Snapshot> History { get; } = new();

    public IEnumerable<string> Documents => _docs.Keys;

    public bool Has(string doc) => _docs.ContainsKey(doc);

    public Row RowOf(string doc) => _docs[doc];

    public static long KeyRef(int key) => 1_000 + key * 37L;

    public static long[] KeyRefs(params int[] keys) => keys.Select(KeyRef).Distinct().Order().ToArray();

    private long NewEntry()
    {
        if (_policy == IdPolicy.ReuseFreed && _freed.Count > 0)
        {
            long entry = _freed.Min;
            _freed.Remove(entry);
            return entry;
        }

        return _nextEntry++;
    }

    private Snapshot Build(long transactionId)
    {
        var snapshot = new Snapshot { EnvironmentId = EnvironmentId, TransactionId = transactionId, LastEntryId = _nextEntry - 1 };
        var lists = new Dictionary<long, List<long>>();
        foreach ((string _, Row row) in _docs)
        {
            snapshot.Rows[row.Entry] = row;
            foreach (long key in row.Keys)
            {
                if (lists.TryGetValue(key, out List<long> list) == false)
                    lists[key] = list = [];

                list.Add(row.Entry);
            }
        }

        foreach ((long key, List<long> list) in lists)
        {
            list.Sort();
            snapshot.Lists[key] = list.ToArray();
        }

        return snapshot;
    }

    /// <summary>A write transaction that changes nothing: the transaction id moves.</summary>
    public Snapshot Touch() => Publish(Build(Current.TransactionId + 1));

    private Snapshot Publish(Snapshot snapshot)
    {
        History[snapshot.TransactionId] = snapshot;
        return Current = snapshot;
    }

    /// <summary>One write transaction: upserts (keys null or empty: a document without a key) and deletes; the changes are per document, in the order that the refresh would see them.</summary>
    public List<RowChange> Write(IEnumerable<(string Doc, long[] Keys)> upserts, IEnumerable<string> deletes)
    {
        var changes = new List<RowChange>();
        foreach (string doc in deletes)
        {
            if (_docs.Remove(doc, out Row old) == false)
                continue;

            _freed.Add(old.Entry);
            changes.Add(new RowChange(old.Entry, old.Keys, 0, []));
        }

        foreach ((string doc, long[] keys) in upserts)
        {
            long[] sorted = (keys ?? []).Distinct().Order().ToArray();
            if (_docs.TryGetValue(doc, out Row old))
            {
                long entry = old.Entry;
                if (_policy != IdPolicy.KeepOnUpdate)
                {
                    _freed.Add(old.Entry);
                    entry = NewEntry();
                }

                _docs[doc] = new Row(entry, sorted);
                changes.Add(new RowChange(old.Entry, old.Keys, entry, sorted));
            }
            else
            {
                long entry = NewEntry();
                _docs[doc] = new Row(entry, sorted);
                changes.Add(new RowChange(0, [], entry, sorted));
            }
        }

        Publish(Build(Current.TransactionId + 1));
        return changes;
    }

    /// <summary>What the refresh has to do to get from the state of one snapshot to the other (a refresh that retries after a rollback sees the changes of several writes at once).</summary>
    public static List<RowChange> Diff(Snapshot from, Snapshot to)
    {
        var changes = new List<RowChange>();
        foreach ((long entry, Row row) in from.Rows)
        {
            if (to.Rows.TryGetValue(entry, out Row other) == false)
                changes.Add(new RowChange(entry, row.Keys, 0, []));
            else if (row.Keys.SequenceEqual(other.Keys) == false)
                changes.Add(new RowChange(entry, row.Keys, entry, other.Keys));
        }

        foreach ((long entry, Row row) in to.Rows)
        {
            if (from.Rows.ContainsKey(entry) == false)
                changes.Add(new RowChange(0, [], entry, row.Keys));
        }

        return changes;
    }

    public static ResidentPatch PatchOf(IEnumerable<RowChange> changes)
    {
        var patch = new ResidentPatch();
        foreach (RowChange change in changes)
        {
            if (change.OldEntry == 0)
                patch.AddRow(change.NewEntry, change.NewKeys);
            else if (change.NewEntry == 0)
                patch.RemoveRow(change.OldEntry, change.OldKeys);
            else
                patch.ChangeRow(change.OldEntry, change.OldKeys, change.NewEntry, change.NewKeys);
        }

        return patch;
    }
}
