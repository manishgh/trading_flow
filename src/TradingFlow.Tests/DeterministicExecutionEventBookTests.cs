using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class DeterministicExecutionEventBookTests
{
    private const long Epoch = 1_800_000_000_000_000_000;
    private static readonly DeterministicExecutionSimulator Simulator = new();
    private static ExecutionEventSimulationPolicy Policy(ExecutionProtectionActivation mode =
        ExecutionProtectionActivation.CustomPerFill, decimal participation = 100, long latency = 0) =>
        new(new(participation, 0, 0, 0, 1, 1, .0000278m, .000166m, 8.3m),
            mode, 1000, latency, true, 1, 1, "fixture-raw-sip-v1");
    private static ExecutionQuoteTick Quote(long at, decimal bid = 9, decimal ask = 10, long shares = 100) =>
        new("quote-" + at, Epoch + at, bid, ask, shares, shares, true);
    private static ExecutionTradeTick Trade(long at, decimal price = 9.75m, long shares = 100, bool eligible = true) =>
        new("trade-" + at, Epoch + at, price, shares, eligible);
    private static ExecutionEventBatch Batch(long at, ExecutionQuoteTick[]? quotes = null,
        ExecutionTradeTick[]? trades = null, bool covered = true) =>
        new("X", Epoch + at, Epoch + at, at, quotes ?? [], trades ?? [], covered);
    private static ExecutionEventOrderCommand Entry(string id = "entry", long known = 1, int quantity = 10,
        SimulatedTimeInForce tif = SimulatedTimeInForce.ImmediateOrCancel, string? feeGroup = null) =>
        new(new(ExecutionCommandKind.Submit, id, "X", SimulatedOrderSide.Buy, SimulatedOrderType.Market,
            quantity, ExecutionEventTime.ToUtc(Epoch + known), known, false, tif,
            FixedFeeGroupId: feeGroup ?? id), Epoch + known);
    private static ExecutionEventOrderCommand Stop(long known = 2, int quantity = 10, bool repair = false,
        string parent = "entry") =>
        new(new(ExecutionCommandKind.Submit, "stop", "X", SimulatedOrderSide.Sell, SimulatedOrderType.Stop,
            quantity, ExecutionEventTime.ToUtc(Epoch + known), known, true, SimulatedTimeInForce.GoodTillCanceled,
            StopPrice: 9.5m, FixedFeeGroupId: "exit-fee", OcoGroupId: "position"),
            Epoch + known, parent, IsProtection: true, IsRepair: repair);
    private static ExecutionEventOrderCommand Target(long known = 2, int quantity = 10) =>
        new(new(ExecutionCommandKind.Submit, "target", "X", SimulatedOrderSide.Sell, SimulatedOrderType.Limit,
            quantity, ExecutionEventTime.ToUtc(Epoch + known), known + 1, true, SimulatedTimeInForce.GoodTillCanceled,
            LimitPrice: 11m, FixedFeeGroupId: "exit-fee", OcoGroupId: "position"),
            Epoch + known, "entry", IsProtection: true);
    private static ExecutionEventOrderCommand MarketExit(long known = 2, int quantity = 10) =>
        new(new(ExecutionCommandKind.Submit, "market-exit", "X", SimulatedOrderSide.Sell,
            SimulatedOrderType.Market, quantity, ExecutionEventTime.ToUtc(Epoch + known), known, true,
            SimulatedTimeInForce.ImmediateOrCancel), Epoch + known, "entry");
    private static ExecutionEventStepResult Advance(ExecutionEventBookState state, ExecutionEventBatch batch,
        ExecutionEventOrderCommand[]? commands = null, ExecutionEventSimulationPolicy? policy = null) =>
        Simulator.AdvanceEvent(new(state, batch, commands ?? [], policy ?? Policy()));
    private static ExecutionEventBookState Seed(ExecutionEventSimulationPolicy? policy = null, long displayed = 100) =>
        Advance(ExecutionEventBookState.Empty, Batch(0, [Quote(0, shares: displayed)]), policy: policy).State;
    private static ExecutionEventStepResult Enter(ExecutionEventSimulationPolicy? policy = null,
        long volume = 100, int quantity = 10) =>
        Advance(Seed(policy), Batch(2, trades: [Trade(2, shares: volume)]),
            [Entry(quantity: quantity)], policy);

    [Fact]
    public void FirstIntervalProtectionUsesLaterTradesWithoutReplayingTheEntryObservation()
    {
        var entry = Enter();
        Assert.Equal(10, Assert.Single(entry.Events, e => e.Event.LastFillQuantity > 0).Event.LastFillQuantity);
        var elected = Advance(entry.State, Batch(3, trades: [Trade(3, 9.1m)]), [Stop(), Target()]);
        Assert.DoesNotContain(elected.Events, e => e.Event.LastFillQuantity > 0);
        Assert.True(elected.State.Book.WorkingOrders["stop"].StopTriggered);
        var exit = Advance(elected.State, Batch(4, trades: [Trade(4, 9.1m)]));
        var fill = Assert.Single(exit.Events, e => e.Event.LastFillQuantity > 0);
        Assert.Equal(Epoch + 4, fill.OccurredAtNanoseconds);
        Assert.Equal(9m, fill.Event.FillPrice);
        Assert.Equal(0, exit.State.Book.Positions["X"].SignedQuantity);
        Assert.Empty(exit.State.Book.WorkingOrders);
        Assert.Contains(exit.Events, e => e.Event.ClientOrderId == "target" && e.Event.Reason == "oco_sibling_filled");
        Assert.True(exit.State.Book.AccumulatedFees > 2);
    }

    [Fact]
    public void ACommandAtTheProducingNanosecondCannotConsumeIt()
    {
        var first = Advance(Seed(), Batch(2, trades: [Trade(2)]), [Entry(known: 2)]);
        Assert.DoesNotContain(first.Events, e => e.Event.LastFillQuantity > 0);
        var next = Advance(first.State, Batch(3, trades: [Trade(3)]));
        Assert.Equal(10, Assert.Single(next.Events, e => e.Event.LastFillQuantity > 0).Event.LastFillQuantity);
    }

    [Theory]
    [InlineData(SimulatedTimeInForce.Day, 5)]
    [InlineData(SimulatedTimeInForce.Day, 6)]
    [InlineData(SimulatedTimeInForce.ImmediateOrCancel, 5)]
    [InlineData(SimulatedTimeInForce.ImmediateOrCancel, 6)]
    public void PrecommittedExpiryCancelsBeforeTheBoundaryOrLaterTrade(SimulatedTimeInForce tif, long tradeAt)
    {
        var policy = Policy(latency: 10);
        var route = Entry(tif: tif) with { ExpiresAtNanoseconds = Epoch + 5 };
        var submitted = Advance(Seed(policy), Batch(1), [route], policy);
        Assert.Equal(Epoch + 5, submitted.State.OrderExpiries!["entry"]);
        var expired = Advance(submitted.State, Batch(tradeAt, trades: [Trade(tradeAt)]), policy: policy);
        Assert.DoesNotContain(expired.Events, e => e.Event.LastFillQuantity > 0);
        var cancellation = Assert.Single(expired.Events);
        Assert.Equal("route_expired", cancellation.Event.Reason);
        Assert.Equal(Epoch + 5, cancellation.OccurredAtNanoseconds);
        Assert.Equal(Epoch + 15, cancellation.KnownAtNanoseconds);
        Assert.Empty(expired.State.Book.WorkingOrders);
        Assert.True(submitted.State.Book.WorkingOrders.ContainsKey("entry"));
    }

    [Fact]
    public void ExpiredRouteCannotFirstAppearAsAFillOrBeRevivedByALateReplacement()
    {
        var route = Entry(tif: SimulatedTimeInForce.Day) with { ExpiresAtNanoseconds = Epoch + 5 };
        var late = Advance(Seed(), Batch(5, trades: [Trade(5)]), [route]);
        Assert.DoesNotContain(late.Events, e => e.Event.LastFillQuantity > 0);
        Assert.Contains(late.Events, e => e.Event.Reason == "route_expired");
        var submitted = Advance(Seed(), Batch(1), [route]);
        var replacement = Entry("replacement", known: 6, tif: SimulatedTimeInForce.Day) with {
            ExpiresAtNanoseconds = Epoch + 10 };
        replacement = replacement with { Command = replacement.Command with {
            Kind = ExecutionCommandKind.Replace, TargetClientOrderId = "entry" } };
        var result = Advance(submitted.State, Batch(7, trades: [Trade(7)]), [replacement]);
        Assert.Empty(result.State.Book.WorkingOrders);
        Assert.DoesNotContain(result.Events, e => e.Event.LastFillQuantity > 0);
    }

    [Fact]
    public void ExpiryMustBeFutureAndCannotChangeForAnExistingRouteIdentity()
    {
        var route = Entry(tif: SimulatedTimeInForce.Day) with { ExpiresAtNanoseconds = Epoch + 5 };
        Assert.Throws<InvalidDataException>(() => Advance(Seed(), Batch(1),
            [route with { ExpiresAtNanoseconds = Epoch + 1 }]));
        var submitted = Advance(Seed(), Batch(1), [route]);
        Assert.Throws<InvalidDataException>(() => Advance(submitted.State, Batch(2),
            [route with { ExpiresAtNanoseconds = Epoch + 6 }]));
    }

    [Theory]
    [InlineData(SimulatedTimeInForce.Day)]
    [InlineData(SimulatedTimeInForce.ImmediateOrCancel)]
    public void PreExpiryReplacementInheritsTheBoundRouteExpiryWhenOmitted(SimulatedTimeInForce tif)
    {
        var route = Entry(tif: tif) with { ExpiresAtNanoseconds = Epoch + 5 };
        var submitted = Advance(Seed(), Batch(1), [route]);
        var replacement = Entry("replacement", known: 3, tif: tif);
        replacement = replacement with { Command = replacement.Command with {
            Kind = ExecutionCommandKind.Replace, TargetClientOrderId = "entry" } };
        var replaced = Advance(submitted.State, Batch(4), [replacement]);
        Assert.Equal(Epoch + 5, replaced.State.OrderExpiries!["replacement"]);
        var expired = Advance(replaced.State, Batch(6, trades: [Trade(6)]));
        Assert.DoesNotContain(expired.Events, e => e.Event.LastFillQuantity > 0);
        Assert.Contains(expired.Events, e => e.Event.ClientOrderId == "replacement" &&
            e.Event.Reason == "route_expired" && e.OccurredAtNanoseconds == Epoch + 5);
        Assert.Empty(expired.State.Book.WorkingOrders);
        var replay = Advance(expired.State, Batch(7), [replacement]);
        Assert.Empty(replay.Events);
        Assert.Empty(replay.State.Book.WorkingOrders);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void PreExpiryReplacementCannotShortenOrExtendItsBoundExpiry(long replacementExpiry)
    {
        var route = Entry(tif: SimulatedTimeInForce.Day) with { ExpiresAtNanoseconds = Epoch + 5 };
        var submitted = Advance(Seed(), Batch(1), [route]);
        var replacement = Entry("replacement", known: 3, tif: SimulatedTimeInForce.Day) with {
            ExpiresAtNanoseconds = Epoch + replacementExpiry };
        replacement = replacement with { Command = replacement.Command with {
            Kind = ExecutionCommandKind.Replace, TargetClientOrderId = "entry" } };
        Assert.Throws<InvalidDataException>(() => Advance(submitted.State, Batch(4), [replacement]));
    }

    [Fact]
    public void QuoteAndTradeAtSameNanosecondRemainAnAmbiguousBatch()
    {
        var result = Advance(Seed(), Batch(2, [Quote(2)], [Trade(2)]), [Entry()]);
        Assert.Contains("ambiguous_same_nanosecond_market_order", result.UnavailableReasons);
        Assert.DoesNotContain(result.Events, e => e.Event.LastFillQuantity > 0);
    }

    [Fact]
    public void DifferentTradePricesAtOneNanosecondDoNotChooseAProfitableOrder()
    {
        var result = Advance(Seed(), Batch(2, trades: [Trade(2, 9.1m),
            Trade(2, 9.9m) with { EventId = "other-print" }]), [Entry()]);
        Assert.Contains("ambiguous_same_nanosecond_market_order", result.UnavailableReasons);
        Assert.DoesNotContain(result.Events, e => e.Event.LastFillQuantity > 0);
    }

    [Fact]
    public void FullNativeParentCanActivateChildrenButPartialNativeParentRequiresExplicitRepair()
    {
        var policy = Policy(ExecutionProtectionActivation.NativeParentFullyFilled);
        var full = Enter(policy);
        Assert.Empty(Advance(full.State, Batch(3, trades: [Trade(3)]), [Stop()], policy).UnavailableReasons);
        var partial = Enter(policy, volume: 3);
        Assert.Equal(3, partial.State.Book.Positions["X"].SignedQuantity);
        Assert.Throws<InvalidOperationException>(() =>
            Advance(partial.State, Batch(3, trades: [Trade(3)]), [Stop(quantity: 3)], policy));
        var repair = Advance(partial.State, Batch(3, trades: [Trade(3)]), [Stop(quantity: 3, repair: true)], policy);
        Assert.True(repair.State.Book.WorkingOrders.ContainsKey("stop"));
    }

    [Fact]
    public void CustomPerFillProtectionCannotExceedKnownParentExposure()
    {
        var partial = Enter(volume: 3);
        Assert.Throws<InvalidOperationException>(() =>
            Advance(partial.State, Batch(3, trades: [Trade(3)]), [Stop(quantity: 4)]));
        var protectedFill = Advance(partial.State, Batch(3, trades: [Trade(3)]), [Stop(quantity: 3)]);
        Assert.True(protectedFill.State.Book.WorkingOrders.ContainsKey("stop"));
    }

    [Fact]
    public void FillReportDelayPreventsEarlyProtectionEvenWhenActualPositionExists()
    {
        var policy = Policy(latency: 10);
        var entry = Enter(policy);
        Assert.Equal(Epoch + 12, Assert.Single(entry.Events, e => e.Event.LastFillQuantity > 0).KnownAtNanoseconds);
        Assert.Throws<InvalidOperationException>(() =>
            Advance(entry.State, Batch(3, trades: [Trade(3)]), [Stop()], policy));
        var afterKnown = Advance(entry.State, Batch(13, trades: [Trade(13)]), [Stop(known: 12)], policy);
        Assert.True(afterKnown.State.Book.WorkingOrders.ContainsKey("stop"));
    }

    [Fact]
    public void PlainReducingMarketCannotUseExposureBeforeItsParentReceiptIsKnown()
    {
        var policy = Policy(latency: 10);
        var entry = Enter(policy);
        Assert.Throws<InvalidOperationException>(() => Advance(entry.State,
            Batch(3, trades: [Trade(3)]), [MarketExit()], policy));
        var afterKnown = Advance(entry.State, Batch(13, trades: [Trade(13)]),
            [MarketExit(known: 12)], policy);
        Assert.Equal(10, Assert.Single(afterKnown.Events, e => e.Event.LastFillQuantity > 0).Event.LastFillQuantity);
        Assert.Equal(0, afterKnown.State.Book.Positions["X"].SignedQuantity);
    }

    [Fact]
    public void PlainReducingMarketRequiresMatchingParentAndKnownQuantity()
    {
        var partial = Enter(volume: 3);
        Assert.Throws<InvalidDataException>(() => Advance(partial.State,
            Batch(3, trades: [Trade(3)]), [MarketExit(quantity: 3) with { ParentOrderId = null }]));
        var wrongSide = MarketExit(quantity: 3);
        wrongSide = wrongSide with { Command = wrongSide.Command with { Side = SimulatedOrderSide.Buy } };
        Assert.Throws<InvalidDataException>(() => Advance(partial.State,
            Batch(3, trades: [Trade(3)]), [wrongSide]));
        Assert.Throws<InvalidOperationException>(() => Advance(partial.State,
            Batch(3, trades: [Trade(3)]), [MarketExit(quantity: 4)]));
        var exit = Advance(partial.State, Batch(3, trades: [Trade(3)]), [MarketExit(quantity: 3)]);
        Assert.Equal(0, exit.State.Book.Positions["X"].SignedQuantity);
    }

    [Fact]
    public void AReducingOrderCannotBorrowAnEntryParentFromAnotherSymbol()
    {
        var entry = Enter();
        var contracts = entry.State.Book.KnownOrderContracts.ToDictionary(p => p.Key, p => p.Value);
        contracts["entry"] = contracts["entry"] with { Symbol = "OTHER" };
        var otherParent = entry.State with { Book = entry.State.Book with { KnownOrderContracts = contracts } };
        Assert.Throws<InvalidDataException>(() => Advance(otherParent,
            Batch(3, trades: [Trade(3)]), [MarketExit()]));
    }

    [Fact]
    public void ReducingMarketReplacementCannotIncreaseBeyondKnownParentQuantity()
    {
        var partial = Enter(volume: 3);
        var submitted = Advance(partial.State, Batch(3), [MarketExit(quantity: 3)]);
        var replacement = MarketExit(known: 3, quantity: 4);
        replacement = replacement with { Command = replacement.Command with {
            Kind = ExecutionCommandKind.Replace, ClientOrderId = "replacement-exit",
            TargetClientOrderId = "market-exit" } };
        Assert.Throws<InvalidOperationException>(() => Advance(submitted.State,
            Batch(4, trades: [Trade(4)]), [replacement]));
    }

    [Fact]
    public void AClosedParentCannotBorrowALaterUnreportedPosition()
    {
        var policy = Policy(latency: 10);
        var first = Enter(policy, volume: 3, quantity: 3);
        var closed = Advance(first.State, Batch(13, trades: [Trade(13)]),
            [MarketExit(known: 12, quantity: 3)], policy);
        var later = Advance(closed.State, Batch(14, trades: [Trade(14)]),
            [Entry("later-entry", known: 13, quantity: 3)], policy);
        Assert.Equal(3, later.State.Book.Positions["X"].SignedQuantity);
        Assert.Throws<InvalidOperationException>(() => Advance(later.State,
            Batch(24, trades: [Trade(24)]), [MarketExit(known: 23, quantity: 3) with {
                Command = MarketExit(known: 23, quantity: 3).Command with { ClientOrderId = "stale-parent-exit" }
            }], policy));
        var replay = Advance(later.State, Batch(24, trades: [Trade(24)]),
            [MarketExit(known: 12, quantity: 3)], policy);
        Assert.Equal(3, replay.State.Book.Positions["X"].SignedQuantity);
        Assert.DoesNotContain(replay.Events, e => e.Event.LastFillQuantity > 0);
    }

    [Fact]
    public void NativePartialTargetLeavesOnlyItsOwnRemainingSharesForTheSiblingStop()
    {
        var policy = Policy(ExecutionProtectionActivation.NativeParentFullyFilled);
        var entry = Enter(policy);
        var protectedEntry = Advance(entry.State, Batch(3, [Quote(3, 11, 12)]), [Stop(), Target()], policy);
        var partialTarget = Advance(protectedEntry.State, Batch(4, trades: [Trade(4, 11.5m, 3)]), policy: policy);
        Assert.Equal(7, partialTarget.State.Book.Positions["X"].SignedQuantity);
        var quote = Advance(partialTarget.State, Batch(5, [Quote(5)]), policy: policy);
        var otherEntry = Advance(quote.State, Batch(6, trades: [Trade(6)]),
            [Entry("other-entry", known: 5, quantity: 5)], policy);
        var elected = Advance(otherEntry.State, Batch(7, trades: [Trade(7, 9.1m)]), policy: policy);
        var stopped = Advance(elected.State, Batch(8, trades: [Trade(8, 9.1m)]), policy: policy);
        Assert.Equal(7, Assert.Single(stopped.Events, e => e.Event.LastFillQuantity > 0).Event.LastFillQuantity);
        Assert.Equal(5, stopped.State.Book.Positions["X"].SignedQuantity);
        Assert.DoesNotContain("stop", stopped.State.Book.WorkingOrders.Keys);
        Assert.DoesNotContain("target", stopped.State.Book.WorkingOrders.Keys);
        Assert.Equal(10, stopped.State.ParentReductionReceipts!["entry"].Sum(r => r.Quantity));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    public void AReportedExposureRouteIsRejectedWhenAnUnreportedProtectiveFillAlreadyReducedIt(int protectiveFill)
    {
        var policy = Policy(ExecutionProtectionActivation.NativeParentFullyFilled, latency: 10);
        var entry = Enter(policy);
        var elected = Advance(entry.State, Batch(13, trades: [Trade(13, 9.1m)]),
            [Stop(known: 12)], policy);
        var physicalExit = Advance(elected.State,
            Batch(14, trades: [Trade(14, 9.1m, protectiveFill)]), policy: policy);
        var route = MarketExit(known: 15);
        // Route on a controller clock without another print filling the still-working stop first.
        var rejected = Advance(physicalExit.State, Batch(15), [route], policy);
        var rejection = Assert.Single(rejected.Events, e => e.Event.State == "rejected");
        Assert.Equal("physical_parent_exposure_changed_before_route", rejection.Event.Reason);
        Assert.Equal(Epoch + 25, rejection.KnownAtNanoseconds);
        Assert.Equal(10 - protectiveFill, rejected.State.Book.Positions["X"].SignedQuantity);
        Assert.DoesNotContain("market-exit", rejected.State.Book.WorkingOrders.Keys);
        Assert.Contains("market-exit", rejected.State.Book.KnownOrderContracts.Keys);
        var replay = Advance(rejected.State, Batch(16), [route], policy);
        Assert.Empty(replay.Events);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("basis")]
    [InlineData("price")]
    [InlineData("quantity")]
    public void MissingWorkflowOrUnconvertedUnitsNeverFallBack(string field)
    {
        var policy = Policy();
        policy = field switch {
            "mode" => policy with { ProtectionActivation = ExecutionProtectionActivation.Unspecified },
            "basis" => policy with { RawPriceAndQuantityBasisVerified = false },
            "price" => policy with { PriceFactorToRaw = 2 },
            _ => policy with { QuantityFactorToRaw = 2 }
        };
        Assert.Throws<InvalidDataException>(() => Seed(policy));
    }

    [Fact]
    public void StaleQuoteOrMissingCoverageIsUnavailableRatherThanAnOptimisticNonFill()
    {
        var stale = Advance(Seed(), Batch(1001, trades: [Trade(1001)]), [Entry()]);
        Assert.Contains("prior_fresh_nbbo_unavailable", stale.UnavailableReasons);
        var missing = Advance(Seed(), Batch(2, trades: [Trade(2)], covered: false), [Entry()]);
        Assert.Contains("execution_interval_coverage_unavailable", missing.UnavailableReasons);
        Assert.Empty(missing.State.Book.Positions);
    }

    [Fact]
    public void InvalidQuoteClearsThePriorSideAndCannotManufactureCapacity()
    {
        var invalid = Advance(Seed(), Batch(1, [Quote(1) with { Ask = 0 }]));
        var next = Advance(invalid.State, Batch(2, trades: [Trade(2)]), [Entry()]);
        Assert.Contains("prior_fresh_nbbo_unavailable", next.UnavailableReasons);
    }

    [Fact]
    public void TradeOutsidePriorNbboCannotElectAStop()
    {
        var entry = Enter();
        var result = Advance(entry.State, Batch(3, trades: [Trade(3, 8)]), [Stop()]);
        Assert.False(result.State.Book.WorkingOrders["stop"].StopTriggered);
        Assert.DoesNotContain(result.Events, e => e.Event.LastFillQuantity > 0);
    }

    [Fact]
    public void EligibleOddLotIsNotAutomaticallyExcluded()
    {
        var result = Enter(volume: 1, quantity: 1);
        Assert.Equal(1, Assert.Single(result.Events, e => e.Event.LastFillQuantity > 0).Event.LastFillQuantity);
    }

    [Fact]
    public void ParticipationAndDisplayedLiquidityAreSharedAcrossOrdersAndEvents()
    {
        var policy = Policy(participation: 50);
        var first = Advance(Seed(policy, displayed: 4), Batch(2, trades: [Trade(2, shares: 10)]),
            [Entry("first", quantity: 10, tif: SimulatedTimeInForce.Day),
             Entry("second", quantity: 10, tif: SimulatedTimeInForce.Day)], policy);
        Assert.Equal(4, first.Events.Sum(e => e.Event.LastFillQuantity));
        var next = Advance(first.State, Batch(3, trades: [Trade(3, shares: 1000)]), policy: policy);
        Assert.DoesNotContain(next.Events, e => e.Event.LastFillQuantity > 0);
        Assert.Equal(4, next.State.Book.Positions["X"].SignedQuantity);
    }

    [Fact]
    public void ReusedQuoteIdentityDoesNotReplenishDisplayedLiquidity()
    {
        var state = Enter().State;
        var result = Advance(state, Batch(3, [Quote(3) with { EventId = "quote-0" }]));
        Assert.Contains("reused_quote_event_identity", result.UnavailableReasons);
        Assert.Same(state, result.State);
    }

    [Fact]
    public void FixedFeeGroupIsChargedOnceAcrossPartialEntryFills()
    {
        var first = Advance(Seed(), Batch(2, trades: [Trade(2, shares: 2)]),
            [Entry(quantity: 5, tif: SimulatedTimeInForce.Day)]);
        var second = Advance(first.State, Batch(3, trades: [Trade(3, shares: 2)]));
        var third = Advance(second.State, Batch(4, trades: [Trade(4, shares: 1)]));
        Assert.Equal(5, third.State.Book.Positions["X"].SignedQuantity);
        Assert.Equal(1, third.State.Book.AccumulatedFees);
    }

    [Fact]
    public void ExactReplayIsIdempotentButCannotChangeItsEvidence()
    {
        var batch = Batch(2, trades: [Trade(2)]);
        var first = Advance(Seed(), batch, [Entry()]);
        var replay = Advance(first.State, batch);
        Assert.Same(first.State, replay.State);
        Assert.Empty(replay.Events);
        Assert.Throws<InvalidDataException>(() => Advance(first.State,
            batch with { Trades = [Trade(2, 9.5m)] }));
        Assert.Throws<InvalidDataException>(() => Advance(first.State, batch, [Entry()]));
    }

    [Fact]
    public void AStopElectsWithZeroParticipationAndRemainsMarketAfterARebound()
    {
        var policy = Policy(participation: 5);
        var entry = Enter(policy, quantity: 1);
        var elected = Advance(entry.State, Batch(3, trades: [Trade(3, 9.1m, shares: 1)]),
            [Stop(quantity: 1)], policy);
        Assert.True(elected.State.Book.WorkingOrders["stop"].StopTriggered);
        Assert.DoesNotContain(elected.Events, e => e.Event.LastFillQuantity > 0);
        var rebound = Advance(elected.State, Batch(4, [Quote(4, 10, 11)]), policy: policy);
        var exit = Advance(rebound.State, Batch(5, trades: [Trade(5, 10.5m)]), policy: policy);
        Assert.Equal(10m, Assert.Single(exit.Events, e => e.Event.LastFillQuantity > 0).Event.FillPrice);
        Assert.Equal(0, exit.State.Book.Positions["X"].SignedQuantity);
    }

    [Fact]
    public void RegulatoryCapCarriesAcrossPartialSellFills()
    {
        var policy = Policy() with { Costs = Policy().Costs with {
            FixedBuyFee = 0, FixedSellFee = 0, SecFeeRate = 0, FinraTafPerShare = .1m, FinraTafCap = .3m } };
        var sell = Entry(quantity: 5, tif: SimulatedTimeInForce.Day);
        sell = sell with { Command = sell.Command with { Side = SimulatedOrderSide.Sell } };
        var first = Advance(Seed(policy), Batch(2, trades: [Trade(2, shares: 2)]), [sell], policy);
        var second = Advance(first.State, Batch(3, trades: [Trade(3, shares: 2)]), policy: policy);
        var third = Advance(second.State, Batch(4, trades: [Trade(4, shares: 1)]), policy: policy);
        Assert.Equal(.3m, third.State.Book.AccumulatedFees);
        Assert.Equal(-5, third.State.Book.Positions["X"].SignedQuantity);
    }

    [Fact]
    public void AReplacedParentRetainsKnownEarlierPartialFillsForNativeActivation()
    {
        var policy = Policy(ExecutionProtectionActivation.NativeParentFullyFilled);
        var first = Advance(Seed(policy), Batch(2, trades: [Trade(2, shares: 2)]),
            [Entry(quantity: 5, tif: SimulatedTimeInForce.Day)], policy);
        var replacement = Entry("replacement", known: 3, quantity: 5, tif: SimulatedTimeInForce.Day);
        replacement = replacement with { Command = replacement.Command with {
            Kind = ExecutionCommandKind.Replace, TargetClientOrderId = "entry", FixedFeeGroupId = "entry" } };
        var full = Advance(first.State, Batch(4, trades: [Trade(4, shares: 3)]), [replacement], policy);
        var protection = Advance(full.State, Batch(5, trades: [Trade(5)]),
            [Stop(known: 4, quantity: 5, parent: "replacement")], policy);
        Assert.Equal(5, protection.State.Book.Positions["X"].SignedQuantity);
        Assert.True(protection.State.Book.WorkingOrders.ContainsKey("stop"));
        Assert.Equal(1, protection.State.Book.AccumulatedFees);
    }

    [Fact]
    public void ANewFutureSuffixCannotMutateAnEarlierSnapshot()
    {
        var before = Seed();
        var prefix = Advance(before, Batch(2, trades: [Trade(2)]), [Entry()]);
        _ = Advance(prefix.State, Batch(3, trades: [Trade(3, 9.1m)]), [Stop()]);
        Assert.Empty(before.Book.Positions);
        Assert.Equal(100, before.Quotes["X"].RemainingAskShares);
        Assert.Equal(10, prefix.State.Book.Positions["X"].SignedQuantity);
        Assert.False(prefix.State.Book.WorkingOrders.ContainsKey("stop"));
    }

    private static ExecutionEventOrderCommand[] NativeBracket(long expiry = 50,
        SimulatedTimeInForce tif = SimulatedTimeInForce.Day) => [
        Entry(tif: tif) with { ExpiresAtNanoseconds = Epoch + expiry },
        Stop(known: 1) with { NativeParentCommitted = true },
        Target(known: 1) with { NativeParentCommitted = true }
    ];

    [Fact]
    public void PrecommittedNativeChildrenActivateOnPhysicalFullFillBeforeClientReportsAndNeverReuseItsTrade()
    {
        var policy = Policy(ExecutionProtectionActivation.NativeParentFullyFilled, latency: 1000);
        var before = Seed(policy);
        var committed = Advance(before, Batch(1), NativeBracket(), policy);
        Assert.Empty(committed.State.Book.Positions);
        Assert.Equal(2, committed.State.NativeChildCommitments!.Count);
        Assert.Equal(Int64.MaxValue, committed.State.OrderActivationTimes["stop"]);
        var parentFill = Advance(committed.State, Batch(2, trades: [Trade(2, 9.1m)]), policy: policy);
        Assert.Equal(Epoch + 2, parentFill.State.OrderActivationTimes["stop"]);
        Assert.Equal(Epoch + 2, parentFill.State.OrderActivationTimes["target"]);
        Assert.Equal(Epoch + 1002, parentFill.State.FillKnownTimes["entry"]);
        Assert.DoesNotContain(parentFill.Events, e => e.Event.IsRiskReducing && e.Event.LastFillQuantity > 0);
        Assert.False(parentFill.State.Book.WorkingOrders["stop"].StopTriggered);
        Assert.Contains(parentFill.Events, e => e.Event.Reason == "native_parent_physically_fully_filled" &&
            e.OccurredAtNanoseconds == Epoch + 2 && e.KnownAtNanoseconds == Epoch + 1002);
        var elected = Advance(parentFill.State, Batch(3, trades: [Trade(3, 9.1m)]), policy: policy);
        Assert.True(elected.State.Book.WorkingOrders["stop"].StopTriggered);
        Assert.DoesNotContain(elected.Events, e => e.Event.LastFillQuantity > 0);
        var exited = Advance(elected.State, Batch(4, trades: [Trade(4, 9.1m)]), policy: policy);
        Assert.Equal(0, exited.State.Book.Positions["X"].SignedQuantity);
        Assert.Empty(exited.State.Book.WorkingOrders);
        Assert.Contains(exited.Events, e => e.Event.ClientOrderId == "target" && e.Event.Reason == "oco_sibling_filled");
        Assert.Empty(before.Book.Positions);
        Assert.Empty(before.NativeChildCommitments!);
        Assert.Equal(Int64.MaxValue, committed.State.OrderActivationTimes["stop"]);
    }

    [Fact]
    public void PrecommittedChildrenStayDormantThroughPartialParentThenActivateOnlyOnItsFinalFill()
    {
        var policy = Policy(ExecutionProtectionActivation.NativeParentFullyFilled, latency: 1000);
        var committed = Advance(Seed(policy), Batch(1), NativeBracket(), policy);
        var partial = Advance(committed.State, Batch(2, trades: [Trade(2, 9.1m, 3)]), policy: policy);
        Assert.Equal(3, partial.State.Book.Positions["X"].SignedQuantity);
        Assert.Equal(Int64.MaxValue, partial.State.OrderActivationTimes["stop"]);
        Assert.DoesNotContain(partial.Events, e => e.Event.Reason == "native_parent_physically_fully_filled");
        var full = Advance(partial.State, Batch(3, trades: [Trade(3, 9.1m, 7)]), policy: policy);
        Assert.Equal(10, full.State.Book.Positions["X"].SignedQuantity);
        Assert.Equal(Epoch + 3, full.State.OrderActivationTimes["stop"]);
        Assert.False(full.State.Book.WorkingOrders["stop"].StopTriggered);
        Assert.DoesNotContain(full.Events, e => e.Event.IsRiskReducing && e.Event.LastFillQuantity > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentExpiryCancelsDormantChildrenAndPartialExposureRemainsExplicitlyUnprotected(bool partialFill)
    {
        var policy = Policy(ExecutionProtectionActivation.NativeParentFullyFilled);
        var committed = Advance(Seed(policy), Batch(1), NativeBracket(expiry: 5), policy);
        var state = partialFill ? Advance(committed.State, Batch(2, trades: [Trade(2, shares: 3)]), policy: policy).State : committed.State;
        var expired = Advance(state, Batch(5, trades: [Trade(5, 9.1m)]), policy: policy);
        Assert.Empty(expired.State.Book.WorkingOrders);
        Assert.Equal(2, expired.Events.Count(e => e.Event.Reason == "native_parent_terminated_before_full_fill"));
        Assert.DoesNotContain(expired.Events, e => e.Event.LastFillQuantity > 0);
        if (partialFill) {
            Assert.Equal(3, expired.State.Book.Positions["X"].SignedQuantity);
            Assert.Contains("native_parent_expired_partially_filled_unprotected", expired.UnavailableReasons);
        }
        else Assert.Empty(expired.UnavailableReasons);
    }

    [Fact]
    public void CancelingAnUnfilledNativeParentCancelsItsChildrenWithoutInventingExposure()
    {
        var policy = Policy(ExecutionProtectionActivation.NativeParentFullyFilled);
        var committed = Advance(Seed(policy), Batch(1), NativeBracket(), policy);
        var cancel = new ExecutionEventOrderCommand(new(ExecutionCommandKind.Cancel, "cancel-parent", "X",
            SimulatedOrderSide.Buy, SimulatedOrderType.Market, 0, ExecutionEventTime.ToUtc(Epoch + 2), 2,
            false, TargetClientOrderId: "entry"), Epoch + 2);
        var canceled = Advance(committed.State, Batch(2), [cancel], policy);
        Assert.Empty(canceled.State.Book.WorkingOrders);
        Assert.Empty(canceled.State.Book.Positions);
        Assert.Empty(canceled.UnavailableReasons);
        Assert.Equal(2, canceled.Events.Count(e => e.Event.Reason == "native_parent_terminated_before_full_fill"));
    }

    [Fact]
    public void NativeIocPartialTerminationCannotActivateChildrenOrInventARepair()
    {
        var policy = Policy(ExecutionProtectionActivation.NativeParentFullyFilled);
        var committed = Advance(Seed(policy), Batch(1), NativeBracket(tif: SimulatedTimeInForce.ImmediateOrCancel), policy);
        var partial = Advance(committed.State, Batch(2, trades: [Trade(2, shares: 3)]), policy: policy);
        Assert.Equal(3, partial.State.Book.Positions["X"].SignedQuantity);
        Assert.Empty(partial.State.Book.WorkingOrders);
        Assert.Contains("native_parent_terminated_partially_filled_unprotected", partial.UnavailableReasons);
        Assert.DoesNotContain(partial.Events, e => e.Event.Reason == "native_parent_physically_fully_filled");
    }

    [Theory]
    [InlineData("workflow")]
    [InlineData("quantity")]
    [InlineData("side")]
    [InlineData("parent")]
    [InlineData("oco")]
    [InlineData("protection")]
    [InlineData("repair")]
    [InlineData("stop_price")]
    public void NativeCommitmentRequiresItsExactEntryContractAndExplicitWorkflow(string failure)
    {
        var policy = Policy(failure == "workflow" ? ExecutionProtectionActivation.CustomPerFill : ExecutionProtectionActivation.NativeParentFullyFilled);
        var submitted = Advance(Seed(policy), Batch(1), [Entry(tif: SimulatedTimeInForce.Day)], policy);
        var child = Stop(known: 2) with { NativeParentCommitted = true };
        child = failure switch {
            "quantity" => child with { Command = child.Command with { Quantity = 9 } },
            "side" => child with { Command = child.Command with { Side = SimulatedOrderSide.Buy } },
            "parent" => child with { ParentOrderId = "missing" },
            "oco" => child with { Command = child.Command with { OcoGroupId = null } },
            "protection" => child with { IsProtection = false },
            "repair" => child with { IsRepair = true },
            "stop_price" => child with { Command = child.Command with { StopPrice = 0 } },
            _ => child };
        Assert.Throws<InvalidDataException>(() => Advance(submitted.State, Batch(2), [child], policy));
    }

    [Fact]
    public void NativeTermsCannotFirstAppearAfterAnyPhysicalFillEvenBeforeItsClientReport()
    {
        var policy = Policy(ExecutionProtectionActivation.NativeParentFullyFilled, latency: 1000);
        var partial = Advance(Seed(policy), Batch(2, trades: [Trade(2, shares: 3)]),
            [Entry(tif: SimulatedTimeInForce.Day)], policy);
        Assert.Throws<InvalidOperationException>(() => Advance(partial.State, Batch(3),
            [Stop(known: 3) with { NativeParentCommitted = true }], policy));
    }

    [Fact]
    public void ReplacementCannotDiscardNativeFlagChangeParentOrResizeTheCommittedEntry()
    {
        var policy = Policy(ExecutionProtectionActivation.NativeParentFullyFilled);
        var committed = Advance(Seed(policy), Batch(1), NativeBracket(), policy);
        var replacement = Stop(known: 2) with { NativeParentCommitted = true,
            Command = Stop(known: 2).Command with { Kind = ExecutionCommandKind.Replace, ClientOrderId = "new-stop", TargetClientOrderId = "stop" } };
        Assert.Throws<InvalidDataException>(() => Advance(committed.State, Batch(2),
            [replacement with { NativeParentCommitted = false }], policy));
        Assert.Throws<InvalidDataException>(() => Advance(committed.State, Batch(2),
            [replacement with { ParentOrderId = "other" }], policy));
        var resize = Entry("new-entry", known: 2, quantity: 9, tif: SimulatedTimeInForce.Day);
        resize = resize with { Command = resize.Command with { Kind = ExecutionCommandKind.Replace, TargetClientOrderId = "entry" } };
        Assert.Throws<InvalidDataException>(() => Advance(committed.State, Batch(2), [resize], policy));
        var replaced = Advance(committed.State, Batch(2), [replacement], policy);
        Assert.False(replaced.State.Book.WorkingOrders.ContainsKey("stop"));
        Assert.Equal(Int64.MaxValue, replaced.State.OrderActivationTimes["new-stop"]);
        Assert.Equal("entry", replaced.State.NativeChildCommitments!["new-stop"].EntryParentId);
        Assert.Throws<InvalidDataException>(() => Advance(replaced.State, Batch(3),
            [replacement with { NativeParentCommitted = false }], policy));
    }

    [Fact]
    public void SameQuantityEntryReplacementRetainsPhysicalNativeActivationForTheOriginalParent()
    {
        var policy = Policy(ExecutionProtectionActivation.NativeParentFullyFilled, latency: 1000);
        var committed = Advance(Seed(policy), Batch(1), NativeBracket(), policy);
        var replacement = Entry("new-entry", known: 2, tif: SimulatedTimeInForce.Day);
        replacement = replacement with { Command = replacement.Command with { Kind = ExecutionCommandKind.Replace, TargetClientOrderId = "entry" } };
        var replaced = Advance(committed.State, Batch(2), [replacement], policy);
        var full = Advance(replaced.State, Batch(3, trades: [Trade(3)]), policy: policy);
        Assert.Equal(Epoch + 3, full.State.OrderActivationTimes["stop"]);
        Assert.Equal("entry", full.State.NativeChildCommitments!["stop"].EntryParentId);
        Assert.DoesNotContain(full.Events, e => e.Event.IsRiskReducing && e.Event.LastFillQuantity > 0);
    }

    [Fact]
    public void NativeOcoGroupCannotBeSharedAcrossEntryParentsOrFirstAppearAtTheFillNanosecond()
    {
        var policy = Policy(ExecutionProtectionActivation.NativeParentFullyFilled);
        var committed = Advance(Seed(policy), Batch(1), NativeBracket(), policy);
        var other = Entry("other-entry", known: 2, tif: SimulatedTimeInForce.Day);
        var both = Advance(committed.State, Batch(2), [other], policy);
        var foreignChild = Stop(known: 3, parent: "other-entry") with { NativeParentCommitted = true,
            Command = Stop(known: 3).Command with { ClientOrderId = "foreign-stop" } };
        Assert.Throws<InvalidDataException>(() => Advance(both.State, Batch(3), [foreignChild], policy));
        // The same-time instruction is processed after the market observation;
        // it cannot retroactively become a broker-held bracket for that fill.
        Assert.Throws<InvalidDataException>(() => Advance(Seed(policy), Batch(2, trades: [Trade(2)]),
            [Entry(), Stop(known: 2) with { NativeParentCommitted = true }], policy));
    }
}
