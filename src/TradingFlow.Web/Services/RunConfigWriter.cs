using System.Globalization;
using System.Text;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
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
    private readonly StrategyArtifactCatalog strategyArtifacts;
    private readonly StrategyExperimentArtifactStore experimentArtifacts;
    private readonly IStrategyExperimentAuthorizationCommands experimentAuthorizations;

    public RunConfigWriter(
        ProjectPaths paths,
        SimpleYamlReader yamlReader,
        IArtifactWriter artifactWriter,
        StrategyArtifactCatalog strategyArtifacts,
        StrategyExperimentArtifactStore experimentArtifacts,
        IStrategyExperimentAuthorizationCommands experimentAuthorizations)
    {
        this.paths = paths;
        this.yamlReader = yamlReader;
        this.artifactWriter = artifactWriter;
        this.strategyArtifacts = strategyArtifacts ?? throw new ArgumentNullException(nameof(strategyArtifacts));
        this.experimentArtifacts = experimentArtifacts ?? throw new ArgumentNullException(nameof(experimentArtifacts));
        this.experimentAuthorizations = experimentAuthorizations
            ?? throw new ArgumentNullException(nameof(experimentAuthorizations));
    }

    /// <summary>Uses the host's configured runtime data root for both backtest plan review and generated run source/archive paths.</summary>
    public BacktestRunConfig ResolveBacktestDataRoots(BacktestRunConfig run) => run with
    {
        RawRoot = Path.Combine(paths.DataRoot, "backtest", "raw"),
        NormalizedRoot = Path.Combine(paths.DataRoot, "backtest", "normalized"),
        ResultsRoot = Path.Combine(paths.DataRoot, "backtest", "results")
    };

    /// <summary>Deletes captured files owned by one rejected backtest attempt; shared profiles, receipts and source archives are excluded.</summary>
    public void DiscardUnacceptedBacktest(string configPath, IReadOnlyList<string>? capturedPaths = null)
    {
        var root = Path.GetFullPath(paths.GeneratedBacktestConfigsRoot).TrimEnd(Path.DirectorySeparatorChar);
        var config = Path.GetFullPath(configPath);
        if (!String.Equals(Path.GetDirectoryName(config), root, StringComparison.OrdinalIgnoreCase)) return;
        var strategyRoot = Path.Combine(root, "strategies", Path.GetFileNameWithoutExtension(config));
        // With no captured graph yet, inspect only this writer-owned directory after checking its parents below.
        var candidatePaths = capturedPaths ?? [config];
        var owned = candidatePaths.Select(Path.GetFullPath).Where(path =>
            path.Equals(config, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(strategyRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Refuse junctions at every existing parent before performing any deletion.
        foreach (var path in owned.Append(Path.Combine(strategyRoot, ".boundary-check")))
        for (var directory = Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Generated attempt cleanup cannot follow a directory link.");
            if (directory.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        if (capturedPaths is null && Directory.Exists(strategyRoot))
            owned = [.. owned, .. Directory.GetFiles(strategyRoot, "*", SearchOption.TopDirectoryOnly)];
        foreach (var path in owned) File.Delete(path);
        if (Directory.Exists(strategyRoot) && !Directory.EnumerateFileSystemEntries(strategyRoot).Any())
            Directory.Delete(strategyRoot);
    }

    /// <summary>Freezes selected portfolio, strategy-compatible execution assumptions and required market-data intervals.</summary>
    public string WriteBacktestConfig(BacktestRunRequest request)
    {
        var baseConfig = ResolveBacktestDataRoots(request.ResolvedProfile ?? yamlReader.ReadBacktestRun(request.BaseConfigPath));
        var selectedStrategies = request.StrategyPaths
            .Select(strategyArtifacts.RequireSourcePath)
            .Select(artifact => artifact.ResolvedStrategy)
            .ToArray();
        baseConfig = BacktestExecutionProfileResolver.Resolve(baseConfig, selectedStrategies, request.ExecutionReplayPolicyId);
        if (StrategyHorizonResolver.Resolve(selectedStrategies[0]) == "day" && request.ResolvedProfile is null)
            throw new InvalidOperationException("Day research requires the normal saved-plan workflow to resolve its authoritative preceding-session calendar before generating market-data requests.");
        if (request.ResolvedProfile is { } resolvedProfile)
            baseConfig = baseConfig with { TimeWindow = resolvedProfile.TimeWindow };
        ValidateBacktestSettings(request, baseConfig);
        var unsupportedStrategies = selectedStrategies
            .Where(strategy => !strategy.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase) &&
                StrategyHorizonResolver.Resolve(strategy) != "day")
            .Select(strategy => $"{strategy.StrategyId} ({strategy.Timeframe})")
            .ToArray();
        if (unsupportedStrategies.Length > 0)
        {
            throw new InvalidOperationException(
                $"Backtesting requires a supported day or daily-primary swing artifact. Unsupported: {String.Join(", ", unsupportedStrategies)}.");
        }
        var generatedConfigNeedsNews = baseConfig.News.Enabled || selectedStrategies.Any(StrategyCapabilityInspector.UsesNews);
        var runName = SanitizeRunName(request.RunName);
        var outputPath = Path.Combine(paths.GeneratedBacktestConfigsRoot, $"{runName}.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var yaml = new StringBuilder();
        yaml.AppendLine($"run_name: {runName}");
        yaml.AppendLine("mode: backtest");
        yaml.AppendLine();
        AppendEngine(yaml, request.EvidenceBoundEngine is { } evidenceEngine
            ? BacktestExecutionProfileResolver.Resolve(baseConfig with { Engine = evidenceEngine }, selectedStrategies, request.ExecutionReplayPolicyId).Engine
            : baseConfig.Engine);
        yaml.AppendLine();
        yaml.AppendLine("time_window:");
        yaml.AppendLine(request.TestWindowStartUtc is null ? "  type: rolling" : "  type: fixed");
        yaml.AppendLine($"  lookback_days: {request.LookbackDays}");
        yaml.AppendLine($"  warmup_lookback_days: {baseConfig.TimeWindow.WarmupLookbackDays}");
        yaml.AppendLine("  start: " + request.TestWindowStartUtc?.ToString("O", CultureInfo.InvariantCulture));
        yaml.AppendLine("  end: " + request.TestWindowEndUtc?.ToString("O", CultureInfo.InvariantCulture));
        yaml.AppendLine();
        yaml.AppendLine("universe:");
        yaml.AppendLine("  mode: resolved_snapshot");
        yaml.AppendLine($"  source: {RunYamlScalar(request.UniverseSource, "universe.source")}");
        if (request.UniverseSnapshotId is { } snapshotId) yaml.AppendLine($"  snapshot_id: {snapshotId}");
        yaml.AppendLine($"  wishlist_id: {request.WishlistId?.ToString() ?? String.Empty}");
        yaml.AppendLine($"  wishlist_name: {RunYamlScalar(request.WishlistName ?? String.Empty, "universe.wishlist_name")}");
        yaml.AppendLine("  resolved_at_utc: " + (request.UniverseResolvedAtUtc ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture));
        AppendDailyEligibility(yaml, request.DailyEligibility);
        AppendDailyRanking(yaml, request.DailyRanking);
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
        yaml.AppendLine($"  raw_root: {RunYamlScalar(NormalizePath(baseConfig.RawRoot), "market_data.raw_root")}");
        yaml.AppendLine($"  normalized_root: {RunYamlScalar(NormalizePath(baseConfig.NormalizedRoot), "market_data.normalized_root")}");
        yaml.AppendLine($"  results_root: {RunYamlScalar(NormalizePath(baseConfig.ResultsRoot), "market_data.results_root")}");
        yaml.AppendLine($"  cache_policy: {request.CachePolicy}");
        yaml.AppendLine();
        AppendProviders(yaml, baseConfig);
        yaml.AppendLine();
        AppendPortfolio(yaml, baseConfig.Portfolio with
        {
            StartingCapital = request.StartingCapital,
            AccountRiskBudgetPct = request.AccountRiskBudgetPct,
            MaxPositionNotionalPct = request.MaxPositionNotionalPct,
            MaxConcurrentPositions = request.MaxConcurrentPositions
        });
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
        yaml.AppendLine($"  order_expiration: {baseConfig.Execution.OrderExpiration}");
        yaml.AppendLine($"  entry_order_type: {baseConfig.Execution.EntryOrderType}");
        yaml.AppendLine($"  allow_extended_hours_trading: {baseConfig.Execution.AllowExtendedHoursTrading.ToString().ToLowerInvariant()}");
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
        try
        {
            foreach (var strategyPath in WriteStrategyConfigs(request, outputPath, runName))
            {
                var relative = Path.GetRelativePath(Path.GetDirectoryName(outputPath)!, strategyPath).Replace('\\', '/');
                yaml.AppendLine($"  - {relative}");
            }
            WriteExclusive(outputPath, yaml.ToString());
            return outputPath;
        }
        catch
        {
            DiscardUnacceptedBacktest(outputPath);
            throw;
        }
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
            var source = strategyArtifacts.RequireSourcePath(strategyPath);
            var definition = source.ResolvedStrategy;
            var fullPath = Path.GetFullPath(strategyPath);
            var effective = overridesByPath.TryGetValue(fullPath, out var strategyOverride)
                ? ApplyOverride(definition, strategyOverride)
                : definition;
            var semanticVersion = strategyOverride?.DerivedSemanticVersion;
            if (strategyOverride is not null && String.IsNullOrWhiteSpace(semanticVersion))
            {
                throw new InvalidOperationException(
                    $"Backtest parameter overrides for '{source.Identity.StrategyId}' require an explicit derived semantic version.");
            }

            semanticVersion ??= source.Identity.SemanticVersion;
            var runArtifact = strategyArtifacts.CreateDerivedArtifact(
                source,
                effective,
                semanticVersion);
            var outputStrategyPath = WriteStrategySnapshot(
                strategyRoot,
                runArtifact,
                source,
                StrategySelectionMode.Backtest,
                strategyOverride?.Actor ?? "web-backtest-operator",
                strategyOverride is null ? EmptyOverrideDiff : BuildOverrideDiff(strategyOverride));
            generatedPaths.Add(outputStrategyPath);
        }

        return generatedPaths;
    }

    private static TradingFlow.Domain.Strategies.StrategyDefinition ApplyOverride(
        TradingFlow.Domain.Strategies.StrategyDefinition definition,
        StrategyParameterOverride strategyOverride)
    {
        ValidateOverride(strategyOverride);
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

    private static void ValidateOverride(StrategyParameterOverride value)
    {
        if (value.MinVolumeSpike < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value.MinVolumeSpike), "Minimum volume spike cannot be negative.");
        }

        if (value.MinEntryRsi is < 0m or > 100m ||
            value.MaxEntryRsi is < 0m or > 100m ||
            value.MinEntryRsi > value.MaxEntryRsi)
        {
            throw new ArgumentOutOfRangeException(nameof(value.MinEntryRsi), "Entry RSI must be an ordered range between 0 and 100.");
        }

        if (value.MaxVwapExtensionAtr is <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value.MaxVwapExtensionAtr), "VWAP extension must be positive when configured.");
        }

        if (value.ConfluenceEnabled && value.ConfluenceEmaPeriod <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value.ConfluenceEmaPeriod), "Confluence EMA period must be positive.");
        }

        if ((value.ConfluenceEnabled && TimeframeParser.Parse(value.ConfluenceTimeframe) <= TimeSpan.Zero) ||
            TimeframeParser.Parse(value.ExecutionTimeframe) <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(value.ExecutionTimeframe), "Strategy timeframes must be positive.");
        }

        if (value.StopAtrMultiple <= 0m || value.TargetRMultiple <= 0m || value.MaxHoldHours <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value.StopAtrMultiple), "Stop, target, and maximum hold values must be positive.");
        }

        if (value.EnableAtrTrailingStop &&
            (value.TrailingStopAtrMultiple <= 0m || value.TrailingActivationR < 0m))
        {
            throw new ArgumentOutOfRangeException(nameof(value.TrailingStopAtrMultiple), "Enabled trailing-stop values must be valid and non-negative.");
        }

        if (value.MinHoldBarsBeforeTechnicalExit < 0 || value.SlippageBps < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value.MinHoldBarsBeforeTechnicalExit), "Hold bars and slippage cannot be negative.");
        }
    }

    /// <summary>Freezes an authorized paper strategy and source membership, applying the central account holding limit when supplied.</summary>
    public string SaveTempConfig(
        string baseConfigPath,
        IEnumerable<string> tickers,
        StrategyArtifact strategyArtifact,
        StrategySelectionMode selectionMode,
        string? orderExpiration = null,
        string? entryOrderType = null,
        bool allowExtendedHoursTrading = false,
        string? resolvedScreenerQuery = null,
        string? runName = null,
        bool? newsEnabled = null,
        Guid? wishlistId = null,
        string? wishlistName = null,
        string universeSource = "ephemeral",
        DateTimeOffset? universeResolvedAtUtc = null,
        IEnumerable<string>? selectedSourceTickers = null,
        IEnumerable<string>? resolvedScreenerTickers = null,
        Guid? universeSnapshotId = null,
        int? maxConcurrentPositions = null)
    {
        ArgumentNullException.ThrowIfNull(strategyArtifact);
        if (maxConcurrentPositions is <= 0)
            throw new InvalidOperationException("The central open-position limit must be positive.");
        var authorized = selectionMode switch
        {
            StrategySelectionMode.RunPaperExperiment =>
                strategyArtifact.EffectiveLifecycle == StrategyLifecycleState.PaperExperiment,
            StrategySelectionMode.RunPaperShadow =>
                strategyArtifact.EffectiveLifecycle is StrategyLifecycleState.PaperShadow or StrategyLifecycleState.Validated,
            _ => false
        };
        if (!authorized)
        {
            throw new InvalidOperationException(
                $"Paper execution mode '{selectionMode}' is not authorized for strategy lifecycle " +
                $"'{strategyArtifact.EffectiveLifecycle}'.");
        }

        ExtendedHoursOrderPolicy.ValidateConfiguration(
            entryOrderType,
            orderExpiration,
            allowExtendedHoursTrading);
        var rawYaml = File.ReadAllText(baseConfigPath);
        var baseConfig = yamlReader.ReadBacktestRun(baseConfigPath);
        var effectiveNewsEnabled = newsEnabled ?? baseConfig.News.Enabled;
        var tempName = SanitizeRunName(
            string.IsNullOrWhiteSpace(runName)
                ? $"temp-run-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"
                : runName);
        var outputPath = Path.Combine(paths.GeneratedPaperConfigsRoot, $"{tempName}.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var snapshotPath = WriteStrategySnapshot(
            Path.Combine(Path.GetDirectoryName(outputPath)!, "strategies", tempName),
            strategyArtifact,
            strategyArtifact,
            selectionMode,
            "web-paper-operator",
            EmptyOverrideDiff);

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
        var skipDiscoveryBlock = false;
        var wrotePositionLimit = false;

        foreach (var line in lines)
        {
            if (maxConcurrentPositions is { } limit && line.StartsWith("  max_concurrent_positions:", StringComparison.Ordinal))
            {
                newYaml.AppendLine($"  max_concurrent_positions: {limit}");
                wrotePositionLimit = true;
                continue;
            }
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

            if (skipDiscoveryBlock)
            {
                if (line.StartsWith("  ", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                skipDiscoveryBlock = false;
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

            if (line.Trim().Equals("mode: unresolved_wishlist", StringComparison.OrdinalIgnoreCase))
            {
                newYaml.AppendLine("  mode: resolved_snapshot");
                if (universeSnapshotId is { } snapshotId) newYaml.AppendLine($"  snapshot_id: {snapshotId}");
                newYaml.AppendLine($"  source: {universeSource}");
                if (wishlistId is not null)
                {
                    newYaml.AppendLine($"  wishlist_id: {wishlistId}");
                }

                if (!String.IsNullOrWhiteSpace(wishlistName))
                {
                    newYaml.AppendLine($"  wishlist_name: {wishlistName}");
                }

                AppendTickerList(newYaml, "selected_source_tickers", selectedSourceTickers);
                AppendTickerList(newYaml, "screener_tickers", resolvedScreenerTickers);

                if (!String.IsNullOrWhiteSpace(resolvedScreenerQuery))
                {
                    newYaml.AppendLine($"  resolved_screener_query: {QuoteYaml(resolvedScreenerQuery)}");
                }

                newYaml.AppendLine("  resolved_at_utc: " +
                    (universeResolvedAtUtc ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture));
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
                var relative = Path.GetRelativePath(Path.GetDirectoryName(outputPath)!, snapshotPath).Replace('\\', '/');
                newYaml.AppendLine($"  - {relative}");
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

            if (line.StartsWith("discovery:"))
            {
                skipDiscoveryBlock = true;
                continue;
            }

            newYaml.AppendLine(line);
        }

        newYaml.AppendLine();
        newYaml.AppendLine("screener:");
        // Operational discovery is resolved before admission. Keeping this
        // disabled prevents the live runner from changing the frozen universe.
        newYaml.AppendLine("  enabled: false");
        newYaml.AppendLine("  provider: finviz");
        newYaml.AppendLine("  filters: []");

        AppendDiscoveryRuntime(
            newYaml,
            wishlistId,
            universeSource,
            resolvedScreenerQuery,
            selectedSourceTickers ?? tickers,
            resolvedScreenerTickers ?? []);

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

        if (maxConcurrentPositions is not null && !wrotePositionLimit)
            throw new InvalidOperationException("The paper profile must declare portfolio.max_concurrent_positions before applying the common account limit.");
        WriteExclusive(outputPath, newYaml.ToString());
        return outputPath;
    }

    private static void AppendDiscoveryRuntime(
        StringBuilder yaml,
        Guid? wishlistId,
        string universeSource,
        string? finvizQuery,
        IEnumerable<string> selectedTickers,
        IEnumerable<string> finvizTickers)
    {
        var normalizedSource = (universeSource ?? String.Empty).Trim().ToLowerInvariant();
        var selected = selectedTickers
            .Where(value => !String.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        yaml.AppendLine();
        yaml.AppendLine("discovery:");
        yaml.AppendLine("  enabled: true");
        yaml.AppendLine($"  refresh_seconds: {TradingFlow.Domain.Backtesting.DiscoveryRuntimeConfig.DefaultRefreshSeconds}");
        yaml.AppendLine($"  source_expiry_seconds: {TradingFlow.Domain.Backtesting.DiscoveryRuntimeConfig.DefaultSourceExpirySeconds}");
        yaml.AppendLine($"  wishlist_id: {wishlistId?.ToString() ?? String.Empty}");
        AppendTickerList(yaml, "wishlist_tickers", normalizedSource.Contains("wishlist", StringComparison.Ordinal) ? selected : []);
        yaml.AppendLine($"  finviz_query: {QuoteYaml(finvizQuery ?? String.Empty)}");
        AppendTickerList(yaml, "finviz_tickers", finvizTickers);
        AppendTickerList(yaml, "operator_tickers", normalizedSource is "operator" or "ephemeral" ? selected : []);
        AppendTickerList(yaml, "alert_tickers", normalizedSource.Contains("alert", StringComparison.Ordinal) ? selected : []);
        AppendTickerList(yaml, "news_tickers", normalizedSource.Contains("news", StringComparison.Ordinal) ? selected : []);
        AppendTickerList(yaml, "earnings_tickers", normalizedSource.Contains("earnings", StringComparison.Ordinal) ? selected : []);
    }

    public StrategyArtifact RegisterPaperExperiment(
        string sourceStrategyPath,
        string semanticVersion,
        string actor,
        Guid operationId,
        DateTimeOffset requestedAtUtc,
        StrategyParameterOverride? strategyOverride = null)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Paper-experiment registration operation ID cannot be empty.", nameof(operationId));
        }

        if (requestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Paper-experiment registration time must be UTC.", nameof(requestedAtUtc));
        }

        var source = strategyArtifacts.RequireSourcePath(sourceStrategyPath);
        var resolved = strategyOverride is null
            ? source.ResolvedStrategy
            : ApplyOverride(source.ResolvedStrategy, strategyOverride);
        var overrideDiff = strategyOverride is null ? EmptyOverrideDiff : BuildOverrideDiff(strategyOverride);
        var artifact = experimentArtifacts.Register(
            source,
            resolved,
            semanticVersion,
            actor,
            overrideDiff,
            SerializeStrategyYaml(resolved));
        experimentAuthorizations.GrantPaperExperimentAsync(
                artifact.Identity,
                $"paper-experiment-grant-{operationId:N}",
                actor,
                requestedAtUtc,
                "Immutable paper-experiment artifact registered for operator execution.")
            .GetAwaiter()
            .GetResult();
        return artifact with { EffectiveLifecycle = StrategyLifecycleState.PaperExperiment };
    }

    private static IReadOnlyDictionary<string, string> EmptyOverrideDiff { get; } =
        new SortedDictionary<string, string>(StringComparer.Ordinal);

    private string WriteStrategySnapshot(
        string outputDirectory,
        StrategyArtifact artifact,
        StrategyArtifact source,
        StrategySelectionMode selectionMode,
        string actor,
        IReadOnlyDictionary<string, string> overrideDiff)
    {
        Directory.CreateDirectory(outputDirectory);
        var fileStem = SanitizeFileComponent(
            $"{artifact.Identity.StrategyId}-{artifact.Identity.SemanticVersion}-{artifact.Identity.ContentSha256[..12]}");
        var strategyPath = Path.Combine(outputDirectory, fileStem + ".yaml");
        WriteExclusive(strategyPath, SerializeStrategyYaml(artifact.ResolvedStrategy));
        var manifest = new StrategyRunArtifactManifest(
            1,
            artifact.Identity,
            selectionMode,
            source.Identity,
            source.SourcePath,
            artifact.AdmissionProfile,
            DateTimeOffset.UtcNow,
            String.IsNullOrWhiteSpace(actor) ? "unknown-operator" : actor.Trim(),
            overrideDiff);
        WriteExclusive(
            strategyPath + ".artifact.json",
            Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest)));
        return strategyPath;
    }

    private static IReadOnlyDictionary<string, string> BuildOverrideDiff(
        StrategyParameterOverride value) =>
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["entry_rules.min_volume_spike"] = value.MinVolumeSpike.ToString(CultureInfo.InvariantCulture),
            ["entry_rules.min_entry_rsi"] = value.MinEntryRsi.ToString(CultureInfo.InvariantCulture),
            ["entry_rules.max_entry_rsi"] = value.MaxEntryRsi.ToString(CultureInfo.InvariantCulture),
            ["entry_rules.max_vwap_extension_atr"] = value.MaxVwapExtensionAtr?.ToString(CultureInfo.InvariantCulture) ?? String.Empty,
            ["confluence.enabled"] = value.ConfluenceEnabled.ToString().ToLowerInvariant(),
            ["confluence.timeframe"] = value.ConfluenceTimeframe,
            ["confluence.ema_period"] = value.ConfluenceEmaPeriod.ToString(CultureInfo.InvariantCulture),
            ["exit_rules.stop_atr_multiple"] = value.StopAtrMultiple.ToString(CultureInfo.InvariantCulture),
            ["exit_rules.target_r_multiple"] = value.TargetRMultiple.ToString(CultureInfo.InvariantCulture),
            ["exit_rules.max_hold_hours"] = value.MaxHoldHours.ToString(CultureInfo.InvariantCulture),
            ["exit_rules.enable_atr_trailing_stop"] = value.EnableAtrTrailingStop.ToString().ToLowerInvariant(),
            ["exit_rules.trailing_stop_atr_multiple"] = value.TrailingStopAtrMultiple.ToString(CultureInfo.InvariantCulture),
            ["exit_rules.trailing_activation_r"] = value.TrailingActivationR.ToString(CultureInfo.InvariantCulture),
            ["exit_rules.min_hold_bars_before_technical_exit"] = value.MinHoldBarsBeforeTechnicalExit.ToString(CultureInfo.InvariantCulture),
            ["execution.timeframe"] = value.ExecutionTimeframe,
            ["execution.slippage_bps"] = value.SlippageBps.ToString(CultureInfo.InvariantCulture)
        };

    private static string SanitizeFileComponent(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray())
            .Replace('.', '-');
    }

    private static string QuoteYaml(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)}\"";

    /// <summary>
    /// Writes a saved plan's per-day eligibility rules under the resolved stock list, so each stock trades only on days it
    /// qualified from completed earlier sessions. Writes nothing when the plan has no rules; unset thresholds are omitted.
    /// </summary>
    private static void AppendDailyEligibility(StringBuilder yaml, TradingFlow.Domain.Market.DailyScreenRules? rules)
    {
        if (rules is null) return;
        rules.Validate();
        static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);
        yaml.AppendLine("  daily_eligibility:");
        yaml.AppendLine($"    min_price: {Text(rules.MinPrice)}");
        yaml.AppendLine($"    price_basis: {TradingFlow.Domain.Market.DailyScreenRules.PriceBasisName(rules.PriceBasis)}");
        yaml.AppendLine($"    average_sessions: {rules.AverageLookbackSessions.ToString(CultureInfo.InvariantCulture)}");
        if (rules.MinAverageShareVolume is { } shares) yaml.AppendLine($"    min_average_volume: {Text(shares)}");
        if (rules.MinAverageDollarVolume is { } dollars) yaml.AppendLine($"    min_average_dollar_volume: {Text(dollars)}");
        if (rules.AtrPeriod is { } period) yaml.AppendLine($"    atr_period: {period.ToString(CultureInfo.InvariantCulture)}");
        if (rules.MinAtr is { } atr) yaml.AppendLine($"    min_atr: {Text(atr)}");
        yaml.AppendLine($"    min_history_sessions: {rules.MinHistorySessions.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// Writes a saved plan's per-session ranking of the eligible stocks (stocks in play), every field explicit so the frozen
    /// plan never depends on a code default. Writes nothing when the plan does not rank.
    /// </summary>
    private static void AppendDailyRanking(StringBuilder yaml, TradingFlow.Domain.Market.DailyRankingRules? rules)
    {
        if (rules is null) return;
        rules.Validate();
        yaml.AppendLine("  daily_ranking:");
        yaml.AppendLine($"    measure: {rules.Measure}");
        yaml.AppendLine($"    baseline_sessions: {rules.BaselineSessions.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"    baseline_average: {TradingFlow.Domain.Market.DailyRankingRules.AverageName(rules.BaselineAverage)}");
        yaml.AppendLine($"    min_relative_volume: {rules.MinRelativeVolume.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"    max_per_session: {rules.MaxMembersPerSession.ToString(CultureInfo.InvariantCulture)}");
    }

    private static void AppendTickerList(StringBuilder yaml, string key, IEnumerable<string>? tickers)
    {
        var normalized = (tickers ?? [])
            .Where(ticker => !String.IsNullOrWhiteSpace(ticker))
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ticker => ticker, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        yaml.AppendLine($"  {key}:");
        foreach (var ticker in normalized)
        {
            yaml.AppendLine($"    - {ticker}");
        }
    }

    public void DeleteTempConfig(string configPath)
    {
        var fullPath = Path.GetFullPath(configPath);
        var tempRoot = Path.GetFullPath(paths.GeneratedPaperConfigsRoot);
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

    // Preserve all resolved rules in generated snapshots and reject incompatible continuation settings before writing.
    public static string SerializeStrategyYaml(TradingFlow.Domain.Strategies.StrategyDefinition strategy)
    {
        strategy.ExitRules.ContinuationProtection?.Validate(strategy.ExitRules);
        strategy.EntryRules.OpeningRangeBreakout?.Validate(strategy);
        var yaml = new StringBuilder();
        yaml.AppendLine($"strategy_id: {strategy.StrategyId}");
        yaml.AppendLine($"strategy_name: {strategy.StrategyName}");
        yaml.AppendLine($"source: {strategy.Source}");
        yaml.AppendLine($"version: {strategy.Version}");
        yaml.AppendLine($"timeframe: {strategy.Timeframe}");
        yaml.AppendLine($"direction: {strategy.Direction}");
        yaml.AppendLine("entry_rules:");
        yaml.AppendLine($"  setup_type: {strategy.EntryRules.SetupType}");
        if (strategy.EntryRules.OpeningRangeBreakout is { } openingRange)
        {
            yaml.AppendLine("  opening_range_breakout:");
            yaml.AppendLine($"    policy_id: {openingRange.PolicyId}");
            yaml.AppendLine($"    max_filled_parent_trades_per_session: {openingRange.MaxFilledParentTradesPerSession}");
            if (openingRange.RangeMinutes is { } rangeMinutes) yaml.AppendLine($"    range_minutes: {rangeMinutes}");
            if (openingRange.VolumeLookbackSessions is { } volumeSessions) yaml.AppendLine($"    volume_lookback_sessions: {volumeSessions}");
            AppendOptionalDecimal(yaml, "    min_relative_volume", openingRange.MinRelativeVolume);
            if (openingRange.AtrSessions is { } atrSessions) yaml.AppendLine($"    atr_sessions: {atrSessions}");
            // Written only when switched on, so snapshots of strategies without them keep their exact identity.
            if (openingRange.StopFromFill) yaml.AppendLine("    stop_from_fill: true");
            if (openingRange.ExitAtSessionClose) yaml.AppendLine("    exit_at_session_close: true");
        }
        if (strategy.EntryRules.DrawdownQualification is { } drawdown)
        {
            drawdown.Validate();
            yaml.AppendLine("  drawdown_qualification:");
            yaml.AppendLine($"    mode: {drawdown.Mode}");
            if (drawdown.Metric is not null) yaml.AppendLine($"    metric: {drawdown.Metric}");
            AppendOptionalDecimal(yaml, "    min_value", drawdown.MinValue);
            AppendOptionalDecimal(yaml, "    max_value", drawdown.MaxValue);
            yaml.AppendLine($"    require_sector_context: {drawdown.RequireSectorContext.ToString().ToLowerInvariant()}");
            yaml.AppendLine($"    require_event_context: {drawdown.RequireEventContext.ToString().ToLowerInvariant()}");
            yaml.AppendLine($"    minimum_matured_episodes: {drawdown.MinimumMaturedEpisodes}");
            if (drawdown.RejectAdverseEvents) yaml.AppendLine("    reject_adverse_events: true");
            if (drawdown.EventLookbackDays is { } eventDays) yaml.AppendLine($"    event_lookback_days: {eventDays}");
            if (drawdown.EventMaxCoverageAgeHours is { } eventAge) yaml.AppendLine($"    event_max_coverage_age_hours: {eventAge}");
        }
        if (strategy.EntryRules.TechnicalFactor is { } factor)
        {
            factor.Validate();
            yaml.AppendLine("  technical_factor:");
            yaml.AppendLine($"    metric: {factor.Metric}");
            yaml.AppendLine($"    lookback_bars: {factor.LookbackBars}");
            AppendOptionalDecimal(yaml, "    min_value", factor.MinValue);
            AppendOptionalDecimal(yaml, "    max_value", factor.MaxValue);
            AppendOptionalDecimal(yaml, "    max_context_value", factor.MaxContextValue);
            yaml.AppendLine($"    benchmark_symbol: {factor.BenchmarkSymbol}");
        }
        yaml.AppendLine($"  enable_short: {strategy.EntryRules.EnableShort.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  min_volume_spike: {strategy.EntryRules.MinVolumeSpike.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  min_volume_spike_source: {strategy.EntryRules.MinVolumeSpikeSource.ToConfigValue()}");
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
        AppendOptionalDecimal(yaml, "  min_close_location_value", strategy.EntryRules.MinCloseLocationValue);
        AppendOptionalDecimal(yaml, "  max_macd_histogram", strategy.EntryRules.MaxMacdHistogram);
        yaml.AppendLine($"  prior_entry_gain_lookback_bars: {strategy.EntryRules.PriorEntryGainLookbackBars}");
        AppendOptionalDecimal(yaml, "  max_prior_entry_gain_pct", strategy.EntryRules.MaxPriorEntryGainPct);
        yaml.AppendLine($"  reversion_stretch_lookback_bars: {strategy.EntryRules.ReversionStretchLookbackBars}");
        yaml.AppendLine($"  min_consecutive_down_closes_for_stretch: {strategy.EntryRules.MinConsecutiveDownClosesForStretch}");
        AppendOptionalDecimal(yaml, "  max_reversion_rsi2", strategy.EntryRules.MaxReversionRsi2);
        yaml.AppendLine($"  enable_lower_bollinger_stretch: {strategy.EntryRules.EnableLowerBollingerStretch.ToString().ToLowerInvariant()}");
        AppendOptionalDecimal(yaml, "  veto_fresh_news_hours", strategy.EntryRules.VetoFreshNewsHours);
        if (strategy.EntryRules.HistoricalNewsDiagnostic is { } diagnosticNews)
            yaml.AppendLine($"  historical_news_diagnostic: {diagnosticNews}");
        yaml.AppendLine($"  vcp_pivot_strength_bars: {strategy.EntryRules.VcpPivotStrengthBars}");
        yaml.AppendLine($"  vcp_maximum_depth_ratio_to_previous: {strategy.EntryRules.VcpMaximumDepthRatioToPrevious.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  vcp_minimum_low_rise_pct: {strategy.EntryRules.VcpMinimumLowRisePct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  vcp_maximum_contraction_to_advance_volume_ratio: {strategy.EntryRules.VcpMaximumContractionToAdvanceVolumeRatio.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  vcp_require_progressive_contraction_volume: {strategy.EntryRules.VcpRequireProgressiveContractionVolume.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  vcp_maximum_volume_ratio_to_previous_contraction: {strategy.EntryRules.VcpMaximumVolumeRatioToPreviousContraction.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  vcp_minimum_volume_reference_bars: {strategy.EntryRules.VcpMinimumVolumeReferenceBars}");
        yaml.AppendLine($"  vcp_breakout_buffer_pct: {strategy.EntryRules.VcpBreakoutBufferPct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  portfolio_rank_mode: {strategy.EntryRules.PortfolioRankMode}");
        yaml.AppendLine($"  sma200_rising_lookback_bars: {strategy.EntryRules.Sma200RisingLookbackBars}");
        AppendOptionalDecimal(yaml, "  min_breakout_volume_vs_50_day_average", strategy.EntryRules.MinBreakoutVolumeVs50DayAverage);
        AppendOptionalDecimal(yaml, "  min_close_below_sma_pct", strategy.EntryRules.MinCloseBelowSmaPct);
        if (strategy.EntryRules.CloseBelowSmaPeriod is { } closeBelowSmaPeriod) yaml.AppendLine($"  close_below_sma_period: {closeBelowSmaPeriod}");
        AppendOptionalDecimal(yaml, "  min_short_reversion_rsi2", strategy.EntryRules.MinShortReversionRsi2);
        if (strategy.EntryRules.RequirePriceBelowSma200ForShort) yaml.AppendLine("  require_price_below_sma200_for_short: true");
        AppendOptionalDecimal(yaml, "  min_close_above_sma_pct", strategy.EntryRules.MinCloseAboveSmaPct);
        if (strategy.EntryRules.CloseAboveSmaPeriod is { } closeAboveSmaPeriod) yaml.AppendLine($"  close_above_sma_period: {closeAboveSmaPeriod}");
        yaml.AppendLine($"  reject_weak_close_on_high_relative_volume: {strategy.EntryRules.RejectWeakCloseOnHighRelativeVolume.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  weak_close_max_location_value: {strategy.EntryRules.WeakCloseMaxLocationValue.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  weak_close_min_relative_volume: {strategy.EntryRules.WeakCloseMinRelativeVolume.ToString(CultureInfo.InvariantCulture)}");
        AppendOptionalDecimal(yaml, "  min_gap_up_pct", strategy.EntryRules.MinGapUpPct);
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
        AppendOptionalDecimal(yaml, "  min_price_vs_52_week_low_pct", strategy.EntryRules.MinPriceVs52WeekLowPct);
        AppendOptionalDecimal(yaml, "  max_price_vs_52_week_high_pct", strategy.EntryRules.MaxPriceVs52WeekHighPct);
        AppendOptionalInt(yaml, "  min_contractions", strategy.EntryRules.MinContractions);
        AppendOptionalInt(yaml, "  max_contractions", strategy.EntryRules.MaxContractions);
        yaml.AppendLine($"  require_volatility_halving_left_to_right: {strategy.EntryRules.RequireVolatilityHalvingLeftToRight.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  require_volume_dry_up_pre_breakout: {strategy.EntryRules.RequireVolumeDryUpPreBreakout.ToString().ToLowerInvariant()}");
        AppendOptionalDecimal(yaml, "  min_breakout_volume_ratio", strategy.EntryRules.MinBreakoutVolumeRatio);
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
        if (strategy.EntryRules.RequireOrderedReclaimPullback)
            yaml.AppendLine("  require_ordered_reclaim_pullback: true");
        // Preserve configured values even when another setup is currently selected.
        {
            yaml.AppendLine($"  step_prior_move_lookback_bars: {strategy.EntryRules.StepPriorMoveLookbackBars}");
            yaml.AppendLine($"  step_consolidation_min_bars: {strategy.EntryRules.StepConsolidationMinBars}");
            yaml.AppendLine($"  step_consolidation_max_bars: {strategy.EntryRules.StepConsolidationMaxBars}");
            yaml.AppendLine($"  min_step_prior_move_pct: {strategy.EntryRules.MinStepPriorMovePct.ToString(CultureInfo.InvariantCulture)}");
            yaml.AppendLine($"  min_step_prior_decline_pct: {strategy.EntryRules.MinStepPriorDeclinePct.ToString(CultureInfo.InvariantCulture)}");
            yaml.AppendLine($"  max_step_base_depth_pct: {strategy.EntryRules.MaxStepBaseDepthPct.ToString(CultureInfo.InvariantCulture)}");
            AppendOptionalDecimal(yaml, "  max_step_base_volume_ratio", strategy.EntryRules.MaxStepBaseVolumeRatio);
            AppendOptionalDecimal(yaml, "  min_step_breakout_volume_ratio", strategy.EntryRules.MinStepBreakoutVolumeRatio);
            yaml.AppendLine($"  require_step_higher_lows: {strategy.EntryRules.RequireStepHigherLows.ToString().ToLowerInvariant()}");
            yaml.AppendLine($"  require_step_lower_highs_for_short: {strategy.EntryRules.RequireStepLowerHighsForShort.ToString().ToLowerInvariant()}");
            yaml.AppendLine($"  require_step_bollinger_contraction: {strategy.EntryRules.RequireStepBollingerContraction.ToString().ToLowerInvariant()}");
            yaml.AppendLine($"  step_bollinger_width_ratio_max: {strategy.EntryRules.StepBollingerWidthRatioMax.ToString(CultureInfo.InvariantCulture)}");
        }
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
        if (strategy.ExitRules.ContinuationProtection is { } continuation)
        {
            yaml.AppendLine("  continuation_protection:");
            yaml.AppendLine($"    activation_r: {continuation.ActivationR.ToString(CultureInfo.InvariantCulture)}");
            yaml.AppendLine($"    cost_buffer_ticks: {continuation.CostBufferTicks.ToString(CultureInfo.InvariantCulture)}");
            yaml.AppendLine($"    confirmation_bars: {continuation.ConfirmationBars.ToString(CultureInfo.InvariantCulture)}");
            yaml.AppendLine($"    target_step_r: {continuation.TargetStepR.ToString(CultureInfo.InvariantCulture)}");
        }
        yaml.AppendLine($"  stop_atr_multiple: {strategy.ExitRules.StopAtrMultiple.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  target_r_multiple: {strategy.ExitRules.TargetRMultiple.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  max_hold_hours: {strategy.ExitRules.MaxHoldHours.ToString(CultureInfo.InvariantCulture)}");
        AppendOptionalInt(yaml, "  max_hold_bars", strategy.ExitRules.MaxHoldBars);
        AppendOptionalDecimal(yaml, "  exit_on_rsi2_above", strategy.ExitRules.ExitOnRsi2Above);
        yaml.AppendLine($"  exit_on_close_above_sma5: {strategy.ExitRules.ExitOnCloseAboveSma5.ToString().ToLowerInvariant()}");
        if (strategy.ExitRules.ExitOnCloseBelowSma5) yaml.AppendLine("  exit_on_close_below_sma5: true");
        if (strategy.ExitRules.CheckExitsFromFillDay) yaml.AppendLine("  check_exits_from_fill_day: true");
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
        yaml.AppendLine($"  stop_tick_buffer: {strategy.ExitRules.StopTickBuffer.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  require_confirmed_ema20_exit: {strategy.ExitRules.RequireConfirmedEma20Exit.ToString().ToLowerInvariant()}");
        yaml.AppendLine("execution:");
        yaml.AppendLine($"  timeframe: {strategy.Execution.Timeframe}");
        yaml.AppendLine($"  slippage_bps: {strategy.Execution.SlippageBps.ToString(CultureInfo.InvariantCulture)}");
        if (strategy.Execution.OpeningGapPolicy is { } openingGapPolicy) yaml.AppendLine($"  opening_gap_policy: {openingGapPolicy}");
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

    /// <summary>Reject unusable requests before creating immutable run and strategy files.</summary>
    private static void ValidateBacktestSettings(BacktestRunRequest request, BacktestRunConfig profile)
    {
        if (request.StartingCapital <= 0 || request.AccountRiskBudgetPct is <= 0 or > 100 ||
            request.MaxPositionNotionalPct is <= 0 or > 100 || request.MaxConcurrentPositions <= 0)
            throw new InvalidOperationException("Backtest capital, risk budget, position size and open-position limit must be positive; percentages cannot exceed 100.");
        if (request.CachePolicy is not ("use_cache" or "refresh"))
            throw new InvalidOperationException("Backtest cache policy must be use_cache or refresh.");
        if ((request.TestWindowStartUtc is null) != (request.TestWindowEndUtc is null) ||
            request.TestWindowStartUtc >= request.TestWindowEndUtc ||
            (request.TestWindowStartUtc is null && request.LookbackDays <= 0))
            throw new InvalidOperationException("Choose a positive rolling lookback or a fixed start and later end date.");
        if (request.StrategyPaths.Count == 0 || request.Tickers.Count == 0)
            throw new InvalidOperationException("Select at least one strategy and one stock for the backtest.");
        if (profile.Engine.ExecutionReplayPolicyId is not (null or ExecutionReplayPolicy.DailyRthFiveMinuteV1 or ExecutionReplayPolicy.DailyRthSipPortfolioV1 or ExecutionReplayPolicy.IntradayRthFiveMinuteV1 or ExecutionReplayPolicy.IntradayRthFiveMinuteStopMarketV1 or ExecutionReplayPolicy.IntradayRthFiveMinuteNextBarMarketV1))
            throw new InvalidOperationException($"Unsupported execution replay policy: {profile.Engine.ExecutionReplayPolicyId}.");
        if (profile.Engine.MaxRetainedReplayBars <= 0 || profile.Engine.MaxStrategyWorkItems <= 0 ||
            profile.Engine.MaxRetainedCandidates <= 0 || profile.Portfolio.MaxExecutionOrderHistory <= 0)
            throw new InvalidOperationException("Replay bars, strategy work, retained candidates and execution order history must each have a positive limit.");

        foreach (var ticker in request.Tickers)
        {
            if (String.IsNullOrWhiteSpace(ticker) || ticker.Trim().Any(character =>
                    !Char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-')))
                throw new InvalidOperationException($"Invalid US-equity stock symbol: {ticker}.");
        }
        _ = RunYamlScalar(request.UniverseSource, "universe.source");
        _ = RunYamlScalar(request.WishlistName ?? String.Empty, "universe.wishlist_name");
    }

    /// <summary>Write all typed engine settings so a generated run uses the selected profile's limits and replay policy.</summary>
    private static void AppendEngine(StringBuilder yaml, EngineConfig engine)
    {
        yaml.AppendLine("engine:");
        yaml.AppendLine($"  pipeline: {RunYamlScalar(engine.Pipeline, "engine.pipeline")}");
        yaml.AppendLine($"  worker_count: {engine.WorkerCount}");
        yaml.AppendLine($"  bounded_capacity: {engine.BoundedCapacity}");
        yaml.AppendLine($"  indicator_warmup_bars: {engine.IndicatorWarmupBars}");
        yaml.AppendLine($"  ticker_timeout_seconds: {engine.TickerTimeoutSeconds}");
        yaml.AppendLine($"  fail_fast: {engine.FailFast.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  max_retained_replay_bars: {engine.MaxRetainedReplayBars}");
        yaml.AppendLine($"  max_strategy_work_items: {engine.MaxStrategyWorkItems}");
        yaml.AppendLine($"  max_retained_candidates: {engine.MaxRetainedCandidates}");
        yaml.AppendLine($"  allow_late_warmup: {engine.AllowLateWarmup.ToString().ToLowerInvariant()}");
        if (engine.PrecommittedLiquidationSourceUtc is { } liquidation)
            yaml.AppendLine($"  precommitted_liquidation_source_utc: {liquidation.ToString("O", CultureInfo.InvariantCulture)}");
        if (engine.ExecutionReplayPolicyId is { } policy)
            yaml.AppendLine($"  execution_replay_policy_id: {RunYamlScalar(policy, "engine.execution_replay_policy_id")}");
        if (engine.ExecutionSourceManifestPath is { } executionSource)
        {
            if (!Path.IsPathFullyQualified(executionSource))
                throw new InvalidOperationException("The execution source manifest must have an explicit absolute path.");
            yaml.AppendLine($"  execution_source_manifest_path: {RunYamlScalar(NormalizePath(executionSource), "engine.execution_source_manifest_path")}");
        }
        if (engine.ExecutionSourceManifestSha256 is { } executionHash)
            yaml.AppendLine($"  execution_source_manifest_sha256: {RunYamlScalar(executionHash, "engine.execution_source_manifest_sha256")}");
        if (engine.EvidenceManifestPath is { } manifest)
        {
            var fullPath = Path.IsPathFullyQualified(manifest) ? manifest : Path.Combine(engine.EvidenceResearchRoot
                ?? throw new InvalidDataException("Evidence manifest requires an explicit research root."), manifest);
            yaml.AppendLine($"  evidence_manifest_path: {RunYamlScalar(NormalizePath(fullPath), "engine.evidence_manifest_path")}");
        }
        if (engine.EvidenceResearchRoot is { } root)
            yaml.AppendLine($"  evidence_research_root: {RunYamlScalar(NormalizePath(root), "engine.evidence_research_root")}");
        if (engine.EvidenceManifestSha256 is { } evidenceHash)
            yaml.AppendLine($"  evidence_manifest_sha256: {RunYamlScalar(evidenceHash, "engine.evidence_manifest_sha256")}");
        if (engine.EvidenceDependencyGraphSha256 is { } graphHash)
            yaml.AppendLine($"  evidence_dependency_graph_sha256: {RunYamlScalar(graphHash, "engine.evidence_dependency_graph_sha256")}");
    }

    /// <summary>Retain execution fees, participation and history limits alongside the operator's portfolio choices.</summary>
    private static void AppendPortfolio(StringBuilder yaml, PortfolioConfig portfolio)
    {
        yaml.AppendLine("portfolio:");
        yaml.AppendLine($"  starting_capital: {portfolio.StartingCapital.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  account_risk_budget_pct: {portfolio.AccountRiskBudgetPct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  max_position_notional_pct: {portfolio.MaxPositionNotionalPct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  max_concurrent_positions: {portfolio.MaxConcurrentPositions}");
        yaml.AppendLine($"  fixed_buy_fee: {portfolio.FixedBuyFee.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  fixed_sell_fee: {portfolio.FixedSellFee.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  max_open_trades_per_ticker: {portfolio.MaxOpenTradesPerTicker}");
        yaml.AppendLine($"  prevent_overlapping_ticker_positions: {portfolio.PreventOverlappingTickerPositions.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"  max_bar_participation_pct: {portfolio.MaxBarParticipationPct.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  sec_fee_rate: {portfolio.SecFeeRate.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  finra_taf_per_share: {portfolio.FinraTafPerShare.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  finra_taf_cap: {portfolio.FinraTafCap.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  impact_bps_at_max_participation: {portfolio.ImpactBpsAtMaxParticipation.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"  max_execution_order_history: {portfolio.MaxExecutionOrderHistory}");
        // Opt-in published-method costs and leverage, written only when the profile sets them so other runs are unchanged.
        if (portfolio.CommissionPerShare != 0m)
            yaml.AppendLine($"  commission_per_share: {portfolio.CommissionPerShare.ToString(CultureInfo.InvariantCulture)}");
        if (portfolio.MaxGrossExposurePct is { } gross)
            yaml.AppendLine($"  max_gross_exposure_pct: {gross.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>Quote a scalar the current reader can round-trip; reject characters it cannot preserve instead of changing them.</summary>
    private static string RunYamlScalar(string value, string setting)
    {
        if (value.Any(Char.IsControl) || value.Contains('#') || value.Contains('"') || value.StartsWith('\'') || value.EndsWith('\''))
            throw new InvalidDataException($"{setting} contains characters unsupported by the run configuration reader.");
        return $"\"{value}\"";
    }

    private static void AppendProviders(StringBuilder yaml, TradingFlow.Domain.Backtesting.BacktestRunConfig baseConfig)
    {
        yaml.AppendLine("providers:");
        yaml.AppendLine("  alpaca:");
        yaml.AppendLine($"    data_feed: {baseConfig.Providers.Alpaca.DataFeed}");
    }

    /// <summary>Retain the profile's validation assumptions, including its dated universe evidence.</summary>
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
        yaml.AppendLine("    universe_as_of_date: " + baseConfig.Validation.BiasRisk.UniverseAsOfDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        yaml.AppendLine($"    price_adjustment_policy: {baseConfig.Validation.BiasRisk.PriceAdjustmentPolicy}");
    }

    /// <summary>Preserves profile intervals while adding finer strategy intervals that cannot be derived from the configured source.</summary>
    internal static IReadOnlyList<string> ResolveDownloadTimeframes(
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

    private static string SanitizeRunName(string runName)
    {
        var cleaned = new string(runName
            .Trim()
            .ToLowerInvariant()
            .Select(ch => Char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());
        cleaned = String.Join("-", cleaned.Split('-', StringSplitOptions.RemoveEmptyEntries));
        var prefix = String.IsNullOrWhiteSpace(cleaned) ? "ui-run" : cleaned;
        return $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
    }

    private void WriteExclusive(string path, string content) =>
        artifactWriter.WriteTextExclusiveAsync(path, content).GetAwaiter().GetResult();

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).Replace('\\', '/');
    }
}
