using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

/// <summary>One stop calculation for completed-bar generation, explanation and execution evidence checks.</summary>
public static class IntradayZoneStopGeometry
{
    public const string SourceZone = "source_zone";

    // Select a long-only recipe basis; short breakdowns always retain the source-zone top.
    public static string Basis(bool isLong, string? longStopBasis) =>
        isLong && longStopBasis == PythonZoneVolumeSpreadComparisonRules.TwoCandleLowStop
            ? PythonZoneVolumeSpreadComparisonRules.TwoCandleLowStop : SourceZone;

    // Callers verify adjacent completed same-source bars and positive ATR/tick before using this outward-rounded level.
    public static (decimal RawStop, decimal RoundedStop) Calculate(bool isLong, IntradayZoneEvidence zone,
        OhlcvBar previous, OhlcvBar confirmation, decimal atr, decimal tick, decimal stopAtrMultiple, string? longStopBasis = null)
    {
        var anchor = Basis(isLong, longStopBasis) == PythonZoneVolumeSpreadComparisonRules.TwoCandleLowStop
            ? Math.Min(previous.Low, confirmation.Low) : isLong ? zone.Bottom : zone.Top;
        var raw = isLong ? anchor - stopAtrMultiple * atr : anchor + stopAtrMultiple * atr;
        var rounded = (isLong ? decimal.Floor(raw / tick) : decimal.Ceiling(raw / tick)) * tick;
        return (raw, rounded);
    }
}
