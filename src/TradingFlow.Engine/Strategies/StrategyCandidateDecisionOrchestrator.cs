using System.Text.Json;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

public sealed record PersistedStrategyDecision(
    CandidateRecord Candidate,
    StrategyDecisionResult? Decision,
    bool WasAlreadyTerminal)
{
    public bool IsNewTrigger =>
        !WasAlreadyTerminal &&
        Decision?.IsTriggered == true &&
        Candidate.State == StrategyCandidateState.Triggered;
}

public interface IStrategyCandidateDecisionOrchestrator
{
    Task<PersistedStrategyDecision> EvaluateAsync(
        ProductionRun run,
        StrategyDecisionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists discovery before evaluating the pure decision kernel, then journals
/// every resulting transition with optimistic concurrency. Order adapters may act
/// only on the returned persisted Triggered candidate.
/// </summary>
public sealed class StrategyCandidateDecisionOrchestrator(
    IStrategyDecisionKernel kernel,
    ICandidateRepository candidates) : IStrategyCandidateDecisionOrchestrator
{
    public async Task<PersistedStrategyDecision> EvaluateAsync(
        ProductionRun run,
        StrategyDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(request);
        RequireRunProvenance(run, request);

        var existing = await candidates.GetAsync(request.Candidate.CandidateId, cancellationToken);
        if (existing is not null && IsTerminal(existing.State))
        {
            RequireSameIdentity(existing, run, request);
            return new PersistedStrategyDecision(existing, null, true);
        }

        var persisted = await candidates.UpsertDiscoveryAsync(
            run,
            CreateDiscoveryRecord(run, request),
            cancellationToken);
        var persistedRequest = request with
        {
            Candidate = request.Candidate with
            {
                Discovery = request.Candidate.Discovery with { IsPersisted = true },
                State = persisted.State,
                Version = persisted.Version
            }
        };
        var decision = kernel.Evaluate(persistedRequest);
        var transitions = decision.Transitions.Length == 0
            ?
            [
                new CandidateTransitionAppendRequest(
                    persisted.State,
                    persisted.State,
                    request.AsOfUtc,
                    decision.NoEntryReason ?? "candidate_revalidated",
                    "strategy_decision_kernel",
                    decision.CanonicalDecisionJson)
            ]
            : decision.Transitions
                .Select(transition => new CandidateTransitionAppendRequest(
                    transition.From,
                    transition.To,
                    transition.OccurredAtUtc,
                    transition.ReasonCode,
                    "strategy_decision_kernel",
                    decision.CanonicalDecisionJson))
                .ToArray();
        var applied = await candidates.ApplyDecisionAsync(
            run,
            persisted.CandidateId,
            persisted.Version,
            decision.SemanticDecisionSha256,
            request.Candidate.Discovery.ExpiresAtUtc,
            transitions,
            cancellationToken);
        return new PersistedStrategyDecision(applied, decision, false);
    }

    private static CandidateRecord CreateDiscoveryRecord(
        ProductionRun run,
        StrategyDecisionRequest request) => new()
    {
        CandidateId = request.Candidate.CandidateId,
        Symbol = request.Candidate.Identity.Symbol,
        DiscoveredAtUtc = request.Candidate.Discovery.ObservedAtUtc,
        RevalidatedAtUtc = request.AsOfUtc,
        DiscoverySource = String.Join(",", request.Candidate.Discovery.Sources),
        Horizon = request.Strategy.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase)
            ? "swing"
            : "intraday",
        LastPrice = ResolveLastPrice(request),
        SetupScoresJson = JsonSerializer.Serialize(new
        {
            request.Candidate.Identity.SetupKey,
            request.Candidate.Discovery.AggregateId,
            request.Candidate.Discovery.AggregateVersion,
            request.Candidate.Discovery.Sources,
            request.Candidate.Discovery.EvidenceSha256
        }),
        SelectedStrategy = request.StrategyIdentity.StrategyId,
        StrategyContentSha256 = request.StrategyIdentity.ContentSha256,
        AdmissionProfileId = request.AdmissionProfile.ProfileId,
        AdmissionProfileVersion = request.AdmissionProfile.ProfileVersion,
        SetupKey = request.Candidate.Identity.SetupKey,
        DiscoveryWindowStartUtc = request.Candidate.Identity.DiscoveryWindowStartUtc,
        DiscoveryWindowEndUtc = request.Candidate.Identity.DiscoveryWindowEndUtc,
        State = StrategyCandidateState.Discovered,
        ExpiresAtUtc = request.Candidate.Discovery.ExpiresAtUtc,
        RejectReasonsJson = "[]",
        RunId = run.RunId,
        SchemaVersion = run.SchemaVersion,
        ConfigHash = run.ConfigHash,
        CodeVersion = run.CodeVersion
    };

    private static decimal? ResolveLastPrice(StrategyDecisionRequest request) =>
        request.SnapshotsByTimeframe.TryGetValue(request.Strategy.Execution.Timeframe, out var execution) &&
        !execution.IsDefaultOrEmpty
            ? execution[^1].CurrentPrice
            : request.SnapshotsByTimeframe[request.Strategy.Timeframe][^1].CurrentPrice;

    private static void RequireRunProvenance(ProductionRun run, StrategyDecisionRequest request)
    {
        if (run.RunId == Guid.Empty ||
            String.IsNullOrWhiteSpace(run.Profile) ||
            String.IsNullOrWhiteSpace(run.ConfigHash) ||
            String.IsNullOrWhiteSpace(run.CodeVersion) ||
            request.Candidate.CandidateId == Guid.Empty)
        {
            throw new InvalidOperationException("Candidate evaluation requires complete run and candidate provenance.");
        }
    }

    private static void RequireSameIdentity(
        CandidateRecord candidate,
        ProductionRun run,
        StrategyDecisionRequest request)
    {
        if (candidate.RunId != run.RunId)
        {
            throw new InvalidOperationException("Persisted candidate belongs to a different execution run.");
        }

        if (!candidate.Symbol.Equals(request.Candidate.Identity.Symbol, StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(candidate.SelectedStrategy, request.StrategyIdentity.StrategyId, StringComparison.Ordinal) ||
            !candidate.StrategyContentSha256.Equals(request.StrategyIdentity.ContentSha256, StringComparison.Ordinal) ||
            !candidate.AdmissionProfileId.Equals(request.AdmissionProfile.ProfileId, StringComparison.Ordinal) ||
            !candidate.AdmissionProfileVersion.Equals(request.AdmissionProfile.ProfileVersion, StringComparison.Ordinal) ||
            !candidate.SetupKey.Equals(request.Candidate.Identity.SetupKey, StringComparison.Ordinal) ||
            candidate.DiscoveryWindowStartUtc != request.Candidate.Identity.DiscoveryWindowStartUtc ||
            candidate.DiscoveryWindowEndUtc != request.Candidate.Identity.DiscoveryWindowEndUtc)
        {
            throw new InvalidOperationException("Persisted candidate identity does not match the requested strategy setup.");
        }
    }

    private static bool IsTerminal(StrategyCandidateState state) => state is
        StrategyCandidateState.Triggered or
        StrategyCandidateState.Rejected or
        StrategyCandidateState.Expired or
        StrategyCandidateState.RiskBlocked or
        StrategyCandidateState.Consumed;
}
