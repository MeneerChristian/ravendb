using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FastTests.Server.Documents.Indexing.Joins.Query;
using FastTests.Server.Documents.Indexing.Joins.Refresh;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes.Joins;
using Raven.Server.Documents.Indexes.Joins.Auto;
using Raven.Server.Documents.Indexes.Joins.Refresh;
using Raven.Server.Documents.Indexes.Joins.Serving.Resident;
using Sparrow;
using Xunit;

namespace FastTests.Server.Documents.Indexing.Joins.Fuzz;

/// <summary>A fuzz class whose join indexes have the storage mode Auto: the mode is decided by the rows per key of the outer entries when the file is built (Join.IndirectEnabled is on).</summary>
public interface IAutoModeTest
{
}

/// <summary>The first thing that differed: the round of the plan, the phase and what was seen.</summary>
internal sealed record FuzzFailure(int Round, string Phase, string Message);

/// <summary>Replays a <see cref="FuzzPlan"/> against a database and compares the join index with its invariant and with the join of the server after every round.</summary>
internal sealed class JoinFuzzRunner(RavenTestBase test, ITestOutputHelper output)
{
    private JoinIndex _configured;

    public long Queries { get; private set; }

    public long Served { get; private set; }

    public long ResidentListsChecked { get; private set; }

    public string Summary { get; private set; } = "";

    private static readonly object SummaryGate = new();

    private int _seed;

    private bool _dense;

    private readonly Dictionary<JoinStorageMode, int> _resolved = [];

    private readonly SortedSet<int> _rowsPerKey = [];

    private bool IsAuto => test is IAutoModeTest && _shapeIsAuto;

    private bool _shapeIsAuto;

    private JoinStorageMode ExpectedMode(FuzzShape shape) => test is IIndirectModeTest && shape != FuzzShape.OuterId ? JoinStorageMode.Indirect : JoinStorageMode.Expanded;

    private static IndexDefinition[] IndexesOf(FuzzSettings s) => s.Shape switch
    {
        FuzzShape.InnerId => [s.Arrays ? JoinIndexHarness.MultiOuterIndex() : JoinIndexHarness.OuterIndex(), JoinIndexHarness.InnerIndex()],
        FuzzShape.Field => s.Arrays ? [JoinIndexHarness.MultiOuterIndex(), JoinIndexHarness.FieldMultiInnerIndex()] : [JoinIndexHarness.OuterIndex(), JoinIndexHarness.FieldInnerIndex()],
        FuzzShape.OuterId => [JoinIndexHarness.OuterIndex(), s.Arrays ? JoinIndexHarness.FieldMultiInnerIndex() : JoinIndexHarness.FieldInnerIndex()],
        _ => throw new ArgumentOutOfRangeException()
    };

    public async Task<FuzzFailure> RunAsync(FuzzPlan plan)
    {
        FuzzSettings s = plan.Settings;
        _configured = null;
        Queries = Served = ResidentListsChecked = 0;
        var settings = new Dictionary<string, string>
        {
            [RavenConfiguration.GetKey(x => x.Indexing.CoraxIncludeDocumentScore)] = true.ToString(),
            [RavenConfiguration.GetKey(x => x.Join.MaxIdsPerCommit)] = s.MaxIds.ToString(),
            [RavenConfiguration.GetKey(x => x.Join.MaxUnindexedIdsPerCommit)] = s.MaxUnindexedIds.ToString(),
            [RavenConfiguration.GetKey(x => x.Join.MaxInnerListChangesPerOuterEntry)] = s.MaxInnerListChanges.ToString(),
            [RavenConfiguration.GetKey(x => x.Join.MinAtomEntriesForDirectWrite)] = s.MinAtomEntriesForDirectWrite.ToString()
        };
        _shapeIsAuto = s.Shape != FuzzShape.OuterId;
        _seed = s.Seed;
        _dense = s.Dense;
        _resolved.Clear();
        _rowsPerKey.Clear();
        if (IsAuto)
        {
            settings[RavenConfiguration.GetKey(x => x.Join.IndirectEnabled)] = true.ToString();
            settings[RavenConfiguration.GetKey(x => x.Join.AutoMinimumAvailableMemory)] = "100";
        }

        using JoinIndexHarness h = await JoinIndexHarness.CreateAsync(test, settings, putJoin: false, runInMemory: s.Persistent == false, indexes: IndexesOf(s));
        IndexDefinition definition = s.Shape switch
        {
            FuzzShape.InnerId => JoinIndexHarness.JoinIndexDefinition(),
            FuzzShape.Field => JoinIndexHarness.FieldJoinIndexDefinition(),
            FuzzShape.OuterId => JoinIndexHarness.FieldJoinIndexDefinition(outerId: true),
            _ => throw new ArgumentOutOfRangeException()
        };
        if (IsAuto)
            SetAutoInputs(h);
        else
        {
            definition.Join.StorageMode = ExpectedMode(s.Shape);
            await h.PutJoinAsync(definition);
            ApplyResident(h, s);
            Assert.Equal(ExpectedMode(s.Shape), h.Join().Plan.BuildMode);
        }

        var oracle = new JoinIndexQueryOracle(h);
        for (int r = 0; r < plan.Rounds.Count; r++)
        {
            FuzzRound round = plan.Rounds[r];
            FuzzFailure failure = await WriteAsync(h, s, round, r);
            if (failure == null && IsAuto && r == 0)
            {
                // the first build sees the data of the preload, so that the rule has rows to count
                definition.Join.StorageMode = Enum.TryParse(Environment.GetEnvironmentVariable("RAVEN_JOIN_FUZZ_AUTO_FORCE"), out JoinStorageMode forced) ? forced : JoinStorageMode.Auto;
                await h.PutJoinAsync(definition);
            }

            failure ??= await StaleQueriesAsync(oracle, s, round, r) ?? await CheckAsync(h, oracle, s, round, r);
            if (failure != null)
                return failure;
        }

        Summarize(h);
        return null;
    }

    private async Task<FuzzFailure> WriteAsync(JoinIndexHarness h, FuzzSettings s, FuzzRound round, int r)
    {
        bool paused = false;
        try
        {
            for (int b = 0; b <= round.Batches.Count; b++)
            {
                foreach (FuzzAdmin admin in round.Admin.Where(x => x.AfterBatch == b))
                {
                    await AdminAsync(h, s, admin);
                    paused |= admin.Kind == FuzzAdminKind.PauseJoin;
                }

                if (b < round.Batches.Count)
                    Write(h, s, round.Batches[b]);
            }
        }
        catch (Exception e)
        {
            return new FuzzFailure(r, "Write", e.ToString());
        }
        finally
        {
            if (paused)
                await RetryWhileMissingAsync(() => h.Store.Maintenance.SendAsync(new StartIndexOperation(JoinIndexHarness.JoinName)));
        }

        return null;
    }

    private static void Write(JoinIndexHarness h, FuzzSettings s, List<FuzzOp> batch)
    {
        bool fieldKeys = s.Shape != FuzzShape.InnerId;
        h.Save(session =>
        {
            foreach (FuzzOp op in batch)
            {
                switch (op.Kind)
                {
                    case FuzzKind.PutOuter:
                        session.Store(new JoinIndexHarness.Conversation
                        {
                            ContextId = s.Arrays ? null : op.Keys?.FirstOrDefault(),
                            ContextIds = s.Arrays ? op.Keys : null,
                            Title = op.Title
                        }, $"conversations/{op.Number}");
                        break;
                    case FuzzKind.DeleteOuter:
                        session.Delete($"conversations/{op.Number}");
                        break;
                    case FuzzKind.PutInner:
                        session.Store(new JoinIndexHarness.Context
                        {
                            Code = fieldKeys && s.Arrays == false ? op.Keys?.FirstOrDefault() : null,
                            Codes = fieldKeys && s.Arrays ? op.Keys : null,
                            Country = op.Country,
                            Name = op.Name,
                            Rank = op.Rank,
                            IsActive = op.Active,
                            Title = op.Title
                        }, $"contexts/{op.Number}");
                        break;
                    case FuzzKind.DeleteInner:
                        session.Delete($"contexts/{op.Number}");
                        break;
                }
            }
        });
    }

    private async Task AdminAsync(JoinIndexHarness h, FuzzSettings s, FuzzAdmin admin)
    {
        switch (admin.Kind)
        {
            case FuzzAdminKind.Restart:
                await h.Store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(h.Store.Database, disable: true));
                await h.Store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(h.Store.Database, disable: false));
                await h.AttachAsync();
                if (IsAuto)
                    SetAutoInputs(h);

                ApplyResident(h, s);
                break;
            case FuzzAdminKind.PauseJoin:
                await ThrowUnlessAsync(() => h.Join() != null, "the join index does not exist before it is paused");
                await RetryWhileMissingAsync(() => h.Store.Maintenance.SendAsync(new StopIndexOperation(JoinIndexHarness.JoinName)));
                await ThrowUnlessAsync(() => h.Join() == null || h.Stats().Status == IndexRunningStatus.Paused, "the join index was not paused");
                break;
            default:
                string name = admin.Kind switch
                {
                    FuzzAdminKind.ResetJoin => JoinIndexHarness.JoinName,
                    FuzzAdminKind.ResetOuterIndex => JoinIndexHarness.OuterName,
                    _ => JoinIndexHarness.InnerName
                };
                JoinIndex before = h.Join();
                await RetryWhileMissingAsync(() => h.Store.Maintenance.SendAsync(new ResetIndexOperation(name)));
                // a join index that was already being made again keeps its instance: the invariant and the queries of the round tell whether it is right
                try
                {
                    await ThrowUnlessAsync(() => h.Join() is { } now && ReferenceEquals(now, before) == false, name);
                }
                catch (TimeoutException)
                {
                    await ThrowUnlessAsync(() => h.Join() != null, $"the join index does not exist after the reset of {name}");
                }

                ApplyResident(h, s);
                break;
        }
    }

    /// <summary>What the server tells the choice of the mode, so that the mode depends on the rows per key only and not on the memory of the machine or on the caches of other tests of the process.</summary>
    private static void SetAutoInputs(JoinIndexHarness h)
    {
        AutoJoinManager hooks = h.Database.IndexStore.AutoJoins;
        hooks.AvailableMemoryForTesting = () => new Size(64L << 30, SizeUnit.Bytes);
        hooks.ResidentInputsForTesting = () => new JoinOptions.ResidentInputs(1L << 30, false);
    }

    /// <summary>The mode that the build resolved must be the one of the rule for the rows per key that it counted (the threshold is <c>Join.AutoIndirectMinRowsPerKey</c>, 30).</summary>
    private FuzzFailure CheckAutoMode(JoinIndexHarness h, int r)
    {
        JoinIndexStats stats = h.Stats().Join;
        JoinStorageMode resolved = stats.ResolvedStorageMode ?? JoinStorageMode.Auto;
        _resolved[resolved] = _resolved.GetValueOrDefault(resolved) + 1;

        // the rows per key are those the build counted: a file that was opened again after a restart has no count, only its mode
        if (stats.RowsPerKey is not { } rows)
            return stats.StorageMode == JoinStorageMode.Auto && resolved is JoinStorageMode.Indirect or JoinStorageMode.Expanded ? null : new FuzzFailure(r, "AutoMode", $"mode {stats.StorageMode} resolved to {resolved}");

        _rowsPerKey.Add((int)rows);
        JoinStorageMode expected = rows >= 30 ? JoinStorageMode.Indirect : JoinStorageMode.Expanded;
        if (stats.StorageMode != JoinStorageMode.Auto || resolved != expected)
            return new FuzzFailure(r, "AutoMode", $"mode {stats.StorageMode} resolved to {stats.ResolvedStorageMode} with {stats.RowsPerKey?.ToString() ?? "no"} rows per key, the rule says {expected}");

        return null;
    }

    /// <summary>A join index that a refresh found too costly to refresh is made again by the server (deleted and created): for a moment it does not exist, and a call that found it before may reach it disposed.</summary>
    private static async Task RetryWhileMissingAsync(Func<Task> action)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception e) when (i < 1500 && (e is IndexDoesNotExistException || e is RavenException && e.ToString().Contains("was already disposed")))
            {
                // the index is gone, or it was disposed between the lookup of the handler and its call
                await Task.Delay(20);
            }
        }
    }

    private static async Task ThrowUnlessAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 1500; i++)
        {
            try
            {
                if (condition())
                    return;
            }
            catch (Exception e) when (e is Raven.Client.Exceptions.RavenException or InvalidDataException)
            {
                // the index is being replaced
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(what);
    }

    /// <summary>The cache of a join index that was made again (a restart, a reset) has the options of the mode of the test, with the limit of the patch of the seed.</summary>
    private void ApplyResident(JoinIndexHarness h, FuzzSettings s)
    {
        if (test is not IResidentModeTest resident || h.Join() is not { } joinIndex || ReferenceEquals(joinIndex, _configured))
            return;

        ResidentCacheOptions options = resident.ResidentMode switch
        {
            ResidentMode.Off => null,
            ResidentMode.On => new ResidentCacheOptions { BudgetBytes = long.MaxValue / 4, Admission = ResidentAdmission.Always, MaxPatchDocuments = s.MaxPatchDocuments },
            ResidentMode.Tiny => new ResidentCacheOptions { BudgetBytes = 600, Admission = ResidentAdmission.Always, MaxPatchDocuments = s.MaxPatchDocuments },
            _ => new ResidentCacheOptions { BudgetBytes = ResidentCacheRegistry.DefaultBudgetBytes, Admission = ResidentAdmission.FillHold, MaxPatchDocuments = s.MaxPatchDocuments }
        };
        joinIndex.UseResidentCache(options);
        _configured = joinIndex;
    }

    /// <summary>Queries while the indexes are still at work: the answer is not compared (the join of the server and the lists may be at other generations), but it must be an answer.</summary>
    private async Task<FuzzFailure> StaleQueriesAsync(JoinIndexQueryOracle oracle, FuzzSettings s, FuzzRound round, int r)
    {
        if (round.Admin.Any(x => x.Kind is FuzzAdminKind.ResetJoin or FuzzAdminKind.ResetOuterIndex or FuzzAdminKind.ResetInnerIndex or FuzzAdminKind.Restart))
            return null;

        foreach (FuzzQuery q in round.Queries.Take(2))
        {
            try
            {
                await Task.Run(() => oracle.Run(JoinFuzzQueries.Rql(s.Shape, q)));
            }
            catch (Exception e)
            {
                return new FuzzFailure(r, "QueryWhileIndexing", $"{q}: {e}");
            }
        }

        return null;
    }

    private async Task<FuzzFailure> CheckAsync(JoinIndexHarness h, JoinIndexQueryOracle oracle, FuzzSettings s, FuzzRound round, int r)
    {
        try
        {
            JoinIndexInvariantChecker.Result result = await h.CheckAsync(TimeSpan.FromSeconds(round.Admin.Count > 0 ? 90 : 40));
            if (result.Ok == false)
                return new FuzzFailure(r, "Invariant", result.ToString());

            if (result.ResidentChecked > 0)
                ResidentListsChecked += result.ResidentChecked;
        }
        catch (Exception e)
        {
            return new FuzzFailure(r, "Invariant", e.ToString());
        }

        ApplyResident(h, s);
        if (IsAuto && Environment.GetEnvironmentVariable("RAVEN_JOIN_FUZZ_AUTO_FORCE") == null && CheckAutoMode(h, r) is { } autoFailure)
            return autoFailure;

        var differences = new List<string>();
        foreach (FuzzQuery q in new[] { JoinFuzzQueries.Probe }.Concat(round.Queries))
        {
            string rql = JoinFuzzQueries.Rql(s.Shape, q);
            try
            {
                long servedBefore = oracle.Info.Served;
                (QueryAnswer served, QueryAnswer scan) = await Task.Run(() => oracle.Both(rql));
                Queries++;
                bool wasServed = oracle.Info.Served > servedBefore;
                if (wasServed)
                    Served++;

                JoinIndexQueryOracle.AssertSame(rql, served, scan);
                if (ReferenceEquals(q, JoinFuzzQueries.Probe) && wasServed == false)
                    differences.Add($"the probe was not served by the join index although it is current: {rql}. Fallbacks: {string.Join(", ", oracle.Info.Fallbacks)}");
            }
            catch (Exception e)
            {
                differences.Add(e.Message);
            }
        }

        return differences.Count == 0 ? null : new FuzzFailure(r, "Query", string.Join(Environment.NewLine + "----" + Environment.NewLine, differences));
    }

    private void Summarize(JoinIndexHarness h)
    {
        JoinIndexStats stats = h.Stats().Join;
        JoinIndex joinIndex = h.Join();
        var text = new StringBuilder();
        if (IsAuto)
            text.Append($"auto=[{string.Join(", ", _resolved.Select(x => x.Key + "=" + x.Value))}] rowsPerKey=[{string.Join(",", _rowsPerKey)}] ");

        text.Append($"queries={Queries} served={Served} residentListsChecked={ResidentListsChecked} fallbacks=[{string.Join(", ", stats.Fallbacks.Select(x => x.Key + "=" + x.Value))}] ");
        text.Append($"refreshes={joinIndex.Counters.Refreshes} partial={joinIndex.Counters.PartialRefreshes} builds={joinIndex.Counters.Builds} rebuilds=[{string.Join(", ", joinIndex.Counters.RebuildsByReason.Select(x => x.Key + "=" + x.Value))}] ");
        text.Append($"residentHits={stats.ResidentHits} residentMisses={stats.ResidentMisses} residentEpochMisses={stats.ResidentEpochMisses}");
        Summary = text.ToString();
        output.WriteLine(Summary);
        string directory = Environment.GetEnvironmentVariable("RAVEN_JOIN_FUZZ_OUT");
        if (IsAuto && string.IsNullOrWhiteSpace(directory) == false)
        {
            // one line per run, so that a batch run can be counted: how many runs had a build on each side of the threshold
            Directory.CreateDirectory(directory);
            lock (SummaryGate)
                File.AppendAllText(Path.Combine(directory, "auto-summary.txt"), $"seed={_seed} dense={_dense} {text}{Environment.NewLine}");
        }
    }
}
