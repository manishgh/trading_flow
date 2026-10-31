using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Engine.Strategies;

/// <summary>Owns frozen retests and fresh-touch state; admission and filled-parent limits remain execution concerns.</summary>
public sealed class IntradayZonePatternEvaluator
{
    private readonly IntradayZoneRetestState _retests = new();
    private readonly HashSet<(long ZoneId, IntradayZoneRole Role)> _consumedBounces = [];
    private OhlcvBar? _lastBar;
    private DateTimeOffset? _lastSessionClose;
    private IntradayZoneDecisionContext? _lastDecision;
    private DateOnly? _sessionDate;
    private bool _coverageKnown = true;
    private readonly IntradayZoneRules _rules;

    // Pattern state shares the same immutable policy as its zone calculator and submitted execution evidence.
    public IntradayZonePatternEvaluator(IntradayZoneRules? rules = null)
    {
        _rules = rules ?? new();
        _rules.ValidateParameters();
    }

    // Record each completed flip once; arming becomes eligible only for a five-minute candle beginning at or after it.
    public void ObserveZoneUpdate(IntradayZoneUpdate update)
    {
        _retests.Observe(update);
        if (!update.Snapshot.CoverageKnown) _coverageKnown = false;
    }

    // Consume a completed confirmation exactly once, using the zone version that was known before that candle opened.
    public IntradayZoneDecisionContext EvaluateCompleted5m(IntradayZoneSnapshot snapshot, OhlcvBar bar,
        IndicatorSnapshot? indicator, IntradayZoneSessionContext session, DateTimeOffset asOfUtc, decimal tickSize)
    {
        if (!IntradayZoneBarEvidence.IsValid(bar, session, asOfUtc, 5, _rules.PriorSessionCount) || tickSize <= 0m ||
            snapshot.Ticker != bar.Ticker || snapshot.AsOfUtc > bar.Timestamp)
            return new(snapshot, null, "intraday_zone_completed_5m_evidence_invalid");
        if (_lastBar == bar) return _coverageKnown && snapshot.CoverageKnown ? _lastDecision! :
            Save(snapshot, null, "intraday_zone_5m_or_15m_coverage_unknown");
        if (_lastBar is not null && (!IntradayZoneBarEvidence.SameSource(_lastBar, bar) || bar.Timestamp <= _lastBar.Timestamp) ||
            !IntradayZoneBarEvidence.IsNext(_lastBar, _lastSessionClose, bar, session, 5))
            _coverageKnown = false;
        _lastBar = bar;
        _lastSessionClose = session.CloseUtc;
        BeginSession(session.SessionDate);
        ActivateKnownFlips(bar.Timestamp);
        ExpireRetests(bar, session, asOfUtc);
        RearmFreshTouches(snapshot, bar);
        if (!_coverageKnown || !snapshot.CoverageKnown)
            return Save(snapshot, null, "intraday_zone_5m_or_15m_coverage_unknown");
        if (asOfUtc >= session.DeadlineUtc)
            return Save(snapshot, null, "outside_intraday_zone_entry_window");
        if (!HasEntryEvidence(indicator, bar))
            return Save(snapshot, null, "intraday_zone_vwap_or_slot_volume_unavailable");
        if (indicator!.SlotRelativeVolume <= 1m)
            return Save(snapshot, null, "intraday_zone_slot_relative_volume_not_above_one");
        var retest = SelectRetest(bar, indicator.Vwap!.Value);
        if (retest is not null)
        {
            var signal = BuildSignal(retest.Zone, retest.FlippedAtUtc, "breakout_retest", bar, indicator, session, asOfUtc, tickSize);
            if (signal is null) return Save(snapshot, null, "intraday_zone_structural_stop_invalid");
            _retests.Consume(retest.Zone.ZoneId);
            _consumedBounces.Add((retest.Zone.ZoneId, retest.Zone.Role));
            return Save(snapshot, signal, null);
        }
        var bounce = SelectBounce(snapshot, bar, indicator.Vwap.Value);
        if (bounce is null) return Save(snapshot, null, "intraday_zone_fresh_confirmation_required");
        var bounceSignal = BuildSignal(bounce, null, "rejection_bounce", bar, indicator, session, asOfUtc, tickSize);
        if (bounceSignal is null) return Save(snapshot, null, "intraday_zone_structural_stop_invalid");
        _consumedBounces.Add((bounce.ZoneId, bounce.Role));
        return Save(snapshot, bounceSignal, null);
    }

    // Pure kernel guard checks identity and availability without consuming calculator or pattern state again.
    public static bool MatchesDecision(IntradayZoneDecisionContext context, OhlcvBar bar, DateTimeOffset asOfUtc,
        IndicatorSnapshot? expectedIndicator = null) =>
        context.Signal is { } signal && context.RejectionReason is null && context.Snapshot.CoverageKnown &&
        context.Snapshot.Ticker == bar.Ticker && context.Snapshot.AsOfUtc <= bar.Timestamp &&
        signal.ConfirmationBar == bar && signal.ActiveAtUtc == asOfUtc && bar.Timestamp.AddMinutes(5) == asOfUtc &&
        signal.Evidence.ConfirmationCompletedAtUtc == asOfUtc && signal.Evidence.Zone.KnownAtUtc <= bar.Timestamp &&
        signal.SessionDeadlineUtc > asOfUtc && signal.TickSize > 0m && signal.EntryReferencePrice == bar.Close &&
        signal.Evidence.SlotRelativeVolume > 1m && signal.Evidence.SlotMedianVolume > 0m &&
        signal.Evidence.SlotRelativeVolume == bar.Volume / signal.Evidence.SlotMedianVolume &&
        signal.Evidence.SlotRelativeVolumeSampleCount == MarketEvidenceProfile.DefaultLookbackSessions &&
        signal.Evidence.MarketEvidenceProfileVersion == MarketEvidenceProfile.ProductionVersion &&
        signal.Direction == (signal.Evidence.Zone.Role == IntradayZoneRole.Support ? "long" : "short") &&
        signal.Evidence.Vwap is { } vwap && Qualifies(signal.Evidence.Zone, bar, vwap) &&
        (signal.Pattern == "rejection_bounce" && signal.Evidence.FlippedAtUtc is null ||
         signal.Pattern == "breakout_retest" && signal.Evidence.FlippedAtUtc is { } flippedAt && flippedAt <= bar.Timestamp) &&
        signal.InitialStopPrice == (signal.Direction == "long" ? decimal.Floor(bar.Low / signal.TickSize) : decimal.Ceiling(bar.High / signal.TickSize)) * signal.TickSize &&
        (expectedIndicator is null || HasEntryEvidence(expectedIndicator, bar) &&
            expectedIndicator.Vwap == signal.Evidence.Vwap && expectedIndicator.SlotRelativeVolume == signal.Evidence.SlotRelativeVolume &&
            expectedIndicator.SlotMedianVolume == signal.Evidence.SlotMedianVolume &&
            expectedIndicator.SlotRelativeVolumeSampleCount == signal.Evidence.SlotRelativeVolumeSampleCount &&
            expectedIndicator.MarketEvidenceProfileVersion == signal.Evidence.MarketEvidenceProfileVersion);

    // Do not carry pending intent or touch consumption overnight; the retained zone geometry remains in the calculator.
    private void BeginSession(DateOnly sessionDate)
    {
        if (_sessionDate == sessionDate) return;
        _consumedBounces.Clear();
        _sessionDate = sessionDate;
    }

    // Delegate frozen-arm availability and replacement semantics to the shared lifecycle primitive.
    private void ActivateKnownFlips(DateTimeOffset candleStartUtc) => _retests.Activate(candleStartUtc);

    // Preserve this original policy's exclusive deadline and symmetric opposite-edge cancellation.
    private void ExpireRetests(OhlcvBar bar, IntradayZoneSessionContext session, DateTimeOffset asOfUtc) =>
        _retests.Expire(bar, session, asOfUtc, session.DeadlineUtc, inclusiveCutoff: false);
    // A resolved touch needs an intervening non-overlap before another bounce can consume the same role.
    private void RearmFreshTouches(IntradayZoneSnapshot snapshot, OhlcvBar bar)
    {
        foreach (var key in _consumedBounces.ToArray())
        {
            var zone = snapshot.Zones.FirstOrDefault(item => item.ZoneId == key.ZoneId && item.Role == key.Role);
            if (zone is null || !Overlaps(bar, zone)) _consumedBounces.Remove(key);
        }
    }

    // Require the existing twenty-session RTH slot median, with the exact confirmation candle's same-feed volume ratio.
    private static bool HasEntryEvidence(IndicatorSnapshot? indicator, OhlcvBar bar) =>
        IntradayZoneBarEvidence.Matches(indicator, bar) && indicator!.Vwap is > 0m && indicator.SlotRelativeVolume is not null &&
        indicator.SlotMedianVolume is > 0m && indicator.SlotRelativeVolume == bar.Volume / indicator.SlotMedianVolume &&
        indicator.SlotRelativeVolumeSampleCount == MarketEvidenceProfile.DefaultLookbackSessions &&
        indicator.RelativeVolumeMinimumSamples == MarketEvidenceProfile.DefaultMinimumValidSamples &&
        indicator.RelativeVolumeCohort == "regular" && indicator.MarketEvidenceReliability == "verified_same_feed" &&
        indicator.MarketEvidenceProfileVersion == MarketEvidenceProfile.ProductionVersion;

    // Retest priority is resolved before bounce, then nearest edge, oldest creation and stable identifier break ties.
    private IntradayZoneArmedRetestEvidence? SelectRetest(OhlcvBar bar, decimal vwap) => _retests.Armed
        .Where(armed => armed.FlippedAtUtc <= bar.Timestamp && Qualifies(armed.Zone, bar, vwap))
        .OrderBy(armed => EdgeDistance(armed.Zone, bar.Close)).ThenBy(armed => armed.Zone.CreatedAtUtc)
        .ThenBy(armed => armed.Zone.ZoneId).FirstOrDefault();

    // A current-role bounce can use only a box version already known when its five-minute candle began.
    private IntradayZoneEvidence? SelectBounce(IntradayZoneSnapshot snapshot, OhlcvBar bar, decimal vwap) => snapshot.Zones
        .Where(zone => zone.KnownAtUtc <= bar.Timestamp && !_consumedBounces.Contains((zone.ZoneId, zone.Role)) && Qualifies(zone, bar, vwap))
        .OrderBy(zone => EdgeDistance(zone, bar.Close)).ThenBy(zone => zone.CreatedAtUtc).ThenBy(zone => zone.ZoneId).FirstOrDefault();

    // Both patterns require inclusive box overlap and a strict rejection beyond the edge and same-session VWAP.
    private static bool Qualifies(IntradayZoneEvidence zone, OhlcvBar bar, decimal vwap) => Overlaps(bar, zone) &&
        (zone.Role == IntradayZoneRole.Support ? bar.Close > zone.Top && bar.Close > vwap : bar.Close < zone.Bottom && bar.Close < vwap);

    // Preserve zero-width boxes as touchable prices.
    private static bool Overlaps(OhlcvBar bar, IntradayZoneEvidence zone) => bar.Low <= zone.Top && bar.High >= zone.Bottom;

    // Literal nearest-edge distance supports overlapping candidate rectangles without changing their raw bounds.
    private static decimal EdgeDistance(IntradayZoneEvidence zone, decimal close) => Math.Min(Math.Abs(close - zone.Bottom), Math.Abs(close - zone.Top));

    // Round only submitted stops outward; actual-fill 3R and risk sizing remain in the common execution planner.
    private IntradayZoneSignal? BuildSignal(IntradayZoneEvidence zone, DateTimeOffset? flippedAtUtc, string pattern,
        OhlcvBar bar, IndicatorSnapshot indicator, IntradayZoneSessionContext session, DateTimeOffset asOfUtc, decimal tickSize)
    {
        var isLong = zone.Role == IntradayZoneRole.Support;
        var stop = (isLong ? decimal.Floor(bar.Low / tickSize) : decimal.Ceiling(bar.High / tickSize)) * tickSize;
        if (stop <= 0m || (isLong ? stop >= bar.Close : stop <= bar.Close)) return null;
        var evidence = new IntradayZoneSignalEvidence(zone, flippedAtUtc, asOfUtc, indicator.Vwap!.Value,
            indicator.SlotRelativeVolume!.Value, indicator.SlotMedianVolume!.Value, indicator.SlotRelativeVolumeSampleCount,
            indicator.MarketEvidenceProfileVersion);
        return new(isLong ? "long" : "short", pattern, bar, bar.Close, stop, tickSize, asOfUtc, session.DeadlineUtc, evidence,
            _rules.MaxFilledParentTradesPerSession);
    }

    // Cache the immutable result for transport retries; downstream intent identifiers must remain idempotent.
    private IntradayZoneDecisionContext Save(IntradayZoneSnapshot snapshot, IntradayZoneSignal? signal, string? reason) =>
        _lastDecision = new(snapshot, signal, reason);
}
