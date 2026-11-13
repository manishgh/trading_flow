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

    // A 200-session history minimum (the Connors screen) passes once 200 earlier sessions exist; the screen once read only
    // 100 earlier sessions, so such a minimum never passed and the published screen was empty.
    [Fact]
    public void Screen_ReadsAsManyEarlierSessionsAsTheHistoryMinimumNeeds()
    {
        var rules = new DailyScreenRules(5m, DailyScreenPriceBasis.PriorClose, 20, MinAverageShareVolume: 1_000m, MinHistorySessions: 200);
        var start = new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        var series = Enumerable.Range(0, 202).Select(index => new OhlcvBar("ABC", start.AddDays(index), "1Day", 10m, 11m, 9m, 10m, 10_000m)).ToArray();
        var first = DateOnly.FromDateTime(series[0].Timestamp.UtcDateTime);

        var screened = TradingFlow.Cli.DailyScreenCommand.Screen("ABC", series, [], rules, first, first.AddDays(300)).ToArray();

        Assert.Equal(new[] { DateOnly.FromDateTime(series[200].Timestamp.UtcDateTime), DateOnly.FromDateTime(series[201].Timestamp.UtcDateTime) },
            screened.Select(item => item.Date));
        Assert.All(screened, item => Assert.Equal(200, item.Values.HistorySessions));
    }

    // A liquidity tier keeps each session's stocks ranked within the tier by average dollar volume (highest first, ties by
    // symbol), rewrites eligible.csv and recounts each session.
    [Fact]
    public async Task LiquidityTier_KeepsEachSessionsRankedStocksOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), "tf-tier-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            await File.WriteAllLinesAsync(path, [
                "session_date,ticker,price,average_volume,average_dollar_volume,atr,history_sessions",
                "2026-01-05,AAA,10,1,300,,200", "2026-01-05,BBB,10,1,100,,200", "2026-01-05,CCC,10,1,200,,200",
                "2026-01-06,AAA,10,1,50,,200", "2026-01-06,BBB,10,1,90,,200", "2026-01-06,CCC,10,1,90,,200"]);

            var counts = await TradingFlow.Cli.DailyScreenCommand.ApplyLiquidityTierAsync(path, TradingFlow.Cli.DailyScreenCommand.ParseTier("2-3"), default);

            var kept = (await File.ReadAllLinesAsync(path)).Skip(1).Select(line => line[..14]).ToArray();
            // Day 1 ranks AAA, CCC, BBB -> keeps CCC, BBB; day 2 ranks BBB, CCC (tie by symbol), AAA -> keeps CCC, AAA.
            Assert.Equal(new[] { "2026-01-05,BBB", "2026-01-05,CCC", "2026-01-06,AAA", "2026-01-06,CCC" }, kept);
            Assert.Equal(new[] { 2, 2 }, counts.Values);
            Assert.Throws<ArgumentException>(() => TradingFlow.Cli.DailyScreenCommand.ParseTier("5-1"));
        }
        finally { File.Delete(path); }
    }

    // The complete-stocks rule keeps a stock only with a bar on every required session and no split or suspected split in
    // that span; each failure names its first reason.
    [Fact]
    public void IncompleteReason_KeepsOnlyStocksWithEveryRequiredSessionAndNoSplit()
    {
        var days = Enumerable.Range(0, 5).Select(index => new DateOnly(2026, 1, 5).AddDays(index)).ToArray();
        var required = new SortedSet<DateOnly>(days);

        Assert.Null(TradingFlow.Cli.DailyScreenCommand.IncompleteReason(days, required, [], []));
        Assert.Equal("missing_sessions_1", TradingFlow.Cli.DailyScreenCommand.IncompleteReason(days.Where(day => day != days[2]).ToArray(), required, [], []));
        Assert.Equal("split_recorded", TradingFlow.Cli.DailyScreenCommand.IncompleteReason(days, required, [new ShareSplit(days[3], 2m)], []));
        Assert.Equal("suspected_unrecorded_split", TradingFlow.Cli.DailyScreenCommand.IncompleteReason(days, required, [], [days[1]]));
        // A split before the required span does not count.
        Assert.Null(TradingFlow.Cli.DailyScreenCommand.IncompleteReason(days, required, [new ShareSplit(days[0].AddDays(-30), 2m)], []));
    }

    // A suspected unrecorded split is listed with its overnight move and the mean volume on each side, so a 1-for-10 reverse
    // split (price x10, volume /10) can be told apart from a real move afterwards.
    [Fact]
    public void SuspectedSplitEvents_RecordTheMoveAndVolumeOnEachSide()
    {
        var start = new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        var series = Enumerable.Range(0, 6).Select(index => index < 3
            ? new OhlcvBar("ABC", start.AddDays(index), "1Day", 1m, 1m, 1m, 1m, 1_000m)
            : new OhlcvBar("ABC", start.AddDays(index), "1Day", 10m, 10m, 10m, 10m, 100m)).ToArray();

        var item = Assert.Single(TradingFlow.Cli.DailyScreenCommand.SuspectedSplitEvents(series, []));

        Assert.Equal(DateOnly.FromDateTime(series[3].Timestamp.UtcDateTime), item.Session);
        Assert.Equal(10m, item.Move);
        Assert.Equal(1_000m, item.VolumeBefore);
        Assert.Equal(100m, item.VolumeAfter);
    }
}
