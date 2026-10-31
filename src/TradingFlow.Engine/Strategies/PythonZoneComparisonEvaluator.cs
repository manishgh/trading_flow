using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Engine.Strategies;

/// <summary>Literal asymmetric Python entry comparison over the existing causal fifteen-minute boxes.</summary>
public sealed class PythonZoneComparisonEvaluator
{
    private readonly IntradayZoneRules _rules;
    private OhlcvBar? _lastBar;
    private DateTimeOffset? _lastSessionClose;
    private IntradayZoneDecisionContext? _lastDecision;
    private bool _coverageKnown = true;
    private readonly IntradayZoneRetestState _retests = new();

    // Keep every numeric threshold in the immutable recipe rather than in the shared engine.
    public PythonZoneComparisonEvaluator(IntradayZoneRules rules)
    {
        rules.ValidateParameters();
        if (!rules.IsPythonFixedTargetComparison) throw new ArgumentException("Python comparison requires its own policy.", nameof(rules));
        _rules = rules;
    }

    // A rejected conflicting/gapped 15m update poisons future entries even if the last retained snapshot was healthy.
    public void ObserveZoneUpdate(IntradayZoneUpdate update)
    {
        if (_rules.PythonVolumeSpreadComparison?.UsesBreakoutRetestEntry == true) _retests.Observe(update);
        if (!update.Snapshot.CoverageKnown) _coverageKnown = false;
    }

    // Cache completed decisions; the optional retest package consumes emitted arms while legacy policies retain stateless patterns.
    public IntradayZoneDecisionContext EvaluateCompleted5m(IntradayZoneSnapshot snapshot, OhlcvBar bar,
        IndicatorSnapshot? indicator, IntradayZoneSessionContext session, DateTimeOffset asOfUtc, decimal tickSize)
    {
        var previous = _lastBar == bar ? _lastDecision?.Trace?.PreviousBar : _lastBar;
        var decision = EvaluateCompletedCore(snapshot, bar, indicator, session, asOfUtc, tickSize);
        if (!_rules.IsPythonVolumeSpreadComparison) return decision;
        var wasCached = ReferenceEquals(decision, _lastDecision);
        if (_rules.PythonVolumeSpreadComparison!.UsesBreakoutRetestEntry && decision.ArmedRetests is null)
            decision = decision with { ArmedRetests = CurrentLongArms(), RetestCancellations = Array.AsReadOnly(_retests.Cancellations.ToArray()),
                RetestConsumptions = Array.AsReadOnly(_retests.Consumptions.ToArray()) };
        var traced = decision with { Trace = PythonZoneVolumeSpreadPatterns.CreateTrace(decision, previous, bar, indicator, asOfUtc, tickSize, _rules, session) };
        if (wasCached) _lastDecision = traced;
        return traced;
    }

    // Advance causal state once; trace creation is separate so rejected bars retain evidence without changing strategy decisions.
    private IntradayZoneDecisionContext EvaluateCompletedCore(IntradayZoneSnapshot snapshot, OhlcvBar bar,
        IndicatorSnapshot? indicator, IntradayZoneSessionContext session, DateTimeOffset asOfUtc, decimal tickSize)
    {
        if (!IntradayZoneBarEvidence.IsValid(bar, session, asOfUtc, 5, _rules.PriorSessionCount) || tickSize <= 0m ||
            snapshot.Ticker != bar.Ticker || snapshot.SessionDate != session.SessionDate || snapshot.AsOfUtc > bar.Timestamp)
            return new(snapshot, null, "python_zone_completed_5m_evidence_invalid");
        if (_lastBar == bar) return _coverageKnown && snapshot.CoverageKnown ? _lastDecision! :
            new(snapshot, null, "intraday_zone_5m_or_15m_coverage_unknown");
        var previous = _lastBar;
        if (previous is not null && (!IntradayZoneBarEvidence.SameSource(previous, bar) || bar.Timestamp <= previous.Timestamp) ||
            !IntradayZoneBarEvidence.IsNext(previous, _lastSessionClose, bar, session, 5)) _coverageKnown = false;
        _lastBar = bar;
        _lastSessionClose = session.CloseUtc;
        if (_rules.PythonVolumeSpreadComparison?.UsesBreakoutRetestEntry == true)
        {
            _retests.Activate(bar.Timestamp);
            _retests.Expire(bar, session, asOfUtc, session.CloseUtc.AddMinutes(-EntryCutoffMinutes(_rules)), inclusiveCutoff: true, activeSnapshot: snapshot);
        }
        if (!_coverageKnown || !snapshot.CoverageKnown)
            return _lastDecision = new(snapshot, null, "intraday_zone_5m_or_15m_coverage_unknown");
        if (asOfUtc > session.CloseUtc.AddMinutes(-EntryCutoffMinutes(_rules)))
            return _lastDecision = new(snapshot, null, "outside_python_zone_entry_window");
        if (!HasPriceEvidence(indicator, bar, _rules))
            return _lastDecision = new(snapshot, null, _rules.IsPythonVolumeSpreadComparison ? "python_zone_atr_unavailable" : "python_zone_atr_or_vwap_unavailable");
        if (previous is null || previous.Timestamp < session.OpenUtc || previous.Timestamp.AddMinutes(5) != bar.Timestamp)
            return _lastDecision = new(snapshot, null, "python_zone_previous_same_session_bar_unavailable");
        if (_rules.IsPythonVolumeSpreadComparison)
            return _lastDecision = EvaluateVolumeSpread(snapshot, previous, bar, indicator!, session, asOfUtc, tickSize);
        var signal = SelectSignal(snapshot, previous, bar, indicator!, session, asOfUtc, tickSize, _rules, out var missingShortVolume);
        return _lastDecision = new(snapshot, signal, signal is null ? (missingShortVolume ? "python_zone_short_volume_unavailable" : "python_zone_no_qualified_bounce_or_breakdown") : null);
    }

    // Recompute selection from supplied immutable bars, indicators and recipe without advancing any evaluator state.
    public static bool MatchesDecision(IntradayZoneDecisionContext context, OhlcvBar bar, DateTimeOffset asOfUtc,
        IndicatorSnapshot indicator, OhlcvBar? previous, IntradayZoneRules rules)
    {
        if (!rules.IsPythonFixedTargetComparison || context.Signal is not { } signal || context.RejectionReason is not null ||
            !context.Snapshot.CoverageKnown || context.Snapshot.Ticker != bar.Ticker || context.Snapshot.AsOfUtc > bar.Timestamp ||
            previous is null || bar.Timeframe != "5m" || previous.Timeframe != "5m" ||
            signal.Evidence.PreviousBar != previous || !HasPriceEvidence(indicator, bar, rules) ||
            signal.ActiveAtUtc != asOfUtc || bar.Timestamp.AddMinutes(5) != asOfUtc || signal.TickSize <= 0m ||
            previous.Timestamp.AddMinutes(5) != bar.Timestamp || !IntradayZoneBarEvidence.SameSource(previous, bar) ||
            signal.EntryCutoffUtc is not { } cutoff || asOfUtc > cutoff) return false;
        // The cutoff and liquidation clocks encode the declared calendar session used by the emitted intent.
        var close = cutoff.AddMinutes(EntryCutoffMinutes(rules));
        if (signal.Evidence.SessionOpenUtc is not { } open || signal.Evidence.SessionCloseUtc != close || open >= close ||
            open.Offset != TimeSpan.Zero || close.Offset != TimeSpan.Zero || signal.SessionDeadlineUtc != close.AddMinutes(-5) ||
            previous.Timestamp < open || asOfUtc > close || (bar.Timestamp - open).Ticks % TimeSpan.FromMinutes(5).Ticks != 0 ||
            !SameSessionDate(previous, bar, context.Snapshot.SessionDate)) return false;
        var sessionDate = context.Snapshot.SessionDate;
        var session = new IntradayZoneSessionContext(sessionDate, open, close, []);
        if (rules.IsPythonVolumeSpreadComparison)
        {
            if (rules.PythonVolumeSpreadComparison!.UsesBreakoutRetestEntry && !ValidRetestArms(context, bar, session, rules)) return false;
            var expected = PythonZoneVolumeSpreadPatterns.Evaluate(context.Snapshot, previous, bar, indicator, session, asOfUtc, signal.TickSize, rules,
                context.ArmedRetests);
            return signal == expected.Signal && context.OrderedAlternatives is { Count: > 0 } alternatives &&
                expected.OrderedAlternatives is { } expectedAlternatives && alternatives.SequenceEqual(expectedAlternatives);
        }
        return context.OrderedAlternatives is null && signal == SelectSignal(context.Snapshot, previous, bar, indicator, session, asOfUtc, signal.TickSize, rules, out _);
    }

    // Freeze the pre-emission arm set and consume every emitted long source before any account-dependent admission.
    private IntradayZoneDecisionContext EvaluateVolumeSpread(IntradayZoneSnapshot snapshot, OhlcvBar previous, OhlcvBar bar,
        IndicatorSnapshot indicator, IntradayZoneSessionContext session, DateTimeOffset asOfUtc, decimal tickSize)
    {
        if (!_rules.PythonVolumeSpreadComparison!.UsesBreakoutRetestEntry)
            return PythonZoneVolumeSpreadPatterns.Evaluate(snapshot, previous, bar, indicator, session, asOfUtc, tickSize, _rules);
        var arms = CurrentLongArms();
        var decision = PythonZoneVolumeSpreadPatterns.Evaluate(snapshot, previous, bar, indicator, session, asOfUtc, tickSize, _rules, arms)
            with { ArmedRetests = arms, RetestCancellations = Array.AsReadOnly(_retests.Cancellations.ToArray()) };
        foreach (var emitted in decision.OrderedAlternatives ?? [])
            if (emitted.Direction == "long") _retests.Consume(emitted.Evidence.Zone.ZoneId, asOfUtc);
        return decision with { RetestConsumptions = Array.AsReadOnly(_retests.Consumptions.ToArray()) };
    }

    // An arm must meet the recipe's pivot threshold at the flip; a later merge cannot retroactively qualify it.
    private IReadOnlyList<IntradayZoneArmedRetestEvidence> CurrentLongArms() => Array.AsReadOnly(_retests.Armed
        .Where(arm => arm.Zone.Role == IntradayZoneRole.Support && arm.Zone.PivotCount >= _rules.PythonVolumeSpreadComparison!.MinimumPivotCount).ToArray());

    // Check supplied immutable arm/start-snapshot evidence without invoking or consuming the live lifecycle again.
    private static bool ValidRetestArms(IntradayZoneDecisionContext context, OhlcvBar bar, IntradayZoneSessionContext session, IntradayZoneRules rules)
    {
        if (context.ArmedRetests is not { } arms || arms.Select(arm => arm.Zone.ZoneId).Distinct().Count() != arms.Count ||
            context.Snapshot.Zones.Any(zone => zone.KnownAtUtc > context.Snapshot.AsOfUtc)) return false;
        foreach (var arm in arms)
        {
            var active = context.Snapshot.Zones.FirstOrDefault(zone => zone.ZoneId == arm.Zone.ZoneId && zone.Role == IntradayZoneRole.Support);
            if (arm.SessionDate != context.Snapshot.SessionDate || active is null ||
                !IntradayZoneRetestEvidenceValidator.MatchesArm(new(arm.Transition, active, context.Snapshot.AsOfUtc), arm.Zone, bar,
                    session.OpenUtc, session.CloseUtc, rules)) return false;
        }
        return true;
    }

    // Independent kernel verification keeps previous-bar evidence inside the same New York regular session.
    private static bool SameSessionDate(OhlcvBar previous, OhlcvBar bar, DateOnly date)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var previousLocal = TimeZoneInfo.ConvertTime(previous.Timestamp, zone);
        var currentLocal = TimeZoneInfo.ConvertTime(bar.Timestamp, zone);
        return DateOnly.FromDateTime(previousLocal.DateTime) == date && DateOnly.FromDateTime(currentLocal.DateTime) == date;
    }

    // The original policy needs VWAP; the separate volume/spread recipe requires only completed positive ATR here.
    private static bool HasPriceEvidence(IndicatorSnapshot? indicator, OhlcvBar bar, IntradayZoneRules rules) =>
        IntradayZoneBarEvidence.Matches(indicator, bar) && indicator!.Atr is > 0m &&
        (rules.IsPythonVolumeSpreadComparison || indicator.Vwap is > 0m);

    // Resolve each immutable recipe's close-relative entry clock without changing the original policy.
    private static int EntryCutoffMinutes(IntradayZoneRules rules) => rules.IsPythonVolumeSpreadComparison
        ? rules.PythonVolumeSpreadComparison!.EntryCutoffMinutesBeforeClose : rules.PythonComparison!.EntryCutoffMinutesBeforeClose;

    // Verify the shared same-feed twenty-session volume baseline for the exact completed five-minute clock slot.
    internal static bool HasVerifiedSlotVolume(IndicatorSnapshot indicator, OhlcvBar bar) =>
        indicator.SlotRelativeVolume is not null && indicator.SlotMedianVolume is > 0m &&
        indicator.SlotRelativeVolume == bar.Volume / indicator.SlotMedianVolume &&
        indicator.SlotRelativeVolumeSampleCount == MarketEvidenceProfile.DefaultLookbackSessions &&
        indicator.RelativeVolumeMinimumSamples == MarketEvidenceProfile.DefaultMinimumValidSamples &&
        indicator.RelativeVolumeCohort == "regular" && indicator.MarketEvidenceReliability == "verified_same_feed" &&
        indicator.MarketEvidenceProfileVersion == MarketEvidenceProfile.ProductionVersion;

    // Preserve Python's stable bottom ordering, long then short per box, including its asymmetric target search.
    private static IntradayZoneSignal? SelectSignal(IntradayZoneSnapshot snapshot, OhlcvBar previous, OhlcvBar bar,
        IndicatorSnapshot indicator, IntradayZoneSessionContext session, DateTimeOffset asOfUtc, decimal tickSize, IntradayZoneRules rules, out bool missingShortVolume)
    {
        missingShortVolume = false;
        var parameters = rules.PythonComparison!;
        var zones = snapshot.Zones.Where(zone => zone.PivotCount >= parameters.MinimumPivotCount && zone.KnownAtUtc <= bar.Timestamp)
            .OrderBy(zone => zone.Bottom).ThenBy(zone => zone.CreatedAtUtc).ThenBy(zone => zone.ZoneId).ToArray();
        if (zones.Length < parameters.MinimumZoneCount) return null;
        var atr = indicator.Atr!.Value;
        var vwap = indicator.Vwap!.Value;
        if (Math.Abs(bar.Close - vwap) > parameters.MaximumVwapDistanceAtrMultiple * atr) return null;
        foreach (var zone in zones)
        {
            if (zone.Role == IntradayZoneRole.Support && previous.Low <= zone.Top + parameters.LongTouchDollarTolerance &&
                bar.Close > zone.Top && bar.Close > bar.Open && bar.Close >= vwap - parameters.LongVwapAllowanceAtrMultiple * atr)
            {
                var target = zones.FirstOrDefault(other => other.Bottom > bar.Close);
                if (target is not null && BuildSignal(true, zone, target, previous, bar, indicator, session, asOfUtc, tickSize, rules) is { } signal)
                    return signal;
            }
            if (previous.Close >= zone.Bottom && bar.Close < zone.Bottom && bar.Close < bar.Open && bar.Close <= vwap)
            {
                var target = zones.LastOrDefault(other => other.Top < bar.Close);
                if (target is not null && !HasVerifiedSlotVolume(indicator, bar)) { missingShortVolume = true; continue; }
                if (!HasVerifiedSlotVolume(indicator, bar) || indicator.SlotRelativeVolume < parameters.MinimumShortRelativeVolume) continue;
                if (target is not null && BuildSignal(false, zone, target, previous, bar, indicator, session, asOfUtc, tickSize, rules) is { } signal)
                    return signal;
            }
        }
        return null;
    }

    // Keep raw geometry in evidence; submitted stops round outward and targets toward the known entry reference.
    private static IntradayZoneSignal? BuildSignal(bool isLong, IntradayZoneEvidence zone, IntradayZoneEvidence target,
        OhlcvBar previous, OhlcvBar bar, IndicatorSnapshot indicator, IntradayZoneSessionContext session, DateTimeOffset asOfUtc,
        decimal tickSize, IntradayZoneRules rules)
    {
        var rawStop = isLong ? zone.Bottom - rules.PythonComparison!.StopAtrMultiple * indicator.Atr!.Value :
            zone.Top + rules.PythonComparison!.StopAtrMultiple * indicator.Atr!.Value;
        var rawTarget = isLong ? target.Bottom : target.Top;
        var stop = (isLong ? decimal.Floor(rawStop / tickSize) : decimal.Ceiling(rawStop / tickSize)) * tickSize;
        var takeProfit = (isLong ? decimal.Floor(rawTarget / tickSize) : decimal.Ceiling(rawTarget / tickSize)) * tickSize;
        if (stop <= 0m || takeProfit <= 0m || (isLong ? stop >= bar.Close || takeProfit <= bar.Close : stop <= bar.Close || takeProfit >= bar.Close)) return null;
        var validVolume = HasVerifiedSlotVolume(indicator, bar);
        var evidence = new IntradayZoneSignalEvidence(zone, null, asOfUtc, indicator.Vwap!.Value,
            validVolume ? indicator.SlotRelativeVolume : null, validVolume ? indicator.SlotMedianVolume : null,
            validVolume ? indicator.SlotRelativeVolumeSampleCount : 0, validVolume ? indicator.MarketEvidenceProfileVersion : null,
            previous, indicator.Atr, target, rawStop, rawTarget, rules.PolicyId, session.OpenUtc, session.CloseUtc);
        return new(isLong ? "long" : "short", isLong ? "python_support_bounce" : "python_zone_breakdown", bar,
            bar.Close, stop, tickSize, asOfUtc, session.DeadlineUtc, evidence, rules.MaxFilledParentTradesPerSession,
            session.CloseUtc.AddMinutes(-rules.PythonComparison!.EntryCutoffMinutesBeforeClose), takeProfit);
    }
}
