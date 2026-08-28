using TradingFlow.Engine.Indicators;

namespace TradingFlow.Engine.Abstractions;

/// <summary>
/// Loads authoritative exchange-session schedules for a complete, inclusive date range.
/// Implementations must return every requested date exactly once; dates omitted by a
/// successful, complete provider response are represented as closed sessions.
/// </summary>
public interface IMarketSessionScheduleProvider
{
    Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
        DateOnly startDateInclusive,
        DateOnly endDateInclusive,
        CancellationToken cancellationToken);
}
