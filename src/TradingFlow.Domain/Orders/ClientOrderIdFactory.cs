namespace TradingFlow.Domain.Orders;

public static class ClientOrderIdFactory
{
    public static bool IsBindingFormat(string? clientOrderId)
    {
        if (String.IsNullOrWhiteSpace(clientOrderId))
        {
            return false;
        }

        var parts = clientOrderId.Split('-', StringSplitOptions.None);
        return parts.Length == 6 &&
            IsComponent(parts[0], 10) &&
            parts[1] is "B" or "S" &&
            IsComponent(parts[2], 10) &&
            DateOnly.TryParseExact(
                parts[3],
                "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out _) &&
            parts[4].Length == 3 &&
            Int32.TryParse(parts[4], out var sequence) &&
            sequence is >= 1 and <= 999 &&
            parts[5].Length == 8 &&
            parts[5].All(Uri.IsHexDigit);
    }

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

    private static bool IsComponent(string value, int maximumLength) =>
        value.Length is > 0 && value.Length <= maximumLength && value.All(Char.IsLetterOrDigit);

    private static string NormalizeSide(string side) => side.Trim().ToLowerInvariant() switch
    {
        "b" or "buy" => "B",
        "s" or "sell" => "S",
        _ => throw new ArgumentException("Order side must be buy or sell.", nameof(side))
    };
}
