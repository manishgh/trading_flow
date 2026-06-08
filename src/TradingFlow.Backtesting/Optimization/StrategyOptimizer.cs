using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Optimization;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Backtesting.Optimization;

public sealed class StrategyOptimizer(SimpleYamlReader yamlReader, BacktestRunner runner)
{
    public async Task<OptimizationResult> OptimizeAsync(
        string optimizationConfigPath,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var config = yamlReader.ReadOptimizationConfig(optimizationConfigPath);
        var baseStrategy = yamlReader.ReadStrategy(config.BaseStrategyPath);
        var backtestConfig = yamlReader.ReadBacktestRun(config.BacktestConfigPath);
        
        var permutations = GeneratePermutations(config.Parameters);
        var runs = new List<OptimizationRun>();
        
        for (var i = 0; i < permutations.Count; i++)
        {
            var p = permutations[i];
            progress?.Report(BacktestProgress.StageOnly("optimization_run", $"Running permutation {i + 1} of {permutations.Count}..."));
            
            var testStrategy = ApplyParameters(baseStrategy, p);
            // Give it a unique ID to distinguish
            testStrategy = testStrategy with { StrategyId = $"{baseStrategy.StrategyId}-opt-{i}" };
            
            var runPath = Path.Combine(backtestConfig.ResultsRoot, "optimization", config.RunName, $"run_{i}.json");
            
            var result = await runner.RunAsync(
                backtestConfig,
                new[] { testStrategy },
                DateTimeOffset.UtcNow,
                runPath,
                cancellationToken);
                
            runs.Add(new OptimizationRun(
                0,
                p,
                ExtractMetric(result, testStrategy.StrategyId, config.Metric),
                result.Winner?.TotalReturnPct ?? 0,
                result.Winner?.NetProfit ?? 0,
                result.Winner?.MaxDrawdownPct ?? 0,
                result.StrategyResults.FirstOrDefault()?.WinningTradeCount ?? 0,
                result.StrategyResults.FirstOrDefault()?.LosingTradeCount ?? 0,
                result));
        }
        
        var topRuns = runs
            .OrderByDescending(r => r.MetricValue)
            .Take(config.TopNResults)
            .Select((r, i) => r with { Rank = i + 1 })
            .ToArray();
            
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
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(optimizationResult, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        
        progress?.Report(BacktestProgress.StageOnly("optimization_complete", $"Optimization complete. Wrote {resultPath}."));
        return optimizationResult;
    }
    
    private decimal ExtractMetric(BacktestResult result, string strategyId, string metricName)
    {
        var strategy = result.StrategyResults.FirstOrDefault();
        if (strategy == null) return 0;
        
        return metricName.ToLowerInvariant() switch
        {
            "totalreturnpct" => strategy.TotalReturnPct,
            "netprofit" => strategy.NetProfit,
            "maxdrawdownpct" => -strategy.MaxDrawdownPct, // negative so higher is better
            "winrate" => strategy.CandidateTradeCount == 0 ? 0 : ((decimal)strategy.WinningTradeCount / strategy.AcceptedTradeCount) * 100,
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
            "entry_rules.min_entry_rsi" => strategy with { EntryRules = strategy.EntryRules with { MinEntryRsi = Convert.ToDecimal(value) } },
            "entry_rules.max_entry_rsi" => strategy with { EntryRules = strategy.EntryRules with { MaxEntryRsi = Convert.ToDecimal(value) } },
            "entry_rules.max_vwap_extension_atr" => strategy with { EntryRules = strategy.EntryRules with { MaxVwapExtensionAtr = Convert.ToDecimal(value) } },
            "entry_rules.opening_range_minutes" => strategy with { EntryRules = strategy.EntryRules with { OpeningRangeMinutes = Convert.ToInt32(value) } },
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
