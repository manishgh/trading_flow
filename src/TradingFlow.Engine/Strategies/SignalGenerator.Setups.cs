using System;
using System.Collections.Generic;
using System.Linq;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Catalysts;
using TradingFlow.Engine.Market;

namespace TradingFlow.Engine.Strategies;

// Setup/trigger detectors (breakout, reclaim, reversion, catalyst-drift, VCP, step, bull-flag, avwap) for SignalGenerator (partial split).
public sealed partial class SignalGenerator
{
    private static (
        bool IsBreakout,
        decimal? PriceVsLowPct,
        decimal? PriceVsHighPct,
        int? Contractions,
        bool IsVolatilityHalving,
        bool IsVolumeDryUp,
        decimal? BreakoutVolumeRatio) GetVcpContext(
            StrategyDefinition strategy,
            IReadOnlyList<OhlcvBar> bars,
            IReadOnlyList<IndicatorSnapshot> snapshots,
            int index)
    {
        var lookback = Math.Max(10, strategy.EntryRules.VolatilityContractionLookbackBars);
        if (index < lookback)
        {
            return (false, null, null, null, false, false, null);
        }

        var window = bars.Skip(index - lookback).Take(lookback).ToArray();
        var high = window.Max(x => x.High);
        var low = window.Min(x => x.Low);
        var price = bars[index].Close;
        var priceVsLowPct = low <= 0m ? (decimal?)null : ((price / low) - 1m) * 100m;
        var priceVsHighPct = high <= 0m ? (decimal?)null : ((price / high) - 1m) * 100m;
        var firstHalf = window.Take(window.Length / 2).ToArray();
        var secondHalf = window.Skip(window.Length / 2).ToArray();
        var firstRange = firstHalf.Max(x => x.High) - firstHalf.Min(x => x.Low);
        var secondRange = secondHalf.Max(x => x.High) - secondHalf.Min(x => x.Low);
        var isVolatilityHalving = firstRange > 0m && secondRange <= firstRange / 2m;
        var averageFirstVolume = firstHalf.Where(x => x.Volume > 0m).Select(x => x.Volume).DefaultIfEmpty(0m).Average();
        var averageSecondVolume = secondHalf.Where(x => x.Volume > 0m).Select(x => x.Volume).DefaultIfEmpty(0m).Average();
        var isVolumeDryUp = averageFirstVolume > 0m && averageSecondVolume <= averageFirstVolume * 0.70m;
        var breakoutBaseVolume = secondHalf.Where(x => x.Volume > 0m).Select(x => x.Volume).DefaultIfEmpty(0m).Average();
        var breakoutVolumeRatio = breakoutBaseVolume <= 0m ? (decimal?)null : bars[index].Volume / breakoutBaseVolume;
        var contractions = CountRangeContractions(window);
        var minContractions = strategy.EntryRules.MinContractions ?? 0;
        var maxContractions = strategy.EntryRules.MaxContractions ?? int.MaxValue;
        var minBreakoutVolumeRatio = strategy.EntryRules.MinBreakoutVolumeRatio ?? 0m;
        var isBreakout = bars[index].Close >= high &&
            contractions >= minContractions &&
            contractions <= maxContractions &&
            (!strategy.EntryRules.RequireVolatilityHalvingLeftToRight || isVolatilityHalving) &&
            (!strategy.EntryRules.RequireVolumeDryUpPreBreakout || isVolumeDryUp) &&
            (breakoutVolumeRatio is null || breakoutVolumeRatio.Value >= minBreakoutVolumeRatio);

        return (isBreakout, priceVsLowPct, priceVsHighPct, contractions, isVolatilityHalving, isVolumeDryUp, breakoutVolumeRatio);
    }

    private static int CountRangeContractions(IReadOnlyList<OhlcvBar> bars)
    {
        if (bars.Count < 4)
        {
            return 0;
        }

        var bucketSize = Math.Max(2, bars.Count / 4);
        var ranges = new List<decimal>();
        for (var i = 0; i < bars.Count; i += bucketSize)
        {
            var bucket = bars.Skip(i).Take(bucketSize).ToArray();
            if (bucket.Length == 0)
            {
                continue;
            }

            ranges.Add(bucket.Max(x => x.High) - bucket.Min(x => x.Low));
        }

        var contractions = 0;
        for (var i = 1; i < ranges.Count; i++)
        {
            if (ranges[i] < ranges[i - 1])
            {
                contractions++;
            }
        }

        return contractions;
    }

    private static (
        decimal? High,
        decimal? Low,
        decimal? Vwap,
        decimal? Volume,
        decimal? RunPct,
        decimal? VwapExtensionPct,
        bool IsHighBreak) GetPremarketContext(
            StrategyDefinition strategy,
            IReadOnlyList<OhlcvBar> bars,
            IReadOnlyList<IndicatorSnapshot> snapshots,
            int index)
    {
        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var premarketStart = currentExchangeTime.Date.Add(new TimeSpan(4, 0, 0));
        var regularOpen = currentExchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        var premarketBars = bars
            .Take(index + 1)
            .Where(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date == currentExchangeTime.Date &&
                    exchangeTime >= premarketStart &&
                    exchangeTime < regularOpen;
            })
            .ToArray();

        if (premarketBars.Length == 0)
        {
            return (null, null, null, null, null, null, false);
        }

        var high = premarketBars.Max(x => x.High);
        var low = premarketBars.Min(x => x.Low);
        var volume = premarketBars.Sum(x => x.Volume);
        var priceVolume = premarketBars.Sum(x => ((x.High + x.Low + x.Close) / 3m) * x.Volume);
        var vwap = volume <= 0m ? (decimal?)null : priceVolume / volume;
        var previousRegularClose = GetPreviousRegularClose(strategy, bars, snapshots, index);
        var runPct = previousRegularClose is null || previousRegularClose.Value <= 0m
            ? (decimal?)null
            : ((high / previousRegularClose.Value) - 1m) * 100m;
        var vwapExtensionPct = vwap is null || vwap.Value <= 0m
            ? (decimal?)null
            : ((snapshots[index].CurrentPrice / vwap.Value) - 1m) * 100m;
        var breakoutLevel = high * (1m + (strategy.EntryRules.PremarketHighBreakBufferPct / 100m));
        var isHighBreak = snapshots[index].CurrentPrice >= breakoutLevel;

        return (high, low, vwap, volume, runPct, vwapExtensionPct, isHighBreak);
    }

    private static int? GetMinutesAfterRegularOpen(StrategyDefinition strategy, DateTimeOffset timestamp)
    {
        var currentExchangeTime = ConvertToExchangeTime(timestamp, strategy.Session.ExchangeTimezone);
        var regularOpen = currentExchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        return currentExchangeTime < regularOpen
            ? null
            : (int)Math.Floor((currentExchangeTime - regularOpen).TotalMinutes);
    }

    private static int CountConsecutiveClosesAboveVwap(IReadOnlyList<IndicatorSnapshot> snapshots, int index)
    {
        var count = 0;
        for (var i = index; i >= 0; i--)
        {
            if (snapshots[i].Vwap is null || snapshots[i].CurrentPrice <= snapshots[i].Vwap!.Value)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static (
        bool IsReclaim,
        decimal? PullbackDepthPct,
        decimal? RecentHigh,
        decimal? PullbackLow) GetSwingReclaimContext(
            StrategyDefinition strategy,
            IReadOnlyList<OhlcvBar> bars,
            IReadOnlyList<IndicatorSnapshot> snapshots,
            int index)
    {
        var rules = strategy.EntryRules;
        var lookback = Math.Max(3, rules.ReclaimLookbackBars);
        if (index < lookback || index <= 0)
        {
            return (false, null, null, null);
        }

        var start = index - lookback;
        var priorBars = bars.Skip(start).Take(lookback).ToArray();
        var recentHigh = priorBars.Max(x => x.High);
        var pullbackLow = priorBars.Min(x => x.Low);
        var pullbackLowOffset = Array.FindIndex(priorBars, x => x.Low == pullbackLow);
        var pullbackLowIndex = pullbackLowOffset < 0 ? -1 : start + pullbackLowOffset;
        if (recentHigh <= 0m || pullbackLow <= 0m || pullbackLowIndex < 0)
        {
            return (false, null, null, null);
        }

        var pullbackDepthPct = ((recentHigh - pullbackLow) / recentHigh) * 100m;
        if (pullbackDepthPct < rules.MinReclaimPullbackDepthPct ||
            pullbackDepthPct > rules.MaxReclaimPullbackDepthPct)
        {
            return (false, pullbackDepthPct, recentHigh, pullbackLow);
        }

        if (rules.RequireReclaimLowAboveSma50 &&
            (snapshots[pullbackLowIndex].Sma50 is null || pullbackLow < snapshots[pullbackLowIndex].Sma50!.Value))
        {
            return (false, pullbackDepthPct, recentHigh, pullbackLow);
        }

        var current = snapshots[index];
        var previous = snapshots[index - 1];
        var reclaimedSma10 = !rules.RequireReclaimCloseAboveSma10 ||
            (current.Sma10 is { } currentSma10 &&
             current.CurrentPrice > currentSma10 &&
             previous.Sma10 is { } previousSma10 &&
             previous.CurrentPrice <= previousSma10);
        var reclaimedSma20 = !rules.RequireReclaimCloseAboveSma20 ||
            (current.Sma20 is { } currentSma20 &&
             current.CurrentPrice > currentSma20 &&
             previous.Sma20 is { } previousSma20 &&
             previous.CurrentPrice <= previousSma20);

        return (reclaimedSma10 && reclaimedSma20, pullbackDepthPct, recentHigh, pullbackLow);
    }

    // Archetype C (doctrine §6C): the L4 trigger is today's first close back above the prior day's high,
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

    // Archetype B one-shot (doctrine §6B / edge-recovery Phase 1): the attached catalyst fires exactly once
    // -- on the FIRST technically-confirmed bar (EMA10x20 flip or MACD turn + volume) of that catalyst's
    // attached run. Being the first confirmed bar of THIS catalyst is the anti-churn guarantee in the
    // backtest without a stateful consumption set; the live runner enforces the same via the eligibility
    // service (window + TryBeginAttempt). The attacher already bounds attachment to bars after the catalyst,
    // and the shared evaluator applies the bucket/news freshness gate (positive/new, MaxCatalystConfirmationBars).
    private static bool IsCatalystDriftTrigger(
        StrategyDefinition strategy,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        if (index <= 0 ||
            snapshots[index].Catalyst is not { } catalyst ||
            !CatalystConfirmation.HasTechnicalConfirmation(snapshots[index], snapshots[index - 1]))
        {
            return false;
        }

        // One shot: not if an earlier bar of the SAME catalyst's attached run already confirmed.
        var catalystKey = CatalystEligibilityService.KeyFor(catalyst);
        for (var i = index - 1;
             i >= 1 && snapshots[i].Catalyst is { } earlier && CatalystEligibilityService.KeyFor(earlier) == catalystKey;
             i--)
        {
            if (CatalystConfirmation.HasTechnicalConfirmation(snapshots[i], snapshots[i - 1]))
            {
                return false;
            }
        }

        return true;
    }

    private static (
        bool IsRollover,
        decimal? AdvancePct,
        decimal? DropFromHighPct,
        decimal? RecentHigh) GetSwingRolloverContext(
            StrategyDefinition strategy,
            IReadOnlyList<OhlcvBar> bars,
            IReadOnlyList<IndicatorSnapshot> snapshots,
            int index)
    {
        var rules = strategy.EntryRules;
        var lookback = Math.Max(3, rules.RolloverLookbackBars);
        if (index < lookback || index <= 0)
        {
            return (false, null, null, null);
        }

        var start = index - lookback;
        var priorBars = bars.Skip(start).Take(lookback).ToArray();
        if (priorBars.Length != lookback)
        {
            return (false, null, null, null);
        }

        var recentHigh = priorBars.Max(x => x.High);
        var highOffset = Array.FindIndex(priorBars, x => x.High == recentHigh);
        if (recentHigh <= 0m || highOffset < 0)
        {
            return (false, null, null, null);
        }

        var priorLowBeforeHigh = priorBars.Take(highOffset + 1).Min(x => x.Low);
        if (priorLowBeforeHigh <= 0m)
        {
            return (false, null, null, recentHigh);
        }

        var advancePct = ((recentHigh / priorLowBeforeHigh) - 1m) * 100m;
        var currentClose = bars[index].Close;
        var dropFromHighPct = ((recentHigh - currentClose) / recentHigh) * 100m;
        var lowerCloseBars = Math.Max(1, rules.RolloverConsecutiveLowerCloseBars);
        var hasRequiredLowerCloses = HasConsecutiveLowerCloses(bars, index, lowerCloseBars);
        var brokePriorLow = !rules.RequireRolloverCloseBelowPriorLow || currentClose < bars[index - 1].Low;

        if (advancePct < rules.MinRolloverAdvancePct ||
            dropFromHighPct < rules.MinRolloverDropFromHighPct ||
            dropFromHighPct > rules.MaxRolloverDropFromHighPct ||
            !hasRequiredLowerCloses ||
            !brokePriorLow)
        {
            return (false, advancePct, dropFromHighPct, recentHigh);
        }

        var current = snapshots[index];
        var belowSma10 = !rules.RequireRolloverCloseBelowSma10 ||
            (current.Sma10 is { } sma10 && current.CurrentPrice < sma10);
        var belowSma20 = !rules.RequireRolloverCloseBelowSma20 ||
            (current.Sma20 is { } sma20 && current.CurrentPrice < sma20);
        var belowSma50 = !rules.RequireRolloverCloseBelowSma50 ||
            (current.Sma50 is { } sma50 && current.CurrentPrice < sma50);

        return (belowSma10 && belowSma20 && belowSma50, advancePct, dropFromHighPct, recentHigh);
    }

    private static bool HasConsecutiveLowerCloses(IReadOnlyList<OhlcvBar> bars, int index, int requiredBars)
    {
        if (index < requiredBars)
        {
            return false;
        }

        for (var offset = 0; offset < requiredBars; offset++)
        {
            var current = bars[index - offset].Close;
            var previous = bars[index - offset - 1].Close;
            if (current >= previous)
            {
                return false;
            }
        }

        return true;
    }

    private static (
        bool IsBreakout,
        bool IsBreakdown,
        decimal? PriorMovePct,
        decimal? PriorDeclinePct,
        decimal? BaseDepthPct,
        decimal? BaseVolumeRatio,
        decimal? BreakoutVolumeRatio,
        decimal? BollingerWidthRatio) GetStepBreakoutContext(
            StrategyDefinition strategy,
            IReadOnlyList<OhlcvBar> bars,
            IReadOnlyList<IndicatorSnapshot> snapshots,
            int index)
    {
        var rules = strategy.EntryRules;
        var minBaseBars = Math.Max(2, rules.StepConsolidationMinBars);
        var maxBaseBars = Math.Max(minBaseBars, rules.StepConsolidationMaxBars);
        var priorLookbackBars = Math.Max(2, rules.StepPriorMoveLookbackBars);

        for (var baseBars = minBaseBars; baseBars <= maxBaseBars; baseBars++)
        {
            var baseStart = index - baseBars;
            var priorStart = baseStart - priorLookbackBars;
            if (baseStart < 1 || priorStart < 0)
            {
                continue;
            }

            var baseWindow = bars.Skip(baseStart).Take(baseBars).ToArray();
            var priorWindow = bars.Skip(priorStart).Take(priorLookbackBars).ToArray();
            if (baseWindow.Length != baseBars || priorWindow.Length != priorLookbackBars)
            {
                continue;
            }

            var baseHigh = baseWindow.Max(x => x.High);
            var baseLow = baseWindow.Min(x => x.Low);
            if (baseHigh <= 0m || baseLow <= 0m || baseLow >= baseHigh)
            {
                continue;
            }

            var baseDepthPct = ((baseHigh - baseLow) / baseHigh) * 100m;
            if (baseDepthPct > rules.MaxStepBaseDepthPct)
            {
                continue;
            }

            var averagePriorVolume = priorWindow.Average(x => x.Volume);
            var averageBaseVolume = baseWindow.Average(x => x.Volume);
            if (averagePriorVolume <= 0m || averageBaseVolume <= 0m)
            {
                continue;
            }

            var baseVolumeRatio = averageBaseVolume / averagePriorVolume;
            if (rules.MaxStepBaseVolumeRatio is { } maxBaseVolumeRatio &&
                baseVolumeRatio > maxBaseVolumeRatio)
            {
                continue;
            }

            var breakoutVolumeRatio = bars[index].Volume / averageBaseVolume;
            if (rules.MinStepBreakoutVolumeRatio is { } minBreakoutVolumeRatio &&
                breakoutVolumeRatio < minBreakoutVolumeRatio)
            {
                continue;
            }

            var bollingerWidthRatio = GetBollingerWidthRatio(snapshots, baseStart, index - 1);
            if (rules.RequireStepBollingerContraction &&
                (bollingerWidthRatio is null || bollingerWidthRatio.Value > rules.StepBollingerWidthRatioMax))
            {
                continue;
            }

            var priorLow = priorWindow.Min(x => x.Low);
            var priorHigh = priorWindow.Max(x => x.High);
            var priorMovePct = priorLow <= 0m ? (decimal?)null : ((baseHigh / priorLow) - 1m) * 100m;
            var priorDeclinePct = priorHigh <= 0m ? (decimal?)null : ((priorHigh - baseLow) / priorHigh) * 100m;
            var higherLows = HasHigherLows(baseWindow);
            var lowerHighs = HasLowerHighs(baseWindow);

            var isBreakout = bars[index].Close > baseHigh &&
                priorMovePct is not null &&
                priorMovePct.Value >= rules.MinStepPriorMovePct &&
                (!rules.RequireStepHigherLows || higherLows);

            var isBreakdown = bars[index].Close < baseLow &&
                priorDeclinePct is not null &&
                priorDeclinePct.Value >= rules.MinStepPriorDeclinePct &&
                (!rules.RequireStepLowerHighsForShort || lowerHighs);

            if (isBreakout || isBreakdown)
            {
                return (
                    isBreakout,
                    isBreakdown,
                    priorMovePct,
                    priorDeclinePct,
                    baseDepthPct,
                    baseVolumeRatio,
                    breakoutVolumeRatio,
                    bollingerWidthRatio);
            }
        }

        return (false, false, null, null, null, null, null, null);
    }

    private static decimal? GetBollingerWidthRatio(IReadOnlyList<IndicatorSnapshot> snapshots, int baseStart, int baseEnd)
    {
        if (baseStart < 0 || baseEnd < baseStart || baseEnd >= snapshots.Count)
        {
            return null;
        }

        var startWidth = snapshots[baseStart].BollingerUpper - snapshots[baseStart].BollingerLower;
        var endWidth = snapshots[baseEnd].BollingerUpper - snapshots[baseEnd].BollingerLower;
        if (startWidth is null || endWidth is null || startWidth.Value <= 0m)
        {
            return null;
        }

        return endWidth.Value / startWidth.Value;
    }

    private static bool HasHigherLows(IReadOnlyList<OhlcvBar> bars)
    {
        var midpoint = bars.Count / 2;
        if (midpoint == 0 || midpoint >= bars.Count)
        {
            return false;
        }

        var firstHalfLow = bars.Take(midpoint).Min(x => x.Low);
        var secondHalfLow = bars.Skip(midpoint).Min(x => x.Low);
        return secondHalfLow >= firstHalfLow;
    }

    private static bool HasLowerHighs(IReadOnlyList<OhlcvBar> bars)
    {
        var midpoint = bars.Count / 2;
        if (midpoint == 0 || midpoint >= bars.Count)
        {
            return false;
        }

        var firstHalfHigh = bars.Take(midpoint).Max(x => x.High);
        var secondHalfHigh = bars.Skip(midpoint).Max(x => x.High);
        return secondHalfHigh <= firstHalfHigh;
    }
    private static (
        bool IsBreakout,
        decimal? PoleMovePct,
        decimal? PullbackDepthPct,
        decimal? PullbackVolumeRatio,
        decimal? BreakoutVolumeRatio) GetBullFlagContext(
            StrategyDefinition strategy,
            IReadOnlyList<OhlcvBar> bars,
            int index)
    {
        var rules = strategy.EntryRules;
        var pullbackMinBars = Math.Max(1, rules.BullFlagPullbackMinBars);
        var pullbackMaxBars = Math.Max(pullbackMinBars, rules.BullFlagPullbackMaxBars);
        var poleMaxBars = Math.Max(1, rules.BullFlagPoleMaxBars);

        for (var pullbackBars = pullbackMinBars; pullbackBars <= pullbackMaxBars; pullbackBars++)
        {
            var pullbackStart = index - pullbackBars;
            var poleEndExclusive = pullbackStart;
            var poleStart = Math.Max(0, poleEndExclusive - poleMaxBars);
            if (pullbackStart < 1 || poleEndExclusive <= poleStart)
            {
                continue;
            }

            var poleBars = bars.Skip(poleStart).Take(poleEndExclusive - poleStart).ToArray();
            var flagBars = bars.Skip(pullbackStart).Take(pullbackBars).ToArray();
            if (poleBars.Length == 0 || flagBars.Length == 0)
            {
                continue;
            }

            var poleLow = poleBars.Min(x => x.Low);
            var poleHigh = poleBars.Max(x => x.High);
            if (poleLow <= 0 || poleHigh <= poleLow)
            {
                continue;
            }

            var poleMovePct = ((poleHigh - poleLow) / poleLow) * 100m;
            if (rules.MinBullFlagPoleMovePct is { } minPoleMove &&
                poleMovePct < minPoleMove)
            {
                continue;
            }

            var pullbackLow = flagBars.Min(x => x.Low);
            var pullbackDepthPct = ((poleHigh - pullbackLow) / (poleHigh - poleLow)) * 100m;
            if (pullbackDepthPct < 0 || pullbackDepthPct > rules.BullFlagMaxDepthPctOfPole)
            {
                continue;
            }

            var averagePoleVolume = poleBars.Average(x => x.Volume);
            var averagePullbackVolume = flagBars.Average(x => x.Volume);
            if (averagePoleVolume <= 0 || averagePullbackVolume <= 0)
            {
                continue;
            }

            var pullbackVolumeRatio = averagePullbackVolume / averagePoleVolume;
            if (rules.BullFlagPullbackVolumeRatioMax is { } maxPullbackVolumeRatio &&
                pullbackVolumeRatio > maxPullbackVolumeRatio)
            {
                continue;
            }

            var breakoutLevel = Math.Max(poleHigh, flagBars.Max(x => x.High));
            if (bars[index].Close <= breakoutLevel)
            {
                continue;
            }

            var breakoutVolumeRatio = bars[index].Volume / averagePullbackVolume;
            if (rules.BullFlagBreakoutVolumeRatioMin is { } minBreakoutVolumeRatio &&
                breakoutVolumeRatio < minBreakoutVolumeRatio)
            {
                continue;
            }

            return (true, poleMovePct, pullbackDepthPct, pullbackVolumeRatio, breakoutVolumeRatio);
        }

        return (false, null, null, null, null);
    }

    private static bool IsFlatTopBreakout(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        int index)
    {
        var lookback = Math.Max(3, Math.Min(strategy.EntryRules.RecentHighLookbackBars, 8));
        if (index <= lookback)
        {
            return false;
        }

        var consolidationBars = bars.Skip(index - lookback).Take(lookback).ToArray();
        var high = consolidationBars.Max(x => x.High);
        var low = consolidationBars.Min(x => x.Low);
        if (high <= 0)
        {
            return false;
        }

        var rangePct = ((high - low) / high) * 100m;
        return rangePct <= 3.0m && bars[index].Close > high;
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

    private static (decimal? RangePct, decimal? PullbackFromHighPct) GetSessionContext(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var sessionOpen = currentExchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        var sessionBars = bars
            .Take(index + 1)
            .Where(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date == currentExchangeTime.Date && exchangeTime >= sessionOpen;
            })
            .ToArray();

        if (sessionBars.Length == 0)
        {
            return (null, null);
        }

        var sessionHigh = sessionBars.Max(x => x.High);
        var sessionLow = sessionBars.Min(x => x.Low);
        if (sessionHigh <= 0)
        {
            return (null, null);
        }

        var rangePct = ((sessionHigh - sessionLow) / sessionHigh) * 100m;
        var pullbackFromHighPct = ((sessionHigh - snapshots[index].CurrentPrice) / sessionHigh) * 100m;
        return (rangePct, pullbackFromHighPct);
    }

}
