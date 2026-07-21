namespace TradingFlow.Domain.Persistence;

public sealed record ReconciliationWriteRequest(
    ProductionRun Run,
    Guid ReconciliationId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string Status,
    string BrokerSnapshotJson,
    string LocalSnapshotJson,
    string DiffJson,
    string DiffHash,
    bool RequiresAcknowledgement);

public sealed record ReconciliationAcknowledgement(
    Guid ReconciliationId,
    string Actor,
    string Reason,
    DateTimeOffset AcknowledgedAtUtc);

public interface IReconciliationRepository
{
    Task<ReconciliationRecord> RecordAsync(
        ReconciliationWriteRequest request,
        CancellationToken cancellationToken = default);

    Task<ReconciliationRecord?> GetOutstandingAsync(
        CancellationToken cancellationToken = default);

    Task<ReconciliationRecord> AcknowledgeAsync(
        ReconciliationAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default);
}
