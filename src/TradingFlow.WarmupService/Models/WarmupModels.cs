using TradingFlow.Domain.Market;

namespace TradingFlow.WarmupService.Models;

public sealed class WarmupOptions
{
    public string DataRoot { get; init; } = "data/warmup";

    public string CacheRoot { get; init; } = "data/cache/warmup";

    public string ArchiveRoot { get; init; } = "data/warmup/archive";

    public string ProviderName { get; init; } = "alpaca";

    public string MarketDataFeed { get; init; } = "sip";

    public int DefaultWarmupDays { get; init; } = 60;

    public int DefaultNewsLookbackDays { get; init; } = 14;

    public string[] DefaultTimeframes { get; init; } = ["1m", "5m", "15m", "1h", "1d"];

    public bool IncludeNewsByDefault { get; init; } = true;

    public int MaxParallelTickers { get; init; } = 4;

    public string NightlyRunLocalTime { get; init; } = "20:30";

    public string MarketTimeZone { get; init; } = "America/New_York";

    public bool RunNightlyScheduler { get; init; } = true;

    public string BlobContainerSasUrl { get; init; } = "";

    public string BlobPrefix { get; init; } = "warmup";
}

public sealed record WarmupWatchRequest(
    IReadOnlyCollection<string> Tickers,
    string? Reason = null,
    string? RequestedBy = null,
    int? WarmupDays = null,
    int? NewsLookbackDays = null,
    IReadOnlyCollection<string>? Timeframes = null,
    bool? IncludeNews = null,
    bool RunNow = false);

public sealed record WarmupRunNowRequest(
    IReadOnlyCollection<string>? Tickers = null,
    string? Reason = null);

public sealed record WarmupTickerIntent(
    string Ticker,
    string Reason,
    string RequestedBy,
    DateTimeOffset RequestedAtUtc,
    int WarmupDays,
    int NewsLookbackDays,
    IReadOnlyList<string> Timeframes,
    bool IncludeNews,
    bool Active,
    DateTimeOffset? LastWarmedAtUtc = null,
    string LastStatus = "pending",
    string? LastError = null);

public sealed record WarmupJobRequest(
    string RunId,
    string Reason,
    IReadOnlyCollection<string>? Tickers,
    DateTimeOffset QueuedAtUtc);

public sealed record WarmupRunRecord(
    string RunId,
    string Reason,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string Status,
    int RequestedTickerCount,
    int SucceededTickerCount,
    int FailedTickerCount,
    IReadOnlyList<WarmupTickerResult> Tickers);

public sealed record WarmupTickerResult(
    string Ticker,
    bool Succeeded,
    int BarCount,
    int CatalystCount,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    IReadOnlyList<string> Timeframes,
    IReadOnlyList<string> ArtifactPaths,
    string? Error);

public sealed record WarmupArtifacts(
    string Ticker,
    IReadOnlyList<string> Paths);

public sealed record WarmupTickerPayload(
    WarmupTickerIntent Intent,
    IReadOnlyList<OhlcvBar> Bars,
    IReadOnlyList<CatalystEvent> Catalysts,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);
