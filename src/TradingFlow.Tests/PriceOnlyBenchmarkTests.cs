using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Tests;

public sealed class PriceOnlyBenchmarkTests
{
    private static readonly DateTimeOffset Jan31 = new(2017, 1, 31, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Feb1 = Jan31.AddDays(1);
    private static readonly DateTimeOffset Feb2 = Feb1.AddDays(1);
    private static readonly BenchmarkConfig Benchmark = new(true, "SPY");

    // The run excludes a large warm-up and future move, and a single session can establish both monthly prices.
    [Fact]
    public void WindowAndMonths_UseInWindowOpenAndCarryPriorClosingPrice()
    {
        var result = PriceOnlyBenchmark.Build(Benchmark, [
            Daily(Jan31.AddDays(-1), 1m, 10m), Daily(Jan31, 100m, 105m),
            Daily(Feb1, 110m, 121m), Daily(Feb2, 120m, 999m),
            Daily(Feb1, 1m, 999m) with { Ticker = "OTHER" }
        ], Jan31, Feb2, Calendar(), []);

        Assert.Equal(21m, result.BenchmarkReturnPct);
        Assert.Equal(Jan31.AddHours(9.5), result.OpeningTimestampUtc);
        Assert.Equal(Feb1.AddHours(16), result.ClosingTimestampUtc);
        Assert.Equal("raw", result.PriceAdjustmentPolicy);
        Assert.Null(result.UnavailableReason);
        Assert.Collection(result.Months!,
            january =>
            {
                Assert.Equal(("2017-01", 5m, "first_evaluation_session_open"),
                    (january.Month, january.ReturnPct!.Value, january.OpeningBasis));
                Assert.Equal((Jan31, Feb1, 100m, 105m),
                    (january.PeriodStartUtc, january.PeriodEndUtc, january.OpeningPrice!.Value, january.ClosingPrice!.Value));
            },
            february =>
            {
                Assert.Equal(("2017-02", 15.2381m, "prior_month_final_session_close"),
                    (february.Month, february.ReturnPct!.Value, february.OpeningBasis));
                Assert.Equal((Feb1, Feb2, 105m, 121m),
                    (february.PeriodStartUtc, february.PeriodEndUtc, february.OpeningPrice!.Value, february.ClosingPrice!.Value));
                Assert.Equal(Jan31.AddHours(16), february.OpeningTimestampUtc);
                Assert.Equal(Feb1.AddHours(16), february.ClosingTimestampUtc);
            });
    }

    // An in-window source date still needs a completed bar and a receipt by the cutoff.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndingBenchmarkBar_MustBeCompletedAndKnown(bool delayedReceipt)
    {
        var end = delayedReceipt ? Feb2 : Feb2.AddHours(-1);
        var receipt = delayedReceipt ? Feb2.AddMinutes(1) : (DateTimeOffset?)null;
        var result = PriceOnlyBenchmark.Build(Benchmark,
            [Daily(Jan31, 100m, 105m), Daily(Feb1, 110m, 121m) with { KnownAtUtc = receipt }],
            Jan31, end, Calendar(), []);

        Assert.Null(result.BenchmarkReturnPct);
        Assert.Contains("2017-02-01", result.UnavailableReason);
        Assert.Equal(5m, result.Months![0].ReturnPct);
        Assert.Null(result.Months[1].ReturnPct);
    }

    // A month's first open cannot substitute for missing January's closing basis.
    [Fact]
    public void MissingBoundary_DoesNotShortenRunOrRestartBenchmarkNextMonth()
    {
        var result = PriceOnlyBenchmark.Build(Benchmark, [Daily(Feb1, 110m, 121m)], Jan31, Feb2, Calendar(), []);

        Assert.Null(result.BenchmarkReturnPct);
        Assert.Contains("2017-01-31", result.Months![0].UnavailableReason);
        Assert.Null(result.Months[1].ReturnPct);
        Assert.Contains("Prior month closing", result.Months[1].UnavailableReason);
    }

    // Sub-daily prices need both boundary bars and every interval in between, even on a shortened session.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SubDailyBenchmark_RequiresContinuousCoverageThroughTheScheduledClose(bool includeMiddle)
    {
        var session = new DateOnly(2017, 1, 31);
        var calendar = new Dictionary<DateOnly, MarketSessionSchedule>
        {
            [session] = MarketSessionSchedule.RegularDay(session) with { RegularClose = new TimeOnly(10, 15) }
        };
        var start = Jan31.AddHours(9.5);
        var bars = new[]
        {
            Daily(start, 100, 102) with { Timeframe = "15m" },
            Daily(start.AddMinutes(15), 102, 104) with { Timeframe = "15m" },
            Daily(start.AddMinutes(30), 104, 105) with { Timeframe = "15m" }
        };
        var result = PriceOnlyBenchmark.Build(Benchmark, includeMiddle ? bars : [bars[0], bars[2]],
            Jan31, Feb1, calendar, []);
        Assert.Equal(includeMiddle ? 5m : (decimal?)null, result.BenchmarkReturnPct);
        Assert.Equal(includeMiddle ? start.AddMinutes(45) : (DateTimeOffset?)null, result.ClosingTimestampUtc);
    }

    // Unknown or ambiguous metadata cannot establish a price-only return.
    [Theory]
    [InlineData("unspecified")]
    [InlineData("adjusted")]
    public void UnverifiedAdjustmentBasis_WithholdsAggregateAndMonthlyReturns(string basis)
    {
        var result = PriceOnlyBenchmark.Build(Benchmark,
            [Daily(Jan31, 100m, 110m) with { AdjustmentPolicy = basis }], Jan31, Feb1, Calendar(), []);

        Assert.Null(result.BenchmarkReturnPct);
        Assert.Contains("does not verify raw or split-only prices", result.UnavailableReason);
        var month = Assert.Single(result.Months!);
        Assert.Null(month.ReturnPct);
        Assert.Null(month.OpeningTimestampUtc);
        Assert.Equal(result.UnavailableReason, month.UnavailableReason);
    }

    // A source contract can establish the adjustment basis for candles without their own label.
    [Fact]
    public void VerifiedProviderAdjustment_SupportsPriceOnlyReturnForUnspecifiedBarMetadata()
    {
        var provenance = new TradingFlow.Engine.Abstractions.MarketDataProvenance("synthetic", "sip", "split");
        var result = PriceOnlyBenchmark.Build(Benchmark,
            [Daily(Jan31, 100m, 110m) with { AdjustmentPolicy = "unspecified" }], Jan31, Feb1, Calendar(), [], provenance);

        Assert.Equal("split", result.PriceAdjustmentPolicy);
        Assert.Equal(10m, result.BenchmarkReturnPct);
        Assert.Equal(10m, Assert.Single(result.Months!).ReturnPct);
    }

    // Provider dividend adjustments prevent a comparison made exclusively from price changes.
    [Fact]
    public void DividendAdjustedPrices_AreUnavailableForPriceOnlyComparison()
    {
        var result = PriceOnlyBenchmark.Build(Benchmark,
            [Daily(Jan31, 100m, 110m) with { AdjustmentPolicy = "all" }], Jan31, Feb1, Calendar(), []);
        Assert.Equal("all", result.PriceAdjustmentPolicy);
        Assert.Null(result.BenchmarkReturnPct);
        Assert.Contains("dividend adjustments", Assert.Single(result.Months!).UnavailableReason);
    }

    // The complete calendar distinguishes a closed date from an unverified or absent source date.
    private static IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Calendar() =>
        new[] { new DateOnly(2017, 1, 31), new DateOnly(2017, 2, 1) }.ToDictionary(date => date, MarketSessionSchedule.RegularDay);

    // The provider stamps each native daily candle at midnight New York.
    private static OhlcvBar Daily(DateTimeOffset date, decimal open, decimal close) => new(
        "SPY", date, "1d", open, Math.Max(open, close), Math.Min(open, close), close, 1_000m, "sip", "raw");
}
