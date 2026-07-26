using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence;

public enum ImmutableArtifactDeleteOutcome
{
    Deleted = 1,
    AlreadyAbsent = 2
}

public sealed record ImmutableArtifactDeleteResult(
    EvidenceArtifactReference Artifact,
    ImmutableArtifactDeleteOutcome Outcome);

/// <summary>
/// Maintenance-only extension for an immutable store. Deletion is conditional on the
/// complete content address so lifecycle code can never remove an unexpected object.
/// </summary>
public interface IImmutableArtifactLifecycleStore :
    IImmutableArtifactStore,
    IImmutableArtifactMaintenanceScanner
{
    Task<ImmutableArtifactDeleteResult> DeleteIfMatchesAsync(
        EvidenceArtifactReference artifact,
        CancellationToken cancellationToken = default);
}
