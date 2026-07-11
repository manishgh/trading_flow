using System;
using System.Collections.Generic;
using System.Linq;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

// Anchored-VWAP computation for SignalGenerator (partial split).
public sealed partial class SignalGenerator
{
    private static decimal? ComputePrimaryAnchoredVwap(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        int index)
    {
        if (!strategy.EntryRules.AnchoredVwapMode.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return ComputeAnchoredVwap(strategy.EntryRules.AnchoredVwapMode, strategy.EntryRules.AnchoredVwapLookbackBars, bars, index);
        }

        return String.IsNullOrWhiteSpace(strategy.EntryRules.AnchorType)
            ? null
            : ComputeAnchoredVwapFromAnchorType(strategy.EntryRules.AnchorType!, strategy.EntryRules.AnchoredVwapLookbackBars, bars, index);
    }

    private static decimal? ComputeAnchoredVwap(
        string mode,
        int lookbackBars,
        IReadOnlyList<OhlcvBar> bars,
        int index)
    {
        if (mode.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var lookback = Math.Max(1, lookbackBars);
        var start = Math.Max(0, index - lookback + 1);
        var anchorIndex = mode.ToLowerInvariant() switch
        {
            "lookback_low" => Enumerable.Range(start, index - start + 1).MinBy(i => bars[i].Low),
            "lookback_high" => Enumerable.Range(start, index - start + 1).MaxBy(i => bars[i].High),
            _ => throw new NotSupportedException($"Unsupported anchored_vwap_mode: {mode}.")
        };

        decimal cumulativePriceVolume = 0m;
        decimal cumulativeVolume = 0m;
        for (var i = anchorIndex; i <= index; i++)
        {
            var typicalPrice = (bars[i].High + bars[i].Low + bars[i].Close) / 3m;
            cumulativePriceVolume += typicalPrice * bars[i].Volume;
            cumulativeVolume += bars[i].Volume;
        }

        return cumulativeVolume <= 0m ? null : cumulativePriceVolume / cumulativeVolume;
    }

    private static decimal? ComputeAnchoredVwapFromAnchorType(
        string anchorType,
        int lookbackBars,
        IReadOnlyList<OhlcvBar> bars,
        int index)
    {
        var lookback = Math.Max(2, lookbackBars);
        var start = Math.Max(0, index - lookback + 1);
        var anchorIndex = anchorType.ToLowerInvariant() switch
        {
            "recent_gap_or_high_volume_node" => FindRecentGapOrHighVolumeAnchor(bars, start, index),
            "high_volume_node" => Enumerable.Range(start, index - start + 1).MaxBy(i => bars[i].Volume),
            "recent_gap" => FindRecentGapAnchor(bars, start, index),
            _ => throw new NotSupportedException($"Unsupported anchor_type: {anchorType}.")
        };

        decimal cumulativePriceVolume = 0m;
        decimal cumulativeVolume = 0m;
        for (var i = anchorIndex; i <= index; i++)
        {
            var typicalPrice = (bars[i].High + bars[i].Low + bars[i].Close) / 3m;
            cumulativePriceVolume += typicalPrice * bars[i].Volume;
            cumulativeVolume += bars[i].Volume;
        }

        return cumulativeVolume <= 0m ? null : cumulativePriceVolume / cumulativeVolume;
    }

    private static int FindRecentGapOrHighVolumeAnchor(IReadOnlyList<OhlcvBar> bars, int start, int index)
    {
        var gapAnchor = FindRecentGapAnchor(bars, start, index);
        var volumeAnchor = Enumerable.Range(start, index - start + 1).MaxBy(i => bars[i].Volume);
        return Math.Max(gapAnchor, volumeAnchor);
    }

    private static int FindRecentGapAnchor(IReadOnlyList<OhlcvBar> bars, int start, int index)
    {
        var bestIndex = start;
        var bestGap = 0m;
        for (var i = Math.Max(start + 1, 1); i <= index; i++)
        {
            if (bars[i - 1].Close <= 0m)
            {
                continue;
            }

            var gapPct = Math.Abs((bars[i].Open / bars[i - 1].Close) - 1m) * 100m;
            if (gapPct >= bestGap)
            {
                bestGap = gapPct;
                bestIndex = i;
            }
        }

        return bestIndex;
    }
}
