namespace TradingFlow.Domain.Market;

/// <summary>
/// Identifies one active ownership tenure for a shared market-data stream.
/// Consumers must propagate <see cref="FencingToken"/> with emitted data so work
/// from a superseded owner can be rejected after a lease takeover.
/// </summary>
public sealed record MarketStreamLease(
    string ResourceKey,
    string OwnerId,
    long FencingToken,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset RenewedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Provides atomic ownership operations for a single-owner market-data resource.
/// All timestamps returned by the repository are UTC.
/// </summary>
public interface IMarketStreamLeaseRepository
{
    /// <summary>
    /// Acquires an unowned or expired resource. Reacquiring an active lease as its
    /// current owner is idempotent and renews it without changing its fencing token.
    /// </summary>
    Task<MarketStreamLease?> TryAcquireAsync(
        string resourceKey,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews only an unexpired lease matching both owner and fencing token.
    /// </summary>
    Task<MarketStreamLease?> TryRenewAsync(
        string resourceKey,
        string ownerId,
        long fencingToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Expires an active matching lease while retaining its fencing-token history.
    /// </summary>
    Task<bool> TryReleaseAsync(
        string resourceKey,
        string ownerId,
        long fencingToken,
        CancellationToken cancellationToken = default);
}
