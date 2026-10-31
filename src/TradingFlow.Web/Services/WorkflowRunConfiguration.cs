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
    TradingFlow.Backtesting.ConfiguredDataProviders? dataProviders = null,
    IBacktestEventSourcePreparation? eventSourcePreparation = null) : IWorkflowRunConfiguration
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
                    item.Artifact.AdmissionProfile.ExecutionTimingPolicy, item.Artifact.AdmissionProfile.RiskPolicy),
                ZoneSettings(run, item.Definition))).ToArray(),
            string.Join(Environment.NewLine, selected.Select(item =>
                $"{item.Definition.StrategyName}: {ExecutionAssumptionNote(run, item.Definition)}")),
            StrategyHorizonResolver.Resolve(selected[0].Definition),
            SourceHistoryNote(run, selected[0].Definition));
    }

    /// <summary>Describes the selected server execution contract, including an explicitly disabled minimum reward/risk filter.</summary>
    private static string ExecutionAssumptionNote(BacktestRunConfig run, StrategyDefinition strategy)
    {
        if (strategy.EntryRules.IntradayZone is { IsPythonVolumeSpreadComparison: true } volumeZone)
        {
            return VolumeSpreadAssumptionNote(run, volumeZone.PythonVolumeSpreadComparison!);
        }
        if (strategy.EntryRules.IntradayZone is { IsPythonComparison: true })
            return "Python zone comparison diagnostic: causal 15-minute zones require at least two merged confirmed pivots as an approximation of Python touches. Completed five-minute ATR14 and regular-session VWAP determine bounce/breakdown entries; shorts require same-clock RVOL >= 1.2, longs have no volume gate. Fixed zone/ATR stop and next-zone target remain unchanged after fills; no 3R or continuation protection. New entries stop inclusively thirty minutes before the authoritative session close (15:30 New York on ordinary sessions); existing exposure closes five minutes before close. Five-minute candle fills use declared costs, participation and adverse ordering assumptions. Two filled parent trades per stock/session, one position. Short borrow is diagnostic.";
        if (strategy.EntryRules.IntradayZone is not null)
            return "15-minute completed close-pivot zones with five-minute breakout/retest or rejection/bounce confirmations. Confirmation closes must be on the directional side of regular-session VWAP and have volume strictly above the prior twenty same-clock session median; no EMA filter. Actual historical SIP quotes/trades determine fills, spread, displayed-size participation and 3R. Two filled parent trades per stock/session, one position, completed-close continuation and five-minute-before-close deadline. Missing or invalid quotes reject execution; no candle fallback. Short borrow and original historical receipt times remain research limitations.";
        if (strategy.EntryRules.OpeningRangeBreakout is { PolicyId: OpeningRangeBreakoutRules.CompletedBreakoutPolicyId } completed)
            return $"Opening-range diagnostic: first fifteen minutes freeze body direction and twenty-session median RVOL > 1. A completed post-range five-minute close strictly beyond the valid trigger commits a next-bar-opening market entry (earliest 09:50 New York). Stop is that completed candle low/high, rounded outward without an extra buffer; size uses completed close, actual fills determine 3R. Filled parent trade limit per stock per session: {completed.MaxFilledParentTradesPerSession}, with a fresh completed inside-range return after resolution before another breakout. Partial market remainder persists until fill, protection or session deadline. Lagged completed volume supplies opening capacity; gaps including through the stop are filled and reduced through shared risk protection. Existing continuation protection, adverse OHLC ordering, costs, caps and session liquidation remain diagnostic.";
        if (strategy.EntryRules.OpeningRangeBreakout is { } openingRange)
            return $"Opening-range diagnostic: direction and relative volume freeze after the first fifteen minutes; stop-market entry remains active until the session deadline. Filled parent trade limit per stock per session: {openingRange.MaxFilledParentTradesPerSession}, with a completed return inside the frozen range before re-entry. Triggered entry remainder remains a market order. Liquidation is scheduled five minutes before the authoritative close. Missing executable evidence leaves unresolved exposure; five-minute OHLC uses participation and adverse ordering assumptions; short borrow is diagnostic.";
        if (StrategyHorizonResolver.Resolve(strategy) == "day")
            return "Day diagnostic: entries expire after one bar; liquidation is scheduled at the final regular-session bar opening. Missing executable evidence leaves unresolved exposure. Five-minute OHLC uses declared penetration, participation and adverse ordering assumptions; short borrow is diagnostic.";
        return run.Engine.PrecommittedLiquidationSourceUtc is { } liquidation
            ? $"Liquidation is precommitted at source time {liquidation:O}; ordinary fills still require executable market evidence."
            : "Positions remain open at the exclusive cutoff; no forced terminal exit.";
    }

    /// <summary>Publishes server-owned zone policy and the frozen prospective cost basis to all clients.</summary>
    private static BacktestZoneSettingsResponse? ZoneSettings(BacktestRunConfig run, StrategyDefinition strategy)
    {
        var rules = strategy.EntryRules.IntradayZone?.PythonVolumeSpreadComparison;
        if (rules is null) return null;
        var planningCosts = rules.UsesFullCostTargets
            ? $"Completed confirmation midpoint; adverse half-spread {rules.SyntheticSpreadBps / 2m} bps, slippage {strategy.Execution.SlippageBps} bps and configured maximum impact {run.Portfolio.ImpactBpsAtMaxParticipation} bps. Target estimate equals its limit with no favorable improvement. Full-cost target estimates include fixed and sell-side regulatory fees; existing shared sizing remains unchanged."
            : "Existing next-zone admission ratio uses synthetic entry bid/ask and fixed round-trip ticket fees only; it omits exit friction and regulatory fees. Actual execution retains all configured modeled costs. No full-cost target calculation is applied.";
        return new(rules.LongBounceVolumeMode ?? "threshold", rules.TargetPolicy ?? "next_zone_only",
            rules.FallbackNetRiskMultiple, rules.MinimumRelativeVolume, rules.SyntheticSpreadBps,
            VolumeSpreadStopNote(rules),
            planningCosts);
    }

    /// <summary>Describes the selected volume treatment and separates old ticket-only admission from full-cost target planning.</summary>
    private static string VolumeSpreadAssumptionNote(BacktestRunConfig run, PythonZoneVolumeSpreadComparisonRules rules)
    {
        var volume = rules.UsesBreakoutRetestEntry
            ? $"Long-retest volume is diagnostic only with valid same-clock evidence required; shorts require RVOL >= {rules.MinimumRelativeVolume}."
            : rules.LongVolumeIsDiagnostic
            ? $"Long-bounce volume is diagnostic only with valid same-clock evidence required; shorts require RVOL >= {rules.MinimumRelativeVolume}."
            : $"Both directions require same-clock RVOL >= {rules.MinimumRelativeVolume}.";
        return $"Zone volume/spread candle diagnostic: causal completed regular-session 15-minute zones use at least two merged confirmed pivots, not independent price touches. {VolumeSpreadEntryNote(rules)} {volume} No VWAP or overextension filter. Synthetic full spread {rules.SyntheticSpreadBps} bps; maximum {rules.MaximumSpreadBps} bps. Alternatives retain frozen zone-bottom order, long then short. Current capital and existing risk/notional sizing determine shares before target admission. {VolumeSpreadTargetNote(run, rules)} {VolumeSpreadStopNote(rules)} No continuation protection. Entry cutoff is inclusive ten minutes before the authoritative close (15:50 New York on ordinary sessions); liquidation is five minutes before close (15:55). One position and two filled parent entries per stock/session. Actual later execution includes configured spread, slippage and fees. Estimated quotes are not historical quotes; actual spread eligibility and short borrow remain diagnostic assumptions.";
    }

    // Name the selected long sequence while keeping its admission costs and unchanged short rule explicit.
    private static string VolumeSpreadEntryNote(PythonZoneVolumeSpreadComparisonRules rules) =>
        rules.UsesBreakoutRetestEntry
            ? "Long entry: existing resistance must complete an upward 15-minute flip, followed by a later five-minute actual overlap and close above the frozen box. No green-candle requirement or dollar touch tolerance. Every emitted long alternative consumes its arm even if admission rejects it. Active source and target snapshot versions remain separate from the frozen arm. Short breakdown rule unchanged."
            : "Long entry: previous low within the configured support-top tolerance and a green close above support. Short entry: completed downward zone crossing.";

    // Explain the configured long stop and unchanged short stop with the same buffer used by the engine.
    private static string VolumeSpreadStopNote(PythonZoneVolumeSpreadComparisonRules rules) =>
        rules.LongStopBasis == PythonZoneVolumeSpreadComparisonRules.TwoCandleLowStop
            ? $"Long stop: lower low of the previous and confirmation candles minus {rules.StopAtrMultiple} times completed five-minute ATR, rounded down to the legal tick. Short stop: source-zone top plus the same ATR buffer. Fixed after admission; no clamp to the old zone stop."
            : $"Fixed source-zone/ATR stop: long below source-zone bottom, short above source-zone top, using {rules.StopAtrMultiple} times completed five-minute ATR; fixed after admission.";

    /// <summary>States target origin, cost-covering rejection and the distinction between planned payoff and realized fills.</summary>
    private static string VolumeSpreadTargetNote(BacktestRunConfig run, PythonZoneVolumeSpreadComparisonRules rules)
    {
        if (rules.UsesFullCostTargets)
            return $"A known next-zone target must have positive estimated net reward after full modeled costs; reject only that source alternative and continue frozen source order if it fails. Never replace its nearby failing target with a farther box. Only an absent next zone permits a planned net-{rules.FallbackNetRiskMultiple}R target. Freeze the target before the execution candle using completed-close estimates, adverse spread/slippage and configured maximum impact {run.Portfolio.ImpactBpsAtMaxParticipation} bps, limit-price target execution and fixed/regulatory fees. Full-cost loss is the target denominator at unchanged shared-sized quantity, not a new sizing budget. No 1.5R or 2R minimum on known-zone targets; the new positive-net floor is explicit. Planned payoff does not guarantee realized reward/risk.";
        var admission = rules.MinimumRewardRisk == 0m
            ? "first successfully sized alternative; minimum reward/risk filter disabled. The fee-adjusted ratio remains audit evidence even when its net reward is negative."
            : $"first alternative passing estimated reward/risk >= {rules.MinimumRewardRisk} after configured round-trip ticket fees.";
        return $"Reserve only the {admission} This legacy admission estimate omits exit spread, slippage and regulatory fees; modeled execution includes them. Fixed next-zone target; no target means no candidate.";
    }
    /// <summary>Shows the calendar-derived source requirement that was frozen through ordinary plan review.</summary>
    private static string? SourceHistoryNote(BacktestRunConfig run, StrategyDefinition strategy)
    {
        if (StrategyHorizonResolver.Resolve(strategy) != "day") return null;
        var context = strategy.EntryRules.IntradayZone is not null
            ? "the immediately preceding twenty authoritative sessions for same-clock five-minute volume medians and five-session carried 15-minute zones"
            : strategy.EntryRules.OpeningRangeBreakout is not null
            ? "the immediately preceding twenty authoritative exchange sessions for the opening-volume median (twenty valid observations required), plus previous-session context"
            : "the preceding completed exchange session for previous-session levels";
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
            var originalPath = path;
            if (generated.Engine.ExecutionReplayPolicyId == ExecutionReplayPolicy.IntradayRthFiveMinuteQuoteMarketV1 &&
                generated.Engine.ExecutionSourceManifestPath is null)
            {
                var preparation = eventSourcePreparation ?? throw new InvalidOperationException(
                    "The saved quote-execution plan needs the configured historical SIP preparation service.");
                using var generatedLease = DurableFileJobRequest.Capture(ResolveInputs(path, generated, scope)).AcquireVerifiedReadLease();
                path = await preparation.PrepareAsync(path, generated, request.TestWindowEndUtc, token);
                generated = reader.ReadBacktestRun(path);
            }
            return ResolveInputs(path, generated, scope).Append(originalPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
        if (selected.Any(item => item.Definition.EntryRules.IntradayZone is not null) && request.MaxConcurrentPositions != 1)
            throw new InvalidOperationException("Intraday zone strategies require the saved plan's maximum concurrent positions to be one.");
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
            var requiredSessions = selected.Any(item => item.Definition.EntryRules.OpeningRangeBreakout is not null || item.Definition.EntryRules.IntradayZone is not null) ? 20 : 1;
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
