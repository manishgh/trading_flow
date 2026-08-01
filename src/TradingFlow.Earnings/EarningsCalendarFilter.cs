using TradingFlow.Domain.Earnings;

namespace TradingFlow.Earnings;

public enum EarningsMarketCapBand
{
    All,
    Under10B,
    From10BTo50B,
    From50BTo100B,
    AtLeast100B,
    Unknown
}

/// <summary>
/// Defines the canonical earnings filters shared by the web and mobile APIs.
/// Finviz market capitalization is stored in millions of US dollars.
/// </summary>
public sealed record EarningsCalendarFilter(
    EarningsReleaseWindow? ReleaseWindow,
    EarningsMarketCapBand MarketCapBand)
{
    public static EarningsCalendarFilter All { get; } = new(null, EarningsMarketCapBand.All);

    public bool Matches(EarningsCalendarEvent calendarEvent) =>
        (ReleaseWindow is null || calendarEvent.ReleaseWindow == ReleaseWindow) &&
        MatchesMarketCap(calendarEvent.MarketCapMillions, MarketCapBand);

    public static bool TryParse(
        string? session,
        string? marketCap,
        out EarningsCalendarFilter filter,
        out string? error)
    {
        if (!TryParseSession(session, out var releaseWindow))
        {
            filter = All;
            error = "session must be all, beforeMarketOpen, duringMarket, or afterMarketClose.";
            return false;
        }

        if (!TryParseMarketCap(marketCap, out var marketCapBand))
        {
            filter = All;
            error = "marketCap must be all, under10b, 10bTo50b, 50bTo100b, 100bPlus, or unknown.";
            return false;
        }

        filter = new EarningsCalendarFilter(releaseWindow, marketCapBand);
        error = null;
        return true;
    }

    public static string SessionValue(EarningsReleaseWindow? releaseWindow) => releaseWindow switch
    {
        null => "all",
        EarningsReleaseWindow.BeforeMarketOpen => "beforeMarketOpen",
        EarningsReleaseWindow.DuringMarket => "duringMarket",
        EarningsReleaseWindow.AfterMarketClose => "afterMarketClose",
        _ => "unknown"
    };

    public static string MarketCapValue(EarningsMarketCapBand band) => band switch
    {
        EarningsMarketCapBand.All => "all",
        EarningsMarketCapBand.Under10B => "under10b",
        EarningsMarketCapBand.From10BTo50B => "10bTo50b",
        EarningsMarketCapBand.From50BTo100B => "50bTo100b",
        EarningsMarketCapBand.AtLeast100B => "100bPlus",
        EarningsMarketCapBand.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(band), band, null)
    };

    public static bool MatchesMarketCap(decimal? marketCapMillions, EarningsMarketCapBand band) => band switch
    {
        EarningsMarketCapBand.All => true,
        EarningsMarketCapBand.Unknown => marketCapMillions is null,
        EarningsMarketCapBand.Under10B => marketCapMillions is >= 0m and < 10_000m,
        EarningsMarketCapBand.From10BTo50B => marketCapMillions is >= 10_000m and < 50_000m,
        EarningsMarketCapBand.From50BTo100B => marketCapMillions is >= 50_000m and < 100_000m,
        EarningsMarketCapBand.AtLeast100B => marketCapMillions is >= 100_000m,
        _ => throw new ArgumentOutOfRangeException(nameof(band), band, null)
    };

    private static bool TryParseSession(string? value, out EarningsReleaseWindow? releaseWindow)
    {
        switch (Normalize(value))
        {
            case "all":
                releaseWindow = null;
                return true;
            case "beforemarketopen":
                releaseWindow = EarningsReleaseWindow.BeforeMarketOpen;
                return true;
            case "duringmarket":
                releaseWindow = EarningsReleaseWindow.DuringMarket;
                return true;
            case "aftermarketclose":
                releaseWindow = EarningsReleaseWindow.AfterMarketClose;
                return true;
            default:
                releaseWindow = null;
                return false;
        }
    }

    private static bool TryParseMarketCap(string? value, out EarningsMarketCapBand band)
    {
        switch (Normalize(value))
        {
            case "all": band = EarningsMarketCapBand.All; return true;
            case "under10b": band = EarningsMarketCapBand.Under10B; return true;
            case "10bto50b": band = EarningsMarketCapBand.From10BTo50B; return true;
            case "50bto100b": band = EarningsMarketCapBand.From50BTo100B; return true;
            case "100bplus": band = EarningsMarketCapBand.AtLeast100B; return true;
            case "unknown": band = EarningsMarketCapBand.Unknown; return true;
            default: band = EarningsMarketCapBand.All; return false;
        }
    }

    private static string Normalize(string? value) =>
        String.IsNullOrWhiteSpace(value) ? "all" : value.Trim().Replace("-", String.Empty).ToLowerInvariant();
}
