namespace TradingFlow.Earnings;

/// <summary>Evidence for the last stored, fully queried Finviz calendar window, with inclusive exchange dates.</summary>
public sealed record EarningsCalendarEvidence(DateTimeOffset ReceivedAtUtc,
    DateOnly FromExchangeDate, DateOnly ToExchangeDate, int EventCount, DateTimeOffset RefreshDueAtUtc);

/// <summary>Outcome of the latest attempt, independent of the time of the last saved analysis.</summary>
public sealed record EarningsAnalysisAttempt(string State, DateTimeOffset CompletedAtUtc, string Detail);

public sealed record EarningsMonitorStatus(
    bool IsRunning,
    TimeSpan AnalysisInterval,
    DateTimeOffset? LastCalendarRefreshUtc,
    DateTimeOffset? LastAnalysisUtc,
    int TrackedEventCount,
    string? LastError,
    EarningsCalendarEvidence? CalendarCoverage = null,
    EarningsAnalysisAttempt? LatestAttempt = null);
