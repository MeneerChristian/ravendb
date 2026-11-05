using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config.Categories;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Raven.Server.ServerWide.Context;
using Sparrow.Server;
using Sparrow.Threading;
using Voron.Data.CompactTrees;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Refresh;

/// <summary>What a refresh did: the number of documents it looked at and whether the file now holds so many terms without a list that it should be built again.</summary>
public readonly record struct JoinRefreshOutcome(long Ids, bool EmptyTerms, JoinIndexGeneration Generation);

/// <summary>
/// Brings the lists of a join index in line with the changes of the two indexes since its generation. It runs in one write transaction of the home of the join index
/// (<see cref="IJoinFile"/>), whether that is a file of its own or the environment of a join index, and writes the generation in the same transaction. One engine, one thread.
/// </summary>
internal sealed partial class JoinIncrementalRefresh : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Change
    {
        public long Old;
        public long New;

        /// <summary>The entry did not change and joins the entry it joined before: nothing to do.</summary>
        public int Skip;
    }

    /// <summary>An entry that names several documents and a list that one of them changed: whether the entry is in the list is decided once all the changes of the batch are in.</summary>
    private struct Pending : IComparable<Pending>
    {
        public long Row;
        public long Atom;

        public int CompareTo(Pending other)
        {
            int result = Row.CompareTo(other.Row);
            return result != 0 ? result : Atom.CompareTo(other.Atom);
        }
    }

    private readonly JoinPlan _plan;
    private readonly JoinOptions _options;
    private readonly DocumentsStorage _documents;
    private readonly JoinCounters _counters;
    private readonly ByteStringContext _workAllocator;
    private readonly DirtyIds _dirtyOuter;
    private readonly DirtyIds _dirtyInner;
    private readonly InnerProjector _projector;
    private readonly OuterKeyReader _keyReader;
    private ContextBoundNativeList<Change> _outerChanges;
    private ContextBoundNativeList<Change> _innerChanges;
    private ContextBoundNativeList<long> _affected;
    private ContextBoundNativeList<long> _dirtyRows;
    private ContextBoundNativeList<long> _multiRows;
    private ContextBoundNativeList<long> _rowKeys;
    private ContextBoundNativeList<long> _patchOldKeys;
    private ContextBoundNativeList<int> _pairsBefore;
    private bool _pairsBeforeKnown;
    private long _pairCountLookups;
    private ContextBoundNativeList<KeySpan> _patchOld;
    private ContextBoundNativeList<int> _newMasks;
    private ContextBoundNativeList<Pending> _pending;
    private bool _multiRowsLoaded;
    private bool _readMembershipFromFile;
    private long _innerRemaining;
    private int _queueLimit;

    /// <summary>The bytes of memory that are available to the server now. Null: the queue of list changes has the size that the options say.</summary>
    public Func<long> AvailableMemoryBytes { get; set; }
    private readonly ContextBoundNativeList<long>[] _oldRefs;
    private readonly ContextBoundNativeList<long>[] _newRefs;
    private readonly ContextBoundNativeList<long>[] _scratchRefs;
    private readonly ContextBoundNativeList<long>[] _oldMembers;
    private readonly bool[] _oldMembersLoaded;
    private IJoinFile _file;

    /// <summary>
    /// Where an Indirect refresh records what it does to the lists of the keys of its outer rows (the old keys come from the file, the new ones from the outer index). The caller applies it to the resident cache
    /// after the transaction committed; the engine only fills it. Null when there is no cache.
    /// </summary>
    public ResidentPatch Patch { get; set; }

    /// <summary>Where the old keys of a document that changed are kept between its removal and its addition.</summary>
    private readonly record struct KeySpan(int Start, int Length);

    /// <summary>Called at named points of a refresh; an exception that it throws ends the refresh like one of the engine.</summary>
    public Action<string> OnPhase { get; set; }

    /// <summary>True from the moment a refresh started to change the lists until the next one starts: an exception after that leaves lists that are not a state of the indexes.</summary>
    public bool HasWritten { get; private set; }

    public JoinIncrementalRefresh(JoinPlan plan, JoinOptions options, DocumentsStorage documents, JoinCounters counters)
    {
        _plan = plan;
        _options = options;
        _documents = documents;
        _counters = counters;
        _workAllocator = new ByteStringContext(SharedMultipleUseFlag.None);
        _dirtyOuter = new DirtyIds(_workAllocator);
        _dirtyInner = new DirtyIds(_workAllocator);
        _projector = new InnerProjector(plan, _workAllocator);
        _keyReader = new OuterKeyReader(plan, _workAllocator);
        _outerChanges = new ContextBoundNativeList<Change>(_workAllocator, 256);
        _innerChanges = new ContextBoundNativeList<Change>(_workAllocator, 64);
        _affected = new ContextBoundNativeList<long>(_workAllocator, 256);
        _dirtyRows = new ContextBoundNativeList<long>(_workAllocator, 256);
        _multiRows = new ContextBoundNativeList<long>(_workAllocator, 64);
        _rowKeys = new ContextBoundNativeList<long>(_workAllocator, 32);
        _patchOldKeys = new ContextBoundNativeList<long>(_workAllocator, 32);
        _pairsBefore = new ContextBoundNativeList<int>(_workAllocator, 64);
        _classifiedKeyRefs = new ContextBoundNativeList<long>(_workAllocator, 32);
        _classifiedKeys = new ContextBoundNativeList<KeySpan>(_workAllocator, 32);
        _classifiedFingerprints = new ContextBoundNativeList<long>(_workAllocator, 32);
        _patchOld = new ContextBoundNativeList<KeySpan>(_workAllocator, 32);
        _newMasks = new ContextBoundNativeList<int>(_workAllocator, 64);
        _pending = new ContextBoundNativeList<Pending>(_workAllocator, 64);
        _deltas = new ContextBoundNativeList<PairDelta>(_workAllocator, 64);
        _oldCodes = new ContextBoundNativeList<long>(_workAllocator, 32);
        _newCodes = new ContextBoundNativeList<long>(_workAllocator, 32);
        _oldKeyRefs = new ContextBoundNativeList<long>(_workAllocator, 8);
        _newKeyRefs = new ContextBoundNativeList<long>(_workAllocator, 8);
        _recheck = new ContextBoundNativeList<SeveralKeysCheck>(_workAllocator, 16);
        _cachedCodes = new ContextBoundNativeList<long>(_workAllocator, 32);
        _keyAtomAdds = new ContextBoundNativeList<long>(_workAllocator, 8);
        _termHashes = new ContextBoundNativeList<long>(_workAllocator, 8);
        _keyAtomRemovals = new ContextBoundNativeList<long>(_workAllocator, 8);
        _cleanRows = new ContextBoundNativeList<long>(_workAllocator, 64);
        _moves = new ContextBoundNativeList<Move>(_workAllocator, 64);
        _moveAdds = new ContextBoundNativeList<long>(_workAllocator, 64);
        _moveRemovals = new ContextBoundNativeList<long>(_workAllocator, 64);
        _moveEncoded = new ContextBoundNativeList<long>(_workAllocator, 64);
        _oldRefs = NewLists(plan.Fields.Length);
        _newRefs = NewLists(plan.Fields.Length);
        _scratchRefs = NewLists(plan.Fields.Length);
        _oldMembers = NewLists(plan.GroupingFields.Length);
        _oldMembersLoaded = new bool[plan.GroupingFields.Length];
    }

    private ContextBoundNativeList<long>[] NewLists(int count)
    {
        var lists = new ContextBoundNativeList<long>[count];
        for (int i = 0; i < count; i++)
            lists[i] = new ContextBoundNativeList<long>(_workAllocator, 32);

        return lists;
    }

    private static long Lap(ref long mark)
    {
        long now = Stopwatch.GetTimestamp();
        long ticks = now - mark;
        mark = now;
        return ticks;
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        _dirtyOuter.Dispose();
        _dirtyInner.Dispose();
        _projector.Dispose();
        _keyReader.Dispose();
        _outerChanges.Dispose();
        _innerChanges.Dispose();
        _affected.Dispose();
        _dirtyRows.Dispose();
        _multiRows.Dispose();
        _rowKeys.Dispose();
        _patchOldKeys.Dispose();
        _pairsBefore.Dispose();
        _classifiedKeyRefs.Dispose();
        _classifiedKeys.Dispose();
        _classifiedFingerprints.Dispose();
        _patchOld.Dispose();
        _newMasks.Dispose();
        _pending.Dispose();
        _deltas.Dispose();
        _oldCodes.Dispose();
        _newCodes.Dispose();
        _oldKeyRefs.Dispose();
        _newKeyRefs.Dispose();
        _recheck.Dispose();
        _cachedCodes.Dispose();
        _keyAtomAdds.Dispose();
        _termHashes.Dispose();
        _keyAtomRemovals.Dispose();
        _cleanRows.Dispose();
        _moves.Dispose();
        _moveAdds.Dispose();
        _moveRemovals.Dispose();
        _moveEncoded.Dispose();
        foreach (var lists in new[] { _oldRefs, _newRefs, _scratchRefs, _oldMembers })
        {
            foreach (ContextBoundNativeList<long> list in lists)
                list.Dispose();
        }

        _workAllocator.Dispose();
    }

    /// <summary>
    /// Applies the changes since <paramref name="published"/> to the lists of <paramref name="file"/> and writes the generation of the two snapshots in the same write transaction.
    /// The transaction is committed by whoever owns it (the writer of a file that owns its transaction does it here).
    /// </summary>
    public JoinRefreshOutcome Run(IJoinFile file, IndexView outer, IndexView inner, JoinIndexGeneration published, long startedAt, CancellationToken token)
    {
        _file = file;
        HasWritten = false;
        JoinPlan plan = _plan;

        _dirtyOuter.Clear();
        _dirtyInner.Clear();

        long mark = Stopwatch.GetTimestamp();
        long openTicks = mark - startedAt;
        long cpuStart = ThreadCpuClock.Now();
        long commitCpu = 0;
        long discoverTicks = 0, classifyTicks = 0, removeTicks = 0, innerTicks = 0, addTicks = 0, flushTicks = 0, commitTicks = 0;
        string listStats = null;

        // the documents are read after the snapshots: every document that a snapshot reflects is visible in this transaction
        JoinCursors outerReached;
        JoinCursors innerReached = published.Inner;
        _innerRemaining = 0;
        bool outerCut;
        bool innerCut = false;
        using (_documents.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
        using (context.OpenReadTransaction())
        {
            int budget = _options.MaxIdsPerCommit;
            int unindexedBudget = _options.MaxUnindexedIdsPerCommit;
            if (file.Schema.IsIndirect)
            {
                // the inner side decides what is served and reads no outer entry: it goes first, the outer side gets what is left of the budget
                if (Discover(context, file, inner.Snapshot, published.Inner, _dirtyInner, innerSide: true, ref budget, ref unindexedBudget, out innerReached, out innerCut) == false)
                    throw new JoinRebuildRequiredException(JoinRebuildReason.TooManyUnindexedChanges);

                // a cut on the inner side used the budget up: the outer side waits for the next commit
                outerReached = published.Outer;
                outerCut = false;
                if (innerCut == false &&
                    Discover(context, file, outer.Snapshot, published.Outer, _dirtyOuter, innerSide: false, ref budget, ref unindexedBudget, out outerReached, out outerCut) == false)
                    throw new JoinRebuildRequiredException(JoinRebuildReason.TooManyUnindexedChanges);
            }
            else
            {
                if (Discover(context, file, outer.Snapshot, published.Outer, _dirtyOuter, innerSide: false, ref budget, ref unindexedBudget, out outerReached, out outerCut) == false)
                    throw new JoinRebuildRequiredException(JoinRebuildReason.TooManyUnindexedChanges);

                // the changes of the inner side find the outer entries through the outer snapshot, which is the file only when the outer side is complete
                if (outerCut == false &&
                    Discover(context, file, inner.Snapshot, published.Inner, _dirtyInner, innerSide: true, ref budget, ref unindexedBudget, out innerReached, out innerCut) == false)
                    throw new JoinRebuildRequiredException(JoinRebuildReason.TooManyUnindexedChanges);
            }
        }

        bool partial = outerCut || innerCut;

        // a document that is deferred has the entry of the snapshot, not the state of the file
        _readMembershipFromFile = partial || published.IsPartial;

        discoverTicks = Lap(ref mark);
        if (_dirtyOuter.SortAndDedupe() == false || _dirtyInner.SortAndDedupe() == false)
            throw new JoinRebuildRequiredException(JoinRebuildReason.HashCollision);

        token.ThrowIfCancellationRequested();
        OnPhase?.Invoke("Discovered");

        long ids = _dirtyOuter.Count + _dirtyInner.Count;
        bool emptyTerms = false;

        JoinIndexGeneration committed;
        using (JoinWriter writer = file.BeginWrite())
        using (var outerLists = new ListStore(outer.Snapshot.Transaction.LowLevelTransaction, default))
        {
            _queueLimit = QueueLimit(_options.MaxQueuedListChanges);
            writer.FlushThreshold = _queueLimit;
            _counters.LastQueueLimit = _queueLimit;
            _projector.Bind(inner);
            _keyReader.Bind(outer);
            Array.Clear(_oldMembersLoaded);
            _multiRowsLoaded = false;
            _pending.Count = 0;
            _patchOldKeys.Count = 0;
            _patchOld.Count = 0;
            if (Patch != null)
            {
                for (int k = 0; k < _dirtyOuter.Count; k++)
                    _patchOld.Add(default);
            }

            ClassifyOuter(writer, outer, inner);
            ClassifyInner(writer, inner);
            classifyTicks = Lap(ref mark);

            HasWritten = true;

            // removals first, then the changes of the inner side, then the additions: a changed document is first taken out of the lists that its old terms put it in
            for (int k = 0; k < _outerChanges.Count; k++)
            {
                Change change = _outerChanges[k];
                if (change.Old != 0 && change.Skip == 0)
                {
                    if (plan.IsFieldJoin)
                        RemoveOuterFieldRow(writer, change.Old, _dirtyOuter.HashAt(k), change.New != 0, k);
                    else
                        RemoveOuterPair(writer, change.Old, _dirtyOuter.HashAt(k));
                }
            }

            removeTicks = Lap(ref mark);
            _cachedKeyRef = 0;
            CompactTree keyTree = outer.TermsOf(plan.OuterKeyField);
            for (int k = 0; k < _innerChanges.Count; k++)
            {
                Change change = _innerChanges[k];
                if (change.Old == 0 && change.New == 0)
                    continue;

                if (plan.IsFieldJoin)
                    ApplyInnerFieldChange(writer, inner, _dirtyInner.HashAt(k), change, k);
                else
                    ApplyInnerChange(writer, outerLists, inner, keyTree, _dirtyInner.IdAt(k), _dirtyInner.HashAt(k), change, k);
            }

            if (plan.IsFieldJoin)
                ApplyPairDeltas(writer, outerLists, keyTree);

            ResolvePending(writer, inner);

            innerTicks = Lap(ref mark);
            _cachedKeyRef = 0;
            token.ThrowIfCancellationRequested();

            for (int k = 0; k < _outerChanges.Count; k++)
            {
                Change change = _outerChanges[k];
                if (change.New == 0 || change.Skip != 0)
                    continue;

                if (plan.IsFieldJoin)
                    AddOuterFieldRow(writer, outer, _dirtyOuter.HashAt(k), change.New, change.Old, k);
                else
                    AddOuter(writer, outer, inner, _dirtyOuter.HashAt(k), change.New);
            }

            addTicks = Lap(ref mark);
            long pagesBeforeFlush = writer.ModifiedPages;
            writer.Flush();
            flushTicks = Lap(ref mark);
            for (int f = 0; f < plan.Fields.Length; f++)
            {
                long terms = writer.NumberOfTerms(f);
                if (terms >= _options.MinTermsForEmptyRatio && terms - writer.NumberOfTermsWithList(f) > _options.MaxEmptyTermRatio * terms)
                    emptyTerms = true;
            }

            JoinIndexGeneration generation = file.Schema.IsIndirect
                ? new(0, outer.Snapshot.EnvironmentId, outerReached, inner.Snapshot.EnvironmentId, innerReached, inner.Searcher.LastEntryId, 0, JoinStorageMode.Indirect, innerCut, outerCut)
                : new(0, outer.Snapshot.EnvironmentId, outerReached, inner.Snapshot.EnvironmentId, innerReached, inner.Searcher.LastEntryId, 0, partial);

            OnPhase?.Invoke("BeforeCommit");
            long commitCpuStart = ThreadCpuClock.Now();
            committed = writer.Commit(generation);
            commitCpu = ThreadCpuClock.Now() - commitCpuStart;
            commitTicks = Lap(ref mark);
            _counters.Add(ref _counters.ListUpdates, writer.ListUpdates);
            _counters.Add(ref _counters.ListFlushes, writer.Flushes);
            _counters.Add(ref _counters.Flushes, writer.Flushes);
            _counters.Add(ref _counters.QueuedOps, writer.OpsQueued);
            _counters.Add(ref _counters.ThresholdFlushes, writer.ThresholdFlushes);
            _counters.Add(ref _counters.PairCountLookups, _pairCountLookups);
            _pairCountLookups = 0;
            listStats = $"{writer.ListChangesQueued} list changes queued, {writer.ListUpdates} lists, {pagesBeforeFlush} pages before the flush, store {Ms(writer.ListStoreTicks):0.0} ms";
        }

        long closeTicks = Lap(ref mark);
        _counters.LastBreakdown = $"open {Ms(openTicks):0.0}, discover {Ms(discoverTicks):0.0}, classify {Ms(classifyTicks):0.0}, remove {Ms(removeTicks):0.0}, inner {Ms(innerTicks):0.0}, add {Ms(addTicks):0.0}, flush {Ms(flushTicks):0.0}, commit {Ms(commitTicks):0.0}, close {Ms(closeTicks):0.0} ms, thread CPU {(ThreadCpuClock.Now() - cpuStart) / (double)TimeSpan.TicksPerMillisecond:0.0} ms of which the commit {commitCpu / (double)TimeSpan.TicksPerMillisecond:0.0} ({_dirtyOuter.Count} outer, {_dirtyInner.Count} inner ids; {listStats})";

        _counters.Add(ref _counters.Commits, 1);
        _counters.Add(ref _counters.IdsProcessed, ids);
        return new JoinRefreshOutcome(ids, emptyTerms, committed);
    }

    /// <summary>Compares a snapshot of an index with what the file reflects of it. True when the index went on.</summary>
    public static bool Classify(IndexView view, Guid environmentId, JoinCursors cursors, ref JoinRebuildReason? reset)
    {
        if (view.Snapshot.EnvironmentId != environmentId)
        {
            reset ??= JoinRebuildReason.EnvironmentChanged;
            return false;
        }

        if (cursors.HasSameCollections(view.Snapshot.Consumed) == false)
        {
            reset ??= JoinRebuildReason.CollectionsChanged;
            return false;
        }

        switch (cursors.CompareTo(view.Snapshot.Consumed))
        {
            case GenerationComparison.Equal:
                return false;
            case GenerationComparison.Behind:
                return true;
            default:
                reset ??= JoinRebuildReason.CursorsWentBackwards;
                return false;
        }
    }

    /// <summary>
    /// Collects the ids of the documents that were written or deleted since the cursors of the file. Both are read up to the end, not up to what the index consumed:
    /// a document that was written again after the snapshot moved above it, and a document that was deleted after the snapshot has a tombstone above what the index processed,
    /// and in both cases the snapshot may still hold the entry of the older version. Only what the snapshot consumed counts against <paramref name="budget"/>: what is above it is
    /// not a change of the snapshot yet and is read again by the next refresh, so it has the budget of its own. When the changes of the snapshot are more than the budget allows the
    /// discovery stops after the last document in etag order that fits (<paramref name="cut"/>): <paramref name="reached"/> are the cursors up to there, and the collections after it keep theirs.
    /// False when the documents above the snapshot are more than their budget allows.
    /// </summary>
    private bool Discover(DocumentsOperationContext context, IJoinFile file, IndexSnapshot snapshot, JoinCursors cursors, DirtyIds into, bool innerSide, ref int budget, ref int unindexedBudget,
        out JoinCursors reached, out bool cut)
    {
        DocumentsStorage documents = _documents;
        var progress = new List<(string Collection, long Document, long Tombstone)>(snapshot.Consumed.Count);
        cut = false;
        reached = null;

        foreach ((string collection, (long consumedDocument, long consumedTombstone)) in snapshot.Consumed)
        {
            (long cursorDocument, long cursorTombstone) = cursors.Of(collection);
            if (cut)
            {
                progress.Add((collection, cursorDocument, cursorTombstone));
                continue;
            }

            long reachedDocument = cursorDocument;
            long reachedTombstone = cursorTombstone;
            int taken = 0, unindexed = 0;
            foreach (Document document in documents.GetDocumentsFrom(context, collection, cursorDocument + 1, 0, budget + 1L + unindexedBudget, DocumentFields.LowerId))
            {
                using (document)
                {
                    if (document.Etag <= consumedDocument)
                    {
                        if (taken >= budget)
                        {
                            cut = true;
                            if (innerSide)
                                _innerRemaining += CountRemaining(context, collection, tombstones: false, reachedDocument, consumedDocument);

                            break;
                        }

                        taken++;
                        reachedDocument = document.Etag;
                    }
                    else if (++unindexed > unindexedBudget)
                    {
                        return false;
                    }

                    ReadOnlySpan<byte> id = document.LowerId.AsReadOnlySpan();
                    into.Add(id, innerSide ? file.InnerKeyOf(id) : file.HashDocumentId(id));
                }
            }

            budget -= taken;
            unindexedBudget -= unindexed;
            if (cut)
            {
                progress.Add((collection, reachedDocument, reachedTombstone));
                continue;
            }

            reachedDocument = consumedDocument;

            taken = 0;
            unindexed = 0;
            long take = budget + 1L + unindexedBudget;
            long read = 0;
            long lastRead = 0;
            foreach (Tombstone tombstone in documents.GetTombstonesFrom(context, collection, cursorTombstone + 1, 0, take))
            {
                using (tombstone)
                {
                    read++;
                    lastRead = tombstone.Etag;
                    if (tombstone.Type != Tombstone.TombstoneType.Document)
                        continue;

                    if (tombstone.Etag <= consumedTombstone)
                    {
                        if (taken >= budget)
                        {
                            cut = true;
                            if (innerSide)
                                _innerRemaining += CountRemaining(context, collection, tombstones: true, reachedTombstone, consumedTombstone);

                            break;
                        }

                        taken++;
                        reachedTombstone = tombstone.Etag;
                    }
                    else if (++unindexed > unindexedBudget)
                    {
                        return false;
                    }

                    ReadOnlySpan<byte> id = tombstone.LowerId.AsReadOnlySpan();
                    into.Add(id, innerSide ? file.InnerKeyOf(id) : file.HashDocumentId(id));
                }
            }

            budget -= taken;
            unindexedBudget -= unindexed;
            if (cut == false)
            {
                // tombstones of other kinds count against the number that is read: when they filled it the tombstones after them are not known
                if (read >= take)
                {
                    if (lastRead > consumedTombstone)
                        return false;

                    cut = true;
                    reachedTombstone = lastRead;
                }
                else
                {
                    reachedTombstone = consumedTombstone;
                }
            }

            progress.Add((collection, reachedDocument, reachedTombstone));
        }

        reached = new JoinCursors(progress);
        return true;
    }

    /// <summary>
    /// The number of list changes that are queued before they are applied: what the options say, but not more than what a quarter of the available memory holds at the cost of a queued change (a
    /// change is 24 bytes in the queue and about 100 more in the pages of the transaction that the lists it changes touch), and not less than <see cref="ShortestQueue"/>.
    /// Join.MinQueuedListChanges (a fraction of the memory of the machine) is the queue only when the available memory is not known.
    /// </summary>
    internal int QueueLimit(int limit)
    {
        if (AvailableMemoryBytes == null)
            return Math.Min(limit, _options.MinQueuedListChanges);

        long available = AvailableMemoryBytes();
        return (int)Math.Clamp(available / 4 / BytesPerQueuedChange, Math.Min(limit, ShortestQueue(available)), limit);
    }

    /// <summary>
    /// The shortest queue for an amount of available memory: <see cref="AbsoluteMinQueuedChanges"/> (a shorter one flushes more often) while
    /// the available memory is at least twice what it holds, and half of the available memory in changes under that, but not less than <see cref="HardMinQueuedChanges"/>.
    /// </summary>
    internal static int ShortestQueue(long availableBytes)
    {
        long halfOfAvailable = availableBytes / 2 / BytesPerQueuedChange;
        return (int)Math.Max(HardMinQueuedChanges, Math.Min(AbsoluteMinQueuedChanges, halfOfAvailable));
    }

    internal const int AbsoluteMinQueuedChanges = 262_144;

    internal const int HardMinQueuedChanges = 1024;

    private const int BytesPerQueuedChange = JoinConfiguration.BytesPerQueuedListChange;

    /// <summary>The number of documents (or tombstones of documents) above an etag that the index consumed: what is left of a change that was cut at the budget.</summary>
    private long CountRemaining(DocumentsOperationContext context, string collection, bool tombstones, long after, long consumed)
    {
        const long Limit = 50_000_000;
        long count = 0;
        if (tombstones)
        {
            foreach (Tombstone tombstone in _documents.GetTombstonesFrom(context, collection, after + 1, 0, Limit))
            {
                using (tombstone)
                {
                    if (tombstone.Etag > consumed)
                        break;

                    if (tombstone.Type == Tombstone.TombstoneType.Document)
                        count++;
                }
            }

            return count;
        }

        foreach (Document document in _documents.GetDocumentsFrom(context, collection, after + 1, 0, Limit, DocumentFields.LowerId))
        {
            using (document)
            {
                if (document.Etag > consumed)
                    break;

                count++;
            }
        }

        return count;
    }

    private void ClassifyOuter(JoinWriter writer, IndexView outer, IndexView inner)
    {
        _outerChanges.Count = 0;
        _dirtyRows.Count = 0;
        _classifiedKeys.Count = 0;
        _classifiedFingerprints.Count = 0;
        _classifiedKeyRefs.Count = 0;
        for (int k = 0; k < _dirtyOuter.Count; k++)
        {
            ReadOnlySpan<byte> id = _dirtyOuter.IdAt(k);
            long old = writer.TryGetOuterDocument(_dirtyOuter.HashAt(k), out long stored) ? stored : 0;

            long current = 0;
            if (outer.TryFindEntry(id, out long entry, out bool multiple))
                current = entry;
            else if (multiple)
                throw new JoinUnsupportedException("A document of the outer index has several entries: a join index holds one entry per document.");

            if (old != 0 && old != current && outer.EntryBelongsToAnotherDocument(old, id))
                throw new JoinRebuildRequiredException(JoinRebuildReason.HashCollision);

            // an index keeps the entry id when it updates the entry of a document: the same entry can have other keys
            _classifiedKeys.Add(new KeySpan(-1, 0));
            _classifiedFingerprints.Add(0);
            bool skip = old != 0 && old == current && (_plan.IsFieldJoin ? HasTheSameFieldKey(writer, outer, current, k) : HasTheSameKeys(writer, outer, current));
            _outerChanges.Add(new Change { Old = old, New = current, Skip = skip ? 1 : 0 });

            // the entries that are taken out and put in again are not touched by the changes of the inner documents in between
            if (current != 0 && skip == false)
                _dirtyRows.Add(current);
        }

        _dirtyRows.ToSpan().Sort();
    }

    /// <summary>Whether the outer entry names now the keys that the file has for it (none for none).</summary>
    private bool HasTheSameKeys(JoinWriter writer, IndexView outer, long entry)
    {
        bool has = _keyReader.Read(outer, entry, _file);
        _rowKeys.Count = 0;
        bool stored = writer.TryGetOuterKeys(entry, ref _rowKeys);
        return has ? stored && _rowKeys.ToSpan().SequenceEqual(_keyReader.Keys) : stored == false;
    }

    private void ClassifyInner(JoinWriter writer, IndexView inner)
    {
        _innerChanges.Count = 0;
        for (int k = 0; k < _dirtyInner.Count; k++)
        {
            ReadOnlySpan<byte> id = _dirtyInner.IdAt(k);
            long old = writer.TryGetInnerDocument(_dirtyInner.HashAt(k), out long stored) ? stored : 0;

            long current = 0;
            if (inner.TryFindEntry(id, out long entry, out bool multiple))
                current = entry;
            else if (multiple)
                throw new JoinUnsupportedException("A document of the inner index has several entries: a join index holds one entry per document.");

            if (old != 0 && old != current && inner.EntryBelongsToAnotherDocument(old, id))
                throw new JoinRebuildRequiredException(JoinRebuildReason.HashCollision);

            _innerChanges.Add(new Change { Old = old, New = current });
        }

        // the groupings that each changed document is in after its change, once it has been applied
        _newMasks.Count = 0;
        for (int k = 0; k < _innerChanges.Count; k++)
            _newMasks.Add(0);
    }

    /// <summary>Whether the old inner entry was in the groupings, as of the file before this refresh.</summary>
    private int OldMembership(JoinWriter writer, long innerEntry)
    {
        int mask = 0;
        for (int g = 0; g < _oldMembers.Length; g++)
        {
            if (_oldMembersLoaded[g] == false)
            {
                _oldMembers[g].Count = 0;
                writer.ReadList(writer.SlotHandle(writer.GroupingMembersSlot(g)), ref _oldMembers[g]);
                _oldMembersLoaded[g] = true;
            }

            if (_oldMembers[g].ToSpan().BinarySearch(innerEntry) >= 0)
                mask |= 1 << g;
        }

        return mask;
    }

    /// <summary>Takes an outer entry out of everything the inner documents it names put it in.</summary>
    private void RemoveOuterPair(JoinWriter writer, long entry, long hash)
    {
        JoinPlan plan = _plan;

        _rowKeys.Count = 0;
        if (writer.TryGetOuterKeys(entry, ref _rowKeys))
        {
            int joined = 0;
            foreach (long inner in _rowKeys.ToSpan())
            {
                if (writer.TryGetInnerDocument(inner, out _) == false)
                    continue;

                joined++;
                for (int f = 0; f < plan.Fields.Length; f++)
                {
                    _scratchRefs[f].Count = 0;
                    writer.TryGetInnerTerms(f, inner, ref _scratchRefs[f]);
                    foreach (long termRef in _scratchRefs[f].ToSpan())
                        writer.RemoveFromTerm(f, termRef, entry);
                }

                writer.RemoveFromAny(entry);
                int mask = OldMembership(writer, inner);
                for (int g = 0; g < plan.GroupingFields.Length; g++)
                {
                    if ((mask & (1 << g)) != 0)
                        writer.RemoveFromGrouping(g, entry);
                }
            }

            if (joined >= 2)
                writer.AddRowsWithSeveralJoined(-1);

            writer.SetOuterKeys(entry, default);
        }

        writer.RemoveOuterDocument(hash);
    }

    /// <summary>
    /// Brings the file in line with the new state of an inner document. The inner side of the file is addressed by <paramref name="key"/>, which is made of the id of the document,
    /// so a document that got another entry in the index (deleted and created again) with the same terms changes nothing but its row in doc.i.
    /// </summary>
    private void ApplyInnerChange(JoinWriter writer, ListStore outerLists, IndexView inner, CompactTree keyTree, ReadOnlySpan<byte> id, long key, Change change, int index)
    {
        JoinPlan plan = _plan;
        long oldEntry = change.Old;
        long newEntry = change.New;
        bool hadOld = oldEntry != 0;
        bool hasNew = newEntry != 0;

        int oldMask = 0;
        for (int f = 0; f < plan.Fields.Length; f++)
        {
            _oldRefs[f].Count = 0;
            _newRefs[f].Count = 0;
            if (hadOld)
                writer.TryGetInnerTerms(f, key, ref _oldRefs[f]);
        }

        if (hadOld)
            oldMask = OldMembership(writer, key);

        int newMask = 0;
        if (hasNew)
        {
            _projector.Project(inner, newEntry);
            for (int t = 0; t < _projector.Count; t++)
            {
                int f = _projector.FieldOf(t);
                _newRefs[f].Add(writer.GetOrAddTerm(f, _projector.TermOf(t)));
            }

            newMask = _projector.Membership;
        }

        _newMasks[index] = newMask;

        for (int f = 0; f < plan.Fields.Length; f++)
        {
            _oldRefs[f].ToSpan().Sort();
            _newRefs[f].ToSpan().Sort();
        }

        if (hadOld == hasNew && oldMask == newMask)
        {
            bool same = true;
            for (int f = 0; f < plan.Fields.Length && same; f++)
                same = _oldRefs[f].ToSpan().SequenceEqual(_newRefs[f].ToSpan());

            if (same)
            {
                if (hasNew && oldEntry != newEntry)
                    SetInnerDocumentRow(writer, key, oldEntry, newEntry);

                return;
            }
        }

        // the outer entries that point to this document, as the outer snapshot has them
        _affected.Count = 0;
        if (keyTree != null && keyTree.TryGetValue(id, out long keyValue))
            outerLists.Read(keyValue, ref _affected);

        if (_affected.Count > 0)
        {
            LoadMultiRows(writer);
            long oldKey = hadOld ? key : 0;
            long newKey = hasNew ? key : 0;
            foreach (long entry in _affected.ToSpan())
            {
                if (_dirtyRows.Count > 0 && _dirtyRows.ToSpan().BinarySearch(entry) >= 0)
                    continue;

                // an entry that names one document is in the list of an atom when this document has it; one that names several is decided when all the changes are in
                bool several = _multiRows.Count > 0 && _multiRows.ToSpan().BinarySearch(entry) >= 0;
                if (several && hadOld != hasNew)
                    CountEntryWithSeveralJoined(writer, entry, hasNew ? 1 : -1);

                ChangePair(writer, entry, oldKey, newKey, oldMask, newMask, several);
            }
        }

        for (int f = 0; f < plan.Fields.Length; f++)
        {
            if (_oldRefs[f].Count > 0 || _newRefs[f].Count > 0)
                writer.SetInnerTerms(f, key, _newRefs[f].ToSpan());
        }

        for (int g = 0; g < plan.GroupingFields.Length; g++)
        {
            bool was = (oldMask & (1 << g)) != 0;
            bool now = (newMask & (1 << g)) != 0;
            if (was && now == false)
                writer.RemoveGroupingMember(g, key);
            else if (was == false && now)
                writer.AddGroupingMember(g, key);
        }

        if (hasNew)
            SetInnerDocumentRow(writer, key, oldEntry, newEntry);
        else
            writer.RemoveInnerDocument(key);
    }

    private static void SetInnerDocumentRow(JoinWriter writer, long key, long oldEntry, long newEntry)
    {
        long previous = writer.SetInnerDocument(key, newEntry);
        if (previous != 0 && previous != oldEntry)
            throw new JoinRebuildRequiredException(JoinRebuildReason.HashCollision);
    }

    /// <summary>The entries that hold two or more keys in the file as it was before this refresh: no change of an inner document makes an entry one of them, only a change of the entry itself.</summary>
    private void LoadMultiRows(JoinWriter writer)
    {
        if (_multiRowsLoaded)
            return;

        _multiRows.Count = 0;
        writer.ReadList(writer.SlotHandle(writer.MultiKeySlot), ref _multiRows);
        _multiRowsLoaded = true;
    }

    /// <summary>
    /// Moves an outer entry from the old joined inner document (its key, 0 for none) to the new one, touching only the lists in which the two differ.
    /// For an entry that names several documents the lists are only marked to be decided: another document may have the atom too.
    /// </summary>
    private void ChangePair(JoinWriter writer, long entry, long oldInner, long newInner, int oldMask, int newMask, bool several)
    {
        JoinPlan plan = _plan;

        for (int f = 0; f < plan.Fields.Length; f++)
        {
            ReadOnlySpan<long> before = _oldRefs[f].ToSpan();
            ReadOnlySpan<long> after = _newRefs[f].ToSpan();
            int b = 0;
            int a = 0;
            while (b < before.Length || a < after.Length)
            {
                if (a == after.Length || (b < before.Length && before[b] < after[a]))
                    Emit(writer, JoinReader.AtomOfTerm(f, before[b++]), entry, add: false, several);
                else if (b == before.Length || after[a] < before[b])
                    Emit(writer, JoinReader.AtomOfTerm(f, after[a++]), entry, add: true, several);
                else
                {
                    a++;
                    b++;
                }
            }
        }

        if (oldInner != 0 && newInner == 0)
            Emit(writer, JoinReader.AtomOfAny(), entry, add: false, several);
        else if (oldInner == 0 && newInner != 0)
            Emit(writer, JoinReader.AtomOfAny(), entry, add: true, several);

        int differ = oldMask ^ newMask;
        for (int g = 0; g < plan.GroupingFields.Length; g++)
        {
            if ((differ & (1 << g)) != 0)
                Emit(writer, JoinReader.AtomOfGrouping(g), entry, add: (newMask & (1 << g)) != 0, several);
        }
    }

    private void Emit(JoinWriter writer, long atom, long entry, bool add, bool several)
    {
        if (several)
            _pending.Add(new Pending { Row = entry, Atom = atom });
        else if (add)
            writer.AddToAtom(atom, entry);
        else
            writer.RemoveFromAtom(atom, entry);
    }

    /// <summary>An entry that names several documents became joined with one more or one less: counts it in or out of the entries that are joined with two or more.</summary>
    private void CountEntryWithSeveralJoined(JoinWriter writer, long entry, int delta)
    {
        _rowKeys.Count = 0;
        writer.TryGetOuterKeys(entry, ref _rowKeys);
        int joined = 0;
        foreach (long key in _rowKeys.ToSpan())
        {
            if (writer.TryGetInnerDocument(key, out _))
                joined++;
        }

        // the row of the changed document is not updated yet: it counts as it was
        int after = joined + delta;
        if (joined < 2 && after >= 2)
            writer.AddRowsWithSeveralJoined(1);
        else if (joined >= 2 && after < 2)
            writer.AddRowsWithSeveralJoined(-1);
    }

    /// <summary>
    /// Decides the lists of the entries that name several documents, now that every changed document has its new state: an entry is in the list of an atom when any of the documents it names has the atom.
    /// </summary>
    private void ResolvePending(JoinWriter writer, IndexView inner)
    {
        if (_pending.Count == 0)
            return;

        Span<Pending> pending = _pending.ToSpan();
        pending.Sort();

        int index = 0;
        while (index < pending.Length)
        {
            long row = pending[index].Row;
            _rowKeys.Count = 0;
            writer.TryGetOuterKeys(row, ref _rowKeys);

            while (index < pending.Length && pending[index].Row == row)
            {
                long atom = pending[index].Atom;
                index++;
                if (index < pending.Length && pending[index].Row == row && pending[index].Atom == atom)
                    continue;

                if (_plan.IsFieldJoin ? RowHasAtomOfKeys(writer, atom) : RowHasAtom(writer, inner, atom))
                    writer.AddToAtom(atom, row);
                else
                    writer.RemoveFromAtom(atom, row);
            }

        }

        _pending.Count = 0;
    }

    /// <summary>Whether one of the existing documents among <see cref="_rowKeys"/> has the atom, in the file as it is now.</summary>
    private bool RowHasAtom(JoinWriter writer, IndexView inner, long atom)
    {
        bool isTerm = JoinReader.IsTermAtom(atom);
        int field = isTerm ? JoinReader.FieldOfAtom(atom) : 0;
        long termRef = isTerm ? JoinReader.TermOfAtom(atom) : 0;
        int slot = isTerm ? 0 : JoinReader.SlotOfAtom(atom);

        foreach (long key in _rowKeys.ToSpan())
        {
            if (writer.TryGetInnerDocument(key, out _) == false)
                continue;

            if (isTerm)
            {
                _scratchRefs[field].Count = 0;
                writer.TryGetInnerTerms(field, key, ref _scratchRefs[field]);
                if (_scratchRefs[field].ToSpan().IndexOf(termRef) >= 0)
                    return true;
            }
            else if (slot == JoinReader.AnySlot || (MembershipNow(writer, inner, key) & (1 << (slot - JoinReader.GroupingListSlot(0)))) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The groupings that the inner document is in once the changes of this refresh are in: the new state of a document that changed, what its entry in the index says for any other (what the file says when documents are deferred).</summary>
    private int MembershipNow(JoinWriter writer, IndexView inner, long innerKey)
    {
        int changed = _dirtyInner.IndexOfHash(innerKey);
        if (changed >= 0)
            return _newMasks[changed];

        if (_plan.GroupingFields.Length == 0 || writer.TryGetInnerDocument(innerKey, out long innerEntry) == false)
            return 0;

        if (_readMembershipFromFile)
            return OldMembership(writer, innerKey);

        _projector.Project(inner, innerEntry);
        return _projector.Membership;
    }

    /// <summary>Remembers the new entry of an outer document and joins it with the inner documents that its keys name.</summary>
    private void AddOuter(JoinWriter writer, IndexView outer, IndexView inner, long hash, long entry)
    {
        JoinPlan plan = _plan;

        if (writer.SetOuterDocument(hash, entry) != 0)
            throw new JoinRebuildRequiredException(JoinRebuildReason.HashCollision);

        if (_keyReader.Read(outer, entry, _file) == false)
            return;

        ReadOnlySpan<long> keys = _keyReader.Keys;
        writer.SetOuterKeys(entry, keys);

        int joined = 0;
        foreach (long innerKey in keys)
        {
            if (writer.TryGetInnerDocument(innerKey, out _) == false)
                continue;

            joined++;
            for (int f = 0; f < plan.Fields.Length; f++)
            {
                _scratchRefs[f].Count = 0;
                writer.TryGetInnerTerms(f, innerKey, ref _scratchRefs[f]);
                foreach (long termRef in _scratchRefs[f].ToSpan())
                    writer.AddToTerm(f, termRef, entry);
            }

            writer.AddToAny(entry);
            if (plan.GroupingFields.Length > 0)
            {
                int mask = MembershipNow(writer, inner, innerKey);
                for (int g = 0; g < plan.GroupingFields.Length; g++)
                {
                    if ((mask & (1 << g)) != 0)
                        writer.AddToGrouping(g, entry);
                }
            }
        }

        if (joined >= 2)
            writer.AddRowsWithSeveralJoined(1);
    }
}
