namespace TradingFlow.Domain.Locking;

public interface ITickerLockService
{
    Task<bool> TryAcquireLockAsync(string ticker, string podId, TimeSpan ttl, CancellationToken cancellationToken);
    Task ReleaseLockAsync(string ticker, string podId, CancellationToken cancellationToken);
}
