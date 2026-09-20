using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TradingFlow.Domain.Orders;

public static class ProtectiveStopReplacementIdFactory
{
    private const string ReplacementClientOrderIdPrefix = "TFR-";

    public static Guid Create(
        string accountId,
        string ownerClientOrderId,
        string brokerOrderId,
        string symbol,
        decimal stopPrice)
    {
        var identity = String.Join(
            '|',
            accountId.Trim(),
            ownerClientOrderId.Trim(),
            brokerOrderId.Trim(),
            symbol.Trim().ToUpperInvariant(),
            stopPrice.ToString("G29", CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new Guid(hash.AsSpan(0, 16));
    }

    public static string CreateReplacementClientOrderId(Guid commandId)
    {
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("A replacement command ID is required.", nameof(commandId));
        }

        return ReplacementClientOrderIdPrefix + commandId.ToString("N");
    }
}
