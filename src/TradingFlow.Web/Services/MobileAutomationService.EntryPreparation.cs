using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Data.Catalysts;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Web.Services;

// Entry preparation and market-state loading for an automation session. Strategy-gated
// alerts go through the same immutable decision request and persisted candidate lifecycle
// as paper/live runs. Operator-direct alerts are a separately labelled policy override.
public sealed partial class MobileAutomationService
{
    private async Task<TickerMarketState> LoadTickerStateAsync(
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        string ticker,
        IMarketDataProvider provider,
        CancellationToken cancellationToken)
    {
        var end = DateTimeOffset.UtcNow;
        var lookbackDays = runConfig.TimeWindow.WarmupLookbackDays > 0
            ? runConfig.TimeWindow.WarmupLookbackDays
            : Math.Max(runConfig.TimeWindow.LookbackDays, 10);
        var start = end.AddDays(-lookbackDays);
        var requiredTimeframes = ResolveRequiredTimeframes(strategy);
        var downloadTimeframes = ResolveDownloadTimeframesForAutomation(runConfig, strategy, requiredTimeframes);
        var deriveFromTimeframe = ResolveDeriveFromTimeframe(runConfig, requiredTimeframes, downloadTimeframes);

        var pipeline = new CandlePipelineEngine(candleStore);
        var state = await pipeline.RunAsync(
            new CandlePipelineRequest(
                [ticker],
                downloadTimeframes,
                requiredTimeframes,
                deriveFromTimeframe,
                start,
                end,
                runConfig.Engine.BoundedCapacity,
                runConfig.Engine.WorkerCount,
                runConfig.Execution.AllowExtendedHoursTrading,
                strategy.Session.ExchangeTimezone),
            provider,
            cancellationToken);

        if (!state.TickerStates.TryGetValue(ticker, out var tickerState))
        {
            var reason = state.Failures.TryGetValue(ticker, out var failure)
                ? failure
                : "missing_ticker_market_state";
            throw new InvalidOperationException(reason);
        }

        return tickerState;
    }

    private static string[] ResolveDownloadTimeframesForAutomation(
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        IReadOnlyCollection<string> requiredTimeframes)
    {
        var source = ResolveDeriveFromTimeframe(runConfig, requiredTimeframes, runConfig.Intervals);
        var sourceDuration = TimeframeParser.Parse(source);
        var timeframes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            source
        };

        foreach (var timeframe in runConfig.Intervals.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var duration = TimeframeParser.Parse(timeframe);
            if (duration <= sourceDuration || TimeframeParser.IsDailyOrHigher(timeframe))
            {
                timeframes.Add(timeframe);
            }
        }

        if (RequiresDailyContext(strategy))
        {
            timeframes.Add("1d");
        }

        return timeframes
            .OrderBy(TimeframeParser.Parse)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveDeriveFromTimeframe(
        BacktestRunConfig runConfig,
        IReadOnlyCollection<string> requiredTimeframes,
        IReadOnlyCollection<string> candidateDownloadTimeframes)
    {
        var subDailyConfirmationTimeframes = requiredTimeframes
            .Where(timeframe => !TimeframeParser.IsDailyOrHigher(timeframe))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(TimeframeParser.Parse)
            .ToArray();

        if (subDailyConfirmationTimeframes.Length == 0)
        {
            return runConfig.DerivedTimeframes.Source;
        }

        var finestRequired = subDailyConfirmationTimeframes[0];
        var finestRequiredDuration = TimeframeParser.Parse(finestRequired);
        var currentSource = string.IsNullOrWhiteSpace(runConfig.DerivedTimeframes.Source)
            ? finestRequired
            : runConfig.DerivedTimeframes.Source;

        if (TimeframeParser.Parse(currentSource) <= finestRequiredDuration)
        {
            return currentSource;
        }

        var providerCandidate = candidateDownloadTimeframes
            .Where(timeframe => !string.IsNullOrWhiteSpace(timeframe))
            .Where(timeframe => !TimeframeParser.IsDailyOrHigher(timeframe))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(TimeframeParser.Parse)
            .FirstOrDefault(timeframe => TimeframeParser.Parse(timeframe) <= finestRequiredDuration);

        return providerCandidate ?? finestRequired;
    }

    private static bool RequiresDailyContext(StrategyDefinition strategy)
    {
        return TimeframeParser.IsDailyOrHigher(strategy.Timeframe) ||
            TimeframeParser.IsDailyOrHigher(strategy.Execution.Timeframe) ||
            strategy.EntryRules.RequirePriceAboveSma50Daily ||
            strategy.EntryRules.RequirePriceAboveSma200Daily;
    }

    // The shared per-stock regime check. Under the Nasdaq home-market rule it reads the stock's listing from a broker client
    // created for this lookup and disposed afterwards; without a broker client the listing is unknown and the entry is refused.
    // With VIX sizing the decision also carries the per-entry risk scale applied when the order is sized.
    private async Task<RegimeEntryDecision> MobileRegimeDecisionAsync(RegimeRules regime, BacktestRunConfig runConfig,
        IMarketDataProvider provider, string ticker, DateTimeOffset decisionAtUtc, CancellationToken cancellationToken)
    {
        var broker = regime.UsesNasdaqHomeMarket ? runtimeFactory.CreateBrokerClient(runConfig) : null;
        try
        {
            return await regimeGate.EntryDecisionAsync(regime, provider, runConfig.Intervals, decisionAtUtc, ticker, broker, cancellationToken);
        }
        finally
        {
            (broker as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Evaluates a strategy-gated mobile entry at <paramref name="decisionAtUtc"/> through the shared decision kernel,
    /// applying the regime gate (benchmark, optional Nasdaq home-market, and optional VIX sizing) and carrying the
    /// resulting per-entry risk scale to order planning. Throws when required timeframe data is unavailable or the
    /// candidate decision does not trigger.
    /// </summary>
    private async Task<PreparedEntryExecution> PrepareStrategyEntryExecutionAsync(
        BacktestRunConfig runConfig,
        AuthorizedRuntimeStrategy runtimeStrategy,
        MutableAutomationSession session,
        TickerMarketState state,
        IMarketDataProvider provider,
        ExecutionRunContext runContext,
        DateTimeOffset decisionAtUtc,
        CancellationToken cancellationToken)
    {
        var strategy = runtimeStrategy.Definition;
        RequireSupportedEntryWindow(strategy, "strategy_gated");
        var ticker = session.Ticker;
        if (!state.SnapshotsByTimeframe.TryGetValue(strategy.Timeframe, out var strategySnapshots) ||
            strategySnapshots.Count == 0 ||
            !state.BarsByTimeframe.TryGetValue(strategy.Timeframe, out var strategyBars) ||
            strategyBars.Count == 0)
        {
            throw new InvalidOperationException($"Strategy timeframe {strategy.Timeframe} is unavailable for {ticker}.");
        }

        if (candidateDecisions is null)
        {
            throw new InvalidOperationException(
                "Mobile strategy admission requires durable candidate persistence.");
        }

        var catalysts = await new CatalystStreamer(runtimeFactory.CreateNewsProvider(runConfig))
            .LoadTickerCatalystsAsync(
                ticker,
                state.AllBars.Min(bar => bar.Timestamp),
                decisionAtUtc,
                cancellationToken);
        var regimeDecision = strategy.Regime is not { IsActive: true } regimeRule ? RegimeEntryDecision.Allowed :
            await MobileRegimeDecisionAsync(regimeRule, runConfig, provider, ticker, decisionAtUtc, cancellationToken);
        var regimeRejection = regimeDecision.Rejection;
        var regimeOn = regimeRejection is null;
        var latest = strategySnapshots[^1];
        var setupDuration = TimeframeParser.Parse(strategy.Timeframe);
        var setupAvailableAtUtc = latest.Timestamp.Add(setupDuration).ToUniversalTime();
        var candidateExpiresAtUtc = setupAvailableAtUtc.Add(setupDuration);
        var discovery = StrategyDecisionRequestAssembler.CreateAlertDiscovery(
            runContext.RunId,
            ticker,
            session.Source,
            session.CreatedAt.ToUniversalTime(),
            candidateExpiresAtUtc);
        var universeEvidence = new StrategyEligibilityEvidence(
            true,
            "mobile_alert_universe",
            discovery.AggregateId.ToString("N"),
            decisionAtUtc,
            JsonSerializer.Serialize(new
            {
                eligible = true,
                session.Source,
                session.SourcePackage,
                session.SourceTitle
            }));
        var regimeEvidence = new StrategyEligibilityEvidence(
            regimeOn,
            "shared_regime_gate",
            strategy.Regime is { IsActive: true }
                ? $"{strategy.Regime.EvidenceRule}:{decisionAtUtc:O}"
                : $"not-required:{decisionAtUtc:O}",
            decisionAtUtc,
            // The VIX reading is recorded only for strategies with VIX sizing, so other strategies' evidence is unchanged.
            regimeDecision.Vix is not { } vixReading
                ? JsonSerializer.Serialize(new { eligible = regimeOn, strategy.Regime, regimeRejection })
                : JsonSerializer.Serialize(new { eligible = regimeOn, strategy.Regime, regimeRejection, vix = vixReading, regimeDecision.RiskScale }));
        var setupKey = $"{strategy.StrategyId}:{latest.Timestamp.ToUniversalTime():O}";
        var request = StrategyDecisionRequestAssembler.Create(
            runtimeStrategy,
            ticker,
            state.BarsByTimeframe,
            state.SnapshotsByTimeframe,
            catalysts,
            decisionAtUtc,
            discovery,
            universeEvidence,
            regimeEvidence,
            StrategyCandidateState.Discovered,
            0,
            setupKey,
            setupAvailableAtUtc,
            candidateExpiresAtUtc,
            runConfig.Engine.IndicatorWarmupBars,
            runContext.RunId);
        var persisted = await candidateDecisions.EvaluateAsync(
            CreateProductionRun(runContext),
            request,
            cancellationToken);
        if (!persisted.IsNewTrigger ||
            persisted.Decision?.Signal is not { } signal ||
            persisted.Decision.OrderPlan is not { } orderPlan)
        {
            throw new InvalidOperationException(
                persisted.Decision?.NoEntryReason ??
                (persisted.WasAlreadyTerminal
                    ? $"candidate_already_{persisted.Candidate.State.ToString().ToLowerInvariant()}"
                    : $"candidate_{persisted.Candidate.State.ToString().ToLowerInvariant()}"));
        }

        if (!state.SnapshotsByTimeframe.TryGetValue(orderPlan.ExecutionTimeframe, out var executionSnapshots))
        {
            throw new InvalidOperationException(
                $"Execution timeframe {orderPlan.ExecutionTimeframe} is unavailable for {ticker}.");
        }

        var executionSignal = StrategyDecisionRequestAssembler.CreateExecutionSignal(
            signal,
            orderPlan,
            executionSnapshots);
        return new PreparedEntryExecution(
            signal,
            executionSignal,
            new ValidatedEntryCandidate(
                persisted.Candidate.CandidateId,
                persisted.Candidate.DiscoverySource,
                persisted.Candidate.Horizon,
                persisted.Candidate.DiscoveredAtUtc,
                persisted.Candidate.RevalidatedAtUtc,
                persisted.Decision.CanonicalDecisionJson,
                persisted.Candidate.Version,
                persisted.Candidate.SemanticDecisionSha256),
            OperatorOverride: false,
            OrderPlan: orderPlan,
            EntryRiskScale: regimeDecision.RiskScale);
    }

    private async Task<PreparedEntryExecution> PrepareAutomationEntryExecutionAsync(
        BacktestRunConfig runConfig,
        AuthorizedRuntimeStrategy runtimeStrategy,
        MutableAutomationSession session,
        TickerMarketState state,
        IMarketDataProvider provider,
        ExecutionRunContext runContext,
        string entryMode,
        DateTimeOffset decisionAtUtc,
        CancellationToken cancellationToken)
    {
        if (entryMode.Equals("operator_direct", StringComparison.OrdinalIgnoreCase))
        {
            if (manualEntryOptions.Policy != ManualEntryPolicy.OperatorDirect)
            {
                throw new InvalidOperationException(
                    "Operator-direct mobile entry is disabled by manual_entry_policy.");
            }

            return PrepareOperatorDirectEntry(
                runtimeStrategy,
                session,
                state,
                decisionAtUtc);
        }

        return await PrepareStrategyEntryExecutionAsync(
            runConfig,
            runtimeStrategy,
            session,
            state,
            provider,
            runContext,
            decisionAtUtc,
            cancellationToken);
    }

    private static PreparedEntryExecution PrepareOperatorDirectEntry(
        AuthorizedRuntimeStrategy runtimeStrategy,
        MutableAutomationSession session,
        TickerMarketState state,
        DateTimeOffset decisionAtUtc)
    {
        var strategy = runtimeStrategy.Definition;
        var ticker = session.Ticker;
        if (!state.SnapshotsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionSnapshots) ||
            executionSnapshots.Count == 0)
        {
            throw new InvalidOperationException($"Execution timeframe {strategy.Execution.Timeframe} is unavailable for {ticker}.");
        }

        var latest = executionSnapshots[^1];
        if (strategy.ExitRules.ResolveVolatilityUnit(latest) is null or <= 0m)
        {
            throw new InvalidOperationException(strategy.ExitRules.UsesMtr14
                ? "signal_missing_indicators (mtr14)"
                : "signal_missing_indicators (atr)");
        }

        return new PreparedEntryExecution(
            null,
            null,
            new ValidatedEntryCandidate(
                session.SessionId,
                "operator_alert",
                "swing",
                session.CreatedAt.ToUniversalTime(),
                decisionAtUtc,
                JsonSerializer.Serialize(new
                {
                    operatorOverride = true,
                    session.Source,
                    session.SourcePackage,
                    session.SourceTitle,
                    exitGuardianStrategy = strategy.StrategyId,
                    exitPolicyIdentity = runtimeStrategy.Identity,
                    latest.Timestamp,
                    latest.CurrentPrice,
                    latest.Atr
                })),
            OperatorOverride: true,
            OperatorSnapshot: latest);
    }

    /// <summary>
    /// Sizes a strategy-gated mobile entry order via <see cref="StrategyOrderPlanner"/> using the portfolio risk limits
    /// and the regime's per-entry risk scale (<see cref="PreparedEntryExecution.EntryRiskScale"/>). Throws if the
    /// canonical order plan is missing or rejected by risk limits.
    /// </summary>
    private static FinalizedOrder PrepareStrategyOrder(
        BacktestRunConfig runConfig,
        string ticker,
        decimal accountEquity,
        PreparedEntryExecution execution)
    {
        var orderPlan = new StrategyOrderPlanner().Plan(
            new StrategyOrderPlanningRequest(
                execution.OrderPlan
                    ?? throw new InvalidOperationException("Persisted canonical order plan is unavailable."),
                execution.ExecutionSignal
                    ?? throw new InvalidOperationException("Persisted execution signal is unavailable."),
                accountEquity,
                new OrderPlanningRiskLimits(
                    runConfig.Portfolio.AccountRiskBudgetPct,
                    runConfig.Portfolio.MaxPositionNotionalPct,
                    execution.EntryRiskScale),
                runConfig.Portfolio.FixedBuyFee,
                runConfig.Portfolio.FixedSellFee));
        return orderPlan.Order
            ?? throw new InvalidOperationException(
                orderPlan.RiskRejection?.Reason ??
                orderPlan.StopRejectionReason ??
                $"Unable to build an order risk plan for {ticker}.");
    }

    // Reject unsupported guardian policies before sizing an operator-requested entry.
    private static FinalizedOrder PrepareOperatorDirectOrder(
        BacktestRunConfig runConfig,
        StrategyDefinition guardianStrategy,
        string ticker,
        decimal accountEquity,
        IndicatorSnapshot snapshot)
    {
        TechnicalExecutionEngine.RequireSupportedExitPolicy(guardianStrategy);
        TechnicalExecutionEngine.RequireNoBacktestOnlyRules(guardianStrategy);
        var volatilityUnit = guardianStrategy.ExitRules.ResolveVolatilityUnit(snapshot) is > 0m and var unit
            ? unit
            : throw new InvalidOperationException(guardianStrategy.ExitRules.UsesMtr14
                ? "Operator-direct entry requires a positive MTR-14."
                : "Operator-direct entry requires a positive ATR.");
        var riskPlan = new SharedOrderRiskPlanner().Plan(
            new OrderPlanningRequest(
                ticker,
                PlannedOrderSide.Long,
                accountEquity,
                snapshot.CurrentPrice,
                PlannedStopRequest.FromAtr(volatilityUnit, guardianStrategy.ExitRules.StopAtrMultiple),
                new OrderPlanningRiskLimits(
                    runConfig.Portfolio.AccountRiskBudgetPct,
                    runConfig.Portfolio.MaxPositionNotionalPct),
                new EstimatedOrderExecutionCosts(
                    guardianStrategy.Execution.SlippageBps,
                    runConfig.Portfolio.FixedBuyFee,
                    runConfig.Portfolio.FixedSellFee)));
        var planned = riskPlan.Order
            ?? throw new InvalidOperationException(
                riskPlan.Rejection?.Reason ?? $"Unable to risk-size operator entry for {ticker}.");
        var triggerRiskPerShare = planned.EstimatedEntryPrice - planned.StopTriggerPrice;
        var takeProfit = planned.EstimatedEntryPrice +
            triggerRiskPerShare * guardianStrategy.ExitRules.TargetRMultiple;
        return new FinalizedOrder(
            planned.Symbol,
            $"Operator Alert Entry / {guardianStrategy.StrategyName} Exit",
            planned.Quantity,
            Decimal.Round(planned.EstimatedEntryPrice, 4),
            Decimal.Round(planned.StopTriggerPrice, 4),
            Decimal.Round(takeProfit, 4),
            snapshot.Timestamp);
    }

    // Mobile does not own the daily calendar window; refuse this opt-in before provider work on every entry path.
    internal static void RequireSupportedEntryWindow(StrategyDefinition strategy, string entryMode)
    {
        if (strategy.Execution.EntryWindow is not null && entryMode != "operator_direct")
            throw new InvalidOperationException("Mobile strategy automation does not support next-open entry windows. Use the reviewed paper-run workflow.");
    }

    internal static string NormalizeEntryMode(string? entryMode)
    {
        if (String.IsNullOrWhiteSpace(entryMode))
        {
            return "validate_strategy";
        }

        return entryMode.Trim().ToLowerInvariant() switch
        {
            "validate_strategy" => "validate_strategy",
            "operator_direct" => "operator_direct",
            _ => throw new InvalidOperationException(
                "Entry mode must be validate_strategy or operator_direct.")
        };
    }

    // Delegates required timeframe resolution to the shared assembler so mobile entry preparation matches backtest/live.
    private static string[] ResolveRequiredTimeframes(StrategyDefinition strategy) =>
        StrategyDecisionRequestAssembler.ResolveRequiredTimeframes(strategy);

    private static ProductionRun CreateProductionRun(ExecutionRunContext context) => new()
    {
        RunId = context.RunId,
        SchemaVersion = 1,
        ConfigHash = context.ConfigHash,
        CodeVersion = context.CodeVersion,
        Profile = context.Profile,
        Status = "running",
        StartedAtUtc = context.StartedAtUtc
    };

    private static int FindFirstBarIndexAtOrAfter(IReadOnlyList<OhlcvBar> bars, DateTimeOffset timestamp)
    {
        for (var index = 0; index < bars.Count; index++)
        {
            if (bars[index].Timestamp >= timestamp)
            {
                return index;
            }
        }

        return bars.Count;
    }

}
