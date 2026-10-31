using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

/// <summary>Freezes every literal price/volume-qualified alternative before portfolio sizing or a future open is visible.</summary>
internal static partial class PythonZoneVolumeSpreadPatterns
{
    // Preserve per-zone long-then-short iteration; account-dependent ticket-fee qualification belongs to admission.
    internal static IntradayZoneDecisionContext Evaluate(IntradayZoneSnapshot snapshot, OhlcvBar previous, OhlcvBar bar,
        IndicatorSnapshot indicator, IntradayZoneSessionContext session, DateTimeOffset asOfUtc, decimal tickSize,
        IntradayZoneRules rules, IReadOnlyList<IntradayZoneArmedRetestEvidence>? armedRetests = null)
    {
        var parameters = rules.PythonVolumeSpreadComparison!;
        var halfSpread = parameters.SyntheticSpreadBps / 20000m;
        var bid = bar.Close * (1m - halfSpread);
        var ask = bar.Close * (1m + halfSpread);
        var spread = (ask - bid) / ((ask + bid) / 2m) * 10000m;
        if (bid <= 0m || spread > parameters.MaximumSpreadBps)
            return new(snapshot, null, "python_zone_estimated_spread_exceeds_limit");
        if (!PythonZoneComparisonEvaluator.HasVerifiedSlotVolume(indicator, bar))
            return new(snapshot, null, "python_zone_volume_unavailable");
        if (!parameters.LongVolumeIsDiagnostic && indicator.SlotRelativeVolume < parameters.MinimumRelativeVolume)
            return new(snapshot, null, "python_zone_relative_volume_below_minimum");
        var zones = snapshot.Zones.Where(zone => zone.PivotCount >= parameters.MinimumPivotCount && zone.KnownAtUtc <= bar.Timestamp)
            .OrderBy(zone => zone.Bottom).ToArray();
        var alternatives = new List<IntradayZoneSignal>();
        if (zones.Length >= parameters.MinimumZoneCount)
        {
            if (parameters.UsesBreakoutRetestEntry)
                CollectRetestAlternatives(snapshot, zones, armedRetests ?? [], previous, bar, indicator, session, asOfUtc, tickSize,
                    rules, bid, ask, spread, alternatives);
            else CollectAlternatives(zones, previous, bar, indicator, session, asOfUtc, tickSize, rules, bid, ask, spread, alternatives);
        }
        return alternatives.Count == 0 ? new(snapshot, null, parameters.UsesBreakoutRetestEntry
                ? "python_zone_no_qualified_breakout_retest_or_breakdown" : "python_zone_no_qualified_bounce_or_breakdown") :
            new(snapshot, alternatives[0], OrderedAlternatives: Array.AsReadOnly(alternatives.ToArray()));
    }

    // Target searches retain the supplied suffix/prefix and their bottom order rather than sorting by distance.
    private static void CollectAlternatives(IntradayZoneEvidence[] zones, OhlcvBar previous, OhlcvBar bar,
        IndicatorSnapshot indicator, IntradayZoneSessionContext session, DateTimeOffset asOfUtc, decimal tickSize,
        IntradayZoneRules rules, decimal bid, decimal ask, decimal spread, List<IntradayZoneSignal> alternatives)
    {
        for (var index = 0; index < zones.Length; index++)
        {
            var zone = zones[index];
            var longFacts = PatternTrace(true, zone, zones, previous, bar, indicator, tickSize, rules.PythonVolumeSpreadComparison!, true);
            if (longFacts.Reason is null or "risk_target_pending_sizing")
            {
                AddAlternative(true, zone, longFacts.TargetZone, previous, bar, indicator, session, asOfUtc, tickSize, rules, bid, ask, spread, alternatives);
            }
            var shortFacts = PatternTrace(false, zone, zones, previous, bar, indicator, tickSize, rules.PythonVolumeSpreadComparison!, true);
            if (shortFacts.Reason is null or "risk_target_pending_sizing")
            {
                AddAlternative(false, zone, shortFacts.TargetZone, previous, bar, indicator, session, asOfUtc, tickSize, rules, bid, ask, spread, alternatives);
            }
        }
    }

    // Freeze rounded stop/target geometry and unrounded synthetic quotes once; the simulator charges spread separately.
    private static void AddAlternative(bool isLong, IntradayZoneEvidence zone, IntradayZoneEvidence? target,
        OhlcvBar previous, OhlcvBar bar, IndicatorSnapshot indicator, IntradayZoneSessionContext session, DateTimeOffset asOfUtc,
        decimal tickSize, IntradayZoneRules rules, decimal bid, decimal ask, decimal spread, List<IntradayZoneSignal> alternatives,
        IntradayZoneRetestEvidence? retest = null)
    {
        var parameters = rules.PythonVolumeSpreadComparison!;
        if (target is null && !parameters.UsesFullCostTargets) return;
        var entry = isLong ? ask : bid;
        var geometry = Geometry(isLong, zone, target, previous, bar, indicator.Atr!.Value, tickSize, parameters, entry);
        if (geometry.Reason is not null) return;
        var estimate = new PythonZoneVolumeSpreadEvidence(bid, ask, spread, parameters.MaximumSpreadBps,
            parameters.MinimumRewardRisk, alternatives.Count);
        var evidence = new IntradayZoneSignalEvidence(zone, retest?.ArmTransition.FlippedAtUtc, asOfUtc, indicator.Vwap,
            indicator.SlotRelativeVolume, indicator.SlotMedianVolume, indicator.SlotRelativeVolumeSampleCount,
            indicator.MarketEvidenceProfileVersion, previous, indicator.Atr, target, geometry.RawStop, geometry.RawTarget, rules.PolicyId,
            session.OpenUtc, session.CloseUtc, estimate,
            parameters.UsesFullCostTargets ? new(target is null ? "risk_derived" : "known_zone", parameters.FallbackNetRiskMultiple) : null,
            parameters.LongBounceVolumeMode, IntradayZoneStopGeometry.Basis(isLong, parameters.LongStopBasis), retest);
        alternatives.Add(new(isLong ? "long" : "short", isLong ? retest is not null ? "breakout_retest" : "python_support_bounce" : "python_zone_breakdown", bar,
            bar.Close, geometry.Stop, tickSize, asOfUtc, session.DeadlineUtc, evidence, rules.MaxFilledParentTradesPerSession,
            session.CloseUtc.AddMinutes(-parameters.EntryCutoffMinutesBeforeClose), geometry.Target));
    }

    // Retain compact source predicates for every completed evaluation, including absent targets and early evidence rejections.
    internal static IntradayZoneEvaluationTrace CreateTrace(IntradayZoneDecisionContext context, OhlcvBar? previous,
        OhlcvBar bar, IndicatorSnapshot? indicator, DateTimeOffset asOfUtc, decimal tickSize, IntradayZoneRules rules,
        IntradayZoneSessionContext session)
    {
        var p = rules.PythonVolumeSpreadComparison!;
        var eligible = context.Snapshot.Zones.Where(z => z.PivotCount >= p.MinimumPivotCount && z.KnownAtUtc <= bar.Timestamp)
            .OrderBy(z => z.Bottom).ToArray();
        var volumeValid = indicator is not null && PythonZoneComparisonEvaluator.HasVerifiedSlotVolume(indicator, bar);
        var traces = new List<IntradayZonePatternTrace>();
        foreach (var zone in context.Snapshot.Zones.OrderBy(z => z.Bottom))
        {
            if (!p.UsesBreakoutRetestEntry)
                traces.Add(PatternTrace(true, zone, eligible, previous, bar, indicator, tickSize, p, volumeValid));
            else if (context.ArmedRetests?.Any(arm => arm.Zone.ZoneId == zone.ZoneId && arm.Zone.Role == IntradayZoneRole.Support) != true)
                traces.Add(new(zone, "breakout_retest", "long", null, null, null, volumeValid, null, false, null,
                    context.RetestConsumptions?.Any(item => item.ArmedRetest.Zone.ZoneId == zone.ZoneId) == true
                        ? "breakout_retest_source_consumed_at_emission" : "breakout_retest_source_not_armed"));
            traces.Add(PatternTrace(false, zone, eligible, previous, bar, indicator, tickSize, p, volumeValid));
        }
        if (p.UsesBreakoutRetestEntry)
        {
            foreach (var arm in context.ArmedRetests ?? [])
                if (arm.Zone.Role == IntradayZoneRole.Support)
                    traces.Add(RetestTrace(context.Snapshot, arm, eligible, previous, bar, indicator, tickSize, rules, session, volumeValid));
            foreach (var cancellation in context.RetestCancellations ?? [])
                if (cancellation.ArmedRetest.Zone.Role == IntradayZoneRole.Support)
                    traces.Add(new(cancellation.ArmedRetest.Zone, "breakout_retest", "long", null, null, null,
                        volumeValid, null, false, null, cancellation.Reason));
        }
        return new(bar.Ticker, asOfUtc, bar, previous, context.Snapshot.AsOfUtc, context.Snapshot.CoverageKnown,
            p.LongBounceVolumeMode ?? "threshold", p.TargetPolicy ?? "next_zone_only", indicator?.SlotRelativeVolume,
            indicator?.SlotMedianVolume, indicator?.SlotRelativeVolumeSampleCount ?? 0, indicator?.Atr, context.RejectionReason, traces,
            context.RetestCancellations, context.RetestConsumptions);
    }

    // Explain the same literal long/short predicates and frozen suffix/prefix target search without using later bars.
    private static IntradayZonePatternTrace PatternTrace(bool isLong, IntradayZoneEvidence zone,
        IntradayZoneEvidence[] eligible, OhlcvBar? previous, OhlcvBar bar, IndicatorSnapshot? indicator, decimal tickSize,
        PythonZoneVolumeSpreadComparisonRules p, bool volumeValid)
    {
        var previousPass = previous is null ? (bool?)null : isLong ? previous.Low <= zone.Top + p.LongTouchDollarTolerance : previous.Close >= zone.Bottom;
        var beyond = isLong ? bar.Close > zone.Top : bar.Close < zone.Bottom;
        var direction = isLong ? bar.Close > bar.Open : bar.Close < bar.Open;
        var volumePass = volumeValid ? indicator!.SlotRelativeVolume >= p.MinimumRelativeVolume : (bool?)null;
        var required = !isLong || !p.LongVolumeIsDiagnostic;
        var index = Array.FindIndex(eligible, z => z.ZoneId == zone.ZoneId && z.Version == zone.Version);
        var quote = bar.Close * (1m + (isLong ? 1 : -1) * p.SyntheticSpreadBps / 20000m);
        var target = index < 0 ? null : isLong ? eligible.Skip(index + 1).FirstOrDefault(z => z.Bottom > quote)
            : eligible.Take(index).LastOrDefault(z => z.Top < quote);
        var reason = index < 0 ? "zone_not_eligible_at_confirmation_start" : eligible.Length < p.MinimumZoneCount ? "insufficient_eligible_zones"
            : isLong && zone.Role != IntradayZoneRole.Support ? "source_zone_not_support"
            : previousPass != true ? "previous_bar_predicate_not_met" : !beyond ? "close_not_beyond_zone" : !direction ? "candle_direction_not_met"
            : !volumeValid ? "python_zone_volume_unavailable" : required && volumePass != true ? "python_zone_relative_volume_below_minimum"
            : target is null ? p.UsesFullCostTargets ? "risk_target_pending_sizing" : "next_zone_target_unavailable" : null;
        var geometry = previous is not null && indicator?.Atr is > 0m && tickSize > 0m
            ? Geometry(isLong, zone, target, previous, bar, indicator!.Atr.GetValueOrDefault(), tickSize, p, quote)
            : ((decimal RawStop, decimal Stop, decimal? RawTarget, decimal? Target, string? Reason)?)null;
        if (reason is null or "risk_target_pending_sizing")
            reason = geometry is null ? "python_zone_atr_or_tick_unavailable" : geometry.Value.Reason ?? reason;
        return new(zone, isLong ? "python_support_bounce" : "python_zone_breakdown", isLong ? "long" : "short",
            previousPass, beyond, direction, volumeValid, volumePass, required, target, reason, tickSize > 0 ? tickSize : null,
            geometry?.RawStop, geometry?.Stop, geometry?.RawTarget, geometry?.Target,
            IntradayZoneStopGeometry.Basis(isLong, p.LongStopBasis));
    }

    // Compute the same legal executable geometry for strategy admission and rejection traces; rounding can invalidate an otherwise favourable raw target.
    private static (decimal RawStop, decimal Stop, decimal? RawTarget, decimal? Target, string? Reason) Geometry(bool isLong,
        IntradayZoneEvidence zone, IntradayZoneEvidence? targetZone, OhlcvBar previous, OhlcvBar confirmation,
        decimal atr, decimal tick, PythonZoneVolumeSpreadComparisonRules parameters, decimal entryQuote)
    {
        var (rawStop, stop) = IntradayZoneStopGeometry.Calculate(isLong, zone, previous, confirmation, atr, tick,
            parameters.StopAtrMultiple, parameters.LongStopBasis);
        var rawTarget = targetZone is null ? (decimal?)null : isLong ? targetZone.Bottom : targetZone.Top;
        var target = rawTarget is { } value ? (isLong ? decimal.Floor(value / tick) : decimal.Ceiling(value / tick)) * tick : (decimal?)null;
        var reason = stop <= 0m || (isLong ? stop >= entryQuote : stop <= entryQuote) ? "rounded_structural_stop_invalid"
            : target is { } price && (price <= 0m || (isLong ? price <= entryQuote : price >= entryQuote)) ? "rounded_target_not_favourable" : null;
        return (rawStop, stop, rawTarget, target, reason);
    }
}
