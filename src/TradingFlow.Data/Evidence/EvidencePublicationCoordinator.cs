using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence;

/// <summary>
/// Journals the exact provider receipt before publishing immutable bytes, then atomically
/// makes the observation discoverable with its publication state.
/// </summary>
public sealed class EvidencePublicationCoordinator : IEvidencePublicationCoordinator
{
    private readonly SqliteEvidenceCatalog catalog;
    private readonly IImmutableArtifactStore artifactStore;
    private readonly TimeProvider timeProvider;

    public EvidencePublicationCoordinator(
        SqliteEvidenceCatalog catalog,
        IImmutableArtifactStore artifactStore,
        TimeProvider? timeProvider = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<EvidenceCatalogCommitResult> PublishSourceObservationAsync(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var publication = await catalog.PrepareSourceObservationPublicationAsync(
            observation,
            timeProvider.GetUtcNow(),
            cancellationToken);
        try
        {
            await artifactStore.PutIfAbsentAsync(
                new ImmutableArtifactWriteRequest(observation.Artifact),
                rawContent,
                cancellationToken);
            await catalog.MarkPublicationObjectPublishedAsync(
                publication.PublicationId,
                timeProvider.GetUtcNow(),
                cancellationToken);
            return await catalog.RegisterSourceObservationAsync(observation, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await catalog.MarkPublicationFailureAsync(
                publication.PublicationId,
                exception.Message,
                timeProvider.GetUtcNow(),
                cancellationToken);
            throw;
        }
    }

    public async Task<EvidencePublicationRecoveryResult> RecoverPendingPublicationsAsync(
        int maximumPublications,
        CancellationToken cancellationToken = default)
    {
        if (maximumPublications <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPublications));
        }

        var pending = await catalog.FindPendingPublicationsAsync(
            maximumPublications,
            cancellationToken);
        var recovered = 0;
        var waiting = 0;
        var quarantined = 0;
        var failed = 0;
        foreach (var publication in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var verification = await artifactStore.VerifyAsync(
                    publication.Artifact,
                    cancellationToken);
                if (!verification.IsValid)
                {
                    if (verification.ActualByteLength is null)
                    {
                        waiting++;
                        continue;
                    }

                    await catalog.MarkPublicationQuarantinedAsync(
                        publication.PublicationId,
                        verification.FailureReason ?? "Immutable artifact verification failed.",
                        timeProvider.GetUtcNow(),
                        cancellationToken);
                    quarantined++;
                    continue;
                }

                await catalog.MarkPublicationObjectPublishedAsync(
                    publication.PublicationId,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                await CompletePublicationAsync(publication, cancellationToken);
                recovered++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                await catalog.MarkPublicationFailureAsync(
                    publication.PublicationId,
                    exception.Message,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
            }
        }

        return new EvidencePublicationRecoveryResult(
            pending.Count,
            recovered,
            waiting,
            quarantined,
            failed);
    }

    private async Task CompletePublicationAsync(
        EvidencePublicationRecord publication,
        CancellationToken cancellationToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(publication.CanonicalOwnerJson);
        switch (publication.OwnerKind)
        {
            case EvidencePublicationOwnerKind.SourceObservation:
                await catalog.RegisterSourceObservationAsync(
                    EvidenceCanonicalJson.Deserialize<EvidenceSourceObservation>(bytes),
                    cancellationToken);
                break;
            case EvidencePublicationOwnerKind.Dataset:
                await catalog.CommitDatasetAsync(
                    EvidenceCanonicalJson.Deserialize<EvidenceDatasetManifest>(bytes),
                    cancellationToken);
                break;
            case EvidencePublicationOwnerKind.ResearchRun:
                await catalog.RegisterResearchRunAsync(
                    EvidenceCanonicalJson.Deserialize<EvidenceResearchRunManifest>(bytes),
                    cancellationToken);
                break;
            default:
                throw new InvalidDataException(
                    $"Publication owner kind '{publication.OwnerKind}' is not supported.");
        }
    }
}
