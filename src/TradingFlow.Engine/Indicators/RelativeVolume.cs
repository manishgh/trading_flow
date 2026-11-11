using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Indicators;

/// <summary>
/// The one relative-volume calculation: current volume divided by an average of the same measure in earlier sessions.
/// Shared by the indicator engine (median), the opening-range recipe (mean) and the daily stocks-in-play ranking.
/// </summary>
public static class RelativeVolume
{
    /// <summary>
    /// Averages earlier-session volumes by the chosen rule. Fails on an empty sample because an average of nothing would
    /// silently become zero. The median of an even count is the mean of the two middle values.
    /// </summary>
    public static decimal Baseline(IReadOnlyCollection<decimal> samples, VolumeBaselineAverage average)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) throw new ArgumentException("A volume baseline needs at least one earlier session.", nameof(samples));
        if (average == VolumeBaselineAverage.Mean) return samples.Sum() / samples.Count;
        var ordered = samples.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2m : ordered[middle];
    }

    /// <summary>Current volume over its baseline; null when the baseline is zero, because no ratio exists then.</summary>
    public static decimal? Ratio(decimal current, decimal baseline) => baseline <= 0m ? null : current / baseline;
}
