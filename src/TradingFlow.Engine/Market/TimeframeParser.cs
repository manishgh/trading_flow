namespace TradingFlow.Engine.Market;

/// <summary>
/// Converts strategy timeframe tokens into durations used by candle resampling,
/// signal-close timing, and session rules.
/// </summary>
public static class TimeframeParser
{
    public static TimeSpan Parse(string timeframe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeframe);

        var normalized = timeframe.Trim().ToLowerInvariant();
        if (normalized.EndsWith('m') &&
            Int32.TryParse(normalized[..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minutes))
        {
            return TimeSpan.FromMinutes(minutes);
        }

        if (normalized.EndsWith('h') &&
            Int32.TryParse(normalized[..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var hours))
        {
            return TimeSpan.FromHours(hours);
        }

        if (normalized.EndsWith('d') &&
            Int32.TryParse(normalized[..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var days))
        {
            return TimeSpan.FromDays(days);
        }

        throw new NotSupportedException($"Unsupported timeframe: {timeframe}");
    }

    public static bool IsDailyOrHigher(string timeframe)
    {
        return Parse(timeframe) >= TimeSpan.FromDays(1);
    }
}
