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

    // Archetype C / Rubber-Band Reclaim: the L4 trigger is today's close back above the prior day's high,
    // and the L3 setup is an oversold "stretch" within the lookback ending at the prior bar. When
    // RequireCombinedBollingerAndRsi2Stretch is true, at least one bar in the lookback must satisfy BOTH
    // Close < BollingerLower AND Rsi2 < MaxReversionRsi2; otherwise any enabled stretch condition qualifies.
    // StretchLow is the lowest low across the stretch (and the reclaim bar) and feeds the swing-low stop.
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
            bool isStretch;
            if (rules.RequireCombinedBollingerAndRsi2Stretch)
            {
                isStretch =
                    rules.EnableLowerBollingerStretch &&
                    snapshots[j].BollingerLower is { } combinedLower &&
                    bars[j].Close < combinedLower &&
                    rules.MaxReversionRsi2 is { } combinedMaxRsi2 &&
                    snapshots[j].Rsi2 is { } combinedRsi2 &&
                    combinedRsi2 < combinedMaxRsi2;
            }
            else
            {
                isStretch =
                    (rules.MinConsecutiveDownClosesForStretch > 0 &&
                        CountConsecutiveDownCloses(bars, j) >= rules.MinConsecutiveDownClosesForStretch) ||
                    (rules.MaxReversionRsi2 is { } maxRsi2 && snapshots[j].Rsi2 is { } rsi2 && rsi2 < maxRsi2) ||
                    (rules.EnableLowerBollingerStretch && snapshots[j].BollingerLower is { } lower && bars[j].Close < lower);
            }

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

    /// <summary>
    /// Evaluates the 21-EMA trend pullback reclaim setup on completed bar D (<paramref name="index"/>):
    /// bar D must close above EMA(21)_D and above High_{D-1}; within the prior <c>PullbackLookbackBars</c> completed bars
    /// at least one bar k must pull into within <c>MaxEma21PullbackAtr * ATR_k</c> of EMA(21)_k on dry-up relative volume
    /// (<c>RelativeVolume_k &lt; MaxPullbackRelativeVolume</c>); and when <c>RequirePullbackLowAboveSma50</c> is true,
    /// every bar from k through D must keep <c>Low &gt;= SMA(50)</c>. Missing indicators or RVOL fail closed with a
    /// concrete diagnostic reason.
    /// </summary>
    internal static (bool IsReclaim, string? RejectionDetail) GetEma21PullbackReclaimContext(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var rules = strategy.EntryRules;
        if (index < 1 ||
            rules.PullbackLookbackBars < 1 ||
            rules.MaxEma21PullbackAtr is not { } maxAtr || maxAtr <= 0m ||
            rules.MaxPullbackRelativeVolume is not { } maxRvol || maxRvol <= 0m)
        {
            return (false, "invalid_pullback_configuration");
        }

        if (snapshots[index].Ema21 is not { } triggerEma21)
        {
            return (false, "ema21_unavailable_on_trigger_bar");
        }

        if (bars[index].Close <= triggerEma21)
        {
            return (false, "trigger_close_not_above_ema21");
        }

        if (bars[index].Close <= bars[index - 1].High)
        {
            return (false, "trigger_close_not_above_prior_high");
        }

        if (rules.RequirePullbackLowAboveSma50)
        {
            if (snapshots[index].Sma50 is not { } triggerSma50)
            {
                return (false, "sma50_unavailable_on_trigger_bar");
            }

            if (bars[index].Low < triggerSma50)
            {
                return (false, "trigger_low_below_sma50");
            }
        }

        var start = Math.Max(0, index - rules.PullbackLookbackBars);
        var sawZoneTouch = false;
        var sawDryVolume = false;
        var sawSma50Failure = false;
        var sawMissingIndicators = false;

        for (var k = start; k <= index - 1; k++)
        {
            var snapK = snapshots[k];
            var barK = bars[k];
            if (snapK.Ema21 is not { } ema21K || snapK.Atr is not { } atrK || atrK <= 0m)
            {
                sawMissingIndicators = true;
                continue;
            }

            var band = maxAtr * atrK;
            var inZone = barK.Low <= ema21K + band && barK.Close >= ema21K - band;
            if (!inZone)
            {
                continue;
            }

            sawZoneTouch = true;
            if (snapK.RelativeVolume is not { } rvolK)
            {
                sawMissingIndicators = true;
                continue;
            }

            if (rvolK >= maxRvol)
            {
                continue;
            }

            sawDryVolume = true;
            if (rules.RequirePullbackLowAboveSma50 && !AllLowsAtOrAboveSma50(bars, snapshots, k, index))
            {
                sawSma50Failure = true;
                continue;
            }

            return (true, null);
        }

        var detail = sawSma50Failure
            ? "pullback_low_below_sma50_or_sma50_missing"
            : sawDryVolume
                ? "pullback_conditions_not_met"
                : sawZoneTouch
                    ? (sawMissingIndicators ? "pullback_rvol_unavailable" : "pullback_volume_not_dried_up")
                    : (sawMissingIndicators ? "pullback_indicators_unavailable" : "no_ema21_zone_pullback_in_lookback");
        return (false, detail);
    }

    /// <summary>Verifies that every bar in [<paramref name="fromIndex"/>, <paramref name="toIndex"/>] has a known SMA(50) and Low &gt;= SMA(50).</summary>
    private static bool AllLowsAtOrAboveSma50(
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int fromIndex,
        int toIndex)
    {
        for (var m = fromIndex; m <= toIndex; m++)
        {
            if (snapshots[m].Sma50 is not { } sma50 || bars[m].Low < sma50)
            {
                return false;
            }
        }

        return true;
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
