using System.Text.Json;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Backtesting.Optimization;
using TradingFlow.Data.Evidence.Governance;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Optimization;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Cli;

internal static class ResearchBacktestCommand
{
    /// <summary>Uses the same immutable input graph and exact admission gate as the saved and file-based Web workflows.</summary>
    internal static async Task<BacktestResult> BacktestAsync(string configPath, string? retryKey, SimpleYamlReader reader,
        StrategyArtifactCatalog artifacts, SqliteResearchTrialRegistry registry, CancellationToken token)
    {
        configPath = Path.GetFullPath(configPath);
        var config = reader.ReadBacktestRun(configPath);
        var files = DurableFileJobRequest.Capture(BacktestRunInputFiles.ResolveForResearchRegistration(configPath, config));
        using var held = files.AcquireVerifiedReadLease();
        config = reader.ReadBacktestRun(configPath);
        files.EnsureContains(BacktestRunInputFiles.ResolveForResearchRegistration(configPath, config));
        var validator = new StrategyRunArtifactValidator(reader, artifacts);
        var strategies = config.Strategies.Select(path => validator.ReadAndAuthorizeRuntimeArtifact(path, StrategySelectionMode.Backtest)).ToArray();
        var hash = EvidenceCanonicalJson.ComputeSha256(files.Files);
        var start = ResearchFileStart.Portfolio("cli-backtest", retryKey ?? hash, "backtest", hash,
            ResearchFileStart.EngineCodeSha256(typeof(ResearchBacktestCommand).Assembly), config, strategies);
        var runner = new BacktestRunner(reader, artifacts);
        return await registry.RunDirectAsync(start, "backtest", async (binding, ct) =>
        {
            using var protectedObservations = ResearchObservationBoundary.Enter(binding.Binding.ProtectedObservationStartUtc);
            using var observationInputs = DurableFileJobRequest.Capture(BacktestRunInputFiles.Resolve(configPath, config)).AcquireVerifiedReadLease();
            var path = Path.Combine(ResultFolder(config.ResultsRoot, start.StartId), "result.json");
            var result = await runner.RunAsync(configPath, path, binding.RegisteredAtUtc, ct);
            var publication = await BacktestResearchPublication.VerifyAsync(result.ResultPath, ct, result.ArtifactManifestPath);
            var resultSha = ResearchStartIdentity.HashFile(result.ResultPath);
            return new DirectResearchCompletion<BacktestResult>(result, result.ResultPath, resultSha,
                CanonicalResults(binding, resultSha, publication, result.FinishedAt), publication);
        }, async (prior, ct) =>
        {
            var result = await ReadResultAsync<BacktestResult>(prior, ct);
            var expected = prior.Publication ?? throw new InvalidDataException("This backtest has no recorded publication manifest identity.");
            await BacktestResearchPublication.VerifyAsync(prior.ArtifactReference!, ct, result.ArtifactManifestPath, expected);
            return result;
        }, token);
    }

    /// <summary>Freezes every parameter search permutation before the optimizer reads market or simulation data.</summary>
    internal static async Task<OptimizationResult> OptimizeAsync(string configPath, string? retryKey, SimpleYamlReader reader,
        StrategyArtifactCatalog artifacts, SqliteResearchTrialRegistry registry, IProgress<BacktestProgress>? progress,
        IProgress<OptimizationProgress>? optimizationProgress, CancellationToken token)
    {
        configPath = Path.GetFullPath(configPath);
        var options = reader.ReadOptimizationConfig(configPath);
        var config = reader.ReadBacktestRun(options.BacktestConfigPath);
        var files = DurableFileJobRequest.Capture(ResearchFileStart.OptimizationInputs(configPath, config, options, includeObservations: false));
        using var held = files.AcquireVerifiedReadLease();
        options = reader.ReadOptimizationConfig(configPath);
        config = reader.ReadBacktestRun(options.BacktestConfigPath);
        files.EnsureContains(ResearchFileStart.OptimizationInputs(configPath, config, options, includeObservations: false));
        var runner = new BacktestRunner(reader, artifacts);
        var strategy = StrategyDecisionRequestAssembler.CreateBacktestRuntimeStrategy(runner.ReadResearchStrategy(options.BaseStrategyPath), artifacts);
        var hash = EvidenceCanonicalJson.ComputeSha256(files.Files);
        var start = ResearchFileStart.Optimization("cli-optimization", retryKey ?? hash, "optimization", hash,
            ResearchFileStart.EngineCodeSha256(typeof(ResearchBacktestCommand).Assembly), config, options, strategy);
        return await registry.RunDirectAsync(start, "optimization", async (binding, ct) =>
        {
            using var protectedObservations = ResearchObservationBoundary.Enter(binding.Binding.ProtectedObservationStartUtc);
            using var observationInputs = DurableFileJobRequest.Capture(ResearchFileStart.OptimizationInputs(configPath, config, options)).AcquireVerifiedReadLease();
            var folder = ResultFolder(config.ResultsRoot, start.StartId);
            var result = await new StrategyOptimizer(reader, runner).OptimizeAsync(configPath, ct, progress, optimizationProgress,
                folder, binding.RegisteredAtUtc, captureResearchEvidence: true);
            var path = Path.Combine(folder, "summary.json");
            var summary = await OptimizationPublication.ReadRecordedAsync(path, ct);
            return new DirectResearchCompletion<OptimizationResult>(result, path, summary.ManifestSha256,
                CanonicalResults(binding, summary.ManifestSha256, summary, result.FinishedAt), summary);
        }, async (prior, ct) =>
        {
            var result = await ReadResultAsync<OptimizationResult>(prior, ct);
            await OptimizationPublication.VerifyRankedAndUnrankedResultsAsync(result, ct);
            return result;
        }, token);
    }

    /// <summary>Links every frozen direct trial to its own verified publication, including unranked optimization permutations.</summary>
    private static CanonicalResearchResultManifest[] CanonicalResults(ResearchStart start, string resultSha,
        ResearchPublicationEvidence publication, DateTimeOffset finishedAt)
    {
        var outcomes = publication.VariantOutcomes?.ToDictionary(item => item.VariantKey, StringComparer.Ordinal);
        return start.Binding.Trials.Select(trial =>
        {
            var variant = start.Binding.Request.Variants.Single(item => item.VariantKey == trial.VariantKey);
            var outcome = outcomes?[trial.VariantKey];
            var metric = outcome?.Metric ?? "completed_executions";
            if (metric != variant.PrimaryMetric) throw new InvalidDataException("The result metric differs from the frozen research objective.");
            var hashes = outcome is null ? new[] { resultSha, publication.ManifestSha256 } :
                [resultSha, publication.ManifestSha256, outcome.ResultSha256, outcome.ManifestSha256];
            return new CanonicalResearchResultManifest(trial.ExperimentId + "-development", trial.ExperimentId, trial.DefinitionSha256,
                start.Binding.Request.StartId, start.Binding.Request.CodeSha256, "development", metric,
                new Dictionary<string, decimal> { [metric] = outcome?.Value ?? 1m }, hashes, finishedAt.ToUniversalTime());
        }).ToArray();
    }

    /// <summary>Retains files from failed or interrupted economic attempts in separate owned directories.</summary>
    private static string ResultFolder(string root, string startId) =>
        Path.Combine(root, "research-starts", startId, Guid.NewGuid().ToString("N"));

    /// <summary>Returns the verified immutable result for an identical CLI retry without reevaluating it.</summary>
    private static async Task<T> ReadResultAsync<T>(ResearchExecutionResult prior, CancellationToken token)
    {
        if (prior.ArtifactReference is null)
            throw new InvalidDataException("The recorded CLI research result has no artifact reference.");
        var bytes = await File.ReadAllBytesAsync(prior.ArtifactReference, token);
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != prior.ArtifactSha256)
            throw new InvalidDataException("The recorded CLI research result failed its saved SHA-256 check.");
        return JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The recorded CLI research result is empty.");
    }
}
