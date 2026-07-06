using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class PositionGuardianEngineTests
{
    [Fact]
    public void EvaluateLong_WhenRunnerMovesInFavor_RaisesEffectiveStopWithoutExit()
    {
        var strategy = LoadStrategyWithTrailingStop();
        var entryTime = new DateTimeOffset(2026, 6, 16, 13, 30, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 12)
            .Select(i => new OhlcvBar(
                "RGNT",
                entryTime.AddMinutes(i),
                "1m",
                100m + i,
                101m + i,
                99.50m + i,
                100.75m + i,
                100_000m + (i * 10_000m)))
            .ToArray();
        var snapshots = bars
            .Select(bar => CreateSnapshot(bar, atr: 1m, vwap: 98m, ema10: 99m, ema20: 98.50m, macdHistogram: 0.10m))
            .ToArray();

        var decision = new PositionGuardianEngine().EvaluateLong(
            strategy,
            bars,
            snapshots,
            entryTime,
            entryPrice: 100m,
            initialStopLossPrice: 98m,
            takeProfitPrice: 140m);

        Assert.False(decision.ShouldExit);
        Assert.Equal("strategy_still_valid", decision.Reason);
        Assert.True(decision.CurrentStopLossPrice > 98m);
    }

    [Fact]
    public void EvaluateLong_WhenConfirmedVwapFailureOccurs_ExitsAfterConfirmation()
    {
        var strategy = LoadStrategyWithConfirmedVwapExit();
        var entryTime = new DateTimeOffset(2026, 6, 16, 13, 30, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 9)
            .Select(i =>
            {
                var close = i < 6 ? 101m : 98m - i * 0.10m;
                return new OhlcvBar(
                    "TDIC",
                    entryTime.AddMinutes(i),
                    "1m",
                    close + 0.10m,
                    close + 0.25m,
                    close - 0.25m,
                    close,
                    150_000m);
            })
            .ToArray();
        var snapshots = bars
            .Select((bar, i) => CreateSnapshot(
                bar,
                atr: 1m,
                vwap: i < 6 ? 99m : 100m,
                ema10: 99m,
                ema20: 98.50m,
                macdHistogram: 0.05m))
            .ToArray();

        var decision = new PositionGuardianEngine().EvaluateLong(
            strategy,
            bars,
            snapshots,
            entryTime,
            entryPrice: 100m,
            initialStopLossPrice: 96m,
            takeProfitPrice: 140m);

        Assert.True(decision.ShouldExit);
        Assert.Equal("confirmed_vwap_failure", decision.Reason);
        Assert.NotNull(decision.ExitSignalTimestamp);
    }

    private static IndicatorSnapshot CreateSnapshot(
        OhlcvBar bar,
        decimal atr,
        decimal vwap,
        decimal ema10,
        decimal ema20,
        decimal macdHistogram)
    {
        return new IndicatorSnapshot(
            bar.Ticker,
            bar.Timestamp,
            bar.Timeframe,
            bar.Close,
            bar.Volume,
            vwap,
            55m,
            atr,
            ema20,
            97m,
            95m,
            98m,
            110m,
            90m,
            2m,
            0.10m,
            0.05m,
            macdHistogram,
            Ema10: ema10,
            SlotRelativeVolume: 2m,
            SessionRelativeVolume: 2m);
    }

    private static StrategyDefinition LoadStrategyWithTrailingStop()
    {
        var strategy = LoadBaseStrategy();
        return strategy with
        {
            ExitRules = strategy.ExitRules with
            {
                EnableAtrTrailingStop = true,
                TrailingStopAtrMultiple = 2.0m,
                TrailingActivationR = 1.0m,
                EnableConfirmedVwapExit = false
            }
        };
    }

    private static StrategyDefinition LoadStrategyWithConfirmedVwapExit()
    {
        var strategy = LoadBaseStrategy();
        return strategy with
        {
            ExitRules = strategy.ExitRules with
            {
                EnableAtrTrailingStop = false,
                EnableConfirmedVwapExit = true,
                ConfirmedVwapExitBars = 3,
                ConfirmedVwapExitAtrBuffer = 0.15m,
                DisableConfirmedVwapExitAfterR = 1.0m
            }
        };
    }

    private static StrategyDefinition LoadBaseStrategy()
    {
        var root = FindRepositoryRoot();
        return new SimpleYamlReader().ReadStrategy(Path.Combine(
            root,
            "configs",
            "strategies",
            "intraday-ema10-ema20-macd-volume.v2-additive.yaml"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradingFlow.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate TradingFlow repository root.");
    }
}


