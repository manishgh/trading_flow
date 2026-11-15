using TradingFlow.Domain.Strategies;

namespace TradingFlow.Domain.Persistence;

/// <summary>The exact archived quote receipt used by admission, where the broker provided one.</summary>
public sealed record AdmissionQuoteReceipt(string ArchiveId, string Sha256, DateTimeOffset ReceivedAtUtc);

/// <summary>The quote read at admission. Its observation and provider clocks have different meanings.</summary>
public sealed record AdmissionOrderQuote(decimal? Bid, decimal? Ask, string Feed,
    DateTimeOffset? ProviderTimestampUtc, DateTimeOffset ObservedAtUtc, int FreshnessLimitMilliseconds,
    AdmissionQuoteReceipt? Receipt);

/// <summary>A frozen trigger price and optional separately modeled executable price.</summary>
public sealed record OrderDecisionReference(decimal TriggerReferencePrice, DateTimeOffset TriggeredAtUtc,
    decimal? ModeledExecutablePrice = null);

/// <summary>Entry observations stored beside the first winning reservation.</summary>
public sealed record OrderAdmissionEvidence(AdmissionOrderQuote? Quote, OrderDecisionReference? Decision,
    StrategyArtifactIdentity? StrategyIdentity);

/// <summary>Immutable entry evidence keyed to its already-reserved intent.</summary>
public sealed class OrderAdmissionTelemetryRecord
{
    public Guid IntentId { get; set; }
    public decimal? Bid { get; set; }
    public decimal? Ask { get; set; }
    public string? QuoteFeed { get; set; }
    public DateTimeOffset? QuoteProviderTimestampUtc { get; set; }
    public DateTimeOffset? QuoteObservedAtUtc { get; set; }
    public int? QuoteFreshnessLimitMilliseconds { get; set; }
    public string? QuoteReceiptId { get; set; }
    public string? QuoteReceiptSha256 { get; set; }
    public DateTimeOffset? QuoteReceiptAtUtc { get; set; }
    public decimal? DecisionPrice { get; set; }
    public DateTimeOffset? DecisionAtUtc { get; set; }
    public decimal? ModeledExecutablePrice { get; set; }
    public string? StrategyArtifactId { get; set; }
    public string? StrategyVersion { get; set; }
    public string? StrategyContentSha256 { get; set; }
}

/// <summary>One lease-fenced local broker attempt. The intent supplies its client, run and account identities.</summary>
public sealed class OrderDispatchAttemptTelemetryRecord
{
    public Guid IntentId { get; set; }
    public int AttemptNumber { get; set; }
    public DateTimeOffset PrePostAtUtc { get; set; }
}
