namespace TradingFlow.Domain.Orders;

public static class ClientOrderIdFactory
{
    public static string Create(
        string strategyId,
        string side,
        string symbol,
        DateOnly sessionDate,
        int sequenceNumber,
        Guid intentId)
    {
        if (sequenceNumber is < 1 or > 999)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceNumber),
                sequenceNumber,
                "Order sequence must be between 1 and 999.");
        }

        var strategy = NormalizeComponent(strategyId, 10, nameof(strategyId));
        var normalizedSide = NormalizeSide(side);
        var normalizedSymbol = NormalizeComponent(symbol, 10, nameof(symbol));
        var uuid = intentId.ToString("N", System.Globalization.CultureInfo.InvariantCulture)[..8];
        return String.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{strategy}-{normalizedSide}-{normalizedSymbol}-{sessionDate:yyyyMMdd}-{sequenceNumber:000}-{uuid}");
    }

    private static string NormalizeComponent(string value, int maximumLength, string parameterName)
    {
        var normalized = new string((value ?? String.Empty)
            .Where(Char.IsLetterOrDigit)
            .Select(Char.ToUpperInvariant)
            .Take(maximumLength)
            .ToArray());
        return normalized.Length > 0
            ? normalized
            : throw new ArgumentException("Value must contain at least one letter or digit.", parameterName);
    }

    private static string NormalizeSide(string side) => side.Trim().ToLowerInvariant() switch
    {
        "b" or "buy" => "B",
        "s" or "sell" => "S",
        _ => throw new ArgumentException("Order side must be buy or sell.", nameof(side))
    };
}
