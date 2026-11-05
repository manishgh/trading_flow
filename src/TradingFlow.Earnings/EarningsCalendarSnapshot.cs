using TradingFlow.Domain.Earnings;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Earnings;

public sealed record EarningsCalendarSnapshot(
    DateTimeOffset GeneratedAtUtc,
    DateOnly FromExchangeDate,
    DateOnly ToExchangeDate,
    IReadOnlyList<EarningsCalendarSnapshotItem> Items);

public sealed record EarningsCalendarSnapshotItem(
    EarningsCalendarEvent Event,
    EarningsAnalysisSnapshot? Analysis,
    EarningsCalendarEvent? PreviousReportedEvent,
    // The stock's most recent trade, for display only; null outside trading hours or when it could not be read.
    LatestTrade? LatestTrade = null);
