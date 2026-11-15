using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Governance;

public sealed record DirectResearchCompletion<T>(T Value, string ArtifactReference, string ArtifactSha256,
    IReadOnlyList<CanonicalResearchResultManifest>? CanonicalResults = null, ResearchPublicationEvidence? Publication = null);

public sealed partial class SqliteResearchTrialRegistry
{
    /// <summary>Opens the shared local registry. Only deployment configuration may name the reviewed evidence file.</summary>
    public static SqliteResearchTrialRegistry OpenLocal(string localDataRoot, string? registryPath = null, string? reviewsPath = null)
    {
        registryPath ??= Environment.GetEnvironmentVariable("TRADINGFLOW_RESEARCH_REGISTRY_PATH")
            ?? Path.Combine(localDataRoot, "research", "research-trials.db");
        reviewsPath ??= Environment.GetEnvironmentVariable("TRADINGFLOW_RESEARCH_REVIEWS_PATH");
        var reviewRecords = String.IsNullOrWhiteSpace(reviewsPath)
            ? ResearchAdmissionReviews.Empty
            : EvidenceCanonicalJson.Deserialize<ResearchAdmissionReviews>(File.ReadAllBytes(reviewsPath));
        return new(new ResearchTrialRegistryOptions(registryPath, ResearchTrialRegistryOpenMode.OpenOrCreate), reviewRecords);
    }

    /// <summary>Serializes a synchronous start across processes and recovers a finished result without recomputation.</summary>
    public async Task<T> RunDirectAsync<T>(FrozenResearchStart request, string executionId,
        Func<ResearchStart, CancellationToken, Task<DirectResearchCompletion<T>>> run,
        Func<ResearchExecutionResult, CancellationToken, Task<T>> replay, CancellationToken token)
    {
        var start = await ReserveStartAsync(request, token);
        if (start.State == ResearchStartState.Refused) throw new InvalidOperationException(start.Reason);
        if (!start.Binding.Request.Executions.ContainsKey(executionId))
            throw new InvalidDataException("The execution does not belong to the frozen research start.");
        var ownerDirectory = databasePath + ".owners";
        Directory.CreateDirectory(ownerDirectory);
        using var lease = AcquireDirectOwner(ownerDirectory, request.StartId);
        start = await ReserveStartAsync(request, token);
        var prior = await FindExecutionResultAsync(request.StartId, executionId, token);
        if (prior?.State == "completed")
            return await replay(prior, token);

        if (start.State == ResearchStartState.Admitted)
            await RecordExecutionResultAsync(request.StartId,
                new(executionId, "interrupted", clock.GetUtcNow(), Detail: "The previous owner ended before publishing its result."), token);
        await SetStartStateAsync(request.StartId, ResearchStartState.Admitted, null, token);
        try
        {
            var completed = await run(start, token);
            await CompleteDirectAsync(start, executionId, completed, token);
            return completed.Value;
        }
        catch (Exception exception)
        {
            await RecordExecutionResultAsync(request.StartId, new(executionId,
                exception is OperationCanceledException ? "cancelled" : "failed", clock.GetUtcNow(),
                Detail: exception is OperationCanceledException ? "Research evaluation cancelled." : exception.Message), CancellationToken.None);
            throw;
        }
    }

    /// <summary>Retains a stable lock inode; only one synchronous process can enter evaluation for this key.</summary>
    private static FileStream AcquireDirectOwner(string ownerDirectory, string startId)
    {
        try
        {
            return new(Path.Combine(ownerDirectory, ResearchStartIdentity.HashText(startId) + ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("This exact research start is in use. Retry when the current evaluation has finished.", exception);
        }
    }

    /// <summary>Commits verified canonical results and the successful execution event together in the registry database.</summary>
    private async Task CompleteDirectAsync<T>(ResearchStart start, string executionId, DirectResearchCompletion<T> completion, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var manifest in completion.CanonicalResults ?? [])
        {
            if (!start.Binding.Trials.Any(trial => trial.ExperimentId == manifest.ExperimentId) ||
                manifest.ResearchRunId != start.Binding.Request.StartId || manifest.CodeVersion != start.Binding.Request.CodeSha256 ||
                !manifest.OutputArtifactSha256.Contains(completion.ArtifactSha256, StringComparer.Ordinal))
                throw new InvalidDataException("The canonical result differs from its exact admitted execution.");
            var trial = await ReadAndValidateTrialForResultAsync(connection, transaction, manifest, token);
            if (IsHoldoutPartition(trial.Trial, manifest.Partition))
                throw new InvalidDataException("The holdout requires its separate one-use reservation.");
            await RegisterCanonicalResultAsync(connection, transaction, manifest, token, trial.Trial);
        }
        await StoreOutcomeAsync(connection, transaction, start.Binding.Request.StartId,
            new(executionId, "completed", clock.GetUtcNow(), completion.ArtifactReference,
                ResearchStartIdentity.RequireSha256(completion.ArtifactSha256), Publication: completion.Publication), token);
        transaction.Commit();
    }
}
