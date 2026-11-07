using TradingFlow.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

/// <summary>
/// Source-coverage and order-increment checks used by the opening-range breakout candidate path.
/// Moved from the retired previous-session breakout tests so the kept ORB path keeps its coverage.
/// </summary>
public sealed class IntradaySourceEvidenceTests
{
    // A missing mandatory source interval suppresses economics from its first affected session, while unused warm-up dates stay excluded.
    [Theory]
    [InlineData("complete", -1)]
    [InlineData("prior_gap", 0)]
    [InlineData("current_gap", 5)]
    [InlineData("empty_current", 0)]
    public void SourceCoverage_ReportsFirstAffectedRequestedInterval(string mutation, int missingMinute)
    {
        var (bars, schedules, asOf) = Fixture();
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var sessions = bars.GroupBy(bar => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bar.Timestamp, zone).DateTime))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var priorDay = new DateOnly(2026, 7, 2);
        var currentDay = new DateOnly(2026, 7, 6);
        if (mutation == "prior_gap") sessions[priorDay] = sessions[priorDay].Skip(1).ToArray();
        if (mutation == "current_gap") sessions[currentDay] = sessions[currentDay].Take(1).ToArray();
        if (mutation == "empty_current") sessions.Remove(currentDay);
        var start = bars[^2].Timestamp;
        var boundary = BacktestRunner.FindIntradaySourceGap(sessions, schedules,
            new Dictionary<DateOnly, DateOnly> { [currentDay] = priorDay }, zone, start, asOf);
        Assert.Equal(missingMinute < 0 ? (DateTimeOffset?)null : start.AddMinutes(missingMinute), boundary);
    }

    // Regulatory boundaries remain independent of the selected study dates and cannot be guessed across the one-dollar boundary.
    [Theory]
    [InlineData(1, 100, 0.01)]
    [InlineData(0.1, 0.9, 0.0001)]
    public void DocumentedOrderIncrement_PreservesSourceAndPriceBand(decimal low, decimal high, decimal tick)
    {
        var evidence = UsEquityOrderIncrementResolver.Resolve(new DateOnly(2026, 7, 6), low, high);
        Assert.Equal(tick, evidence!.TickSize);
        Assert.Equal(UsEquityOrderIncrementResolver.PolicyId, evidence.PolicyId);
        Assert.Equal(UsEquityOrderIncrementResolver.SourceUrl, evidence.SourceUrl);
        Assert.NotNull(UsEquityOrderIncrementResolver.Resolve(new DateOnly(2018, 10, 1), low, high));
        Assert.NotNull(UsEquityOrderIncrementResolver.Resolve(new DateOnly(2027, 10, 29), low, high));
        Assert.Null(UsEquityOrderIncrementResolver.Resolve(new DateOnly(2018, 9, 28), low, high));
        Assert.Null(UsEquityOrderIncrementResolver.Resolve(new DateOnly(2027, 11, 1), low, high));
        Assert.Null(UsEquityOrderIncrementResolver.Resolve(new DateOnly(2026, 7, 6), 0.9m, 1.1m));
    }

    // One complete prior regular session (2 July 2026), a closed holiday weekend, and two opening bars on 6 July.
    private static (List<OhlcvBar> Bars, IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Schedules, DateTimeOffset AsOf) Fixture()
    {
        var bars = new List<OhlcvBar>();
        var priorOpen = new DateTimeOffset(2026, 7, 2, 13, 30, 0, TimeSpan.Zero);
        for (var index = 0; index < 78; index++) bars.Add(Bar(priorOpen.AddMinutes(5 * index), 98, 100, 95, 99));
        var open = new DateTimeOffset(2026, 7, 6, 13, 30, 0, TimeSpan.Zero);
        bars.Add(Bar(open, 99, 103, 97, 101));
        bars.Add(Bar(open.AddMinutes(5), 101, 104, 98, 102));
        var schedules = Enumerable.Range(0, 5).Select(day => new DateOnly(2026, 7, 2).AddDays(day))
            .ToDictionary(day => day, day => day.Day == 2 || day.Day == 6 ? MarketSessionSchedule.RegularDay(day) : MarketSessionSchedule.Closed(day));
        return (bars, schedules, open.AddMinutes(10));
    }

    // Keep every fixture candle on the same explicit SIP adjustment basis.
    private static OhlcvBar Bar(DateTimeOffset time, decimal open, decimal high, decimal low, decimal close) =>
        new("SPY", time, "5m", open, high, low, close, 1000m, "sip", "all", time.AddMinutes(5));
}
