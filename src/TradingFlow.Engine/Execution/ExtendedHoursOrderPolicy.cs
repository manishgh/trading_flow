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
    string? BorrowStatus = null);

public interface IAssetTradingEligibilityProvider
{
    Task<AssetTradingEligibility?> GetEligibilityAsync(
        string symbol,
        CancellationToken cancellationToken);
}

/// <summary>
/// Validates an exact order contract against an authoritative market-session snapshot.
/// It never changes the caller's order type, time in force, or extended-hours permission.
/// </summary>
public static class ExtendedHoursOrderPolicy
{
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
            "Extended-hours equity entry is blocked because Alpaca does not support broker-protected bracket orders outside the regular session.");
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
