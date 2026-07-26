using System;

namespace TradingFlow.Domain.Market;

public enum CatalystType
{
    NewsReport,
    EarningsRelease,
    AnalystUpgrade,
    AnalystDowngrade,
    MergerAnnouncement,
    InsiderBuying,
    DividendAnnouncement,
    ProductLaunch,
    RegulatoryFiling
}

public sealed record CatalystEvent(
    string Ticker,
    DateTimeOffset Timestamp,
    CatalystType Type,
    string Headline,
    decimal SentimentScore, // -1.0 (Very Bearish) to 1.0 (Very Bullish)
    string? Provider = null,
    string? ExternalId = null,
    string? Summary = null,
    string? Source = null,
    string? Url = null,
    DateTimeOffset? ReceivedAt = null,
    DateTimeOffset? UpdatedAt = null,
    string? AvailabilityEvidence = null,
    DateTimeOffset? DecisionAvailableAt = null,
    string? DecisionAvailabilityEvidence = null
);

public static class CatalystAvailabilityEvidence
{
    public const string ProviderTimestampOnly = "provider_timestamp_only";
    public const string ProviderUpdatedTimestampOnly = "provider_updated_timestamp_only";
    public const string ObservedReceiptTime = "observed_receipt_time";
    public const string NewsAndAssessmentObservedTime =
        "news_and_assessment_observed_time";
    public const string ProviderTimestampAndAssessmentObservedTime =
        "provider_timestamp_and_assessment_observed_time";
}
