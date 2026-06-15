using System;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using Xunit;

namespace TradingFlow.Tests;

public sealed class TechnicalExecutionEngineTests
{
    [Fact]
    public void GetTechnicalExitReason_WhenLogPriceFadesWithRisingVolume_ReturnsLogFadeExit()
    {
        var strategy = CreateStrategy();
        var snapshot = new IndicatorSnapshot(
            "TEST",
            DateTimeOffset.UtcNow,
            "5m",
            CurrentPrice: 10m,
            CurrentVolume: 1000m,
            Vwap: 9m,
            Rsi: 55m,
            Atr: 0.2m,
            Ema20: 9m,
            Ema50: 8m,
            Ema200: 7m,
            BollingerMiddle: 9m,
            BollingerUpper: 11m,
            BollingerLower: 8m,
            RelativeVolume: 1.2m,
            MacdLine: 0.1m,
            MacdSignal: 0.05m,
            MacdHistogram: 0.01m);

        var reason = new TechnicalExecutionEngine().GetTechnicalExitReason(
            strategy,
            snapshot,
            barsHeld: 6,
            priceLogSlope: -0.0002m,
            volumeLogSlope: 0.004m);

        Assert.Equal("technical_exit_log_price_fade", reason);
    }

    [Fact]
    public void GetTechnicalExitReason_WhenBelowVwapIsRequired_DoesNotExitOnHealthyPullbackAboveVwap()
    {
        var strategy = CreateStrategy(requireBelowVwap: true);
        var snapshot = CreateSnapshot(currentPrice: 10m, vwap: 9m);

        var reason = new TechnicalExecutionEngine().GetTechnicalExitReason(
            strategy,
            snapshot,
            barsHeld: 6,
            priceLogSlope: -0.0002m,
            volumeLogSlope: 0.004m);

        Assert.Null(reason);
    }

    [Fact]
    public void GetTechnicalExitReason_WhenBelowVwapIsRequired_ExitsAfterStructureBreak()
    {
        var strategy = CreateStrategy(requireBelowVwap: true);
        var snapshot = CreateSnapshot(currentPrice: 10m, vwap: 11m);

        var reason = new TechnicalExecutionEngine().GetTechnicalExitReason(
            strategy,
            snapshot,
            barsHeld: 6,
            priceLogSlope: -0.0002m,
            volumeLogSlope: 0.004m);

        Assert.Equal("technical_exit_log_price_fade", reason);
    }

    [Fact]
    public void GetTechnicalExitReason_WhenSma10NearSma20_ReturnsNearExit()
    {
        var strategy = CreateStrategy(exitOnSmaNear: true, smaNearPct: 0.50m);
        var snapshot = CreateSnapshot(currentPrice: 10m, vwap: 9m) with
        {
            Sma10 = 10.02m,
            Sma20 = 10.00m
        };

        var reason = new TechnicalExecutionEngine().GetTechnicalExitReason(strategy, snapshot, barsHeld: 6);

        Assert.Equal("technical_exit_sma10_near_sma20", reason);
    }

    [Fact]
    public void GetTechnicalExitReason_WhenSma10CrossesBelowSma20_ReturnsCrossExit()
    {
        var strategy = CreateStrategy(exitOnSmaCrossDown: true);
        var previous = CreateSnapshot(currentPrice: 11m, vwap: 9m) with
        {
            Sma10 = 10.10m,
            Sma20 = 10.00m
        };
        var current = CreateSnapshot(currentPrice: 9.9m, vwap: 9m) with
        {
            Sma10 = 9.95m,
            Sma20 = 10.00m
        };

        var reason = new TechnicalExecutionEngine().GetTechnicalExitReason(
            strategy,
            current,
            barsHeld: 6,
            previousSnapshot: previous);

        Assert.Equal("technical_exit_sma10_cross_below_sma20", reason);
    }

    [Fact]
    public void GetTechnicalExitReason_WhenSma10AlreadyBelowSma20_DoesNotReturnCrossExit()
    {
        var strategy = CreateStrategy(exitOnSmaCrossDown: true);
        var previous = CreateSnapshot(currentPrice: 9.8m, vwap: 9m) with
        {
            Sma10 = 9.90m,
            Sma20 = 10.00m
        };
        var current = CreateSnapshot(currentPrice: 9.7m, vwap: 9m) with
        {
            Sma10 = 9.80m,
            Sma20 = 10.00m
        };

        var reason = new TechnicalExecutionEngine().GetTechnicalExitReason(
            strategy,
            current,
            barsHeld: 6,
            previousSnapshot: previous);

        Assert.Null(reason);
    }

    [Fact]
    public void GetTechnicalExitReason_WhenConfirmedVwapExitIsEnabled_DoesNotExitOnSingleVwapLoss()
    {
        var strategy = CreateStrategy(exitOnCloseBelowVwap: true, enableConfirmedVwapExit: true);
        var snapshot = CreateSnapshot(currentPrice: 9.95m, vwap: 10m);

        var reason = new TechnicalExecutionEngine().GetTechnicalExitReason(strategy, snapshot, barsHeld: 6);

        Assert.Null(reason);
    }

    [Fact]
    public void ShouldExitLongOnConfirmedVwapFailure_WhenConsecutiveBufferedFailures_ReturnsTrue()
    {
        var strategy = CreateStrategy(enableConfirmedVwapExit: true);
        var snapshots = new[]
        {
            CreateSnapshot(currentPrice: 10.20m, vwap: 10.00m) with { Atr = 1.0m },
            CreateSnapshot(currentPrice: 9.80m, vwap: 10.00m) with { Atr = 1.0m },
            CreateSnapshot(currentPrice: 9.82m, vwap: 10.00m) with { Atr = 1.0m }
        };

        var shouldExit = new TechnicalExecutionEngine().ShouldExitLongOnConfirmedVwapFailure(
            strategy,
            snapshots,
            index: 2,
            entryIndex: 0,
            entryPrice: 10.0m,
            stopDistance: 1.0m,
            highestHighSinceEntry: 10.5m,
            barsHeld: 6);

        Assert.True(shouldExit);
    }

    [Fact]
    public void GetTechnicalExitReason_WhenEma10CrossesBelowEma20_ReturnsStructuralExit()
    {
        var strategy = CreateStrategy(exitOnEmaCrossDown: true);
        var previous = CreateSnapshot(currentPrice: 10.4m, vwap: 9m) with
        {
            Ema10 = 10.20m,
            Ema20 = 10.00m
        };
        var current = CreateSnapshot(currentPrice: 9.8m, vwap: 9m) with
        {
            Ema10 = 9.95m,
            Ema20 = 10.00m
        };

        var reason = new TechnicalExecutionEngine().GetTechnicalExitReason(
            strategy,
            current,
            barsHeld: 6,
            previousSnapshot: previous);

        Assert.Equal("technical_exit_ema10_cross_below_ema20", reason);
    }

    private static IndicatorSnapshot CreateSnapshot(decimal currentPrice, decimal vwap)
    {
        return new IndicatorSnapshot(
            "TEST",
            DateTimeOffset.UtcNow,
            "5m",
            CurrentPrice: currentPrice,
            CurrentVolume: 1000m,
            Vwap: vwap,
            Rsi: 55m,
            Atr: 0.2m,
            Ema20: 9m,
            Ema50: 8m,
            Ema200: 7m,
            BollingerMiddle: 9m,
            BollingerUpper: 11m,
            BollingerLower: 8m,
            RelativeVolume: 1.2m,
            MacdLine: 0.1m,
            MacdSignal: 0.05m,
            MacdHistogram: 0.01m);
    }

    private static StrategyDefinition CreateStrategy(
        bool requireBelowVwap = false,
        bool exitOnSmaNear = false,
        decimal smaNearPct = 0.25m,
        bool exitOnSmaCrossDown = false,
        bool exitOnCloseBelowVwap = false,
        bool enableConfirmedVwapExit = false,
        bool exitOnEmaCrossDown = false)
    {
        return new StrategyDefinition(
            "test",
            "Test",
            "test",
            1,
            "5m",
            "long",
            EntryRules: null!,
            Confluence: null!,
            ExitRules: new ExitRules(
                StopAtrMultiple: 2.0m,
                TargetRMultiple: 3.0m,
                MaxHoldHours: 6.5m,
                EnableAtrTrailingStop: true,
                TrailingStopAtrMultiple: 2.0m,
                TrailingActivationR: 1.0m,
                ExitOnCloseBelowEma20: false,
                ExitOnCloseBelowVwap: exitOnCloseBelowVwap,
                ExitOnMacdHistogramNegative: true,
                MinHoldBarsBeforeTechnicalExit: 6,
                ExitOnLogPriceFade: true,
                ExitLogPriceLookbackBars: 6,
                ExitLogVolumeLookbackBars: 6,
                MaxExitLogPriceSlope: -0.0001m,
                MinExitLogVolumeSlope: 0.003m,
                RequireRisingVolumeForLogFadeExit: true,
                RequireBelowVwapForLogFadeExit: requireBelowVwap,
                EnableConfirmedVwapExit: enableConfirmedVwapExit,
                ConfirmedVwapExitBars: 2,
                ConfirmedVwapExitAtrBuffer: 0.10m,
                ExitOnSma10NearSma20: exitOnSmaNear,
                Sma10NearSma20Pct: smaNearPct,
                ExitOnSma10CrossBelowSma20: exitOnSmaCrossDown,
                ExitOnEma10CrossBelowEma20: exitOnEmaCrossDown),
            Execution: null!,
            Session: null!);
    }
}
