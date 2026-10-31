using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

/// <summary>Pure checks of the completed flip, frozen source and active confirmation-start version.</summary>
public static class IntradayZoneRetestEvidenceValidator
{
    // Validate the supplied transition without replaying or consuming pattern state; same-close merges are permitted.
    public static bool MatchesArm(IntradayZoneRetestEvidence evidence, IntradayZoneEvidence frozen, OhlcvBar confirmation,
        DateTimeOffset sessionOpenUtc, DateTimeOffset sessionCloseUtc, IntradayZoneRules rules)
    {
        var transition = evidence.ArmTransition;
        var before = transition.Before;
        var active = evidence.ActiveSourceAtConfirmationStart;
        if (!transition.CanArmRetest || transition.Atr <= 0 || transition.After != frozen ||
            transition.FlipBar is not { } flip || flip.Timeframe != "15m" ||
            flip.Timestamp.Offset != TimeSpan.Zero || transition.FlippedAtUtc.Offset != TimeSpan.Zero ||
            flip.Timestamp.AddMinutes(15) != transition.FlippedAtUtc || flip.Timestamp < sessionOpenUtc ||
            (flip.Timestamp - sessionOpenUtc).Ticks % TimeSpan.FromMinutes(15).Ticks != 0 ||
            transition.FlippedAtUtc > confirmation.Timestamp || transition.FlippedAtUtc >= sessionCloseUtc ||
            flip.KnownAtUtc > transition.FlippedAtUtc || flip.CoverageVerifiedThroughUtc < transition.FlippedAtUtc ||
            !IntradayZoneBarEvidence.SameSource(flip, confirmation) || before.Role != IntradayZoneRole.Resistance ||
            before.CreatedAtUtc > flip.Timestamp || before.KnownAtUtc > transition.FlippedAtUtc ||
            before.Bottom <= 0 || before.Top < before.Bottom || before.PivotCount < rules.PythonVolumeSpreadComparison!.MinimumPivotCount ||
            flip.Open <= 0 || flip.Low <= 0 || flip.High < flip.Low || flip.Volume < 0 ||
            flip.High < Math.Max(flip.Open, flip.Close) || flip.Low > Math.Min(flip.Open, flip.Close) ||
            flip.Close <= before.Top + rules.FlipAtrMultiple * transition.Atr ||
            frozen != (before with { Version = before.Version + 1, KnownAtUtc = transition.FlippedAtUtc, Role = IntradayZoneRole.Support }))
            return false;
        return evidence.ConfirmationSnapshotAsOfUtc <= confirmation.Timestamp &&
            evidence.ConfirmationSnapshotAsOfUtc >= transition.FlippedAtUtc && active.KnownAtUtc <= evidence.ConfirmationSnapshotAsOfUtc &&
            active.ZoneId == frozen.ZoneId && active.Role == IntradayZoneRole.Support && active.Version >= frozen.Version &&
            active.Bottom <= frozen.Bottom && active.Top >= frozen.Top && active.PivotCount >= frozen.PivotCount &&
            (active.Version != frozen.Version || active == frozen);
    }

    // A retest requires actual overlap and a completed reclaim; candle color, VWAP and long RVOL thresholds are descriptive.
    public static bool Qualifies(IntradayZoneEvidence frozen, OhlcvBar confirmation) =>
        confirmation.Low <= frozen.Top && confirmation.High >= frozen.Bottom && confirmation.Close > frozen.Top;
}
