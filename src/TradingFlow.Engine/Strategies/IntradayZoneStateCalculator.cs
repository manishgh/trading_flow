using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

/// <summary>Incrementally confirms literal close pivots, merges their raw boxes, then flips current roles.</summary>
public sealed class IntradayZoneStateCalculator
{
    private readonly Queue<(OhlcvBar Bar, DateOnly Session)> _window = new();
    private readonly List<IntradayZoneEvidence> _zones = [];
    private OhlcvBar? _lastBar;
    private DateTimeOffset? _lastSessionClose;
    private IntradayZoneUpdate? _lastUpdate;
    private bool _coverageKnown = true;
    private long _nextId;
    private readonly IntradayZoneRules _rules;

    // Bind one immutable strategy policy to this symbol's incremental state.
    public IntradayZoneStateCalculator(IntradayZoneRules? rules = null)
    {
        _rules = rules ?? new();
        _rules.ValidateParameters();
    }

    // Consume each completed fifteen-minute slot once; identical retries return the original immutable result.
    public IntradayZoneUpdate ApplyCompleted15m(OhlcvBar bar, IndicatorSnapshot? indicator,
        IntradayZoneSessionContext session, DateTimeOffset asOfUtc)
    {
        if (!IntradayZoneBarEvidence.IsValid(bar, session, asOfUtc, 15, _rules.PriorSessionCount))
            return Unavailable(bar, session, asOfUtc, "intraday_zone_completed_15m_evidence_invalid");
        if (_lastBar == bar) return _coverageKnown ? _lastUpdate! :
            Unavailable(bar, session, asOfUtc, "intraday_zone_15m_coverage_unknown");
        if (_lastBar is not null && (!IntradayZoneBarEvidence.SameSource(_lastBar, bar) || bar.Timestamp <= _lastBar.Timestamp))
            return LoseCoverage(bar, session, asOfUtc, "intraday_zone_duplicate_or_source_conflict");
        if (!IntradayZoneBarEvidence.IsNext(_lastBar, _lastSessionClose, bar, session, 15))
            return LoseCoverage(bar, session, asOfUtc, "intraday_zone_15m_coverage_gap");
        _lastBar = bar;
        _lastSessionClose = session.CloseUtc;
        _window.Enqueue((bar, session.SessionDate));
        var windowLength = _rules.PivotStrength * 2 + 1;
        if (_window.Count > windowLength) _window.Dequeue();
        Expire(session);
        if (!_coverageKnown) return Save(bar, session, asOfUtc, [], "intraday_zone_15m_coverage_unknown");
        if (!IntradayZoneBarEvidence.Matches(indicator, bar) || indicator!.Atr is not > 0m)
            return Save(bar, session, asOfUtc, [], "intraday_zone_atr_unavailable");
        var atr = indicator.Atr.Value;
        if (_window.Count == windowLength) ConfirmPivots(asOfUtc, atr);
        var transitions = Flip(bar, asOfUtc, atr);
        return Save(bar, session, asOfUtc, transitions, null);
    }

    // Calendar retention follows the newest merged pivot session and the policy's exact prior-session list.
    private void Expire(IntradayZoneSessionContext session) => _zones.RemoveAll(zone =>
        zone.LatestPivotSession != session.SessionDate && !session.PreviousSessionDates.Contains(zone.LatestPivotSession));

    // A flat configured close window deliberately confirms both independent pivot definitions, peak first.
    private void ConfirmPivots(DateTimeOffset asOfUtc, decimal atr)
    {
        var window = _window.ToArray();
        var pivot = window[_rules.PivotStrength];
        if (window.All(item => pivot.Bar.Close >= item.Bar.Close))
            AddPivot(pivot.Bar, pivot.Session, IntradayZonePivotKind.Peak, asOfUtc, atr);
        if (window.All(item => pivot.Bar.Close <= item.Bar.Close))
            AddPivot(pivot.Bar, pivot.Session, IntradayZonePivotKind.Valley, asOfUtc, atr);
    }

    // Merge by literal bottom distance and current role; selecting the oldest never becomes a transitive union pass.
    private void AddPivot(OhlcvBar bar, DateOnly session, IntradayZonePivotKind kind, DateTimeOffset asOfUtc, decimal atr)
    {
        var peak = kind == IntradayZonePivotKind.Peak;
        var bottom = peak ? Math.Max(bar.Open, bar.Close) : bar.Low;
        var top = peak ? bar.High : Math.Min(bar.Open, bar.Close);
        var role = peak ? IntradayZoneRole.Resistance : IntradayZoneRole.Support;
        var pivot = new IntradayZonePivotEvidence(kind, bar.Timestamp, bar.Timestamp.AddMinutes(15), asOfUtc, session, bottom, top);
        var index = _zones.FindIndex(zone => zone.Role == role && Math.Abs(bottom - zone.Bottom) <= _rules.MergeAtrMultiple * atr);
        if (index < 0)
        {
            _zones.Add(new(++_nextId, 1, role, bottom, top, 1, asOfUtc, asOfUtc, bar.Timestamp, session, pivot));
            return;
        }
        var prior = _zones[index];
        _zones[index] = prior with { Version = prior.Version + 1, Bottom = Math.Min(bottom, prior.Bottom), Top = Math.Max(top, prior.Top),
            PivotCount = prior.PivotCount + 1, KnownAtUtc = asOfUtc, LatestPivotSession = session, LatestPivot = pivot };
    }

    // Apply strict symmetric close thresholds after both pivot operations; freeze each flip's final box.
    private IReadOnlyList<IntradayZoneTransition> Flip(OhlcvBar bar, DateTimeOffset asOfUtc, decimal atr)
    {
        var transitions = new List<IntradayZoneTransition>();
        for (var index = 0; index < _zones.Count; index++)
        {
            var before = _zones[index];
            var flips = before.Role == IntradayZoneRole.Support
                ? bar.Close < before.Bottom - _rules.FlipAtrMultiple * atr
                : bar.Close > before.Top + _rules.FlipAtrMultiple * atr;
            if (!flips) continue;
            var after = before with { Version = before.Version + 1, KnownAtUtc = asOfUtc,
                Role = before.Role == IntradayZoneRole.Support ? IntradayZoneRole.Resistance : IntradayZoneRole.Support };
            _zones[index] = after;
            transitions.Add(new(before, after, asOfUtc, before.CreatedAtUtc <= bar.Timestamp, atr,
                _rules.PythonVolumeSpreadComparison?.UsesBreakoutRetestEntry == true ? bar : null));
        }
        return Array.AsReadOnly(transitions.ToArray());
    }

    // Store copies so neither later merges nor caller collections can rewrite published historical evidence.
    private IntradayZoneUpdate Save(OhlcvBar bar, IntradayZoneSessionContext session, DateTimeOffset asOfUtc,
        IReadOnlyList<IntradayZoneTransition> transitions, string? reason)
    {
        _lastUpdate = new(new(bar.Ticker, asOfUtc, session.SessionDate, Array.AsReadOnly(_zones.ToArray()), transitions, _coverageKnown), reason);
        return _lastUpdate;
    }

    // Missing or conflicting source slots poison this stream; a caller must replay complete evidence into fresh state.
    private IntradayZoneUpdate LoseCoverage(OhlcvBar bar, IntradayZoneSessionContext session, DateTimeOffset asOfUtc, string reason)
    {
        _coverageKnown = false;
        return Unavailable(bar, session, asOfUtc, reason);
    }

    // Reject malformed/future observations without advancing the completed source sequence.
    private IntradayZoneUpdate Unavailable(OhlcvBar bar, IntradayZoneSessionContext session, DateTimeOffset asOfUtc, string reason) =>
        new(new(bar.Ticker, asOfUtc, session.SessionDate, Array.AsReadOnly(_zones.ToArray()), [], _coverageKnown), reason);
}
