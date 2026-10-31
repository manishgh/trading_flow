using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

/// <summary>Shared bounded armed-source lifecycle; each evaluator retains its own pattern gates and entry deadline.</summary>
internal sealed class IntradayZoneRetestState
{
    private readonly Dictionary<long, IntradayZoneArmedRetestEvidence> _armed = [];
    private readonly Dictionary<long, int> _observedFlipVersions = [];
    private readonly List<IntradayZoneArmedRetestEvidence> _pending = [];
    private readonly List<IntradayZoneRetestCancellationEvidence> _cancellations = [];
    private readonly Dictionary<long, IntradayZoneRetestConsumptionEvidence> _consumed = [];
    private DateOnly? _consumedSession;

    public IEnumerable<IntradayZoneArmedRetestEvidence> Armed => _armed.Values;
    public IReadOnlyList<IntradayZoneRetestCancellationEvidence> Cancellations => _cancellations;
    public IEnumerable<IntradayZoneRetestConsumptionEvidence> Consumptions => _consumed.Values;

    // Observe completed transitions once, deferring activation until their availability is no later than a candle's opening.
    public void Observe(IntradayZoneUpdate update)
    {
        var activeIds = update.Snapshot.Zones.Select(zone => zone.ZoneId).ToHashSet();
        foreach (var expired in _observedFlipVersions.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            _observedFlipVersions.Remove(expired);
        foreach (var transition in update.Snapshot.Transitions)
        {
            if (_observedFlipVersions.TryGetValue(transition.After.ZoneId, out var version) && version >= transition.After.Version) continue;
            _observedFlipVersions[transition.After.ZoneId] = transition.After.Version;
            _pending.Add(new(transition, update.Snapshot.SessionDate));
        }
    }

    // A replacement flip always cancels the previous arm, including flips that cannot themselves arm.
    public void Activate(DateTimeOffset candleStartUtc)
    {
        _cancellations.Clear();
        foreach (var pending in _pending.Where(item => item.FlippedAtUtc <= candleStartUtc).ToArray())
        {
            Cancel(pending.Zone.ZoneId, "breakout_retest_replaced_by_flip");
            _consumed.Remove(pending.Zone.ZoneId);
            if (pending.Transition.CanArmRetest) _armed[pending.Zone.ZoneId] = pending;
            _pending.Remove(pending);
        }
    }

    // Cancel strict opposite-edge closes, session changes and expired decisions; optional active-role checks use only the start snapshot.
    public void Expire(OhlcvBar bar, IntradayZoneSessionContext session, DateTimeOffset asOfUtc,
        DateTimeOffset cutoffUtc, bool inclusiveCutoff, IntradayZoneSnapshot? activeSnapshot = null)
    {
        if (_consumedSession != session.SessionDate)
        {
            _consumed.Clear();
            _consumedSession = session.SessionDate;
        }
        if (activeSnapshot is not null)
        {
            var activeIds = activeSnapshot.Zones.Where(zone => zone.KnownAtUtc <= bar.Timestamp).Select(zone => zone.ZoneId).ToHashSet();
            foreach (var expired in _consumed.Keys.Where(id => !activeIds.Contains(id)).ToArray()) _consumed.Remove(expired);
        }
        foreach (var pair in _armed.ToArray())
        {
            var armed = pair.Value;
            if (armed.FlippedAtUtc > bar.Timestamp) continue;
            var active = activeSnapshot?.Zones.FirstOrDefault(zone => zone.ZoneId == armed.Zone.ZoneId && zone.KnownAtUtc <= bar.Timestamp);
            var reason = armed.SessionDate != session.SessionDate ? "breakout_retest_new_session"
                : (inclusiveCutoff ? asOfUtc > cutoffUtc : asOfUtc >= cutoffUtc) ? "breakout_retest_entry_cutoff_expired"
                : (armed.Zone.Role == IntradayZoneRole.Support ? bar.Close < armed.Zone.Bottom : bar.Close > armed.Zone.Top) ? "breakout_retest_close_through_frozen_edge"
                : activeSnapshot is not null && active is null ? "breakout_retest_source_expired"
                : active is not null && active.Role != armed.Zone.Role ? "breakout_retest_source_role_changed" : null;
            if (reason is not null) Cancel(pair.Key, reason);
        }
    }

    // Emission consumes a frozen source independently of later admission, sizing or execution outcomes.
    public void Consume(long zoneId, DateTimeOffset? emittedAtUtc = null)
    {
        if (_armed.Remove(zoneId, out var arm) && emittedAtUtc is { } at) _consumed[zoneId] = new(arm, at);
    }

    // Keep cancellation proof separate from emission consumption so retrying an immutable decision does not repeat either transition.
    private void Cancel(long zoneId, string reason)
    {
        if (_armed.Remove(zoneId, out var arm)) _cancellations.Add(new(arm, reason));
    }
}
