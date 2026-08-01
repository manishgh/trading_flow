namespace TradingFlow.Domain.Earnings;

/// <summary>
/// Point-in-time earnings schedule and result data as observed from a provider.
/// All timestamps are UTC; local/exchange display values are derived at the API boundary.
/// </summary>
public sealed class EarningsCalendarEvent
{
    public string Id { get; set; } = String.Empty;
    public string Ticker { get; set; } = String.Empty;
    public string CompanyName { get; set; } = String.Empty;
    public DateOnly ReportDateExchange { get; set; }
    public DateTimeOffset ScheduledAtUtc { get; set; }
    public EarningsReleaseWindow ReleaseWindow { get; set; }
    public bool IsScheduleEstimate { get; set; }
    public decimal? MarketCapMillions { get; set; }
    public decimal? EpsEstimate { get; set; }
    public decimal? EpsActual { get; set; }
    public decimal? EpsSurprisePercent { get; set; }
    public decimal? ReportedEpsEstimate { get; set; }
    public decimal? ReportedEpsActual { get; set; }
    public decimal? ReportedEpsSurprisePercent { get; set; }
    public decimal? RevenueEstimateMillions { get; set; }
    public decimal? RevenueActualMillions { get; set; }
    public decimal? RevenueSurprisePercent { get; set; }
    public decimal? OneDayPriceReactionPercent { get; set; }
    public string Provider { get; set; } = String.Empty;
    public string SourceUrl { get; set; } = String.Empty;
    public string SourceArtifactSha256 { get; set; } = String.Empty;
    public DateTimeOffset ProviderReceivedAtUtc { get; set; }
    public DateTimeOffset? ResultFirstSeenAtUtc { get; set; }
    public DateTimeOffset FirstSeenAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
}
