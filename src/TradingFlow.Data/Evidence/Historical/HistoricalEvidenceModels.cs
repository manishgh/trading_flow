using System.Collections.Immutable;

namespace TradingFlow.Data.Evidence.Historical;

public enum HistoricalFactKind { NewsVersion, EarningsCurrentVintage, CurrentSector, CurrentAsset, CorporateAction, OfficialFilingAcceptance }
public enum HistoricalEvidenceBasis { CapturedProviderVersion, CurrentSnapshot, OfficialAcceptedArchive }
public enum HistoricalAvailabilityMode { StrictOriginalDelivery, ObservedCapture, ConservativeProviderUpdateDiagnostic, OfficialArchivePublicationDiagnostic }

/// <summary>Actual receipt and physical source bytes, never a reconstructed historical receipt.</summary>
public sealed record HistoricalSourceObservation(string ObservationId, string Provider, string Resource,
    string RequestParametersSha256, string ReceiptRelativePath, string ReceiptSha256,
    string PayloadRelativePath, string PayloadSha256, long PayloadBytes, int? HttpStatus,
    DateTimeOffset CapturedAtUtc, bool OriginalDeliveryObserved, string? OriginalDeliveryEvidence);

/// <summary>ValueJson is an observed source assertion, not an authorization or certified security master.</summary>
public sealed record HistoricalFactVersion(string FactId, HistoricalFactKind Kind, string Subject,
    string SourceRecordId, string ContentSha256, HistoricalEvidenceBasis Basis,
    DateTimeOffset? EffectiveAtUtc, DateTimeOffset? PublishedAtUtc, DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset ActualReceivedAtUtc, ImmutableArray<string> SourceObservationIds,
    string ValueJson, string? QualityReason = null);

public sealed record HistoricalSession(DateOnly ExchangeDate, DateTimeOffset OpenUtc, DateTimeOffset CloseUtc);
public sealed record HistoricalClaimDisposition(string Claim, string Status, string Reason);
public sealed record HistoricalEvidenceManifest(string SchemaVersion, string CollectionRoot,
    DateTimeOffset BuiltAtUtc, ImmutableArray<HistoricalSourceObservation> Observations,
    ImmutableArray<HistoricalFactVersion> Facts, ImmutableArray<HistoricalSession> Sessions,
    string? CalendarObservationId, ImmutableArray<HistoricalClaimDisposition> Claims)
{
    public const string CurrentSchema = "historical-evidence-manifest.v1";
}

/// <summary>Explicit, isolated simulation assumption. It does not modify any actual known-at field.</summary>
public sealed record HistoricalReconstructionPolicy(string ContractId, TimeSpan ProviderUpdateLag,
    TimeSpan ProcessingAllowance, TimeSpan OfficialArchiveLag)
{
    public static HistoricalReconstructionPolicy ConservativeV1 { get; } = new(
        "captured-provider-version-next-session-24h.v1", TimeSpan.FromHours(24), TimeSpan.Zero, TimeSpan.FromHours(24));
}

public sealed record HistoricalAvailabilityResolution(string FactId, HistoricalAvailabilityMode Mode,
    bool Available, string Status, string Reason, DateTimeOffset? AvailableAtUtc,
    DateTimeOffset ActualReceivedAtUtc, string? ReconstructionContractId, string? ReconstructionPolicySha256 = null);
public sealed record HistoricalAvailabilityAuditRow(DateTimeOffset DecisionAtUtc, HistoricalFactKind Kind,
    HistoricalAvailabilityMode Mode, string Status, string Reason, int Count, string? ReconstructionPolicySha256 = null);
