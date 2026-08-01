using TradingFlow.Domain.Earnings;

namespace TradingFlow.Finviz;

public sealed record FinvizEarningsCalendarPage(
    IReadOnlyList<EarningsCalendarEvent> Items,
    int Page,
    int TotalPages,
    int TotalItems);
