using TradingFlow.Domain.Market;
using TradingFlow.Engine.Catalysts;

namespace TradingFlow.Tests;

public sealed class CatalystConfirmationTests
{
    [Fact]
    public void HasTechnicalConfirmation_WhenEma10CrossesAboveEma20_Confirms()
    {
        var previous = Snap(ema10: 9.9m, ema20: 10.0m, macdHist: -0.1m, volume: 1000m);
        var current = Snap(ema10: 10.1m, ema20: 10.0m, macdHist: -0.1m, volume: 900m);

        Assert.True(CatalystConfirmation.HasTechnicalConfirmation(current, previous));
    }

    [Fact]
    public void HasTechnicalConfirmation_WhenMacdTurnsPositiveWithVolumeExpansion_Confirms()
    {
        var previous = Snap(ema10: 9.0m, ema20: 10.0m, macdHist: -0.05m, volume: 1000m);
        var current = Snap(ema10: 9.2m, ema20: 10.0m, macdHist: 0.03m, volume: 1500m);

        Assert.True(CatalystConfirmation.HasTechnicalConfirmation(current, previous));
    }

    [Fact]
    public void HasTechnicalConfirmation_WhenMacdTurnsPositiveButVolumeFalls_DoesNotConfirm()
    {
        var previous = Snap(ema10: 9.0m, ema20: 10.0m, macdHist: -0.05m, volume: 1500m);
        var current = Snap(ema10: 9.2m, ema20: 10.0m, macdHist: 0.03m, volume: 1000m);

        Assert.False(CatalystConfirmation.HasTechnicalConfirmation(current, previous));
    }

    [Fact]
    public void HasTechnicalConfirmation_WhenNoCrossOrTurn_DoesNotConfirm()
    {
        var previous = Snap(ema10: 9.0m, ema20: 10.0m, macdHist: -0.1m, volume: 1000m);
        var current = Snap(ema10: 9.1m, ema20: 10.0m, macdHist: -0.08m, volume: 2000m);

        Assert.False(CatalystConfirmation.HasTechnicalConfirmation(current, previous));
    }

    [Fact]
    public void HasTechnicalConfirmation_WithNoPreviousBar_DoesNotConfirm()
    {
        var current = Snap(ema10: 10.1m, ema20: 10.0m, macdHist: 0.03m, volume: 1500m);

        Assert.False(CatalystConfirmation.HasTechnicalConfirmation(current, previous: null));
    }

    private static IndicatorSnapshot Snap(decimal ema10, decimal ema20, decimal macdHist, decimal volume)
    {
        return new IndicatorSnapshot(
            "T",
            DateTimeOffset.UtcNow,
            "1d",
            CurrentPrice: 10m,
            CurrentVolume: volume,
            Vwap: null,
            Rsi: null,
            Atr: null,
            Ema20: ema20,
            Ema50: null,
            Ema200: null,
            BollingerMiddle: null,
            BollingerUpper: null,
            BollingerLower: null,
            RelativeVolume: null,
            MacdLine: null,
            MacdSignal: null,
            MacdHistogram: macdHist,
            Ema10: ema10);
    }
}
