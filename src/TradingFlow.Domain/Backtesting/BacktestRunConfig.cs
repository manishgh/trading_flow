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
    IReadOnlyList<string> Strategies);

public sealed record TimeWindowConfig(
    string Type,
    int LookbackDays,
    DateTimeOffset? Start,
    DateTimeOffset? End);

public sealed record EngineConfig(
    string Pipeline,
    int WorkerCount,
    int BoundedCapacity,
    int IndicatorWarmupBars,
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
    YahooProviderConfig Yahoo,
    AlpacaProviderConfig Alpaca,
    TradingViewProviderConfig TradingView);

public sealed record AlpacaProviderConfig(
    string DataFeed);

public sealed record YahooProviderConfig(
    string BaseUrl,
    YahooHeaderConfig Headers,
    int RequestTimeoutSeconds,
    int MaxRetries,
    int ThrottleMs);

public sealed record YahooHeaderConfig(
    string UserAgent,
    string Accept,
    string AcceptLanguage);

public sealed record TradingViewProviderConfig(bool Enabled);

public sealed record PortfolioConfig(
    decimal StartingCapital,
    decimal RiskPerTradePct,
    decimal MaxPositionValuePct,
    int MaxConcurrentPositions,
    decimal FixedBuyFee,
    decimal FixedSellFee,
    int MaxOpenTradesPerTicker,
    bool PreventOverlappingTickerPositions);

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
    decimal VetoNegativeThreshold);

public sealed record ScreenerConfig(
    bool Enabled,
    string Provider,
    IReadOnlyList<string> Filters);

public sealed record ScreenerEntry(
    string Name,
    string Url);
