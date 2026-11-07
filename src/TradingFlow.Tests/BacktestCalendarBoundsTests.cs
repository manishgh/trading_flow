using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Pipeline;

namespace TradingFlow.Tests;

public sealed class BacktestCalendarBoundsTests
{
    // Exclusive midnight belongs to the preceding source date, including both New York daylight-saving transitions.
    [Theory]
    [InlineData("2026-10-27T04:00:00Z", "2026-10-26")]
    [InlineData("2026-11-02T05:00:00Z", "2026-11-01")]
    [InlineData("2026-03-09T04:00:00Z", "2026-03-08")]
    [InlineData("2026-10-27T14:00:00Z", "2026-10-27")]
    [InlineData("2026-10-27T05:00:00+01:00", "2026-10-26")]
    public void IntradayBounds_UseExclusiveNewYorkSourceDate(string end, string expected)
    {
        var run = Profile();
        foreach (var mode in new[] { ExecutionReplayPolicy.IntradayRthFiveMinuteV1,
            ExecutionReplayPolicy.IntradayRthFiveMinuteStopMarketV1, ExecutionReplayPolicy.IntradayRthFiveMinuteNextBarMarketV1 })
            Assert.Equal(DateOnly.Parse(expected), BacktestCalendarBounds.IntradayLastRequiredDate(
                run with { Engine = run.Engine with { ExecutionReplayPolicyId = mode } }, DateTimeOffset.Parse(end), "America/New_York"));
        Assert.Null(BacktestCalendarBounds.IntradayLastRequiredDate(run with {
            Engine = run.Engine with { ExecutionReplayPolicyId = ExecutionReplayPolicy.DailyRthFiveMinuteV1 }
        }, DateTimeOffset.Parse(end), "America/New_York"));
    }

    // The optional pipeline date removes unused future context and cannot omit a date containing requested source bars.
    [Theory]
    [InlineData(null, "2026-06-10")]
    [InlineData("2026-06-08", "2026-06-08")]
    public async Task PipelineCalendar_DefaultRetainedAndExplicitSourceBoundHonored(string? declared, string expected)
    {
        var provider = new CalendarSource();
        var request = Request(declared is null ? null : DateOnly.Parse(declared));
        await new CandlePipelineEngine().RunAsync(request, provider, default);
        Assert.Equal(DateOnly.Parse(expected), provider.LastRequestedDate);
    }

    // A caller cannot shorten a calendar below the last required exchange source date.
    [Fact]
    public async Task PipelineCalendar_RejectsOverrideThatOmitsSourceDate()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new CandlePipelineEngine().RunAsync(
            Request(new(2026, 6, 7)), new CalendarSource(), default));
    }

    // Use the ordinary profile only to obtain the existing complete source configuration shape.
    private static BacktestRunConfig Profile() => new SimpleYamlReader().ReadBacktestRun(
        Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "swing-backtest-profile.yaml"));

    // Supply a full historical exchange day ending at exclusive New York midnight.
    private static CandlePipelineRequest Request(DateOnly? calendarEnd) => new(["X"], ["5m"], ["5m"], "5m",
        DateTimeOffset.Parse("2026-06-08T13:30:00Z"), DateTimeOffset.Parse("2026-06-09T04:00:00Z"), 10, 1,
        CalendarEndDateInclusive: calendarEnd);

    private sealed class CalendarSource : IMarketDataProvider, IMarketSessionScheduleProvider
    {
        public DateOnly LastRequestedDate { get; private set; }

        // Record the exact explicit calendar request while retaining every requested open or closed source date.
        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDateInclusive, DateOnly endDateInclusive, CancellationToken cancellationToken)
        {
            LastRequestedDate = endDateInclusive;
            return Task.FromResult<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>(Enumerable.Range(
                startDateInclusive.DayNumber, endDateInclusive.DayNumber - startDateInclusive.DayNumber + 1)
                .Select(DateOnly.FromDayNumber).ToDictionary(date => date, MarketSessionSchedule.RegularDay));
        }

        // Yield one completed synthetic source bar without any HTTP or operator-data access.
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes, DateTimeOffset start, DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new("X", start, "5m", 100, 101, 99, 100, 1000);
        }
    }
}
