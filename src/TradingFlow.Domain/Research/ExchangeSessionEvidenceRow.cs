namespace TradingFlow.Domain.Research;

/// <summary>
/// Immutable point-in-time US equity exchange-session evidence. Every bound is explicit UTC
/// evidence; consumers must not reconstruct historical sessions from a current timezone rule.
/// </summary>
public sealed record ExchangeSessionEvidenceRow : INormalizedEvidenceRow
{
    public ExchangeSessionEvidenceRow(
        int schemaVersion,
        string runId,
        string configHash,
        string codeVersion,
        string dataFeed,
        string provider,
        string exchange,
        DateOnly tradeDate,
        DateTimeOffset premarketOpenUtc,
        DateTimeOffset regularOpenUtc,
        DateTimeOffset regularCloseUtc,
        DateTimeOffset postmarketCloseUtc,
        bool isEarlyClose,
        string calendarSource,
        DateTimeOffset availableAtUtc,
        IReadOnlyList<EvidenceRowSourceAddress> sources)
    {
        SchemaVersion = EvidenceRowContract.NormalizeSchemaVersion(schemaVersion);
        RunId = EvidenceRowContract.NormalizeRequired(runId, nameof(runId));
        ConfigHash = EvidenceValue.NormalizeSha256(configHash);
        CodeVersion = EvidenceRowContract.NormalizeRequired(codeVersion, nameof(codeVersion));
        DataFeed = EvidenceRowContract.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        Provider = EvidenceRowContract.NormalizeRequired(provider, nameof(provider)).ToLowerInvariant();
        Exchange = EvidenceRowContract.NormalizeRequired(exchange, nameof(exchange)).ToUpperInvariant();
        TradeDate = tradeDate;
        PremarketOpenUtc = EvidenceRowContract.RequireUtc(premarketOpenUtc, nameof(premarketOpenUtc));
        RegularOpenUtc = EvidenceRowContract.RequireUtc(regularOpenUtc, nameof(regularOpenUtc));
        RegularCloseUtc = EvidenceRowContract.RequireUtc(regularCloseUtc, nameof(regularCloseUtc));
        PostmarketCloseUtc = EvidenceRowContract.RequireUtc(postmarketCloseUtc, nameof(postmarketCloseUtc));
        if (!(PremarketOpenUtc < RegularOpenUtc &&
              RegularOpenUtc < RegularCloseUtc &&
              RegularCloseUtc < PostmarketCloseUtc))
        {
            throw new ArgumentException(
                "Exchange-session bounds must be strictly ordered from premarket through postmarket.");
        }

        IsEarlyClose = isEarlyClose;
        CalendarSource = EvidenceRowContract.NormalizeRequired(
            calendarSource,
            nameof(calendarSource));
        AvailableAtUtc = EvidenceRowContract.RequireUtc(availableAtUtc, nameof(availableAtUtc));
        Sources = EvidenceRowContract.CopySources(sources);
    }

    public int SchemaVersion { get; }
    public string RunId { get; }
    public string ConfigHash { get; }
    public string CodeVersion { get; }
    public string DataFeed { get; }
    public string Provider { get; }
    public string Exchange { get; }
    public DateOnly TradeDate { get; }
    public DateTimeOffset PremarketOpenUtc { get; }
    public DateTimeOffset RegularOpenUtc { get; }
    public DateTimeOffset RegularCloseUtc { get; }
    public DateTimeOffset PostmarketCloseUtc { get; }
    public bool IsEarlyClose { get; }
    public string CalendarSource { get; }
    public DateTimeOffset AvailableAtUtc { get; }
    public IReadOnlyList<EvidenceRowSourceAddress> Sources { get; }
    public DateTimeOffset SourceTimestampUtc => AvailableAtUtc;
}
