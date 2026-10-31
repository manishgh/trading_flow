using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Optimization;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Backtesting.Optimization;

public sealed class StrategyOptimizer(SimpleYamlReader yamlReader, BacktestRunner runner, IArtifactWriter? artifactWriter = null)
{
    private readonly IArtifactWriter _artifactWriter = artifactWriter ?? AtomicFileArtifactWriter.Instance;

    /// <summary>Rejects unsupported objectives and excessive work before allocating a workspace or requesting market data.</summary>
    public async Task<OptimizationResult> OptimizeAsync(
        string optimizationConfigPath,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null,
        IProgress<OptimizationProgress>? optimizationProgress = null,
        string? durableAttemptRoot = null,
        DateTimeOffset? evaluationCutoffUtc = null)
    {
        var config = yamlReader.ReadOptimizationConfig(optimizationConfigPath);
        var run = yamlReader.ReadBacktestRun(config.BacktestConfigPath);
        if (evaluationCutoffUtc is not null && run.TimeWindow.Type.Equals("rolling", StringComparison.OrdinalIgnoreCase))
            run = run with { TimeWindow = run.TimeWindow with { End = run.TimeWindow.End ?? evaluationCutoffUtc } };
        ValidateConfiguration(config, run);
        var resultOwner = durableAttemptRoot ?? Path.Combine(run.ResultsRoot, "optimization", config.RunName);
        var workspace = runner.CreateWorkspace(run, Path.Combine(resultOwner, "optimization.json"));
        var scoped = new StrategyOptimizer(yamlReader, workspace.Runner, _artifactWriter);
        return await workspace.ExecuteAsync(() => scoped.OptimizeCoreAsync(optimizationConfigPath, cancellationToken,
            progress, optimizationProgress, durableAttemptRoot, evaluationCutoffUtc, workspace.Run));
    }

    /// <summary>Reuses prepared market state and evaluates one bounded parameter combination at a time.</summary>
    private async Task<OptimizationResult> OptimizeCoreAsync(string optimizationConfigPath, CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress, IProgress<OptimizationProgress>? optimizationProgress,
        string? durableAttemptRoot, DateTimeOffset? evaluationCutoffUtc, BacktestRunConfig scopedRun)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var config = yamlReader.ReadOptimizationConfig(optimizationConfigPath);
        var baseStrategy = yamlReader.ReadStrategy(config.BaseStrategyPath);
        var backtestConfig = scopedRun;

        var permutationCount = ValidateConfiguration(config, backtestConfig);
        var runs = new List<OptimizationRun>();
        progress?.Report(BacktestProgress.StageOnly(
            "optimization_prepare_market",
            $"Preparing candles and indicators once for {permutationCount} optimization permutation(s)."));
        var preparedMarket = await runner.PrepareMarketAsync(
            backtestConfig,
            [baseStrategy],
            cancellationToken,
            progress is null
                ? null
                : new Progress<BacktestProgress>(update =>
                    progress.Report(update with
                    {
                        Stage = $"optimization_{update.Stage}",
                        Message = $"Reusable market state: {update.Message}"
                    })));
        progress?.Report(BacktestProgress.StageOnly(
            "optimization_prepare_market",
            "Reusable candle and indicator state is ready. Evaluating parameter combinations."));

        var i = 0;
        foreach (var p in EnumeratePermutations(config.Parameters))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var permutationNumber = i + 1;
            var permutationLabel = $"Permutation {permutationNumber} of {permutationCount}";

            var testStrategy = ApplyParameters(baseStrategy, p);
            // Give it a unique ID to distinguish
            testStrategy = testStrategy with { StrategyId = $"{baseStrategy.StrategyId}-opt-{i}" };
            var parameterText = FormatParameters(p);
            progress?.Report(BacktestProgress.StageOnly("optimization_run", $"Running {permutationLabel.ToLowerInvariant()}: {testStrategy.StrategyName} [{parameterText}]."));
            optimizationProgress?.Report(new OptimizationProgress(
                "started",
                permutationNumber,
                permutationCount,
                testStrategy.StrategyName,
                p,
                null,
                RankRuns(runs, config.TopNResults),
                $"Running {testStrategy.StrategyName} with {parameterText}."));

            var runPath = durableAttemptRoot is null
                ? Path.Combine(backtestConfig.ResultsRoot, "optimization", config.RunName, $"run_{i}.json")
                : Path.Combine(durableAttemptRoot, "runs", $"run_{i}.json");
            var permutationProgress = progress is null
                ? null
                : new Progress<BacktestProgress>(update =>
                    progress.Report(update with
                    {
                        Stage = $"optimization_{update.Stage}",
                        Message = $"{permutationLabel}: {update.Message}"
                    }));

            var result = await runner.RunPreparedAsync(
                backtestConfig,
                new[] { testStrategy },
                DateTimeOffset.UtcNow,
                runPath,
                preparedMarket,
                cancellationToken,
                permutationProgress);

            var optimizationRun = new OptimizationRun(
                0,
                p,
                ExtractMetric(result, testStrategy.StrategyId, config.Metric),
                result.Winner?.TotalReturnPct ?? 0,
                result.Winner?.AverageDailyReturnPct ?? 0,
                result.Winner?.NetProfit ?? 0,
                result.Winner?.MaxDrawdownPct ?? 0,
                result.StrategyResults.FirstOrDefault()?.WinningTradeCount ?? 0,
                result.StrategyResults.FirstOrDefault()?.LosingTradeCount ?? 0,
                result);
            runs.Add(optimizationRun);
            // Only selected results are published; do not retain every full backtest result while searching.
            runs = RankRuns(runs, config.TopNResults).ToList();
            optimizationProgress?.Report(new OptimizationProgress(
                "completed",
                permutationNumber,
                permutationCount,
                testStrategy.StrategyName,
                p,
                optimizationRun,
                RankRuns(runs, config.TopNResults),
                $"Completed {permutationLabel}: return {optimizationRun.TotalReturnPct:0.00}%, daily avg {optimizationRun.AverageDailyReturnPct:0.0000}%, net {optimizationRun.NetProfit:0.00}, drawdown {optimizationRun.MaxDrawdownPct:0.00}%."));
            i++;
        }

        var topRuns = RankRuns(runs, config.TopNResults);

        var optimizationResult = new OptimizationResult(
            config.RunName,
            baseStrategy.StrategyId,
            config.Metric,
            startedAt,
            DateTimeOffset.UtcNow,
            permutationCount,
            topRuns);

        var resultPath = durableAttemptRoot is null
            ? Path.Combine(backtestConfig.ResultsRoot, "optimization", $"{config.RunName}_summary.json")
            : Path.Combine(durableAttemptRoot, "summary.json");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        await _artifactWriter.WriteTextAsync(
            resultPath,
            JsonSerializer.Serialize(optimizationResult, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        progress?.Report(BacktestProgress.StageOnly("optimization_complete", $"Optimization complete. Wrote {resultPath}."));
        return optimizationResult;
    }

    /// <summary>Retains the best requested results in stable objective order, including stable ties.</summary>
    private static OptimizationRun[] RankRuns(IEnumerable<OptimizationRun> runs, int topN)
    {
        return runs
            .OrderByDescending(r => r.MetricValue)
            .Take(topN)
            .Select((r, i) => r with { Rank = i + 1 })
            .ToArray();
    }

    private static string FormatParameters(IReadOnlyDictionary<string, object> parameters)
    {
        if (parameters.Count == 0)
        {
            return "default parameters";
        }

        return String.Join(", ", parameters.Select(p => $"{p.Key.Split('.').Last()}={p.Value}"));
    }

    /// <summary>Reads the requested strategy's objective; an absent strategy or unknown metric cannot become a zero/default score.</summary>
    internal static decimal ExtractMetric(BacktestResult result, string strategyId, string metricName)
    {
        var metric = NormalizeMetric(metricName);
        var strategy = result.StrategyResults.SingleOrDefault(item => item.StrategyId == strategyId)
            ?? throw new InvalidOperationException($"Optimization result does not contain strategy '{strategyId}'.");

        return metric switch
        {
            "totalreturnpct" => strategy.TotalReturnPct,
            "averagedailyreturnpct" => strategy.AverageDailyReturnPct,
            "netprofit" => strategy.NetProfit,
            "maxdrawdownpct" => -strategy.MaxDrawdownPct, // negative so higher is better
            "winrate" => strategy.AcceptedTradeCount == 0 ? 0 : ((decimal)strategy.WinningTradeCount / strategy.AcceptedTradeCount) * 100,
            _ => throw new InvalidOperationException("The validated optimization objective is not implemented.")
        };
    }

    /// <summary>Checks the complete search against the existing strategy/ticker work budget before any product is generated.</summary>
    public static int ValidateConfiguration(OptimizationConfig config, BacktestRunConfig run)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(run);
        NormalizeMetric(config.Metric);
        if (config.TopNResults <= 0) throw new ArgumentOutOfRangeException(nameof(config), "Optimization top_n_results must be positive.");
        var stockCount = Math.Max(1, run.Tickers.Count);
        var maximumPermutations = run.Engine.MaxStrategyWorkItems / stockCount;
        return CountPermutations(config.Parameters, maximumPermutations);
    }

    /// <summary>Uses division before multiplication, rejecting huge products without overflow or reading their values.</summary>
    internal static int CountPermutations(IReadOnlyDictionary<string, IReadOnlyList<object>> parameters, int maximum)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (maximum <= 0) throw new InvalidOperationException("The configured engine.max_strategy_work_items does not allow even one optimization combination for these stocks.");
        var count = 1;
        foreach (var parameter in parameters)
        {
            if (String.IsNullOrWhiteSpace(parameter.Key) || parameter.Value is null || parameter.Value.Count == 0)
                throw new ArgumentException("Every optimization parameter requires a name and at least one value.", nameof(parameters));
            if (count > maximum / parameter.Value.Count)
                throw new InvalidOperationException($"Optimization combinations exceed the configured strategy/ticker work budget ({maximum} combinations available for these stocks). Reduce the parameter values or increase engine.max_strategy_work_items.");
            count *= parameter.Value.Count;
        }
        return count;
    }

    /// <summary>Produces one independent combination at a time in the existing key/value order, without recursive product allocation.</summary>
    internal static IEnumerable<IReadOnlyDictionary<string, object>> EnumeratePermutations(IReadOnlyDictionary<string, IReadOnlyList<object>> parameters)
    {
        var axes = parameters.ToArray();
        if (axes.Any(axis => axis.Value is null || axis.Value.Count == 0))
            throw new ArgumentException("Optimization parameter values cannot be empty.", nameof(parameters));
        var indexes = new int[axes.Length];
        while (true)
        {
            var combination = new Dictionary<string, object>(axes.Length);
            for (var axis = 0; axis < axes.Length; axis++)
                combination.Add(axes[axis].Key, axes[axis].Value[indexes[axis]]);
            yield return combination;

            var changing = axes.Length - 1;
            while (changing >= 0 && ++indexes[changing] == axes[changing].Value.Count)
            {
                indexes[changing] = 0;
                changing--;
            }
            if (changing < 0) yield break;
        }
    }

    /// <summary>Defines supported objectives once so admission and scoring reject the same misspellings.</summary>
    private static string NormalizeMetric(string metricName) => metricName?.Trim().ToLowerInvariant() switch
    {
        "totalreturnpct" => "totalreturnpct",
        "averagedailyreturnpct" => "averagedailyreturnpct",
        "netprofit" => "netprofit",
        "maxdrawdownpct" => "maxdrawdownpct",
        "winrate" => "winrate",
        _ => throw new ArgumentException($"Unsupported optimization metric '{metricName}'. Choose TotalReturnPct, AverageDailyReturnPct, NetProfit, MaxDrawdownPct or WinRate.", nameof(metricName))
    };

    private StrategyDefinition ApplyParameters(StrategyDefinition baseStrategy, IReadOnlyDictionary<string, object> parameters)
    {
        var strategy = baseStrategy;
        foreach (var kvp in parameters)
        {
            strategy = ApplyParameter(strategy, kvp.Key, kvp.Value);
        }
        return strategy;
    }

    private StrategyDefinition ApplyParameter(StrategyDefinition strategy, string key, object value)
    {
        // Simple case insensitive path matching to replace nested records
        var path = key.ToLowerInvariant();
        return path switch
        {
            "entry_rules.min_volume_spike" => strategy with { EntryRules = strategy.EntryRules with { MinVolumeSpike = Convert.ToDecimal(value) } },
            "entry_rules.min_entry_rsi" => strategy with { EntryRules = strategy.EntryRules with { MinEntryRsi = Convert.ToDecimal(value) } },
            "entry_rules.max_entry_rsi" => strategy with { EntryRules = strategy.EntryRules with { MaxEntryRsi = Convert.ToDecimal(value) } },
            "entry_rules.max_vwap_extension_atr" => strategy with { EntryRules = strategy.EntryRules with { MaxVwapExtensionAtr = Convert.ToDecimal(value) } },
            "entry_rules.volume_sma_period" => strategy with { EntryRules = strategy.EntryRules with { VolumeSmaPeriod = Convert.ToInt32(value) } },
            "entry_rules.volume_sma_rising_lookback_bars" => strategy with { EntryRules = strategy.EntryRules with { VolumeSmaRisingLookbackBars = Convert.ToInt32(value) } },
            "entry_rules.min_volume_sma_rise_pct" => strategy with { EntryRules = strategy.EntryRules with { MinVolumeSmaRisePct = Convert.ToDecimal(value) } },
            "exit_rules.stop_atr_multiple" => strategy with { ExitRules = strategy.ExitRules with { StopAtrMultiple = Convert.ToDecimal(value) } },
            "exit_rules.target_r_multiple" => strategy with { ExitRules = strategy.ExitRules with { TargetRMultiple = Convert.ToDecimal(value) } },
            "exit_rules.max_hold_hours" => strategy with { ExitRules = strategy.ExitRules with { MaxHoldHours = Convert.ToDecimal(value) } },
            "exit_rules.trailing_stop_atr_multiple" => strategy with { ExitRules = strategy.ExitRules with { TrailingStopAtrMultiple = Convert.ToDecimal(value) } },
            "exit_rules.trailing_activation_r" => strategy with { ExitRules = strategy.ExitRules with { TrailingActivationR = Convert.ToDecimal(value) } },
            "exit_rules.min_hold_bars_before_technical_exit" => strategy with { ExitRules = strategy.ExitRules with { MinHoldBarsBeforeTechnicalExit = Convert.ToInt32(value) } },
            _ => throw new ArgumentException($"Optimization parameter path '{key}' is not mapped in StrategyOptimizer.")
        };
    }
}
