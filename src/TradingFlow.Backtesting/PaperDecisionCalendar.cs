using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;

namespace TradingFlow.Backtesting;

/// <summary>Shares a daily exchange-calendar lookup across all tickers in one paper runner.</summary>
internal sealed class PaperDecisionCalendar(IMarketDataProvider provider, string exchangeTimezone)
{
    private DateOnly? cachedDate;
    private IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? cachedSchedules;

    // Keep entry-only calendar I/O lazy so exits can proceed during a provider failure.
    public Lazy<Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>?>> ForPoll(
        DateTimeOffset timestamp, CancellationToken cancellationToken) =>
        new(() => LoadAsync(ExecutionRunContextFactory.ResolveSessionDate(timestamp, exchangeTimezone), cancellationToken));

    // Refresh a successful calendar once per exchange date. Failed reads can retry next poll.
    private async Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>?> LoadAsync(
        DateOnly exchangeDate, CancellationToken cancellationToken)
    {
        if (cachedDate != exchangeDate && provider is IMarketSessionScheduleProvider calendar)
        {
            cachedSchedules = await calendar.LoadMarketSessionSchedulesAsync(
                exchangeDate.AddDays(-31), exchangeDate.AddDays(31), cancellationToken);
            cachedDate = exchangeDate;
        }

        return cachedSchedules;
    }

    // The same completed daily setup has one executable session. Cut off entries
    // at the actual close, including an early close, and honor holiday/DST changes.
    public static DateTimeOffset ResolveExpiry(
        DateTimeOffset available,
        StrategyDefinition strategy,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules)
    {
        if (strategy.Session.IsContinuousMarket)
        {
            return available.Add(TimeframeParser.Parse(strategy.Timeframe));
        }
        if (schedules is null)
        {
            throw new InvalidOperationException("Paper equity entries require an authoritative exchange calendar.");
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(strategy.Session.ExchangeTimezone);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(available, zone).DateTime);
        var schedule = BacktestDecisionTiming.ResolveScheduledSession(date, schedules);
        var localClose = schedule.TradeDate.ToDateTime(schedule.RegularClose, DateTimeKind.Unspecified);
        return new DateTimeOffset(localClose, zone.GetUtcOffset(localClose)).ToUniversalTime();
    }
}
