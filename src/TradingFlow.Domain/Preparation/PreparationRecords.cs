using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Wishlists;
namespace TradingFlow.Domain.Preparation;

public sealed class PreparationDefinitionRecord
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public int Revision { get; set; }
    public string OwnerId { get; set; } = "";
    public string AuthorizationStamp { get; set; } = "";
    public long EffectiveTicks { get; set; }
    public string ContentSha256 { get; set; } = "";
    public string SettingsJson { get; set; } = "";
}
public sealed class PreparationOccurrenceRecord
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid DefinitionId { get; set; }
    public string SlotKey { get; set; } = "";
    public long ScheduledTicks { get; set; }
    public string SlotJson { get; set; } = "";
    public string? ResultJson { get; set; }
}
public sealed class PreparationReviewRecord
{
    public Guid Id { get; set; }
    public Guid OccurrenceId { get; set; }
    public Guid WishlistId { get; set; }
    public string OwnerId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RequestSha256 { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Json { get; set; } = "";
}
public sealed class PreparationScheduleCursor { public Guid WorkflowId {get;set;} public long ThroughTicks {get;set;} }

/// <summary>Small projection of saved occurrences; the daily card does not load captured symbol evidence.</summary>
public sealed record PreparationWorkspaceRecord(Guid OccurrenceId, Guid WorkflowId, string TargetDate,
    string Phase, string HandoffUntilUtc, string? RevisionId, string? ExpiresAtUtc, string? Readiness,
    string? Outcome, string? JobStatus, string? ReviewKey, string? ReviewExpiresAtUtc);

public interface IPreparationRepository
{
    Task<long> CursorAsync(Guid workflowId, CancellationToken token);
    Task AdvanceCursorAsync(Guid workflowId, Guid definitionId, long throughTicks, CancellationToken token);
    Task<PreparationDefinitionRecord[]> DefinitionsAsync(CancellationToken token);
    Task<PreparationDefinitionRecord> SaveDefinitionAsync(PreparationDefinitionRecord definition, int expectedRevision, CancellationToken token);
    Task<PreparationOccurrenceRecord[]> OccurrencesAsync(Guid workflowId, CancellationToken token);
    Task<PreparationWorkspaceRecord[]> WorkspaceOccurrencesAsync(string owner, CancellationToken token);
    Task<PreparationOccurrenceRecord?> OccurrenceAsync(Guid id, CancellationToken token);
    Task<PreparationOccurrenceRecord?> ReserveAsync(PreparationOccurrenceRecord occurrence, PersistedJob job, CancellationToken token, Wishlist? sourceWishlist = null, PreparationReviewRecord? sourceBinding = null);
    Task<bool> PublishAsync(Guid occurrenceId, Guid leaseToken, string resultJson, string? eventJson, CancellationToken token);
    Task PublishFailureAsync(Guid id, string resultJson, string? eventJson, CancellationToken token);
    Task<PreparationReviewRecord?> ReviewAsync(string ownerId, string key, CancellationToken token);
    Task<PreparationReviewRecord> HandoffAsync(PreparationReviewRecord review, Wishlist wishlist, UniversePreviewRecord preview, CancellationToken token);
}
