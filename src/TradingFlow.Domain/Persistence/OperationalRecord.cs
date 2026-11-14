namespace TradingFlow.Domain.Persistence;

/// <summary>
/// Required provenance carried by every operational and research-journal row.
/// </summary>
public abstract class OperationalRecord
{
    public Guid RunId { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public string ConfigHash { get; set; } = string.Empty;
    public string CodeVersion { get; set; } = string.Empty;
}

public sealed class ProductionRun : OperationalRecord
{
    public Guid UniverseSnapshotId { get; set; }
    public Guid? DecisionRunId { get; set; }
    public string Profile { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }

    // Present only on host-owned reconciliation runs; legacy and manual rows carry no inferred owner.
    public long? HostLeaseExpiresAtUtcTicks { get; set; }

    // Paper job attempts own the run through the exact lease that opened it.
    public Guid? PaperJobLeaseToken { get; set; }

    // A pipeline stop is sticky for the run and enforced at the first broker POST.
    public DateTimeOffset? PaperEntriesStoppedAtUtc { get; set; }
}
