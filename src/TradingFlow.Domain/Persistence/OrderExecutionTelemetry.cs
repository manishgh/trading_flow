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
    decimal? ModeledExecutablePrice = null)
{
    /// <summary>Source-bound submission window recorded before reserving the entry.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public OrderEntryWindowEvidence? EntryWindow { get; init; }
}

/// <summary>Distinct source, receipt/availability, confirmation and economic-open clocks for a bounded entry.</summary>
public sealed record OrderEntryWindowEvidence(string Policy, int Version, int GraceSeconds,
    DateTimeOffset SetupSourceUtc, DateTimeOffset SetupAvailableAtUtc, DateTimeOffset? ConfirmationAtUtc,
    DateTimeOffset IntendedOpenUtc, DateTimeOffset ExclusiveEndUtc)
{
    // Unknown or malformed persisted evidence cannot supply a policy label or a guessed execution clock.
    public static OrderEntryWindowEvidence? Read(string? json)
    {
        if (json is not { Length: > 0 and <= 4096 }) return null;
        try
        {
            var value = System.Text.Json.JsonSerializer.Deserialize<OrderEntryWindowEvidence>(json);
            return value is { Policy: EntryWindowRules.NextRegularOpen, Version: 1, GraceSeconds: > 0 and <= 86400 } &&
                value.SetupSourceUtc <= value.SetupAvailableAtUtc && value.SetupSourceUtc < value.IntendedOpenUtc &&
                value.IntendedOpenUtc < value.ExclusiveEndUtc &&
                value.ExclusiveEndUtc - value.IntendedOpenUtc <= TimeSpan.FromSeconds(value.GraceSeconds)
                ? value : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}

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
    public string? EntryWindowJson { get; set; }
}

/// <summary>One lease-fenced local broker attempt. The intent supplies its client, run and account identities.</summary>
public sealed class OrderDispatchAttemptTelemetryRecord
{
    public Guid IntentId { get; set; }
    public int AttemptNumber { get; set; }
    public DateTimeOffset PrePostAtUtc { get; set; }
}
