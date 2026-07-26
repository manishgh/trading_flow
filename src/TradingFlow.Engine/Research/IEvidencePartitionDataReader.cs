using TradingFlow.Domain.Research;

namespace TradingFlow.Engine.Research;

/// <summary>
/// Reads normalized rows only from immutable partitions named by a committed catalog manifest.
/// Implementations must verify every partition artifact and must not contact a market-data or
/// news provider.
/// </summary>
public interface IEvidencePartitionDataReader
{
    Task<IReadOnlyList<MarketBarEvidenceRow>> ReadMarketBarsAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NewsRevisionEvidenceRow>> ReadNewsRevisionsAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SentimentAssessmentEvidenceRow>> ReadSentimentAssessmentsAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClassifierGroundTruthEvidenceRow>> ReadClassifierGroundTruthAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This evidence reader does not support classifier ground-truth partitions.");

    Task<IReadOnlyList<SecurityMasterSnapshotEvidenceRow>> ReadSecurityMasterSnapshotsAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SymbolIntervalEvidenceRow>> ReadSymbolIntervalsAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CorporateActionEvidenceRow>> ReadCorporateActionsAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UniverseMembershipEvidenceRow>> ReadUniverseMembershipAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SipQuoteEvidenceRow>> ReadSipQuotesAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExchangeSessionEvidenceRow>> ReadExchangeSessionsAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default);
}
