namespace TradingFlow.Earnings;

public sealed record EarningsMonitorStatus(
    bool IsRunning,
    TimeSpan AnalysisInterval,
    DateTimeOffset? LastCalendarRefreshUtc,
    DateTimeOffset? LastAnalysisUtc,
    int TrackedEventCount,
    string? LastError);
