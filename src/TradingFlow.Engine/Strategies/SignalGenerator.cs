using System;
using System.Collections.Generic;
using System.Linq;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Sessions;

namespace TradingFlow.Engine.Strategies;

public sealed class SignalGenerator
{

    public TradeSignal? CreateTradeSignal(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var snapshot = snapshots[index];
        var previous = snapshots[index - 1];
        var bar = bars[index];
        if (snapshot.Rsi is null ||
            snapshot.Atr is null ||
            snapshot.RelativeVolume is null ||
            snapshot.Vwap is null ||
            snapshot.BollingerMiddle is null ||
            snapshot.MacdHistogram is null)
        {
            return null;
        }

        var previousHistogram = previous.MacdHistogram ?? snapshot.MacdHistogram.Value;
        var isAboveVwap = snapshot.CurrentPrice > snapshot.Vwap.Value;
        var isVwapPullback = previous.Vwap is not null &&
            previous.CurrentPrice >= previous.Vwap.Value &&
            bar.Low <= snapshot.Vwap.Value &&
            snapshot.CurrentPrice >= snapshot.Vwap.Value;
        var isVwapReclaim = previous.Vwap is not null &&
            previous.CurrentPrice < previous.Vwap.Value &&
            snapshot.CurrentPrice >= snapshot.Vwap.Value;
        var isVwapRejection = previous.Vwap is not null &&
            previous.CurrentPrice <= previous.Vwap.Value &&
            bar.High >= snapshot.Vwap.Value &&
            snapshot.CurrentPrice <= snapshot.Vwap.Value;
        var isEma20Pullback = snapshot.Ema20 is not null &&
            previous.Ema20 is not null &&
            previous.CurrentPrice >= previous.Ema20.Value &&
            bar.Low <= snapshot.Ema20.Value &&
            snapshot.CurrentPrice >= snapshot.Ema20.Value;
        var isPriceAboveEma20 = snapshot.Ema20 is not null && snapshot.CurrentPrice > snapshot.Ema20.Value;
        var isPriceAboveEma50 = snapshot.Ema50 is not null && snapshot.CurrentPrice > snapshot.Ema50.Value;
        var isEma20AboveEma50 = snapshot.Ema20 is not null &&
            snapshot.Ema50 is not null &&
            snapshot.Ema20.Value > snapshot.Ema50.Value;
        var isPriceAboveSma10 = snapshot.Sma10 is not null && snapshot.CurrentPrice > snapshot.Sma10.Value;
        var isPriceAboveSma20 = snapshot.Sma20 is not null && snapshot.CurrentPrice > snapshot.Sma20.Value;
        var isPriceAboveSma50 = snapshot.Sma50 is not null && snapshot.CurrentPrice > snapshot.Sma50.Value;
        var isSma10AboveSma20 = snapshot.Sma10 is not null &&
            snapshot.Sma20 is not null &&
            snapshot.Sma10.Value > snapshot.Sma20.Value;
        var isSma20AboveSma50 = snapshot.Sma20 is not null &&
            snapshot.Sma50 is not null &&
            snapshot.Sma20.Value > snapshot.Sma50.Value;
        var anchoredVwap = ComputeAnchoredVwap(strategy.EntryRules.AnchoredVwapMode, strategy.EntryRules.AnchoredVwapLookbackBars, bars, index);
        var isAboveAnchoredVwap = anchoredVwap is not null && snapshot.CurrentPrice > anchoredVwap.Value;
        var isBelowAnchoredVwap = anchoredVwap is not null && snapshot.CurrentPrice < anchoredVwap.Value;
        var anchoredVwapExtensionAtr = anchoredVwap is null || snapshot.Atr.Value <= 0m
            ? (decimal?)null
            : Math.Abs(snapshot.CurrentPrice - anchoredVwap.Value) / snapshot.Atr.Value;
        var shortAnchoredVwap = ComputeAnchoredVwap(strategy.EntryRules.ShortAnchoredVwapMode, strategy.EntryRules.ShortAnchoredVwapLookbackBars, bars, index);
        var isBelowShortAnchoredVwap = shortAnchoredVwap is not null && snapshot.CurrentPrice < shortAnchoredVwap.Value;
        var shortAnchoredVwapExtensionAtr = shortAnchoredVwap is null || snapshot.Atr.Value <= 0m
            ? (decimal?)null
            : Math.Abs(snapshot.CurrentPrice - shortAnchoredVwap.Value) / snapshot.Atr.Value;
        decimal? vwapExtensionAtr = snapshot.Atr.Value <= 0
            ? null
            : Math.Max(0, snapshot.CurrentPrice - snapshot.Vwap.Value) / snapshot.Atr.Value;
        var priceLogTrend = ComputeLogTrend(
            bars,
            index,
            strategy.EntryRules.LogPriceLookbackBars,
            bar => bar.Close,
            addOne: false);
        var volumeLogTrend = ComputeLogTrend(
            bars,
            index,
            strategy.EntryRules.LogVolumeLookbackBars,
            bar => bar.Volume,
            addOne: true);
        var previousRegularClose = GetPreviousRegularClose(strategy, bars, snapshots, index);
        var sessionOpen = GetSessionOpen(strategy, bars, snapshots, index);
        var sessionContext = GetSessionContext(strategy, bars, snapshots, index);
        var catalystContext = GetCatalystContext(snapshot.Catalyst, bars, snapshots, index);
        var bullFlag = GetBullFlagContext(strategy, bars, index);
        var stepContext = GetStepBreakoutContext(strategy, bars, snapshots, index);
        var reclaimContext = GetSwingReclaimContext(strategy, bars, snapshots, index);
        var rolloverContext = GetSwingRolloverContext(strategy, bars, snapshots, index);
        var premarketContext = GetPremarketContext(strategy, bars, snapshots, index);
        var minutesAfterRegularOpen = GetMinutesAfterRegularOpen(strategy, snapshots[index].Timestamp);
        var consecutiveClosesAboveVwap = CountConsecutiveClosesAboveVwap(snapshots, index);
        var bollingerContext = GetBollingerContext(snapshot);

        return new TradeSignal(
            snapshot.Ticker,
            snapshot.Timestamp,
            snapshot.Timeframe,
            snapshot.CurrentPrice,
            snapshot.CurrentVolume,
            snapshot.Rsi.Value,
            snapshot.Atr.Value,
            isAboveVwap,
            isVwapPullback,
            isVwapReclaim,
            isVwapRejection,
            isEma20Pullback,
            IsOpeningRangeBreakout(strategy, bars, snapshots, index),
            IsOpeningRangeBreakdown(strategy, bars, snapshots, index),
            IsRecentHighBreakout(strategy, bars, index),
            IsRecentLowBreakdown(strategy, bars, index),
            IsVolatilityContraction(strategy, bars, snapshots, index),
            isPriceAboveEma20,
            isPriceAboveEma50,
            isEma20AboveEma50,
            vwapExtensionAtr,
            snapshot.CurrentPrice > snapshot.BollingerMiddle.Value,
            snapshot.MacdHistogram.Value > 0,
            snapshot.MacdHistogram.Value >= 0 || snapshot.MacdHistogram.Value >= previousHistogram,
            priceLogTrend?.Slope,
            priceLogTrend?.R2,
            volumeLogTrend?.Slope,
            volumeLogTrend?.R2,
            IsOpeningDriveContinuation(strategy, bars, snapshots, index),
            IsAboveSessionOpen(strategy, bars, snapshots, index),
            IsBelowSessionOpen(strategy, bars, snapshots, index),
            ComputeCloseLocationValue(bar),
            previousRegularClose is null ? null : ((snapshot.CurrentPrice / previousRegularClose.Value) - 1m) * 100m,
            sessionOpen is null ? null : ((snapshot.CurrentPrice / sessionOpen.Value) - 1m) * 100m,
            sessionContext.RangePct,
            sessionContext.PullbackFromHighPct,
            snapshot.Catalyst,
            catalystContext.AgeHours,
            catalystContext.PriceMovePct,
            bullFlag.IsBreakout,
            IsFlatTopBreakout(strategy, bars, index),
            bullFlag.PoleMovePct,
            bullFlag.PullbackDepthPct,
            bullFlag.PullbackVolumeRatio,
            bullFlag.BreakoutVolumeRatio,
            premarketContext.High,
            premarketContext.Low,
            premarketContext.Vwap,
            premarketContext.Volume,
            premarketContext.RunPct,
            premarketContext.VwapExtensionPct,
            premarketContext.IsHighBreak,
            minutesAfterRegularOpen,
            consecutiveClosesAboveVwap,
            (bar.High + bar.Low) / 2m,
            bollingerContext.Position,
            bollingerContext.WidthPct,
            isPriceAboveSma10,
            isPriceAboveSma20,
            isPriceAboveSma50,
            isSma10AboveSma20,
            isSma20AboveSma50,
            anchoredVwap,
            anchoredVwapExtensionAtr,
            isAboveAnchoredVwap,
            isBelowAnchoredVwap,
            shortAnchoredVwap,
            shortAnchoredVwapExtensionAtr,
            isBelowShortAnchoredVwap,
            stepContext.IsBreakout,
            stepContext.IsBreakdown,
            stepContext.PriorMovePct,
            stepContext.PriorDeclinePct,
            stepContext.BaseDepthPct,
            stepContext.BaseVolumeRatio,
            stepContext.BreakoutVolumeRatio,
            stepContext.BollingerWidthRatio,
            reclaimContext.IsReclaim,
            reclaimContext.PullbackDepthPct,
            reclaimContext.RecentHigh,
            reclaimContext.PullbackLow,
            rolloverContext.IsRollover,
            rolloverContext.AdvancePct,
            rolloverContext.DropFromHighPct,
            rolloverContext.RecentHigh);
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

    private static (decimal? Position, decimal? WidthPct) GetBollingerContext(IndicatorSnapshot snapshot)
    {
        if (snapshot.BollingerLower is null ||
            snapshot.BollingerUpper is null ||
            snapshot.BollingerMiddle is null ||
            snapshot.BollingerUpper.Value <= snapshot.BollingerLower.Value)
        {
            return (null, null);
        }

        var width = snapshot.BollingerUpper.Value - snapshot.BollingerLower.Value;
        var position = (snapshot.CurrentPrice - snapshot.BollingerLower.Value) / width;
        var widthPct = snapshot.BollingerMiddle.Value == 0m
            ? (decimal?)null
            : (width / snapshot.BollingerMiddle.Value) * 100m;

        return (position, widthPct);
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

    private static (decimal? AgeHours, decimal? PriceMovePct) GetCatalystContext(
        CatalystEvent? catalyst,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        if (catalyst is null)
        {
            return (null, null);
        }

        var ageHours = (decimal)Math.Abs((snapshots[index].Timestamp - catalyst.Timestamp).TotalHours);
        var catalystBar = bars
            .Take(index + 1)
            .LastOrDefault(bar => bar.Timestamp <= catalyst.Timestamp);
        if (catalystBar is null || catalystBar.Close <= 0m)
        {
            return (ageHours, null);
        }

        var priceMovePct = ((snapshots[index].CurrentPrice / catalystBar.Close) - 1m) * 100m;
        return (ageHours, priceMovePct);
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

    private static decimal? GetSessionOpen(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var sessionOpen = currentExchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        return bars
            .Take(index + 1)
            .FirstOrDefault(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date == currentExchangeTime.Date &&
                    exchangeTime >= sessionOpen;
            })
            ?.Open;
    }

    private static decimal? GetPreviousRegularClose(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var regularOpen = new TimeSpan(9, 30, 0);
        var regularClose = new TimeSpan(16, 0, 0);

        return bars
            .Take(index)
            .Where(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date < currentExchangeTime.Date &&
                    exchangeTime.TimeOfDay >= regularOpen &&
                    exchangeTime.TimeOfDay <= regularClose;
            })
            .OrderBy(bar => bar.Timestamp)
            .LastOrDefault()
            ?.Close;
    }

    private static decimal? ComputeCloseLocationValue(OhlcvBar bar)
    {
        var range = bar.High - bar.Low;
        return range <= 0m
            ? null
            : (bar.Close - bar.Low) / range;
    }

    private static (decimal Slope, decimal R2)? ComputeLogTrend(
        IReadOnlyList<OhlcvBar> bars,
        int index,
        int lookback,
        Func<OhlcvBar, decimal> valueSelector,
        bool addOne)
    {
        if (lookback < 2 || index < lookback - 1)
        {
            return null;
        }

        var startIndex = index - lookback + 1;
        var n = lookback;
        var sumX = 0d;
        var sumY = 0d;
        var sumX2 = 0d;
        var sumXY = 0d;
        var yValues = new double[n];

        for (var offset = 0; offset < n; offset++)
        {
            var rawValue = (double)valueSelector(bars[startIndex + offset]);
            if (addOne)
            {
                rawValue += 1d;
            }

            if (rawValue <= 0d)
            {
                return null;
            }

            var x = (double)offset;
            var y = Math.Log(rawValue);
            yValues[offset] = y;
            sumX += x;
            sumY += y;
            sumX2 += x * x;
            sumXY += x * y;
        }

        var denominator = n * sumX2 - sumX * sumX;
        if (Math.Abs(denominator) < Double.Epsilon)
        {
            return null;
        }

        var slope = (n * sumXY - sumX * sumY) / denominator;
        var intercept = (sumY - slope * sumX) / n;
        var meanY = sumY / n;
        var sse = 0d;
        var sst = 0d;

        for (var offset = 0; offset < n; offset++)
        {
            var predicted = intercept + slope * offset;
            var residual = yValues[offset] - predicted;
            sse += residual * residual;

            var variance = yValues[offset] - meanY;
            sst += variance * variance;
        }

        var r2 = sst <= Double.Epsilon
            ? 1d
            : Math.Clamp(1d - sse / sst, 0d, 1d);

        return ((decimal)slope, (decimal)r2);
    }

    private static bool IsOpeningRangeBreakout(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var sessionOpen = currentExchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        var openingRangeEnd = sessionOpen.AddMinutes(strategy.EntryRules.OpeningRangeMinutes);
        if (currentExchangeTime < openingRangeEnd)
        {
            return false;
        }

        var openingRangeHigh = bars
            .Take(index)
            .Where(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date == currentExchangeTime.Date &&
                    exchangeTime >= sessionOpen &&
                    exchangeTime < openingRangeEnd;
            })
            .Select(bar => (decimal?)bar.High)
            .Max();

        return openingRangeHigh is not null &&
            snapshots[index].CurrentPrice > openingRangeHigh.Value &&
            snapshots[index - 1].CurrentPrice <= openingRangeHigh.Value;
    }

    private static bool IsOpeningDriveContinuation(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var sessionOpen = currentExchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        var openingWindowEnd = sessionOpen.AddMinutes(strategy.EntryRules.OpeningRangeMinutes);
        if (currentExchangeTime < openingWindowEnd)
        {
            return false;
        }

        var sessionOpenBar = bars
            .Take(index + 1)
            .FirstOrDefault(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date == currentExchangeTime.Date &&
                    exchangeTime >= sessionOpen;
            });
        if (sessionOpenBar is null || snapshots[index].CurrentPrice <= sessionOpenBar.Open)
        {
            return false;
        }

        var holdBars = Math.Max(1, strategy.EntryRules.VwapHoldBars);
        if (index < holdBars - 1)
        {
            return false;
        }

        for (var i = index - holdBars + 1; i <= index; i++)
        {
            var exchangeTime = ConvertToExchangeTime(snapshots[i].Timestamp, strategy.Session.ExchangeTimezone);
            if (exchangeTime.Date != currentExchangeTime.Date ||
                snapshots[i].Vwap is null ||
                snapshots[i].CurrentPrice < snapshots[i].Vwap!.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOpeningRangeBreakdown(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var sessionOpen = currentExchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        var openingRangeEnd = sessionOpen.AddMinutes(strategy.EntryRules.OpeningRangeMinutes);
        if (currentExchangeTime < openingRangeEnd)
        {
            return false;
        }

        var openingRangeLow = bars
            .Take(index)
            .Where(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date == currentExchangeTime.Date &&
                    exchangeTime >= sessionOpen &&
                    exchangeTime < openingRangeEnd;
            })
            .Select(bar => (decimal?)bar.Low)
            .Min();

        return openingRangeLow is not null &&
            snapshots[index].CurrentPrice < openingRangeLow.Value &&
            snapshots[index - 1].CurrentPrice >= openingRangeLow.Value;
    }

    private static bool IsAboveSessionOpen(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var sessionOpen = currentExchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        var sessionOpenBar = bars
            .Take(index + 1)
            .FirstOrDefault(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date == currentExchangeTime.Date &&
                    exchangeTime >= sessionOpen;
            });

        return sessionOpenBar is not null && snapshots[index].CurrentPrice > sessionOpenBar.Open;
    }

    private static bool IsBelowSessionOpen(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var sessionOpen = currentExchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        var sessionOpenBar = bars
            .Take(index + 1)
            .FirstOrDefault(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date == currentExchangeTime.Date &&
                    exchangeTime >= sessionOpen;
            });

        return sessionOpenBar is not null && snapshots[index].CurrentPrice < sessionOpenBar.Open;
    }

    private static bool IsRecentHighBreakout(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        int index)
    {
        var lookback = strategy.EntryRules.RecentHighLookbackBars;
        if (index <= lookback)
        {
            return false;
        }

        var priorHigh = bars
            .Skip(index - lookback)
            .Take(lookback)
            .Max(x => x.High);
        return bars[index].Close > priorHigh;
    }

    private static bool IsRecentLowBreakdown(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        int index)
    {
        var lookback = strategy.EntryRules.RecentHighLookbackBars;
        if (index <= lookback)
        {
            return false;
        }

        var priorLow = bars
            .Skip(index - lookback)
            .Take(lookback)
            .Min(x => x.Low);
        return bars[index].Close < priorLow;
    }

    private static bool IsVolatilityContraction(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var lookback = strategy.EntryRules.VolatilityContractionLookbackBars;
        if (index <= lookback ||
            snapshots[index].Atr is null ||
            snapshots[index - lookback].Atr is null)
        {
            return false;
        }

        var currentRange = bars[index].High - bars[index].Low;
        var priorWindowAverageRange = bars
            .Skip(index - lookback)
            .Take(lookback)
            .Average(x => x.High - x.Low);
        return snapshots[index].Atr!.Value < snapshots[index - lookback].Atr!.Value &&
            currentRange <= priorWindowAverageRange;
    }

    private static DateTime ConvertToExchangeTime(DateTimeOffset timestamp, string timezoneId)
    {
        return TimeZoneInfo.ConvertTime(timestamp, ResolveTimezone(timezoneId)).DateTime;
    }

    private static TimeZoneInfo ResolveTimezone(string timezoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException) when (timezoneId.Equals("America/New_York", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    public bool PassesConfluenceGate(
        StrategyDefinition strategy,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe)
    {
        return GetConfluenceRejection(strategy, timestamp, snapshotsByTimeframe) is null;
    }

    public string? GetConfluenceRejection(
        StrategyDefinition strategy,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe)
    {
        if (!strategy.Confluence.Enabled)
        {
            return null;
        }

        if (!snapshotsByTimeframe.TryGetValue(strategy.Confluence.Timeframe, out var confluenceSnapshots))
        {
            return $"confluence_missing_timeframe (Required: {strategy.Confluence.Timeframe})";
        }

        var confluence = confluenceSnapshots.LastOrDefault(x => x.Timestamp.Add(ParseTimeframe(x.Timeframe)) <= timestamp);
        if (confluence is null)
        {
            return $"confluence_no_closed_bar (Required: {strategy.Confluence.Timeframe}, SignalTime: {timestamp:O})";
        }

        if (confluence.MacdHistogram is null)
        {
            return $"confluence_missing_macd (Timeframe: {confluence.Timeframe}, Bar: {confluence.Timestamp:O})";
        }

        var ema = strategy.Confluence.EmaPeriod switch
        {
            20 => confluence.Ema20,
            50 => confluence.Ema50,
            200 => confluence.Ema200,
            _ => throw new NotSupportedException($"Unsupported confluence EMA period: {strategy.Confluence.EmaPeriod}")
        };

        if (ema is null)
        {
            return $"confluence_missing_ema{strategy.Confluence.EmaPeriod} (Timeframe: {confluence.Timeframe}, Bar: {confluence.Timestamp:O})";
        }

        if (strategy.Confluence.MacdFilter.Equals("bearish", StringComparison.OrdinalIgnoreCase))
        {
            if (confluence.CurrentPrice >= ema.Value)
            {
                return $"confluence_price_above_ema{strategy.Confluence.EmaPeriod} (Price: {confluence.CurrentPrice:F2}, EMA: {ema.Value:F2}, Timeframe: {confluence.Timeframe})";
            }

            if (confluence.MacdHistogram.Value >= 0)
            {
                return $"confluence_macd_not_bearish (Histogram: {confluence.MacdHistogram.Value:F4}, Timeframe: {confluence.Timeframe})";
            }

            return null;
        }

        if (confluence.CurrentPrice <= ema.Value)
        {
            return $"confluence_price_below_ema{strategy.Confluence.EmaPeriod} (Price: {confluence.CurrentPrice:F2}, EMA: {ema.Value:F2}, Timeframe: {confluence.Timeframe})";
        }

        if (strategy.Confluence.MacdFilter.Equals("not_bearish", StringComparison.OrdinalIgnoreCase) &&
            confluence.MacdHistogram.Value < 0)
        {
            return $"confluence_macd_bearish (Histogram: {confluence.MacdHistogram.Value:F4}, Timeframe: {confluence.Timeframe})";
        }

        return null;
    }

    private static TimeSpan ParseTimeframe(string timeframe)
    {
        if (timeframe.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
            Int32.TryParse(timeframe[..^1], out var minutes))
        {
            return TimeSpan.FromMinutes(minutes);
        }

        if (timeframe.EndsWith("h", StringComparison.OrdinalIgnoreCase) &&
            Int32.TryParse(timeframe[..^1], out var hours))
        {
            return TimeSpan.FromHours(hours);
        }

        if (timeframe.EndsWith("d", StringComparison.OrdinalIgnoreCase) &&
            Int32.TryParse(timeframe[..^1], out var days))
        {
            return TimeSpan.FromDays(days);
        }

        throw new NotSupportedException($"Unsupported timeframe: {timeframe}");
    }
}
