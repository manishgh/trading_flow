namespace TradingFlow.Domain.Backtesting;

/// <summary>
/// Stable identity for one listed security and its issuing company.
/// IssuerId groups share classes such as GOOG and GOOGL without relying on ticker text.
/// </summary>
public sealed record UniverseSecurityIdentity
{
    public UniverseSecurityIdentity(string symbol, string issuerId, string shareClassId)
    {
        Symbol = NormalizeRequired(symbol, nameof(symbol)).ToUpperInvariant();
        IssuerId = NormalizeRequired(issuerId, nameof(issuerId));
        ShareClassId = NormalizeRequired(shareClassId, nameof(shareClassId));
    }

    public string Symbol { get; }

    public string IssuerId { get; }

    public string ShareClassId { get; }

    public string SecurityKey => $"{IssuerId}|{ShareClassId}|{Symbol}";

    private static string NormalizeRequired(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}
