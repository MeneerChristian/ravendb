using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Corax.Mappings;
using Corax.Querying;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Storage;
using Raven.Server.Logging;
using Sparrow.Logging;
using Sparrow.Server.Logging;
using Voron;
using Voron.Data.CompactTrees;
using Voron.Util;

namespace Raven.Server.Documents.Indexes.Joins.Serving.Resident;

/// <summary>
/// The resident cache of one Indirect join index and the rules that tie it to the file and to the outer snapshots. The lists of the cache are valid for one outer snapshot, the epoch; the epoch is in a scope that
/// names the outer environment and the file whose key references the lists use (the seed of the file is new for every file that is built), so a reader of an older file or of another environment never hits.
/// A refresh collects its changes of the outer rows in a <see cref="Run"/> and applies them only after its join index transaction committed.
/// </summary>
internal sealed class JoinResident : IDisposable
{
    private static readonly RavenLogger Logger = RavenLogManager.Instance.GetLoggerForServer<JoinResident>();

    private volatile bool _disposed;
    private int _warmPending;
    private volatile Stamp _stamp;
    private long _followed;
    private long _establishes, _patchesApplied, _patchEpochMismatches, _patchTooLarge, _patchIdTooLarge, _partialDrops, _warmUps, _failures, _rebuildDrops;

    public JoinResident(ResidentCacheOptions options, ResidentCacheRegistry registry)
    {
        Cache = new ResidentKeyListCache(options, registry);
    }

    public ResidentKeyListCache Cache { get; }

    /// <summary>What the outer index had consumed in the snapshot that an epoch names: another snapshot of the same environment with the same cursors has the same entries.</summary>
    private sealed record Stamp(ResidentEpoch Epoch, IReadOnlyDictionary<string, (long Doc, long Tombstone)> Consumed, long OuterLastEntryId);

    public long Followed => Interlocked.Read(ref _followed);

    /// <summary>Called when the cache has no epoch and the next refresh has to establish one, even when the indexes did not change.</summary>
    public Action ColdRequested { get; set; }

    public long Establishes => Interlocked.Read(ref _establishes);

    public long PatchesApplied => Interlocked.Read(ref _patchesApplied);

    public long PatchEpochMismatches => Interlocked.Read(ref _patchEpochMismatches);

    public long PatchTooLarge => Interlocked.Read(ref _patchTooLarge);

    public long PatchIdTooLarge => Interlocked.Read(ref _patchIdTooLarge);

    public long PartialDrops => Interlocked.Read(ref _partialDrops);

    public long RebuildDrops => Interlocked.Read(ref _rebuildDrops);

    public long WarmUps => Interlocked.Read(ref _warmUps);

    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>The scope of the epoch of a file: the outer environment and the seed of the file, in the form of the environment id of an epoch.</summary>
    public static Guid ScopeOf(Guid outerEnvironmentId, long seed)
    {
        Span<byte> bytes = stackalloc byte[16];
        outerEnvironmentId.TryWriteBytes(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes, BinaryPrimitives.ReadInt64LittleEndian(bytes) ^ seed);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.Slice(8), BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(8)) ^ (long)(BitOperations.RotateLeft((ulong)seed, 29) * 0x9E3779B97F4A7C15UL));
        var scope = new Guid(bytes);
        return scope == Guid.Empty ? new Guid(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) : scope;
    }

    /// <summary>The cache a query reads, for the outer snapshot of the query. Null when nothing can be read from it.</summary>
    public JoinResidentRead ReadOf(Guid outerEnvironmentId, long outerTransactionId, long seed, long atomCode, IReadOnlyDictionary<string, (long Doc, long Tombstone)> outerConsumed) =>
        new(this, ScopeOf(outerEnvironmentId, seed), outerTransactionId, atomCode, outerConsumed);

    /// <summary>
    /// The outer index commits transactions that move none of the cursors it consumed (the statistics of a batch, for one), so its snapshot is newer than the epoch with the same entries. A reader whose snapshot has
    /// the cursors of the epoch and a newer transaction moves the epoch to its transaction: the entries of an index are a function of what it consumed. A reader of other cursors is not followed.
    /// </summary>
    public void Follow(in JoinResidentRead read, long outerLastEntryId)
    {
        Stamp stamp = _stamp;
        if (stamp == null || stamp.Consumed == null || read.Consumed == null || _disposed)
            return;

        ResidentEpoch epoch = Cache.Epoch;
        if (epoch.IsNone || epoch != stamp.Epoch || epoch.EnvironmentId != read.Scope || read.OuterTransactionId <= epoch.TransactionId || SameCursors(stamp.Consumed, read.Consumed) == false)
            return;

        var next = epoch with { TransactionId = read.OuterTransactionId };
        if (Cache.TryAdvanceEpoch(epoch, next, outerLastEntryId))
        {
            Interlocked.Increment(ref _followed);
            _stamp = new Stamp(next, stamp.Consumed, outerLastEntryId);
        }
    }

    private static bool SameCursors(IReadOnlyDictionary<string, (long Doc, long Tombstone)> a, IReadOnlyDictionary<string, (long Doc, long Tombstone)> b)
    {
        if (a.Count != b.Count)
            return false;

        foreach ((string collection, (long doc, long tombstone) value) in a)
        {
            if (b.TryGetValue(collection, out (long Doc, long Tombstone) other) == false || other.Doc != value.doc || other.Tombstone != value.tombstone)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Starts a refresh. A cache that is not at the epoch the file's generation says (cold after the open, dropped by a partial refresh or by a patch that did not apply, another file, another environment) is
    /// established at the outer snapshot of this refresh: an empty cache is valid for any snapshot. The run is disposed with the transaction of the refresh, whether it commits or not.
    /// </summary>
    public Run BeginRun(Guid outerEnvironmentId, long outerTransactionId, long outerLastEntryId, long seed, long publishedSerial, IReadOnlyDictionary<string, (long Doc, long Tombstone)> outerConsumed,
        bool outerPartial)
    {
        if (_disposed)
            return new Run(this, Guid.Empty, ResidentEpoch.None, outerTransactionId, outerLastEntryId, outerConsumed, false);

        Guid scope = ScopeOf(outerEnvironmentId, seed);
        ResidentEpoch current = Cache.Epoch;
        bool needsWarmUp = false;
        if (current.IsNone || current.EnvironmentId != scope || current.Generation != publishedSerial)
        {
            var epoch = new ResidentEpoch(scope, outerTransactionId, publishedSerial);
            if (Cache.TryEstablish(epoch, outerLastEntryId))
            {
                Interlocked.Increment(ref _establishes);
                _stamp = new Stamp(epoch, outerConsumed, outerLastEntryId);

                // the chunks of a refresh that was cut drop the cache at every commit: the lists are loaded once the outer side is complete
                if (outerPartial)
                    Volatile.Write(ref _warmPending, 1);
                else
                    needsWarmUp = true;
            }
        }
        else if (outerPartial == false && Interlocked.Exchange(ref _warmPending, 0) == 1)
        {
            needsWarmUp = true;
        }

        return new Run(this, scope, Cache.Epoch, outerTransactionId, outerLastEntryId, outerConsumed, needsWarmUp) { WarmUpSkipped = outerPartial };
    }

    /// <summary>The keys that a query can ask for, the keys of the any list, are loaded from the outer snapshot of the epoch until the budget is full. A failure only leaves the cache cold.</summary>
    public void WarmUp(Run run, JoinReader file, IndexSearcher outer, Slice outerKeyField, string outerField, CancellationToken token)
    {
        if (run.Start.IsNone || _disposed)
            return;

        var keys = new ContextBoundNativeList<long>(outer.Allocator);
        try
        {
            CompactTree tree = outer.GetTermsFor(outerKeyField);
            if (tree == null)
                return;

            file.ReadAtomKeys(JoinReader.AtomOfAny(), ref keys);
            FieldMetadata metadata = outer.FieldMetadataBuilder(outerField);
            var source = new JoinOuterKeyLists(outer, metadata, file, tree);
            try
            {
                ResidentWarmUpResult result = Cache.WarmUp(run.Start, keys.ToSpan(), ref source, token);
                Interlocked.Add(ref _warmUps, result.Loaded);
            }
            finally
            {
                source.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Interlocked.Increment(ref _failures);
            Cache.Invalidate();
            if (Logger.IsWarnEnabled)
                Logger.Warn("The warm-up of the resident cache of a join index failed. The cache is empty.", e);
        }
        finally
        {
            keys.Dispose();
        }
    }

    /// <summary>The file was built: the key references are new, nothing of the cache is meant for them.</summary>
    public void DropAfterBuild()
    {
        if (_disposed)
            return;

        Interlocked.Increment(ref _rebuildDrops);
        Cache.Invalidate();
        ColdRequested?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        Cache.Dispose();
    }

    /// <summary>The changes that one refresh makes to the lists, until its transaction is committed (applied after it) or ends (dropped).</summary>
    internal sealed class Run : IDisposable
    {
        private readonly JoinResident _owner;
        private readonly Guid _scope;
        private readonly long _outerTransactionId;
        private readonly long _outerLastEntryId;
        private readonly IReadOnlyDictionary<string, (long Doc, long Tombstone)> _outerConsumed;
        private readonly object _gate = new();
        private bool _disposed;

        internal Run(JoinResident owner, Guid scope, ResidentEpoch start, long outerTransactionId, long outerLastEntryId, IReadOnlyDictionary<string, (long Doc, long Tombstone)> outerConsumed,
            bool needsWarmUp)
        {
            _owner = owner;
            _scope = scope;
            Start = start;
            _outerTransactionId = outerTransactionId;
            _outerLastEntryId = outerLastEntryId;
            _outerConsumed = outerConsumed;
            NeedsWarmUp = needsWarmUp;
            if (start.IsNone == false)
                Patch = new ResidentPatch();
        }

        /// <summary>The epoch the cache was at when the refresh started, None when it could not be established.</summary>
        public ResidentEpoch Start { get; }

        public bool NeedsWarmUp { get; }

        /// <summary>The lists were not warmed because the outer side is cut into chunks: the run after the last chunk does it.</summary>
        public bool WarmUpSkipped { get; init; }

        /// <summary>Where the refresh records its changes of the outer rows; null when the cache has no epoch.</summary>
        public ResidentPatch Patch { get; }

        /// <summary>
        /// After the join index transaction committed: moves the cache to the outer snapshot of the refresh with the changes (<paramref name="outerApplied"/>: the outer side was applied completely, so the lists are the snapshot's;
        /// else the lists stay at the snapshot they had and only the stamp of the generation moves), or drops it when the outer side was cut. A patch that does not apply clears the cache, which the next refresh establishes again.
        /// </summary>
        public void Apply(long serial, bool outerApplied, bool outerPartial)
        {
            lock (_gate)
            {
                JoinResident owner = _owner;
                try
                {
                    if (owner._disposed)
                        return;

                    if (_disposed)
                    {
                        // the transaction ended before the changes were applied: they are lost, and the lists are not the snapshot's any more
                        owner.Cache.Invalidate();
                        owner.ColdRequested?.Invoke();
                        return;
                    }

                    if (outerPartial)
                    {
                        Interlocked.Increment(ref owner._partialDrops);
                        owner.Cache.Invalidate();
                        return;
                    }

                    if (Start.IsNone)
                        return;

                    // a reader may have followed the outer index to a newer transaction with the same cursors while the refresh ran: the changes start from that epoch as well
                    ResidentEpoch current = owner.Cache.Epoch;
                    ResidentEpoch expected = current.IsNone == false && current.EnvironmentId == Start.EnvironmentId && current.Generation == Start.Generation && current.TransactionId >= Start.TransactionId
                        ? current
                        : Start;
                    Stamp before = owner._stamp;
                    var next = new ResidentEpoch(_scope, outerApplied ? _outerTransactionId : expected.TransactionId, serial);
                    ResidentPatchOutcome outcome = owner.Cache.ApplyPatch(expected, next, _outerLastEntryId, Patch);
                    switch (outcome)
                    {
                        case ResidentPatchOutcome.Applied:
                            Interlocked.Increment(ref owner._patchesApplied);
                            owner._stamp = new Stamp(next, outerApplied ? _outerConsumed : before?.Epoch == expected ? before.Consumed : null, _outerLastEntryId);
                            if (WarmUpSkipped && outerApplied)
                                owner.ColdRequested?.Invoke();

                            return;
                        case ResidentPatchOutcome.EpochMismatch:
                            Interlocked.Increment(ref owner._patchEpochMismatches);
                            break;
                        case ResidentPatchOutcome.TooLarge:
                            Interlocked.Increment(ref owner._patchTooLarge);
                            break;
                        case ResidentPatchOutcome.IdTooLarge:
                            Interlocked.Increment(ref owner._patchIdTooLarge);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    owner.ColdRequested?.Invoke();
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // the cache clears itself when a patch fails half way
                    Interlocked.Increment(ref owner._failures);
                    owner.Cache.Invalidate();
                    owner.ColdRequested?.Invoke();
                    if (Logger.IsWarnEnabled)
                        Logger.Warn("The resident cache of a join index could not apply the changes of a refresh. The cache was cleared.", e);
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                Patch?.Dispose();
            }
        }
    }
}
