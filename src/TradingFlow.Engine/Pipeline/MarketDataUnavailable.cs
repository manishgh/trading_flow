namespace TradingFlow.Engine.Pipeline;

/// <summary>
/// Named reasons a stock's market data cannot be used. A failure message starting with one of these codes and a colon means
/// the data is missing or unusable, not that the engine failed; a whole-market research screen skips and counts such a stock
/// instead of stopping. Any other failure (an exception, a timeout) still stops the run.
/// </summary>
public static class MarketDataUnavailable
{
    /// <summary>The provider returned no bars for the stock in the requested window.</summary>
    public const string BarsMissing = "market_bars_missing";

    /// <summary>A returned bar failed validation (impossible prices) or conflicted with another bar at the same time.</summary>
    public const string BarsUnusable = "market_bars_unusable";

    /// <summary>The strategy's signal timeframe has no bars or indicator values for the stock.</summary>
    public const string SignalTimeframeMissing = "missing_signal_timeframe";

    /// <summary>The strategy's execution timeframe has no bars or indicator values for the stock.</summary>
    public const string ExecutionTimeframeMissing = "missing_execution_timeframe";

    /// <summary>Too few completed bars before the window to warm the strategy's indicators.</summary>
    public const string InsufficientHistory = "insufficient_indicator_warmup";

    /// <summary>The five-minute bars an intraday version needs are unavailable.</summary>
    public const string FiveMinuteEvidenceUnavailable = "five_minute_market_evidence_unavailable";

    private static readonly string[] Codes =
        [BarsMissing, BarsUnusable, SignalTimeframeMissing, ExecutionTimeframeMissing, InsufficientHistory, FiveMinuteEvidenceUnavailable];

    /// <summary>True when the failure message names one of the data-unavailable reasons (the code alone or the code and a colon).</summary>
    public static bool IsNamedReason(string? message) => message is not null && Codes.Any(code =>
        message.Equals(code, StringComparison.Ordinal) || message.StartsWith(code + ":", StringComparison.Ordinal));
}
