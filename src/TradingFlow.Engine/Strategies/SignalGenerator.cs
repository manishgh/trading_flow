using System;
using System.Collections.Generic;
using System.Linq;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Catalysts;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Sessions;

namespace TradingFlow.Engine.Strategies;

public sealed partial class SignalGenerator
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
        var isPriceAboveEma10 = snapshot.Ema10 is not null && snapshot.CurrentPrice > snapshot.Ema10.Value;
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
        var isEma10AboveEma20 = snapshot.Ema10 is not null &&
            snapshot.Ema20 is not null &&
            snapshot.Ema10.Value > snapshot.Ema20.Value;
        var isPriceAboveSma150 = snapshot.Sma150 is not null && snapshot.CurrentPrice > snapshot.Sma150.Value;
        var isPriceAboveSma200 = snapshot.Sma200 is not null && snapshot.CurrentPrice > snapshot.Sma200.Value;
        var isSma50AboveSma150 = snapshot.Sma50 is not null &&
            snapshot.Sma150 is not null &&
            snapshot.Sma50.Value > snapshot.Sma150.Value;
        var isSma150AboveSma200 = snapshot.Sma150 is not null &&
            snapshot.Sma200 is not null &&
            snapshot.Sma150.Value > snapshot.Sma200.Value;
        var isPriceAboveEma5 = snapshot.Ema5 is not null && snapshot.CurrentPrice > snapshot.Ema5.Value;
        var anchoredVwap = ComputePrimaryAnchoredVwap(strategy, bars, index);
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
        decimal? dayGainPct = previousRegularClose is null ? null : ((snapshot.CurrentPrice / previousRegularClose.Value) - 1m) * 100m;
        decimal? gapUpPct = previousRegularClose is null ? null : ((bar.Open / previousRegularClose.Value) - 1m) * 100m;
        decimal? sessionGainPct = sessionOpen is null ? null : ((snapshot.CurrentPrice / sessionOpen.Value) - 1m) * 100m;
        var sessionContext = GetSessionContext(strategy, bars, snapshots, index);
        var catalystContext = GetCatalystContext(snapshot.Catalyst, bars, snapshots, index);
        var bullFlag = UsesLongSetup(strategy, "ross_gap_go_bull_flag")
            ? GetBullFlagContext(strategy, bars, index)
            : (false, null, null, null, null);
        var stepContext = UsesLongSetup(strategy, "step_breakout") || UsesShortSetup(strategy, "step_breakdown")
            ? GetStepBreakoutContext(strategy, bars, snapshots, index)
            : (false, false, null, null, null, null, null, null);
        var reclaimContext = UsesLongSetup(strategy, "swing_reclaim")
            ? GetSwingReclaimContext(strategy, bars, snapshots, index)
            : (false, null, null, null);
        var reversionContext = UsesLongSetup(strategy, "mean_reversion_reclaim")
            ? GetMeanReversionReclaimContext(strategy, bars, snapshots, index)
            : (IsReclaim: false, StretchLow: (decimal?)null);
        var connorsRsi2Context = UsesLongSetup(strategy, "rsi2_oversold")
            ? new ConnorsRsi2SignalAnalyzer().AnalyzeCompletedDailyBar(
                bar,
                snapshot,
                new ConnorsRsi2SignalOptions(
                    strategy.EntryRules.MaxReversionRsi2 ?? 5m,
                    strategy.EntryRules.RequirePriceAboveSma200))
            : null;
        var isCatalystDrift = UsesLongSetup(strategy, "catalyst_drift") &&
            IsCatalystDriftTrigger(strategy, snapshots, index);
        var rolloverContext = UsesShortSetup(strategy, "swing_rollover")
            ? GetSwingRolloverContext(strategy, bars, snapshots, index)
            : (false, null, null, null);
        var premarketContext = GetPremarketContext(strategy, bars, snapshots, index);
        var minutesAfterRegularOpen = GetMinutesAfterRegularOpen(strategy, snapshots[index].Timestamp);
        var consecutiveClosesAboveVwap = CountConsecutiveClosesAboveVwap(snapshots, index);
        var bollingerContext = GetBollingerContext(snapshot);
        var trapContext = GetVwapReclaimTrapContext(strategy, bars, snapshots, index);
        var avwapBounceContext = GetAnchoredVwapBounceContext(strategy, bars, index, anchoredVwap);
        var vcpContext = GetVcpContext(strategy, bars, index);
        var price52WeekContext = Get52WeekPriceContext(bars, index);
        var volumeSmaTrend = GetVolumeSmaTrend(
            strategy,
            bars,
            index);
        var adxTrend = GetNullableIndicatorTrend(
            snapshots,
            index,
            strategy.EntryRules.AdxRisingLookbackBars,
            snapshot => snapshot.Adx);
        var obvTrend = GetNullableIndicatorTrend(
            snapshots,
            index,
            strategy.EntryRules.ObvRisingLookbackBars,
            snapshot => snapshot.Obv);
        var isEpisodicPivotGap = strategy.EntryRules.MinGapUpPct is { } minGap &&
            gapUpPct is not null &&
            gapUpPct.Value >= minGap;
        var priorEntryGainPct = ComputePriorEntryGainPct(
            bars,
            index,
            strategy.EntryRules.PriorEntryGainLookbackBars);
        var priorDayStructure = GetPriorDayStructure(strategy, bars, snapshots, index);
        var divergenceContext = GetMacdDivergenceFadeContext(strategy, bars, snapshots, index);
        var vwapDistanceAtr = snapshot.Atr.Value <= 0m
            ? (decimal?)null
            : Math.Abs(snapshot.CurrentPrice - snapshot.Vwap.Value) / snapshot.Atr.Value;

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
            dayGainPct,
            sessionGainPct,
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
            rolloverContext.RecentHigh,
            isPriceAboveEma10,
            isEma10AboveEma20,
            trapContext.IsTrap,
            trapContext.PriorFlushBars,
            trapContext.BarsSinceFlush,
            trapContext.ReclaimVolumeRatio,
            avwapBounceContext.IsBounce,
            avwapBounceContext.ProximityPct,
            avwapBounceContext.IsPullbackVolumeDryup,
            avwapBounceContext.BounceVolumeRatio,
            isEpisodicPivotGap,
            gapUpPct,
            vcpContext.IsBreakout,
            price52WeekContext.PriceVsLowPct,
            price52WeekContext.PriceVsHighPct,
            vcpContext.Contractions,
            vcpContext.IsVolatilityHalving,
            vcpContext.IsVolumeDryUp,
            vcpContext.BreakoutVolumeRatio,
            isPriceAboveSma150,
            isPriceAboveSma200,
            isSma50AboveSma150,
            isSma150AboveSma200,
            isPriceAboveEma5,
            snapshot.SessionRelativeVolume,
            snapshot.SlotRelativeVolume,
            volumeSmaTrend.IsRising,
            volumeSmaTrend.CurrentSma,
            volumeSmaTrend.PreviousSma,
            volumeSmaTrend.RisePct,
            catalystContext.AgeBars,
            snapshot.Adx,
            adxTrend.Previous,
            adxTrend.IsRising,
            snapshot.Obv,
            obvTrend.Previous,
            obvTrend.IsRising,
            obvTrend.Change,
            snapshot.MacdHistogram.Value,
            priorEntryGainPct,
            priorDayStructure.IsInsideDay,
            priorDayStructure.IsNr7,
            divergenceContext.IsBullishFade,
            divergenceContext.IsBearishFade,
            vwapDistanceAtr,
            reversionContext.IsReclaim,
            reversionContext.StretchLow,
            isCatalystDrift,
            connorsRsi2Context?.IsSignal ?? false,
            snapshot.Rsi2,
            vcpContext.StructuralStopPrice);
    }

    private static (bool IsRising, decimal? Previous, decimal? Change) GetNullableIndicatorTrend(
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index,
        int lookbackBars,
        Func<IndicatorSnapshot, decimal?> selector)
    {
        var lookback = Math.Max(1, lookbackBars);
        if (index - lookback < 0)
        {
            return (false, null, null);
        }

        for (var i = index - lookback; i <= index; i++)
        {
            if (selector(snapshots[i]) is null)
            {
                return (false, null, null);
            }
        }

        var previous = selector(snapshots[index - lookback]);
        var current = selector(snapshots[index]);
        var change = current - previous;
        return (change > 0m, previous, change);
    }

    private static (bool IsRising, decimal? CurrentSma, decimal? PreviousSma, decimal? RisePct) GetVolumeSmaTrend(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        int index)
    {
        var period = Math.Max(1, strategy.EntryRules.VolumeSmaPeriod);
        var lookbackBars = Math.Max(1, strategy.EntryRules.VolumeSmaRisingLookbackBars);
        if (index - lookbackBars - period + 1 < 0)
        {
            return (false, null, null, null);
        }

        decimal? previousSma = null;
        for (var offset = lookbackBars; offset >= 0; offset--)
        {
            var sma = AverageVolumeEndingAt(bars, index - offset, period);
            if (sma is null)
            {
                return (false, null, null, null);
            }

            if (previousSma is not null && sma.Value <= previousSma.Value)
            {
                return (false, AverageVolumeEndingAt(bars, index, period), AverageVolumeEndingAt(bars, index - lookbackBars, period), null);
            }

            previousSma = sma;
        }

        var current = AverageVolumeEndingAt(bars, index, period);
        var prior = AverageVolumeEndingAt(bars, index - lookbackBars, period);
        var risePct = current is not null && prior is > 0m
            ? ((current.Value / prior.Value) - 1m) * 100m
            : (decimal?)null;

        return (true, current, prior, risePct);
    }

    private static decimal? AverageVolumeEndingAt(IReadOnlyList<OhlcvBar> bars, int endIndex, int period)
    {
        if (endIndex < period - 1 || endIndex >= bars.Count)
        {
            return null;
        }

        decimal sum = 0m;
        for (var i = endIndex - period + 1; i <= endIndex; i++)
        {
            sum += bars[i].Volume;
        }

        return sum / period;
    }

    private static bool UsesLongSetup(StrategyDefinition strategy, string setupType)
    {
        return strategy.EntryRules.SetupType.Equals(setupType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool UsesShortSetup(StrategyDefinition strategy, string setupType)
    {
        return strategy.EntryRules.EnableShort &&
            strategy.EntryRules.ShortSetupType.Equals(setupType, StringComparison.OrdinalIgnoreCase);
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
        bool IsTrap,
        int? PriorFlushBars,
        int? BarsSinceFlush,
        decimal? ReclaimVolumeRatio) GetVwapReclaimTrapContext(
            StrategyDefinition strategy,
            IReadOnlyList<OhlcvBar> bars,
            IReadOnlyList<IndicatorSnapshot> snapshots,
            int index)
    {
        var maxBarsSinceFlush = Math.Max(1, strategy.EntryRules.VwapReclaimMaxBarsSinceFlush ?? 12);
        var start = Math.Max(0, index - maxBarsSinceFlush);
        var flushIndex = -1;
        var consecutiveBelow = 0;
        var bestConsecutiveBelow = 0;
        for (var i = start; i < index; i++)
        {
            if (snapshots[i].Vwap is not null && snapshots[i].CurrentPrice < snapshots[i].Vwap!.Value)
            {
                consecutiveBelow++;
                bestConsecutiveBelow = Math.Max(bestConsecutiveBelow, consecutiveBelow);
                flushIndex = i;
            }
            else
            {
                consecutiveBelow = 0;
            }
        }

        if (flushIndex < 0)
        {
            return (false, bestConsecutiveBelow, null, null);
        }

        var barsSinceFlush = index - flushIndex;
        var lookbackStart = Math.Max(0, index - 20);
        var priorVolumes = bars.Skip(lookbackStart).Take(index - lookbackStart).Select(x => x.Volume).Where(x => x > 0m).ToArray();
        var averageVolume = priorVolumes.Length == 0 ? 0m : priorVolumes.Average();
        var reclaimVolumeRatio = averageVolume <= 0m ? (decimal?)null : bars[index].Volume / averageVolume;
        var requiredFlushBars = strategy.EntryRules.RequirePriorFlushBelowVwapBars ?? 1;
        var requiredVolumeRatio = strategy.EntryRules.MinReclaimVolumeRatio ?? 0m;
        var isTrap = snapshots[index].Vwap is not null &&
            snapshots[index].CurrentPrice > snapshots[index].Vwap!.Value &&
            bestConsecutiveBelow >= requiredFlushBars &&
            barsSinceFlush <= maxBarsSinceFlush &&
            (reclaimVolumeRatio is null || reclaimVolumeRatio.Value >= requiredVolumeRatio);

        return (isTrap, bestConsecutiveBelow, barsSinceFlush, reclaimVolumeRatio);
    }

    private static (
        bool IsBounce,
        decimal? ProximityPct,
        bool IsPullbackVolumeDryup,
        decimal? BounceVolumeRatio) GetAnchoredVwapBounceContext(
            StrategyDefinition strategy,
            IReadOnlyList<OhlcvBar> bars,
            int index,
            decimal? anchoredVwap)
    {
        if (anchoredVwap is null || anchoredVwap.Value <= 0m)
        {
            return (false, null, false, null);
        }

        var bar = bars[index];
        var proximityPct = Math.Abs((bar.Close / anchoredVwap.Value) - 1m) * 100m;
        var maxProximityPct = strategy.EntryRules.AvwapProximityPct ?? 1.5m;
        var touchedAnchor = bar.Low <= anchoredVwap.Value * (1m + (maxProximityPct / 100m));
        var closedAboveAnchor = bar.Close >= anchoredVwap.Value;
        var lookbackStart = Math.Max(0, index - 20);
        var priorVolumes = bars.Skip(lookbackStart).Take(index - lookbackStart).Select(x => x.Volume).Where(x => x > 0m).ToArray();
        var averagePriorVolume = priorVolumes.Length == 0 ? 0m : priorVolumes.Average();
        var bounceVolumeRatio = averagePriorVolume <= 0m ? (decimal?)null : bar.Volume / averagePriorVolume;
        var pullbackStart = Math.Max(0, index - 3);
        var pullbackVolumes = bars.Skip(pullbackStart).Take(index - pullbackStart).Select(x => x.Volume).Where(x => x > 0m).ToArray();
        var isPullbackDryup = averagePriorVolume > 0m &&
            pullbackVolumes.Length > 0 &&
            pullbackVolumes.Average() <= averagePriorVolume * 0.80m;
        var minBounceVolumeRatio = strategy.EntryRules.MinBounceVolumeRatio ?? 0m;
        var isBounce = touchedAnchor &&
            closedAboveAnchor &&
            proximityPct <= maxProximityPct &&
            (!strategy.EntryRules.RequirePullbackVolumeDryup || isPullbackDryup) &&
            (bounceVolumeRatio is null || bounceVolumeRatio.Value >= minBounceVolumeRatio);

        return (isBounce, proximityPct, isPullbackDryup, bounceVolumeRatio);
    }


}
