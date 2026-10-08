using System.Runtime.CompilerServices;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;

namespace TradingFlow.Backtesting;

internal class CompletedBacktestDataProvider(IMarketDataProvider inner) :
    IMarketDataProvider, IMarketDataCompletenessProvider, IDisposable
{
    public static IMarketDataProvider Wrap(IMarketDataProvider inner) => inner is IMarketSessionScheduleProvider calendar
        ? new CompletedCalendarBacktestDataProvider(inner,calendar) : new CompletedBacktestDataProvider(inner);
    public bool OmittedSubDailyIntervalsMeanNoQualifyingTrades =>
        inner is IMarketDataCompletenessProvider source && source.OmittedSubDailyIntervalsMeanNoQualifyingTrades;
    public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
        DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var bar in inner.GetBarsAsync(tickers,timeframes,start,end,cancellationToken))
            if (bar.Timestamp.Add(TimeframeParser.Parse(bar.Timeframe)) <= end && (bar.KnownAtUtc is null || bar.KnownAtUtc <= end))
                yield return bar;
    }
    public void Dispose() { if(inner is IDisposable owned) owned.Dispose(); }
}

internal sealed class CompletedCalendarBacktestDataProvider(IMarketDataProvider inner, IMarketSessionScheduleProvider calendar)
    : CompletedBacktestDataProvider(inner), IMarketSessionScheduleProvider
{
    public Task<IReadOnlyDictionary<DateOnly,MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(DateOnly start,DateOnly end,CancellationToken token)
        => calendar.LoadMarketSessionSchedulesAsync(start,end,token);
}
