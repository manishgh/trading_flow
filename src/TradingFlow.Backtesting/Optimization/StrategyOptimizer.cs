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

    public async Task<OptimizationResult> OptimizeAsync(
        string optimizationConfigPath,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null,
        IProgress<OptimizationProgress>? optimizationProgress = null)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var config = yamlReader.ReadOptimizationConfig(optimizationConfigPath);
        var baseStrategy = yamlReader.ReadStrategy(config.BaseStrategyPath);
        var backtestConfig = yamlReader.ReadBacktestRun(config.BacktestConfigPath);

        var permutations = GeneratePermutations(config.Parameters);
        var runs = new List<OptimizationRun>();
        progress?.Report(BacktestProgress.StageOnly(
            "optimization_prepare_market",
            $"Preparing candles and indicators once for {permutations.Count} optimization permutation(s)."));
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

        for (var i = 0; i < permutations.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = permutations[i];
            var permutationNumber = i + 1;
            var permutationLabel = $"Permutation {permutationNumber} of {permutations.Count}";

            var testStrategy = ApplyParameters(baseStrategy, p);
            // Give it a unique ID to distinguish
            testStrategy = testStrategy with { StrategyId = $"{baseStrategy.StrategyId}-opt-{i}" };
            var parameterText = FormatParameters(p);
            progress?.Report(BacktestProgress.StageOnly("optimization_run", $"Running {permutationLabel.ToLowerInvariant()}: {testStrategy.StrategyName} [{parameterText}]."));
            optimizationProgress?.Report(new OptimizationProgress(
                "started",
                permutationNumber,
                permutations.Count,
                testStrategy.StrategyName,
                p,
                null,
                RankRuns(runs, config.TopNResults),
                $"Running {testStrategy.StrategyName} with {parameterText}."));

            var runPath = Path.Combine(backtestConfig.ResultsRoot, "optimization", config.RunName, $"run_{i}.json");
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
            optimizationProgress?.Report(new OptimizationProgress(
                "completed",
                permutationNumber,
                permutations.Count,
                testStrategy.StrategyName,
                p,
                optimizationRun,
                RankRuns(runs, config.TopNResults),
                $"Completed {permutationLabel}: return {optimizationRun.TotalReturnPct:0.00}%, daily avg {optimizationRun.AverageDailyReturnPct:0.0000}%, net {optimizationRun.NetProfit:0.00}, drawdown {optimizationRun.MaxDrawdownPct:0.00}%."));
        }

        var topRuns = RankRuns(runs, config.TopNResults);

        var optimizationResult = new OptimizationResult(
            config.RunName,
            baseStrategy.StrategyId,
            config.Metric,
            startedAt,
            DateTimeOffset.UtcNow,
            permutations.Count,
            topRuns);

        var resultPath = Path.Combine(backtestConfig.ResultsRoot, "optimization", $"{config.RunName}_summary.json");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        await _artifactWriter.WriteTextAsync(
            resultPath,
            JsonSerializer.Serialize(optimizationResult, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        progress?.Report(BacktestProgress.StageOnly("optimization_complete", $"Optimization complete. Wrote {resultPath}."));
        return optimizationResult;
    }

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

    private decimal ExtractMetric(BacktestResult result, string strategyId, string metricName)
    {
        var strategy = result.StrategyResults.FirstOrDefault();
        if (strategy == null) return 0;

        return metricName.ToLowerInvariant() switch
        {
            "totalreturnpct" => strategy.TotalReturnPct,
            "averagedailyreturnpct" => strategy.AverageDailyReturnPct,
            "netprofit" => strategy.NetProfit,
            "maxdrawdownpct" => -strategy.MaxDrawdownPct, // negative so higher is better
            "winrate" => strategy.AcceptedTradeCount == 0 ? 0 : ((decimal)strategy.WinningTradeCount / strategy.AcceptedTradeCount) * 100,
            _ => strategy.TotalReturnPct
        };
    }

    private IReadOnlyList<IReadOnlyDictionary<string, object>> GeneratePermutations(IReadOnlyDictionary<string, IReadOnlyList<object>> parameters)
    {
        var result = new List<IReadOnlyDictionary<string, object>>();
        if (parameters.Count == 0)
        {
            result.Add(new Dictionary<string, object>());
            return result;
        }

        var keys = parameters.Keys.ToArray();
        GeneratePermutationsRecursive(parameters, keys, 0, new Dictionary<string, object>(), result);
        return result;
    }

    private void GeneratePermutationsRecursive(
        IReadOnlyDictionary<string, IReadOnlyList<object>> parameters,
        string[] keys,
        int keyIndex,
        Dictionary<string, object> current,
        List<IReadOnlyDictionary<string, object>> result)
    {
        if (keyIndex >= keys.Length)
        {
            result.Add(new Dictionary<string, object>(current));
            return;
        }

        var key = keys[keyIndex];
        var values = parameters[key];

        foreach (var value in values)
        {
            current[key] = value;
            GeneratePermutationsRecursive(parameters, keys, keyIndex + 1, current, result);
        }

        current.Remove(key);
    }

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
            "entry_rules.min_session_relative_volume" => strategy with { EntryRules = strategy.EntryRules with { MinSessionRelativeVolume = Convert.ToDecimal(value) } },
            "entry_rules.min_entry_rsi" => strategy with { EntryRules = strategy.EntryRules with { MinEntryRsi = Convert.ToDecimal(value) } },
            "entry_rules.max_entry_rsi" => strategy with { EntryRules = strategy.EntryRules with { MaxEntryRsi = Convert.ToDecimal(value) } },
            "entry_rules.max_vwap_extension_atr" => strategy with { EntryRules = strategy.EntryRules with { MaxVwapExtensionAtr = Convert.ToDecimal(value) } },
            "entry_rules.opening_range_minutes" => strategy with { EntryRules = strategy.EntryRules with { OpeningRangeMinutes = Convert.ToInt32(value) } },
            "entry_rules.min_session_gain_pct" => strategy with { EntryRules = strategy.EntryRules with { MinSessionGainPct = Convert.ToDecimal(value) } },
            "entry_rules.max_pre_entry_session_range_pct" => strategy with { EntryRules = strategy.EntryRules with { MaxPreEntrySessionRangePct = Convert.ToDecimal(value) } },
            "entry_rules.max_entry_pullback_from_session_high_pct" => strategy with { EntryRules = strategy.EntryRules with { MaxEntryPullbackFromSessionHighPct = Convert.ToDecimal(value) } },
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
