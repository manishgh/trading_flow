using TradingFlow.Domain.Earnings;

namespace TradingFlow.Earnings;

/// <summary>Market-cap choices: one small-cap range and cumulative "at least" thresholds.</summary>
public enum EarningsMarketCapBand
{
    All,
    Under2B,
    AtLeast2B,
    AtLeast10B,
    AtLeast50B,
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
            error = "marketCap must be all, under2b, 2bPlus, 10bPlus, 50bPlus, 100bPlus, or unknown.";
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
        EarningsMarketCapBand.Under2B => "under2b",
        EarningsMarketCapBand.AtLeast2B => "2bPlus",
        EarningsMarketCapBand.AtLeast10B => "10bPlus",
        EarningsMarketCapBand.AtLeast50B => "50bPlus",
        EarningsMarketCapBand.AtLeast100B => "100bPlus",
        EarningsMarketCapBand.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(band), band, null)
    };

    public static bool MatchesMarketCap(decimal? marketCapMillions, EarningsMarketCapBand band) => band switch
    {
        EarningsMarketCapBand.All => true,
        EarningsMarketCapBand.Unknown => marketCapMillions is null,
        EarningsMarketCapBand.Under2B => marketCapMillions is >= 0m and < 2_000m,
        EarningsMarketCapBand.AtLeast2B => marketCapMillions is >= 2_000m,
        EarningsMarketCapBand.AtLeast10B => marketCapMillions is >= 10_000m,
        EarningsMarketCapBand.AtLeast50B => marketCapMillions is >= 50_000m,
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
            case "under2b": band = EarningsMarketCapBand.Under2B; return true;
            case "2bplus": band = EarningsMarketCapBand.AtLeast2B; return true;
            case "10bplus": band = EarningsMarketCapBand.AtLeast10B; return true;
            case "50bplus": band = EarningsMarketCapBand.AtLeast50B; return true;
            case "100bplus": band = EarningsMarketCapBand.AtLeast100B; return true;
            case "unknown": band = EarningsMarketCapBand.Unknown; return true;
            default: band = EarningsMarketCapBand.All; return false;
        }
    }

    private static string Normalize(string? value) =>
        String.IsNullOrWhiteSpace(value) ? "all" : value.Trim().Replace("-", String.Empty).ToLowerInvariant();
}
