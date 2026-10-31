using TradingFlow.Domain.Strategies;
using System.Text.Json.Serialization;

namespace TradingFlow.Domain.Backtesting;

/// <summary>Versioned execution assumptions, separate from immutable strategy rules.</summary>
public static class ExecutionReplayPolicy
{
    public const string DailyRthFiveMinuteV1 = "daily-rth5m-nextslice-ioc-v1";
    public const string DailyRthSipPortfolioV1 = "daily-rth-sip-portfolio-native-v1";
    public const string IntradayRthFiveMinuteV1 = "intraday-rth5m-limit-portfolio-v1";
    public const string IntradayRthFiveMinuteStopMarketV1 = "intraday-rth5m-stop-market-portfolio-v1";
    public const string IntradayRthFiveMinuteNextBarMarketV1 = "intraday-rth5m-next-open-market-portfolio-v1";
    public const string IntradayRthFiveMinuteQuoteMarketV1 = "intraday-rth5m-sip-market-portfolio-v1";

    /// <summary>Identify policies that require verified historical trade/quote execution without candle fallback.</summary>
    public static bool IsEventPortfolio(string? policy) => policy is DailyRthSipPortfolioV1 or IntradayRthFiveMinuteQuoteMarketV1;

    /// <summary>Bind each explicit order contract to its immutable replay assumptions.</summary>
    public static string ForEntryMode(IntradayEntryOrderMode mode) => mode switch
    {
        IntradayEntryOrderMode.NextBarLimit => IntradayRthFiveMinuteV1,
        IntradayEntryOrderMode.StopMarket => IntradayRthFiveMinuteStopMarketV1,
        IntradayEntryOrderMode.NextBarMarket => IntradayRthFiveMinuteNextBarMarketV1,
        _ => throw new InvalidOperationException("Unsupported intraday entry order mode.")
    };

    public const string PythonZoneBounceBreakdownV1 = "python-zone-bounce-breakdown-v1";
    public const string PythonZoneVolumeSpreadV1 = "python-zone-volume-spread-v1";

    /// <summary>Identify candle zone execution without changing legacy entry-mode bindings.</summary>
    public static bool IsPythonZone(string? policy) => policy is PythonZoneBounceBreakdownV1 or PythonZoneVolumeSpreadV1;

    /// <summary>Identify the ordered, quantity-dependent volume/spread comparison without changing earlier zone rules.</summary>
    public static bool IsPythonZoneVolumeSpread(string? policy) => policy == PythonZoneVolumeSpreadV1;

    public const string Timeframe = "5m";
}

/// <summary>Frozen completed-candle order intent; prices in its future execution bar never size this order.</summary>
public sealed record IntradayExecutionEvidence(decimal LimitPrice, decimal TickSize,
    DateTimeOffset ActiveAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset SessionDeadlineUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? PreviousSessionHigh,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? PreviousSessionLow, DateTimeOffset BreakoutTimestampUtc,
    DateTimeOffset ConfirmationTimestampUtc, string EntryPattern)
{
    /// <summary>Inclusive last actual entry opening, separate from the later liquidation deadline.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EntryCutoffUtc { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IntradayEntryOrderMode EntryOrderMode { get; init; } = IntradayEntryOrderMode.NextBarLimit;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? BreakoutTriggerPrice { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? OpeningRangeHigh { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? OpeningRangeLow { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? OpeningRangeOpen { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? OpeningRangeClose { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? OpeningRangeCompletedAtUtc { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? OpeningRelativeVolume { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OpeningVolumeBaselineSampleCount { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MaxFilledParentTradesPerSession { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequireRangeReturnBeforeReentry { get; init; }
}

public sealed record CandidateExecutionReplay(
    string PolicyId,
    DateTimeOffset DailyEntrySourceUtc,
    DateTimeOffset DailyExitSourceUtc,
    DateTimeOffset TerminalOpenUtc,
    IReadOnlyList<DateTimeOffset> RequiredSliceStarts)
{
    /// <summary>Exclusive frozen event coverage deadline; unresolved routes cannot roll into a later archive window.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EventExecutionEndUtc { get; init; }
}
