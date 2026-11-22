using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Web.Services;

/// <summary>
/// The prices a ticket opens with. Every value comes from market data or from the
/// strategy's own rules; a value that cannot be derived is null, never a
/// stand-in number, and <see cref="StopNote"/> / <see cref="TargetNote"/> say why.
/// </summary>
/// <param name="Limit">Inside ask, else inside mid, else the limit carried in by a replace; null without any.</param>
/// <param name="Stop">The strategy's initial stop from the engine's resolver, or null.</param>
/// <param name="Target">The strategy's target by the planner's rule, or null.</param>
/// <param name="StopNote">How the stop was derived, or why there is none.</param>
/// <param name="TargetNote">How the target was derived, or why there is none.</param>
/// <param name="RiskBudget">Account equity times the profile's risk budget, or null when either is unknown.</param>
/// <param name="Direction">"long" (stop below, target above the entry) or "short" (stop above, target below).</param>
public sealed record DeskTicketPlan(
    decimal? Limit,
    decimal? Stop,
    decimal? Target,
    string StopNote,
    string TargetNote,
    decimal? RiskBudget,
    string Direction = "long")
{
    /// <summary>A plan with no stop or target and the reason, keeping the entry price and side for display.</summary>
    public static DeskTicketPlan Unavailable(decimal? limit, string reason, decimal? riskBudget, string direction = "long") =>
        new(limit, null, null, reason, reason, riskBudget, direction);

    /// <summary>True for a strategy that enters short; such a plan is priced at the bid with the stop above the entry.</summary>
    public static bool IsShort(StrategyDefinition strategy) => strategy.Direction.Equals("short", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Plans a ticket's stop and target with the same engine code paper and live use:
/// <see cref="SignalGenerator"/> on the strategy's setup timeframe,
/// <see cref="StrategyInitialStopResolver"/> on its execution timeframe, and the
/// target rule of <see cref="StrategyOrderPlanner"/> (R multiple, or VWAP mode).
/// </summary>
/// <remarks>
/// Market state comes from the live streaming processor, warmed to the paper
/// profile's indicator warm-up depth. A symbol that is not streamed, not warmed,
/// or stale gets no stop and no target - the ticket says so and the operator
/// enters them. Nothing here is a placeholder percentage. The server's ticket
/// review re-checks whatever is posted either way.
/// </remarks>
public sealed class DeskTicketPlanService
{
    private readonly IMarketStateSnapshotProvider marketState;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<DeskTicketPlanService> logger;
    private readonly StreamingMarketStateProcessor? processor;

    /// <param name="processor">
    /// The live processor, when present, is asked which symbols it already tracks.
    /// Reading state for an untracked symbol would register a pipeline for it,
    /// and pipelines are only removed by explicit eviction - a ticket opened on
    /// an arbitrary symbol must not leave one behind.
    /// </param>
    public DeskTicketPlanService(
        IMarketStateSnapshotProvider marketState,
        TimeProvider timeProvider,
        ILogger<DeskTicketPlanService> logger,
        StreamingMarketStateProcessor? processor = null)
    {
        this.marketState = marketState;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.processor = processor;
    }

    /// <param name="strategy">Strategy in context; null plans no stop or target.</param>
    /// <param name="ticker">Symbol the ticket is open on.</param>
    /// <param name="ask">Inside ask.</param>
    /// <param name="mid">Inside mid.</param>
    /// <param name="limitOverride">A limit carried in, such as the order being replaced.</param>
    /// <param name="warmupBars">The paper profile's indicator warm-up depth.</param>
    /// <param name="riskBudget">Equity times risk budget, passed through for the 1R preset.</param>
    public async Task<DeskTicketPlan> PlanAsync(
        StrategyDefinition? strategy,
        string ticker,
        decimal? ask,
        decimal? mid,
        decimal? limitOverride,
        int warmupBars,
        decimal? riskBudget,
        CancellationToken cancellationToken)
    {
        var limit = ResolveLimit(ask, mid, limitOverride);
        if (strategy is null)
        {
            return DeskTicketPlan.Unavailable(limit, "No strategy in context, so no strategy stop or target.", riskBudget);
        }

        // Every answer below carries the strategy's side, so a short ticket never opens as a buy even without levels.
        var direction = DeskTicketPlan.IsShort(strategy) ? "short" : "long";

        if (limit is null)
        {
            return DeskTicketPlan.Unavailable(null, "No quote and no limit to plan from.", riskBudget, direction);
        }

        if (processor is not null &&
            !processor.GetTrackedSymbols().Contains(ticker.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase))
        {
            return DeskTicketPlan.Unavailable(
                limit,
                $"{ticker.ToUpperInvariant()} is not streamed, so there is no live market state to plan a stop from; enter a stop and target.",
                riskBudget,
                direction);
        }

        TickerMarketState? state;
        try
        {
            state = await marketState.GetTickerStateAsync(
                ticker,
                new[] { strategy.Timeframe, strategy.Execution.Timeframe }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Math.Max(2, warmupBars),
                timeProvider.GetUtcNow(),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogInformation(exception, "Market state unavailable for the {Ticker} ticket plan.", ticker);
            state = null;
        }

        if (state is null)
        {
            return DeskTicketPlan.Unavailable(
                limit,
                $"No warmed live market state for {ticker.ToUpperInvariant()} on {strategy.Timeframe}/{strategy.Execution.Timeframe}; enter a stop and target.",
                riskBudget,
                direction);
        }

        return Build(
            strategy,
            limit.Value,
            state.BarsByTimeframe.GetValueOrDefault(strategy.Timeframe) ?? [],
            state.SnapshotsByTimeframe.GetValueOrDefault(strategy.Timeframe) ?? [],
            state.BarsByTimeframe.GetValueOrDefault(strategy.Execution.Timeframe) ?? [],
            state.SnapshotsByTimeframe.GetValueOrDefault(strategy.Execution.Timeframe) ?? [],
            riskBudget);
    }

    /// <summary>The limit a ticket opens with, or null when nothing real supports one.</summary>
    public static decimal? ResolveLimit(decimal? ask, decimal? mid, decimal? limitOverride) =>
        limitOverride is > 0m ? limitOverride
        : ask is > 0m ? ask
        : mid is > 0m ? mid
        : null;

    /// <summary>
    /// The planning core, pure over completed bars and their indicator snapshots,
    /// so the rules are testable without a live stream. Long strategies plan a stop
    /// below and a target above the entry, short strategies the mirror image; a strategy that may go either way gets no levels.
    /// Unsupported dynamic protection is reported before any entry prices are planned.
    /// </summary>
    public static DeskTicketPlan Build(
        StrategyDefinition strategy,
        decimal limit,
        IReadOnlyList<OhlcvBar> setupBars,
        IReadOnlyList<IndicatorSnapshot> setupSnapshots,
        IReadOnlyList<OhlcvBar> executionBars,
        IReadOnlyList<IndicatorSnapshot> executionSnapshots,
        decimal? riskBudget)
    {
        // Long strategies plan a stop below and a target above the entry; short strategies the mirror image. A strategy
        // that may go either way has no single side to plan, so it gets no levels.
        var isShort = DeskTicketPlan.IsShort(strategy);
        var direction = isShort ? "short" : "long";
        if (strategy.ExitRules.ContinuationProtection is not null)
            return DeskTicketPlan.Unavailable(limit,
                "Continuation protection requires the day-trading stop and target lifecycle, which is not connected yet.", riskBudget, direction);
        if (!isShort && !strategy.Direction.Equals("long", StringComparison.OrdinalIgnoreCase))
            return DeskTicketPlan.Unavailable(limit,
                "This strategy can enter either way, so there is no single side to plan a stop and target for.", riskBudget);
        if (setupBars.Count < 2 || setupSnapshots.Count != setupBars.Count ||
            executionBars.Count == 0 || executionSnapshots.Count != executionBars.Count)
        {
            return DeskTicketPlan.Unavailable(limit, "Not enough completed bars to plan a stop.", riskBudget, direction);
        }

        var signal = new SignalGenerator().CreateTradeSignal(
            strategy,
            setupBars,
            setupSnapshots,
            setupSnapshots.Count - 1);
        if (signal is null)
        {
            return DeskTicketPlan.Unavailable(limit, "The strategy's setup indicators are not available yet.", riskBudget, direction);
        }

        var stopContextIndex = executionSnapshots.Count - 1;
        var resolved = new StrategyInitialStopResolver().Resolve(new StrategyInitialStopRequest(
            strategy,
            signal,
            isShort ? PlannedOrderSide.Short : PlannedOrderSide.Long,
            limit,
            stopContextIndex,
            executionBars,
            executionSnapshots));
        if (!resolved.IsResolved || resolved.StopPrice is not { } stopPrice)
        {
            var reason = resolved.RejectionReason ?? "initial_stop_not_resolved";
            return DeskTicketPlan.Unavailable(
                limit,
                $"The strategy's stop could not be resolved ({reason.Replace('_', ' ')}).",
                riskBudget,
                direction);
        }

        var stop = Math.Round(stopPrice, 2, MidpointRounding.AwayFromZero);
        var stopNote = $"{strategy.StrategyName} · {strategy.ExitRules.InitialStopMode.Replace('_', ' ')} stop on {strategy.Execution.Timeframe}";

        // The planner's rule: VWAP mode targets the execution VWAP; otherwise the target is the entry moved by the stop
        // distance times the target R multiple, in the trade's favour (up for longs, down for shorts). A target that is
        // non-positive or on the wrong side of the entry is not offered.
        var vwapMode = strategy.ExitRules.ProfitTargetMode.Equals("vwap", StringComparison.OrdinalIgnoreCase);
        var stopDistance = Math.Abs(limit - stopPrice);
        var target = vwapMode
            ? executionSnapshots[stopContextIndex].Vwap
            : isShort ? limit - (stopDistance * strategy.ExitRules.TargetRMultiple) : limit + (stopDistance * strategy.ExitRules.TargetRMultiple);
        var favourable = target is { } candidate && candidate > 0m && (isShort ? candidate < limit : candidate > limit);
        if (!favourable || target is not { } targetPrice)
        {
            var side = isShort ? "below" : "above";
            return new DeskTicketPlan(
                limit,
                stop,
                null,
                stopNote,
                vwapMode
                    ? $"The strategy targets VWAP, which is not {side} the entry now; enter a target."
                    : $"The strategy's target is not {side} the entry; enter a target.",
                riskBudget,
                direction);
        }

        var targetNote = vwapMode
            ? $"{strategy.StrategyName} · VWAP target on {strategy.Execution.Timeframe}"
            : $"{strategy.StrategyName} · {strategy.ExitRules.TargetRMultiple:0.##}R target";
        return new DeskTicketPlan(
            limit,
            stop,
            Math.Round(targetPrice, 2, MidpointRounding.AwayFromZero),
            stopNote,
            targetNote,
            riskBudget,
            direction);
    }

    /// <summary>
    /// Money one R risks: account equity times the profile's risk budget percent.
    /// Null when either is unknown, so the 1R preset is offered only when real.
    /// </summary>
    public static decimal? RiskBudget(decimal? equity, decimal? accountRiskBudgetPct) =>
        equity is > 0m && accountRiskBudgetPct is > 0m
            ? Math.Round(equity.Value * accountRiskBudgetPct.Value / 100m, 2)
            : null;
}
