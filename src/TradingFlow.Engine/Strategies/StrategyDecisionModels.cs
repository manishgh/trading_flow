using System.Collections.Immutable;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

public sealed record StrategyCandidateIdentity(
    string Symbol,
    string StrategyContentSha256,
    string SetupKey,
    DateTimeOffset DiscoveryWindowStartUtc,
    DateTimeOffset DiscoveryWindowEndUtc);

public sealed record StrategyDiscoveryEvidence(
    Guid AggregateId,
    long AggregateVersion,
    bool IsPersisted,
    bool IsActive,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    ImmutableArray<string> Sources,
    string EvidenceSha256);

public sealed record StrategyMarketStateEvidence(
    bool IsWarm,
    string StateFingerprint,
    string Provider,
    string Feed,
    string AdjustmentPolicy,
    string Reliability,
    DateTimeOffset VerifiedAtUtc,
    ImmutableDictionary<string, int> CompletedBarsByTimeframe,
    ImmutableDictionary<string, int> RequiredWarmupBarsByTimeframe);

public sealed record StrategyEligibilityEvidence(
    bool IsEligible,
    string Source,
    string EvidenceId,
    DateTimeOffset EvaluatedAtUtc,
    string EvidenceJson);

/// <summary>
/// Candidate projection supplied to the kernel. The evidence objects make every
/// adapter prove where its persisted, warm, universe, and regime claims came from.
/// </summary>
public sealed record StrategyCandidateSnapshot(
    Guid CandidateId,
    StrategyCandidateIdentity Identity,
    StrategyDiscoveryEvidence Discovery,
    StrategyMarketStateEvidence MarketState,
    StrategyEligibilityEvidence Universe,
    StrategyEligibilityEvidence Regime,
    StrategyCandidateState State,
    int Version);

/// <summary>
/// One exact provider revision. DecisionKnownAtUtc, rather than publication time,
/// determines whether this revision may influence an as-of decision.
/// </summary>
public sealed record StrategyCatalystSnapshot(
    string Provider,
    string ProviderArticleId,
    string RevisionId,
    DateTimeOffset ProviderPublishedAtUtc,
    DateTimeOffset? ProviderUpdatedAtUtc,
    DateTimeOffset? FirstReceivedAtUtc,
    DateTimeOffset DecisionKnownAtUtc,
    string ClassificationVersion,
    string AvailabilityEvidence,
    string DeduplicationKey,
    CatalystEvent Event);

public sealed record StrategyDecisionRequest(
    StrategyArtifactIdentity StrategyIdentity,
    StrategyAdmissionProfile AdmissionProfile,
    StrategyDefinition Strategy,
    StrategyCandidateSnapshot Candidate,
    ImmutableDictionary<string, ImmutableArray<OhlcvBar>> BarsByTimeframe,
    ImmutableDictionary<string, ImmutableArray<IndicatorSnapshot>> SnapshotsByTimeframe,
    ImmutableArray<StrategyCatalystSnapshot> Catalysts,
    DateTimeOffset AsOfUtc);

public sealed record StrategyDecisionRule(
    string Stage,
    string Code,
    bool Passed,
    string Source,
    string EvidenceJson);

public sealed record StrategyCandidateTransition(
    StrategyCandidateState From,
    StrategyCandidateState To,
    DateTimeOffset OccurredAtUtc,
    string ReasonCode);

/// <summary>
/// Canonical pre-risk order plan produced from completed strategy evidence. The
/// initial stop is frozen before the fill bar is observable; account sizing,
/// reservations, and broker intent creation are applied later.
/// </summary>
public sealed record StrategyOrderPlan(
    string StrategyId,
    string StrategyName,
    string Direction,
    DateTimeOffset SetupAvailableAtUtc,
    DateTimeOffset TriggeredAtUtc,
    string ExecutionTimeframe,
    int StopContextIndex,
    decimal TriggerReferencePrice,
    decimal InitialStopPrice,
    decimal TargetRMultiple,
    string ProfitTargetMode,
    decimal? ProfitTargetReferencePrice,
    decimal SlippageBps,
    DateTimeOffset ExpiresAtUtc);

public sealed record StrategyDecisionResult(
    StrategyCandidateState State,
    string? Direction,
    TradeSignal? Signal,
    ImmutableArray<StrategyDecisionRule> Rules,
    ImmutableArray<StrategyCandidateTransition> Transitions,
    StrategyOrderPlan? OrderPlan,
    string? NoEntryReason,
    string SemanticDecisionSha256,
    string CanonicalDecisionJson)
{
    public bool IsTriggered => State == StrategyCandidateState.Triggered;
}

public interface IStrategyDecisionKernel
{
    StrategyDecisionResult Evaluate(StrategyDecisionRequest request);
}
