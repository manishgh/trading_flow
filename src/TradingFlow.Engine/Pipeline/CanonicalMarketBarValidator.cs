using TradingFlow.Domain.Market;
using TradingFlow.Engine.Market;

namespace TradingFlow.Engine.Pipeline;

/// <summary>
/// Canonical normalization and structural validation shared by historical and
/// live candle ingestion. Provider-specific ordering and revision policy remain
/// the responsibility of their event processors.
/// </summary>
public static class CanonicalMarketBarValidator
{
    public static OhlcvBar Normalize(OhlcvBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        return bar with
        {
            Ticker = (bar.Ticker ?? String.Empty).Trim().ToUpperInvariant(),
            Timeframe = (bar.Timeframe ?? String.Empty).Trim().ToLowerInvariant(),
            Timestamp = bar.Timestamp.ToUniversalTime()
        };
    }

    public static bool IsValid(
        OhlcvBar bar,
        bool requireOneMinuteSource,
        out string failure)
    {
        if (String.IsNullOrWhiteSpace(bar.Ticker))
        {
            failure = "Candle ticker is required.";
            return false;
        }

        if (String.IsNullOrWhiteSpace(bar.Timeframe))
        {
            failure = "Candle timeframe is required.";
            return false;
        }

        TimeSpan duration;
        try
        {
            duration = TimeframeParser.Parse(bar.Timeframe);
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or NotSupportedException)
        {
            failure = exception.Message;
            return false;
        }

        if (duration <= TimeSpan.Zero)
        {
            failure = "Candle timeframe duration must be positive.";
            return false;
        }

        if (requireOneMinuteSource && duration != TimeSpan.FromMinutes(1))
        {
            failure = "Live market-state source bars must use the 1m timeframe.";
            return false;
        }

        if (duration < TimeSpan.FromDays(1) &&
            bar.Timestamp.ToUniversalTime().Ticks % TimeSpan.TicksPerMinute != 0)
        {
            failure = "Intraday candle timestamps must be minute-aligned.";
            return false;
        }

        if (bar.Open <= 0m || bar.High <= 0m || bar.Low <= 0m || bar.Close <= 0m ||
            bar.Volume < 0m ||
            bar.High < Math.Max(bar.Open, bar.Close) ||
            bar.Low > Math.Min(bar.Open, bar.Close) ||
            bar.High < bar.Low)
        {
            failure = "Invalid OHLC values: prices must be positive and internally consistent; volume cannot be negative.";
            return false;
        }

        failure = String.Empty;
        return true;
    }

    public static bool IsCompletedAsOf(OhlcvBar bar, DateTimeOffset asOfUtc)
    {
        var duration = TimeframeParser.Parse(bar.Timeframe);
        return bar.Timestamp.ToUniversalTime() + duration <= asOfUtc.ToUniversalTime();
    }
}
