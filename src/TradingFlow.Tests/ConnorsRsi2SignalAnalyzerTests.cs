using TradingFlow.Domain.Market;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class ConnorsRsi2SignalAnalyzerTests
{
    private readonly ConnorsRsi2SignalAnalyzer analyzer = new();

    [Fact]
    public void AnalyzeCompletedDailyBar_SignalsOnOversoldBarWithoutReclaimDelay()
    {
        var bar = DailyBar(close: 105m, high: 106m);
        var snapshot = Snapshot(bar, sma200: 100m, rsi2: 4.99m);

        var result = analyzer.AnalyzeCompletedDailyBar(
            bar,
            snapshot,
            new ConnorsRsi2SignalOptions(OversoldThreshold: 5m));

        Assert.True(result.IsSignal);
        Assert.Null(result.RejectionReason);
        Assert.Equal(bar.Timestamp, result.CompletedBarTimestamp);
        Assert.Equal(4.99m, result.Rsi2);
    }

    [Fact]
    public void AnalyzeCompletedDailyBar_UsesStrictRsiThreshold()
    {
        var bar = DailyBar(close: 105m);
        var snapshot = Snapshot(bar, sma200: 100m, rsi2: 5m);

        var result = analyzer.AnalyzeCompletedDailyBar(
            bar,
            snapshot,
            new ConnorsRsi2SignalOptions(OversoldThreshold: 5m));

        Assert.False(result.IsSignal);
        Assert.Equal("rsi2_not_below_threshold", result.RejectionReason);
    }

    [Fact]
    public void AnalyzeCompletedDailyBar_RequiresCloseStrictlyAboveSma200()
    {
        var bar = DailyBar(close: 100m);
        var snapshot = Snapshot(bar, sma200: 100m, rsi2: 2m);

        var result = analyzer.AnalyzeCompletedDailyBar(
            bar,
            snapshot,
            new ConnorsRsi2SignalOptions());

        Assert.False(result.IsSignal);
        Assert.Equal("close_not_above_sma200", result.RejectionReason);
    }

    [Fact]
    public void AnalyzeCompletedDailyBar_FailsClosedWhenIndicatorsAreUnavailable()
    {
        var bar = DailyBar(close: 105m);

        var missingSma = analyzer.AnalyzeCompletedDailyBar(
            bar,
            Snapshot(bar, sma200: null, rsi2: 2m),
            new ConnorsRsi2SignalOptions());
        var missingRsi = analyzer.AnalyzeCompletedDailyBar(
            bar,
            Snapshot(bar, sma200: 100m, rsi2: null),
            new ConnorsRsi2SignalOptions());

        Assert.Equal("sma200_unavailable", missingSma.RejectionReason);
        Assert.Equal("rsi2_unavailable", missingRsi.RejectionReason);
    }

    [Fact]
    public void AnalyzeCompletedDailyBar_RejectsNonDailyInput()
    {
        var bar = DailyBar(close: 105m) with { Timeframe = "1h" };
        var snapshot = Snapshot(bar, sma200: 100m, rsi2: 2m);

        var result = analyzer.AnalyzeCompletedDailyBar(
            bar,
            snapshot,
            new ConnorsRsi2SignalOptions());

        Assert.False(result.IsSignal);
        Assert.Equal("daily_timeframe_required", result.RejectionReason);
    }

    private static OhlcvBar DailyBar(decimal close, decimal? high = null)
    {
        return new OhlcvBar(
            "TEST",
            DateTimeOffset.Parse("2026-05-14T20:00:00Z"),
            "1d",
            close + 1m,
            high ?? close + 2m,
            close - 2m,
            close,
            1_000_000m);
    }

    private static IndicatorSnapshot Snapshot(
        OhlcvBar bar,
        decimal? sma200,
        decimal? rsi2)
    {
        return new IndicatorSnapshot(
            bar.Ticker,
            bar.Timestamp,
            bar.Timeframe,
            bar.Close,
            bar.Volume,
            Vwap: null,
            Rsi: null,
            Atr: null,
            Ema20: null,
            Ema50: null,
            Ema200: null,
            BollingerMiddle: null,
            BollingerUpper: null,
            BollingerLower: null,
            RelativeVolume: null,
            MacdLine: null,
            MacdSignal: null,
            MacdHistogram: null,
            Sma200: sma200,
            Rsi2: rsi2);
    }
}
