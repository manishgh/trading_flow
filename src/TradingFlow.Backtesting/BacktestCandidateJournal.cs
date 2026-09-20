using System.Text;
using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Data.Backtesting;
using Microsoft.Data.Sqlite;

namespace TradingFlow.Backtesting;

/// <summary>
/// Keeps candidate state for one isolated backtest worker, indexed on disk for durable runs.
/// Backtests need the same persisted transition contract as paper/live, but writing
/// every simulated bar to the operational database would couple parallel research
/// runs and add database latency to deterministic replay.
/// </summary>
internal sealed class BacktestCandidateJournal : ICandidateRepository, IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, CandidateRecord> candidates = [];
    private readonly Dictionary<Guid, List<CandidateTransitionRecord>> transitions = [];
    private readonly FileStream? durableStream;
    private readonly StreamWriter? durableWriter;
    private AuditPersistenceException? persistenceFailure;
    private readonly SqliteBacktestCandidateStateStore? stateStore;

    public BacktestCandidateJournal(string? durableJournalPath = null)
    {
        if (String.IsNullOrWhiteSpace(durableJournalPath))
        {
            return;
        }

        try
        {
            DurableJournalPath = Path.GetFullPath(durableJournalPath);
            Directory.CreateDirectory(Path.GetDirectoryName(DurableJournalPath)!);
            durableStream = new FileStream(DurableJournalPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 16 * 1024, FileOptions.WriteThrough);
            durableWriter = new StreamWriter(durableStream, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
            stateStore = new SqliteBacktestCandidateStateStore(StatePath(DurableJournalPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            try { durableWriter?.Dispose(); }
            finally { durableStream?.Dispose(); }
            throw new AuditPersistenceException("Candidate audit initialization failed.", exception);
        }
    }

    public string? DurableJournalPath { get; }
    internal static string StatePath(string journalPath) => journalPath + ".sqlite";
    internal int RetainedCandidateCount { get { lock (sync) return candidates.Count; } }

    public Task<CandidateRecord> UpsertDiscoveryAsync(
        ProductionRun run,
        CandidateRecord candidate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (persistenceFailure is not null) throw persistenceFailure;
            var existing = Find(candidate.CandidateId);
            if (existing is not null)
            {
                RequireSameIdentity(existing, candidate, run);
                return Task.FromResult(Clone(existing));
            }

            if (candidate.RunId != run.RunId || candidate.State != StrategyCandidateState.Discovered)
            {
                throw new InvalidOperationException(
                    "Backtest discovery must be journaled in Discovered state under its owning run.");
            }

            var persisted = Clone(candidate);
            persisted.Version = 0;
            AppendDurable("discovered", persisted, []);
            if (stateStore is null)
            {
                candidates.Add(persisted.CandidateId, persisted);
                transitions.Add(persisted.CandidateId, []);
            }
            return Task.FromResult(Clone(persisted));
        }
    }

    public Task<CandidateRecord> ApplyDecisionAsync(
        ProductionRun run,
        Guid candidateId,
        int expectedVersion,
        string semanticDecisionSha256,
        DateTimeOffset expiresAtUtc,
        IReadOnlyList<CandidateTransitionAppendRequest> requestedTransitions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (persistenceFailure is not null) throw persistenceFailure;
            if (requestedTransitions.Count == 0)
                throw new ArgumentException("A decision must include at least one transition.", nameof(requestedTransitions));
            var candidate = Find(candidateId);
            if (candidate is null)
            {
                throw new InvalidOperationException($"Candidate {candidateId} was not journaled before evaluation.");
            }

            if (candidate.RunId != run.RunId || candidate.Version != expectedVersion)
            {
                throw new InvalidOperationException(
                    $"Candidate {candidateId} expected run/version {run.RunId}/{expectedVersion}, " +
                    $"actual {candidate.RunId}/{candidate.Version}.");
            }

            var current = candidate.State;
            var sequence = candidate.Version;
            var appended = new List<CandidateTransitionRecord>(requestedTransitions.Count);
            foreach (var transition in requestedTransitions)
            {
                if (transition.PreviousState != current)
                {
                    throw new InvalidOperationException(
                        $"Candidate transition expected {current} but supplied {transition.PreviousState}.");
                }

                StrategyCandidateStateMachine.RequireTransition(current, transition.NewState);
                sequence++;
                var persistedTransition = new CandidateTransitionRecord
                {
                    CandidateId = candidateId,
                    Sequence = sequence,
                    PreviousState = current,
                    NewState = transition.NewState,
                    OccurredAtUtc = transition.OccurredAtUtc,
                    ReasonCode = transition.ReasonCode,
                    Source = transition.Source,
                    SemanticDecisionSha256 = semanticDecisionSha256,
                    EvidenceJson = transition.EvidenceJson,
                    RunId = run.RunId,
                    SchemaVersion = run.SchemaVersion,
                    ConfigHash = run.ConfigHash,
                    CodeVersion = run.CodeVersion
                };
                appended.Add(persistedTransition);
                current = transition.NewState;
            }

            // Validate the entire batch and persist it before publishing any mutable state.
            var updated = Clone(candidate);
            updated.State = current;
            updated.Version = sequence;
            updated.ExpiresAtUtc = expiresAtUtc;
            updated.SemanticDecisionSha256 = semanticDecisionSha256;
            updated.RevalidatedAtUtc = requestedTransitions[^1].OccurredAtUtc;
            AppendDurable("decision", updated, appended);
            if (stateStore is null)
            {
                candidates[candidateId] = updated;
                transitions[candidateId].AddRange(appended);
            }
            return Task.FromResult(Clone(updated));
        }
    }

    public Task<CandidateRecord?> GetAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (persistenceFailure is not null) throw persistenceFailure;
            return Task.FromResult(Find(candidateId));
        }
    }

    public Task<IReadOnlyList<CandidateTransitionRecord>> GetTransitionsAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (persistenceFailure is not null) throw persistenceFailure;
            if (stateStore is not null)
            {
                try { return Task.FromResult(stateStore.GetTransitions(candidateId)); }
                catch (SqliteException exception)
                {
                    persistenceFailure = new AuditPersistenceException("Candidate transition index read failed.", exception);
                    throw persistenceFailure;
                }
            }
            return Task.FromResult<IReadOnlyList<CandidateTransitionRecord>>(
                transitions.TryGetValue(candidateId, out var items)
                    ? items.Select(Clone).ToArray()
                    : []);
        }
    }

    public IReadOnlyList<BacktestCandidateDecisionAudit> Snapshot()
    {
        lock (sync)
        {
            if (stateStore is not null)
                throw new InvalidOperationException("Durable audit history must be streamed from its index, not materialized.");
            return candidates.Values
                .OrderBy(candidate => candidate.DiscoveredAtUtc)
                .ThenBy(candidate => candidate.Symbol, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.CandidateId)
                .Select(candidate => new BacktestCandidateDecisionAudit(
                    Clone(candidate),
                    transitions[candidate.CandidateId]
                        .OrderBy(transition => transition.Sequence)
                        .Select(Clone)
                        .ToArray()))
                .ToArray();
        }
    }

    public static ProductionRun CreateRun(
        object effectiveConfiguration,
        string runKey,
        DateTimeOffset startedAtUtc)
    {
        var configHash = EvidenceCanonicalJson.ComputeSha256(effectiveConfiguration);
        var codeHash = EvidenceCanonicalJson.ComputeSha256(new
        {
            Assembly = typeof(BacktestCandidateJournal).Assembly.GetName().Name,
            Version = typeof(BacktestCandidateJournal).Assembly.GetName().Version?.ToString(),
            Module = typeof(BacktestCandidateJournal).Assembly.ManifestModule.ModuleVersionId
        });
        return new ProductionRun
        {
            RunId = StrategyDecisionRequestAssembler.CreateDeterministicId(new
            {
                Profile = "backtest",
                RunKey = runKey,
                ConfigHash = configHash,
                StartedAtUtc = startedAtUtc.ToUniversalTime()
            }),
            SchemaVersion = 1,
            ConfigHash = configHash,
            CodeVersion = codeHash,
            Profile = "backtest",
            Status = "running",
            StartedAtUtc = startedAtUtc.ToUniversalTime()
        };
    }

    public void Dispose()
    {
        lock (sync)
        {
            try { durableWriter?.Dispose(); }
            finally
            {
                try { durableStream?.Dispose(); }
                finally { stateStore?.Dispose(); }
            }
        }
    }

    private void AppendDurable(
        string recordType,
        CandidateRecord candidate,
        IReadOnlyList<CandidateTransitionRecord> appendedTransitions)
    {
        if (durableWriter is null || durableStream is null)
        {
            return;
        }

        try
        {
            durableWriter.WriteLine(JsonSerializer.Serialize(new BacktestCandidateJournalEntry(
                recordType,
                DateTimeOffset.UtcNow,
                Clone(candidate),
                appendedTransitions.Select(Clone).ToArray())));
            durableWriter.Flush();
            durableStream.Flush(flushToDisk: true);
            stateStore?.Save(candidate, appendedTransitions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            persistenceFailure = new AuditPersistenceException("Candidate audit append failed; the run cannot claim complete evidence.", exception);
            throw persistenceFailure;
        }
    }

    private CandidateRecord? Find(Guid candidateId)
    {
        try
        {
            return stateStore is not null ? stateStore.Get(candidateId)
                : candidates.TryGetValue(candidateId, out var value) ? Clone(value) : null;
        }
        catch (SqliteException exception)
        {
            persistenceFailure = new AuditPersistenceException("Candidate audit index read failed.", exception);
            throw persistenceFailure;
        }
    }

    private static void RequireSameIdentity(
        CandidateRecord existing,
        CandidateRecord incoming,
        ProductionRun run)
    {
        if (existing.RunId != run.RunId || incoming.RunId != run.RunId ||
            !existing.Symbol.Equals(incoming.Symbol, StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(existing.SelectedStrategy, incoming.SelectedStrategy, StringComparison.Ordinal) ||
            !existing.StrategyContentSha256.Equals(incoming.StrategyContentSha256, StringComparison.Ordinal) ||
            !existing.AdmissionProfileId.Equals(incoming.AdmissionProfileId, StringComparison.Ordinal) ||
            !existing.AdmissionProfileVersion.Equals(incoming.AdmissionProfileVersion, StringComparison.Ordinal) ||
            !existing.SetupKey.Equals(incoming.SetupKey, StringComparison.Ordinal) ||
            existing.DiscoveryWindowStartUtc != incoming.DiscoveryWindowStartUtc ||
            existing.DiscoveryWindowEndUtc != incoming.DiscoveryWindowEndUtc ||
            !existing.DiscoverySource.Equals(incoming.DiscoverySource, StringComparison.Ordinal) ||
            !existing.SetupScoresJson.Equals(incoming.SetupScoresJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Backtest candidate replay cannot change immutable run, setup, or discovery evidence.");
        }
    }

    private static CandidateRecord Clone(CandidateRecord value) => new()
    {
        CandidateId = value.CandidateId,
        Symbol = value.Symbol,
        DiscoveredAtUtc = value.DiscoveredAtUtc,
        RevalidatedAtUtc = value.RevalidatedAtUtc,
        DiscoverySource = value.DiscoverySource,
        FinvizPreset = value.FinvizPreset,
        Horizon = value.Horizon,
        PreviousClose = value.PreviousClose,
        LastPrice = value.LastPrice,
        GapPct = value.GapPct,
        GapAtr = value.GapAtr,
        PremarketVolume = value.PremarketVolume,
        PremarketDollarVolume = value.PremarketDollarVolume,
        SameTimeRvol = value.SameTimeRvol,
        SpreadBps = value.SpreadBps,
        QuoteAgeMs = value.QuoteAgeMs,
        BenchmarkReturn = value.BenchmarkReturn,
        SectorReturn = value.SectorReturn,
        MarketExcessReturn = value.MarketExcessReturn,
        SectorExcessReturn = value.SectorExcessReturn,
        CatalystResultId = value.CatalystResultId,
        MarketConfirmationScore = value.MarketConfirmationScore,
        SetupScoresJson = value.SetupScoresJson,
        SelectedStrategy = value.SelectedStrategy,
        StrategySemanticVersion = value.StrategySemanticVersion,
        StrategyContentSha256 = value.StrategyContentSha256,
        AdmissionProfileId = value.AdmissionProfileId,
        AdmissionProfileVersion = value.AdmissionProfileVersion,
        SetupKey = value.SetupKey,
        DiscoveryWindowStartUtc = value.DiscoveryWindowStartUtc,
        DiscoveryWindowEndUtc = value.DiscoveryWindowEndUtc,
        State = value.State,
        Version = value.Version,
        ExpiresAtUtc = value.ExpiresAtUtc,
        SemanticDecisionSha256 = value.SemanticDecisionSha256,
        RejectReasonsJson = value.RejectReasonsJson,
        RunId = value.RunId,
        SchemaVersion = value.SchemaVersion,
        ConfigHash = value.ConfigHash,
        CodeVersion = value.CodeVersion
    };

    private static CandidateTransitionRecord Clone(CandidateTransitionRecord value) => new()
    {
        TransitionId = value.TransitionId,
        CandidateId = value.CandidateId,
        Sequence = value.Sequence,
        PreviousState = value.PreviousState,
        NewState = value.NewState,
        OccurredAtUtc = value.OccurredAtUtc,
        ReasonCode = value.ReasonCode,
        Source = value.Source,
        SemanticDecisionSha256 = value.SemanticDecisionSha256,
        EvidenceJson = value.EvidenceJson,
        RunId = value.RunId,
        SchemaVersion = value.SchemaVersion,
        ConfigHash = value.ConfigHash,
        CodeVersion = value.CodeVersion
    };
}

internal sealed record BacktestCandidateJournalEntry(
    string RecordType,
    DateTimeOffset PersistedAtUtc,
    CandidateRecord Candidate,
    IReadOnlyList<CandidateTransitionRecord> Transitions);
