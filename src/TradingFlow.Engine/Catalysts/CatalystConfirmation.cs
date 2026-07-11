using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Catalysts;

/// <summary>
/// Technical confirmation for the catalyst-drift entry (doctrine §6B / edge-recovery Phase 1.3). Inside a
/// catalyst's bounded confirmation window, the drift is "confirmed" when momentum turns up on the bar:
/// either EMA10 crosses above EMA20, or the MACD histogram flips positive with volume expansion vs the
/// prior bar. Pure and stateless — the runner composes it with CatalystEligibilityService, which supplies
/// the window (first candle strictly after received time) and the one-attempt-per-catalyst consumption.
/// </summary>
public static class CatalystConfirmation
{
    public static bool HasTechnicalConfirmation(IndicatorSnapshot current, IndicatorSnapshot? previous)
    {
        if (previous is null)
        {
            return false;
        }

        var emaCrossedUp =
            current.Ema10 is { } ema10 && current.Ema20 is { } ema20 &&
            previous.Ema10 is { } priorEma10 && previous.Ema20 is { } priorEma20 &&
            priorEma10 <= priorEma20 && ema10 > ema20;

        var macdTurnedPositive =
            current.MacdHistogram is { } histogram && previous.MacdHistogram is { } priorHistogram &&
            priorHistogram <= 0m && histogram > 0m;

        var volumeExpanded = current.CurrentVolume > previous.CurrentVolume;

        return emaCrossedUp || (macdTurnedPositive && volumeExpanded);
    }
}
