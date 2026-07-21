namespace TradingFlow.Engine.Storage;

/// <summary>
/// Configures immutable raw-event storage. Production retention cannot be shorter than the
/// binding specification's 24-month requirement.
/// </summary>
public sealed class RawArchiveOptions
{
    public const int MinimumRetentionDays = 730;

    public RawArchiveOptions(string rootPath, int retentionDays = MinimumRetentionDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var fullRootPath = Path.GetFullPath(rootPath);
        var volumeRoot = Path.GetPathRoot(fullRootPath);
        if (String.Equals(
                Path.TrimEndingDirectorySeparator(fullRootPath),
                Path.TrimEndingDirectorySeparator(volumeRoot ?? String.Empty),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The raw archive cannot use a filesystem root.", nameof(rootPath));
        }

        if (retentionDays < MinimumRetentionDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionDays),
                retentionDays,
                $"Raw archive retention must be at least {MinimumRetentionDays} days.");
        }

        RootPath = fullRootPath;
        RetentionDays = retentionDays;
    }

    public string RootPath { get; }

    public int RetentionDays { get; }
}

/// <summary>
/// Captures non-secret HTTP transport evidence associated with an inbound raw payload.
/// Authentication headers and query parameters are removed by the archive writer.
/// </summary>
public sealed record RawArchiveHttpMetadata(
    Uri? RequestUri = null,
    int? StatusCode = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Headers = null);

/// <summary>
/// Describes one inbound provider payload before interpretation. Source and artifact type are
/// restricted to safe path segments so caller-controlled values cannot escape the archive root.
/// </summary>
public sealed record RawArchiveRequest(
    string Source,
    string ArtifactType,
    DateTimeOffset ReceivedAtUtc,
    string FileExtension,
    string? ContentType = null,
    string? PresetId = null,
    string? ProviderRecordId = null,
    string? CorrelationId = null,
    string? RunId = null,
    string? ConfigHash = null,
    string? CodeVersion = null,
    RawArchiveHttpMetadata? Http = null,
    IReadOnlyDictionary<string, string>? Attributes = null);

/// <summary>
/// Versioned replay metadata persisted beside every immutable payload.
/// </summary>
public sealed record RawArchiveManifest(
    int SchemaVersion,
    string ArchiveId,
    string Source,
    string ArtifactType,
    DateTimeOffset ReceivedAtUtc,
    string PayloadFileName,
    string? ContentType,
    long ByteLength,
    string Sha256,
    string? PresetId,
    string? ProviderRecordId,
    string? CorrelationId,
    string? RunId,
    string? ConfigHash,
    string? CodeVersion,
    RawArchiveHttpMetadata? Http,
    IReadOnlyDictionary<string, string> Attributes);

public sealed record RawArchiveReceipt(
    string PayloadPath,
    string ManifestPath,
    RawArchiveManifest Manifest);

public sealed record RawArchiveRetentionResult(
    int DateDirectoriesExamined,
    int DateDirectoriesDeleted,
    DateOnly DeleteBeforeDate);

/// <summary>
/// Appends byte-exact provider payloads before parsing and enforces the configured retention floor.
/// </summary>
public interface IRawArchiveWriter
{
    Task<RawArchiveReceipt> ArchiveAsync(
        RawArchiveRequest request,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);

    Task<RawArchiveRetentionResult> EnforceRetentionAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}
