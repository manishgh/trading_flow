using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Backtesting;

public sealed partial class BacktestRunner
{
    /// <summary>Recovery uses its admitted cutoff before workspace creation; a changed request cannot supply a replacement clock.</summary>
    private static async Task<BacktestRunConfig> ResolveAdmittedRollingEndAsync(BacktestRunConfig run,
        PreparedMarketDataRequestReference? reference, CancellationToken token)
    {
        if (reference is null || !run.TimeWindow.Type.Equals("rolling", StringComparison.OrdinalIgnoreCase) || run.TimeWindow.End is not null)
            return run;
        var bytes = await File.ReadAllBytesAsync(reference.Path, token);
        var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        if (!actual.Equals(reference.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Prepared comparison request changed before resolving the execution clock.");
        var request = System.Text.Json.JsonSerializer.Deserialize<PreparedMarketDataRequest>(bytes,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Prepared comparison request is empty.");
        return run with { TimeWindow = run.TimeWindow with { End = request.EvaluationCutoffUtc } };
    }

    /// <summary>Freezes provider-backed inputs before evaluation; explicitly injected frozen unit/replay sources retain their separate existing path.</summary>
    private async Task EnsurePreparedDataAsync(BacktestRunConfig run, IReadOnlyCollection<StrategyDefinition> strategies,
        CancellationToken token, IProgress<BacktestProgress>? progress)
    {
        if (preparedData is not null) return;
        if (preparedDataReference is null && marketDataProviderFactory is not null) return;
        // Local CSVs retain their existing file-declared diagnostic path; they have no archived provider coverage claim.
        if (preparedDataReference is null && run.Provider.Equals("csv", StringComparison.OrdinalIgnoreCase)) return;
        if (_transientArtifactRoot is null)
            throw new InvalidOperationException("Provider preparation requires an owned execution workspace.");
        var service = new PreparedMarketDataService(_dataProviders, marketDataProviderFactory, preparedDataBindingStore);
        if (preparedDataReference is not null)
            preparedData = await service.PrepareAsync(preparedDataReference, progress, token);
        else
        {
            var logical = CandidateIdentityConfiguration(run);
            var cutoff = logical.TimeWindow.End ?? DateTimeOffset.UtcNow;
            var request = new PreparedMarketDataRequest(1, preparedComparisonId
                ?? throw new InvalidOperationException("Preparation needs a stable execution identity."), cutoff,
                [new(logical.RunName, logical with { Strategies = [] }, strategies.ToArray())]);
            preparedData = await service.PrepareAsync(request, progress, token);
        }
        ownsPreparedData = true;
        progress?.Report(new BacktestProgress("candles_ready", "The comparison's fixed candles and calendars are ready.",
            null, 0, 0, PreparedMarketData: preparedData.Summary));
    }

    /// <summary>Releases only leases created by this execution; nested workspaces share the parent's immutable selection.</summary>
    internal void ReleasePreparedData()
    {
        if (!ownsPreparedData) return;
        preparedData?.Dispose();
        preparedData = null;
        ownsPreparedData = false;
    }
}
