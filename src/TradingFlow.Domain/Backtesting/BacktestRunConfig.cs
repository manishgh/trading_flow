using TradingFlow.Domain.Market;

namespace TradingFlow.Domain.Backtesting;

public sealed record BacktestRunConfig(
    string RunName,
    string Mode,
    EngineConfig Engine,
    TimeWindowConfig TimeWindow,
    IReadOnlyList<string> Tickers,
    string Provider,
    IReadOnlyList<string> Intervals,
    string RawRoot,
    string NormalizedRoot,
    string ResultsRoot,
    string CachePolicy,
    DerivedTimeframeConfig DerivedTimeframes,
    ValidationConfig Validation,
    ProviderConfig Providers,
    PortfolioConfig Portfolio,
    SignalSourceConfig SignalSource,
    ExecutionConfig Execution,
    NewsConfig News,
    ScreenerConfig Screener,
    ArtifactRetentionConfig Artifacts,
    IReadOnlyList<string> Strategies,
    UniverseConfig? Universe = null,
    /// <summary>
    /// How the desk orders the resolved candidate universe. Read from
    /// <c>universe.rank</c> so backtest, paper and live all take the ranking
    /// through one config path rather than each carrying its own weights.
    /// </summary>
    UniverseRankConfig? Rank = null,
    DiscoveryRuntimeConfig? Discovery = null);

/// <summary>
/// Runtime discovery policy embedded in the immutable run artifact. Initial
/// symbols preserve run-start evidence while wishlist and Finviz sources may be
/// refreshed durably without rewriting the artifact.
/// </summary>
public sealed record DiscoveryRuntimeConfig(
    bool Enabled,
    int RefreshSeconds,
    int SourceExpirySeconds,
    Guid? WishlistId,
    IReadOnlyList<string> WishlistTickers,
    string? FinvizQuery,
    IReadOnlyList<string> FinvizTickers,
    IReadOnlyList<string> OperatorTickers,
    IReadOnlyList<string> AlertTickers,
    IReadOnlyList<string> NewsTickers,
    IReadOnlyList<string> EarningsTickers)
{
    public const int DefaultRefreshSeconds = 60;
    public const int DefaultSourceExpirySeconds = 180;

    public static DiscoveryRuntimeConfig Disabled { get; } = new(
        false,
        DefaultRefreshSeconds,
        DefaultSourceExpirySeconds,
        null,
        [],
        null,
        [],
        [],
        [],
        [],
        []);
}

/// <summary>
/// Controls how the tradable ticker universe is chosen for a run. "static" keeps the
/// hand-listed <c>tickers:</c> (legacy behavior). "historical_screener" selects from a
/// candidate pool using only data available strictly before the evaluation window starts,
/// which avoids within-window screening look-ahead but does not remove hindsight from a present-day candidate pool. See
/// See the point-in-time universe boundary in docs/operating-boundaries.md.
/// </summary>
public sealed record UniverseConfig(
    string Mode,
    IReadOnlyList<string> Candidates,
    decimal MinPrice,
    decimal MinAvgDollarVolume,
    int LookbackDays,
    decimal? MinPriorReturnPct,
    int? MaxSymbols,
    string CandidateSource = "static",
    string? CandidateScreenerQuery = null,
    string RescreenFrequency = "per_run",
    string ResolvedSource = "",
    IReadOnlyList<string>? SelectedSourceTickers = null,
    IReadOnlyList<string>? ScreenerTickers = null,
    string? ResolvedScreenerQuery = null,
    DateTimeOffset? ResolvedAtUtc = null,
    DailyScreenRules? DailyEligibility = null,
    // Optional per-session ranking of the daily-eligible stocks (stocks in play); omitted from settings when unset.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DailyRankingRules? DailyRanking = null,
    // Per-session members read from a published research screen's ranked list instead of being recomputed in the run.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] RankedMembershipReference? RankedMembership = null)
{
    /// <summary>Completed daily sessions the screen reads before a decision day: the eligibility rules' needs, or the legacy look-back.</summary>
    public int ScreenLookbackSessions => DailyEligibility?.PriorSessionsNeeded ?? Math.Max(LookbackDays, 1);

    /// <summary>Calendar days fetched before the first decision day so at least <see cref="ScreenLookbackSessions"/> sessions exist.</summary>
    public int ScreenFetchCalendarDays => checked(ScreenLookbackSessions * 3 + 15);

    public const string RescreenPerRun = "per_run";
    public const string RescreenPerDay = "per_day";

    // Per-day re-screens the universe for every trading day (a ticker is only tradable on days
    // it qualifies). Per-run (default) screens once at the evaluation start.
    public bool IsPerDayRescreen => RescreenFrequency.Equals(RescreenPerDay, StringComparison.OrdinalIgnoreCase);

    public const string StaticMode = "static";
    public const string HistoricalScreenerMode = "historical_screener";
    public const string UnresolvedWishlistMode = "unresolved_wishlist";
    public const string ResolvedSnapshotMode = "resolved_snapshot";
    public const string StaticCandidateSource = "static";
    public const string FinvizCandidateSource = "finviz";
    public const string BothCandidateSource = "both";

    public bool IsHistoricalScreener => Mode.Equals(HistoricalScreenerMode, StringComparison.OrdinalIgnoreCase);

    public bool IsUnresolvedWishlist => Mode.Equals(UnresolvedWishlistMode, StringComparison.OrdinalIgnoreCase);

    public bool IsResolvedSnapshot => Mode.Equals(ResolvedSnapshotMode, StringComparison.OrdinalIgnoreCase);

    // "finviz" pulls the pool from a screener; "both" unions the curated candidates list
    // with the screener pool (matches a UI/API that can send either or both).
    public bool UsesFinvizScreen =>
        CandidateSource.Equals(FinvizCandidateSource, StringComparison.OrdinalIgnoreCase) ||
        CandidateSource.Equals(BothCandidateSource, StringComparison.OrdinalIgnoreCase);

    public bool MergesCuratedAndFinviz =>
        CandidateSource.Equals(BothCandidateSource, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> ResolvedSelectedSourceTickers => SelectedSourceTickers ?? [];

    public IReadOnlyList<string> ResolvedScreenerTickers => ScreenerTickers ?? [];

    public static UniverseConfig StaticUniverse { get; } =
        new(StaticMode, Array.Empty<string>(), 0m, 0m, 20, null, null);
}

/// <summary>
/// universe.ranked_membership: the screen-manifest.json of a published research screen and its SHA-256. The run reads each
/// session's members from the screen's ranked list (known at 09:35 New York) and refuses a manifest whose bytes changed.
/// </summary>
public sealed record RankedMembershipReference(string ManifestPath, string ManifestSha256);

public sealed record TimeWindowConfig(
    string Type,
    int LookbackDays,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    int WarmupLookbackDays = 0);

public sealed record EngineConfig(
    string Pipeline,
    int WorkerCount,
    int BoundedCapacity,
    int IndicatorWarmupBars,
    int TickerTimeoutSeconds,
    bool FailFast,
    int MaxRetainedReplayBars = 1_000_000,
    int MaxStrategyWorkItems = 10_000,
    int MaxRetainedCandidates = 1_000_000,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool AllowLateWarmup = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? PrecommittedLiquidationSourceUtc = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ExecutionReplayPolicyId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? EvidenceManifestPath = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? EvidenceResearchRoot = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ExecutionSourceManifestPath = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ExecutionSourceManifestSha256 = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? EvidenceManifestSha256 = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? EvidenceDependencyGraphSha256 = null);

public sealed record DerivedTimeframeConfig(string Source);

public sealed record ValidationConfig(
    OutOfSampleConfig OutOfSample,
    WalkForwardConfig WalkForward,
    BenchmarkConfig Benchmark,
    DataQualityConfig DataQuality,
    BiasRiskConfig BiasRisk);

public sealed record OutOfSampleConfig(
    bool Enabled,
    decimal Percent);

public sealed record WalkForwardConfig(
    bool Enabled,
    int WindowDays,
    int StepDays);

public sealed record BenchmarkConfig(
    bool Enabled,
    string Ticker);

public sealed record DataQualityConfig(
    bool Enabled,
    int MaxDuplicateBars,
    int MaxInvalidOhlcBars,
    decimal MaxZeroVolumePct);

public sealed record BiasRiskConfig(
    string UniverseSource,
    DateOnly? UniverseAsOfDate,
    string PriceAdjustmentPolicy);

public sealed record ExecutionConfig(
    string Router,
    string Broker,
    bool DryRun,
    bool AllowLiveOrders,
    string OrderType,
    string OrderExpiration,
    string EntryOrderType,
    bool AllowExtendedHoursTrading = false);

public sealed record ProviderConfig(
    AlpacaProviderConfig Alpaca,
    // Frozen evidence-catalog datasets read by market_data.provider evidence_catalog; omitted from settings when unused.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    EvidenceCatalogProviderConfig? EvidenceCatalog = null);

/// <summary>
/// providers.evidence_catalog: the immutable evidence catalog and the exact dataset IDs a research-screen run reads. Daily
/// as-traded bars and XNYS exchange sessions come from the screen manifest; the five-minute and one-minute datasets are the
/// ones whose collection attribute ranked_list_sha256 equals <see cref="RankedListSha256"/>. Nothing else is ever read.
/// </summary>
public sealed record EvidenceCatalogProviderConfig(
    string CatalogPath,
    string ArtifactRoot,
    string RankedListSha256,
    string DailyDatasetId,
    string ExchangeSessionsDatasetId,
    IReadOnlyList<string> FiveMinuteDatasetIds,
    IReadOnlyList<string> OneMinuteDatasetIds,
    string? CorporateActionsDatasetId = null);

public sealed record AlpacaProviderConfig(
    string DataFeed);

public sealed record PortfolioConfig(
    decimal StartingCapital,
    decimal AccountRiskBudgetPct,
    decimal MaxPositionNotionalPct,
    int MaxConcurrentPositions,
    decimal FixedBuyFee,
    decimal FixedSellFee,
    int MaxOpenTradesPerTicker,
    bool PreventOverlappingTickerPositions,
    // Execution realism and bounded execution-history controls.
    decimal MaxBarParticipationPct = 0m,
    decimal SecFeeRate = 0m,
    decimal FinraTafPerShare = 0m,
    decimal FinraTafCap = 0m,
    decimal ImpactBpsAtMaxParticipation = 0m,
    int MaxExecutionOrderHistory = 1_000_000,
    // Broker commission in USD per filled share on both sides (the paper's $0.0035); zero charges none.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] decimal CommissionPerShare = 0m,
    // Gross exposure limit in percent of equity (400 = 4x leverage). When set, untriggered stop orders reserve no capital
    // and each position is capped at its trigger so total gross exposure stays within it. Null keeps the cash-only limit.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MaxGrossExposurePct = null);

public sealed record SignalSourceConfig(
    string Type,
    bool RequireSignature,
    string SecretEnv,
    int DedupeWindowSeconds,
    int RejectStaleAfterSeconds);

public sealed record NewsConfig(
    bool Enabled,
    string ProviderName,
    int VetoTtlMinutes,
    decimal VetoNegativeThreshold,
    int MaxArticlesPerTicker = 120,
    int SentimentTimeoutSeconds = 3);

public sealed record ScreenerConfig(
    bool Enabled,
    string Provider,
    IReadOnlyList<string> Filters);

public sealed record ArtifactRetentionConfig(
    string RetentionMode);

public sealed record ScreenerEntry(
    string Name,
    string Url);
