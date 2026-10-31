using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

internal static partial class PythonZoneVolumeSpreadPatterns
{
    // Mix frozen long sources and current short sources by literal bottom, retaining long-before-short ties.
    private static void CollectRetestAlternatives(IntradayZoneSnapshot snapshot, IntradayZoneEvidence[] zones,
        IReadOnlyList<IntradayZoneArmedRetestEvidence> arms, OhlcvBar previous, OhlcvBar bar, IndicatorSnapshot indicator,
        IntradayZoneSessionContext session, DateTimeOffset asOfUtc, decimal tickSize, IntradayZoneRules rules,
        decimal bid, decimal ask, decimal spread, List<IntradayZoneSignal> alternatives)
    {
        var sources = arms.Where(arm => arm.Zone.Role == IntradayZoneRole.Support)
            .Select(arm => (Zone: arm.Zone, Arm: (IntradayZoneArmedRetestEvidence?)arm))
            .Concat(zones.Select(zone => (Zone: zone, Arm: (IntradayZoneArmedRetestEvidence?)null)))
            .OrderBy(source => source.Zone.Bottom).ThenBy(source => source.Arm is null ? 1 : 0);
        foreach (var source in sources)
        {
            var trace = source.Arm is { } arm ? RetestTrace(snapshot, arm, zones, previous, bar, indicator, tickSize, rules, session, true)
                : PatternTrace(false, source.Zone, zones, previous, bar, indicator, tickSize, rules.PythonVolumeSpreadComparison!, true);
            if (trace.Reason is not (null or "risk_target_pending_sizing")) continue;
            AddAlternative(source.Arm is not null, source.Zone, trace.TargetZone, previous, bar, indicator, session,
                asOfUtc, tickSize, rules, bid, ask, spread, alternatives, trace.Retest);
        }
    }

    // Resolve targets against the current eligible snapshot, excluding the frozen source ID without requiring its old version index.
    private static IntradayZonePatternTrace RetestTrace(IntradayZoneSnapshot snapshot, IntradayZoneArmedRetestEvidence arm,
        IntradayZoneEvidence[] eligible, OhlcvBar? previous, OhlcvBar bar, IndicatorSnapshot? indicator, decimal tickSize,
        IntradayZoneRules rules, IntradayZoneSessionContext session, bool volumeValid)
    {
        var p = rules.PythonVolumeSpreadComparison!;
        var zone = arm.Zone;
        var active = snapshot.Zones.FirstOrDefault(current => current.ZoneId == zone.ZoneId && current.Role == IntradayZoneRole.Support && current.KnownAtUtc <= bar.Timestamp);
        var retest = active is null ? null : new IntradayZoneRetestEvidence(arm.Transition, active, snapshot.AsOfUtc);
        var quote = bar.Close * (1m + p.SyntheticSpreadBps / 20000m);
        var target = eligible.FirstOrDefault(current => current.ZoneId != zone.ZoneId && current.Bottom > quote);
        var overlap = bar.Low <= zone.Top && bar.High >= zone.Bottom;
        var beyond = bar.Close > zone.Top;
        var validArm = arm.SessionDate == session.SessionDate && retest is not null &&
            IntradayZoneRetestEvidenceValidator.MatchesArm(retest, zone, bar, session.OpenUtc, session.CloseUtc, rules);
        var reason = !validArm ? "breakout_retest_arm_or_active_source_invalid"
            : eligible.Length < p.MinimumZoneCount ? "insufficient_eligible_zones"
            : !overlap ? "breakout_retest_actual_overlap_required" : !beyond ? "breakout_retest_close_not_above_frozen_top"
            : !volumeValid ? "python_zone_volume_unavailable" : target is null ? "risk_target_pending_sizing" : null;
        var geometry = previous is not null && indicator?.Atr is > 0m && tickSize > 0
            ? Geometry(true, zone, target, previous, bar, indicator.Atr.Value, tickSize, p, quote)
            : ((decimal RawStop, decimal Stop, decimal? RawTarget, decimal? Target, string? Reason)?)null;
        if (reason is null or "risk_target_pending_sizing")
            reason = geometry is null ? "python_zone_atr_or_tick_unavailable" : geometry.Value.Reason ?? reason;
        return new(zone, "breakout_retest", "long", overlap, beyond, null, volumeValid,
            volumeValid ? indicator!.SlotRelativeVolume >= p.MinimumRelativeVolume : null, false, target, reason,
            tickSize > 0 ? tickSize : null, geometry?.RawStop, geometry?.Stop, geometry?.RawTarget, geometry?.Target,
            IntradayZoneStopGeometry.SourceZone, retest);
    }

}
