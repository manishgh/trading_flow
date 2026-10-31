namespace TradingFlow.Domain.Backtesting;

/// <summary>Points to an immutable comparison-wide data request captured with the admitted jobs.</summary>
public sealed record PreparedMarketDataRequestReference(string Path, string Sha256);

/// <summary>Declares the effective source queries and row budget of one independently funded book.</summary>
public sealed record PreparedMarketDataBook(string BookId, BacktestRunConfig Run,
    IReadOnlyList<TradingFlow.Domain.Strategies.StrategyDefinition>? Strategies = null);

/// <summary>Freezes every book's source requirements before the first comparison job collects data.</summary>
public sealed record PreparedMarketDataRequest(int SchemaVersion, string ComparisonId,
    DateTimeOffset EvaluationCutoffUtc, IReadOnlyList<PreparedMarketDataBook> Books);

/// <summary>Public evidence about the selected candles; contains no private storage paths or credentials.</summary>
public sealed record PreparedMarketDataSummary(string SnapshotId, string SnapshotSha256,
    DateTimeOffset PreparedAtUtc, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    IReadOnlyList<string> Providers, IReadOnlyList<string> DataFeeds, IReadOnlyList<string> PriceAdjustments,
    int ReusedQueryCount, int DownloadedQueryCount, string HistoricalEvidenceNote);
