using System.Security.Cryptography;
using System.Text;

namespace TradingFlow.Domain.Orders;

/// <summary>
/// Creates a stable logical-order identity. Re-evaluating the same signal bar in the
/// same run produces the same intent ID, while a later bar produces a new intent.
/// </summary>
public static class OrderIntentIdFactory
{
    public static Guid Create(
        Guid runId,
        string strategyId,
        string side,
        string symbol,
        DateTimeOffset signalTimestamp)
    {
        var canonical = String.Join(
            '\n',
            runId.ToString("N", System.Globalization.CultureInfo.InvariantCulture),
            strategyId.Trim().ToUpperInvariant(),
            side.Trim().ToUpperInvariant(),
            symbol.Trim().ToUpperInvariant(),
            signalTimestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(hash.AsSpan(0, 16));
    }
}
