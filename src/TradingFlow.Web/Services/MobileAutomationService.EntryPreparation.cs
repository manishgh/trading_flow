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
        var regimeOn = strategy.Regime is not { IsActive: true } regimeRule ||
            await regimeGate.IsRegimeOnAsync(
                regimeRule,
                provider,
                runConfig.Intervals,
                decisionAtUtc,
                cancellationToken);
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
                ? $"{strategy.Regime.BenchmarkSymbol}:{strategy.Regime.SmaPeriod}:{decisionAtUtc:O}"
                : $"not-required:{decisionAtUtc:O}",
            decisionAtUtc,
            JsonSerializer.Serialize(new { eligible = regimeOn, strategy.Regime }));
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
            OrderPlan: orderPlan);
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
        if (latest.Atr is null or <= 0m)
        {
            throw new InvalidOperationException("signal_missing_indicators (atr)");
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
                    runConfig.Portfolio.MaxPositionNotionalPct),
                runConfig.Portfolio.FixedBuyFee,
                runConfig.Portfolio.FixedSellFee));
        return orderPlan.Order
            ?? throw new InvalidOperationException(
                orderPlan.RiskRejection?.Reason ??
                orderPlan.StopRejectionReason ??
                $"Unable to build an order risk plan for {ticker}.");
    }

    private static FinalizedOrder PrepareOperatorDirectOrder(
        BacktestRunConfig runConfig,
        StrategyDefinition guardianStrategy,
        string ticker,
        decimal accountEquity,
        IndicatorSnapshot snapshot)
    {
        var atr = snapshot.Atr is > 0m
            ? snapshot.Atr.Value
            : throw new InvalidOperationException("Operator-direct entry requires a positive ATR.");
        var riskPlan = new SharedOrderRiskPlanner().Plan(
            new OrderPlanningRequest(
                ticker,
                PlannedOrderSide.Long,
                accountEquity,
                snapshot.CurrentPrice,
                PlannedStopRequest.FromAtr(atr, guardianStrategy.ExitRules.StopAtrMultiple),
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

    private static string[] ResolveRequiredTimeframes(StrategyDefinition strategy)
    {
        var timeframes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            strategy.Timeframe,
            strategy.Execution.Timeframe
        };
        if (strategy.Confluence.Enabled)
        {
            timeframes.Add(strategy.Confluence.Timeframe);
        }

        return timeframes.ToArray();
    }

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
