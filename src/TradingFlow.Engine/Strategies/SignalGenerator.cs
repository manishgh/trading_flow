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
        if (snapshot.Atr is null || !HasRequiredEntryIndicators(strategy, snapshot))
        {
            return null;
        }

        var currentRsi = snapshot.Rsi ?? 50m;
        var currentHistogram = snapshot.MacdHistogram ?? 0m;
        var previousHistogram = previous.MacdHistogram ?? currentHistogram;
        var isAboveVwap = snapshot.Vwap is not null && snapshot.CurrentPrice > snapshot.Vwap.Value;
        var isVwapPullback = previous.Vwap is not null &&
            snapshot.Vwap is not null &&
            previous.CurrentPrice >= previous.Vwap.Value &&
            bar.Low <= snapshot.Vwap.Value &&
            snapshot.CurrentPrice >= snapshot.Vwap.Value;
        var isVwapReclaim = previous.Vwap is not null &&
            snapshot.Vwap is not null &&
            previous.CurrentPrice < previous.Vwap.Value &&
            snapshot.CurrentPrice >= snapshot.Vwap.Value;
        var isVwapRejection = previous.Vwap is not null &&
            snapshot.Vwap is not null &&
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
        decimal? vwapExtensionAtr = snapshot.Vwap is null || snapshot.Atr.Value <= 0
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
        decimal? gapUpPct = previousRegularClose is null ? null : ((bar.Open / previousRegularClose.Value) - 1m) * 100m;
        var catalystContext = GetCatalystContext(snapshot.Catalyst, bars, snapshots, index);
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
        var bollingerContext = GetBollingerContext(snapshot);
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
        return new TradeSignal(
            Ticker: snapshot.Ticker,
            Timestamp: snapshot.Timestamp,
            Timeframe: snapshot.Timeframe,
            CurrentPrice: snapshot.CurrentPrice,
            CurrentVolume: snapshot.CurrentVolume,
            CurrentRsi: currentRsi,
            CurrentAtr: snapshot.Atr.Value,
            IsAboveVwap: isAboveVwap,
            IsVwapPullback: isVwapPullback,
            IsVwapReclaim: isVwapReclaim,
            IsVwapRejection: isVwapRejection,
            IsEma20Pullback: isEma20Pullback,
            IsRecentHighBreakout: IsRecentHighBreakout(strategy, bars, index),
            IsRecentLowBreakdown: IsRecentLowBreakdown(strategy, bars, index),
            IsVolatilityContraction: IsVolatilityContraction(strategy, bars, snapshots, index),
            IsPriceAboveEma20: isPriceAboveEma20,
            IsPriceAboveEma50: isPriceAboveEma50,
            IsEma20AboveEma50: isEma20AboveEma50,
            VwapExtensionAtr: vwapExtensionAtr,
            IsAboveBollingerMiddle: snapshot.BollingerMiddle is not null && snapshot.CurrentPrice > snapshot.BollingerMiddle.Value,
            IsMacdHistogramPositive: currentHistogram > 0,
            IsMacdNotBearish: currentHistogram >= 0 || currentHistogram >= previousHistogram,
            PriceLogSlope: priceLogTrend?.Slope,
            PriceLogR2: priceLogTrend?.R2,
            VolumeLogSlope: volumeLogTrend?.Slope,
            VolumeLogR2: volumeLogTrend?.R2,
            CloseLocationValue: ComputeCloseLocationValue(bar),
            Catalyst: snapshot.Catalyst,
            CatalystAgeHours: catalystContext.AgeHours,
            CatalystPriceMovePct: catalystContext.PriceMovePct,
            SignalBarMidpoint: (bar.High + bar.Low) / 2m,
            BollingerPosition: bollingerContext.Position,
            BollingerWidthPct: bollingerContext.WidthPct,
            IsPriceAboveSma10: isPriceAboveSma10,
            IsPriceAboveSma20: isPriceAboveSma20,
            IsPriceAboveSma50: isPriceAboveSma50,
            IsSma10AboveSma20: isSma10AboveSma20,
            IsSma20AboveSma50: isSma20AboveSma50,
            AnchoredVwap: anchoredVwap,
            AnchoredVwapExtensionAtr: anchoredVwapExtensionAtr,
            IsAboveAnchoredVwap: isAboveAnchoredVwap,
            IsBelowAnchoredVwap: isBelowAnchoredVwap,
            ShortAnchoredVwap: shortAnchoredVwap,
            ShortAnchoredVwapExtensionAtr: shortAnchoredVwapExtensionAtr,
            IsBelowShortAnchoredVwap: isBelowShortAnchoredVwap,
            IsStepBreakout: stepContext.IsBreakout,
            IsStepBreakdown: stepContext.IsBreakdown,
            StepPriorMovePct: stepContext.PriorMovePct,
            StepPriorDeclinePct: stepContext.PriorDeclinePct,
            StepBaseDepthPct: stepContext.BaseDepthPct,
            StepBaseVolumeRatio: stepContext.BaseVolumeRatio,
            StepBreakoutVolumeRatio: stepContext.BreakoutVolumeRatio,
            StepBollingerWidthRatio: stepContext.BollingerWidthRatio,
            IsSwingReclaim: reclaimContext.IsReclaim,
            ReclaimPullbackDepthPct: reclaimContext.PullbackDepthPct,
            ReclaimRecentHigh: reclaimContext.RecentHigh,
            ReclaimPullbackLow: reclaimContext.PullbackLow,
            IsSwingRollover: rolloverContext.IsRollover,
            RolloverAdvancePct: rolloverContext.AdvancePct,
            RolloverDropFromHighPct: rolloverContext.DropFromHighPct,
            RolloverRecentHigh: rolloverContext.RecentHigh,
            IsPriceAboveEma10: isPriceAboveEma10,
            IsEma10AboveEma20: isEma10AboveEma20,
            IsAnchoredVwapBounce: avwapBounceContext.IsBounce,
            AnchoredVwapProximityPct: avwapBounceContext.ProximityPct,
            IsPullbackVolumeDryup: avwapBounceContext.IsPullbackVolumeDryup,
            BounceVolumeRatio: avwapBounceContext.BounceVolumeRatio,
            IsEpisodicPivotGap: isEpisodicPivotGap,
            GapUpPct: gapUpPct,
            IsVcpBreakout: vcpContext.IsBreakout,
            PriceVs52WeekLowPct: price52WeekContext.PriceVsLowPct,
            PriceVs52WeekHighPct: price52WeekContext.PriceVsHighPct,
            VolatilityContractions: vcpContext.Contractions,
            IsVolatilityHalving: vcpContext.IsVolatilityHalving,
            IsVolumeDryUp: vcpContext.IsVolumeDryUp,
            BreakoutVolumeRatio: vcpContext.BreakoutVolumeRatio,
            IsPriceAboveSma150: isPriceAboveSma150,
            IsPriceAboveSma200: isPriceAboveSma200,
            IsSma50AboveSma150: isSma50AboveSma150,
            IsSma150AboveSma200: isSma150AboveSma200,
            IsPriceAboveEma5: isPriceAboveEma5,
            SlotRelativeVolume: snapshot.SlotRelativeVolume,
            IsVolumeSmaRising: volumeSmaTrend.IsRising,
            VolumeSma: volumeSmaTrend.CurrentSma,
            PreviousVolumeSma: volumeSmaTrend.PreviousSma,
            VolumeSmaRisePct: volumeSmaTrend.RisePct,
            CatalystAgeBars: catalystContext.AgeBars,
            CurrentAdx: snapshot.Adx,
            PreviousAdx: adxTrend.Previous,
            IsAdxRising: adxTrend.IsRising,
            CurrentObv: snapshot.Obv,
            PreviousObv: obvTrend.Previous,
            IsObvRising: obvTrend.IsRising,
            ObvChange: obvTrend.Change,
            MacdHistogram: snapshot.MacdHistogram,
            PriorEntryGainPct: priorEntryGainPct,
            IsMeanReversionReclaim: reversionContext.IsReclaim,
            ReversionStretchLow: reversionContext.StretchLow,
            IsCatalystDrift: isCatalystDrift,
            IsConnorsRsi2Oversold: connorsRsi2Context?.IsSignal ?? false,
            CurrentRsi2: snapshot.Rsi2,
            SetupStructuralStopPrice: vcpContext.StructuralStopPrice);
    }

    private static bool HasRequiredEntryIndicators(
        StrategyDefinition strategy,
        IndicatorSnapshot snapshot)
    {
        var rules = strategy.EntryRules;
        var usesShortEntries = rules.EnableShort &&
            (strategy.Direction.Equals("short", StringComparison.OrdinalIgnoreCase) ||
             strategy.Direction.Equals("long_short", StringComparison.OrdinalIgnoreCase) ||
             strategy.Direction.Equals("both", StringComparison.OrdinalIgnoreCase));
        var requiresRsi = rules.MinEntryRsi > 0m ||
            rules.MaxEntryRsi < 100m ||
            (usesShortEntries && (rules.MinShortEntryRsi is not null || rules.MaxShortEntryRsi is not null));
        var requiresVwap = rules.RequirePriceAboveVwap ||
            (usesShortEntries && rules.RequirePriceBelowVwapForShort) ||
            rules.TrendFilter.Equals("vwap", StringComparison.OrdinalIgnoreCase) ||
            strategy.ExitRules.InitialStopMode.Contains("vwap", StringComparison.OrdinalIgnoreCase);
        var requiresBollinger = rules.RequirePriceAboveBollingerMiddle ||
            rules.EnableLowerBollingerStretch;
        var requiresMacd = rules.RequireMacdHistogramPositive ||
            (usesShortEntries && rules.RequireMacdBearishForShort) ||
            !rules.MacdFilter.Equals("none", StringComparison.OrdinalIgnoreCase);

        return (!requiresRsi || snapshot.Rsi is not null) &&
            (!requiresVwap || snapshot.Vwap is not null) &&
            (!requiresBollinger || snapshot.BollingerMiddle is not null) &&
            (!requiresMacd || snapshot.MacdHistogram is not null);
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
