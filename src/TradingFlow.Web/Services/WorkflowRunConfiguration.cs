using TradingFlow.Application.Jobs;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Models;
using System.Security.Cryptography;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;
using TradingFlow.Engine.Storage;
using TradingFlow.Data.Csv;

namespace TradingFlow.Web.Services;

public sealed class WorkflowRunConfiguration(ConfigCatalogService catalog, RunConfigWriter writer, SimpleYamlReader reader,
    TradingFlow.Domain.Execution.IOperationalTradingLimitStore tradingLimits,
    TradingFlow.Backtesting.ConfiguredDataProviders? dataProviders = null) : IWorkflowRunConfiguration
{
    /// <summary>Freezes the selected strategy/profile policy in the normal command so retries and saved plans retain it.</summary>
    public async Task<StartBacktestRequest> NormalizeBacktestRequestAsync(StartBacktestRequest request, CancellationToken token)
    {
        var config = SelectConfig(catalog.GetBacktestConfigs(), request.ConfigurationKey);
        var options = await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest, token);
        var selected = request.Strategies.Select(identity => SelectStrategy(options, identity)).ToArray();
        var resolved = await ResolveEffectiveProfileAsync(config.Config, selected, request, token);
        return request with { ExecutionReplayPolicyId = resolved.Engine.ExecutionReplayPolicyId };
    }

    /// <summary>Opens one attempt's server scope and releases it if cancellation arrives during the complete replay.</summary>
    public Task<IWorkflowEvidenceVerificationScope?> OpenEvidenceVerificationScopeAsync(StartBacktestRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var scope = catalog.OpenEvidenceVerificationScope(request);
        try
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(scope);
        }
        catch
        {
            scope?.Dispose();
            throw;
        }
    }

    /// <summary>Reads costs and registered strategy policies under the selected profile lease for immutable plan review.</summary>
    public async Task<BacktestResolvedSettingsResponse?> GetBacktestResolvedSettingsAsync(StartBacktestRequest request, CancellationToken token)
    {
        var config = SelectConfig(catalog.GetBacktestConfigs(), request.ConfigurationKey);
        await using var lease = new FileStream(config.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(lease, token));
        var run = reader.ReadBacktestRun(config.Path);
        var options = await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest, token);
        var selected = request.Strategies.Select(identity => SelectStrategy(options, identity)).ToArray();
        run = await ResolveEffectiveProfileAsync(run, selected, request, token);
        var portfolio = run.Portfolio;
        return new(config.FileName, hash, portfolio.FixedBuyFee, portfolio.FixedSellFee, portfolio.SecFeeRate,
            portfolio.FinraTafPerShare, portfolio.FinraTafCap, portfolio.MaxBarParticipationPct,
            portfolio.ImpactBpsAtMaxParticipation, run.Engine.ExecutionReplayPolicyId, run.Engine.PrecommittedLiquidationSourceUtc,
            selected.Select(item => new BacktestStrategySettingsResponse(
                new(item.Identity.StrategyId, item.Identity.SemanticVersion, item.Identity.ContentSha256),
                item.Definition.StrategyName, item.Definition.Execution.SlippageBps,
                item.Artifact.DerivedFromIdentity is { } parent ? new(parent.StrategyId, parent.SemanticVersion, parent.ContentSha256) : null,
                new(item.Artifact.AdmissionProfile.ProfileId, item.Artifact.AdmissionProfile.ProfileVersion,
                    item.Artifact.AdmissionProfile.ExecutionTimingPolicy, item.Artifact.AdmissionProfile.RiskPolicy))).ToArray(),
            string.Join(Environment.NewLine, selected.Select(item =>
                $"{item.Definition.StrategyName}: {ExecutionAssumptionNote(run, item.Definition)}")),
            StrategyHorizonResolver.Resolve(selected[0].Definition),
            SourceHistoryNote(run, selected[0].Definition));
    }

    /// <summary>Describes the selected server execution contract for each strategy horizon.</summary>
    private static string ExecutionAssumptionNote(BacktestRunConfig run, StrategyDefinition strategy)
    {
        if (strategy.EntryRules.OpeningRangeBreakout is { IsPublishedRecipe: true } recipe)
            return $"Published opening-range recipe (Zarattini, Barbon and Aziz) on the run's own stock list: the first {recipe.RangeMinutes} minutes set the range and direction (no order on a doji); first-interval volume must be at least {recipe.MinRelativeVolume} times its {recipe.VolumeLookbackSessions}-session mean. A stop order at the range high or low stays live until five minutes before the close. The protective stop is {strategy.ExitRules.StopAtrMultiple} times the prior {recipe.AtrSessions}-session daily ATR beyond the trigger; no target; exit five minutes before the close. One filled entry per stock per session. No daily top-20 stock selection: a rules check, not a replication. Five-minute OHLC fills; short borrow is diagnostic.";
        if (strategy.EntryRules.OpeningRangeBreakout is { PolicyId: OpeningRangeBreakoutRules.CompletedBreakoutPolicyId } completed)
            return $"Opening-range diagnostic: first fifteen minutes freeze body direction and twenty-session median RVOL > 1. A completed post-range five-minute close strictly beyond the valid trigger commits a next-bar-opening market entry (earliest 09:50 New York). Stop is that completed candle low/high, rounded outward without an extra buffer; size uses completed close, actual fills determine 3R. Filled parent trade limit per stock per session: {completed.MaxFilledParentTradesPerSession}, with a fresh completed inside-range return after resolution before another breakout. Partial market remainder persists until fill, protection or session deadline. Lagged completed volume supplies opening capacity; gaps including through the stop are filled and reduced through shared risk protection. Existing continuation protection, adverse OHLC ordering, costs, caps and session liquidation remain diagnostic.";
        if (strategy.EntryRules.OpeningRangeBreakout is { } openingRange)
            return $"Opening-range diagnostic: direction and relative volume freeze after the first fifteen minutes; stop-market entry remains active until the session deadline. Filled parent trade limit per stock per session: {openingRange.MaxFilledParentTradesPerSession}, with a completed return inside the frozen range before re-entry. Triggered entry remainder remains a market order. Liquidation is scheduled five minutes before the authoritative close. Missing executable evidence leaves unresolved exposure; five-minute OHLC uses participation and adverse ordering assumptions; short borrow is diagnostic.";
        return run.Engine.PrecommittedLiquidationSourceUtc is { } liquidation
            ? $"Liquidation is precommitted at source time {liquidation:O}; ordinary fills still require executable market evidence."
            : "Positions remain open at the exclusive cutoff; no forced terminal exit.";
    }

    /// <summary>Shows the calendar-derived source requirement that was frozen through ordinary plan review.</summary>
    private static string? SourceHistoryNote(BacktestRunConfig run, StrategyDefinition strategy)
    {
        if (StrategyHorizonResolver.Resolve(strategy) != "day") return null;
        const string context = "the immediately preceding twenty authoritative exchange sessions for the opening-volume median (twenty valid observations required), plus previous-session context";
        return $"Native five-minute regular-session candles, including {context}; calendar-derived warmup {run.TimeWindow.WarmupLookbackDays} days. No inherited daily indicator history.";
    }

    /// <summary>Extracts reviewed server evidence facts with the exact selected recipe's lookback settings.</summary>
    public async Task<DecisionContextEvidenceSummary?> GetDecisionContextEvidenceAsync(StartBacktestRequest request, CancellationToken token,
        IWorkflowEvidenceVerificationScope? evidenceScope = null)
    {
        var config = SelectConfig(catalog.GetBacktestConfigs(), request.ConfigurationKey);
        var options = await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest, token);
        var selected = request.Strategies.Select(identity => SelectStrategy(options, identity)).ToArray();
        var run = await ResolveEffectiveProfileAsync(config.Config, selected, request, token);
        return catalog.BindDecisionContextEvidence(request, run.Engine, selected, RequireScope(evidenceScope)).Summary;
    }

    /// <summary>Checks context hashes under the durable lease, closing the gap between writer validation and enqueue.</summary>
    public Task ValidateCapturedBacktestInputsAsync(string configPath, IReadOnlyList<string> files, CancellationToken token,
        IWorkflowEvidenceVerificationScope? evidenceScope = null)
    {
        token.ThrowIfCancellationRequested();
        var required = ResolveInputs(configPath, reader.ReadBacktestRun(configPath), RequireScope(evidenceScope));
        if (required.Except(files, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("Generated backtest references an uncaptured dependency.");
        return Task.CompletedTask;
    }
    /// <summary>Embeds effective strategy definitions in one comparison-owned request, independent of attempt-specific config paths.</summary>
    public async Task<PreparedMarketDataRequestReference?> WritePreparedComparisonRequestAsync(
        string comparisonId, IReadOnlyList<PersistedJob> books, DateTimeOffset cutoffUtc, CancellationToken token)
    {
        if (books.Count == 0 || comparisonId.Length > 100 || comparisonId.Any(character => !Char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new InvalidOperationException("A prepared comparison needs a safe server identity and at least one book.");
        var inputs = new List<PreparedMarketDataBook>(books.Count);
        foreach (var book in books)
        {
            using var lease = DurableFileJobRequest.Deserialize(book.RequestJson).AcquireVerifiedReadLease();
            var run = reader.ReadBacktestRun(book.ConfigPath);
            var definitions = run.Strategies.Select(reader.ReadStrategy).ToArray();
            inputs.Add(new(book.Id.ToString("N"), run with { RunName = book.RunName, Strategies = [] }, definitions));
        }
        var request = new PreparedMarketDataRequest(1, comparisonId, cutoffUtc, inputs);
        // Ordinary local CSV files keep their existing declared provenance and receive no verified historical snapshot claim.
        if (inputs.All(book => book.Run.Provider.Equals("csv", StringComparison.OrdinalIgnoreCase))) return null;
        if (inputs.Any(book => book.Run.Provider.Equals("csv", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A comparison cannot mix local CSV inputs with prepared provider history.");
        var service = new TradingFlow.Backtesting.PreparedMarketDataService(dataProviders ?? TradingFlow.Backtesting.ConfiguredDataProviders.FromEnvironment());
        service.ValidateRequest(request);
        var parent = Path.GetDirectoryName(Path.GetFullPath(books[0].ConfigPath))!;
        var attempt = Path.GetFileNameWithoutExtension(books[0].ConfigPath);
        var path = Path.Combine(parent, "prepared-comparisons", comparisonId, attempt, "request.json");
        return await TradingFlow.Backtesting.PreparedMarketDataService.WriteRequestAsync(request, path, token);
    }

    /// <summary>Captures the chosen profile's content hash without publishing private profile contents.</summary>
    public async Task<string> GetBacktestConfigurationHashAsync(string? configurationKey, CancellationToken token)
    {
        var config = SelectConfig(catalog.GetBacktestConfigs(), configurationKey);
        await using var stream = new FileStream(config.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }

    /// <summary>Resolve exact catalog choices and freeze the backtest configuration plus its strategy evidence.</summary>
    public async Task<IReadOnlyList<string>> WriteAsync(StartBacktestRequest request, UniversePreviewResponse universe, string uniqueName, CancellationToken token,
        IWorkflowEvidenceVerificationScope? evidenceScope = null)
    {
        var config = SelectConfig(catalog.GetBacktestConfigs(), request.ConfigurationKey);
        // Keep the profile read-only during generation so the checked settings cannot change between hash and parsing.
        await using var profileLease = new FileStream(config.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(profileLease, token));
        if (request.ConfigurationSha256 is { } expectedHash && !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The backtest profile changed after this plan was saved. Review the new settings and save a new plan before starting.");
        var options = await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest, token);
        var selected = request.Strategies.Select(identity => SelectStrategy(options, identity)).ToArray();
        var run = await ResolveEffectiveProfileAsync(reader.ReadBacktestRun(config.Path), selected, request, token);
        if (universe.Horizon != StrategyHorizonResolver.Resolve(selected[0].Definition))
            throw new InvalidOperationException("The source preview horizon differs from the selected strategy. Preview the stock source again for this strategy.");
        var scope = RequireScope(evidenceScope);
        var evidence = catalog.BindDecisionContextEvidence(request, run.Engine, selected, scope);
        var path = writer.WriteBacktestConfig(new BacktestRunRequest(config.Path, uniqueName, 0, null, null,
            universe.Members.Select(item => item.Symbol).ToArray(), selected.Select(item => item.Path).ToArray(),
            request.StartingCapital, request.AccountRiskBudgetPct, request.MaxPositionNotionalPct, request.MaxConcurrentPositions,
            request.CachePolicy, [], request.TestWindowStartUtc, request.TestWindowEndUtc, universe.UniverseSnapshotId, universe.ResolvedAtUtc,
            String.Join("+", universe.Members.SelectMany(item => item.Sources).Select(item => item.SourceKind).Distinct().Order()),
            evidence.Engine, run.Engine.ExecutionReplayPolicyId, run));
        try
        {
            var generated = reader.ReadBacktestRun(path);
            return ResolveInputs(path, generated, scope).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            writer.DiscardUnacceptedBacktest(path);
            throw;
        }
    }

    /// <summary>Uses the generated run's runtime data roots to reuse authoritative calendar coverage before collecting missing preceding-session context.</summary>
    private async Task<BacktestRunConfig> ResolveEffectiveProfileAsync(BacktestRunConfig profile,
        IReadOnlyList<StrategyOption> selected, StartBacktestRequest request, CancellationToken token)
    {
        var run = BacktestExecutionProfileResolver.Resolve(profile, selected.Select(item => item.Definition).ToArray(), request.ExecutionReplayPolicyId);
        if (StrategyHorizonResolver.Resolve(selected[0].Definition) != "day") return run;
        run = writer.ResolveBacktestDataRoots(run);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(selected[0].Definition.Session.ExchangeTimezone);
        var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.TestWindowStartUtc, zone).DateTime);
        var provider = (dataProviders ?? TradingFlow.Backtesting.ConfiguredDataProviders.FromEnvironment()).CreateMarket(run,
            new FileSystemRawArchiveWriter(new RawArchiveOptions(run.RawRoot)), run.NormalizedRoot);
        try
        {
            // This is a bounded session-calendar read; no candles or news are requested during plan review.
            var calendar = new CachedMarketDataProvider(provider, run.NormalizedRoot, "use_cache");
            var requiredSessions = selected.Any(item => item.Definition.EntryRules.OpeningRangeBreakout is not null) ? 20 : 1;
            var schedules = await calendar.LoadSessionContextCalendarAsync(firstDate, token, requiredSessions);
            return BacktestSessionContextResolver.IncludeCompletedSessions(run, request.TestWindowStartUtc, schedules, zone, requiredSessions);
        }
        finally
        {
            if (provider is IDisposable disposable) disposable.Dispose();
        }
    }

    /// <summary>Freeze an authorized paper strategy and the selected universe without changing the configured portfolio limits.</summary>
    public async Task<IReadOnlyList<string>> WriteAsync(StartPaperRunRequest request, UniversePreviewResponse universe, string uniqueName, CancellationToken token)
    {
        var limits = await tradingLimits.GetAsync(request.Mode, token);
        var config = SelectConfig(catalog.GetPaperConfigs(), request.ConfigurationKey);
        var mode = request.Mode == "paper_experiment" ? StrategySelectionMode.RunPaperExperiment : StrategySelectionMode.RunPaperShadow;
        var selected = SelectStrategy(await catalog.GetStrategiesAsync(mode, token), request.Strategy);
        var wishlist = universe.Members.Where(item => item.Sources.Any(source => source.SourceKind == "wishlist")).Select(item => item.Symbol).ToArray();
        var screener = universe.Members.Where(item => item.Sources.Any(source => source.SourceKind is "screener" or "finviz")).Select(item => item.Symbol).ToArray();
        var path = writer.SaveTempConfig(config.Path, universe.Members.Select(item => item.Symbol), selected.Artifact, mode,
            request.TimeInForce, request.EntryOrderType, request.AllowExtendedHoursTrading, null, uniqueName, request.NewsEnabled,
            universeSource: screener.Length > 0 ? (wishlist.Length > 0 ? "wishlist+finviz" : "screener") : "wishlist",
            universeResolvedAtUtc: universe.ResolvedAtUtc, selectedSourceTickers: wishlist, resolvedScreenerTickers: screener,
            universeSnapshotId: universe.UniverseSnapshotId, maxConcurrentPositions: limits.MaxOpenPositions);
        return [path, .. reader.ReadBacktestRun(path).Strategies];
    }

    /// <summary>Delegates rejected-attempt cleanup to the writer that owns generated paths, never to arbitrary input deletion.</summary>
    public Task DiscardUnacceptedAsync(string configPath, IReadOnlyList<string> capturedPaths, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        writer.DiscardUnacceptedBacktest(configPath, capturedPaths);
        return Task.CompletedTask;
    }

    /// <summary>Rejects fabricated or foreign application tokens before any reuse or generated-file write.</summary>
    private WorkflowEvidenceVerificationScope? RequireScope(IWorkflowEvidenceVerificationScope? token)
    {
        if (token is null) return null;
        if (token is not WorkflowEvidenceVerificationScope scope)
            throw new InvalidOperationException("Evidence verification token was not created by the server scope factory.");
        scope.RequireOwner(catalog);
        return scope;
    }

    /// <summary>Every generated or captured input resolution keeps the owner's live guard and exact graph checks.</summary>
    private static IReadOnlyList<string> ResolveInputs(string path, BacktestRunConfig run, WorkflowEvidenceVerificationScope? scope) =>
        scope is null ? TradingFlow.Backtesting.BacktestRunInputFiles.Resolve(path, run)
        : TradingFlow.Backtesting.BacktestRunInputFiles.Resolve(path, run, scope.Resolve(run.Engine), scope.Guard);

    /// <summary>Require an explicit server profile so missing input cannot silently choose a different mode's settings.</summary>
    private static RunConfigSummary SelectConfig(IReadOnlyList<RunConfigSummary> configs, string? key) =>
        (!String.IsNullOrWhiteSpace(key) ? configs.SingleOrDefault(item => item.FileName == key) : null)
        ?? throw new InvalidOperationException("Select a server catalog configuration explicitly.");
    /// <summary>Requires the complete strategy identity rather than resolving a mutable display alias.</summary>
    private static StrategyOption SelectStrategy(IReadOnlyList<StrategyOption> options, StrategyReference identity) =>
        options.SingleOrDefault(item => item.Identity.StrategyId == identity.StrategyId && item.Identity.SemanticVersion == identity.SemanticVersion &&
            item.Identity.ContentSha256.Equals(identity.ContentSha256, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("Selected exact strategy is no longer authorized.");
}
