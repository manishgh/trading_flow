namespace TradingFlow.Data.Csv;

/// <summary>Immutable selected source files; transport paths are private runner evidence, never semantic strategy identity.</summary>
public sealed record PreparedMarketDataVersion(string RequestSha256, string ManifestPath, string ManifestSha256,
    string DataPath, string ContentSha256, long RowCount, string ProviderIdentity, string DataFeed,
    string AdjustmentPolicy, string Ticker, string Timeframe, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    DateTimeOffset CollectedAtUtc, bool Reused, bool IsCalendar, bool OmittedSubDailyIntervalsMeanNoQualifyingTrades)
{
    /// <summary>The original provider request when this reference admits only a slice of its immutable parent files.</summary>
    public string? SourceRequestSha256 { get; init; }
}

/// <summary>All selected payloads and receipts are verified and leased by the comparison reader.</summary>
public sealed record PreparedMarketDataEvidenceFile(string Path, long Length, string Sha256);
