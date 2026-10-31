using System.Globalization;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Contracts.Presentation;

/// <summary>Displays audited zone and execution facts consistently in Web and Android.</summary>
public static class IntradayZoneDecisionPresentation
{
    // Present frozen prices and clocks; clients never qualify patterns or infer executions.
    public static IReadOnlyList<string> Lines(RunZoneDecisionDetail item)
    {
        var volumeSpread = item.ZonePolicyId == "python-zone-volume-spread-v1";
        var comparison = volumeSpread || item.ZonePolicyId == "python-zone-bounce-breakdown-v1";
        var lines = new List<string>
        {
            $"{item.Ticker} | {item.Direction} | {item.Pattern} | {item.Status} | {item.ResolutionReason}",
            $"15m zone {item.ZoneId} v{item.ZoneVersion} | {item.ZoneRole} [{Price(item.ZoneBottom)}, {Price(item.ZoneTop)}] | {item.PivotCount} confirmed pivots" +
                (comparison ? " (merged-pivot approximation of Python touches)" : ""),
            $"Pivot candle starts UTC {Clock(item.PivotBarStartUtc)} | completed {Clock(item.PivotCompletedAtUtc)} | confirmed {Clock(item.PivotConfirmedAtUtc)}",
            $"Zone version known UTC {Clock(item.ZoneKnownAtUtc)} | flip UTC {Clock(item.FlippedAtUtc)}",
            $"5m confirmation starts UTC {Clock(item.ConfirmationBarStartUtc)} | completed {Clock(item.ConfirmationCompletedAtUtc)} | close {Price(item.ConfirmationClose)}",
            FilterLine(item, comparison, volumeSpread),
            $"{StopBasisLabel(item, comparison)} stop {Price(item.FrozenStopPrice)} | actual entry {Price(item.ActualEntryPrice)} | actual exit {Price(item.ActualExitPrice)} | deadline UTC {Clock(item.SessionDeadlineUtc)}"
        };
        if (comparison)
        {
            if (item.StopBasis is not null)
                lines.Add($"Stop basis: {StopBasisLabel(item, comparison)} | previous low {Price(item.PreviousLow)} | confirmation low {Price(item.ConfirmationLow)} | raw stop {Price(item.RawStopPrice)} | rounded stop {Price(item.FrozenStopPrice)} | tick {Price(item.TickSize)}");
            lines.Add($"Fixed target {Price(item.FixedTargetPrice)} | completed 5m ATR14 {Price(item.Atr)} | " +
                (item.TargetZoneId is { } id ? $"target reason: next zone {id} [{Price(item.TargetZoneBottom)}, {Price(item.TargetZoneTop)}]"
                    : item.TargetPlan?.TargetOrigin == "risk_derived" ? "no eligible target zone; risk-derived target" : "target zone unavailable"));
            lines.Add($"Entry cutoff UTC {Clock(item.EntryCutoffUtc)} (inclusive) | fixed stop and target; no 3R or continuation protection");
        }
        if (volumeSpread) AddVolumeSpreadEvidence(lines, item);
        if (item.Retest is { } retest) AddRetestEvidence(lines, item, retest);
        if (item.TargetPlan is { } target) AddTargetPlanning(lines, target);
        lines.Add($"{(comparison ? "Five-minute candle execution diagnostic" : "Historical SIP quote/trade execution")} | Strategy {item.StrategyId} | Candidate {item.CandidateId}");
        return lines;
    }

    // Name the actual selected stop basis without recomputing its geometry on a client.
    private static string StopBasisLabel(RunZoneDecisionDetail item, bool comparison) =>
        item.StopBasis == "two_candle_low" ? "Lower low of the two signal candles minus ATR buffer" :
        comparison ? "Fixed source-zone/ATR" : "Candle";

    // Display the selected recipe's volume treatment; no client-side filter is applied.
    private static string FilterLine(RunZoneDecisionDetail item, bool comparison, bool volumeSpread) =>
        volumeSpread
            ? $"Confirmation slot RVOL {Price(item.SlotRelativeVolume)} ({(item.LongBounceVolumeMode == "diagnostic_only" ? "long volume diagnostic only; short minimum 1.2; valid volume evidence required on both sides" : "both directions minimum 1.2")}) | same-clock median volume {Price(item.SlotMedianVolume)} | prior valid sessions {item.SlotSampleCount}/20 | no VWAP or overextension filter"
        : comparison && item.Direction == "long"
            ? $"Regular-session VWAP {Price(item.Vwap)} | long entry has no volume filter"
            : $"Regular-session VWAP {Price(item.Vwap)} | confirmation slot RVOL {Price(item.SlotRelativeVolume)}" +
                (comparison ? " (short minimum 1.2)" : "") +
                $" | same-clock median volume {Price(item.SlotMedianVolume)} | prior valid sessions {item.SlotSampleCount}/20";

    // Explain the frozen breakout box and later active version without making new entry decisions on either client.
    private static void AddRetestEvidence(ICollection<string> lines, RunZoneDecisionDetail item, RunZoneRetestDetail retest)
    {
        lines.Add($"Breakout and retest: resistance v{retest.ResistanceVersion} [{Price(retest.ResistanceBottom)}, {Price(retest.ResistanceTop)}] known UTC {Clock(retest.ResistanceKnownAtUtc)}; flip UTC {Clock(retest.FlippedAtUtc)} | flip ATR15 {Price(retest.FlipAtr)} | arm eligible {retest.CanArmRetest}");
        lines.Add($"Frozen armed source {item.ZoneId} v{item.ZoneVersion} [{Price(item.ZoneBottom)}, {Price(item.ZoneTop)}] | active source v{retest.ActiveSourceVersion} [{Price(retest.ActiveSourceBottom)}, {Price(retest.ActiveSourceTop)}] known UTC {Clock(retest.ActiveSourceKnownAtUtc)}");
        lines.Add($"Confirmation-start snapshot UTC {Clock(retest.ConfirmationSnapshotAsOfUtc)} | target version {item.TargetZoneVersion?.ToString(CultureInfo.InvariantCulture) ?? "none"} known UTC {Clock(item.TargetZoneKnownAtUtc)} | emitted long setup consumed even if admission rejects it");
    }

    // Display ordered admission decisions and make the zero threshold's disabled filter explicit while preserving audited ratios.
    private static void AddVolumeSpreadEvidence(ICollection<string> lines, RunZoneDecisionDetail item)
    {
        if (item.VolumeSpread is { } estimate)
        {
            lines.Add($"Synthetic bid {Price(estimate.BidPrice)} / ask {Price(estimate.AskPrice)} | estimated full spread {Price(estimate.FullSpreadBps)} bps (maximum {Price(estimate.MaximumSpreadBps)}) | no historical quote spread test");
            var filter = estimate.MinimumRewardRisk == 0m ? "minimum reward/risk filter disabled" : $"minimum {Price(estimate.MinimumRewardRisk)}";
            if (item.TargetPlan is null)
                lines.Add($"Alternative index {estimate.OrderedAlternativeIndex} | capped planned quantity {Price(estimate.PlannedQuantity)} | estimated round-trip ticket fees {Price(estimate.EstimatedRoundTripTicketFees)} | estimated risk {Price(estimate.EstimatedNetRiskDollars)} / reward {Price(estimate.EstimatedNetRewardDollars)} | estimated reward/risk after ticket fees {Price(estimate.SelectedRewardRisk)} ({filter})");
        }
        foreach (var alternative in item.AlternativeDecisions ?? [])
        {
            lines.Add($"Alternative index {alternative.AlternativeIndex} | {alternative.Direction} | zone {alternative.ZoneId} | planned quantity {Price(alternative.PlannedQuantity)} | estimated reward/risk {Price(alternative.RewardRisk)} | {(alternative.Selected ? "selected" : "rejected")}: {alternative.Reason}");
            if (alternative.TargetPlan is { } target) AddTargetPlanning(lines, target);
        }
        if (item.TargetPlan is null)
            lines.Add("The admission ratio omits exit spread, slippage and regulatory fees. Candle execution separately charges the approved synthetic spread once, per-side slippage and configured fees.");
    }

    // Separate the unchanged sizing budget from the full-cost denominator used for a prospective target.
    private static void AddTargetPlanning(ICollection<string> lines, RunZoneTargetPlanningDetail target)
    {
        lines.Add($"Target origin: {target.TargetOrigin} | frozen target {Price(target.ResolvedTargetPrice)} | no-zone estimated net multiple {Price(target.FallbackNetRiskMultiple)}R; realized reward/risk can differ.");
        lines.Add($"Planned whole shares {Price(target.PlannedQuantity)} | planner loss budget use {Price(target.PlannerLossDollars)} / budget {Price(target.PlannerRiskBudgetDollars)} | full-cost stop loss estimate {Price(target.EstimatedNetStopRisk)}");
        lines.Add($"Prospective entry {Price(target.EstimatedEntryFillPrice)} / stop fill {Price(target.EstimatedStopFillPrice)} | adverse costs {Price(target.AdverseCostBps)} bps | entry/stop-exit/target-exit fees {Price(target.EstimatedEntryFees)} / {Price(target.EstimatedStopExitFees)} / {Price(target.EstimatedTargetFees)}");
        lines.Add($"Estimated net target reward {Price(target.EstimatedNetReward)} | estimated net reward/risk {Price(target.EstimatedNetRewardRisk)}; target limit fill assumes no favorable improvement. Stops and targets are frozen before future execution bars.");
    }

    // Preserve raw decimal evidence, including exact boundary prices and absent fills.
    private static string Price(decimal? value) => value?.ToString("0.############################", CultureInfo.InvariantCulture) ?? "unavailable";

    // Keep market-data clocks independent from the operator device timezone.
    private static string Clock(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "none";
}
