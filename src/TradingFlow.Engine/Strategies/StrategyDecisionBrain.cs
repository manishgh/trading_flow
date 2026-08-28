using System.Globalization;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Engine.Strategies;

/// <summary>
/// Shared entry-decision facade used by backtests and paper/live runners.
/// It centralizes volume-source selection and rejection wording so strategy behavior
/// stays identical across research, paper, and live execution.
/// </summary>
public sealed class StrategyDecisionBrain
{
    private readonly BasicStrategyEvaluator evaluator = new();

    public static decimal? ResolveEntryRelativeVolume(StrategyDefinition strategy, IndicatorSnapshot snapshot)
    {
        return strategy.EntryRules.MinVolumeSpikeSource switch
        {
            RelativeVolumeMeasure.CumulativeSameTime => snapshot.RelativeVolume,
            RelativeVolumeMeasure.SlotBar => snapshot.SlotRelativeVolume,
            _ => throw new ArgumentOutOfRangeException(
                nameof(strategy),
                strategy.EntryRules.MinVolumeSpikeSource,
                "Unsupported relative-volume measure.")
        };
    }

    public string? GetLongEntryRejection(
        StrategyDefinition strategy,
        TradeSignal signal,
        IndicatorSnapshot snapshot,
        decimal? relativeVolume,
        string? relativeVolumeSource = null)
    {
        var volumeRejection = GetVolumeConfirmationRejection(strategy, snapshot, relativeVolume, relativeVolumeSource);
        return volumeRejection ?? evaluator.GetLongEntryRejection(strategy, signal, relativeVolume ?? 0m);
    }

    public string? GetShortEntryRejection(
        StrategyDefinition strategy,
        TradeSignal signal,
        IndicatorSnapshot snapshot,
        decimal? relativeVolume,
        string? relativeVolumeSource = null)
    {
        var volumeRejection = GetVolumeConfirmationRejection(strategy, snapshot, relativeVolume, relativeVolumeSource);
        return volumeRejection ?? evaluator.GetShortEntryRejection(strategy, signal, relativeVolume ?? 0m);
    }

    public string? GetVolumeConfirmationRejection(
        StrategyDefinition strategy,
        IndicatorSnapshot snapshot,
        decimal? actualRelativeVolume,
        string? relativeVolumeSource = null)
    {
        var mode = strategy.EntryRules.VolumeConfirmationMode;
        var confirmationIsAdvisory = mode.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("soft_confirmation", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("soft_marker", StringComparison.OrdinalIgnoreCase);
        var anotherGateConsumesRelativeVolume = strategy.EntryRules.RejectWeakCloseOnHighRelativeVolume;
        if (confirmationIsAdvisory && !anotherGateConsumesRelativeVolume)
        {
            return null;
        }

        var source = strategy.EntryRules.MinVolumeSpikeSource;
        var sampleCount = source == RelativeVolumeMeasure.SlotBar
            ? snapshot.SlotRelativeVolumeSampleCount
            : snapshot.RelativeVolumeSampleCount;
        var minimumSamples = snapshot.RelativeVolumeMinimumSamples > 0
            ? snapshot.RelativeVolumeMinimumSamples
            : MarketEvidenceProfile.DefaultMinimumValidSamples;
        if (actualRelativeVolume is null || sampleCount < minimumSamples)
        {
            return
                $"rvol_baseline_not_ready (Source: {source.ToConfigValue()}, Samples: {sampleCount}, " +
                $"Required: {minimumSamples}, Profile: {snapshot.MarketEvidenceProfileVersion ?? "unknown"}, " +
                $"Cohort: {snapshot.RelativeVolumeCohort ?? "unknown"}, DataFeed: {snapshot.DataFeed ?? "unknown"}, " +
                $"AdjustmentPolicy: {snapshot.AdjustmentPolicy ?? "unknown"}, " +
                $"Reliability: {snapshot.MarketEvidenceReliability ?? "unknown"})";
        }

        if (confirmationIsAdvisory)
        {
            return null;
        }

        if (mode.Equals("liquidity_floor", StringComparison.OrdinalIgnoreCase))
        {
            var floor = strategy.EntryRules.MinVolumeLiquidityFloor ?? 0m;
            return floor > 0m && actualRelativeVolume.Value < floor
                ? FormatRelativeVolumeRejection(
                    "volume_liquidity_floor_below_minimum",
                    snapshot,
                    floor,
                    actualRelativeVolume.Value,
                    relativeVolumeSource ?? strategy.EntryRules.MinVolumeSpikeSource.ToConfigValue(),
                    mode,
                    sampleCount)
                : null;
        }

        return actualRelativeVolume.Value < strategy.EntryRules.MinVolumeSpike
            ? FormatRelativeVolumeRejection(
                "relative_volume_below_minimum",
                snapshot,
                strategy.EntryRules.MinVolumeSpike,
                actualRelativeVolume.Value,
                relativeVolumeSource ?? strategy.EntryRules.MinVolumeSpikeSource.ToConfigValue(),
                mode,
                sampleCount)
            : null;
    }

    public static string BuildSignalAuditJson(
        TradeSignal signal,
        decimal? relativeVolumeUsed,
        string relativeVolumeSource,
        IndicatorSnapshot snapshot)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            signal,
            relativeVolumeUsed,
            relativeVolumeSource,
            calculatedRelativeVolume = snapshot.RelativeVolume,
            slotRelativeVolume = snapshot.SlotRelativeVolume,
            slotMedianVolume = snapshot.SlotMedianVolume,
            cumulativeSameTimeMedianVolume = snapshot.CumulativeSameTimeMedianVolume,
            relativeVolumeSampleCount = snapshot.RelativeVolumeSampleCount,
            slotRelativeVolumeSampleCount = snapshot.SlotRelativeVolumeSampleCount,
            marketEvidenceProfileVersion = snapshot.MarketEvidenceProfileVersion,
            relativeVolumeCohort = snapshot.RelativeVolumeCohort,
            dataFeed = snapshot.DataFeed,
            adjustmentPolicy = snapshot.AdjustmentPolicy,
            marketEvidenceReliability = snapshot.MarketEvidenceReliability
        });
    }

    private static string FormatRelativeVolumeRejection(
        string reason,
        IndicatorSnapshot snapshot,
        decimal requiredRelativeVolume,
        decimal actualRelativeVolume,
        string volumeSource,
        string volumeMode,
        int sampleCount)
    {
        return
            $"{reason} (Actual: {actualRelativeVolume:F2}, Required: {requiredRelativeVolume:F2}, Source: {volumeSource}, Mode: {volumeMode}, " +
            $"Ticker: {snapshot.Ticker}, BarTime: {snapshot.Timestamp:O}, Timeframe: {snapshot.Timeframe}, " +
            $"BarVolume: {FormatWhole(snapshot.CurrentVolume)}, CumulativeSameTimeMedianVolume: {FormatNullableWhole(snapshot.CumulativeSameTimeMedianVolume)}, " +
            $"SlotMedianVolume: {FormatNullableWhole(snapshot.SlotMedianVolume)}, " +
            $"SampleSessions: {sampleCount}, Profile: {snapshot.MarketEvidenceProfileVersion ?? "unknown"}, " +
            $"Cohort: {snapshot.RelativeVolumeCohort ?? "unknown"}, DataFeed: {snapshot.DataFeed ?? "unknown"}, " +
            $"AdjustmentPolicy: {snapshot.AdjustmentPolicy ?? "unknown"}, " +
            $"Reliability: {snapshot.MarketEvidenceReliability ?? "unknown"})";
    }

    private static string FormatWhole(decimal value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string FormatNullableWhole(decimal? value)
    {
        return value is null
            ? "n/a"
            : value.Value.ToString("N0", CultureInfo.InvariantCulture);
    }
}
