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
    UniverseConfig? Universe = null);

/// <summary>
/// Controls how the tradable ticker universe is chosen for a run. "static" keeps the
/// hand-listed <c>tickers:</c> (legacy behavior). "historical_screener" selects from a
/// candidate pool using only data available strictly before the evaluation window starts,
/// which removes hindsight survivorship from the universe. See
/// docs/edge-recovery-master-plan.md phase 0.1.
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
    string RescreenFrequency = "per_run")
{
    public const string RescreenPerRun = "per_run";
    public const string RescreenPerDay = "per_day";

    // Per-day re-screens the universe for every trading day (a ticker is only tradable on days
    // it qualifies). Per-run (default) screens once at the evaluation start.
    public bool IsPerDayRescreen => RescreenFrequency.Equals(RescreenPerDay, StringComparison.OrdinalIgnoreCase);

    public const string StaticMode = "static";
    public const string HistoricalScreenerMode = "historical_screener";
    public const string StaticCandidateSource = "static";
    public const string FinvizCandidateSource = "finviz";
    public const string BothCandidateSource = "both";

    public bool IsHistoricalScreener => Mode.Equals(HistoricalScreenerMode, StringComparison.OrdinalIgnoreCase);

    // "finviz" pulls the pool from a screener; "both" unions the curated candidates list
    // with the screener pool (matches a UI/API that can send either or both).
    public bool UsesFinvizScreen =>
        CandidateSource.Equals(FinvizCandidateSource, StringComparison.OrdinalIgnoreCase) ||
        CandidateSource.Equals(BothCandidateSource, StringComparison.OrdinalIgnoreCase);

    public bool MergesCuratedAndFinviz =>
        CandidateSource.Equals(BothCandidateSource, StringComparison.OrdinalIgnoreCase);

    public static UniverseConfig StaticUniverse { get; } =
        new(StaticMode, Array.Empty<string>(), 0m, 0m, 20, null, null);
}

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
    bool FailFast);

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
    bool ExtendedHours = false);

public sealed record ProviderConfig(
    AlpacaProviderConfig Alpaca);

public sealed record AlpacaProviderConfig(
    string DataFeed);

public sealed record PortfolioConfig(
    decimal StartingCapital,
    decimal RiskPerTradePct,
    decimal MaxPositionValuePct,
    int MaxConcurrentPositions,
    decimal FixedBuyFee,
    decimal FixedSellFee,
    int MaxOpenTradesPerTicker,
    bool PreventOverlappingTickerPositions,
    // Execution realism (all default to 0 = disabled, preserving legacy results):
    decimal MaxBarParticipationPct = 0m,
    decimal SecFeeRate = 0m,
    decimal FinraTafPerShare = 0m,
    decimal FinraTafCap = 0m);

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
