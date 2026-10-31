namespace TradingFlow.Contracts.V1;

public sealed record WarmupQueueStatus(string RunId, string Reason, DateTimeOffset QueuedAtUtc,
    string Status, IReadOnlyList<string> Tickers, string? Error);

public sealed record WarmupArchiveStatus(string Ticker, bool CacheCompleted, string ArchiveStatus, string? Error);

public sealed record WarmupArchiveRetryResponse(string RunId, IReadOnlyList<WarmupArchiveStatus> Tickers);
