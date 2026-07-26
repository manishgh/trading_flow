namespace TradingFlow.Domain.Research;

/// <summary>
/// Point-in-time SIP quote used to determine whether a historical signal was executable.
/// Nullable sides preserve incomplete provider observations so quality checks can reject
/// them explicitly instead of silently removing them during normalization.
/// </summary>
public sealed record SipQuoteEvidenceRow : INormalizedEvidenceRow
{
    public SipQuoteEvidenceRow(
        int schemaVersion,
        string runId,
        string configHash,
        string codeVersion,
        string securityId,
        string symbol,
        DateTimeOffset quoteTimestampUtc,
        long? bidPriceUnits,
        long? askPriceUnits,
        long? bidSize,
        long? askSize,
        string bidExchange,
        string askExchange,
        string dataFeed,
        string currency,
        IReadOnlyList<string>? conditions,
        DateTimeOffset providerTimestampUtc,
        DateTimeOffset receivedAtUtc,
        IReadOnlyList<EvidenceRowSourceAddress> sources)
    {
        SchemaVersion = EvidenceRowContract.NormalizeSchemaVersion(schemaVersion);
        RunId = EvidenceRowContract.NormalizeRequired(runId, nameof(runId));
        ConfigHash = EvidenceValue.NormalizeSha256(configHash);
        CodeVersion = EvidenceRowContract.NormalizeRequired(codeVersion, nameof(codeVersion));
        SecurityId = EvidenceRowContract.NormalizeRequired(securityId, nameof(securityId));
        Symbol = EvidenceRowContract.NormalizeRequired(symbol, nameof(symbol)).ToUpperInvariant();
        QuoteTimestampUtc = EvidenceRowContract.RequireUtc(quoteTimestampUtc, nameof(quoteTimestampUtc));

        if (bidPriceUnits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bidPriceUnits));
        }

        if (askPriceUnits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(askPriceUnits));
        }

        if (bidSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bidSize));
        }

        if (askSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(askSize));
        }

        BidPriceUnits = bidPriceUnits;
        AskPriceUnits = askPriceUnits;
        BidSize = bidSize;
        AskSize = askSize;
        BidExchange = NormalizeOptionalCode(bidExchange);
        AskExchange = NormalizeOptionalCode(askExchange);
        DataFeed = EvidenceRowContract.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        Currency = EvidenceRowContract.NormalizeRequired(currency, nameof(currency)).ToUpperInvariant();
        Conditions = EvidenceRowContract.CopyNormalizedStrings(conditions);
        ProviderTimestampUtc = EvidenceRowContract.RequireUtc(
            providerTimestampUtc,
            nameof(providerTimestampUtc));
        ReceivedAtUtc = EvidenceRowContract.RequireUtc(receivedAtUtc, nameof(receivedAtUtc));

        if (ProviderTimestampUtc != QuoteTimestampUtc)
        {
            throw new ArgumentException(
                "Quote and provider timestamps must identify the same market observation.");
        }

        if (ReceivedAtUtc < ProviderTimestampUtc)
        {
            throw new ArgumentException("Received timestamp cannot precede the provider timestamp.");
        }

        Sources = EvidenceRowContract.CopySources(sources);
    }

    public int SchemaVersion { get; }
    public string RunId { get; }
    public string ConfigHash { get; }
    public string CodeVersion { get; }
    public string SecurityId { get; }
    public string Symbol { get; }
    public DateTimeOffset QuoteTimestampUtc { get; }
    public long? BidPriceUnits { get; }
    public long? AskPriceUnits { get; }
    public long? BidSize { get; }
    public long? AskSize { get; }
    public string? BidExchange { get; }
    public string? AskExchange { get; }
    public string DataFeed { get; }
    public string Currency { get; }
    public IReadOnlyList<string> Conditions { get; }
    public DateTimeOffset ProviderTimestampUtc { get; }
    public DateTimeOffset ReceivedAtUtc { get; }
    public IReadOnlyList<EvidenceRowSourceAddress> Sources { get; }
    public DateTimeOffset SourceTimestampUtc => ProviderTimestampUtc;

    private static string? NormalizeOptionalCode(string? value) =>
        String.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}
