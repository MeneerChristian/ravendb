using System;
using System.Collections.Generic;
using System.Threading;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Raven.Server.Documents.Queries;
using Sparrow.Server.Logging;

namespace Raven.Server.Documents.Indexes.Joins.Serving;

/// <summary>
/// What the join indexes hold for one attempt of a query: a read snapshot of every join index of the indexes of the query. It is opened before the query opens the transaction of any index,
/// so the lists never reflect more than the indexes the query reads. It is closed after those transactions.
/// </summary>
internal sealed class JoinQueryLease : IDisposable
{
    private readonly IndexQueryServerSide _query;
    private readonly CancellationToken _token;
    private int _disposed;

    public JoinQueryLease(IndexQueryServerSide query, CancellationToken token)
    {
        _query = query;
        _token = token;
    }

    public List<Entry> Entries { get; } = new(1);

    private static bool IsJoinOf(Entry entry, string outerIndex, string innerIndex, string outerField)
    {
        return string.Equals(entry.Index.OuterName, outerIndex, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(entry.Index.InnerName, innerIndex, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(entry.Index.Join.OuterField, outerField, StringComparison.Ordinal);
    }

    /// <summary>
    /// Answers the join from a join index that is defined for it: the same indexes, the same outer field and the same inner field (none for a join on the id), so a join index on the id never answers a join on a field
    /// and the other way round. Every such join index is tried in the order of the entries and the first that can answer does. False (the join is read as usual) when there is none or none can answer;
    /// the reason of the join index that came closest is counted.
    /// </summary>
    public bool TryServe(JoinServeRequest request, RavenLogger logger, out JoinServed served)
    {
        served = null;
        Entry closest = null;
        string closestReason = null;
        int closestRank = int.MaxValue;
        int closestDeclared = 0;
        Entry otherKind = null;

        for (int i = 0; i < Entries.Count; i++)
        {
            Entry entry = Entries[i];
            if (IsJoinOf(entry, request.OuterIndexName, request.InnerIndexName, request.OuterField) == false)
                continue;

            JoinIndex index = entry.Index;
            if (string.Equals(index.Join.InnerField, request.Join.InnerField, StringComparison.Ordinal) == false)
            {
                otherKind ??= entry;
                continue;
            }

            if (index.QueryHooks?.ForceFallback == true)
                continue;

            if (index.Type == IndexType.AutoJoin)
                index.MarkUsedByQuery();

            if (TryServe(entry, request, logger, out served, out string fallback))
                return true;

            int rank = RankOf(fallback);
            int declared = index.Join.Fields.Count + index.Join.Groupings.Count;
            if (rank < closestRank || (rank == closestRank && declared > closestDeclared))
            {
                closest = entry;
                closestReason = fallback;
                closestRank = rank;
                closestDeclared = declared;
            }
        }

        if (closest != null)
        {
            closest.Index.QueryCounters.Fallback(closestReason);
            request.Timings?.AddCounter("JoinFallback:" + closestReason, 1);
            return false;
        }

        // the lists of a join on a field of the inner index are not in a join index that holds another join of these indexes
        if (otherKind != null && request.Join.InnerField != null && otherKind.Index.QueryHooks?.ForceFallback != true)
        {
            string notServed = JoinFallbacks.KindNotServed("FieldJoin");
            otherKind.Index.QueryCounters.Fallback(notServed);
            request.Timings?.AddCounter("JoinFallback:" + notServed, 1);
        }

        return false;
    }

    // How far a join index that did not answer is from answering: its state (0), unknown because it holds nothing to read (1), another kind of join (2), fields or groupings it does not declare (3)
    private static int RankOf(string fallback)
    {
        if (fallback == JoinFallbacks.BehindTimeout || fallback == JoinFallbacks.Ahead || fallback == JoinFallbacks.EnvironmentChanged)
            return 0;

        if (fallback.StartsWith(JoinFallbacks.KindNotServedPrefix, StringComparison.Ordinal) == false)
            return 1;

        return fallback.StartsWith(JoinFallbacks.UndeclaredFieldPrefix, StringComparison.Ordinal) ? 3 : 2;
    }

    private bool TryServe(Entry entry, JoinServeRequest request, RavenLogger logger, out JoinServed served, out string fallback)
    {
        JoinIndex index = entry.Index;
        request.ProbeCache = index.ScoreProbes;
        fallback = null;
        try
        {
            var target = new JoinServing.Target
            {
                Name = index.Name,
                Definition = index.Join,
                Snapshot = entry.Snapshot,
                Generation = entry.Snapshot?.Generation ?? JoinIndexGeneration.Initial,
                Unavailable = entry.Unavailable,
                MaxResidualRows = index.Options.MaxResidualRows
            };

            served = JoinServing.Serve(request, target, _ =>
            {
                index.QueryHooks?.BeforeCompare?.Invoke(entry);
                target.Snapshot = entry.Snapshot;
                target.Generation = entry.Snapshot.Generation;
            }, out fallback);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            if (logger?.IsWarnEnabled == true)
                logger.Warn($"The join index '{index.Name}' failed to answer a join of a query. The join is read as usual.", e);

            fallback = JoinFallbacks.Failed;
            served = null;
        }

        if (served != null)
        {
            index.QueryCounters.Served();
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            foreach (Entry entry in Entries)
                entry.Dispose();
        }
        finally
        {
            if (ReferenceEquals(_query.JoinLease, this))
                _query.JoinLease = null;
        }
    }

    public sealed class Entry : IDisposable
    {
        private JoinIndexSnapshot _snapshot;

        public Entry(JoinIndex index, JoinIndexSnapshot snapshot, string unavailable)
        {
            Index = index;
            _snapshot = snapshot;
            Unavailable = unavailable;
        }

        public JoinIndex Index { get; }

        /// <summary>The snapshot the query reads the lists from.</summary>
        public JoinIndexSnapshot Snapshot => _snapshot;

        /// <summary>Why the join index cannot answer (a <see cref="JoinFallbacks"/> name), null when it holds a snapshot.</summary>
        public string Unavailable { get; }

        /// <summary>Takes the place of the snapshot; the one it replaces is closed.</summary>
        public void Replace(JoinIndexSnapshot snapshot)
        {
            JoinIndexSnapshot old = Interlocked.Exchange(ref _snapshot, snapshot);
            old?.Dispose();
        }

        public void Dispose() => Replace(null);
    }
}

/// <summary>What a test does to the way a join index answers queries.</summary>
internal sealed class JoinIndexQueryHooks
{
    /// <summary>Every join is read as usual, whatever the join index holds.</summary>
    public bool ForceFallback;

    /// <summary>Called when the join index is about to compare its snapshot with what the query reads; may replace the snapshot of the entry.</summary>
    public Action<JoinQueryLease.Entry> BeforeCompare;
}
