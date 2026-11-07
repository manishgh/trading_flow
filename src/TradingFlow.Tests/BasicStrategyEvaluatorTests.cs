using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class BasicStrategyEvaluatorTests
{
    [Fact]
    public void GetLongEntryRejection_Rsi2Setup_UsesDedicatedCompletedDailySignal()
    {
        var signal = CreateSignal() with { IsConnorsRsi2Oversold = true, CurrentRsi2 = 3m };

        var rejection = new BasicStrategyEvaluator().GetLongEntryRejection(
            CreateStrategy("rsi2_oversold"),
            signal,
            1m);

        Assert.Null(rejection);
    }

    // No swing short setup remains, so a daily strategy that enables shorts fails closed instead of silently trading.
    [Fact]
    public void GetShortEntryRejection_RemovedSwingShortSetup_FailsClosed()
    {
        var strategy = CreateStrategy("momentum") with
        {
            Direction = "short",
            EntryRules = CreateEntryRules("momentum") with { EnableShort = true, ShortSetupType = "swing_rollover" }
        };

        Assert.Throws<NotSupportedException>(() =>
            new BasicStrategyEvaluator().GetShortEntryRejection(strategy, CreateSignal(), 1m));
    }

    [Fact]
    public void UnsupportedRemovedSetupName_FailsClosed()
    {
        var strategy = CreateStrategy("removed_setup");

        Assert.Throws<NotSupportedException>(() =>
            new BasicStrategyEvaluator().GetLongEntryRejection(strategy, CreateSignal(), 1m));
    }

    private static StrategyDefinition CreateStrategy(string setupType) => new(
        "test.swing.v1",
        "Test Swing",
        "test",
        1,
        "1d",
        "long",
        CreateEntryRules(setupType),
        new ConfluenceRules(false, "1h", 20, "none"),
        new ExitRules(1.5m, 2m, 240m, true, 2m, 1m, false, false, false, 1),
        new ExecutionRules("1h", 5m),
        new SessionRules("America/New_York", 0, 0, 0));

    private static EntryRules CreateEntryRules(string setupType) => new(
        SetupType: setupType,
        MinVolumeSpike: 0m,
        MinEntryRsi: 0m,
        MaxEntryRsi: 100m,
        TrendFilter: "none",
        MacdFilter: "none",
        RequirePriceAboveBollingerMiddle: false,
        RequireMacdHistogramPositive: false,
        RequirePriceAboveVwap: false,
        RequirePriceAboveEma20: false,
        RequirePriceAboveEma50: false,
        RequireEma20AboveEma50: false,
        MaxVwapExtensionAtr: null,
        RecentHighLookbackBars: 20,
        VolatilityContractionLookbackBars: 20);

    private static TradeSignal CreateSignal() => new(
        Ticker: "TEST",
        Timestamp: new DateTimeOffset(2026, 1, 5, 21, 0, 0, TimeSpan.Zero),
        Timeframe: "1d",
        CurrentPrice: 100m,
        CurrentVolume: 1_000_000m,
        CurrentRsi: 50m,
        CurrentAtr: 2m,
        IsAboveVwap: true,
        IsVwapPullback: false,
        IsVwapReclaim: false,
        IsVwapRejection: false,
        IsEma20Pullback: false,
        IsRecentHighBreakout: false,
        IsRecentLowBreakdown: false,
        IsVolatilityContraction: false,
        IsPriceAboveEma20: true,
        IsPriceAboveEma50: true,
        IsEma20AboveEma50: true,
        VwapExtensionAtr: 0m,
        IsAboveBollingerMiddle: true,
        IsMacdHistogramPositive: true,
        IsMacdNotBearish: true);
}
