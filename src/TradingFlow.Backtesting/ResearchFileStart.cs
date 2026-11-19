using System.Globalization;
using System.Text;
using TradingFlow.Backtesting.Optimization;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Optimization;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Backtesting;

/// <summary>Freezes portfolio and optimization inputs consistently for standalone and durable-file routes.</summary>
public static class ResearchFileStart
{
    /// <summary>Identifies exact deployed simulation code and its direct engine/data dependencies.</summary>
    public static string EngineCodeSha256(params System.Reflection.Assembly[] hostAssemblies) =>
        ResearchStartIdentity.CodeSha256(new[]
        {
            typeof(BacktestRunner).Assembly, typeof(SimpleYamlReader).Assembly, typeof(ResearchTrialDefinition).Assembly,
            typeof(TradingFlow.Data.Evidence.EvidenceCatalogMarketDataProvider).Assembly,
            typeof(TradingFlow.Alpaca.AlpacaMarketDataProvider).Assembly
        }.Concat(hostAssemblies).ToArray());

    /// <summary>Records every selected strategy before the direct runner or legacy file worker can evaluate it.</summary>
    public static FrozenResearchStart Portfolio(string route, string key, string executionId, string inputSha256,
        string codeSha256, BacktestRunConfig run, IReadOnlyList<AuthorizedRuntimeStrategy> strategies)
    {
        var variants = strategies.Select(strategy => Variant(run, strategy.Identity, strategy.Definition,
            EvidenceCanonicalJson.ComputeSha256(strategy.Identity), inputSha256, null, null)).ToArray();
        return new(ResearchStartIdentity.StartId(route, key), ResearchStartKind.Portfolio, inputSha256, codeSha256, variants,
            new Dictionary<string, string[]> { [executionId] = variants.Select(variant => variant.VariantKey).ToArray() });
    }

    /// <summary>Records every independently scored parameter permutation before preparing even the shared market data.</summary>
    public static FrozenResearchStart Optimization(string route, string key, string executionId, string inputSha256,
        string codeSha256, BacktestRunConfig run, OptimizationConfig optimization, AuthorizedRuntimeStrategy baseStrategy)
    {
        var permutations = StrategyOptimizer.FreezePermutations(optimization, run);
        var variants = permutations.Select((parameters, index) => Variant(run, baseStrategy.Identity, baseStrategy.Definition,
            OptimizationVariantKey(index, parameters), inputSha256,
            optimization.Metric, parameters)).ToArray();
        return new(ResearchStartIdentity.StartId(route, key), ResearchStartKind.Optimization, inputSha256, codeSha256, variants,
            new Dictionary<string, string[]> { [executionId] = variants.Select(variant => variant.VariantKey).ToArray() });
    }

    /// <summary>Uses the same deterministic ordinal and parameter bytes before and after evaluating a permutation.</summary>
    internal static string OptimizationVariantKey(int index, IReadOnlyDictionary<string, object> parameters) =>
        index.ToString(CultureInfo.InvariantCulture) + "-" + EvidenceCanonicalJson.ComputeSha256(parameters);

    /// <summary>Uses the ordinary backtest input closure plus exact optimization and base-strategy files.</summary>
    public static IEnumerable<string> OptimizationInputs(string configPath, BacktestRunConfig run, OptimizationConfig optimization,
        bool includeObservations = true)
    {
        yield return configPath;
        yield return optimization.BaseStrategyPath;
        var manifestPath = optimization.BaseStrategyPath + ".artifact.json";
        if (File.Exists(manifestPath)) yield return manifestPath;
        if (StrategyExperimentArtifactStore.FindManifestForStrategyDocument(optimization.BaseStrategyPath) is { } experimentManifest)
            yield return experimentManifest;
        var inputs = includeObservations ? BacktestRunInputFiles.Resolve(optimization.BacktestConfigPath, run) :
            BacktestRunInputFiles.ResolveForResearchRegistration(optimization.BacktestConfigPath, run);
        foreach (var path in inputs)
            yield return path;
    }

    /// <summary>Uses verified content-addressed datasets. Moving provider responses are eligible only as reviewed diagnostic replays.</summary>
    private static FrozenResearchVariant Variant(BacktestRunConfig run, StrategyArtifactIdentity identity, StrategyDefinition strategy,
        string variantKey, string configHash, string? objective, IReadOnlyDictionary<string, object>? parameters)
    {
        var catalog = run.Provider.Equals("evidence_catalog", StringComparison.OrdinalIgnoreCase) ? run.Providers.EvidenceCatalog : null;
        var fredVixDataset = ResolveFrozenVixDatasetId(run);
        var datasets = catalog is null ? new[] { "unverified-provider:" + run.Provider } :
            new[] { catalog.DailyDatasetId, catalog.ExchangeSessionsDatasetId, catalog.CorporateActionsDatasetId, catalog.SecurityMasterDatasetId,
                    "screen-membership:" + catalog.RankedListSha256, run.Universe?.RankedMembership?.ManifestSha256,
                    fredVixDataset,
                    run.Engine.ExecutionSourceManifestSha256, run.Engine.EvidenceDependencyGraphSha256 }
                .Where(value => value is not null).Select(value => value!)
                .Concat(catalog.FiveMinuteDatasetIds).Concat(catalog.OneMinuteDatasetIds).Concat(catalog.HourlyDatasetIds ?? [])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var partitionFrozen = run.TimeWindow.Start is not null && run.TimeWindow.End is not null &&
            run.TimeWindow.End > run.TimeWindow.Start && !run.TimeWindow.Type.Equals("rolling", StringComparison.OrdinalIgnoreCase);
        ResearchPartitionDefinition[] partitions = partitionFrozen
            ? [new("development", run.TimeWindow.Start!.Value.ToUniversalTime(), run.TimeWindow.End!.Value.ToUniversalTime(), TimeSpan.Zero)]
            : [];
        var portfolio = run.Portfolio;
        return new(variantKey, identity.StrategyId, $"Evaluate {identity.StrategyId}@{identity.SemanticVersion} using its frozen execution rules.",
            identity.ContentSha256, datasets,
            new Dictionary<string, string> { ["market_clock"] = "America/New_York", ["stored_clock"] = "UTC" },
            new Dictionary<string, string>
            {
                ["configuration_sha256"] = configHash,
                ["strategy_version"] = identity.SemanticVersion,
                ["price_basis"] = catalog is null ? run.Validation.BiasRisk.PriceAdjustmentPolicy : "as-traded with known split handling",
                ["time_horizon"] = Text(new { run.TimeWindow, run.Intervals, strategy.Timeframe }),
                ["optimization_parameters"] = parameters is null ? "none" : Text(parameters)
            },
            new Dictionary<string, decimal>
            {
                ["account_risk_budget_pct"] = portfolio.AccountRiskBudgetPct,
                ["max_position_notional_pct"] = portfolio.MaxPositionNotionalPct,
                ["slots"] = portfolio.MaxConcurrentPositions
            },
            new Dictionary<string, string>
            {
                ["execution_costs"] = Text(new
                {
                    portfolio.FixedBuyFee, portfolio.FixedSellFee, portfolio.SecFeeRate, portfolio.FinraTafPerShare,
                    portfolio.FinraTafCap, portfolio.MaxBarParticipationPct, portfolio.ImpactBpsAtMaxParticipation, portfolio.CommissionPerShare
                })
            },
            partitions, objective ?? "completed_executions",
            ["Any missing required evidence or incomplete execution", "Investment success policy awaits explicit approval."],
            catalog is not null && partitionFrozen && run.Engine.ExecutionSourceManifestPath is null && run.Engine.EvidenceManifestPath is null);
    }

    /// <summary>
    /// Reads the screen manifest's frozen FRED VIXCLS SHA-256 when the run references a published research screen on disk
    /// that includes the VIX copy, returning its trial-registry dataset tag ("fred-vixcls:&lt;sha256&gt;"), or null otherwise.
    /// </summary>
    private static string? ResolveFrozenVixDatasetId(BacktestRunConfig run)
    {
        if (run.Universe?.RankedMembership is not { } ranked || !File.Exists(ranked.ManifestPath))
            return null;
        var folder = Path.GetDirectoryName(ranked.ManifestPath);
        if (String.IsNullOrWhiteSpace(folder)) return null;
        var (manifest, _, _) = TradingFlow.Engine.Universe.PublishedResearchScreen.ReadDefinition(folder, ranked.ManifestSha256);
        return manifest.Outputs.TryGetValue(TradingFlow.Engine.Universe.ResearchScreenManifest.FredVixFileName, out var sha)
            ? "fred-vixcls:" + sha
            : null;
    }

    /// <summary>Preserves all exact structured formula and cost data in the registry's existing string contract.</summary>
    private static string Text<T>(T value) => Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(value));
}
