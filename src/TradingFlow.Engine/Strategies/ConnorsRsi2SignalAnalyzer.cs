using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Strategies;

/// <summary>
/// Evaluates the completed-bar entry control from Larry Connors' RSI(2) research:
/// the daily close is above its long-term trend average and RSI(2) is strictly
/// below the configured oversold threshold.
/// </summary>
public sealed class ConnorsRsi2SignalAnalyzer
{
    public ConnorsRsi2SignalAnalysis AnalyzeCompletedDailyBar(
        OhlcvBar completedBar,
        IndicatorSnapshot completedSnapshot,
        ConnorsRsi2SignalOptions options)
    {
        ArgumentNullException.ThrowIfNull(completedBar);
        ArgumentNullException.ThrowIfNull(completedSnapshot);
        ArgumentNullException.ThrowIfNull(options);

        if (options.OversoldThreshold <= 0m || options.OversoldThreshold > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.OversoldThreshold,
                "The RSI(2) oversold threshold must be greater than zero and no greater than 100.");
        }

        if (!IsDaily(completedBar.Timeframe) || !IsDaily(completedSnapshot.Timeframe))
        {
            return Rejected(
                completedBar,
                completedSnapshot,
                options,
                "daily_timeframe_required");
        }

        if (!completedBar.Ticker.Equals(completedSnapshot.Ticker, StringComparison.OrdinalIgnoreCase) ||
            completedBar.Timestamp != completedSnapshot.Timestamp ||
            !completedBar.Timeframe.Equals(completedSnapshot.Timeframe, StringComparison.OrdinalIgnoreCase))
        {
            return Rejected(
                completedBar,
                completedSnapshot,
                options,
                "bar_snapshot_mismatch");
        }

        if (completedSnapshot.Sma200 is null)
        {
            return Rejected(
                completedBar,
                completedSnapshot,
                options,
                "sma200_unavailable");
        }

        if (completedSnapshot.Rsi2 is null)
        {
            return Rejected(
                completedBar,
                completedSnapshot,
                options,
                "rsi2_unavailable");
        }

        if (options.RequireCloseAboveSma200 &&
            completedBar.Close <= completedSnapshot.Sma200.Value)
        {
            return Rejected(
                completedBar,
                completedSnapshot,
                options,
                "close_not_above_sma200");
        }

        if (completedSnapshot.Rsi2.Value >= options.OversoldThreshold)
        {
            return Rejected(
                completedBar,
                completedSnapshot,
                options,
                "rsi2_not_below_threshold");
        }

        return new ConnorsRsi2SignalAnalysis(
            true,
            null,
            completedBar.Timestamp,
            completedBar.Close,
            completedSnapshot.Sma200,
            completedSnapshot.Rsi2,
            options.OversoldThreshold);
    }

    private static ConnorsRsi2SignalAnalysis Rejected(
        OhlcvBar completedBar,
        IndicatorSnapshot completedSnapshot,
        ConnorsRsi2SignalOptions options,
        string rejectionReason)
    {
        return new ConnorsRsi2SignalAnalysis(
            false,
            rejectionReason,
            completedBar.Timestamp,
            completedBar.Close,
            completedSnapshot.Sma200,
            completedSnapshot.Rsi2,
            options.OversoldThreshold);
    }

    private static bool IsDaily(string timeframe)
    {
        return timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase) ||
            timeframe.Equals("d", StringComparison.OrdinalIgnoreCase) ||
            timeframe.Equals("day", StringComparison.OrdinalIgnoreCase) ||
            timeframe.Equals("daily", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ConnorsRsi2SignalOptions(
    decimal OversoldThreshold = 5m,
    bool RequireCloseAboveSma200 = true);

public sealed record ConnorsRsi2SignalAnalysis(
    bool IsSignal,
    string? RejectionReason,
    DateTimeOffset CompletedBarTimestamp,
    decimal Close,
    decimal? Sma200,
    decimal? Rsi2,
    decimal OversoldThreshold);
