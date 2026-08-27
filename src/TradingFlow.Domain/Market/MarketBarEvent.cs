namespace TradingFlow.Domain.Market;

public enum MarketBarEventKind
{
    CompletedBar,
    ProviderRevision,
    Replay
}

/// <summary>
/// Canonical completed-bar event shared by live ingestion and historical replay.
/// Provider revisions are explicit so ordinary out-of-order data cannot rewrite
/// completed trading history.
/// </summary>
public sealed record MarketBarEvent(
    string Provider,
    string Feed,
    OhlcvBar Bar,
    MarketBarEventKind Kind,
    DateTimeOffset ObservedAtUtc,
    long FencingToken)
{
    public string ContentSha256 => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(String.Join(
                    '|',
                    Bar.Ticker.Trim().ToUpperInvariant(),
                    Bar.Timeframe.Trim().ToLowerInvariant(),
                    Bar.Timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    Bar.Open.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Bar.High.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Bar.Low.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Bar.Close.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Bar.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture)))))
        .ToLowerInvariant();
}

public enum MarketBarDisposition
{
    Accepted,
    Duplicate,
    RevisionAccepted,
    RevisionOutsideWindow,
    OutOfOrder,
    ConflictingDuplicate,
    StaleLease,
    BackpressureRejected,
    GapDetected,
    Invalid
}

public sealed record MarketBarApplyResult(
    MarketBarDisposition Disposition,
    string Symbol,
    DateTimeOffset BarTimestampUtc,
    string Detail);

public sealed record CompletedMarketState(
    string Symbol,
    long FencingToken,
    IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> BarsByTimeframe,
    string Fingerprint);
