namespace TradingFlow.Domain.Persistence;

/// <summary>Registers strategy-run provenance under the owning durable paper job lease.</summary>
public interface IPaperRunLifecycleRepository
{
    Task ValidatePaperRunAsync(ProductionRun requested, CancellationToken cancellationToken);

    Task<PaperRunStart?> StartPaperRunAsync(
        ProductionRun requested, Guid leaseToken, CancellationToken cancellationToken);

    Task<bool> SavePaperEntryStopSnapshotAsync(
        Guid runId, Guid leaseToken, string snapshotJson, DateTimeOffset stoppedAtUtc,
        DateTimeOffset heartbeatAtUtc, CancellationToken cancellationToken);
}
