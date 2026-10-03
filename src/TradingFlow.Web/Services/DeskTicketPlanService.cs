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
public sealed record DeskTicketPlan(
    decimal? Limit,
    decimal? Stop,
    decimal? Target,
    string StopNote,
    string TargetNote,
    decimal? RiskBudget)
{
    public static DeskTicketPlan Unavailable(decimal? limit, string reason, decimal? riskBudget) =>
        new(limit, null, null, reason, reason, riskBudget);
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

        if (limit is null)
        {
            return DeskTicketPlan.Unavailable(null, "No quote and no limit to plan from.", riskBudget);
        }

        if (processor is not null &&
            !processor.GetTrackedSymbols().Contains(ticker.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase))
        {
            return DeskTicketPlan.Unavailable(
                limit,
                $"{ticker.ToUpperInvariant()} is not streamed, so there is no live market state to plan a stop from; enter a stop and target.",
                riskBudget);
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
                riskBudget);
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
    /// so the rules are testable without a live stream. Long entries only: the
    /// ticket's entry side is buy, and a sell closes a position with no bracket.
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
        if (setupBars.Count < 2 || setupSnapshots.Count != setupBars.Count ||
            executionBars.Count == 0 || executionSnapshots.Count != executionBars.Count)
        {
            return DeskTicketPlan.Unavailable(limit, "Not enough completed bars to plan a stop.", riskBudget);
        }

        var signal = new SignalGenerator().CreateTradeSignal(
            strategy,
            setupBars,
            setupSnapshots,
            setupSnapshots.Count - 1);
        if (signal is null)
        {
            return DeskTicketPlan.Unavailable(limit, "The strategy's setup indicators are not available yet.", riskBudget);
        }

        var stopContextIndex = executionSnapshots.Count - 1;
        var resolved = new StrategyInitialStopResolver().Resolve(new StrategyInitialStopRequest(
            strategy,
            signal,
            PlannedOrderSide.Long,
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
                riskBudget);
        }

        var stop = Math.Round(stopPrice, 2, MidpointRounding.AwayFromZero);
        var stopNote = $"{strategy.StrategyName} · {strategy.ExitRules.InitialStopMode.Replace('_', ' ')} stop on {strategy.Execution.Timeframe}";

        // The planner's rule: VWAP mode targets the execution VWAP; otherwise the
        // target is the entry plus the stop distance times the target R multiple.
        // A target that is non-positive or not above the entry is not offered.
        var vwapMode = strategy.ExitRules.ProfitTargetMode.Equals("vwap", StringComparison.OrdinalIgnoreCase);
        var target = vwapMode
            ? executionSnapshots[stopContextIndex].Vwap
            : limit + ((limit - stopPrice) * strategy.ExitRules.TargetRMultiple);
        if (target is not { } targetPrice || targetPrice <= 0m || targetPrice <= limit)
        {
            return new DeskTicketPlan(
                limit,
                stop,
                null,
                stopNote,
                vwapMode
                    ? "The strategy targets VWAP, which is not above the entry now; enter a target."
                    : "The strategy's target is not above the entry; enter a target.",
                riskBudget);
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
            riskBudget);
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
