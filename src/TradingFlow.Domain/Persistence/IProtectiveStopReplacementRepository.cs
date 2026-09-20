namespace TradingFlow.Domain.Persistence;

public sealed record ProtectiveStopReplacementReservation(
    Guid CommandId,
    string AccountId,
    string OwnerClientOrderId,
    string BrokerOrderId,
    string ReplacementClientOrderId,
    string Symbol,
    decimal StopPrice,
    string Reason,
    DateTimeOffset RequestedAtUtc,
    Guid RunId,
    int SchemaVersion,
    string ConfigHash,
    string CodeVersion);

public sealed record ProtectiveStopReplacementLease(
    ProtectiveStopReplacementRecord Command,
    Guid LeaseToken,
    DateTimeOffset LeaseExpiresAtUtc);

public interface IProtectiveStopReplacementRepository
{
    Task<ProtectiveStopReplacementRecord> ReserveAsync(
        ProtectiveStopReplacementReservation reservation,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> ListRecoverableCommandIdsAsync(
        string accountId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ProtectiveStopReplacementLease?> TryAcquireLeaseAsync(
        Guid commandId,
        string accountId,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task AssertExecutionAuthorityAsync(
        Guid commandId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);

    Task<ProtectiveStopReplacementRecord?> GetVerifiedBySuccessorBrokerOrderIdAsync(
        string accountId,
        string brokerOrderId,
        CancellationToken cancellationToken = default);

    Task<ProtectiveStopReplacementRecord?> GetVerifiedByReplacementClientOrderIdAsync(
        string replacementClientOrderId,
        CancellationToken cancellationToken = default);

    Task<string> ResolveCurrentBrokerOrderIdAsync(
        string accountId,
        string rootBrokerOrderId,
        CancellationToken cancellationToken = default);

    Task MarkVerifiedWithLifecycleAsync(
        Guid commandId,
        Guid leaseToken,
        string verifiedBrokerOrderId,
        DateTimeOffset verifiedAtUtc,
        BrokerOrderReplacementTransition? lifecycleTransition,
        CancellationToken cancellationToken = default);

    Task ReleaseLeaseAsync(
        Guid commandId,
        Guid leaseToken,
        string error,
        CancellationToken cancellationToken = default);
}
