using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class DeterministicExecutionSimulatorTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 9, 4, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MarketBuy_UsesAskWithoutChargingTheObservedSpreadTwice()
    {
        var result = new DeterministicExecutionSimulator().Simulate(new ExecutionSimulationRequest(
            SimulatedOrderSide.Buy,
            SimulatedOrderType.Market,
            100,
            Market(open: 100m, high: 103m, low: 99m, close: 102m, volume: 10_000m, bid: 99m, ask: 101m),
            Policy(maxParticipationPct: 10m, baseSlippageBps: 10m, impactBps: 20m)));

        Assert.Equal(SimulatedFillStatus.Filled, result.Status);
        Assert.Equal(100, result.FilledQuantity);
        Assert.Equal(200m, result.SpreadBps);
        Assert.Equal(2m, result.ImpactBps);
        Assert.Equal(12m, result.SlippageBps);
        Assert.Equal(101.1212m, result.AverageFillPrice);
    }

    [Fact]
    public void ParticipationCap_ProducesDeterministicPartialFill()
    {
        var request = new ExecutionSimulationRequest(
            SimulatedOrderSide.Buy,
            SimulatedOrderType.Market,
            5_000,
            Market(10m, 11m, 9m, 10m, 2_000m),
            Policy(maxParticipationPct: 10m));
        var simulator = new DeterministicExecutionSimulator();

        var first = simulator.Simulate(request);
        var replay = simulator.Simulate(request);

        Assert.Equal(first, replay);
        Assert.Equal(SimulatedFillStatus.PartialFill, first.Status);
        Assert.Equal(200, first.FilledQuantity);
        Assert.Equal(4_800, first.RemainingQuantity);
        Assert.Equal("participation_limited_partial_fill", first.Reason);
    }

    [Fact]
    public void EnabledParticipationCap_WithMissingVolume_FailsClosed()
    {
        var result = new DeterministicExecutionSimulator().Simulate(new ExecutionSimulationRequest(
            SimulatedOrderSide.Buy,
            SimulatedOrderType.Market,
            100,
            Market(10m, 10m, 10m, 10m, 0m),
            Policy(maxParticipationPct: 5m)));

        Assert.Equal(SimulatedFillStatus.NoFill, result.Status);
        Assert.Equal(100, result.RemainingQuantity);
        Assert.Equal("insufficient_executable_volume", result.Reason);
    }

    [Fact]
    public void LimitOrder_NotReached_DoesNotFill()
    {
        var result = new DeterministicExecutionSimulator().Simulate(new ExecutionSimulationRequest(
            SimulatedOrderSide.Buy,
            SimulatedOrderType.Limit,
            100,
            Market(10m, 11m, 9.75m, 10.5m, 2_000m),
            Policy(),
            LimitPrice: 9.50m));

        Assert.Equal(SimulatedFillStatus.NoFill, result.Status);
        Assert.Equal("limit_not_reached", result.Reason);
    }

    [Fact]
    public void BuyStop_GapThroughStop_UsesAdverseOpen()
    {
        var result = new DeterministicExecutionSimulator().Simulate(new ExecutionSimulationRequest(
            SimulatedOrderSide.Buy,
            SimulatedOrderType.Stop,
            10,
            Market(11m, 12m, 10.5m, 11.5m, 5_000m),
            Policy(),
            StopPrice: 10m));

        Assert.Equal(SimulatedFillStatus.Filled, result.Status);
        Assert.Equal(11m, result.AverageFillPrice);
    }

    [Fact]
    public void StopLimit_WithUnknownIntrabarSequence_DoesNotInventFill()
    {
        var result = new DeterministicExecutionSimulator().Simulate(new ExecutionSimulationRequest(
            SimulatedOrderSide.Buy,
            SimulatedOrderType.StopLimit,
            10,
            Market(9m, 11m, 8.5m, 10m, 5_000m),
            Policy(),
            LimitPrice: 10.20m,
            StopPrice: 10m));

        Assert.Equal(SimulatedFillStatus.NoFill, result.Status);
        Assert.Equal("ambiguous_intrabar_stop_limit_sequence", result.Reason);
    }

    [Fact]
    public void SellFill_AppliesFixedAndRegulatoryFees()
    {
        var result = new DeterministicExecutionSimulator().Simulate(new ExecutionSimulationRequest(
            SimulatedOrderSide.Sell,
            SimulatedOrderType.Market,
            1_000,
            Market(100m, 101m, 99m, 100m, 1_000_000m),
            Policy(fixedFee: 1m, secFeeRate: 0.0000278m, tafPerShare: 0.000166m, tafCap: 8.30m)));

        Assert.Equal(SimulatedFillStatus.Filled, result.Status);
        Assert.Equal(3.946m, result.Fees);
    }

    [Fact]
    public void NonUtcMarketTimestamp_IsRejected()
    {
        var localTimestamp = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.FromHours(-4));
        var request = new ExecutionSimulationRequest(
            SimulatedOrderSide.Buy,
            SimulatedOrderType.Market,
            1,
            new ExecutionMarketSnapshot(localTimestamp, 10m, 10m, 10m, 10m, 100m),
            Policy());

        var error = Assert.Throws<ArgumentException>(() =>
            new DeterministicExecutionSimulator().Simulate(request));
        Assert.Contains("UTC", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BarOnlyFill_WithoutSyntheticOrPreAppliedFriction_IsRejected()
    {
        var request = new ExecutionSimulationRequest(
            SimulatedOrderSide.Buy,
            SimulatedOrderType.Market,
            1,
            Market(10m, 10m, 10m, 10m, 100m),
            new ExecutionSimulationPolicy(0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m));

        var error = Assert.Throws<ArgumentException>(() =>
            new DeterministicExecutionSimulator().Simulate(request));

        Assert.Contains("requires synthetic friction", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Advance_TwoOrdersShareOneSliceParticipationBudget_RiskReductionFirst()
    {
        var slice = Slice(sequence: 1, volume: 1_000m, price: 10m);
        var state = ExecutionBookState.Empty with
        {
            Positions = new Dictionary<string, SimulatedPosition>(StringComparer.OrdinalIgnoreCase)
            {
                ["TEST"] = new("TEST", 80, 9m, 0m)
            }
        };
        var commands = new[]
        {
            Command("new-entry", SimulatedOrderSide.Buy, 100, slice.StartUtc.AddMinutes(-1), 1, false),
            Command("risk-exit", SimulatedOrderSide.Sell, 80, slice.StartUtc.AddMinutes(-1), 2, true)
        };

        var result = new DeterministicExecutionSimulator().Advance(new ExecutionStepRequest(
            state,
            slice,
            commands,
            Policy(maxParticipationPct: 10m)));

        Assert.Collection(
            result.Events,
            item =>
            {
                Assert.Equal("risk-exit", item.ClientOrderId);
                Assert.Equal(80, item.LastFillQuantity);
            },
            item =>
            {
                Assert.Equal("new-entry", item.ClientOrderId);
                Assert.Equal(20, item.LastFillQuantity);
                Assert.Equal("partially_filled", item.State);
            });
        Assert.Equal(20, result.State.Positions["TEST"].SignedQuantity);
    }

    [Fact]
    public void Advance_PartialRemainderProgressesAcrossSlicesWithoutReusingVolume()
    {
        var simulator = new DeterministicExecutionSimulator();
        var firstSlice = Slice(sequence: 1, volume: 1_000m, price: 10m);
        var command = Command(
            "entry",
            SimulatedOrderSide.Buy,
            250,
            firstSlice.StartUtc.AddMinutes(-1),
            1,
            false);

        var first = simulator.Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty,
            firstSlice,
            [command],
            Policy(maxParticipationPct: 10m)));
        var second = simulator.Advance(new ExecutionStepRequest(
            first.State,
            Slice(sequence: 2, volume: 1_000m, price: 10m, minuteOffset: 5),
            Array.Empty<SimulatedOrderCommand>(),
            Policy(maxParticipationPct: 10m)));
        var third = simulator.Advance(new ExecutionStepRequest(
            second.State,
            Slice(sequence: 3, volume: 1_000m, price: 10m, minuteOffset: 10),
            Array.Empty<SimulatedOrderCommand>(),
            Policy(maxParticipationPct: 10m)));

        Assert.Equal((100, 100, 50), (
            Assert.Single(first.Events).LastFillQuantity,
            Assert.Single(second.Events).LastFillQuantity,
            Assert.Single(third.Events).LastFillQuantity));
        Assert.Equal("filled", Assert.Single(third.Events).State);
        Assert.Empty(third.State.WorkingOrders);
        Assert.Equal(250, third.State.Positions["TEST"].SignedQuantity);
    }

    [Fact]
    public void Advance_CommandKnownAfterSliceStart_WaitsForNextSlice()
    {
        var simulator = new DeterministicExecutionSimulator();
        var firstSlice = Slice(sequence: 1, volume: 1_000m, price: 10m);
        var command = Command(
            "late-entry",
            SimulatedOrderSide.Buy,
            10,
            firstSlice.StartUtc.AddMinutes(1),
            1,
            false);

        var first = simulator.Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty,
            firstSlice,
            [command],
            Policy(maxParticipationPct: 10m)));
        var second = simulator.Advance(new ExecutionStepRequest(
            first.State,
            Slice(sequence: 2, volume: 1_000m, price: 10m, minuteOffset: 5),
            Array.Empty<SimulatedOrderCommand>(),
            Policy(maxParticipationPct: 10m)));

        Assert.Empty(first.Events);
        Assert.Single(first.State.WorkingOrders);
        Assert.Equal(10, Assert.Single(second.Events).LastFillQuantity);
    }

    [Fact]
    public void Advance_ReplayedSlice_IsIdempotentNoOp()
    {
        var simulator = new DeterministicExecutionSimulator();
        var slice = Slice(sequence: 1, volume: 1_000m, price: 10m);
        var first = simulator.Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty,
            slice,
            [Command("entry", SimulatedOrderSide.Buy, 10, slice.StartUtc.AddMinutes(-1), 1, false)],
            Policy(maxParticipationPct: 10m)));

        var replay = simulator.Advance(new ExecutionStepRequest(
            first.State,
            slice,
            Array.Empty<SimulatedOrderCommand>(),
            Policy(maxParticipationPct: 10m)));

        Assert.Same(first.State, replay.State);
        Assert.Empty(replay.Events);
    }

    [Fact]
    public void Advance_PartialScaleOutLeavesExactRemainderAndRealizesProfit()
    {
        var slice = Slice(sequence: 1, volume: 10_000m, price: 12m);
        var state = ExecutionBookState.Empty with
        {
            Positions = new Dictionary<string, SimulatedPosition>(StringComparer.OrdinalIgnoreCase)
            {
                ["TEST"] = new("TEST", 100, 10m, 0m)
            }
        };

        var result = new DeterministicExecutionSimulator().Advance(new ExecutionStepRequest(
            state,
            slice,
            [Command("scale-out", SimulatedOrderSide.Sell, 40, slice.StartUtc.AddMinutes(-1), 1, true)],
            Policy(maxParticipationPct: 10m)));

        var position = result.State.Positions["TEST"];
        Assert.Equal(60, position.SignedQuantity);
        Assert.Equal(10m, position.AveragePrice);
        Assert.Equal(80m, position.RealizedProfit);
    }

    [Fact]
    public void Advance_ZeroCapacityCancelsImmediateOrCancelButRetainsDayOrder()
    {
        var slice = Slice(sequence: 1, volume: 0m, price: 10m);
        var day = Command("day", SimulatedOrderSide.Buy, 10, slice.StartUtc.AddMinutes(-1), 1, false);
        var immediate = Command("ioc", SimulatedOrderSide.Buy, 10, slice.StartUtc.AddMinutes(-1), 2, false) with
        {
            TimeInForce = SimulatedTimeInForce.ImmediateOrCancel
        };

        var result = new DeterministicExecutionSimulator().Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty,
            slice,
            [day, immediate],
            Policy(maxParticipationPct: 10m)));

        var cancellation = Assert.Single(result.Events);
        Assert.Equal("ioc", cancellation.ClientOrderId);
        Assert.Equal("canceled", cancellation.State);
        Assert.True(result.State.WorkingOrders.ContainsKey("day"));
        Assert.False(result.State.WorkingOrders.ContainsKey("ioc"));
    }

    [Fact]
    public void Advance_ReplacementCancelsPredecessorAndUsesSuccessorIdentity()
    {
        var simulator = new DeterministicExecutionSimulator();
        var firstSlice = Slice(sequence: 1, volume: 1_000m, price: 10m);
        var lateLimit = Command(
            "original",
            SimulatedOrderSide.Buy,
            10,
            firstSlice.StartUtc.AddMinutes(-1),
            1,
            false) with
        {
            OrderType = SimulatedOrderType.Limit,
            LimitPrice = 9m
        };
        var first = simulator.Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty,
            firstSlice,
            [lateLimit],
            Policy(maxParticipationPct: 10m)));
        var nextSlice = Slice(sequence: 2, volume: 1_000m, price: 10m, minuteOffset: 5);
        var replacement = Command(
            "replacement",
            SimulatedOrderSide.Buy,
            10,
            nextSlice.StartUtc.AddMinutes(-1),
            2,
            false) with
        {
            Kind = ExecutionCommandKind.Replace,
            TargetClientOrderId = "original"
        };

        var second = simulator.Advance(new ExecutionStepRequest(
            first.State,
            nextSlice,
            [replacement],
            Policy(maxParticipationPct: 10m)));

        Assert.Collection(
            second.Events,
            item =>
            {
                Assert.Equal("original", item.ClientOrderId);
                Assert.Equal("replaced", item.Reason);
            },
            item =>
            {
                Assert.Equal("replacement", item.ClientOrderId);
                Assert.Equal("filled", item.State);
            });
        Assert.False(second.State.WorkingOrders.ContainsKey("original"));
        Assert.True(second.State.KnownOrderContracts.ContainsKey("replacement"));
    }

    [Fact]
    public void Advance_ShuffledEqualTimeCommandsProduceIdenticalEvents()
    {
        var slice = Slice(sequence: 1, volume: 1_000m, price: 10m);
        var knownAt = slice.StartUtc.AddMinutes(-1);
        var alpha = Command("alpha", SimulatedOrderSide.Buy, 40, knownAt, 1, false);
        var beta = Command("beta", SimulatedOrderSide.Buy, 40, knownAt, 1, false);
        var requestPolicy = Policy(maxParticipationPct: 5m);
        var simulator = new DeterministicExecutionSimulator();

        var first = simulator.Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty, slice, [beta, alpha], requestPolicy));
        var second = simulator.Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty, slice, [alpha, beta], requestPolicy));

        Assert.Equal(first.Events, second.Events);
        Assert.Equal(
            first.State.WorkingOrders.OrderBy(item => item.Key).ToArray(),
            second.State.WorkingOrders.OrderBy(item => item.Key).ToArray());
    }

    [Fact]
    public void Advance_ConditionalFillOverlappingMidSliceCancel_IsRejectedAsAmbiguous()
    {
        var simulator = new DeterministicExecutionSimulator();
        var firstSlice = Slice(sequence: 1, volume: 1_000m, price: 10m);
        var restingLimit = Command(
            "resting-limit",
            SimulatedOrderSide.Buy,
            10,
            firstSlice.StartUtc.AddMinutes(-1),
            1,
            false) with
        {
            OrderType = SimulatedOrderType.Limit,
            LimitPrice = 9m
        };
        var first = simulator.Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty,
            firstSlice,
            [restingLimit],
            Policy(maxParticipationPct: 10m)));
        var fillSlice = Slice(sequence: 2, volume: 1_000m, price: 9m, minuteOffset: 5);
        var lateCancel = Command(
            "cancel-command",
            SimulatedOrderSide.Buy,
            0,
            fillSlice.StartUtc.AddMinutes(1),
            2,
            false) with
        {
            Kind = ExecutionCommandKind.Cancel,
            TargetClientOrderId = "resting-limit"
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            simulator.Advance(new ExecutionStepRequest(
                first.State,
                fillSlice,
                [lateCancel],
                Policy(maxParticipationPct: 10m))));

        Assert.Contains("Split the slice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Advance_PartialImmediateOrCancelEmitsFillThenRemainderCancellation()
    {
        var slice = Slice(sequence: 1, volume: 1_000m, price: 10m);
        var command = Command(
            "ioc",
            SimulatedOrderSide.Buy,
            150,
            slice.StartUtc.AddMinutes(-1),
            1,
            false) with
        {
            TimeInForce = SimulatedTimeInForce.ImmediateOrCancel
        };

        var result = new DeterministicExecutionSimulator().Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty,
            slice,
            [command],
            Policy(maxParticipationPct: 10m)));

        Assert.Collection(
            result.Events,
            item =>
            {
                Assert.Equal("partially_filled", item.State);
                Assert.Equal(100, item.LastFillQuantity);
                Assert.Equal(50, item.RemainingQuantity);
            },
            item =>
            {
                Assert.Equal("canceled", item.State);
                Assert.Equal("immediate_or_cancel_remainder", item.Reason);
                Assert.Equal(50, item.RemainingQuantity);
            });
        Assert.Empty(result.State.WorkingOrders);
    }

    [Fact]
    public void Advance_RiskReducingOrderThatCouldReversePositionIsRejected()
    {
        var slice = Slice(sequence: 1, volume: 10_000m, price: 10m);
        var state = ExecutionBookState.Empty with
        {
            Positions = new Dictionary<string, SimulatedPosition>(StringComparer.OrdinalIgnoreCase)
            {
                ["TEST"] = new("TEST", 10, 9m, 0m)
            }
        };
        var invalidExit = Command(
            "oversized-exit",
            SimulatedOrderSide.Sell,
            11,
            slice.StartUtc.AddMinutes(-1),
            1,
            true);

        var error = Assert.Throws<InvalidOperationException>(() =>
            new DeterministicExecutionSimulator().Advance(new ExecutionStepRequest(
                state,
                slice,
                [invalidExit],
                Policy(maxParticipationPct: 10m))));

        Assert.Contains("does not reduce the current position", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Advance_EndOfSliceQuoteCannotPriceStartOfSliceFill()
    {
        var start = Timestamp;
        var slice = new ExecutionMarketSlice(
            "TEST",
            start,
            start.AddMinutes(5),
            start.AddMinutes(5),
            1,
            true,
            10m,
            12m,
            8m,
            11m,
            1_000m,
            8m,
            12m,
            start.AddMinutes(5));
        var order = Command("entry", SimulatedOrderSide.Buy, 10, start.AddMinutes(-1), 1, false);

        var result = new DeterministicExecutionSimulator().Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty,
            slice,
            [order],
            Policy(maxParticipationPct: 10m, syntheticSpreadBps: 100m)));

        Assert.Equal(10.05m, Assert.Single(result.Events).FillPrice);
    }

    [Fact]
    public void Advance_CompetingStopAndTargetCannotReversePosition()
    {
        var start = Timestamp;
        var slice = new ExecutionMarketSlice(
            "TEST", start, start.AddMinutes(5), start.AddMinutes(5), 1, true,
            10m, 12m, 8m, 10m, 10_000m);
        var state = ExecutionBookState.Empty with
        {
            Positions = new Dictionary<string, SimulatedPosition>(StringComparer.OrdinalIgnoreCase)
            {
                ["TEST"] = new("TEST", 100, 10m, 0m)
            }
        };
        var target = Command("target", SimulatedOrderSide.Sell, 100, start.AddMinutes(-1), 1, true) with
        {
            OrderType = SimulatedOrderType.Limit,
            LimitPrice = 11m
        };
        var stop = Command("stop", SimulatedOrderSide.Sell, 100, start.AddMinutes(-1), 2, true) with
        {
            OrderType = SimulatedOrderType.Stop,
            StopPrice = 9m
        };

        var result = new DeterministicExecutionSimulator().Advance(new ExecutionStepRequest(
            state,
            slice,
            [target, stop],
            Policy(maxParticipationPct: 10m)));

        Assert.Collection(
            result.Events,
            item =>
            {
                Assert.Equal("stop", item.ClientOrderId);
                Assert.Equal("filled", item.State);
            },
            item =>
            {
                Assert.Equal("target", item.ClientOrderId);
                Assert.Equal("canceled", item.State);
                Assert.Equal("position_quantity_unavailable", item.Reason);
            });
        Assert.Equal(0, result.State.Positions["TEST"].SignedQuantity);
    }

    [Fact]
    public void Advance_ActivatedStopLimitBecomesWorkingLimitOnLaterSlice()
    {
        var simulator = new DeterministicExecutionSimulator();
        var firstStart = Timestamp;
        var firstSlice = new ExecutionMarketSlice(
            "TEST", firstStart, firstStart.AddMinutes(5), firstStart.AddMinutes(5), 1, true,
            9m, 11m, 8.5m, 10m, 1_000m);
        var order = Command("stop-limit", SimulatedOrderSide.Buy, 10, firstStart.AddMinutes(-1), 1, false) with
        {
            OrderType = SimulatedOrderType.StopLimit,
            StopPrice = 10m,
            LimitPrice = 10.20m
        };

        var first = simulator.Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty,
            firstSlice,
            [order],
            Policy(maxParticipationPct: 10m)));
        var secondStart = firstStart.AddMinutes(5);
        var secondSlice = new ExecutionMarketSlice(
            "TEST", secondStart, secondStart.AddMinutes(5), secondStart.AddMinutes(5), 2, true,
            9.5m, 9.8m, 9m, 9.4m, 1_000m);
        var second = simulator.Advance(new ExecutionStepRequest(
            first.State,
            secondSlice,
            Array.Empty<SimulatedOrderCommand>(),
            Policy(maxParticipationPct: 10m)));

        Assert.Equal("triggered", Assert.Single(first.Events).State);
        Assert.True(first.State.WorkingOrders["stop-limit"].StopTriggered);
        Assert.Equal("filled", Assert.Single(second.Events).State);
        Assert.Equal(9.5m, Assert.Single(second.Events).FillPrice);
    }

    [Fact]
    public void Advance_ReplacementAfterPartialFillCarriesQuantityAndFees()
    {
        var simulator = new DeterministicExecutionSimulator();
        var firstSlice = Slice(sequence: 1, volume: 500m, price: 10m);
        var original = Command(
            "original",
            SimulatedOrderSide.Sell,
            100,
            firstSlice.StartUtc.AddMinutes(-1),
            1,
            false);
        var policy = Policy(
            maxParticipationPct: 10m,
            fixedFee: 1m,
            tafPerShare: 1m,
            tafCap: 60m);
        var first = simulator.Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty,
            firstSlice,
            [original],
            policy));
        var secondSlice = Slice(sequence: 2, volume: 1_000m, price: 10m, minuteOffset: 5);
        var replacement = Command(
            "replacement",
            SimulatedOrderSide.Sell,
            100,
            secondSlice.StartUtc.AddMinutes(-1),
            2,
            false) with
        {
            Kind = ExecutionCommandKind.Replace,
            TargetClientOrderId = "original"
        };
        var second = simulator.Advance(new ExecutionStepRequest(
            first.State,
            secondSlice,
            [replacement],
            policy));

        Assert.Equal(50, Assert.Single(first.Events).LastFillQuantity);
        Assert.Collection(
            second.Events,
            item => Assert.Equal("replaced", item.Reason),
            item =>
            {
                Assert.Equal("filled", item.State);
                Assert.Equal(100, item.CumulativeFillQuantity);
                Assert.Equal(10m, item.Fees);
            });
        Assert.Equal(61m, second.State.AccumulatedFees);
        Assert.Equal(-100, second.State.Positions["TEST"].SignedQuantity);
    }

    [Fact]
    public void BuyFill_DoesNotApplySellSideRegulatoryFees()
    {
        var result = new DeterministicExecutionSimulator().Simulate(new ExecutionSimulationRequest(
            SimulatedOrderSide.Buy,
            SimulatedOrderType.Market,
            1_000,
            Market(100m, 101m, 99m, 100m, 1_000_000m),
            Policy(secFeeRate: 0.0000278m, tafPerShare: 0.000166m, tafCap: 8.30m)));

        Assert.Equal(0m, result.Fees);
    }

    [Fact]
    public void Advance_ReplayedSliceWithCommands_IsRejected()
    {
        var simulator = new DeterministicExecutionSimulator();
        var slice = Slice(sequence: 1, volume: 1_000m, price: 10m);
        var first = simulator.Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty,
            slice,
            Array.Empty<SimulatedOrderCommand>(),
            Policy(maxParticipationPct: 10m)));

        var error = Assert.Throws<InvalidOperationException>(() =>
            simulator.Advance(new ExecutionStepRequest(
                first.State,
                slice,
                [Command("late", SimulatedOrderSide.Buy, 10, slice.StartUtc, 1, false)],
                Policy(maxParticipationPct: 10m))));

        Assert.Contains("already processed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Advance_DuplicateClientOrderIdWithDifferentContract_IsRejected()
    {
        var simulator = new DeterministicExecutionSimulator();
        var firstSlice = Slice(sequence: 1, volume: 0m, price: 10m);
        var first = simulator.Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty,
            firstSlice,
            [Command("stable-id", SimulatedOrderSide.Buy, 10, firstSlice.StartUtc.AddMinutes(-1), 1, false)],
            Policy(maxParticipationPct: 10m)));
        var secondSlice = Slice(sequence: 2, volume: 0m, price: 10m, minuteOffset: 5);
        var conflicting = Command(
            "stable-id",
            SimulatedOrderSide.Buy,
            11,
            secondSlice.StartUtc.AddMinutes(-1),
            2,
            false);

        var error = Assert.Throws<InvalidOperationException>(() =>
            simulator.Advance(new ExecutionStepRequest(
                first.State,
                secondSlice,
                [conflicting],
                Policy(maxParticipationPct: 10m))));

        Assert.Contains("different order contract", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Advance_ReplacementCannotRemoveRiskReducingClassification()
    {
        var simulator = new DeterministicExecutionSimulator();
        var firstSlice = Slice(sequence: 1, volume: 1_000m, price: 10m);
        var state = ExecutionBookState.Empty with
        {
            Positions = new Dictionary<string, SimulatedPosition>(StringComparer.OrdinalIgnoreCase)
            {
                ["TEST"] = new("TEST", 10, 9m, 0m)
            }
        };
        var stop = Command(
            "stop",
            SimulatedOrderSide.Sell,
            10,
            firstSlice.StartUtc.AddMinutes(-1),
            1,
            true) with
        {
            OrderType = SimulatedOrderType.Stop,
            StopPrice = 9m
        };
        var first = simulator.Advance(new ExecutionStepRequest(
            state,
            firstSlice,
            [stop],
            Policy(maxParticipationPct: 10m)));
        var secondSlice = Slice(sequence: 2, volume: 1_000m, price: 10m, minuteOffset: 5);
        var replacement = Command(
            "replacement",
            SimulatedOrderSide.Sell,
            10,
            secondSlice.StartUtc.AddMinutes(-1),
            2,
            false) with
        {
            Kind = ExecutionCommandKind.Replace,
            TargetClientOrderId = "stop",
            OrderType = SimulatedOrderType.Stop,
            StopPrice = 9.5m
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            simulator.Advance(new ExecutionStepRequest(
                first.State,
                secondSlice,
                [replacement],
                Policy(maxParticipationPct: 10m))));

        Assert.Contains("risk classification", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Advance_ActivatedImmediateOrCancelStopLimit_TerminatesSameSlice()
    {
        var start = Timestamp;
        var slice = new ExecutionMarketSlice(
            "TEST", start, start.AddMinutes(5), start.AddMinutes(5), 1, true,
            9m, 11m, 8.5m, 10m, 1_000m);
        var order = Command("ioc-stop-limit", SimulatedOrderSide.Buy, 10, start.AddMinutes(-1), 1, false) with
        {
            OrderType = SimulatedOrderType.StopLimit,
            StopPrice = 10m,
            LimitPrice = 10.20m,
            TimeInForce = SimulatedTimeInForce.ImmediateOrCancel
        };

        var result = new DeterministicExecutionSimulator().Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty,
            slice,
            [order],
            Policy(maxParticipationPct: 10m)));

        Assert.Collection(
            result.Events,
            item => Assert.Equal("triggered", item.State),
            item =>
            {
                Assert.Equal("canceled", item.State);
                Assert.Equal("immediate_or_cancel_unfilled", item.Reason);
            });
        Assert.Empty(result.State.WorkingOrders);
    }

    [Fact]
    public void Advance_OrderContractHistoryFailsClosedAtConfiguredLimit()
    {
        var simulator = new DeterministicExecutionSimulator();
        var firstSlice = Slice(sequence: 1, volume: 1_000m, price: 10m);
        var first = simulator.Advance(new ExecutionStepRequest(
            ExecutionBookState.Empty,
            firstSlice,
            [Command("first", SimulatedOrderSide.Buy, 10, firstSlice.StartUtc.AddMinutes(-1), 1, false)],
            Policy(maxParticipationPct: 10m, maximumTrackedOrderContracts: 1)));
        var secondSlice = Slice(sequence: 2, volume: 1_000m, price: 10m, minuteOffset: 5);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            simulator.Advance(new ExecutionStepRequest(
                first.State,
                secondSlice,
                [Command("second", SimulatedOrderSide.Buy, 10, secondSlice.StartUtc.AddMinutes(-1), 2, false)],
                Policy(maxParticipationPct: 10m, maximumTrackedOrderContracts: 1))));

        Assert.Contains("order-contract limit", exception.Message, StringComparison.Ordinal);
    }

    private static ExecutionMarketSnapshot Market(
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal volume,
        decimal? bid = null,
        decimal? ask = null) =>
        new(Timestamp, open, high, low, close, volume, bid, ask);

    private static ExecutionSimulationPolicy Policy(
        decimal maxParticipationPct = 0m,
        decimal baseSlippageBps = 0m,
        decimal syntheticSpreadBps = 0m,
        decimal impactBps = 0m,
        decimal fixedFee = 0m,
        decimal secFeeRate = 0m,
        decimal tafPerShare = 0m,
        decimal tafCap = 0m,
        int maximumTrackedOrderContracts = 1_000_000) =>
        new(
            maxParticipationPct,
            baseSlippageBps,
            syntheticSpreadBps,
            impactBps,
            fixedFee,
            fixedFee,
            secFeeRate,
            tafPerShare,
            tafCap,
            InputPriceIncludesExecutionFriction: true,
            MaximumTrackedOrderContracts: maximumTrackedOrderContracts);

    private static ExecutionMarketSlice Slice(
        long sequence,
        decimal volume,
        decimal price,
        int minuteOffset = 0)
    {
        var start = Timestamp.AddMinutes(minuteOffset);
        var end = start.AddMinutes(5);
        return new ExecutionMarketSlice(
            "TEST",
            start,
            end,
            end,
            sequence,
            true,
            price,
            price,
            price,
            price,
            volume);
    }

    private static SimulatedOrderCommand Command(
        string clientOrderId,
        SimulatedOrderSide side,
        int quantity,
        DateTimeOffset knownAtUtc,
        long sequence,
        bool riskReducing) =>
        new(
            ExecutionCommandKind.Submit,
            clientOrderId,
            "TEST",
            side,
            SimulatedOrderType.Market,
            quantity,
            knownAtUtc,
            sequence,
            riskReducing);
}
