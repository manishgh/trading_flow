namespace TradingFlow.Domain.Persistence;

public sealed class UniversePreviewRecord
{
    public Guid UniverseSnapshotId { get; set; }
    public string Horizon { get; set; } = string.Empty;
    public string RequestSha256 { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public DateTimeOffset ResolvedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string PreviewJson { get; set; } = string.Empty;
}

public sealed class CandidateRunRequestRecord
{
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestSha256 { get; set; } = string.Empty;
    public Guid CandidateRunId { get; set; }
    public Guid UniverseSnapshotId { get; set; }
    public string Mode { get; set; } = string.Empty;
    public string StrategyIdentitiesJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class ApplicationEventRecord
{
    public long EventSequence { get; set; }
    public string StreamName { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
}

public sealed class OrderPreviewRecord
{
    public Guid PreviewId { get; set; }
    public string TokenSha256 { get; set; } = string.Empty;
    public string RequestSha256 { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string PreviewJson { get; set; } = string.Empty;
    public string? OutcomeJson { get; set; }
    public Guid? ConfirmationLeaseToken { get; set; }
    public DateTimeOffset? ConfirmationLeaseExpiresAtUtc { get; set; }
    public int Version { get; set; }
}

public sealed record CandidateRunReservation(
    Guid CandidateRunId,
    bool Created);

public interface IUniversePreviewRepository
{
    Task SaveAsync(UniversePreviewRecord preview, CancellationToken cancellationToken = default);

    Task<UniversePreviewRecord?> GetAsync(
        Guid universeSnapshotId,
        CancellationToken cancellationToken = default);

    Task<CandidateRunReservation> ReserveCandidateRunAsync(
        CandidateRunRequestRecord request,
        CancellationToken cancellationToken = default);
}

public interface IApplicationEventRepository
{
    Task<long> AppendAsync(
        string streamName,
        string eventType,
        DateTimeOffset occurredAtUtc,
        string payloadJson,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApplicationEventRecord>> ListAsync(
        string streamName,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);

    Task<(long Floor, long Latest)> GetBoundsAsync(
        string streamName,
        CancellationToken cancellationToken = default);
}

public sealed record OrderPreviewConfirmationClaim(
    OrderPreviewRecord Preview,
    Guid? LeaseToken,
    bool OwnsConfirmation);

public interface IOrderPreviewRepository
{
    Task<OrderPreviewRecord> SaveAsync(OrderPreviewRecord preview, CancellationToken cancellationToken = default);
    Task<OrderPreviewRecord?> GetByTokenHashAsync(string tokenSha256, CancellationToken cancellationToken = default);
    Task<OrderPreviewConfirmationClaim> ClaimConfirmationAsync(
        string tokenSha256,
        string idempotencyKey,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
    Task<OrderPreviewRecord> CompleteAsync(
        Guid previewId,
        Guid leaseToken,
        string status,
        string outcomeJson,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default);
}

public sealed class IdempotencyKeyConflictException(string key) : InvalidOperationException(
    $"Idempotency key '{key}' was already used for a different request.");
