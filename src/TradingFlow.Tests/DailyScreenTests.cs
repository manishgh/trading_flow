using TradingFlow.Domain.Market;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Tests;

public sealed class DailyScreenTests
{
    private static readonly DailyScreenRules Orb = new(5m, DailyScreenPriceBasis.SessionOpen, 14,
        MinAverageShareVolume: 1_000_000m, AtrPeriod: 14, MinAtr: 0.5m, MinHistorySessions: 14);

    private static OhlcvBar[] Bars(int count, decimal price = 20m, decimal range = 1m, decimal volume = 2_000_000m) =>
        Enumerable.Range(0, count).Select(index => new OhlcvBar("ABC", new DateTimeOffset(2026, 4, 1, 4, 0, 0, TimeSpan.Zero).AddDays(index),
            "1Day", price, price + range / 2m, price - range / 2m, price, volume)).ToArray();

    [Fact]
    public void Orb_RulesAdmitALiquidVolatileStockAndRecordItsValues()
    {
        var result = DailyScreen.Evaluate("ABC", Bars(30), 21m, Orb);

        Assert.True(result.Eligible);
        Assert.Equal(21m, result.Values!.Price);
        Assert.Equal(2_000_000m, result.Values.AverageShareVolume);
        Assert.Equal(1m, result.Values.Atr!.Value, 6);
    }

    [Theory]
    [InlineData(10, 21, 1, 2_000_000, "history")]
    [InlineData(30, 4.99, 1, 2_000_000, "price")]
    [InlineData(30, 21, 1, 900_000, "average_volume")]
    [InlineData(30, 21, 0.4, 2_000_000, "atr")]
    public void Orb_RulesRejectEachFailureWithItsReason(int sessions, double open, double range, double volume, string reason)
    {
        var result = DailyScreen.Evaluate("ABC", Bars(sessions, range: (decimal)range, volume: (decimal)volume), (decimal)open, Orb);

        Assert.False(result.Eligible);
        Assert.Contains(reason, result.Rejection);
    }

    [Fact]
    public void SessionOpenPriceIsRequiredWhenTheRulesUseIt()
    {
        Assert.Contains("session_open_unavailable", DailyScreen.Evaluate("ABC", Bars(30), null, Orb).Rejection);
    }

    [Fact]
    public void Split_PutsEarlierSessionsOnTheDecisionDateShareBasisOnlyOnceEffective()
    {
        var bars = Bars(4, price: 100m, volume: 1_000m);
        var split = new ShareSplit(new DateOnly(2026, 4, 3), 2m);

        var before = SplitBasis.AsOf(bars, [split], new DateOnly(2026, 4, 2));
        var after = SplitBasis.AsOf(bars, [split], new DateOnly(2026, 4, 4));

        Assert.Equal(100m, before[0].Close);
        Assert.Equal(50m, after[0].Close);
        Assert.Equal(2_000m, after[0].Volume);
        Assert.Equal(100m, after[2].Close);
    }
}

public sealed class DailyScreenCommandTests
{
    [Fact]
    public void Screen_DecidesEachSessionFromEarlierSessionsOnlyAndSkipsDaysOutsideTheWindow()
    {
        var rules = new DailyScreenRules(5m, DailyScreenPriceBasis.SessionOpen, 3, MinAverageShareVolume: 1_000m, MinHistorySessions: 3);
        var start = new DateTimeOffset(2026, 5, 1, 4, 0, 0, TimeSpan.Zero);
        // Volume is too low until a spike on day 4; that spike may only count from day 5 onward.
        var series = Enumerable.Range(0, 7).Select(index => new OhlcvBar("ABC", start.AddDays(index), "1Day", 10m, 11m, 9m, 10m,
            index == 4 ? 10_000m : 100m)).ToArray();

        var dates = TradingFlow.Cli.DailyScreenCommand.Screen("ABC", series, [], rules, new DateOnly(2026, 5, 2), new DateOnly(2026, 5, 6))
            .Select(item => item.Date).ToArray();

        Assert.Equal(new[] { new DateOnly(2026, 5, 6) }, dates);
    }
}
