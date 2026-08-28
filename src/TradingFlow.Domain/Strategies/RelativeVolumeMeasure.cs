namespace TradingFlow.Domain.Strategies;

/// <summary>
/// Candle-derived participation measures permitted in strategy decisions.
/// Vendor-reported RVOL and full-session denominators are intentionally absent.
/// </summary>
public enum RelativeVolumeMeasure
{
    CumulativeSameTime,
    SlotBar
}

public static class RelativeVolumeMeasureParser
{
    public static RelativeVolumeMeasure Parse(string? value)
    {
        return (value ?? "cumulative_same_time").Trim().ToLowerInvariant() switch
        {
            "cumulative_same_time" => RelativeVolumeMeasure.CumulativeSameTime,
            "slot_bar" => RelativeVolumeMeasure.SlotBar,
            _ => throw new InvalidOperationException(
                $"Unsupported min_volume_spike_source '{value}'. Use cumulative_same_time or slot_bar.")
        };
    }

    public static string ToConfigValue(this RelativeVolumeMeasure measure) => measure switch
    {
        RelativeVolumeMeasure.CumulativeSameTime => "cumulative_same_time",
        RelativeVolumeMeasure.SlotBar => "slot_bar",
        _ => throw new ArgumentOutOfRangeException(nameof(measure), measure, null)
    };
}
