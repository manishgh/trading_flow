using System.Globalization;
using System.Text;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed class RunConfigWriter
{
    private readonly ProjectPaths paths;
    private readonly SimpleYamlReader yamlReader;
    private readonly IArtifactWriter artifactWriter;

    public RunConfigWriter(ProjectPaths paths, SimpleYamlReader yamlReader, IArtifactWriter artifactWriter)
    {
        this.paths = paths;
        this.yamlReader = yamlReader;
        this.artifactWriter = artifactWriter;
    }

    public string WriteBacktestConfig(BacktestRunRequest request)
    {
        var baseConfig = yamlReader.ReadBacktestRun(request.BaseConfigPath);
        var selectedStrategies = request.StrategyPaths.Select(yamlReader.ReadStrategy).ToArray();
        var generatedConfigNeedsNews = baseConfig.News.Enabled || selectedStrategies.Any(StrategyCapabilityInspector.UsesNews);
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
        yaml.AppendLine($"  ticker_timeout_seconds: {Math.Max(baseConfig.Engine.TickerTimeoutSeconds, 1800)}");
        yaml.AppendLine($"  fail_fast: {baseConfig.Engine.FailFast.ToString().ToLowerInvariant()}");
        yaml.AppendLine();
        yaml.AppendLine("time_window:");
        yaml.AppendLine("  type: rolling");
        yaml.AppendLine($"  lookback_days: {request.LookbackDays}");
        yaml.AppendLine($"  warmup_lookback_days: {baseConfig.TimeWindow.WarmupLookbackDays}");
        yaml.AppendLine("  start:");
        yaml.AppendLine("  end:");
        yaml.AppendLine();
        yaml.AppendLine("universe:");
        yaml.AppendLine("  source: wishlist");
        yaml.AppendLine($"  wishlist_id: {request.WishlistId?.ToString() ?? String.Empty}");
        yaml.AppendLine($"  wishlist_name: {request.WishlistName ?? String.Empty}");
        yaml.AppendLine("  resolved_at_utc: " + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
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
        foreach (var interval in ResolveDownloadTimeframes(baseConfig, selectedStrategies))
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
        yaml.AppendLine($"  account_risk_budget_pct: {request.AccountRiskBudgetPct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  max_position_notional_pct: {request.MaxPositionNotionalPct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  max_concurrent_positions: {request.MaxConcurrentPositions}");
        yaml.AppendLine($"  fixed_buy_fee: {baseConfig.Portfolio.FixedBuyFee.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  fixed_sell_fee: {baseConfig.Portfolio.FixedSellFee.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  prevent_overlapping_ticker_positions: {baseConfig.Portfolio.PreventOverlappingTickerPositions.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  max_bar_participation_pct: {baseConfig.Portfolio.MaxBarParticipationPct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  sec_fee_rate: {baseConfig.Portfolio.SecFeeRate.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  finra_taf_per_share: {baseConfig.Portfolio.FinraTafPerShare.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  finra_taf_cap: {baseConfig.Portfolio.FinraTafCap.ToString(CultureInfo.InvariantCulture)}");
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
        yaml.AppendLine($"  enabled: {generatedConfigNeedsNews.ToString().ToLowerInvariant()}");
        if (generatedConfigNeedsNews)
        {
            var providerName = !String.IsNullOrWhiteSpace(baseConfig.News.ProviderName) && !baseConfig.News.ProviderName.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? baseConfig.News.ProviderName
                : baseConfig.Provider.Equals("alpaca", StringComparison.OrdinalIgnoreCase) ? "alpaca" : "none";
            yaml.AppendLine("  provider:");
            yaml.AppendLine($"    name: {providerName}");
            yaml.AppendLine("  veto:");
            yaml.AppendLine($"    ttl_minutes: {baseConfig.News.VetoTtlMinutes}");
            yaml.AppendLine($"    negative_threshold: {baseConfig.News.VetoNegativeThreshold.ToString(CultureInfo.InvariantCulture)}");
            yaml.AppendLine($"  max_articles_per_ticker: {baseConfig.News.MaxArticlesPerTicker}");
            yaml.AppendLine($"  sentiment_timeout_seconds: {baseConfig.News.SentimentTimeoutSeconds}");
        }
        yaml.AppendLine();
        yaml.AppendLine("artifacts:");
        yaml.AppendLine("  retention_mode: full");
        yaml.AppendLine();
        yaml.AppendLine("strategies:");
        foreach (var strategyPath in WriteStrategyConfigs(request, outputPath, runName))
        {
            var relative = Path.GetRelativePath(Path.GetDirectoryName(outputPath)!, strategyPath).Replace('\\', '/');
            yaml.AppendLine($"  - {relative}");
        }

        artifactWriter.WriteText(outputPath, yaml.ToString());
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
            artifactWriter.WriteText(outputStrategyPath, WriteStrategyYaml(effective));
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
        artifactWriter.WriteText(strategyPath, WriteStrategyYaml(updated));
    }

    public string SaveTempConfig(
        string baseConfigPath,
        IEnumerable<string> tickers,
        string strategyPath,
        string? orderExpiration = null,
        string? entryOrderType = null,
        bool allowExtendedHoursTrading = false,
        string? screenerFilter = null,
        string? runName = null,
        bool? newsEnabled = null,
        Guid? wishlistId = null,
        string? wishlistName = null,
        string universeSource = "ephemeral")
    {
        ExtendedHoursOrderPolicy.ValidateConfiguration(
            entryOrderType,
            orderExpiration,
            allowExtendedHoursTrading);
        var rawYaml = File.ReadAllText(baseConfigPath);
        var baseConfig = yamlReader.ReadBacktestRun(baseConfigPath);
        var effectiveNewsEnabled = newsEnabled ?? baseConfig.News.Enabled;
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
        var skipScreenerBlock = false;
        var skipNewsBlock = false;

        foreach (var line in lines)
        {
            if (skipNewsBlock)
            {
                if (line.StartsWith("  ", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                skipNewsBlock = false;
            }

            if (skipScreenerBlock)
            {
                if (line.StartsWith("  ", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                skipScreenerBlock = false;
            }

            if (line.StartsWith("  order_expiration:"))
            {
                continue; // Skip existing order_expiration to avoid duplicates, we write it below
            }

            if (line.StartsWith("  entry_order_type:"))
            {
                continue; // Skip existing entry_order_type to avoid duplicates, we write it below
            }

            if (line.StartsWith("  allow_extended_hours_trading:"))
            {
                continue;
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
                newYaml.AppendLine($"  allow_extended_hours_trading: {allowExtendedHoursTrading.ToString().ToLowerInvariant()}");
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
                skipScreenerBlock = true;
                continue;
            }

            if (line.StartsWith("news:"))
            {
                skipNewsBlock = true;
                continue;
            }

            newYaml.AppendLine(line);
        }

        newYaml.AppendLine();
        newYaml.AppendLine("universe:");
        newYaml.AppendLine($"  source: {universeSource}");
        if (wishlistId is not null)
        {
            newYaml.AppendLine($"  wishlist_id: {wishlistId}");
        }

        if (!String.IsNullOrWhiteSpace(wishlistName))
        {
            newYaml.AppendLine($"  wishlist_name: {wishlistName}");
        }

        newYaml.AppendLine("  resolved_at_utc: " + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        newYaml.AppendLine();
        newYaml.AppendLine("screener:");
        if (!string.IsNullOrWhiteSpace(screenerFilter) && screenerFilter != "[]")
        {
            newYaml.AppendLine("  enabled: true");
            newYaml.AppendLine("  provider: finviz");
            newYaml.AppendLine("  filters:");
            newYaml.AppendLine($"    - \"{screenerFilter}\"");
        }
        else
        {
            newYaml.AppendLine("  enabled: false");
            newYaml.AppendLine("  provider: finviz");
            newYaml.AppendLine("  filters: []");
        }

        newYaml.AppendLine();
        newYaml.AppendLine("news:");
        newYaml.AppendLine($"  enabled: {effectiveNewsEnabled.ToString().ToLowerInvariant()}");
        if (effectiveNewsEnabled)
        {
            var providerName = !String.IsNullOrWhiteSpace(baseConfig.News.ProviderName) &&
                !baseConfig.News.ProviderName.Equals("none", StringComparison.OrdinalIgnoreCase)
                    ? baseConfig.News.ProviderName
                    : baseConfig.Provider.Equals("alpaca", StringComparison.OrdinalIgnoreCase) ? "alpaca" : "none";
            newYaml.AppendLine("  provider:");
            newYaml.AppendLine($"    name: {providerName}");
            newYaml.AppendLine("  veto:");
            newYaml.AppendLine($"    ttl_minutes: {baseConfig.News.VetoTtlMinutes}");
            newYaml.AppendLine($"    negative_threshold: {baseConfig.News.VetoNegativeThreshold.ToString(CultureInfo.InvariantCulture)}");
        }

        artifactWriter.WriteText(outputPath, newYaml.ToString());
        return outputPath;
    }

    public void DeleteTempConfig(string configPath)
    {
        var fullPath = Path.GetFullPath(configPath);
        var tempRoot = Path.GetFullPath(Path.Combine(paths.BacktestConfigsRoot, "temp"));
        var relativePath = Path.GetRelativePath(tempRoot, fullPath);
        var isSafeToDelete = !Path.IsPathRooted(relativePath)
            && !relativePath.Equals("..", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && fullPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);

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
        yaml.AppendLine($"  min_volume_spike_source: {strategy.EntryRules.MinVolumeSpikeSource}");
        yaml.AppendLine($"  volume_confirmation_mode: {strategy.EntryRules.VolumeConfirmationMode}");
        AppendOptionalDecimal(yaml, "  min_volume_liquidity_floor", strategy.EntryRules.MinVolumeLiquidityFloor);
        yaml.AppendLine($"  min_entry_rsi: {strategy.EntryRules.MinEntryRsi.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  max_entry_rsi: {strategy.EntryRules.MaxEntryRsi.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  trend_filter: {strategy.EntryRules.TrendFilter}");
        yaml.AppendLine($"  macd_filter: {strategy.EntryRules.MacdFilter}");
        yaml.AppendLine($"  require_price_above_bb_middle: {strategy.EntryRules.RequirePriceAboveBollingerMiddle.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_macd_histogram_positive: {strategy.EntryRules.RequireMacdHistogramPositive.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_price_above_vwap: {strategy.EntryRules.RequirePriceAboveVwap.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_price_above_ema10: {strategy.EntryRules.RequirePriceAboveEma10.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_price_above_ema20: {strategy.EntryRules.RequirePriceAboveEma20.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_price_above_ema50: {strategy.EntryRules.RequirePriceAboveEma50.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_ema10_above_ema20: {strategy.EntryRules.RequireEma10AboveEma20.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_ema20_above_ema50: {strategy.EntryRules.RequireEma20AboveEma50.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_price_above_sma200: {strategy.EntryRules.RequirePriceAboveSma200.ToString().ToLowerInvariant()}");
        AppendOptionalDecimal(yaml, "  max_vwap_extension_atr", strategy.EntryRules.MaxVwapExtensionAtr);
        yaml.AppendLine($"  opening_range_minutes: {strategy.EntryRules.OpeningRangeMinutes}");
        yaml.AppendLine($"  opening_range_break_buffer: {strategy.EntryRules.OpeningRangeBreakBuffer.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  recent_high_lookback_bars: {strategy.EntryRules.RecentHighLookbackBars}");
        yaml.AppendLine($"  volatility_contraction_lookback_bars: {strategy.EntryRules.VolatilityContractionLookbackBars}");
        yaml.AppendLine($"  require_log_price_rising: {strategy.EntryRules.RequireLogPriceRising.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_log_volume_rising: {strategy.EntryRules.RequireLogVolumeRising.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  log_price_lookback_bars: {strategy.EntryRules.LogPriceLookbackBars}");
        yaml.AppendLine($"  log_volume_lookback_bars: {strategy.EntryRules.LogVolumeLookbackBars}");
        yaml.AppendLine($"  min_log_price_slope: {strategy.EntryRules.MinLogPriceSlope.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  min_log_volume_slope: {strategy.EntryRules.MinLogVolumeSlope.ToString(CultureInfo.InvariantCulture)}");
        AppendOptionalDecimal(yaml, "  min_log_price_r2", strategy.EntryRules.MinLogPriceR2);
        AppendOptionalDecimal(yaml, "  min_log_volume_r2", strategy.EntryRules.MinLogVolumeR2);
        yaml.AppendLine($"  reject_falling_price_rising_volume: {strategy.EntryRules.RejectFallingPriceRisingVolume.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  red_volume_max_price_slope: {strategy.EntryRules.RedVolumeMaxPriceSlope.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  red_volume_min_volume_slope: {strategy.EntryRules.RedVolumeMinVolumeSlope.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  vwap_hold_bars: {strategy.EntryRules.VwapHoldBars}");
        yaml.AppendLine($"  max_entries_per_ticker_per_day: {strategy.EntryRules.MaxEntriesPerTickerPerDay}");
        AppendOptionalDecimal(yaml, "  min_close_location_value", strategy.EntryRules.MinCloseLocationValue);
        AppendOptionalDecimal(yaml, "  max_macd_histogram", strategy.EntryRules.MaxMacdHistogram);
        yaml.AppendLine($"  prior_entry_gain_lookback_bars: {strategy.EntryRules.PriorEntryGainLookbackBars}");
        AppendOptionalDecimal(yaml, "  max_prior_entry_gain_pct", strategy.EntryRules.MaxPriorEntryGainPct);
        yaml.AppendLine($"  require_prior_inside_day: {strategy.EntryRules.RequirePriorInsideDay.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_prior_nr7: {strategy.EntryRules.RequirePriorNr7.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  prior_compression_mode: {strategy.EntryRules.PriorCompressionMode}");
        yaml.AppendLine($"  prior_nr7_lookback_days: {strategy.EntryRules.PriorNr7LookbackDays}");
        AppendOptionalDecimal(yaml, "  min_vwap_distance_atr_for_divergence", strategy.EntryRules.MinVwapDistanceAtrForDivergence);
        yaml.AppendLine($"  divergence_lookback_bars: {strategy.EntryRules.DivergenceLookbackBars}");
        yaml.AppendLine($"  divergence_start_hour: {strategy.EntryRules.DivergenceStartHour}");
        yaml.AppendLine($"  reversion_stretch_lookback_bars: {strategy.EntryRules.ReversionStretchLookbackBars}");
        yaml.AppendLine($"  min_consecutive_down_closes_for_stretch: {strategy.EntryRules.MinConsecutiveDownClosesForStretch}");
        AppendOptionalDecimal(yaml, "  max_reversion_rsi2", strategy.EntryRules.MaxReversionRsi2);
        yaml.AppendLine($"  enable_lower_bollinger_stretch: {strategy.EntryRules.EnableLowerBollingerStretch.ToString().ToLowerInvariant()}");
        AppendOptionalDecimal(yaml, "  veto_fresh_news_hours", strategy.EntryRules.VetoFreshNewsHours);
        yaml.AppendLine($"  vcp_pivot_strength_bars: {strategy.EntryRules.VcpPivotStrengthBars}");
        yaml.AppendLine($"  vcp_maximum_depth_ratio_to_previous: {strategy.EntryRules.VcpMaximumDepthRatioToPrevious.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  vcp_minimum_low_rise_pct: {strategy.EntryRules.VcpMinimumLowRisePct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  vcp_maximum_contraction_to_advance_volume_ratio: {strategy.EntryRules.VcpMaximumContractionToAdvanceVolumeRatio.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  vcp_require_progressive_contraction_volume: {strategy.EntryRules.VcpRequireProgressiveContractionVolume.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  vcp_maximum_volume_ratio_to_previous_contraction: {strategy.EntryRules.VcpMaximumVolumeRatioToPreviousContraction.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  vcp_minimum_volume_reference_bars: {strategy.EntryRules.VcpMinimumVolumeReferenceBars}");
        yaml.AppendLine($"  vcp_breakout_buffer_pct: {strategy.EntryRules.VcpBreakoutBufferPct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  portfolio_rank_mode: {strategy.EntryRules.PortfolioRankMode}");
        yaml.AppendLine($"  reject_weak_close_on_high_relative_volume: {strategy.EntryRules.RejectWeakCloseOnHighRelativeVolume.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  weak_close_max_location_value: {strategy.EntryRules.WeakCloseMaxLocationValue.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  weak_close_min_relative_volume: {strategy.EntryRules.WeakCloseMinRelativeVolume.ToString(CultureInfo.InvariantCulture)}");
        AppendOptionalDecimal(yaml, "  min_day_gain_pct", strategy.EntryRules.MinDayGainPct);
        AppendOptionalDecimal(yaml, "  min_session_gain_pct", strategy.EntryRules.MinSessionGainPct);
        AppendOptionalDecimal(yaml, "  max_pre_entry_session_range_pct", strategy.EntryRules.MaxPreEntrySessionRangePct);
        AppendOptionalDecimal(yaml, "  max_entry_pullback_from_session_high_pct", strategy.EntryRules.MaxEntryPullbackFromSessionHighPct);
        yaml.AppendLine($"  require_positive_news: {strategy.EntryRules.RequirePositiveNews.ToString().ToLowerInvariant()}");
        AppendOptionalDecimal(yaml, "  min_news_sentiment", strategy.EntryRules.MinNewsSentiment);
        AppendOptionalDecimal(yaml, "  veto_news_sentiment_below", strategy.EntryRules.VetoNewsSentimentBelow);
        yaml.AppendLine($"  max_news_age_hours: {strategy.EntryRules.MaxNewsAgeHours.ToString(CultureInfo.InvariantCulture)}");
        AppendOptionalDecimal(yaml, "  min_catalyst_price_move_pct", strategy.EntryRules.MinCatalystPriceMovePct);
        AppendOptionalDecimal(yaml, "  max_catalyst_price_move_pct", strategy.EntryRules.MaxCatalystPriceMovePct);
        AppendOptionalInt(yaml, "  max_catalyst_confirmation_bars", strategy.EntryRules.MaxCatalystConfirmationBars);
        AppendOptionalDecimal(yaml, "  gap_variant_min_pct", strategy.EntryRules.GapVariantMinPct);
        AppendOptionalDecimal(yaml, "  min_adx", strategy.EntryRules.MinAdx);
        yaml.AppendLine($"  require_adx_rising: {strategy.EntryRules.RequireAdxRising.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  adx_rising_lookback_bars: {strategy.EntryRules.AdxRisingLookbackBars}");
        yaml.AppendLine($"  require_obv_rising: {strategy.EntryRules.RequireObvRising.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  obv_rising_lookback_bars: {strategy.EntryRules.ObvRisingLookbackBars}");
        AppendOptionalDecimal(yaml, "  min_obv_change", strategy.EntryRules.MinObvChange);
        yaml.AppendLine($"  enable_entry_bar_confirmation: {strategy.EntryRules.EnableEntryBarConfirmation.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  min_entry_bar_close_location_value: {strategy.EntryRules.MinEntryBarCloseLocationValue.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  reject_entry_bar_close_location_below_minimum: {strategy.EntryRules.RejectEntryBarCloseLocationBelowMinimum.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  reject_entry_bar_breaks_signal_midpoint: {strategy.EntryRules.RejectEntryBarBreaksSignalMidpoint.ToString().ToLowerInvariant()}");
        AppendOptionalDecimal(yaml, "  max_vwap_extension_pct_for_direct_entry", strategy.EntryRules.MaxVwapExtensionPctForDirectEntry);
        AppendOptionalDecimal(yaml, "  extended_vwap_min_entry_bar_close_location_value", strategy.EntryRules.ExtendedVwapMinEntryBarCloseLocationValue);
        AppendOptionalDecimal(yaml, "  max_bollinger_position_for_direct_entry", strategy.EntryRules.MaxBollingerPositionForDirectEntry);
        AppendOptionalDecimal(yaml, "  extended_bollinger_min_entry_bar_close_location_value", strategy.EntryRules.ExtendedBollingerMinEntryBarCloseLocationValue);
        yaml.AppendLine($"  require_price_above_sma10: {strategy.EntryRules.RequirePriceAboveSma10.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_price_above_sma20: {strategy.EntryRules.RequirePriceAboveSma20.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_price_above_sma50: {strategy.EntryRules.RequirePriceAboveSma50.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_sma10_above_sma20: {strategy.EntryRules.RequireSma10AboveSma20.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_sma20_above_sma50: {strategy.EntryRules.RequireSma20AboveSma50.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_price_above_sma150: {strategy.EntryRules.RequirePriceAboveSma150.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_sma50_above_sma150: {strategy.EntryRules.RequireSma50AboveSma150.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_sma150_above_sma200: {strategy.EntryRules.RequireSma150AboveSma200.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_price_above_sma50_daily: {strategy.EntryRules.RequirePriceAboveSma50Daily.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_price_above_sma200_daily: {strategy.EntryRules.RequirePriceAboveSma200Daily.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  anchored_vwap_mode: {strategy.EntryRules.AnchoredVwapMode}");
        yaml.AppendLine($"  anchored_vwap_lookback_bars: {strategy.EntryRules.AnchoredVwapLookbackBars}");
        yaml.AppendLine($"  require_price_above_anchored_vwap: {strategy.EntryRules.RequirePriceAboveAnchoredVwap.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_price_below_anchored_vwap_for_short: {strategy.EntryRules.RequirePriceBelowAnchoredVwapForShort.ToString().ToLowerInvariant()}");
        AppendOptionalDecimal(yaml, "  max_anchored_vwap_extension_atr", strategy.EntryRules.MaxAnchoredVwapExtensionAtr);
        yaml.AppendLine($"  short_anchored_vwap_mode: {strategy.EntryRules.ShortAnchoredVwapMode}");
        yaml.AppendLine($"  short_anchored_vwap_lookback_bars: {strategy.EntryRules.ShortAnchoredVwapLookbackBars}");
        yaml.AppendLine($"  require_price_below_short_anchored_vwap: {strategy.EntryRules.RequirePriceBelowShortAnchoredVwap.ToString().ToLowerInvariant()}");
        AppendOptionalDecimal(yaml, "  max_short_anchored_vwap_extension_atr", strategy.EntryRules.MaxShortAnchoredVwapExtensionAtr);
        yaml.AppendLine($"  reclaim_lookback_bars: {strategy.EntryRules.ReclaimLookbackBars}");
        yaml.AppendLine($"  min_reclaim_pullback_depth_pct: {strategy.EntryRules.MinReclaimPullbackDepthPct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  max_reclaim_pullback_depth_pct: {strategy.EntryRules.MaxReclaimPullbackDepthPct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  require_reclaim_low_above_sma50: {strategy.EntryRules.RequireReclaimLowAboveSma50.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_reclaim_close_above_sma10: {strategy.EntryRules.RequireReclaimCloseAboveSma10.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_reclaim_close_above_sma20: {strategy.EntryRules.RequireReclaimCloseAboveSma20.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  short_setup_type: {strategy.EntryRules.ShortSetupType}");
        yaml.AppendLine($"  require_price_below_vwap_for_short: {strategy.EntryRules.RequirePriceBelowVwapForShort.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_macd_bearish_for_short: {strategy.EntryRules.RequireMacdBearishForShort.ToString().ToLowerInvariant()}");
        AppendOptionalDecimal(yaml, "  max_short_news_sentiment", strategy.EntryRules.MaxShortNewsSentiment);
        AppendOptionalDecimal(yaml, "  min_short_catalyst_drop_pct", strategy.EntryRules.MinShortCatalystDropPct);
        AppendOptionalDecimal(yaml, "  max_short_entry_rsi", strategy.EntryRules.MaxShortEntryRsi);
        AppendOptionalDecimal(yaml, "  min_short_entry_rsi", strategy.EntryRules.MinShortEntryRsi);
        AppendOptionalDecimal(yaml, "  max_short_close_location_value", strategy.EntryRules.MaxShortCloseLocationValue);
        yaml.AppendLine("risk_guards:");
        yaml.AppendLine("  per_ticker_daily:");
        yaml.AppendLine($"    enabled: {strategy.EntryRules.EnablePerTickerDailyLossGuard.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"    max_failed_trades: {strategy.EntryRules.MaxPerTickerDailyFailedTrades}");
        AppendOptionalDecimal(yaml, "    max_loss_r", strategy.EntryRules.MaxPerTickerDailyLossR);
        AppendOptionalDecimal(yaml, "    max_loss_pct_of_account", strategy.EntryRules.MaxPerTickerDailyLossPctOfAccount);
        yaml.AppendLine("confluence:");
        yaml.AppendLine($"  enabled: {strategy.Confluence.Enabled.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  timeframe: {strategy.Confluence.Timeframe}");
        yaml.AppendLine($"  ema_period: {strategy.Confluence.EmaPeriod}");
        yaml.AppendLine($"  macd_filter: {strategy.Confluence.MacdFilter}");
        if (strategy.Regime is { } regime)
        {
            yaml.AppendLine("regime:");
            yaml.AppendLine($"  benchmark: {regime.BenchmarkSymbol}");
            yaml.AppendLine($"  rule: {regime.Rule}");
            yaml.AppendLine($"  sma_period: {regime.SmaPeriod}");
        }

        yaml.AppendLine("exit_rules:");
        yaml.AppendLine($"  stop_atr_multiple: {strategy.ExitRules.StopAtrMultiple.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  target_r_multiple: {strategy.ExitRules.TargetRMultiple.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  max_hold_hours: {strategy.ExitRules.MaxHoldHours.ToString(CultureInfo.InvariantCulture)}");
        AppendOptionalInt(yaml, "  max_hold_bars", strategy.ExitRules.MaxHoldBars);
        AppendOptionalDecimal(yaml, "  exit_on_rsi2_above", strategy.ExitRules.ExitOnRsi2Above);
        yaml.AppendLine($"  enable_atr_trailing_stop: {strategy.ExitRules.EnableAtrTrailingStop.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  trailing_stop_atr_multiple: {strategy.ExitRules.TrailingStopAtrMultiple.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  trailing_activation_r: {strategy.ExitRules.TrailingActivationR.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  exit_on_close_below_ema20: {strategy.ExitRules.ExitOnCloseBelowEma20.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  exit_on_close_below_vwap: {strategy.ExitRules.ExitOnCloseBelowVwap.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  exit_on_macd_histogram_negative: {strategy.ExitRules.ExitOnMacdHistogramNegative.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  min_hold_bars_before_technical_exit: {strategy.ExitRules.MinHoldBarsBeforeTechnicalExit}");
        yaml.AppendLine($"  exit_on_log_price_fade: {strategy.ExitRules.ExitOnLogPriceFade.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  exit_log_price_lookback_bars: {strategy.ExitRules.ExitLogPriceLookbackBars}");
        yaml.AppendLine($"  exit_log_volume_lookback_bars: {strategy.ExitRules.ExitLogVolumeLookbackBars}");
        yaml.AppendLine($"  max_exit_log_price_slope: {strategy.ExitRules.MaxExitLogPriceSlope.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  min_exit_log_volume_slope: {strategy.ExitRules.MinExitLogVolumeSlope.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  require_rising_volume_for_log_fade_exit: {strategy.ExitRules.RequireRisingVolumeForLogFadeExit.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_below_vwap_for_log_fade_exit: {strategy.ExitRules.RequireBelowVwapForLogFadeExit.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  enable_confirmed_vwap_exit: {strategy.ExitRules.EnableConfirmedVwapExit.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  confirmed_vwap_exit_bars: {strategy.ExitRules.ConfirmedVwapExitBars}");
        yaml.AppendLine($"  confirmed_vwap_exit_atr_buffer: {strategy.ExitRules.ConfirmedVwapExitAtrBuffer.ToString(CultureInfo.InvariantCulture)}");
        AppendOptionalDecimal(yaml, "  disable_confirmed_vwap_exit_after_r", strategy.ExitRules.DisableConfirmedVwapExitAfterR);
        yaml.AppendLine($"  exit_on_sma10_near_sma20: {strategy.ExitRules.ExitOnSma10NearSma20.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  sma10_near_sma20_pct: {strategy.ExitRules.Sma10NearSma20Pct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  exit_on_sma10_cross_below_sma20: {strategy.ExitRules.ExitOnSma10CrossBelowSma20.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  exit_short_on_sma10_cross_above_sma20: {strategy.ExitRules.ExitShortOnSma10CrossAboveSma20.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  exit_on_ema10_cross_below_ema20: {strategy.ExitRules.ExitOnEma10CrossBelowEma20.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  allow_same_bar_stop_target: {strategy.ExitRules.AllowSameBarStopTarget.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  initial_stop_mode: {strategy.ExitRules.InitialStopMode}");
        yaml.AppendLine($"  profit_target_mode: {strategy.ExitRules.ProfitTargetMode}");
        yaml.AppendLine($"  enable_failed_breakout_circuit_breaker: {strategy.ExitRules.EnableFailedBreakoutCircuitBreaker.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  failed_breakout_bars: {strategy.ExitRules.FailedBreakoutBars}");
        yaml.AppendLine($"  failed_breakout_min_r: {strategy.ExitRules.FailedBreakoutMinR.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  stop_tick_buffer: {strategy.ExitRules.StopTickBuffer.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  require_confirmed_ema20_exit: {strategy.ExitRules.RequireConfirmedEma20Exit.ToString().ToLowerInvariant()}");
        yaml.AppendLine("execution:");
        yaml.AppendLine($"  timeframe: {strategy.Execution.Timeframe}");
        yaml.AppendLine($"  slippage_bps: {strategy.Execution.SlippageBps.ToString(CultureInfo.InvariantCulture)}");
        var confirmation = strategy.Execution.EffectiveConfirmation;
        yaml.AppendLine("  confirmation:");
        yaml.AppendLine($"    enabled: {confirmation.Enabled.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"    max_bars_after_setup: {confirmation.MaxBarsAfterSetup}");
        yaml.AppendLine($"    price_filter: {confirmation.PriceFilter}");
        yaml.AppendLine($"    trend_filter: {confirmation.TrendFilter}");
        yaml.AppendLine($"    momentum_filter: {confirmation.MomentumFilter}");
        AppendOptionalDecimal(yaml, "    min_close_location_value", confirmation.MinCloseLocationValue);
        AppendOptionalDecimal(yaml, "    max_close_location_value", confirmation.MaxCloseLocationValue);
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

    private static void AppendOptionalInt(StringBuilder yaml, string key, int? value)
    {
        yaml.AppendLine(value.HasValue
            ? $"{key}: {value.Value.ToString(CultureInfo.InvariantCulture)}"
            : $"{key}:");
    }

    private static void AppendProviders(StringBuilder yaml, TradingFlow.Domain.Backtesting.BacktestRunConfig baseConfig)
    {
        yaml.AppendLine("providers:");
        yaml.AppendLine("  alpaca:");
        yaml.AppendLine($"    data_feed: {baseConfig.Providers.Alpaca.DataFeed}");
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

    private static IReadOnlyList<string> ResolveDownloadTimeframes(
        TradingFlow.Domain.Backtesting.BacktestRunConfig baseConfig,
        IReadOnlyCollection<TradingFlow.Domain.Strategies.StrategyDefinition> selectedStrategies)
    {
        var intervals = new HashSet<string>(baseConfig.Intervals, StringComparer.OrdinalIgnoreCase);
        var deriveDuration = TimeframeParser.Parse(baseConfig.DerivedTimeframes.Source);

        foreach (var timeframe in selectedStrategies.SelectMany(strategy => new[]
                 {
                     strategy.Timeframe,
                     strategy.Execution.Timeframe,
                     strategy.Confluence.Enabled ? strategy.Confluence.Timeframe : null
                 }))
        {
            if (!String.IsNullOrWhiteSpace(timeframe) &&
                TimeframeParser.Parse(timeframe) < deriveDuration)
            {
                intervals.Add(timeframe);
            }
        }

        return intervals
            .OrderBy(TimeframeParser.Parse)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

        artifactWriter.WriteText(outputPath, yaml.ToString());
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
