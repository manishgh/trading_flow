using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Risk;
using System.Text.Json;

namespace TradingFlow.Tests;

public sealed partial class IntradayPortfolioExecutionTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
    private static readonly DateOnly Day = new(2026, 6, 1);

    // A committed market entry fills at the next opening even when it gaps through the completed-candle stop.
    [Theory]
    [InlineData("long", 99.0, true)]
    [InlineData("short", 101.0, true)]
    [InlineData("long", 100.2, false)]
    [InlineData("short", 99.8, false)]
    public void NextBarMarket_UsesOpeningGapAndActualRisk(string direction, double price, bool throughStop)
    {
        var bars = Bars();
        var candidate = MarketCandidate(bars, direction: direction);
        for (var i = 4; i < bars.Length; i++) bars[i] = Bar(i * 5, (decimal)price, (decimal)price, (decimal)price, (decimal)price);
        var result = Run(bars, [candidate]);
        var trade = Assert.Single(result.Trades);
        Assert.Equal((decimal)price, trade.EntryPrice);
        Assert.Equal(Start.AddMinutes(20), trade.EntryTimestamp);
        Assert.Equal(throughStop ? "gap_fill_through_structural_stop" : "session_deadline", trade.ExitReason);
        if (throughStop) Assert.Equal(trade.EntryTimestamp, trade.ExitTimestamp);
        else Assert.Equal((decimal)price + (direction == "long" ? 1 : -1) * 3 * Math.Abs((decimal)price - candidate.StopLossPrice), trade.TakeProfitPrice);
        Assert.Empty(result.Failures);
    }

    // Opening quantity comes from the completed source bar; the same committed remainder fills later without a fresh breakout.
    [Fact]
    public void NextBarMarket_PartialRemainderUsesLaggedVolumeAndSingleParent()
    {
        var bars = Bars();
        bars[3] = Bar(15, 100, 100.5m, 99.5m, 100, 50);
        bars[4] = Bar(20, 100, 100.2m, 99.6m, 100, 10000);
        var result = Run(bars, [MarketCandidate(bars), MarketCandidate(bars, "duplicate", minute: 25)], Portfolio(10));
        var entries = result.PortfolioState.Fills.Where(x => x.IsEntry).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(5, entries[0].Quantity);
        Assert.Equal(Start.AddMinutes(25), entries[1].TimestampUtc);
        Assert.Single(result.PortfolioState.AdmittedOrders);
        Assert.Single(result.Trades);
        Assert.Empty(result.Failures);
    }

    // Zero opening capacity retains the committed order; a later opening fills below the long trigger without reselection.
    [Fact]
    public void NextBarMarket_ZeroFirstCapacityRetainsUnconditionalRemainder()
    {
        var bars = Bars();
        bars[3] = Bar(15, 100, 100.5m, 99.5m, 100, 0);
        bars[4] = Bar(20, 100, 100.2m, 99.6m, 100, 1000);
        bars[5] = Bar(25, 99.7m, 100, 99.6m, 99.8m);
        var result = Run(bars, [MarketCandidate(bars)], Portfolio(10));
        var fill = Assert.Single(result.PortfolioState.Fills, x => x.IsEntry);
        Assert.Equal(Start.AddMinutes(25), fill.TimestampUtc);
        Assert.Equal(99.7m, fill.Price);
        Assert.Single(result.PortfolioState.AdmittedOrders);
        Assert.Empty(result.Failures);
    }

    // An opening fill receives adverse protection inside its own interval when both original exits trade.
    [Fact]
    public void NextBarMarket_EntryIntervalChoosesStopBeforeTarget()
    {
        var bars = Bars();
        bars[4] = Bar(20, 100, 102, 99, 100);
        var result = Run(bars, [MarketCandidate(bars)]);
        var trade = Assert.Single(result.Trades);
        Assert.Equal("stop_loss", trade.ExitReason);
        Assert.Equal(Start.AddMinutes(20), trade.EntryTimestamp);
        Assert.Equal(Start.AddMinutes(25), trade.ExitTimestamp);
        Assert.Equal(99.5m, trade.ExitPrice);
    }

    // Invalid source clocks, a future receipt or a substituted candle stop cannot enter the replay ledger.
    [Theory]
    [InlineData("clock")]
    [InlineData("receipt")]
    [InlineData("stop")]
    [InlineData("trigger")]
    public void NextBarMarket_RejectsMalformedCompletedSignal(string mutation)
    {
        var bars = Bars();
        var candidate = MarketCandidate(bars);
        if (mutation == "clock") candidate = candidate with { IntradayExecution = candidate.IntradayExecution! with { ConfirmationTimestampUtc = Start.AddMinutes(10) } };
        if (mutation == "receipt") bars[3] = bars[3] with { KnownAtUtc = Start.AddMinutes(25) };
        if (mutation == "stop") candidate = candidate with { StopLossPrice = 99.4m };
        if (mutation == "trigger") candidate = candidate with { IntradayExecution = candidate.IntradayExecution! with { BreakoutTriggerPrice = 99.98m } };
        var result = Run(bars, [candidate]);
        Assert.Empty(result.PortfolioState.Fills);
        Assert.False(Assert.Single(result.Dispositions).Accepted);
    }

    // Risk rejection consumes the original outside signal; subsequent outside closes cannot choose a more convenient stop.
    [Fact]
    public void NextBarMarket_RejectedSignalRequiresFreshInsideRearm()
    {
        var bars = Bars();
        var first = MarketCandidate(bars);
        var next = MarketCandidate(bars, "later", minute: 25);
        var result = Run(bars, [first, next], Portfolio() with { AccountRiskBudgetPct = 0.000001m });
        Assert.Empty(result.Trades);
        Assert.Contains(result.Dispositions, x => x.CandidateId == "later" && x.Reason == "completed_return_inside_opening_range_required");
    }

    // Resolution requires a later completed inside close, then a distinct completed breakout before the second filled parent.
    [Fact]
    public void NextBarMarket_RearmsOnlyAfterResolutionAndLimitsFilledParents()
    {
        var bars = Enumerable.Range(0, 11).Select(i => Bar(i * 5, 100, 100.5m, 99.5m, 100)).ToArray();
        bars[4] = Bar(20, 100, 100.5m, 99, 99.5m);
        bars[5] = Bar(25, 99.5m, 99.8m, 99.2m, 99.5m);
        bars[6] = Bar(30, 100, 100.5m, 99.5m, 100);
        bars[7] = Bar(35, 100, 100.5m, 99, 99.5m);
        var strategy = Strategy(0.000000001m);
        var candidates = new[] { MarketCandidate(bars), MarketCandidate(bars, "early", minute: 25),
            MarketCandidate(bars, "second", minute: 35), MarketCandidate(bars, "third", minute: 45) }
            .Select(x => x with { StrategyId = strategy.StrategyId, IntradayExecution = x.IntradayExecution! with {
                SessionDeadlineUtc = Start.AddMinutes(50), ExpiresAtUtc = Start.AddMinutes(50) } }).ToArray();
        var sessions = new Dictionary<DateOnly, MarketSessionSchedule> { [Day] = new(Day, true, new(9, 30), new(10, 25)) };
        var result = IntradayPortfolioExecution.Replay(new(Portfolio(), [strategy], candidates, bars, sessions, Start, Start.AddMinutes(55)));
        Assert.Equal(2, result.Trades.Count);
        Assert.Contains(result.Dispositions, x => x.CandidateId == "third" && x.Reason == "filled_parent_session_limit_reached");
        Assert.Contains(result.Dispositions, x => x.CandidateId == "early" && x.Reason == "completed_return_inside_opening_range_required");
        Assert.Empty(result.Failures);
    }

    // A stop trigger fixes the submitted shares, while actual gap price fixes original R and its outward-rounded target.
    [Theory]
    [InlineData("long", 100.2, 103.8)]
    [InlineData("short", 99.8, 96.2)]
    public void StopMarketGapUsesActualFillForOriginalRisk(string direction, double opening, double target)
    {
        var price = (decimal)opening;
        var bars = Bars();
        for (var index = 3; index < bars.Length; index++) bars[index] = Bar(index * 5, price, price, price, price);
        var result = Run(bars, [StopCandidate(direction: direction)]);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(price, trade.EntryPrice);
        Assert.Equal((decimal)target, trade.TakeProfitPrice);
        Assert.Equal(Start.AddMinutes(15), trade.EntryTimestamp);
        Assert.Equal(Assert.Single(result.PortfolioState.AdmittedOrders).RequestedQuantity, trade.ShareQuantity);
        Assert.Empty(result.Failures);
    }

    // A deeply favorable short entry gap can make 3R nonpositive; keep the real shares and explicitly reduce that unsupported exposure.
    [Fact]
    public void ShortGapWithNonpositiveActualThreeRTargetExplicitlyReducesRisk()
    {
        var bars = Bars();
        bars[3] = Bar(15, 75, 75, 75, 75);
        var result = Run(bars, [StopCandidate(direction: "short")]);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(75, trade.EntryPrice);
        Assert.Equal(75, trade.ExitPrice);
        Assert.Equal(0, trade.TakeProfitPrice);
        Assert.Equal("actual_fill_three_r_target_not_positive", trade.ExitReason);
        Assert.Equal(Assert.Single(result.PortfolioState.AdmittedOrders).RequestedQuantity, trade.ShareQuantity);
        Assert.Contains(result.Events, item => item.State == "risk_reduction_pending" && item.Reason == trade.ExitReason);
    }

    // A once-triggered partial remainder remains marketable below its original buy stop and opening capacity ignores final-bar volume.
    [Fact]
    public void StopMarketPartialRemainderPersistsWithoutAnotherCrossAndUsesLaggedOpeningVolume()
    {
        var bars = Bars();
        bars[2] = Bar(10, 99.5m, 99.8m, 99.2m, 99.5m, 50);
        bars[3] = Bar(15, 100, 100.2m, 99.5m, 100, 10000);
        for (var index = 4; index < bars.Length; index++) bars[index] = Bar(index * 5, 99.5m, 99.8m, 99.2m, 99.5m);
        var result = Run(bars, [StopCandidate()], Portfolio(10));
        var entries = result.PortfolioState.Fills.Where(fill => fill.IsEntry).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(5, entries[0].Quantity);
        Assert.Equal(99.5m, entries[1].Price);
        Assert.Equal(Start.AddMinutes(20), entries[1].TimestampUtc);
        Assert.Equal(Assert.Single(result.PortfolioState.AdmittedOrders).RequestedQuantity, entries.Sum(fill => fill.Quantity));
        Assert.Single(result.Events, item => item.State == "entry_triggered");
        Assert.Empty(result.Failures);
    }

    // A later low cannot cancel market shares that were already executable at the safe opening of that interval.
    [Fact]
    public void TriggeredMarketRemainderFillsAtOpeningBeforeLaterIntrabarStop()
    {
        var bars = Bars();
        bars[2] = Bar(10, 99.5m, 99.8m, 99.2m, 99.5m, 50);
        bars[3] = Bar(15, 100, 100.2m, 99.5m, 100);
        bars[4] = Bar(20, 100, 100.5m, 98, 99.5m);
        var result = Run(bars, [StopCandidate()], Portfolio(10));
        var entries = result.PortfolioState.Fills.Where(fill => fill.IsEntry).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(Start.AddMinutes(20), entries[1].TimestampUtc);
        Assert.Equal(Assert.Single(result.PortfolioState.AdmittedOrders).RequestedQuantity, entries.Sum(fill => fill.Quantity));
        Assert.Equal("stop_loss", Assert.Single(result.Trades).ExitReason);
        Assert.Equal(Start.AddMinutes(25), result.Trades[0].ExitTimestamp);
    }

    // A just-decided stop cannot cancel an opening market remainder before the exact boundary's older protection has had first claim.
    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public void OpeningBetweenOldAndJustActivatedStopFillsMarketRemainderBeforeUpdatedStop(string direction)
    {
        var isLong = direction == "long";
        var bars = Bars();
        bars[2] = Bar(10, 100, 100.5m, 99.5m, 100, 50);
        bars[3] = isLong ? Bar(15, 100, 102.5m, 99.5m, 102.4m)
            : Bar(15, 100, 100.5m, 97.5m, 97.6m);
        var opening = isLong ? 100.01m : 99.99m;
        bars[4] = isLong ? Bar(20, opening, 100.4m, 99.8m, 100.2m)
            : Bar(20, opening, 100.2m, 99.6m, 99.8m);
        var result = Run(bars, [StopCandidate(direction: direction)], Portfolio(10));
        var activation = Assert.Single(result.Events, item => item.State == "protection_updated");
        Assert.True(isLong ? opening > 99 && opening < activation.StopPrice
            : opening < 101 && opening > activation.StopPrice);
        var entries = result.PortfolioState.Fills.Where(fill => fill.IsEntry).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(opening, entries[1].Price);
        Assert.Equal(Start.AddMinutes(20), entries[1].TimestampUtc);
        Assert.Equal(Assert.Single(result.PortfolioState.AdmittedOrders).RequestedQuantity, entries.Sum(fill => fill.Quantity));
        Assert.Equal("stop_loss", Assert.Single(result.Trades).ExitReason);
        Assert.Equal(Start.AddMinutes(25), result.Trades[0].ExitTimestamp);
        Assert.Empty(result.Failures);
    }

    // Further actual parent fills rebase risk but retain a stop already earned from a completed 2.4R close.
    [Fact]
    public void LaterPartialEntryCannotEraseActivatedCostCoveredStop()
    {
        var bars = Bars();
        bars[2] = Bar(10, 99.5m, 99.8m, 99.2m, 99.5m, 50);
        bars[3] = Bar(15, 100, 102.5m, 99.5m, 102.4m);
        bars[4] = Bar(20, 102.4m, 102.8m, 102.1m, 102.6m);
        bars[5] = Bar(25, 101, 101.5m, 99.5m, 100.5m);
        var result = Run(bars, [StopCandidate()], Portfolio(10));
        var activation = Assert.Single(result.Events, item => item.State == "protection_updated");
        var trade = Assert.Single(result.Trades);
        Assert.Equal("stop_loss", trade.ExitReason);
        Assert.True(trade.ExitPrice >= activation.StopPrice);
        Assert.True(trade.ExitPrice > trade.StopLossPrice);
        Assert.Equal(2, result.PortfolioState.Fills.Count(fill => fill.IsEntry));
    }

    // Unknown intrabar ordering never awards a target already traversed before the stop entry was observed.
    [Fact]
    public void IntrabarStopEntryCannotTakeFavorableSameBarTarget()
    {
        var bars = Bars();
        bars[3] = Bar(15, 99.5m, 104, 99.2m, 100);
        var result = Run(bars, [StopCandidate()]);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(Start.AddMinutes(20), trade.EntryTimestamp);
        Assert.Equal("session_deadline", trade.ExitReason);
        Assert.DoesNotContain(result.FillEvents, fill => fill.Event.Reason == "take_profit");
    }

    // Ambiguous entry/stop/target extremes retain the adverse stop after the completed entry rather than an earlier opening exit.
    [Fact]
    public void IntrabarStopEntryAndBothExitsRetainsAdversePostEntryStop()
    {
        var bars = Bars();
        bars[3] = Bar(15, 99.5m, 104, 98, 100);
        var result = Run(bars, [StopCandidate()]);
        var trade = Assert.Single(result.Trades);
        Assert.Equal("stop_loss", trade.ExitReason);
        Assert.Equal(99, trade.ExitPrice);
        Assert.Equal(Start.AddMinutes(20), trade.EntryTimestamp);
        Assert.Equal(trade.EntryTimestamp, trade.ExitTimestamp);
        Assert.True(result.FillEvents[1].ExecutionSequence > result.FillEvents[0].ExecutionSequence);
    }

    // A second filled parent requires resolution, a later completed close inside, and another executable crossing; a third is refused.
    [Fact]
    public void StopMarketAllowsTwoFilledParentsOnlyAfterCompletedReturnInside()
    {
        var bars = Bars();
        bars[3] = Bar(15, 100, 100.5m, 98, 99.5m);
        bars[4] = Bar(20, 99.5m, 99.8m, 99.2m, 99.5m);
        bars[5] = Bar(25, 100, 100.5m, 98, 99.5m);
        var result = Run(bars, [StopCandidate(), StopCandidate("too-soon", minute: 20),
            StopCandidate("second", minute: 25), StopCandidate("third", minute: 35)]);
        Assert.Equal(2, result.Trades.Count);
        Assert.Contains(result.Dispositions, item => item.CandidateId == "too-soon" &&
            item.Reason == "completed_return_inside_opening_range_required");
        Assert.Contains(result.Dispositions, item => item.CandidateId == "third" &&
            item.Reason == "filled_parent_session_limit_reached");
        Assert.Equal(2, result.PortfolioState.Fills.Count(fill => fill.IsEntry));
        Assert.Empty(result.Failures);
    }

    // Provider history may omit receipt stamps: completed bars can rearm, while an explicitly future-known inside close cannot.
    [Theory]
    [InlineData("long", false)]
    [InlineData("short", false)]
    [InlineData("long", true)]
    [InlineData("short", true)]
    public void StopMarketRearmAcceptsNullKnownAtButRejectsExplicitFutureKnownAt(string direction, bool futureInsideBar)
    {
        var isLong = direction == "long";
        var bars = Bars().Select(bar => bar with { KnownAtUtc = null }).ToArray();
        bars[3] = (isLong ? Bar(15, 100, 100.5m, 98, 99.5m)
            : Bar(15, 100, 102, 99.5m, 100.5m)) with { KnownAtUtc = null };
        bars[4] = (isLong ? Bar(20, 99.5m, 99.8m, 99.2m, 99.5m)
            : Bar(20, 100.5m, 100.8m, 100.2m, 100.5m)) with
        { KnownAtUtc = futureInsideBar ? Start.AddMinutes(30) : null };
        bars[5] = (isLong ? Bar(25, 100, 100.5m, 98, 100)
            : Bar(25, 100, 102, 99.5m, 100)) with { KnownAtUtc = null };
        var result = Run(bars, [StopCandidate(direction: direction),
            StopCandidate("second", direction, 25), StopCandidate("third", direction, 35)]);
        Assert.Equal(futureInsideBar ? 1 : 2, result.Trades.Count);
        Assert.Equal(futureInsideBar ? 1 : 2, result.PortfolioState.Fills.Count(fill => fill.IsEntry));
        var second = Assert.Single(result.Dispositions, item => item.CandidateId == "second");
        Assert.Equal(!futureInsideBar, second.Accepted);
        Assert.Contains(result.Dispositions, item => item.CandidateId == "third" && item.Reason ==
            (futureInsideBar ? "completed_return_inside_opening_range_required" : "filled_parent_session_limit_reached"));
        Assert.Empty(result.Failures);
    }

    // Observing an inside close only rearms a pending stop; it does not manufacture a second entry without a subsequent crossing.
    [Fact]
    public void ReturnedInsideRequiresFreshStopCrossForSecondFill()
    {
        var bars = Bars();
        bars[3] = Bar(15, 100, 100.5m, 98, 99.5m);
        for (var index = 4; index < bars.Length; index++) bars[index] = Bar(index * 5, 99.5m, 99.8m, 99.2m, 99.5m);
        var result = Run(bars, [StopCandidate(), StopCandidate("second", minute: 25)]);
        Assert.Single(result.Trades);
        Assert.Contains(result.Dispositions, item => item.CandidateId == "second" && item.Accepted);
        Assert.Contains(result.Events, item => item.CandidateId == "second" && item.State == "resolved" && item.Reason == "expired_without_fill");
    }

    // Preserve confirmation evidence as decisions only; the first executable interval follows C's completed close.
    [Fact]
    public void ConfirmationBarCannotFill_ThenDeadlineUsesOpeningPrice()
    {
        var bars = Bars();
        bars[1] = Bar(5, 100, 200, 1, 100);
        bars[8] = Bar(40, 101, 500, 1, 250);
        var result = Run(bars);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(Start.AddMinutes(10), trade.EntryTimestamp);
        Assert.Equal(Start.AddMinutes(40), trade.ExitTimestamp);
        Assert.Equal(101, trade.ExitPrice);
        Assert.Equal("session_deadline", trade.ExitReason);
        Assert.Empty(result.Failures);
    }

    // The limit policy refuses a simple intrabar touch, even with abundant retrospective volume.
    [Theory]
    [InlineData("long", 100.1, 100.2, 100)]
    [InlineData("short", 99.9, 100, 99.8)]
    public void TouchAloneExpiresWithoutFill(string direction, double open, double high, double low)
    {
        var bars = Bars();
        bars[2] = Bar(10, (decimal)open, (decimal)high, (decimal)low, (decimal)open);
        var result = Run(bars, [Candidate(direction: direction)]);
        Assert.Empty(result.Trades);
        Assert.Empty(result.PortfolioState.Fills);
        Assert.Contains(result.Events, x => x.Reason == "limit_touch_without_one_tick_penetration");
        Assert.Contains(result.Events, x => x.State == "entry_remainder_canceled");
    }

    // OHLC cannot establish entry versus stop order; retaining the entry and adverse stop avoids optimistic trade erasure.
    [Theory]
    [InlineData("long", 104, 98)]
    [InlineData("short", 102, 96)]
    public void EntryStopAndTargetInOneBarRetainsAdverseStop(string direction, int high, int low)
    {
        var bars = Bars();
        bars[2] = Bar(10, 100, high, low, 100);
        var result = Run(bars, [Candidate(direction: direction)]);
        var trade = Assert.Single(result.Trades);
        Assert.Equal("stop_loss", trade.ExitReason);
        Assert.Equal(direction == "long" ? 99 : 101, trade.ExitPrice);
        Assert.True(trade.NetProfit < 0);
        Assert.Empty(result.Failures);
    }

    // A pre-existing limit remains a real fill when an opening gap crosses its structural stop.
    [Theory]
    [InlineData("long", 98)]
    [InlineData("short", 102)]
    public void GapThroughOriginalStopRetainsFillAndImmediateAdverseLiquidation(string direction, int opening)
    {
        var bars = Bars();
        bars[2] = Bar(10, opening, opening, opening, opening);
        var result = Run(bars, [Candidate(direction: direction)]);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(opening, trade.EntryPrice);
        Assert.Equal(opening, trade.ExitPrice);
        Assert.Equal(0, trade.TakeProfitPrice);
        Assert.Equal("gap_fill_through_structural_stop", trade.ExitReason);
        Assert.Equal(trade.EntryTimestamp, trade.ExitTimestamp);
        Assert.Equal(2, result.PortfolioState.Fills.Count);
    }

    // Model one shared volume pool, protect actual partial entry, then keep a triggered stop working after capacity is exhausted.
    [Fact]
    public void PartialEntryProtectsImmediatelyAndStopContinuesAfterRebound()
    {
        var bars = Bars();
        bars[2] = Bar(10, 100, 100, 98, 100, 50);
        bars[3] = Bar(15, 100.5m, 100.5m, 100, 100.5m);
        var result = Run(bars, portfolio: Portfolio(10));
        var trade = Assert.Single(result.Trades);
        Assert.Equal(5, trade.ShareQuantity);
        Assert.Equal(100.5m, trade.ExitPrice);
        Assert.Equal("partial_fill_remainder_canceled", trade.EntryFillStatus);
        Assert.Contains(result.Events, x => x.State == "entry_remainder_canceled" && x.Quantity > 0);
        Assert.Empty(result.Failures);
    }

    // Both fixed sides and sell-side regulatory fees apply to a short and its cover; proceeds do not create equity.
    [Fact]
    public void ShortCashAndEquityReconcileAfterCoverAndBothSideCosts()
    {
        var bars = Bars();
        bars[8] = Bar(40, 99.5m, 99.5m, 99.5m, 99.5m);
        var portfolio = Portfolio() with { FixedBuyFee = 1, FixedSellFee = 2, SecFeeRate = 0.001m };
        var result = Run(bars, [Candidate(direction: "short")], portfolio);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(1 + 2 + trade.ShareQuantity * 100 * 0.001m, trade.Fees);
        var observation = Assert.Single(result.PortfolioState.Sessions);
        Assert.Equal(10000 + trade.NetProfit, observation.Equity);
        Assert.Equal(observation.Equity, observation.Cash);
        Assert.Contains(result.Events, x => x.Reason.Contains("historical_borrow_unproved"));
    }

    // A completed close updates protection only after existing orders; an old target fill prevents later extension.
    [Fact]
    public void ExistingTargetFillPreventsContinuationUpdate()
    {
        var bars = Bars();
        bars[2] = Bar(10, 100, 102.5m, 99.5m, 102.4m);
        bars[3] = Bar(15, 102.4m, 103.2m, 102, 103.1m);
        var result = Run(bars);
        Assert.Equal("take_profit", Assert.Single(result.Trades).ExitReason);
        Assert.Single(result.Events, x => x.State == "protection_updated");
    }

    // A wick does not activate the cost-covered stop, so the next original stop is still the executable protection.
    [Fact]
    public void ActivationRequiresCompletedCloseAndCannotEraseSameBarStop()
    {
        var bars = Bars();
        bars[2] = Bar(10, 100, 102.8m, 98, 102.4m);
        var result = Run(bars);
        Assert.Equal("stop_loss", Assert.Single(result.Trades).ExitReason);
        Assert.DoesNotContain(result.Events, x => x.State == "protection_updated");
    }

    // Static candidate generation cannot recycle a crossing observed during the prior live order or position.
    [Fact]
    public void CrossingDuringActivePositionCannotRearmAfterItsResolution()
    {
        var first = Candidate();
        var second = Candidate("second", minute: 20);
        var result = Run(Bars(), [first, second]);
        Assert.Single(result.Trades);
        Assert.Contains(result.Dispositions, x => x.CandidateId == "second" && !x.Accepted &&
            x.Reason == "crossing_occurred_before_previous_order_resolved");
    }

    // Final-bar volume and close are future evidence at its opening; lagged completed volume caps this diagnostic liquidation.
    [Fact]
    public void DeadlineCapacityUsesPriorCompletedBarAndPreservesUnresolvedRemainder()
    {
        var bars = Bars();
        bars[7] = Bar(35, 100, 100.5m, 99.5m, 100, 10);
        bars[8] = Bar(40, 100, 1000, 1, 900, 1000000);
        var result = Run(bars, portfolio: Portfolio(10));
        Assert.Empty(result.Trades);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(1, failure.ExitFilledQuantity);
        Assert.True(failure.OpenSignedQuantity > 0);
        Assert.Null(Assert.Single(result.PortfolioState.Sessions).Equity);
        Assert.Equal(Day, result.PortfolioState.UnavailableFromSession);
        Assert.Contains(result.Events, x => x.Reason.Contains("diagnostic_lagged_completed_bar_volume_capacity"));
    }

    // Without the exact final opening there is no executable session exit; the next available candle cannot stand in for it.
    [Fact]
    public void MissingDeadlineOpeningLeavesExposureAndSuppressesMonthlyEquity()
    {
        var result = Run(Bars().Where(x => x.Timestamp != Start.AddMinutes(40)).ToArray());
        Assert.Empty(result.Trades);
        Assert.Contains(result.Failures, x => x.Reason == "session_deadline_opening_price_missing");
        Assert.Single(result.PortfolioState.OpenPositions);
        Assert.Null(Assert.Single(result.PortfolioState.Sessions).Equity);
    }

    // Missing a required active interval makes its price path unknown; later bars cannot prove the position survived it.
    [Fact]
    public void MissingProtectiveIntervalDoesNotFabricateLaterFlatTrade()
    {
        var result = Run(Bars().Where(x => x.Timestamp != Start.AddMinutes(20)).ToArray());
        Assert.Empty(result.Trades);
        Assert.Contains(result.Failures, x => x.Reason == "required_five_minute_execution_bar_missing");
        Assert.Single(result.PortfolioState.OpenPositions);
    }

    // Calendar-defined early closes set the deadline; no hard-coded afternoon clock is used by execution.
    [Fact]
    public void DeadlineMustMatchAuthoritativeCalendar()
    {
        var candidate = Candidate() with { IntradayExecution = Candidate().IntradayExecution! with { SessionDeadlineUtc = Start.AddMinutes(35) } };
        var result = Run(Bars(), [candidate]);
        Assert.Empty(result.Trades);
        Assert.Contains(result.Dispositions, x => !x.Accepted && x.Reason == "invalid_or_stale_intraday_order_clock");
    }

    // The favorable extreme may precede an intrabar pullback entry; only the adverse stop sequence is retained without ordered events.
    [Theory]
    [InlineData("long", 102, 104, 99.99, 102)]
    [InlineData("short", 98, 100.01, 96, 98)]
    public void IntrabarEntryCannotReceiveEarlierAmbiguousFavorableTarget(string direction,
        double open, double high, double low, double close)
    {
        var bars = Bars();
        bars[2] = Bar(10, (decimal)open, (decimal)high, (decimal)low, (decimal)close);
        var result = Run(bars, [Candidate(direction: direction)]);
        Assert.Equal("session_deadline", Assert.Single(result.Trades).ExitReason);
        Assert.DoesNotContain(result.PortfolioState.Fills, x => !x.IsEntry && x.TimestampUtc == Start.AddMinutes(15));
    }

    // A later stop is the actual final exit even after an earlier target reduced part of the position.
    [Fact]
    public void PartialTargetThenStopRetainsFinalAdverseExitReason()
    {
        var bars = Bars();
        bars[2] = Bar(10, 100, 100.5m, 99.5m, 100, 50);
        bars[3] = Bar(15, 102.5m, 103, 102, 102.5m, 20);
        bars[4] = Bar(20, 100, 100.5m, 99, 100);
        var result = Run(bars, portfolio: Portfolio(10));
        Assert.Equal("stop_loss", Assert.Single(result.Trades).ExitReason);
        Assert.Equal(3, result.PortfolioState.Fills.Count);
        foreach (var group in result.FillEvents.GroupBy(x => x.Event.ClientOrderId))
            Assert.Equal(group.Sum(x => x.Event.LastFillQuantity), group.Last().Event.CumulativeFillQuantity);
    }

    // A gap cannot retroactively resize an already routed short; retain its real fill and reduce the excess exposure immediately.
    [Fact]
    public void ShortNotionalGapRetainsFrozenQuantityBeforeRiskReduction()
    {
        var bars = Bars();
        bars[2] = Bar(10, 100.5m, 100.5m, 100.5m, 100.5m);
        var result = Run(bars, [Candidate(direction: "short")]);
        var order = Assert.Single(result.PortfolioState.AdmittedOrders);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(order.RequestedQuantity, trade.ShareQuantity);
        Assert.Equal("actual_fill_notional_limit_breach", trade.ExitReason);
    }

    // Artifact publication keeps exact physical fills and one cutoff per admitted parent in the existing evidence contracts.
    [Fact]
    public void EvidencePublicationUsesStandardFillsAndTerminalContracts()
    {
        var result = Run(Bars());
        var order = Assert.Single(result.PortfolioState.AdmittedOrders);
        var candidates = new[] { Candidate() with { StrategyId = order.StrategyId } };
        var hypotheses = new Sink();
        var execution = new Sink();
        IntradayPortfolioEvidence.Publish(hypotheses, execution, candidates, result);
        Assert.Single(hypotheses.Items);
        Assert.All(execution.Items, x => Assert.Equal("portfolio_execution", x.EvidenceScope));
        var exactFills = execution.Items.Where(x => x.State == ExecutionState.OrderFilled)
            .Select(x => JsonSerializer.Deserialize<SimulatedExecutionEvent>(x.EvidenceJson!)!).ToArray();
        Assert.Equal(result.PortfolioState.Fills.Count, exactFills.Length);
        Assert.Equal(result.PortfolioState.Fills.Sum(x => x.Fees), exactFills.Sum(x => x.Fees));
        Assert.Single(execution.Items, x => x.EvidenceJson!.Contains("\"AdmittedOrder\"", StringComparison.Ordinal));
        Assert.Equal(execution.Items.OrderBy(x => x.Timestamp), execution.Items);
    }

    // The shared accounting verifier rejects the old previous-day mapping for a real intraday fill.
    [Fact]
    public void IntradayLedgerRejectsShiftedSourceSessionDate()
    {
        var result = Run(Bars());
        var state = result.PortfolioState with { Fills = result.PortfolioState.Fills.Select(x => x with
            { SourceSessionDate = x.SourceSessionDate.AddDays(-1) }).ToArray() };
        Assert.Throws<InvalidDataException>(() => NativeDailySessionLedger.VerifyRecordedState(10000, state,
            new(Start, Start.AddMinutes(45), Sessions(), true)));
    }

    // A target extended at t cannot cancel the old target that already executes on the observed opening at the same t.
    [Fact]
    public void ExistingOpeningTargetPrecedesJustDecidedExtension()
    {
        var bars = Bars();
        bars[2] = Bar(10, 100, 102.5m, 99.5m, 102.4m);
        bars[3] = Bar(15, 102.4m, 102.7m, 102.1m, 102.5m);
        bars[4] = Bar(20, 102.5m, 102.9m, 102.3m, 102.8m);
        bars[5] = Bar(25, 103.5m, 103.6m, 103.4m, 103.5m);
        var result = Run(bars);
        var trade = Assert.Single(result.Trades);
        Assert.Equal("take_profit", trade.ExitReason);
        Assert.Equal(Start.AddMinutes(25), trade.ExitTimestamp);
        Assert.Equal(103.5m, trade.ExitPrice);
        Assert.Contains(result.Events, x => x.State == "protection_updated" && x.TargetPrice == 104);
    }

    // Partial opening targets leave real residual shares; a later stop activates despite exhausted same-bar liquidity and survives a rebound.
    [Theory]
    [InlineData("long", 104, 104, 98, 100.5)]
    [InlineData("short", 96, 102, 96, 99.5)]
    public void PartialOpeningTargetStillActivatesLaterResidualStop(string direction, double opening,
        double high, double low, double rebound)
    {
        var bars = Bars();
        bars[2] = Bar(10, 100, 100.5m, 99.5m, 100, 50);
        bars[3] = Bar(15, (decimal)opening, (decimal)high, (decimal)low, 100, 20);
        bars[4] = Bar(20, (decimal)rebound, (decimal)rebound, (decimal)rebound, (decimal)rebound);
        var result = Run(bars, [Candidate(direction: direction)], Portfolio(10));
        var trade = Assert.Single(result.Trades);
        Assert.Equal("stop_loss", trade.ExitReason);
        Assert.Equal(Start.AddMinutes(20), trade.ExitTimestamp);
        Assert.Contains(result.Events, x => x.State == "stop_triggered" && x.Quantity == 3);
        Assert.Equal(3, result.PortfolioState.Fills.Count);
    }

    // Freeze the rejected run's actual RDW/PGY bars and account caps; mirror prices to cover both directions without provider calls.
    [Theory]
    [InlineData("RDW", false)]
    [InlineData("RDW", true)]
    [InlineData("PGY", false)]
    [InlineData("PGY", true)]
    public void HistoricalIntrabarEntryCannotLiquidateAtPrecedingOpening(string ticker, bool mirrorLong)
    {
        var rdw = ticker == "RDW";
        var active = DateTimeOffset.Parse(rdw ? "2026-04-27T13:40:00Z" : "2026-04-27T19:10:00Z");
        var limit = rdw ? 9.45m : 13.31m;
        var stop = rdw ? 9.69m : 13.34m;
        var capital = rdw ? 9940.773986m : 9948.360162m;
        var quantity = rdw ? 262 : 186;
        var date = new DateOnly(2026, 4, 27);
        var start = DateTimeOffset.Parse("2026-04-27T13:30:00Z");
        var end = DateTimeOffset.Parse("2026-04-27T20:00:00Z");
        var deadline = end.AddMinutes(-5);
        var bars = rdw ? new[]
        {
            new OhlcvBar(ticker, active, "5m", 9.35m, 9.48m, 9.29m, 9.48m, 702207, "sip", "all"),
            new OhlcvBar(ticker, active.AddMinutes(5), "5m", 9.475m, 9.51m, 9.36m, 9.36m, 502840, "sip", "all"),
            new OhlcvBar(ticker, active.AddMinutes(10), "5m", 9.45m, 9.70m, 9.40m, 9.69m, 100000, "synthetic_stop_after_source_pair", "all")
        } : new[]
        {
            new OhlcvBar(ticker, active, "5m", 13.3m, 13.32m, 13.28m, 13.31m, 14291, "sip", "all"),
            new OhlcvBar(ticker, active.AddMinutes(5), "5m", 13.315m, 13.42m, 13.315m, 13.395m, 30632, "sip", "all")
        };
        if (mirrorLong)
        {
            bars = bars.Select(bar => bar with { Open = 2 * limit - bar.Open, High = 2 * limit - bar.Low,
                Low = 2 * limit - bar.High, Close = 2 * limit - bar.Close }).ToArray();
            stop = 2 * limit - stop;
        }
        var strategy = Strategy(10m);
        var direction = mirrorLong ? "long" : "short";
        var candidate = new BacktestCandidateTrade(ticker, strategy.StrategyName, direction, active, limit, stop,
            limit + (mirrorLong ? 1 : -1) * 3 * Math.Abs(limit - stop), deadline, null, "session_deadline", Math.Abs(limit - stop),
            StrategyId: strategy.StrategyId, CandidateId: ticker + (mirrorLong ? "-long-mirror" : "-actual-short"))
        {
            ExecutionReplay = new(ExecutionReplayPolicy.IntradayRthFiveMinuteV1, active.AddMinutes(-10), active.AddMinutes(-5), deadline, []),
            IntradayExecution = new(limit, 0.01m, active, active.AddMinutes(5), deadline,
                rdw ? 10.29m : 13.74m, rdw ? 9.53m : 13.325m, active.AddMinutes(-10), active.AddMinutes(-5), "shallow_pullback")
        };
        var portfolio = Portfolio() with { StartingCapital = capital };
        if (!mirrorLong)
        {
            var beforeLimitCap = new SharedOrderRiskPlanner().Plan(new(ticker, PlannedOrderSide.Short, capital, limit,
                PlannedStopRequest.FromResolvedPrice(PlannedStopKind.Structural, stop), new(1, 25), new(10, 0, 0), capital / 4));
            Assert.Equal(quantity + 1, beforeLimitCap.Order!.Quantity);
        }
        var sessions = new Dictionary<DateOnly, MarketSessionSchedule> { [date] = MarketSessionSchedule.RegularDay(date) };
        var result = IntradayPortfolioExecution.Replay(new(portfolio, [strategy], [candidate], bars, sessions, start, end));
        var admitted = Assert.Single(result.PortfolioState.AdmittedOrders);
        Assert.Equal(quantity, admitted.RequestedQuantity);
        Assert.True(admitted.RequestedQuantity * limit <= capital / 4);
        Assert.Equal(active.AddMinutes(5), Assert.Single(result.Trades).EntryTimestamp);
        Assert.Equal(limit, Assert.Single(result.Trades).EntryPrice);
        Assert.Equal(active.AddMinutes(rdw ? 15 : 10), Assert.Single(result.Trades).ExitTimestamp);
        Assert.Equal(Decimal.Round(stop * (mirrorLong ? 0.999m : 1.001m), 6), Assert.Single(result.Trades).ExitPrice);
        Assert.Equal("stop_loss", Assert.Single(result.Trades).ExitReason);
        Assert.DoesNotContain(result.Trades, trade => trade.ExitTimestamp < trade.EntryTimestamp);
        Assert.DoesNotContain(result.PortfolioState.Fills, fill => !fill.IsEntry && fill.TimestampUtc < active.AddMinutes(5));
        Assert.Equal("completed_interval_entry", result.FillEvents[0].ExecutionPhase);
        Assert.Equal(active, result.FillEvents[0].SourceIntervalStartUtc);
        Assert.Equal("completed_interval_protection", result.FillEvents[1].ExecutionPhase);
        Assert.True(result.FillEvents[1].ExecutionSequence > result.FillEvents[0].ExecutionSequence);
        NativeDailySessionLedger.VerifyRecordedState(capital, result.PortfolioState, new(start, end, sessions, true));
    }

    // Construct a bounded synthetic session with explicit calendar, costs and shared capital, using the production replay entry point.
    private static IntradayPortfolioReplayOutcome Run(OhlcvBar[] bars, BacktestCandidateTrade[]? candidates = null, PortfolioConfig? portfolio = null)
    {
        var strategy = Strategy(0.000000001m);
        var selected = (candidates ?? [Candidate()]).Select(x => x with { StrategyId = strategy.StrategyId }).ToArray();
        var result = IntradayPortfolioExecution.Replay(new(portfolio ?? Portfolio(), [strategy], selected, bars,
            Sessions(), Start, Start.AddMinutes(45)));
        NativeDailySessionLedger.VerifyRecordedState((portfolio ?? Portfolio()).StartingCapital, result.PortfolioState,
            new(Start, Start.AddMinutes(45), Sessions(), true));
        Assert.DoesNotContain(result.Trades, trade => trade.ExitTimestamp < trade.EntryTimestamp);
        foreach (var group in result.PortfolioState.Fills.GroupBy(fill => fill.CandidateId))
        {
            var firstEntry = group.Where(fill => fill.IsEntry).Min(fill => fill.TimestampUtc);
            Assert.DoesNotContain(group, fill => !fill.IsEntry && fill.TimestampUtc < firstEntry);
        }
        return result;
    }

    // Keep timing fixtures on the same production strategy contract while making each case's frozen slippage explicit.
    private static StrategyDefinition Strategy(decimal slippageBps)
    {
        var original = StrategyDecisionKernelTests.CreateRequest().Strategy;
        return original with
        {
            Timeframe = "5m", Execution = original.Execution with { Timeframe = "5m", SlippageBps = slippageBps },
            ExitRules = original.ExitRules with { ContinuationProtection = new(), TargetRMultiple = 3 },
            Session = original.Session with { UseExtendedHours = false, QuietMinutesAfterOpen = 0 }
        };
    }

    // Freeze ordinary risk settings independently of future execution prices and volume.
    private static PortfolioConfig Portfolio(decimal participation = 100) => new(10000, 1, 25, 4, 0, 0, 1, true, participation);

    // Retain the exact synthetic exchange schedule independently of observed bars.
    private static IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Sessions() =>
        new Dictionary<DateOnly, MarketSessionSchedule> { [Day] = new(Day, true, new(9, 30), new(10, 15)) };

    // Keep B and C separate from the following active interval and its one-bar pending-order expiry.
    private static BacktestCandidateTrade Candidate(string id = "first", string direction = "long", int minute = 10) =>
        new("X", "intraday", direction, Start.AddMinutes(minute), 100, direction == "long" ? 99 : 101,
            direction == "long" ? 103 : 97, Start.AddMinutes(40), null, "session_deadline", 1, CandidateId: id)
        {
            ExecutionReplay = new(ExecutionReplayPolicy.IntradayRthFiveMinuteV1, Start, Start, Start.AddMinutes(40), []),
            IntradayExecution = new(100, 0.01m, Start.AddMinutes(minute), Start.AddMinutes(minute + 5),
                Start.AddMinutes(40), 99.8m, 100.2m, Start.AddMinutes(minute - 10), Start.AddMinutes(minute - 5), "retest")
        };

    // Freeze the new execution policy and first-three-bar geometry without changing legacy limit fixtures.
    private static BacktestCandidateTrade StopCandidate(string id = "first", string direction = "long", int minute = 15)
    {
        var candidate = Candidate(id, direction, minute);
        return candidate with
        {
            ExecutionReplay = candidate.ExecutionReplay! with { PolicyId = ExecutionReplayPolicy.IntradayRthFiveMinuteStopMarketV1 },
            IntradayExecution = candidate.IntradayExecution! with
            {
                EntryOrderMode = IntradayEntryOrderMode.StopMarket, ExpiresAtUtc = Start.AddMinutes(40),
                OpeningRangeHigh = direction == "long" ? 99.99m : 101,
                OpeningRangeLow = direction == "long" ? 99 : 100.01m,
                OpeningRangeCompletedAtUtc = Start.AddMinutes(15), MaxFilledParentTradesPerSession = 2,
                RequireRangeReturnBeforeReentry = true
            }
        };
    }

    // Freeze market sizing from the completed candle and preserve trigger separately from its close reference.
    private static BacktestCandidateTrade MarketCandidate(OhlcvBar[] bars, string id = "first", string direction = "long", int minute = 20)
    {
        var source = bars.Single(x => x.Timestamp == Start.AddMinutes(minute - 5));
        var candidate = StopCandidate(id, direction, minute);
        var stop = direction == "long" ? decimal.Floor(source.Low / .01m) * .01m : decimal.Ceiling(source.High / .01m) * .01m;
        return candidate with
        {
            EntryPrice = source.Close, StopLossPrice = stop,
            ExecutionReplay = candidate.ExecutionReplay! with { PolicyId = ExecutionReplayPolicy.IntradayRthFiveMinuteNextBarMarketV1 },
            IntradayExecution = candidate.IntradayExecution! with
            {
                EntryOrderMode = IntradayEntryOrderMode.NextBarMarket, LimitPrice = source.Close,
                BreakoutTimestampUtc = source.Timestamp, ConfirmationTimestampUtc = source.Timestamp,
                OpeningRangeHigh = direction == "long" ? 99.98m : 101m,
                OpeningRangeLow = direction == "long" ? 99m : 100.02m,
                BreakoutTriggerPrice = direction == "long" ? 99.99m : 100.01m
            }
        };
    }

    // Supply every interval to the session deadline; individual tests remove or mutate exactly the evidence under test.
    private static OhlcvBar[] Bars() => Enumerable.Range(0, 9).Select(i => Bar(i * 5, 100, 100.5m, 99.5m, 100)).ToArray();

    // Timestamp deterministic OHLC by interval open and make its completed-close availability explicit.
    private static OhlcvBar Bar(int minute, decimal open, decimal high, decimal low, decimal close, decimal volume = 1000) =>
        new("X", Start.AddMinutes(minute), "5m", open, high, low, close, volume,
            KnownAtUtc: Start.AddMinutes(minute + 5));

    // Capture immutable publication envelopes without touching operational stores or provider data.
    private sealed class Sink : IExecutionEventSink
    {
        public List<ExecutionEvent> Items { get; } = [];
        // Keep sink insertion order so the evidence test can verify chronological publication.
        public void Append(ExecutionEvent item) => Items.Add(item);
    }
}
