using TradingFlow.Domain.Research;

namespace TradingFlow.Engine.Storage;

public sealed record ImmutableArtifactWriteRequest(
    EvidenceArtifactReference Artifact);

public sealed record ImmutableArtifactReceipt(
    EvidenceArtifactReference Artifact,
    string ObjectKey,
    bool AlreadyExisted);

public sealed record ImmutableArtifactVerification(
    bool IsValid,
    EvidenceArtifactReference Expected,
    long? ActualByteLength,
    string? ActualSha256,
    string? FailureReason);

/// <summary>
/// Content-addressed immutable byte storage. Implementations must use conditional creation and
/// verify an existing object before reporting an idempotent success.
/// </summary>
public interface IImmutableArtifactStore
{
    Task<ImmutableArtifactReceipt> PutIfAbsentAsync(
        ImmutableArtifactWriteRequest request,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(
        EvidenceArtifactReference artifact,
        CancellationToken cancellationToken = default);

    Task<ImmutableArtifactVerification> VerifyAsync(
        EvidenceArtifactReference artifact,
        CancellationToken cancellationToken = default);
}

public enum ImmutableArtifactScanIssue
{
    MetadataMissing = 1,
    MetadataMalformed = 2,
    MetadataSchemaUnsupported = 3,
    PathLayoutInvalid = 4,
    NamespacePathMismatch = 5,
    ShardPathMismatch = 6,
    HashPathMismatch = 7,
    ContentMissing = 8,
    ContentUnreadable = 9,
    ContentLengthMismatch = 10,
    ContentHashMismatch = 11,
    DirectoryUnreadable = 12,
    CandidateUnreadable = 13,
    RootUnavailable = 14,
    ReparsePointUnsupported = 15
}

public sealed record ImmutableArtifactScanFinding(
    ImmutableArtifactScanIssue Code,
    string Detail);

public sealed record ImmutableArtifactScanEntry(
    string ObjectKey,
    string? PhysicalNamespace,
    string? PhysicalSha256,
    EvidenceArtifactReference? DeclaredArtifact,
    long? ActualByteLength,
    string? ActualSha256,
    IReadOnlyList<ImmutableArtifactScanFinding> Findings)
{
    public bool IsValid => Findings.Count == 0;
}

/// <summary>
/// Read-only physical scan used by recovery and retention maintenance. A corrupt object is
/// returned as findings and never aborts discovery of later objects.
/// </summary>
public interface IImmutableArtifactMaintenanceScanner
{
    IAsyncEnumerable<ImmutableArtifactScanEntry> ScanAsync(
        EvidenceObjectNamespace? objectNamespace = null,
        CancellationToken cancellationToken = default);
}
