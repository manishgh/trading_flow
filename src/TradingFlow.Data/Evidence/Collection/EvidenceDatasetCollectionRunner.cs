using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Data.Evidence.Screens;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence.Collection;

/// <summary>Result returned after collecting, normalizing, and committing a frozen evidence dataset.</summary>
public sealed record EvidenceCollectionRunOutcome(
    string JobId,
    string DatasetId,
    bool AlreadyCommitted,
    int PartitionCount,
    long RowCount);

/// <summary>Executes one frozen evidence collection and normalization request against Alpaca and the local evidence catalog.</summary>
public interface IEvidenceDatasetCollectionRunner
{
    Task<EvidenceCollectionRunOutcome> RunAsync(
        FrozenEvidenceCollectionRequest request,
        string alpacaKeyId,
        string alpacaSecretKey,
        Uri marketDataRestEndpoint,
        Uri tradingRestEndpoint,
        CancellationToken cancellationToken);
}

/// <summary>
/// Shared runtime implementation that recovers pending catalog publications, collects raw Alpaca receipts via
/// <see cref="EvidenceHttpCollector"/>, and commits Parquet partitions via <see cref="EvidenceNormalizationOrchestrator"/>.
/// </summary>
public sealed class EvidenceDatasetCollectionRunner(
    SqliteEvidenceCatalog catalog,
    IImmutableArtifactStore artifactStore) : IEvidenceDatasetCollectionRunner
{
    /// <summary>
    /// Recovers any pending publications, runs bounded HTTP collection for <paramref name="request"/>, and normalizes
    /// collected receipts into an immutable committed Parquet dataset.
    /// </summary>
    public async Task<EvidenceCollectionRunOutcome> RunAsync(
        FrozenEvidenceCollectionRequest request,
        string alpacaKeyId,
        string alpacaSecretKey,
        Uri marketDataRestEndpoint,
        Uri tradingRestEndpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (String.IsNullOrWhiteSpace(alpacaKeyId) || String.IsNullOrWhiteSpace(alpacaSecretKey))
        {
            throw new InvalidOperationException(
                "Alpaca credentials are not configured. Set ALPACA_KEY_ID / ALPACA_SECRET_KEY or Alpaca:KeyId / Alpaca:SecretKey before collecting 5-minute bars.");
        }

        var publicationCoordinator = new EvidencePublicationCoordinator(catalog, artifactStore);
        var recovery = await publicationCoordinator.RecoverPendingPublicationsAsync(
            maximumPublications: 10_000,
            cancellationToken);
        if (recovery.Quarantined > 0 || recovery.Failed > 0)
        {
            throw new InvalidDataException(
                "Evidence publication recovery found quarantined or failed entries. Inspect the evidence catalog before starting another collection.");
        }

        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        httpClient.DefaultRequestHeaders.Add("APCA-API-KEY-ID", alpacaKeyId);
        httpClient.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", alpacaSecretKey);

        var collector = new EvidenceHttpCollector(
            httpClient,
            catalog,
            publicationCoordinator,
            [
                new AlpacaBarsEvidenceRequestAdapter(marketDataRestEndpoint),
                new AlpacaQuotesEvidenceRequestAdapter(marketDataRestEndpoint),
                new AlpacaNewsEvidenceRequestAdapter(marketDataRestEndpoint),
                new AlpacaExchangeCalendarEvidenceRequestAdapter(tradingRestEndpoint)
            ],
            new EvidenceHttpCollectionOptions(
                workerCount: 4,
                boundedCapacity: 16,
                maximumAttempts: 4,
                requestTimeout: TimeSpan.FromSeconds(30)));

        var normalizer = new EvidenceNormalizationOrchestrator(
            catalog,
            artifactStore,
            new EvidenceParquetPartitionPublisher(new EvidenceParquetCodec(), artifactStore));

        var plan = request.CreatePlan();
        var collection = await collector.CollectAsync(plan, cancellationToken);
        if (collection.State is not (EvidenceCollectionState.Normalizing or EvidenceCollectionState.Validating or EvidenceCollectionState.Committed))
        {
            throw new InvalidOperationException(
                $"Evidence collection '{plan.JobId}' stopped in state '{collection.State}' and cannot enter normalization.");
        }

        var normalized = await normalizer.NormalizeAsync(request.CreateNormalizationJob(plan), cancellationToken);
        if (!normalized.JobId.Equals(plan.JobId, StringComparison.Ordinal) || String.IsNullOrWhiteSpace(normalized.DatasetId))
        {
            throw new InvalidDataException($"Normalization returned an invalid committed result for '{plan.JobId}'.");
        }

        return new EvidenceCollectionRunOutcome(
            normalized.JobId,
            normalized.DatasetId,
            normalized.AlreadyCommitted,
            normalized.PartitionCount,
            normalized.RowCount);
    }
}
