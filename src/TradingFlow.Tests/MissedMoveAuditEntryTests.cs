using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Tests;

public sealed class MissedMoveAuditEntryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // An admitted fill counts at the peak slice's receipt whether its holding closes or remains open.
    public void MissedMoveEntries_IncludeActualOpenAndClosedEntryFills(bool closed)
    {
        var strategy = Strategy();
        var bars = Bars();
        var entryTime = bars[1].Timestamp.AddDays(1);
        var fill = new BacktestPortfolioFill("admitted", strategy.StrategyId, "MRVL", true, true,
            3, 10, 1, entryTime, new DateOnly(2026, 10, 19));
        var state = State([fill]) with
        {
            ClosedTradeCount = closed ? 1 : 0,
            OpenPositions = closed ? [] : [new("admitted", strategy.StrategyId, "MRVL", 3, 10, 12, entryTime, 6)]
        };

        var audit = Assert.Single(BacktestRunner.BuildMissedMoveAudits([strategy], [], bars, state));

        Assert.Contains(strategy.StrategyName, audit.StrategiesWithEntries);
        Assert.Empty(audit.StrategiesWithoutEntries);
    }

    [Fact]
    // A working admission with no fill and a rejected candidate cannot become exposure in the diagnostic.
    public void MissedMoveEntries_KeepUnfilledAdmissionsAndRejectedCandidatesMissing()
    {
        var strategy = Strategy();
        var bars = Bars();
        var state = State([]) with
        {
            AdmittedOrderCount = 1,
            FilledCandidateCount = 0,
            AdmittedOrders = [new("unfilled", strategy.StrategyId, "MRVL", 3, 10, "long", bars[1].Timestamp)],
            PendingOrders = [new("unfilled", strategy.StrategyId, "MRVL", "entry", "Buy", "Limit", 3,
                "working", 30, bars[1].Timestamp)]
        };

        var unfilled = Assert.Single(BacktestRunner.BuildMissedMoveAudits([strategy], [], bars, state));
        var rejected = Assert.Single(BacktestRunner.BuildMissedMoveAudits([strategy], [], bars,
            State([]) with { AdmittedOrderCount = 0, FilledCandidateCount = 0 }));

        Assert.Empty(unfilled.StrategiesWithEntries);
        Assert.Contains(strategy.StrategyName, unfilled.StrategiesWithoutEntries);
        Assert.Empty(rejected.StrategiesWithEntries);
    }

    [Fact]
    // Only positive entry fills count; an exit in the window cannot masquerade as a new entry.
    public void MissedMoveEntries_DoNotCountExitOrZeroQuantityFills()
    {
        var strategy = Strategy();
        var bars = Bars();
        var time = bars[1].Timestamp.AddDays(1);
        var exit = new BacktestPortfolioFill("old", strategy.StrategyId, "MRVL", false, false,
            3, 12, 1, time, new DateOnly(2026, 10, 19));
        var zero = exit with { CandidateId = "unfilled", IsBuy = true, IsEntry = true, Quantity = 0 };

        var audit = Assert.Single(BacktestRunner.BuildMissedMoveAudits([strategy], [], bars, State([exit, zero])));

        Assert.Empty(audit.StrategiesWithEntries);
    }

    [Fact]
    // A supplied native snapshot is authoritative; completed records cannot replace an absent actual fill.
    public void MissedMoveEntries_DoNotFallBackWhenFillSnapshotExists()
    {
        var strategy = Strategy();
        var bars = Bars();
        var time = bars[1].Timestamp.AddDays(1);
        var trade = new BacktestTrade("MRVL", strategy.StrategyName, "long", time, time.AddDays(1),
            3, 10, 12, 9, 15, "technical_exit", 6, 2, 4);

        var audit = Assert.Single(BacktestRunner.BuildMissedMoveAudits([strategy], [trade], bars, State([])));

        Assert.Empty(audit.StrategiesWithEntries);
    }

    [Fact]
    // Legacy and SIP results without a native fill snapshot keep their actual completed-trade entry evidence.
    public void MissedMoveEntries_PreserveCompletedOnlyResultsAndReceiptBoundary()
    {
        var strategy = Strategy();
        var bars = Bars();
        var time = bars[1].Timestamp.AddDays(1);
        var trade = new BacktestTrade("MRVL", strategy.StrategyName, "long", time, time.AddDays(1),
            3, 10, 12, 9, 15, "technical_exit", 6, 2, 4);

        var filled = Assert.Single(BacktestRunner.BuildMissedMoveAudits([strategy], [trade], bars));
        var outsideWindow = Assert.Single(BacktestRunner.BuildMissedMoveAudits([strategy],
            [trade with { EntryTimestamp = time.AddTicks(1) }], bars));

        Assert.Contains(strategy.StrategyName, filled.StrategiesWithEntries);
        Assert.Empty(outsideWindow.StrategiesWithEntries);
    }

    // Minimal source bars reproduce an October move; execution receipts remain one daily slice later.
    private static OhlcvBar[] Bars() =>
    [
        new("MRVL", DateTimeOffset.Parse("2026-10-16T04:00:00Z"), "1d", 10, 11, 9, 10, 1000),
        new("MRVL", DateTimeOffset.Parse("2026-10-19T04:00:00Z"), "1d", 12, 13, 11, 12, 1000)
    ];

    // Reuse a registered definition so strategy-id to display-name resolution matches normal results.
    private static StrategyDefinition Strategy() => new TradingFlow.Engine.Configuration.SimpleYamlReader()
        .ReadStrategy(RetiredStrategyFixtures.Strategy("swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml"));

    // The snapshot exposes fills independently of completed trades; no candidate hypothesis enters this helper.
    private static BacktestPortfolioState State(IReadOnlyList<BacktestPortfolioFill> fills) => new(
        NativeDailySessionLedger.ValuationBasis, DateTimeOffset.Parse("2026-10-21T04:00:00Z"),
        1, 1, 0, fills, [], [], [], []);
}
