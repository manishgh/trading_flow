using System.Security.Cryptography;
using System.Text;

namespace TradingFlow.Domain.Orders;

/// <summary>
/// Makes repeated close requests for one persisted position generation converge
/// on the same durable intent instead of creating duplicate broker orders.
/// </summary>
public static class PositionExitIntentIdFactory
{
    public static Guid Create(
        Guid runId,
        string accountId,
        string symbol,
        long positionEventId,
        decimal quantity,
        int attempt = 0)
    {
        if (runId == Guid.Empty || String.IsNullOrWhiteSpace(accountId) ||
            String.IsNullOrWhiteSpace(symbol) || positionEventId <= 0 || quantity <= 0m ||
            attempt is < 0 or > 100)
        {
            throw new ArgumentException(
                "Exit intent identity requires run, account, symbol, position generation, and quantity.");
        }

        var material = String.Join(
            '|',
            runId.ToString("N"),
            accountId.Trim(),
            symbol.Trim().ToUpperInvariant(),
            positionEventId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(material)).AsSpan(0, 16));
    }
}
