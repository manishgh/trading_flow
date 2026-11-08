using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

/// <summary>
/// Decisions of the published opening-range recipe (technical.opening-range-breakout@2.0.0, Zarattini, Barbon and Aziz):
/// direction from the 5-minute range, opening volume against its 14-session mean, stop order at the range, stop at
/// 10% of the 14-session daily ATR, one order per stock per session. Prior sessions: daily range 95-100 (ATR 5) and
/// 100,000 shares in each first bar.
/// </summary>
public sealed class OpeningRangeRecipeEvaluatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly DateTimeOffset Open = new(2026, 10, 1, 13, 30, 0, TimeSpan.Zero);

    // Long: body up, volume twice the mean; trigger at the range high and stop 0.5 (10% of ATR 5) below it.
    [Fact]
    public void BullishRange_PlacesStopOrderAtHighWithAtrFractionStop()
    {
        var signal = Assert.IsType<IntradayBreakoutSignal>(Evaluate(Bar(Open, 99m, 100m, 98m, 99.5m, 200_000m)).Signal);

        Assert.Equal(("long", 100m, 99.5m, 5m, 2m), (signal.Direction, signal.LimitPrice, signal.InitialStopPrice,
            signal.DailyAtr!.Value, signal.OpeningRelativeVolume!.Value));
        Assert.Equal(IntradayEntryOrderMode.StopMarket, signal.EntryOrderMode);
        Assert.Equal(1, signal.MaxFilledParentTradesPerSession);
    }

    // Short: body down; trigger at the range low and stop 0.5 above it.
    [Fact]
    public void BearishRange_PlacesStopOrderAtLowWithAtrFractionStop()
    {
        var signal = Assert.IsType<IntradayBreakoutSignal>(Evaluate(Bar(Open, 99.5m, 100m, 98m, 99m, 200_000m)).Signal);

        Assert.Equal(("short", 98m, 98.5m), (signal.Direction, signal.LimitPrice, signal.InitialStopPrice));
    }

    // No order on a doji, on volume below the mean, without the full ATR history, or after the range has completed.
    [Theory]
    [InlineData("doji", "opening_range_doji")]
    [InlineData("low_volume", "opening_range_relative_volume_below_minimum")]
    [InlineData("short_atr_history", "opening_range_daily_atr_unavailable")]
    [InlineData("later_bar", "opening_range_order_placed_at_range_completion")]
    public void RecipeRejectsWithANamedReason(string scenario, string reason)
    {
        var first = scenario switch
        {
            "doji" => Bar(Open, 99m, 100m, 98m, 99m, 200_000m),
            "low_volume" => Bar(Open, 99m, 100m, 98m, 99.5m, 99_999m),
            _ => Bar(Open, 99m, 100m, 98m, 99.5m, 200_000m)
        };
        var evaluation = scenario == "later_bar"
            ? Evaluate(first, Bar(Open.AddMinutes(5), 99.5m, 100m, 99m, 99.6m, 100_000m))
            : Evaluate(first, dailySessions: scenario == "short_atr_history" ? 14 : 15);

        Assert.Null(evaluation.Signal);
        Assert.Equal(reason, evaluation.RejectionReason);
    }

    // True range includes the gap from the previous close: each day trades 95-100 after closing at 105 the day before,
    // so every true range is 10 (|95 - 105|) and the stop sits 1.00 (10% of ATR 10) below the trigger.
    [Fact]
    public void DailyAtr_UsesTrueRangeIncludingTheGapFromThePreviousClose()
    {
        var signal = Assert.IsType<IntradayBreakoutSignal>(Evaluate(Bar(Open, 99m, 100m, 98m, 99.5m, 200_000m),
            dailyClose: _ => 105m).Signal);

        Assert.Equal((10m, 99m), (signal.DailyAtr!.Value, signal.InitialStopPrice));
    }

    // Opening volume exactly equal to the mean passes (the paper requires at least 100%).
    [Fact]
    public void RelativeVolumeOfExactlyOne_IsAccepted()
    {
        var signal = Assert.IsType<IntradayBreakoutSignal>(Evaluate(Bar(Open, 99m, 100m, 98m, 99.5m, 100_000m)).Signal);

        Assert.Equal(1m, signal.OpeningRelativeVolume);
    }

    // A daily bar that was not yet known at the decision time cannot supply the ATR.
    [Fact]
    public void DailyBarKnownAfterTheDecision_IsRejected()
    {
        var evaluation = Evaluate(Bar(Open, 99m, 100m, 98m, 99.5m, 200_000m), dailyKnownLate: true);

        Assert.Equal("opening_range_daily_atr_unavailable", evaluation.RejectionReason);
    }

    // Evaluate the kept ORB 2.0.0 file at the close of the last supplied current-session bar.
    private static IntradayBreakoutEvaluation Evaluate(OhlcvBar first, OhlcvBar? second = null, int dailySessions = 15,
        Func<int, decimal>? dailyClose = null, bool dailyKnownLate = false)
    {
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(), "configs", "backtest",
            "strategies", "opening-range-breakout.bt-v4.yaml"));
        var current = second is null ? new[] { first } : new[] { first, second };
        var schedules = new Dictionary<DateOnly, MarketSessionSchedule>();
        var prior = new List<DateOnly>();
        for (var date = Today.AddDays(-30); date <= Today; date = date.AddDays(1))
        {
            var trading = date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && date != new DateOnly(2026, 9, 7);
            schedules[date] = trading ? MarketSessionSchedule.RegularDay(date) : MarketSessionSchedule.Closed(date);
            if (trading && date < Today) prior.Add(date);
        }
        DateTimeOffset SessionOpen(DateOnly date) => new(date.ToDateTime(new TimeOnly(13, 30)), TimeSpan.Zero);
        var baseline = prior.TakeLast(14).Select(date => Bar(SessionOpen(date), 99m, 100m, 98m, 99m, 100_000m)).ToList();
        var asOf = current[^1].Timestamp.AddMinutes(5);
        var daily = prior.TakeLast(dailySessions).Select((date, index) =>
            new OhlcvBar("ABC", SessionOpen(date), "1d", 99m, 100m, 95m, dailyClose?.Invoke(index) ?? 99m, 7_800_000m, "sip", "all",
                dailyKnownLate && index == dailySessions - 1 ? asOf.AddMinutes(1) : SessionOpen(date).AddHours(7))).ToList();
        var context = new IntradayBreakoutContext(schedules, 0.01m, asOf)
            { OpeningRangeBaselineBars = baseline, PriorSessionDailyBars = daily };
        return new OpeningRangeBreakoutEvaluator().Evaluate(strategy, current, [], context, asOf);
    }

    // Five-minute SIP bar known at its own close.
    private static OhlcvBar Bar(DateTimeOffset start, decimal open, decimal high, decimal low, decimal close, decimal volume) =>
        new("ABC", start, "5m", open, high, low, close, volume, "sip", "all", start.AddMinutes(5));
}
