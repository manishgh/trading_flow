using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

/// <summary>
/// The ticket opens with prices from market data and the strategy's own rules,
/// through the engine's signal generator, initial-stop resolver and planner
/// target rule. Nothing may fall back to a stand-in percentage: a value that
/// cannot be derived is null and the plan says why.
/// </summary>
public sealed class DeskTicketPlanTests
{
    [Fact]
    public void AtrStopAndRTarget_FollowTheStrategyExitRules()
    {
        var strategy = Strategy(stopMode: "atr", stopAtrMultiple: 2m, targetR: 3m);

        var plan = Plan(strategy, limit: 100m, setupAtr: 2m, executionVwap: 100m);

        Assert.Equal(96m, plan.Stop);
        Assert.Equal(112m, plan.Target);
        Assert.Contains("atr stop on 1h", plan.StopNote);
        Assert.Contains("3R target", plan.TargetNote);
    }

    [Fact]
    public void VwapTarget_UsesExecutionVwapOnlyWhenAboveTheEntry()
    {
        var strategy = Strategy(stopMode: "atr", stopAtrMultiple: 1m, targetR: 3m, targetMode: "vwap");

        var above = Plan(strategy, limit: 100m, setupAtr: 2m, executionVwap: 105m);
        Assert.Equal(98m, above.Stop);
        Assert.Equal(105m, above.Target);

        var below = Plan(strategy, limit: 100m, setupAtr: 2m, executionVwap: 99m);
        Assert.Equal(98m, below.Stop);
        Assert.Null(below.Target);
        Assert.Contains("VWAP", below.TargetNote);
    }

    [Fact]
    public void MissingSetupIndicators_LeaveStopAndTargetEmptyWithAReason()
    {
        var plan = Plan(Strategy("atr", 2m, 3m), limit: 100m, setupAtr: null, executionVwap: 100m);

        Assert.Equal(100m, plan.Limit);
        Assert.Null(plan.Stop);
        Assert.Null(plan.Target);
        Assert.Contains("indicators", plan.StopNote);
    }

    [Fact]
    public void UnresolvedStop_ReportsTheResolverReason()
    {
        var plan = Plan(Strategy("atr", 2m, 3m), limit: 100m, setupAtr: 0m, executionVwap: 100m);

        Assert.Null(plan.Stop);
        Assert.Null(plan.Target);
        Assert.Contains("atr unavailable for initial stop", plan.StopNote);
    }

    [Fact]
    public void TooFewBars_PlanNothing()
    {
        var strategy = Strategy("atr", 2m, 3m);
        var plan = DeskTicketPlanService.Build(strategy, 100m, [Bar("1d", 0)], [Snapshot("1d", 0, 2m, 100m)], [], [], null);

        Assert.Null(plan.Stop);
        Assert.Contains("Not enough completed bars", plan.StopNote);
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData(101.5, 101.2, null, 101.5)]
    [InlineData(null, 101.2, null, 101.2)]
    [InlineData(101.5, 101.2, 99.0, 99.0)]
    [InlineData(0.0, 0.0, null, null)]
    public void Limit_ComesFromAReplacedOrderElseAskElseMidElseNothing(
        double? ask, double? mid, double? replaced, double? expected)
    {
        Assert.Equal(
            (decimal?)expected,
            DeskTicketPlanService.ResolveLimit((decimal?)ask, (decimal?)mid, (decimal?)replaced));
    }

    [Fact]
    public async Task PlanAsync_WithoutStrategyQuoteOrState_OffersNoStopAndSaysWhy()
    {
        var provider = new CountingProvider();
        var service = new DeskTicketPlanService(provider, TimeProvider.System, NullLogger<DeskTicketPlanService>.Instance);

        var noStrategy = await service.PlanAsync(null, "MU", 101m, 100.5m, null, 260, 500m, CancellationToken.None);
        Assert.Equal(101m, noStrategy.Limit);
        Assert.Null(noStrategy.Stop);
        Assert.Contains("No strategy", noStrategy.StopNote);
        Assert.Equal(500m, noStrategy.RiskBudget);

        var noQuote = await service.PlanAsync(Strategy("atr", 2m, 3m), "MU", null, null, null, 260, null, CancellationToken.None);
        Assert.Null(noQuote.Limit);
        Assert.Null(noQuote.Stop);

        var noState = await service.PlanAsync(Strategy("atr", 2m, 3m), "MU", 101m, 100.5m, null, 260, null, CancellationToken.None);
        Assert.Null(noState.Stop);
        Assert.Contains("No warmed live market state", noState.StopNote);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(260, provider.LastMinimumBars);
    }

    [Fact]
    public async Task PlanAsync_NeverReadsStateForASymbolTheProcessorDoesNotTrack()
    {
        var provider = new CountingProvider();
        var processor = new StreamingMarketStateProcessor(
            NullCandleStore.Instance,
            new CandleStoreContext("test", "ticket-plan", "alpaca-sip"),
            StreamingMarketStateOptions.Default,
            TimeProvider.System,
            NullLogger<StreamingMarketStateProcessor>.Instance);
        var service = new DeskTicketPlanService(provider, TimeProvider.System, NullLogger<DeskTicketPlanService>.Instance, processor);

        var plan = await service.PlanAsync(Strategy("atr", 2m, 3m), "ZZZZ", 10m, 10m, null, 260, null, CancellationToken.None);

        Assert.Null(plan.Stop);
        Assert.Contains("not streamed", plan.StopNote);
        Assert.Equal(0, provider.Calls);
        Assert.DoesNotContain("ZZZZ", processor.GetTrackedSymbols());
    }

    private sealed class CountingProvider : IMarketStateSnapshotProvider
    {
        public int Calls { get; private set; }
        public int LastMinimumBars { get; private set; }

        public Task<TickerMarketState?> GetTickerStateAsync(
            string symbol,
            IReadOnlyCollection<string> requiredTimeframes,
            int minimumBarsPerTimeframe,
            DateTimeOffset asOfUtc,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastMinimumBars = minimumBarsPerTimeframe;
            return Task.FromResult<TickerMarketState?>(null);
        }
    }

    [Fact]
    public void RiskBudget_IsEquityTimesTheProfilePercentOrUnknown()
    {
        Assert.Equal(500m, DeskTicketPlanService.RiskBudget(50_000m, 1m));
        Assert.Null(DeskTicketPlanService.RiskBudget(null, 1m));
        Assert.Null(DeskTicketPlanService.RiskBudget(50_000m, null));
        Assert.Null(DeskTicketPlanService.RiskBudget(50_000m, 0m));
    }

    private static DeskTicketPlan Plan(StrategyDefinition strategy, decimal limit, decimal? setupAtr, decimal executionVwap)
    {
        var setupBars = Enumerable.Range(0, 3).Select(index => Bar("1d", index)).ToArray();
        var setupSnapshots = Enumerable.Range(0, 3).Select(index => Snapshot("1d", index, setupAtr, 100m)).ToArray();
        var executionBars = Enumerable.Range(0, 3).Select(index => Bar("1h", index)).ToArray();
        var executionSnapshots = Enumerable.Range(0, 3).Select(index => Snapshot("1h", index, 2m, executionVwap)).ToArray();
        return DeskTicketPlanService.Build(strategy, limit, setupBars, setupSnapshots, executionBars, executionSnapshots, riskBudget: null);
    }

    private static OhlcvBar Bar(string timeframe, int index) => new(
        Ticker: "TEST",
        Timestamp: DateTimeOffset.Parse("2026-09-21T13:30:00Z").AddHours(index),
        Timeframe: timeframe,
        Open: 100m,
        High: 101m,
        Low: 99m,
        Close: 100m,
        Volume: 10_000m);

    private static IndicatorSnapshot Snapshot(string timeframe, int index, decimal? atr, decimal vwap) => new(
        Ticker: "TEST",
        Timestamp: DateTimeOffset.Parse("2026-09-21T13:30:00Z").AddHours(index),
        Timeframe: timeframe,
        CurrentPrice: 100m,
        CurrentVolume: 10_000m,
        Vwap: vwap,
        Rsi: 50m,
        Atr: atr,
        Ema20: 100m,
        Ema50: 99m,
        Ema200: 95m,
        BollingerMiddle: 100m,
        BollingerUpper: 102m,
        BollingerLower: 98m,
        RelativeVolume: 1m,
        MacdLine: 1m,
        MacdSignal: 0.5m,
        MacdHistogram: 0.5m);

    private static StrategyDefinition Strategy(
        string stopMode,
        decimal stopAtrMultiple,
        decimal targetR,
        string targetMode = "r_multiple") =>
        new(
            "strategy.ticket-plan-test",
            "Ticket Plan Test",
            "unit-test",
            1,
            "1d",
            "long",
            new EntryRules(
                SetupType: "momentum",
                MinVolumeSpike: 1m,
                MinEntryRsi: 0m,
                MaxEntryRsi: 100m,
                TrendFilter: "none",
                MacdFilter: "none",
                RequirePriceAboveBollingerMiddle: false,
                RequireMacdHistogramPositive: false,
                RequirePriceAboveVwap: false,
                RequirePriceAboveEma20: false,
                RequirePriceAboveEma50: false,
                RequireEma20AboveEma50: false,
                MaxVwapExtensionAtr: null,
                RecentHighLookbackBars: 8,
                VolatilityContractionLookbackBars: 10),
            new ConfluenceRules(false, "5m", 50, "none"),
            new ExitRules(
                StopAtrMultiple: stopAtrMultiple,
                TargetRMultiple: targetR,
                MaxHoldHours: 48m,
                EnableAtrTrailingStop: false,
                TrailingStopAtrMultiple: 1m,
                TrailingActivationR: 1m,
                ExitOnCloseBelowEma20: false,
                ExitOnCloseBelowVwap: false,
                ExitOnMacdHistogramNegative: false,
                MinHoldBarsBeforeTechnicalExit: 1,
                InitialStopMode: stopMode,
                ProfitTargetMode: targetMode),
            new ExecutionRules("1h", 0m),
            new SessionRules("America/New_York", 0, 0, 0, false, true));
}
