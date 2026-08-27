namespace TradingFlow.Domain.Market;

/// <summary>
/// Durable lease row. Released rows remain stored so fencing tokens never reset.
/// </summary>
public sealed class MarketStreamLeaseRecord
{
    public string ResourceKey { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public long FencingToken { get; set; }
    public DateTimeOffset AcquiredAtUtc { get; set; }
    public DateTimeOffset RenewedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
