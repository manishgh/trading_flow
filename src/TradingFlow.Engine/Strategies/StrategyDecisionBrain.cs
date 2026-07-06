using System.Globalization;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

/// <summary>
/// Shared entry-decision facade used by backtests and paper/live runners.
/// It centralizes volume-source selection and rejection wording so strategy behavior
/// stays identical across research, paper, and live execution.
/// </summary>
public sealed class StrategyDecisionBrain
{
    private readonly BasicStrategyEvaluator evaluator = new();

    public decimal? ResolveEntryRelativeVolume(StrategyDefinition strategy, IndicatorSnapshot snapshot)
    {
        return strategy.EntryRules.MinVolumeSpikeSource.ToLowerInvariant() switch
        {
            "session_vs_average_day" or "session" or "finviz_style" => snapshot.SessionRelativeVolume,
            "slot_bar" or "bar_same_time" => snapshot.SlotRelativeVolume,
            _ => snapshot.RelativeVolume
        };
    }

    public string? GetLongEntryRejection(
        StrategyDefinition strategy,
        TradeSignal signal,
        IndicatorSnapshot snapshot,
        decimal relativeVolume,
        string? relativeVolumeSource = null)
    {
        var volumeRejection = GetVolumeConfirmationRejection(strategy, snapshot, relativeVolume, relativeVolumeSource);
        return volumeRejection ?? evaluator.GetLongEntryRejection(strategy, signal, relativeVolume);
    }

    public string? GetShortEntryRejection(
        StrategyDefinition strategy,
        TradeSignal signal,
        IndicatorSnapshot snapshot,
        decimal relativeVolume,
        string? relativeVolumeSource = null)
    {
        var volumeRejection = GetVolumeConfirmationRejection(strategy, snapshot, relativeVolume, relativeVolumeSource);
        return volumeRejection ?? evaluator.GetShortEntryRejection(strategy, signal, relativeVolume);
    }

    public string? GetVolumeConfirmationRejection(
        StrategyDefinition strategy,
        IndicatorSnapshot snapshot,
        decimal actualRelativeVolume,
        string? relativeVolumeSource = null)
    {
        var mode = strategy.EntryRules.VolumeConfirmationMode;
        if (mode.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("soft_confirmation", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("soft_marker", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (mode.Equals("liquidity_floor", StringComparison.OrdinalIgnoreCase))
        {
            var floor = strategy.EntryRules.MinVolumeLiquidityFloor ?? 0m;
            return floor > 0m && actualRelativeVolume < floor
                ? FormatRelativeVolumeRejection(
                    "volume_liquidity_floor_below_minimum",
                    snapshot,
                    floor,
                    actualRelativeVolume,
                    relativeVolumeSource ?? strategy.EntryRules.MinVolumeSpikeSource,
                    mode)
                : null;
        }

        return actualRelativeVolume < strategy.EntryRules.MinVolumeSpike
            ? FormatRelativeVolumeRejection(
                "relative_volume_below_minimum",
                snapshot,
                strategy.EntryRules.MinVolumeSpike,
                actualRelativeVolume,
                relativeVolumeSource ?? strategy.EntryRules.MinVolumeSpikeSource,
                mode)
            : null;
    }

    public static string BuildSignalAuditJson(
        TradeSignal signal,
        decimal relativeVolumeUsed,
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
            sessionRelativeVolume = snapshot.SessionRelativeVolume,
            slotAverageVolume = snapshot.SlotAverageVolume,
            cumulativeAverageVolume = snapshot.CumulativeAverageVolume,
            averageSessionVolume = snapshot.AverageSessionVolume,
            relativeVolumeSampleCount = snapshot.RelativeVolumeSampleCount
        });
    }

    private static string FormatRelativeVolumeRejection(
        string reason,
        IndicatorSnapshot snapshot,
        decimal requiredRelativeVolume,
        decimal actualRelativeVolume,
        string volumeSource,
        string volumeMode)
    {
        return
            $"{reason} (Actual: {actualRelativeVolume:F2}, Required: {requiredRelativeVolume:F2}, Source: {volumeSource}, Mode: {volumeMode}, " +
            $"Ticker: {snapshot.Ticker}, BarTime: {snapshot.Timestamp:O}, Timeframe: {snapshot.Timeframe}, " +
            $"BarVolume: {FormatWhole(snapshot.CurrentVolume)}, CumulativeAvgVolume: {FormatNullableWhole(snapshot.CumulativeAverageVolume)}, " +
            $"SlotAvgVolume: {FormatNullableWhole(snapshot.SlotAverageVolume)}, AverageSessionVolume: {FormatNullableWhole(snapshot.AverageSessionVolume)}, " +
            $"SampleSessions: {snapshot.RelativeVolumeSampleCount})";
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
