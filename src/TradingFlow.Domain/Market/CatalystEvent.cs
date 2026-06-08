using System;

namespace TradingFlow.Domain.Market;

public enum CatalystType
{
    NewsReport,
    EarningsRelease,
    AnalystUpgrade,
    AnalystDowngrade,
    MergerAnnouncement,
    InsiderBuying,
    DividendAnnouncement,
    ProductLaunch,
    RegulatoryFiling
}

public sealed record CatalystEvent(
    string Ticker,
    DateTimeOffset Timestamp,
    CatalystType Type,
    string Headline,
    decimal SentimentScore // -1.0 (Very Bearish) to 1.0 (Very Bullish)
);
