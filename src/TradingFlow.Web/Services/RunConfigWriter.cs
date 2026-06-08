using System.Globalization;
using System.Text;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed class RunConfigWriter
{
    private readonly ProjectPaths paths;
    private readonly SimpleYamlReader yamlReader;

    public RunConfigWriter(ProjectPaths paths, SimpleYamlReader yamlReader)
    {
        this.paths = paths;
        this.yamlReader = yamlReader;
    }

    public string WriteBacktestConfig(BacktestRunRequest request)
    {
        var baseConfig = yamlReader.ReadBacktestRun(request.BaseConfigPath);
        var runName = SanitizeRunName(request.RunName);
        var outputPath = Path.Combine(paths.GeneratedBacktestConfigsRoot, $"{runName}.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var yaml = new StringBuilder();
        yaml.AppendLine($"run_name: {runName}");
        yaml.AppendLine("mode: backtest");
        yaml.AppendLine();
        yaml.AppendLine("engine:");
        yaml.AppendLine($"  pipeline: {baseConfig.Engine.Pipeline}");
        yaml.AppendLine($"  worker_count: {baseConfig.Engine.WorkerCount}");
        yaml.AppendLine($"  bounded_capacity: {baseConfig.Engine.BoundedCapacity}");
        yaml.AppendLine($"  indicator_warmup_bars: {baseConfig.Engine.IndicatorWarmupBars}");
        yaml.AppendLine($"  fail_fast: {baseConfig.Engine.FailFast.ToString().ToLowerInvariant()}");
        yaml.AppendLine();
        yaml.AppendLine("time_window:");
        yaml.AppendLine("  type: rolling");
        yaml.AppendLine($"  lookback_days: {request.LookbackDays}");
        yaml.AppendLine("  start:");
        yaml.AppendLine("  end:");
        yaml.AppendLine();
        yaml.AppendLine("tickers:");
        foreach (var ticker in request.Tickers.Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yaml.AppendLine($"  - {ticker}");
        }

        yaml.AppendLine();
        yaml.AppendLine("market_data:");
        yaml.AppendLine($"  provider: {baseConfig.Provider}");
        yaml.AppendLine("  download_timeframes:");
        foreach (var interval in baseConfig.Intervals)
        {
            yaml.AppendLine($"    - {interval}");
        }

        yaml.AppendLine($"  derive_from: {baseConfig.DerivedTimeframes.Source}");
        yaml.AppendLine($"  raw_root: {NormalizePath(Path.Combine(paths.RepositoryRoot, "data", "backtest", "raw"))}");
        yaml.AppendLine($"  normalized_root: {NormalizePath(Path.Combine(paths.RepositoryRoot, "data", "backtest", "normalized"))}");
        yaml.AppendLine($"  results_root: {NormalizePath(Path.Combine(paths.RepositoryRoot, "data", "backtest", "results"))}");
        yaml.AppendLine($"  cache_policy: {request.CachePolicy}");
        yaml.AppendLine();
        AppendProviders(yaml, baseConfig);
        yaml.AppendLine();
        yaml.AppendLine("portfolio:");
        yaml.AppendLine($"  starting_capital: {request.StartingCapital.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  risk_per_trade_pct: {request.RiskPerTradePct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  max_position_value_pct: {request.MaxPositionValuePct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  max_concurrent_positions: {request.MaxConcurrentPositions}");
        yaml.AppendLine($"  fixed_buy_fee: {baseConfig.Portfolio.FixedBuyFee.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  fixed_sell_fee: {baseConfig.Portfolio.FixedSellFee.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  prevent_overlapping_ticker_positions: {baseConfig.Portfolio.PreventOverlappingTickerPositions.ToString().ToLowerInvariant()}");
        yaml.AppendLine();
        yaml.AppendLine("signal_source:");
        yaml.AppendLine("  type: internal_candles");
        yaml.AppendLine("  require_signature: false");
        yaml.AppendLine("  secret_env:");
        yaml.AppendLine("  dedupe_window_seconds: 300");
        yaml.AppendLine("  reject_stale_after_seconds: 0");
        yaml.AppendLine();
        AppendValidation(yaml, baseConfig);
        yaml.AppendLine();
        yaml.AppendLine("execution:");
        yaml.AppendLine("  router: simulated");
        yaml.AppendLine("  broker: none");
        yaml.AppendLine("  dry_run: true");
        yaml.AppendLine("  allow_live_orders: false");
        yaml.AppendLine($"  order_type: {baseConfig.Execution.OrderType}");
        yaml.AppendLine();
        yaml.AppendLine("news:");
        yaml.AppendLine("  enabled: false");
        yaml.AppendLine();
        yaml.AppendLine("strategies:");
        foreach (var strategyPath in WriteStrategyConfigs(request, outputPath, runName))
        {
            var relative = Path.GetRelativePath(Path.GetDirectoryName(outputPath)!, strategyPath).Replace('\\', '/');
            yaml.AppendLine($"  - {relative}");
        }

        File.WriteAllText(outputPath, yaml.ToString());
        return outputPath;
    }

    private IReadOnlyList<string> WriteStrategyConfigs(BacktestRunRequest request, string outputPath, string runName)
    {
        var selected = new HashSet<string>(request.StrategyPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var overridesByPath = request.StrategyOverrides
            .Where(x => selected.Contains(Path.GetFullPath(x.StrategyPath)))
            .ToDictionary(x => Path.GetFullPath(x.StrategyPath), StringComparer.OrdinalIgnoreCase);
        var strategyRoot = Path.Combine(Path.GetDirectoryName(outputPath)!, "strategies", runName);
        Directory.CreateDirectory(strategyRoot);
        var generatedPaths = new List<string>();

        foreach (var strategyPath in request.StrategyPaths)
        {
            var definition = yamlReader.ReadStrategy(strategyPath);
            var fullPath = Path.GetFullPath(strategyPath);
            var effective = overridesByPath.TryGetValue(fullPath, out var strategyOverride)
                ? ApplyOverride(definition, strategyOverride)
                : definition;
            var outputStrategyPath = Path.Combine(strategyRoot, Path.GetFileName(strategyPath));
            File.WriteAllText(outputStrategyPath, WriteStrategyYaml(effective));
            generatedPaths.Add(outputStrategyPath);
        }

        return generatedPaths;
    }

    private static TradingFlow.Domain.Strategies.StrategyDefinition ApplyOverride(
        TradingFlow.Domain.Strategies.StrategyDefinition definition,
        StrategyParameterOverride strategyOverride)
    {
        return definition with
        {
            EntryRules = definition.EntryRules with
            {
                MinVolumeSpike = strategyOverride.MinVolumeSpike,
                MinEntryRsi = strategyOverride.MinEntryRsi,
                MaxEntryRsi = strategyOverride.MaxEntryRsi,
                MaxVwapExtensionAtr = strategyOverride.MaxVwapExtensionAtr
            },
            Confluence = definition.Confluence with
            {
                Enabled = strategyOverride.ConfluenceEnabled,
                Timeframe = strategyOverride.ConfluenceTimeframe,
                EmaPeriod = strategyOverride.ConfluenceEmaPeriod
            },
            ExitRules = definition.ExitRules with
            {
                StopAtrMultiple = strategyOverride.StopAtrMultiple,
                TargetRMultiple = strategyOverride.TargetRMultiple,
                MaxHoldHours = strategyOverride.MaxHoldHours,
                EnableAtrTrailingStop = strategyOverride.EnableAtrTrailingStop,
                TrailingStopAtrMultiple = strategyOverride.TrailingStopAtrMultiple,
                TrailingActivationR = strategyOverride.TrailingActivationR,
                MinHoldBarsBeforeTechnicalExit = strategyOverride.MinHoldBarsBeforeTechnicalExit
            },
            Execution = definition.Execution with
            {
                Timeframe = strategyOverride.ExecutionTimeframe,
                SlippageBps = strategyOverride.SlippageBps
            }
        };
    }

    public void SaveStrategyPermanent(string strategyPath, StrategyParameterOverride strategyOverride)
    {
        var definition = yamlReader.ReadStrategy(strategyPath);
        var updated = ApplyOverride(definition, strategyOverride);
        File.WriteAllText(strategyPath, WriteStrategyYaml(updated));
    }

    public string SaveTempConfig(string baseConfigPath, IEnumerable<string> tickers, string strategyPath, string? orderExpiration = null, string? entryOrderType = null, bool extendedHours = false, string? screenerFilter = null, string? runName = null)
    {
        var rawYaml = File.ReadAllText(baseConfigPath);
        var tempName = string.IsNullOrWhiteSpace(runName) ? $"temp-run-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}" : runName;
        var outputPath = Path.Combine(paths.BacktestConfigsRoot, "temp", $"{tempName}.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Replace run_name
        var lines = rawYaml.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith("run_name:"))
            {
                lines[i] = $"run_name: {tempName}";
                break;
            }
        }

        // Replace tickers and strategies blocks
        var newYaml = new StringBuilder();
        var inTickers = false;
        var inStrategies = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("  order_expiration:"))
            {
                continue; // Skip existing order_expiration to avoid duplicates, we write it below
            }

            if (line.StartsWith("  entry_order_type:"))
            {
                continue; // Skip existing entry_order_type to avoid duplicates, we write it below
            }

            if (line.StartsWith("  extended_hours:"))
            {
                continue; // Skip existing extended_hours
            }

            if (line.StartsWith("  order_type:"))
            {
                newYaml.AppendLine(line);
                if (!string.IsNullOrEmpty(orderExpiration))
                {
                    newYaml.AppendLine($"  order_expiration: {orderExpiration}");
                }
                if (!string.IsNullOrEmpty(entryOrderType))
                {
                    newYaml.AppendLine($"  entry_order_type: {entryOrderType}");
                }
                newYaml.AppendLine($"  extended_hours: {extendedHours.ToString().ToLowerInvariant()}");
                continue;
            }

            if (line.StartsWith("tickers:"))
            {
                inTickers = true;
                newYaml.AppendLine("tickers:");
                foreach (var ticker in tickers.Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    newYaml.AppendLine($"  - {ticker}");
                }
                continue;
            }
            
            if (line.StartsWith("strategies:"))
            {
                inStrategies = true;
                newYaml.AppendLine("strategies:");
                if (!string.IsNullOrWhiteSpace(strategyPath))
                {
                    var relative = Path.GetRelativePath(Path.GetDirectoryName(outputPath)!, strategyPath).Replace('\\', '/');
                    newYaml.AppendLine($"  - {relative}");
                }
                continue;
            }

            if (inTickers)
            {
                if (line.StartsWith("  - ") || string.IsNullOrWhiteSpace(line)) continue;
                inTickers = false;
            }
            
            if (inStrategies)
            {
                if (line.StartsWith("  - ") || string.IsNullOrWhiteSpace(line)) continue;
                inStrategies = false;
            }

            // Remove existing screener block if any, because we will rewrite it at the end
            if (line.StartsWith("screener:"))
            {
                continue;
            }
            if (line.StartsWith("  provider: finviz") && newYaml.ToString().Contains("screener:")) continue;
            if (line.StartsWith("  enabled:") && newYaml.ToString().Contains("screener:")) continue;
            if (line.StartsWith("  filters:") && newYaml.ToString().Contains("screener:")) continue;

            newYaml.AppendLine(line);
        }

        if (!string.IsNullOrWhiteSpace(screenerFilter) && screenerFilter != "[]")
        {
            newYaml.AppendLine();
            newYaml.AppendLine("screener:");
            newYaml.AppendLine("  enabled: true");
            newYaml.AppendLine("  provider: finviz");
            newYaml.AppendLine("  filters:");
            newYaml.AppendLine($"    - \"{screenerFilter}\"");
        }

        File.WriteAllText(outputPath, newYaml.ToString());
        return outputPath;
    }

    public void DeleteTempConfig(string configPath)
    {
        var fullPath = Path.GetFullPath(configPath);
        var isSafeToDelete = fullPath.Contains("backtest", StringComparison.OrdinalIgnoreCase) 
            && fullPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            && !fullPath.EndsWith("semiconductors-research.yaml", StringComparison.OrdinalIgnoreCase);

        if (isSafeToDelete && File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    private static string WriteStrategyYaml(TradingFlow.Domain.Strategies.StrategyDefinition strategy)
    {
        var yaml = new StringBuilder();
        yaml.AppendLine($"strategy_id: {strategy.StrategyId}");
        yaml.AppendLine($"strategy_name: {strategy.StrategyName}");
        yaml.AppendLine($"source: {strategy.Source}");
        yaml.AppendLine($"version: {strategy.Version}");
        yaml.AppendLine($"timeframe: {strategy.Timeframe}");
        yaml.AppendLine($"direction: {strategy.Direction}");
        yaml.AppendLine("entry_rules:");
        yaml.AppendLine($"  setup_type: {strategy.EntryRules.SetupType}");
        yaml.AppendLine($"  min_volume_spike: {strategy.EntryRules.MinVolumeSpike.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  min_entry_rsi: {strategy.EntryRules.MinEntryRsi.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  max_entry_rsi: {strategy.EntryRules.MaxEntryRsi.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  trend_filter: {strategy.EntryRules.TrendFilter}");
        yaml.AppendLine($"  macd_filter: {strategy.EntryRules.MacdFilter}");
        yaml.AppendLine($"  require_price_above_bb_middle: {strategy.EntryRules.RequirePriceAboveBollingerMiddle.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_macd_histogram_positive: {strategy.EntryRules.RequireMacdHistogramPositive.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_price_above_vwap: {strategy.EntryRules.RequirePriceAboveVwap.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_price_above_ema20: {strategy.EntryRules.RequirePriceAboveEma20.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_price_above_ema50: {strategy.EntryRules.RequirePriceAboveEma50.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_ema20_above_ema50: {strategy.EntryRules.RequireEma20AboveEma50.ToString().ToLowerInvariant()}");
        AppendOptionalDecimal(yaml, "  max_vwap_extension_atr", strategy.EntryRules.MaxVwapExtensionAtr);
        yaml.AppendLine($"  opening_range_minutes: {strategy.EntryRules.OpeningRangeMinutes}");
        yaml.AppendLine($"  recent_high_lookback_bars: {strategy.EntryRules.RecentHighLookbackBars}");
        yaml.AppendLine($"  volatility_contraction_lookback_bars: {strategy.EntryRules.VolatilityContractionLookbackBars}");
        yaml.AppendLine("confluence:");
        yaml.AppendLine($"  enabled: {strategy.Confluence.Enabled.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  timeframe: {strategy.Confluence.Timeframe}");
        yaml.AppendLine($"  ema_period: {strategy.Confluence.EmaPeriod}");
        yaml.AppendLine($"  macd_filter: {strategy.Confluence.MacdFilter}");
        yaml.AppendLine("exit_rules:");
        yaml.AppendLine($"  stop_atr_multiple: {strategy.ExitRules.StopAtrMultiple.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  target_r_multiple: {strategy.ExitRules.TargetRMultiple.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  max_hold_hours: {strategy.ExitRules.MaxHoldHours.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  enable_atr_trailing_stop: {strategy.ExitRules.EnableAtrTrailingStop.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  trailing_stop_atr_multiple: {strategy.ExitRules.TrailingStopAtrMultiple.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  trailing_activation_r: {strategy.ExitRules.TrailingActivationR.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  exit_on_close_below_ema20: {strategy.ExitRules.ExitOnCloseBelowEma20.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  exit_on_close_below_vwap: {strategy.ExitRules.ExitOnCloseBelowVwap.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  exit_on_macd_histogram_negative: {strategy.ExitRules.ExitOnMacdHistogramNegative.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  min_hold_bars_before_technical_exit: {strategy.ExitRules.MinHoldBarsBeforeTechnicalExit}");
        yaml.AppendLine("execution:");
        yaml.AppendLine($"  timeframe: {strategy.Execution.Timeframe}");
        yaml.AppendLine($"  slippage_bps: {strategy.Execution.SlippageBps.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine("session:");
        yaml.AppendLine($"  exchange_timezone: {strategy.Session.ExchangeTimezone}");
        yaml.AppendLine($"  quiet_minutes_after_open: {strategy.Session.QuietMinutesAfterOpen}");
        yaml.AppendLine($"  close_buffer_minutes: {strategy.Session.CloseBufferMinutes}");
        yaml.AppendLine($"  friday_close_buffer_minutes: {strategy.Session.FridayCloseBufferMinutes}");
        return yaml.ToString();
    }

    private static void AppendOptionalDecimal(StringBuilder yaml, string key, decimal? value)
    {
        yaml.AppendLine(value.HasValue
            ? $"{key}: {value.Value.ToString(CultureInfo.InvariantCulture)}"
            : $"{key}:");
    }

    private static void AppendProviders(StringBuilder yaml, TradingFlow.Domain.Backtesting.BacktestRunConfig baseConfig)
    {
        yaml.AppendLine("providers:");
        yaml.AppendLine("  yahoo:");
        yaml.AppendLine("    api:");
        yaml.AppendLine($"      base_url: {baseConfig.Providers.Yahoo.BaseUrl}");
        yaml.AppendLine("      chart_path: /v8/finance/chart/{ticker}");
        yaml.AppendLine("    headers:");
        yaml.AppendLine($"      user_agent: {baseConfig.Providers.Yahoo.Headers.UserAgent}");
        yaml.AppendLine($"      accept: {baseConfig.Providers.Yahoo.Headers.Accept}");
        yaml.AppendLine($"      accept_language: {baseConfig.Providers.Yahoo.Headers.AcceptLanguage}");
        yaml.AppendLine($"    request_timeout_seconds: {baseConfig.Providers.Yahoo.RequestTimeoutSeconds}");
        yaml.AppendLine($"    max_retries: {baseConfig.Providers.Yahoo.MaxRetries}");
        yaml.AppendLine($"    throttle_ms: {baseConfig.Providers.Yahoo.ThrottleMs}");
    }

    private static void AppendValidation(StringBuilder yaml, TradingFlow.Domain.Backtesting.BacktestRunConfig baseConfig)
    {
        yaml.AppendLine("validation:");
        yaml.AppendLine("  out_of_sample:");
        yaml.AppendLine($"    enabled: {baseConfig.Validation.OutOfSample.Enabled.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"    percent: {baseConfig.Validation.OutOfSample.Percent.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine("  walk_forward:");
        yaml.AppendLine($"    enabled: {baseConfig.Validation.WalkForward.Enabled.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"    window_days: {baseConfig.Validation.WalkForward.WindowDays}");
        yaml.AppendLine($"    step_days: {baseConfig.Validation.WalkForward.StepDays}");
        yaml.AppendLine("  benchmark:");
        yaml.AppendLine($"    enabled: {baseConfig.Validation.Benchmark.Enabled.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"    ticker: {baseConfig.Validation.Benchmark.Ticker}");
        yaml.AppendLine("  data_quality:");
        yaml.AppendLine($"    enabled: {baseConfig.Validation.DataQuality.Enabled.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"    max_duplicate_bars: {baseConfig.Validation.DataQuality.MaxDuplicateBars}");
        yaml.AppendLine($"    max_invalid_ohlc_bars: {baseConfig.Validation.DataQuality.MaxInvalidOhlcBars}");
        yaml.AppendLine($"    max_zero_volume_pct: {baseConfig.Validation.DataQuality.MaxZeroVolumePct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine("  bias_risk:");
        yaml.AppendLine($"    universe_source: {baseConfig.Validation.BiasRisk.UniverseSource}");
        yaml.AppendLine("    universe_as_of_date:");
        yaml.AppendLine($"    price_adjustment_policy: {baseConfig.Validation.BiasRisk.PriceAdjustmentPolicy}");
    }

    public string WriteOptimizationConfig(OptimizationRunRequest request)
    {
        var runName = SanitizeRunName(request.RunName);
        var outputPath = Path.Combine(paths.RepositoryRoot, "configs", "optimization", "ui-runs", $"{runName}.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var yaml = new StringBuilder();
        yaml.AppendLine($"run_name: {runName}");
        yaml.AppendLine($"base_strategy: {NormalizePath(request.BaseStrategyPath)}");
        yaml.AppendLine($"backtest_config: {NormalizePath(request.BacktestConfigPath)}");
        yaml.AppendLine($"metric: {request.Metric}");
        yaml.AppendLine("top_n_results: 10");
        yaml.AppendLine("parameters:");
        
        var targetRs = request.TargetRMultipleCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (targetRs.Length > 0)
        {
            yaml.AppendLine("  exit_rules.target_r_multiple:");
            foreach (var r in targetRs) yaml.AppendLine($"    - {r}");
        }

        var stopAtrs = request.StopAtrMultipleCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (stopAtrs.Length > 0)
        {
            yaml.AppendLine("  exit_rules.stop_atr_multiple:");
            foreach (var a in stopAtrs) yaml.AppendLine($"    - {a}");
        }

        var minVols = request.MinVolumeSpikeCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (minVols.Length > 0)
        {
            yaml.AppendLine("  entry_rules.min_volume_spike:");
            foreach (var v in minVols) yaml.AppendLine($"    - {v}");
        }

        File.WriteAllText(outputPath, yaml.ToString());
        return outputPath;
    }

    private static string SanitizeRunName(string runName)
    {
        var cleaned = new string(runName
            .Trim()
            .ToLowerInvariant()
            .Select(ch => Char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());
        cleaned = String.Join("-", cleaned.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return String.IsNullOrWhiteSpace(cleaned)
            ? $"ui-run-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
            : $"{cleaned}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).Replace('\\', '/');
    }
}
