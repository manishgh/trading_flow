namespace TradingFlow.Domain.Persistence;

public sealed record CandidateCatalystAuditEvidence(
    Guid CatalystResultId,
    string Provider,
    string ProviderArticleId,
    DateTimeOffset ProviderPublishedAtUtc,
    DateTimeOffset FirstReceivedAtUtc,
    string Category,
    string Direction,
    decimal CompositeScore,
    string? Headline,
    string? Url,
    string EvidenceSha256);

public sealed record CandidateExecutionAuditEvidence(
    Guid? OrderIntentId,
    string? ClientOrderId,
    string? BrokerOrderId,
    string State,
    decimal? RequestedQuantity,
    decimal? FilledQuantity,
    decimal? AverageFillPrice,
    DateTimeOffset? UpdatedAtUtc,
    string? RejectionCode,
    bool HasOpenPosition);

public interface ICandidateAuditEvidenceRepository
{
    Task<CandidateCatalystAuditEvidence?> GetCatalystAsync(
        CandidateRecord candidate,
        CancellationToken cancellationToken = default);

    Task<CandidateExecutionAuditEvidence?> GetExecutionAsync(
        CandidateRecord candidate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<Guid>> GetOpenPositionCandidateIdsAsync(
        Guid runId,
        CancellationToken cancellationToken = default);
}
