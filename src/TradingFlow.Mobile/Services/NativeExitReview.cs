using System.Globalization;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Mobile.Services;

/// <summary>Shapes a reviewed native exit contract; fresh account, risk and execution checks remain server-owned.</summary>
public static class NativeExitReview
{
    public static decimal ParseQuantity(string? input, CultureInfo culture) =>
        Decimal.TryParse(input, NumberStyles.Integer, culture, out var value) && value > 0
            ? value : throw new InvalidOperationException("Enter a positive whole-share quantity without separators.");

    public static decimal ParsePrice(string? input, CultureInfo culture, string name) =>
        Decimal.TryParse(input, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite |
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, culture, out var value) && value > 0
            ? value : throw new InvalidOperationException($"Enter a positive {name} using the local decimal separator, without grouping separators.");

    public static ClosePositionRequest Create(string account, string symbol, decimal openQuantity, decimal quantity,
        string type, string timeInForce, bool extended, decimal? limit, decimal? trigger, string key)
    {
        if (String.IsNullOrWhiteSpace(account) || String.IsNullOrWhiteSpace(symbol) || String.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Account, symbol and command identity are required.");
        if (quantity <= 0 || quantity != Decimal.Truncate(quantity) || quantity > openQuantity)
            throw new InvalidOperationException("Choose positive whole shares within the observed open long position.");
        if (type is not ("market" or "limit" or "stop" or "stop_limit") || timeInForce is not ("day" or "gtc" or "ioc" or "fok"))
            throw new InvalidOperationException("Choose a supported exit type and time in force.");
        if (extended && (type != "limit" || timeInForce != "day"))
            throw new InvalidOperationException("Extended-hours permission requires a limit/day exit.");
        var needsLimit = type is "limit" or "stop_limit";
        var needsTrigger = type is "stop" or "stop_limit";
        if (needsLimit && limit is not > 0m || needsTrigger && trigger is not > 0m)
            throw new InvalidOperationException("Enter positive prices required by the selected exit type.");
        return new(ContractVersions.VersionOne, account, symbol.Trim().ToUpperInvariant(), quantity,
            "Operator reviewed Android exit", extended, needsLimit ? limit : null, key, type, timeInForce, needsTrigger ? trigger : null);
    }

    public static bool CanEndUndispatched(OrderCommandResponse? outcome) => outcome?.State == "rejected" &&
        outcome.RejectionCode is "command_expired" or "broker_identity_missing" or "close_revalidation_failed";

    public static bool IsTerminal(string state) => state.ToUpperInvariant() is "FILLED" or "CANCELED" or "REJECTED" or "EXPIRED";
}
