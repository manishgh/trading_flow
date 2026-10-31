using System.Globalization;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Contracts.Presentation;

/// <summary>Displays the server's frozen intraday intent and observed lifecycle identically in Web and Android.</summary>
public static class IntradayDecisionPresentation
{
    /// <summary>Formats audited prices and UTC source clocks without applying any entry, protection or exit rules in the client.</summary>
    public static IReadOnlyList<string> Lines(RunIntradayDecisionDetail detail)
    {
        var nextOpen = detail.EntryOrderMode == "next_bar_market";
        var openingRange = nextOpen || detail.EntryOrderMode == "stop_market";
        var lines = new List<string> {
            $"{detail.Ticker} · {detail.Direction} · {detail.EntryMethod} · {detail.Status}"
        };
        if (openingRange)
        {
            lines.Add($"Opening range high / low: {Price(detail.OpeningRangeHigh)} / {Price(detail.OpeningRangeLow)} (observed) · Completed UTC: {Utc(detail.OpeningRangeCompletedAtUtc)}");
            lines.Add($"Opening open / close: {Price(detail.OpeningRangeOpen)} / {Price(detail.OpeningRangeClose)} · Permitted direction: {detail.Direction}");
            lines.Add($"Frozen opening relative volume: {Price(detail.OpeningRelativeVolume)} · Baseline valid sessions: {detail.OpeningVolumeBaselineSampleCount?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} / 20 (median)");
            if (nextOpen)
            {
                lines.Add($"Entry order: next bar opening market; Completed breakout candle starts UTC: {Utc(detail.ConfirmationTimestampUtc)}; Close/sizing reference: {Price(detail.LimitPrice)}; Valid opening trigger (strict close required): {Price(detail.TriggerPrice)}; Actual average entry: {Price(detail.ActualEntryPrice)}");
                lines.Add("Stop: completed breakout candle low (long) / high (short), rounded outward to valid tick; no added buffer. Earliest entry 09:50 New York. Market remainder persists until fill, protection or session deadline; opening capacity uses prior completed volume.");
            }
            else lines.Add($"Entry order: stop-market · Submitted trigger price: {Price(detail.TriggerPrice)} · Actual average entry: {Price(detail.ActualEntryPrice)}");
            lines.Add($"Filled parent trade limit per stock/session: {detail.MaxFilledParentTradesPerSession} · Return inside frozen range required before re-entry: {detail.RequireRangeReturnBeforeReentry}");
            lines.Add($"Previous session context high / low: {Price(detail.PreviousSessionHigh)} / {Price(detail.PreviousSessionLow)}");
        }
        else
        {
            lines.Add($"Previous session high / low: {Price(detail.PreviousSessionHigh)} / {Price(detail.PreviousSessionLow)}");
            lines.Add($"Breakout candle B starts UTC: {Utc(detail.BreakoutTimestampUtc)} · Confirmation candle C starts UTC: {Utc(detail.ConfirmationTimestampUtc)}");
            lines.Add($"Limit price: {Price(detail.LimitPrice)} · Actual average entry: {Price(detail.ActualEntryPrice)}");
        }
        lines.AddRange([
            $"Entry active UTC: {Utc(detail.ActiveAtUtc)} · Entry expires UTC: {Utc(detail.ExpiresAtUtc)}",
            $"Session exit deadline UTC: {Utc(detail.SessionDeadlineUtc)}",
            $"Frozen structural stop: {Price(detail.FrozenStopPrice)}{(openingRange ? " (submitted)" : "")} · {(detail.Status == "closed" ? "Last active" : "Current/planned")} stop: {Price(detail.CurrentStopPrice)}",
            $"Target (planned/last active): {Price(detail.TargetPrice)}",
            $"Exit / resolution reason: {detail.ResolutionReason} · Last observed UTC: {Utc(detail.LastObservedAtUtc)}",
            $"Strategy: {detail.StrategyId} · Candidate: {detail.CandidateId}"
        ]);
        if (detail.ProtectionEffectiveAfterUtc is { } effective)
            lines.Add($"Latest protection decision becomes eligible only after UTC: {Utc(effective)}; it cannot execute inside its source candle.");
        return lines;
    }

    /// <summary>Retains valid decimal precision and explicitly identifies an absent fill or target.</summary>
    private static string Price(decimal? value) => value?.ToString("0.############################", CultureInfo.InvariantCulture) ?? "not placed / unavailable";

    /// <summary>Labels every source and decision clock in UTC instead of silently using the device timezone.</summary>
    private static string Utc(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "unavailable";
}
