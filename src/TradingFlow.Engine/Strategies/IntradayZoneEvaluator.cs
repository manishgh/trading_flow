using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

/// <summary>One chronological brain per symbol; fifteen-minute state and five-minute decisions share immutable clocks.</summary>
public sealed class IntradayZoneEvaluator
{
    private readonly IntradayZoneStateCalculator _calculator;
    private readonly IntradayZonePatternEvaluator? _patterns;
    private readonly PythonZoneComparisonEvaluator? _pythonPatterns;
    private readonly List<IntradayZoneSnapshot> _snapshots = [];

    // Keep construction tied to the frozen policy while leaving strategy-definition validation to its normal parser.
    public IntradayZoneEvaluator(IntradayZoneRules? rules = null)
    {
        rules ??= new();
        rules.ValidateParameters();
        _calculator = new(rules);
        if (rules.IsPythonFixedTargetComparison) _pythonPatterns = new(rules);
        else _patterns = new(rules);
    }

    // Retain two consecutive zone snapshots so a simultaneous 15m close cannot rewrite the just-completed 5m candle.
    public IntradayZoneUpdate ProcessCompleted15m(OhlcvBar bar, IndicatorSnapshot? indicator,
        IntradayZoneSessionContext session, DateTimeOffset asOfUtc)
    {
        var update = _calculator.ApplyCompleted15m(bar, indicator, session, asOfUtc);
        _patterns?.ObserveZoneUpdate(update);
        _pythonPatterns?.ObserveZoneUpdate(update);
        if (update.RejectionReason is not ("intraday_zone_completed_15m_evidence_invalid" or "intraday_zone_duplicate_or_source_conflict" or "intraday_zone_15m_coverage_gap"))
        {
            if (_snapshots.Count == 0 || _snapshots[^1].AsOfUtc < update.Snapshot.AsOfUtc) _snapshots.Add(update.Snapshot);
            if (_snapshots.Count > 2) _snapshots.RemoveAt(0);
        }
        return update;
    }

    // Select state available before the entry candle starts, regardless of which same-close timeframe arrived first.
    public IntradayZoneDecisionContext ProcessCompleted5m(OhlcvBar bar, IndicatorSnapshot? indicator,
        IntradayZoneSessionContext session, DateTimeOffset asOfUtc, decimal tickSize)
    {
        var snapshot = _snapshots.LastOrDefault(item => item.AsOfUtc <= bar.Timestamp) ??
            new IntradayZoneSnapshot(bar.Ticker, session.OpenUtc, session.SessionDate, [], []);
        var expectedClose = session.OpenUtc.AddMinutes(Math.Floor((bar.Timestamp - session.OpenUtc).TotalMinutes / 15d) * 15d);
        if (expectedClose > session.OpenUtc && snapshot.AsOfUtc != expectedClose)
            snapshot = snapshot with { CoverageKnown = false };
        snapshot = snapshot with { SessionDate = session.SessionDate,
            Zones = Array.AsReadOnly(snapshot.Zones.Where(zone => zone.LatestPivotSession == session.SessionDate ||
                session.PreviousSessionDates.Contains(zone.LatestPivotSession)).ToArray()) };
        return _pythonPatterns is not null
            ? _pythonPatterns.EvaluateCompleted5m(snapshot, bar, indicator, session, asOfUtc, tickSize)
            : _patterns!.EvaluateCompleted5m(snapshot, bar, indicator, session, asOfUtc, tickSize);
    }
}
