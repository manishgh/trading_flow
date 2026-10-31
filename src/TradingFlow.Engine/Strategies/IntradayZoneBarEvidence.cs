using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

internal static class IntradayZoneBarEvidence
{
    // Require one real, completed RTH slot with an explicit calendar and coherent OHLC/source clocks.
    internal static bool IsValid(OhlcvBar bar, IntradayZoneSessionContext session, DateTimeOffset asOfUtc, int minutes, int priorSessionCount)
    {
        var interval = TimeSpan.FromMinutes(minutes);
        return session.OpenUtc.Offset == TimeSpan.Zero && session.CloseUtc.Offset == TimeSpan.Zero &&
            session.OpenUtc < session.CloseUtc && session.PreviousSessionDates.Count == priorSessionCount &&
            session.PreviousSessionDates.Distinct().Count() == priorSessionCount &&
            session.PreviousSessionDates.All(date => date < session.SessionDate) &&
            bar.Timestamp.Offset == TimeSpan.Zero && asOfUtc.Offset == TimeSpan.Zero && bar.Timestamp + interval == asOfUtc &&
            bar.Timeframe == $"{minutes}m" && bar.Timestamp >= session.OpenUtc && asOfUtc <= session.CloseUtc &&
            (bar.Timestamp - session.OpenUtc).Ticks % interval.Ticks == 0 &&
            bar.Open > 0m && bar.Low > 0m && bar.Volume >= 0m && bar.High >= Math.Max(bar.Open, bar.Close) &&
            bar.Low <= Math.Min(bar.Open, bar.Close) && bar.High >= bar.Low &&
            (bar.KnownAtUtc is null || bar.KnownAtUtc <= asOfUtc) &&
            (bar.CoverageVerifiedThroughUtc is null || bar.CoverageVerifiedThroughUtc >= asOfUtc);
    }

    // Overnight continuity is proven by the previous exchange close, not elapsed wall-clock minutes.
    internal static bool IsNext(OhlcvBar? previous, DateTimeOffset? previousSessionClose, OhlcvBar bar,
        IntradayZoneSessionContext session, int minutes) => previous is null
        ? bar.Timestamp == session.OpenUtc
        : previous.Timestamp.AddMinutes(minutes) == bar.Timestamp ||
          bar.Timestamp == session.OpenUtc && previousSessionClose == session.PreviousSessionCloseUtc &&
          previous.Timestamp.AddMinutes(minutes) == session.PreviousSessionCloseUtc;

    // A snapshot must describe the same supplied candle and feed; indicators are never recomputed here.
    internal static bool Matches(IndicatorSnapshot? indicator, OhlcvBar bar) => indicator is not null &&
        indicator.Timestamp == bar.Timestamp && indicator.Timeframe == bar.Timeframe && indicator.Ticker == bar.Ticker &&
        indicator.DataFeed == bar.DataFeed && indicator.AdjustmentPolicy == bar.AdjustmentPolicy &&
        indicator.CurrentPrice == bar.Close && indicator.CurrentVolume == bar.Volume;

    // Keep a calculator bound to one symbol and price/feed basis throughout its chronological stream.
    internal static bool SameSource(OhlcvBar first, OhlcvBar next) => first.Ticker == next.Ticker &&
        first.DataFeed == next.DataFeed && first.AdjustmentPolicy == next.AdjustmentPolicy;
}
