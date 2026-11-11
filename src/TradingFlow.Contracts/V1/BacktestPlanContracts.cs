namespace TradingFlow.Contracts.V1;

/// <summary>An operator-owned stock source or a published research screen; reading choices never copies members into another list.</summary>
public sealed record BacktestStockSource(string Key, string Name, string Kind, Guid? WishlistId, string? Query)
{
    /// <summary>The group a source belongs to in pickers: My wishlists, Finviz lists or Research screens.</summary>
    public static string GroupName(string kind) => kind switch
    {
        "wishlist" => "My wishlists",
        "research_screen" => "Research screens",
        _ => "Finviz lists"
    };

    /// <summary>Shows the source with its group in native pickers.</summary>
    public override string ToString() => $"{GroupName(Kind)} · {Name}";
}

/// <summary>
/// The frozen inputs of a research-screen plan: the screen, its manifest and ranked-list SHA-256, and the evidence-catalog
/// datasets its runs read (daily as-traded bars and XNYS sessions named by the manifest; five-minute and one-minute bars
/// collected for this ranked list). Starting the plan refuses any difference.
/// </summary>
public sealed record ResearchScreenReference(string ScreenName, string ManifestSha256, string RankedListSha256,
    string DailyBarsDatasetId, string ExchangeSessionsDatasetId, IReadOnlyList<string> FiveMinuteDatasetIds,
    IReadOnlyList<string> OneMinuteDatasetIds)
{
    /// <summary>True when both references name the same screen fingerprints and the same datasets.</summary>
    public bool SameInputs(ResearchScreenReference other) =>
        ScreenName == other.ScreenName && ManifestSha256 == other.ManifestSha256 && RankedListSha256 == other.RankedListSha256 &&
        DailyBarsDatasetId == other.DailyBarsDatasetId && ExchangeSessionsDatasetId == other.ExchangeSessionsDatasetId &&
        FiveMinuteDatasetIds.SequenceEqual(other.FiveMinuteDatasetIds) && OneMinuteDatasetIds.SequenceEqual(other.OneMinuteDatasetIds);
}

/// <summary>A research screen's frozen inputs and its pool: the stocks in its ranked list on at least one session of the plan window.</summary>
public sealed record ResearchScreenPool(ResearchScreenReference Reference, IReadOnlyList<string> Symbols);

/// <summary>A named stock group with its own starting capital for each selected strategy.</summary>
public sealed record BacktestCohort(string Name, IReadOnlyList<string> Symbols, decimal StartingCapital);

/// <summary>Saving this request stores choices only; it never starts execution.</summary>
public sealed record SaveBacktestPlanRequest(StartBacktestRequest Run, int? GroupSize,
    IReadOnlyList<BacktestCohort>? Cohorts = null, IReadOnlyList<string>? ExcludedSymbols = null,
    string? GroupOrder = null, int? GroupSeed = null);

/// <summary>
/// Per-day stock eligibility judged only from completed earlier sessions; a stock may trade only on days it passes.
/// PriceBasis is prior_close or session_open. Null thresholds are not applied. Ranking, when present, keeps only each
/// session's highest-ranked eligible stocks.
/// </summary>
public sealed record BacktestEligibilityRules(decimal MinPrice, string PriceBasis, int AverageSessions, int MinHistorySessions,
    decimal? MinAverageVolume = null, decimal? MinAverageDollarVolume = null, int? AtrPeriod = null, decimal? MinAtr = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    BacktestRankingRules? Ranking = null);

/// <summary>
/// Per-session ranking of the eligible stocks. Measure is first_five_minute_relative_volume (09:30-09:35 New York volume
/// over the average of the previous BaselineSessions sessions); BaselineAverage is mean or median; stocks below
/// MinRelativeVolume (1 = 100%) are dropped and the MaxPerSession highest are kept, ties to the lower issuer identifier.
/// </summary>
public sealed record BacktestRankingRules(string Measure, int BaselineSessions, string BaselineAverage,
    decimal MinRelativeVolume, int MaxPerSession);

/// <summary>An immutable saved choice; re-saving edited choices creates another explicit plan.</summary>
public sealed record BacktestPlanResponse(long PlanId, DateTimeOffset SavedAtUtc,
    StartBacktestRequest Run, IReadOnlyList<BacktestCohort> Cohorts, int ExecutionCount,
    int MaximumAcceptedExecutions, string MembershipDescription, UniversePreviewResponse Source,
    string ConfigurationSha256, IReadOnlyList<string>? ExcludedSymbols = null,
    DecisionContextEvidenceSummary? EvidenceSummary = null,
    BacktestResolvedSettingsResponse? ResolvedSettings = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? GroupOrder = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? GroupSeed = null,
    // The frozen ranked list and datasets of a research-screen plan; absent for wishlist and Finviz plans.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ResearchScreenReference? ResearchScreen = null)
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
    StrategyReference? DerivedFromIdentity, TradingFlow.Contracts.Operator.StrategyAdmissionProfileResponse? AdmissionProfile);
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
