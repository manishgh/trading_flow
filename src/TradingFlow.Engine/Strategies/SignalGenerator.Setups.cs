using System;
using System.Collections.Generic;
using System.Linq;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Catalysts;
using TradingFlow.Engine.Market;

namespace TradingFlow.Engine.Strategies;

// Swing setup and trigger detectors for SignalGenerator.
public sealed partial class SignalGenerator
{
    private static (decimal? PriceVsLowPct, decimal? PriceVsHighPct) Get52WeekPriceContext(
        IReadOnlyList<OhlcvBar> bars,
        int index)
    {
        const int tradingSessions = 252;
        if (index < tradingSessions - 1)
        {
            return (null, null);
        }

        var window = bars
            .Skip(index - tradingSessions + 1)
            .Take(tradingSessions)
            .ToArray();
        var high = window.Max(bar => bar.High);
        var low = window.Min(bar => bar.Low);
        var price = bars[index].Close;

        return (
            low <= 0m ? null : ((price / low) - 1m) * 100m,
            high <= 0m ? null : ((price / high) - 1m) * 100m);
    }

    // Archetype C (doctrine §6C): the L4 trigger is today's close back above the prior day's high,
    // and the L3 setup is an oversold "stretch" (consecutive down closes, RSI(2) oversold, or a close
    // below the lower Bollinger band) within the lookback ending at the prior bar. StretchLow is the
    // lowest low across the stretch (and the reclaim bar) and feeds the swing-low stop.
    private static (bool IsReclaim, decimal? StretchLow) GetMeanReversionReclaimContext(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var rules = strategy.EntryRules;
        if (index <= 0 || bars[index].Close <= bars[index - 1].High)
        {
            return (false, null);
        }

        var start = Math.Max(1, index - Math.Max(1, rules.ReversionStretchLookbackBars));
        var stretchFound = false;
        decimal? stretchLow = null;
        for (var j = start; j <= index - 1; j++)
        {
            var isStretch =
                (rules.MinConsecutiveDownClosesForStretch > 0 &&
                    CountConsecutiveDownCloses(bars, j) >= rules.MinConsecutiveDownClosesForStretch) ||
                (rules.MaxReversionRsi2 is { } maxRsi2 && snapshots[j].Rsi2 is { } rsi2 && rsi2 < maxRsi2) ||
                (rules.EnableLowerBollingerStretch && snapshots[j].BollingerLower is { } lower && bars[j].Close < lower);
            if (isStretch)
            {
                stretchFound = true;
                stretchLow = stretchLow is { } low ? Math.Min(low, bars[j].Low) : bars[j].Low;
            }
        }

        if (!stretchFound)
        {
            return (false, null);
        }

        stretchLow = stretchLow is { } finalLow ? Math.Min(finalLow, bars[index].Low) : bars[index].Low;
        return (true, stretchLow);
    }

    private static int CountConsecutiveDownCloses(IReadOnlyList<OhlcvBar> bars, int index)
    {
        var count = 0;
        for (var j = index; j >= 1 && bars[j].Close < bars[j - 1].Close; j--)
        {
            count++;
        }

        return count;
    }

    private static (decimal? AgeHours, decimal? PriceMovePct, int? AgeBars) GetCatalystContext(
        CatalystEvent? catalyst,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        if (catalyst is null)
        {
            return (null, null, null);
        }

        var ageHours = (decimal)Math.Abs((snapshots[index].Timestamp - catalyst.Timestamp).TotalHours);
        var ageBars = bars
            .Take(index + 1)
            .Count(bar => bar.Timestamp > catalyst.Timestamp);
        var catalystBar = bars
            .Take(index + 1)
            .LastOrDefault(bar => bar.Timestamp <= catalyst.Timestamp);
        if (catalystBar is null || catalystBar.Close <= 0m)
        {
            return (ageHours, null, ageBars);
        }

        var priceMovePct = ((snapshots[index].CurrentPrice / catalystBar.Close) - 1m) * 100m;
        return (ageHours, priceMovePct, ageBars);
    }

}
