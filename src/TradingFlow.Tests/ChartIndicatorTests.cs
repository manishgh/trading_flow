using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Tests;

public sealed class ChartIndicatorTests
{
    // Check an independent EMA9 recurrence against the pinned seed and convergence mask.
    [Fact]
    public void EmaNineUsesInitialNineCloseMeanAndThenWeightsEachCompletedClose()
    {
        var bars = Enumerable.Range(0, 271).Select(index => Bar(index, 100 + index % 7)).ToArray();
        var formula = ChartIndicatorMath.Find("ema9")!;
        var output = ChartIndicatorMath.Calculate(formula, bars, 0, bars.Length)[0];
        var expected = bars.Take(9).Average(bar => (double)bar.Bar.Close);
        for (var index = 9; index < bars.Length; index++)
        {
            expected = 0.2 * (double)bars[index].Bar.Close + 0.8 * expected;
            if (index >= 258) Assert.Equal(expected, output[index]!.Value, 10);
        }
        Assert.All(output.Take(258), value => Assert.Null(value));
    }

    // The width of the Bollinger fixture distinguishes population deviation from sample deviation.
    [Fact]
    public void BollingerAndSmaUseTwentyActualCloses()
    {
        var bars = Enumerable.Range(0, 20).Select(index => Bar(index, index + 1)).ToArray();
        var bands = ChartIndicatorMath.Calculate(ChartIndicatorMath.Find("bb20")!, bars, 0, 20);
        var sma = ChartIndicatorMath.Calculate(ChartIndicatorMath.Find("sma20")!, bars, 0, 20);
        Assert.Null(sma[0][18]);
        Assert.Equal(10.5, sma[0][19]);
        Assert.Equal(10.5 - 2 * Math.Sqrt(33.25), bands[0][19]!.Value, 10);
        Assert.Equal(10.5, bands[1][19]);
        Assert.Equal(10.5 + 2 * Math.Sqrt(33.25), bands[2][19]!.Value, 10);
    }

    // A constant rising sequence makes expected convergence, trend direction and range independently calculable.
    [Fact]
    public void WilderAndMacdFamiliesFollowIndependentRisingPriceExpectations()
    {
        var bars = Enumerable.Range(0, 310).Select(index => Bar(index, index + 100, spread: 1)).ToArray();
        AssertLast("rsi14", [100]);
        AssertLast("atr14", [2]);
        AssertLast("atrpct14", [200d / 409]);
        AssertLast("adx14", [100, 50, 0]);
        AssertLast("macd", [7, 7, 0]);

        // Hold back each series until its chart convergence threshold, even when the package supplies a number.
        void AssertLast(string name, double[] expected)
        {
            var formula = ChartIndicatorMath.Find(name)!;
            var actual = ChartIndicatorMath.Calculate(formula, bars, 0, bars.Length);
            for (var column = 0; column < expected.Length; column++)
            {
                Assert.Null(actual[column][formula.WarmObservations - 2]);
                Assert.Equal(expected[column], actual[column][^1]!.Value, 9);
            }
        }
    }

    // A wholly flat, zero-range tape has no directional denominator and never leaks a nonfinite sample.
    [Fact]
    public void FlatZeroRangeAndObvEdgeCasesAreExplicit()
    {
        var flat = Enumerable.Range(0, 265).Select(index => Bar(index, 100)).ToArray();
        var adx = ChartIndicatorMath.Calculate(ChartIndicatorMath.Find("adx14")!, flat, 0, flat.Length);
        Assert.All(adx, column => Assert.Null(column[^1]));
        Assert.Equal(0, ChartIndicatorMath.Calculate(ChartIndicatorMath.Find("atr14")!, flat, 0, flat.Length)[0][^1]);

        var obv = new[] { Bar(0, 100, 5), Bar(1, 101, 3), Bar(2, 101, 900), Bar(3, 100, 0), Bar(4, 99, 2) };
        Assert.Equal(new double?[] { 0, 3, 3, 3, 1 },
            ChartIndicatorMath.Calculate(ChartIndicatorMath.Find("obv")!, obv, 0, obv.Length)[0]);
    }

    // Recursive output keeps the same origin and values across an append, a narrower view and recreated calculation state.
    [Fact]
    public void FixedCalendarSeedSurvivesReconnectionPagingAndCorrection()
    {
        var (daily, calendar) = CalendarFixture();
        var formula = ChartIndicatorMath.Find("ema20")!;
        var displayed = daily[^18].EndUtc;
        var complete = Project(daily, daily[0].SessionDate, displayed);
        var beforeAppend = Project(daily[..^2], daily[0].SessionDate, daily[^12].EndUtc);
        var index = daily.Length - 6;
        Assert.NotNull(complete.Columns[0][index]);
        Assert.Equal(complete.Clocks[index], beforeAppend.Clocks[index]);
        Assert.Equal(complete.Columns[0][index], beforeAppend.Columns[0][index]);

        var revised = daily.ToArray();
        var corrected = revised[index];
        revised[index] = corrected with
        {
            Bar = corrected.Bar with { Close = 900 },
            AvailableAtUtc = daily[^1].EndUtc.AddMinutes(12)
        };
        var correction = Project(revised, daily[0].SessionDate, displayed);
        Assert.Equal(complete.Columns[0][index - 1], correction.Columns[0][index - 1]);
        Assert.NotEqual(complete.Columns[0][index], correction.Columns[0][index]);
        Assert.Equal(revised[index].AvailableAtUtc, correction.Clocks[index + 1]!.Value.AvailableAtUtc);

        var clipped = daily[^100..];
        var noAnchor = Project(clipped, clipped[0].SessionDate, displayed);
        Assert.All(noAnchor.Columns[0], value => Assert.Null(value));

        // Reconstruct each request independently to simulate memo eviction.
        ChartCalculatedIndicator Project(ChartCompletedBar[] bars, DateOnly coverage, DateTimeOffset first) =>
            ChartIndicatorMath.Project(formula, bars, new ChartBarProjector(calendar), coverage, true, first, default);
    }

    // Fixed windows remain suppressed after a split or unknown slot until N genuine post-break observations exist.
    [Fact]
    public void SplitAndMissingMinuteDoNotCarryOldBasisIntoRollingAverage()
    {
        var bars = Enumerable.Range(0, 44).Select(index => index < 22 ? Bar(index, 100) :
            Bar(index, 50) with { Continuity = 1 }).ToArray();
        var oldest = bars[22];
        bars[22] = oldest with { AvailableAtUtc = oldest.EndUtc.AddHours(1) };
        var result = ChartIndicatorMath.Project(ChartIndicatorMath.Find("sma20")!, bars,
            new ChartBarProjector(new Dictionary<DateOnly, MarketSessionSchedule>()), bars[0].SessionDate,
            false, bars[0].EndUtc, default);
        Assert.Equal(100, result.Columns[0][21]);
        Assert.All(result.Columns[0][22..41], value => Assert.Null(value));
        Assert.Equal(50, result.Columns[0][41]);
        Assert.Equal(oldest.EndUtc.AddHours(1), result.Clocks[41]!.Value.AvailableAtUtc);
        Assert.Equal(bars[42].AvailableAtUtc, result.Clocks[42]!.Value.AvailableAtUtc);
    }

    // Real zero-volume and covered quiet minutes have distinct inputs; an unknown minute stops the cumulative prefix.
    [Fact]
    public void RegularPrefixRespectsDstEarlyCloseQuietProofAndMissingMinute()
    {
        var date = new DateOnly(2026, 11, 27);
        var projector = new ChartBarProjector(new Dictionary<DateOnly, MarketSessionSchedule>
        {
            [date] = new(date, true, new TimeOnly(9, 30), new TimeOnly(13, 0))
        });
        var range = projector.Regular(date)!;
        Assert.Equal(14, range.StartUtc.Hour);
        Assert.Equal(18, range.EndUtc.Hour);
        var minute = range.StartUtc;
        var first = Bar(0, 101, 10).Bar with { Timestamp = minute };
        var fourth = Bar(0, 102, 15).Bar with { Timestamp = minute.AddMinutes(3) };
        var proof = minute.AddMinutes(5);
        var source = new Dictionary<DateTimeOffset, ChartSourceBar>
        {
            [minute] = new(first, "fixture", null, minute.AddMinutes(1)),
            [fourth.Timestamp] = new(fourth, "fixture", null, proof)
        };
        var quiet = new Dictionary<DateTimeOffset, DateTimeOffset> { [minute.AddMinutes(1)] = proof };
        var result = ChartRegularSessionStudies.Read(range, range.EndUtc, range.EndUtc, source, quiet,
            new HashSet<DateTimeOffset>(), true);
        Assert.Equal(new decimal[] { 10, 10 }, result.Volume);
        Assert.Equal(new decimal?[] { 101, 101 }, result.Vwap);
        Assert.Equal(proof, result.Available[1]);
        Assert.Null(ChartIndicatorMath.CumulativeRelativeVolume(50, new decimal[19]));
        Assert.Null(ChartIndicatorMath.CumulativeRelativeVolume(50, new decimal[20]));
        Assert.Equal(2, ChartIndicatorMath.CumulativeRelativeVolume(21, Enumerable.Range(1, 20).Select(i => (decimal)i).ToArray()));
    }

    // A chart profile has fixed presets, source constraints and two lower-pane slots even when an ATR readout is selected.
    [Theory]
    [InlineData("obv,atr14,macd")]
    [InlineData("sma20,sma50,sma200,ema20,bb20")]
    [InlineData("ema9:5m")]
    [InlineData("atr14,atr14:readout")]
    [InlineData("macd:1d")]
    [InlineData("atr17")]
    public void InvalidProfileCannotReachSource(string value) =>
        Assert.Throws<ArgumentException>(() => ChartSeriesService.Normalize(new("AAPL", "5m", "regular", "year", value)));

    // Normalization canonicalizes order for shared computations and preserves explicit daily source and readout placement.
    [Fact]
    public void SourceAndPlacementAreCanonicalInOneProfile()
    {
        var key = ChartSeriesService.Normalize(new("AAPL", "5m", "regular", "year", " RSI14, sma50:1D,ATR14:Readout,rsi14 "));
        Assert.Equal("atr14:readout,rsi14,sma50:1d", key.Indicators);
        Assert.Equal("1d", ChartIndicatorProfile.Read(key)[^1].SourceInterval);
    }

    // Make one positive-price, completed minute observation for independent numerical examples.
    private static ChartCompletedBar Bar(int index, decimal close, decimal volume = 10, decimal spread = 0)
    {
        var time = new DateTimeOffset(2026, 4, 2, 14, 0, 0, TimeSpan.Zero).AddMinutes(index);
        var end = time.AddMinutes(1);
        var bar = new OhlcvBar("TEST", time, "1m", close, close + spread, close - spread, close, volume,
            KnownAtUtc: end, DataFeed: "sip", AdjustmentPolicy: "all");
        return new ChartCompletedBar(bar, end, ChartBarProjector.NewYorkDate(time), "regular", 0, "fixture", null, end);
    }

    // Supply an authoritative synthetic calendar and 500 replayable genuine daily observations.
    private static (ChartCompletedBar[] Bars, Dictionary<DateOnly, MarketSessionSchedule> Calendar) CalendarFixture()
    {
        var first = new DateOnly(2023, 1, 2);
        var calendar = Enumerable.Range(0, 1_050).Select(first.AddDays).ToDictionary(day => day,
            day => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? MarketSessionSchedule.Closed(day) : MarketSessionSchedule.RegularDay(day));
        var days = calendar.Where(pair => pair.Value.IsTradingDay).Select(pair => pair.Key).TakeLast(500).ToArray();
        var bars = days.Select((day, index) =>
        {
            var at = ChartBarProjector.At(day, TimeOnly.MinValue);
            var close = ChartBarProjector.At(day, new TimeOnly(16, 0));
            var fixture = Bar(index, 100 + index % 11);
            return fixture with
            {
                Bar = fixture.Bar with { Timestamp = at, Timeframe = "1d", KnownAtUtc = close },
                EndUtc = close, SessionDate = day, Segment = "daily", AvailableAtUtc = close
            };
        }).ToArray();
        return (bars, calendar);
    }
}
