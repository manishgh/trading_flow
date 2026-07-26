using TradingFlow.Data.Evidence.Collection;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Domain.Research;

namespace TradingFlow.Research.Orchestration;

public sealed class EvidenceDatasetCollectionWorkflowException : InvalidOperationException
{
    public EvidenceDatasetCollectionWorkflowException(
        string jobId,
        EvidenceCollectionState collectionState)
        : base(
            $"Evidence collection '{jobId}' stopped in state '{collectionState}' " +
            "and cannot enter normalization.")
    {
        JobId = jobId;
        CollectionState = collectionState;
    }

    public string JobId { get; }

    public EvidenceCollectionState CollectionState { get; }
}

/// <summary>
/// Runs one homogeneous raw evidence collection through immutable dataset commit.
/// Collection and normalization retain ownership of their checkpoints, recovery,
/// validation, and publication semantics; this boundary only sequences them.
/// </summary>
public sealed class EvidenceDatasetCollectionWorkflow
{
    private readonly EvidenceHttpCollector collector;
    private readonly EvidenceNormalizationOrchestrator normalizationOrchestrator;

    public EvidenceDatasetCollectionWorkflow(
        EvidenceHttpCollector collector,
        EvidenceNormalizationOrchestrator normalizationOrchestrator)
    {
        this.collector = collector ?? throw new ArgumentNullException(nameof(collector));
        this.normalizationOrchestrator = normalizationOrchestrator ??
            throw new ArgumentNullException(nameof(normalizationOrchestrator));
    }

    public async Task<EvidenceNormalizationResult> RunAsync(
        EvidenceCollectionPlan plan,
        EvidenceNormalizationJob normalizationJob,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(normalizationJob);
        ValidateBoundary(plan, normalizationJob);

        using var lease = await AsyncRequestGate.AcquireAsync(
            $"evidence-dataset:{plan.JobId}",
            cancellationToken);
        var collection = await collector.CollectAsync(plan, cancellationToken);
        if (!CanNormalize(collection.State))
        {
            throw new EvidenceDatasetCollectionWorkflowException(
                plan.JobId,
                collection.State);
        }

        var result = await normalizationOrchestrator.NormalizeAsync(
            normalizationJob,
            cancellationToken);
        if (!result.JobId.Equals(plan.JobId, StringComparison.Ordinal) ||
            String.IsNullOrWhiteSpace(result.DatasetId))
        {
            throw new InvalidDataException(
                $"Normalization returned an invalid committed result for '{plan.JobId}'.");
        }

        return result;
    }

    private static bool CanNormalize(EvidenceCollectionState state) =>
        state is
            EvidenceCollectionState.Normalizing or
            EvidenceCollectionState.Validating or
            EvidenceCollectionState.Committed;

    private static void ValidateBoundary(
        EvidenceCollectionPlan plan,
        EvidenceNormalizationJob normalizationJob)
    {
        if (!plan.JobId.Equals(normalizationJob.JobId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The normalization job must reference the collection plan job.",
                nameof(normalizationJob));
        }

        var first = plan.Requests[0];
        if (plan.Requests.Any(request =>
                !request.Provider.Equals(first.Provider, StringComparison.Ordinal) ||
                !request.Endpoint.Equals(first.Endpoint, StringComparison.Ordinal) ||
                !request.DataFeed.Equals(first.DataFeed, StringComparison.Ordinal) ||
                !request.Adjustment.Equals(first.Adjustment, StringComparison.Ordinal) ||
                !request.Currency.Equals(first.Currency, StringComparison.Ordinal) ||
                request.RequestedStartUtc is null ||
                request.RequestedEndUtc is null ||
                request.AsOfDate is null))
        {
            throw new ArgumentException(
                "The collection plan must contain homogeneous ranged requests.",
                nameof(plan));
        }
    }
}
