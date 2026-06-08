namespace TradingFlow.Domain.Locking;

public sealed class TickerLockEntity
{
    public string Ticker { get; set; } = string.Empty;
    public string PodId { get; set; } = string.Empty;
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
