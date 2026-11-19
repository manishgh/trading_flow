namespace TradingFlow.Engine.Execution;

public enum EquityTradingSession
{
    Closed,
    Overnight,
    Premarket,
    Regular,
    AfterHours
}

public sealed record TradingSessionSnapshot(
    DateOnly TradeDate,
    EquityTradingSession Session,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? RegularOpenUtc,
    DateTimeOffset? RegularCloseUtc);

public interface ITradingSessionProvider
{
    Task<TradingSessionSnapshot> GetSessionAsync(
        DateTimeOffset timestampUtc,
        CancellationToken cancellationToken);
}

public sealed record AssetTradingEligibility(
    string Symbol,
    bool Active,
    bool Tradable,
    bool OvernightTradable,
    DateTimeOffset ObservedAtUtc,
    bool Shortable = false,
    string? BorrowStatus = null,
    string? AssetClass = null,
    // Listing exchange (Alpaca code: NASDAQ, NYSE, ARCA, AMEX, BATS, OTC), read by the Nasdaq home-market regime rule.
    string? Exchange = null,
    // Broker easy-to-borrow flag (Alpaca "easy_to_borrow"); defaults false (fail closed for short entries).
    bool EasyToBorrow = false);

/// <summary>
/// Shared live/paper/Desk short-borrow eligibility rule (Checkpoint 7B): short entries require an active, tradable,
/// shortable, easy-to-borrow security whose borrow status is not marked unavailable.
/// </summary>
public static class ShortBorrowEligibility
{
    /// <summary>Rejection code when a security is missing, not shortable, or not easy-to-borrow on the broker.</summary>
    public const string NotEasyToBorrowReason = "short_not_easy_to_borrow";

    /// <summary>Returns true iff <paramref name="eligibility"/> explicitly confirms shortable and easy-to-borrow status.</summary>
    public static bool IsShortableAndEasyToBorrow(AssetTradingEligibility? eligibility) =>
        eligibility is { Active: true, Tradable: true, Shortable: true, EasyToBorrow: true } &&
        !String.Equals(eligibility.BorrowStatus, "unavailable", StringComparison.OrdinalIgnoreCase);
}

public interface IAssetTradingEligibilityProvider
{
    Task<AssetTradingEligibility?> GetEligibilityAsync(
        string symbol,
        CancellationToken cancellationToken);
}

/// <summary>
/// Validates an exact order contract against an authoritative market-session snapshot.
/// It never changes the caller's order type, time in force, or extended-hours permission.
/// Regular entries use a simple order followed by an independently owned GTC stop,
/// because an Alpaca bracket activates its exits only after the parent is fully filled.
/// Extended-hours entries remain unavailable because no equivalent immediately
/// protective broker contract exists; eligible risk-reducing exits are handled separately.
/// </summary>
public static class ExtendedHoursOrderPolicy
{
    /// <summary>
    /// Validates the static order settings required before extended-hours execution can be enabled.
    /// Runtime session and asset eligibility are validated separately immediately before submission.
    /// </summary>
    public static void ValidateConfiguration(
        string? orderType,
        string? timeInForce,
        bool allowExtendedHoursTrading)
    {
        if (!allowExtendedHoursTrading)
        {
            return;
        }

        if (!String.Equals(orderType?.Trim(), "limit", StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(timeInForce?.Trim(), "day", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Extended-hours execution requires an explicit limit entry and DAY expiration.");
        }
    }

    public static void Validate(
        TradingSessionSnapshot session,
        string orderType,
        string timeInForce,
        bool allowExtendedHoursTrading)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Session == EquityTradingSession.Closed)
        {
            throw new InvalidOperationException(
                $"Equity entry rejected because Alpaca reports trade date {session.TradeDate:yyyy-MM-dd} as closed.");
        }

        var isExtendedSession = session.Session != EquityTradingSession.Regular;
        if (isExtendedSession && !allowExtendedHoursTrading)
        {
            throw new InvalidOperationException(
                $"Equity entry rejected during {ToDisplayName(session.Session)} because allow_extended_hours_trading is false.");
        }

        if (!isExtendedSession)
        {
            return;
        }

        if (!orderType.Trim().Equals("limit", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Extended-hours equity entries require an explicit limit order; the engine will not convert the order type.");
        }

        if (!timeInForce.Trim().Equals("day", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Extended-hours equity entries require time_in_force=day.");
        }

        throw new InvalidOperationException(
            "Extended-hours equity entry is blocked because Alpaca cannot attach a broker-resting protective stop to that entry contract.");
    }

    private static string ToDisplayName(EquityTradingSession session) => session switch
    {
        EquityTradingSession.Overnight => "overnight",
        EquityTradingSession.Premarket => "premarket",
        EquityTradingSession.AfterHours => "after-hours",
        _ => session.ToString().ToLowerInvariant()
    };

    public static void ValidateOvernightAsset(
        string symbol,
        AssetTradingEligibility? eligibility)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        if (eligibility is null)
        {
            throw new InvalidOperationException(
                $"Overnight entry rejected for {normalized}: Alpaca returned no asset eligibility.");
        }

        if (!eligibility.Active || !eligibility.Tradable || !eligibility.OvernightTradable)
        {
            throw new InvalidOperationException(
                $"Overnight entry rejected for {eligibility.Symbol}: active={eligibility.Active}, tradable={eligibility.Tradable}, overnight_tradable={eligibility.OvernightTradable}.");
        }
    }
}
