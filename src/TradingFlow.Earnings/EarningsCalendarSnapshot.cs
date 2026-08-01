using TradingFlow.Domain.Earnings;

namespace TradingFlow.Earnings;

public sealed record EarningsCalendarSnapshot(
    DateTimeOffset GeneratedAtUtc,
    DateOnly FromExchangeDate,
    DateOnly ToExchangeDate,
    IReadOnlyList<EarningsCalendarSnapshotItem> Items);

public sealed record EarningsCalendarSnapshotItem(
    EarningsCalendarEvent Event,
    EarningsAnalysisSnapshot? Analysis,
    EarningsCalendarEvent? PreviousReportedEvent);
