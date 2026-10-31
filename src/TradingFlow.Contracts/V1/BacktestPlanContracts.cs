namespace TradingFlow.Contracts.V1;

/// <summary>An operator-owned stock source; reading choices never copies its members into another list.</summary>
public sealed record BacktestStockSource(string Key, string Name, string Kind, Guid? WishlistId, string? Query)
{
    /// <summary>Uses the two familiar source groups in native pickers.</summary>
    public override string ToString() => $"{(Kind == "wishlist" ? "My wishlists" : "Finviz lists")} · {Name}";
}

/// <summary>A named stock group with its own starting capital for each selected strategy.</summary>
public sealed record BacktestCohort(string Name, IReadOnlyList<string> Symbols, decimal StartingCapital);

/// <summary>Saving this request stores choices only; it never starts execution.</summary>
public sealed record SaveBacktestPlanRequest(StartBacktestRequest Run, int? GroupSize,
    IReadOnlyList<BacktestCohort>? Cohorts = null, IReadOnlyList<string>? ExcludedSymbols = null);

/// <summary>An immutable saved choice; re-saving edited choices creates another explicit plan.</summary>
public sealed record BacktestPlanResponse(long PlanId, DateTimeOffset SavedAtUtc,
    StartBacktestRequest Run, IReadOnlyList<BacktestCohort> Cohorts, int ExecutionCount,
    int MaximumAcceptedExecutions, string MembershipDescription, UniversePreviewResponse Source,
    string ConfigurationSha256, IReadOnlyList<string>? ExcludedSymbols = null,
    DecisionContextEvidenceSummary? EvidenceSummary = null,
    BacktestResolvedSettingsResponse? ResolvedSettings = null)
{
    /// <summary>Gives saved-plan pickers a human-readable identity without exposing the serialized record.</summary>
    public override string ToString() => $"{Run.RunName} · {Cohorts.Count} groups · {SavedAtUtc:u}";
}

public sealed record StartBacktestPlanRequest(string ContractVersion, string IdempotencyKey);

/// <summary>Exact server-resolved costs and policy displayed before saving and retained when a saved plan is reopened.</summary>
public sealed record BacktestResolvedSettingsResponse(string ConfigurationKey, string ConfigurationSha256,
    decimal FixedBuyFee, decimal FixedSellFee, decimal SecFeeRate, decimal FinraTafPerShare, decimal FinraTafCap,
    decimal MaxBarParticipationPct, decimal ImpactBpsAtMaxParticipation, string? ExecutionReplayPolicyId,
    DateTimeOffset? PrecommittedLiquidationSourceUtc, IReadOnlyList<BacktestStrategySettingsResponse> Strategies,
    string? CutoffPolicy = null, string Horizon = "swing", string? RequiredMarketDataContext = null);

public sealed record BacktestStrategySettingsResponse(StrategyReference Identity, string Name, decimal SlippageBps,
    StrategyReference? DerivedFromIdentity, TradingFlow.Contracts.Operator.StrategyAdmissionProfileResponse? AdmissionProfile,
    BacktestZoneSettingsResponse? Zone = null);
/// <summary>Resolved research choices; clients display these server facts without deriving policy defaults.</summary>
public sealed record BacktestZoneSettingsResponse(string VolumeMode, string TargetPolicy, decimal? FallbackNetRiskMultiple,
    decimal MinimumRelativeVolume, decimal SyntheticSpreadBps, string StopBasis, string PlanningCostAssumption);
public sealed record BacktestCohortExecution(long PlanId, string CohortName,
    IReadOnlyList<string> Symbols, decimal StartingCapital, StrategyReference Strategy);
public sealed record BacktestPlanStartResponse(long PlanId, string IdempotencyKey,
    IReadOnlyList<RunJobResponse> Jobs);

/// <summary>One strategy/cohort/month. RealizedNetProfit and Fees are monthly changes; UnrealizedProfit is the amount at month end, not its monthly change. Unavailable valuations remain null.</summary>
public sealed record MonthlyRunResult(StrategyReference Strategy, string CohortName, string Month,
    decimal? OpeningEquity, decimal? ClosingEquity, decimal? ReturnPct, int? ClosedTrades,
    string ValuationBasis, bool PartialMonth, string? UnavailableReason,
    decimal? MaxDrawdownPct = null, string? StrategyAlias = null, string? StrategyName = null,
    decimal? RealizedNetProfit = null, decimal? UnrealizedProfit = null, decimal? Fees = null,
    decimal? ClosingCash = null, decimal? ClosingPositionValue = null, decimal? GrossExposurePct = null,
    int? OpenPositions = null, int? FilledCandidates = null, int? FillCount = null);
