using System.Security.Cryptography;
using System.Text;

namespace TradingFlow.Domain.Orders;

/// <summary>
/// Creates one stable protective owner for an exact position generation.
/// Mutable coverage and stop calculations must not create sibling broker orders.
/// </summary>
public static class ProtectiveOrderIntentIdFactory
{
    public static Guid Create(
        string symbol,
        string side,
        string positionGenerationIdentity,
        int protectionRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(side);
        ArgumentException.ThrowIfNullOrWhiteSpace(positionGenerationIdentity);
        if (protectionRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(protectionRevision));
        }
        var canonical = String.Join(
            '\n',
            symbol.Trim().ToUpperInvariant(),
            side.Trim().ToUpperInvariant(),
            positionGenerationIdentity.Trim(),
            protectionRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(hash.AsSpan(0, 16));
    }
}
