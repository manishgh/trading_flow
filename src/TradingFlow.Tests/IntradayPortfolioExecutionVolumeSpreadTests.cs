using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Tests;

public sealed partial class IntradayPortfolioExecutionTests
{
    // Zero disables ratio filtering entirely: keep a negative audited fee-net ratio and choose the first sized alternative.
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void VolumeSpread_NoRewardRiskFilterSelectsFirstSizedAlternative(bool disabled, int selectedIndex)
    {
        var bars = VolumeBars();
        var minimum = disabled ? 0m : 1.5m;
        var first = VolumeSignal(bars, bottom: 98.65m, top: 99m, target: 100.06m, minimumRewardRisk: minimum);
        var second = VolumeSignal(bars, index: 1, minimumRewardRisk: minimum);
        var result = RunVolume(bars, [VolumeCandidate([first, second])], noRewardRiskFilter: disabled);
        var selected = Assert.Single(result.SelectedCandidates).ZoneExecution!.Evidence.VolumeSpread!;
        Assert.Equal(selectedIndex, selected.OrderedAlternativeIndex);
        Assert.Equal(minimum, selected.MinimumRewardRisk);
        Assert.Equal(24, selected.PlannedQuantity);
        Assert.True(selected.EstimatedNetRiskDollars > 0);
        Assert.Single(result.PortfolioState.AdmittedOrders);
        if (disabled)
        {
            Assert.True(selected.EstimatedNetRewardDollars < 0);
            Assert.True(selected.SelectedRewardRisk < 0);
            Assert.Equal("selected_with_minimum_reward_risk_filter_disabled", Assert.Single(result.AlternativeDecisions).Reason);
            Assert.Equal("actual_fill_zone_target_not_favorable", Assert.Single(result.Trades).ExitReason);
        }
        else
        {
            Assert.True(selected.SelectedRewardRisk >= 1.5m);
            Assert.Equal("selected_after_ticket_fee_reward_risk", result.AlternativeDecisions[1].Reason);
            Assert.False(result.AlternativeDecisions[0].Selected);
        }
        Assert.Empty(result.Failures);
    }

    // Disabled ratio filtering cannot bypass the structural price/ATR checks or make a shared risk planner fund an unaffordable share.
    [Theory]
    [InlineData("target")]
    [InlineData("atr")]
    [InlineData("quantity")]
    public void VolumeSpread_DisabledFilterRetainsGeometryAndSizingGuards(string failure)
    {
        var bars = VolumeBars();
        var signal = VolumeSignal(bars, minimumRewardRisk: 0m);
        if (failure == "target") signal = signal with { FixedTargetPrice = 100.04m };
        if (failure == "atr") signal = signal with { Evidence = signal.Evidence with { Atr = null } };
        var portfolio = failure == "quantity" ? VolumePortfolio() with { MaxPositionNotionalPct = .0001m } : VolumePortfolio();
        var result = RunVolume(bars, [VolumeCandidate([signal])], portfolio, noRewardRiskFilter: true);
        Assert.Empty(result.SelectedCandidates);
        Assert.Empty(result.PortfolioState.Fills);
        Assert.False(Assert.Single(result.Dispositions).Accepted);
    }

    // The first alternative passes at Python's 100 shares but fails at the actual capped shares; only its successor reserves an order.
    [Fact]
    public void VolumeSpread_UsesCappedQuantityAndContinuesOrderedAlternatives()
    {
        var bars = VolumeBars();
        var first = VolumeSignal(bars, bottom: 98.65m, top: 99m);
        var second = VolumeSignal(bars, index: 1);
        var result = RunVolume(bars, [VolumeCandidate([first, second])]);
        var rejected = result.AlternativeDecisions[0];
        Assert.Equal(24, rejected.PlannedQuantity);
        Assert.False(rejected.Selected);
        Assert.True(rejected.RewardRisk < 1.5m);
        Assert.True(((102.5m - 100.05m) * 100 - 2) / ((100.05m - 98.5m) * 100 + 2) > 1.5m);
        Assert.True(result.AlternativeDecisions[1].Selected);
        Assert.Single(result.PortfolioState.AdmittedOrders);
        Assert.Single(result.Dispositions);
        var selected = Assert.Single(result.SelectedCandidates);
        Assert.Equal(second.Evidence.Zone.ZoneId, selected.ZoneExecution!.Evidence.Zone.ZoneId);
        Assert.Equal(1, selected.ZoneExecution.Evidence.VolumeSpread!.OrderedAlternativeIndex);
        Assert.Equal(second.InitialStopPrice, Assert.Single(result.Trades).StopLossPrice);
        Assert.Equal(24, selected.ZoneExecution.Evidence.VolumeSpread.PlannedQuantity);
        Assert.Empty(result.Failures);
    }

    // The literal threshold is inclusive after fees and final quantity, with no extra exit-spread or slippage term in this ratio.
    [Fact]
    public void VolumeSpread_RewardRiskEqualityIsAccepted()
    {
        var bars = VolumeBars();
        var signal = VolumeSignal(bars, target: 102.10m);
        var result = RunVolume(bars, [VolumeCandidate([signal])], VolumePortfolio() with { MaxPositionNotionalPct = 20.1m });
        var decision = Assert.Single(result.AlternativeDecisions);
        Assert.Equal(20, decision.PlannedQuantity);
        Assert.Equal(26m, decision.EstimatedNetRiskDollars);
        Assert.Equal(39m, decision.EstimatedNetRewardDollars);
        Assert.Equal(1.5m, decision.RewardRisk);
        Assert.True(decision.Selected);
    }

    // An empty result has one group rejection, and no rejected alternative creates a reserved parent or simulated fill.
    [Fact]
    public void VolumeSpread_AllRatiosFailWithoutReservingCapital()
    {
        var bars = VolumeBars();
        var result = RunVolume(bars, [VolumeCandidate([VolumeSignal(bars, bottom: 98.65m, top: 99m)])]);
        Assert.Empty(result.PortfolioState.AdmittedOrders);
        Assert.Empty(result.PortfolioState.Fills);
        Assert.Empty(result.SelectedCandidates);
        Assert.False(Assert.Single(result.Dispositions).Accepted);
        Assert.False(Assert.Single(result.AlternativeDecisions).Selected);
    }

    // Both actual fill sides use the raw opening plus one synthetic half-spread and one slippage charge; ticket fees occur once per side.
    [Theory]
    [InlineData("long", 100.15, 99.85, 24)]
    [InlineData("short", 99.85, 100.15, 25)]
    public void VolumeSpread_SimulatorChargesSpreadAndSlippageOnce(string direction, double entryPrice, double exitPrice, int shares)
    {
        var bars = VolumeBars(direction);
        var result = RunVolume(bars, [VolumeCandidate([VolumeSignal(bars, direction)])]);
        var trade = Assert.Single(result.Trades);
        Assert.Equal((decimal)entryPrice, trade.EntryPrice);
        Assert.Equal((decimal)exitPrice, trade.ExitPrice);
        Assert.Equal(shares, trade.ShareQuantity);
        Assert.Equal(2m, trade.Fees);
        Assert.All(result.FillEvents, x =>
        {
            Assert.Equal(10m, x.Event.SpreadBps);
            Assert.Equal(15m, x.Event.SlippageBps);
        });
        Assert.Empty(result.Failures);
    }

    // A future opening cannot change which frozen alternative passes admission; its actual gap fill remains in the accounting ledger.
    [Fact]
    public void VolumeSpread_SelectionDoesNotInspectCurrentOpening()
    {
        var bars = VolumeBars();
        var candidate = VolumeCandidate([VolumeSignal(bars, bottom: 98.65m, top: 99m), VolumeSignal(bars, index: 1)]);
        var normal = RunVolume(bars, [candidate]);
        bars[2] = Bar(10, 98, 98, 98, 98);
        var gap = RunVolume(bars, [candidate]);
        Assert.Equal(normal.AlternativeDecisions, gap.AlternativeDecisions);
        Assert.Equal(normal.PortfolioState.AdmittedOrders, gap.PortfolioState.AdmittedOrders);
        var trade = Assert.Single(gap.Trades);
        Assert.Equal(98.147m, trade.EntryPrice);
        Assert.Equal("gap_fill_through_structural_stop", trade.ExitReason);
        Assert.True(trade.ShareQuantity > 0);
        Assert.Empty(gap.Failures);
    }

    // Malformed later alternatives invalidate the frozen group rather than letting a favorable first member hide future or substituted evidence.
    [Theory]
    [InlineData("future_target")]
    [InlineData("future_source")]
    [InlineData("spread")]
    [InlineData("stop")]
    [InlineData("ordinal")]
    [InlineData("rvol")]
    public void VolumeSpread_RejectsMalformedFrozenGroup(string mutation)
    {
        var bars = VolumeBars();
        var first = VolumeSignal(bars);
        var later = VolumeSignal(bars, bottom: 99.1m, top: 99.6m, index: 1);
        var e = later.Evidence;
        if (mutation == "future_target") e = e with { TargetZone = e.TargetZone! with { KnownAtUtc = Start.AddMinutes(10) } };
        if (mutation == "future_source") later = later with { ConfirmationBar = later.ConfirmationBar with { KnownAtUtc = Start.AddMinutes(15) } };
        if (mutation == "spread") e = e with { VolumeSpread = e.VolumeSpread! with { AskPrice = 100.06m } };
        if (mutation == "stop") later = later with { InitialStopPrice = 98.9m };
        if (mutation == "ordinal") e = e with { VolumeSpread = e.VolumeSpread! with { OrderedAlternativeIndex = 0 } };
        if (mutation == "rvol") e = e with { SlotRelativeVolume = 1.1m };
        later = later with { Evidence = e };
        var result = RunVolume(bars, [VolumeCandidate([first, later])]);
        Assert.Empty(result.PortfolioState.Fills);
        Assert.False(Assert.Single(result.Dispositions).Accepted);
    }

    // The close-minus-ten opening remains executable and cancels partial remainder there; later admissions cannot borrow the liquidation slot.
    [Fact]
    public void VolumeSpread_InclusiveLateCutoffRetainsFixedPartialProtection()
    {
        var bars = VolumeBars(minute: 35);
        bars[6] = bars[6] with { Volume = 50 };
        var signal = VolumeSignal(bars, minute: 35);
        var result = RunVolume(bars, [VolumeCandidate([signal]), VolumeCandidate([VolumeSignal(bars, minute: 40)], "late")], VolumePortfolio(10));
        var entry = Assert.Single(result.PortfolioState.Fills, x => x.IsEntry);
        Assert.Equal(Start.AddMinutes(35), entry.TimestampUtc);
        Assert.Equal(5, entry.Quantity);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(signal.InitialStopPrice, trade.StopLossPrice);
        Assert.Equal(signal.FixedTargetPrice, trade.TakeProfitPrice);
        Assert.Equal(Start.AddMinutes(40), trade.ExitTimestamp);
        Assert.Contains(result.Dispositions, x => x.CandidateId == "late" && !x.Accepted);
        Assert.DoesNotContain(result.Events, x => x.State == "protection_updated");
        Assert.Empty(result.Failures);
    }

    // Keep production slippage and recipe parsing in the focused execution fixtures while isolating all market data synthetically.
    private static IntradayPortfolioReplayOutcome RunVolume(OhlcvBar[] bars, BacktestCandidateTrade[] candidates, PortfolioConfig? portfolio = null,
        bool noRewardRiskFilter = false)
    {
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies",
            noRewardRiskFilter ? "python-zone-volume-spread-no-rr.bt-v1.yaml" : "python-zone-volume-spread-comparison.bt-v1.yaml"));
        var selected = candidates.Select(x => x with { StrategyId = strategy.StrategyId }).ToArray();
        var result = IntradayPortfolioExecution.Replay(new(portfolio ?? VolumePortfolio(), [strategy], selected, bars, Sessions(), Start, Start.AddMinutes(45)));
        NativeDailySessionLedger.VerifyRecordedState((portfolio ?? VolumePortfolio()).StartingCapital, result.PortfolioState, new(Start, Start.AddMinutes(45), Sessions(), true));
        return result;
    }

    // Ticket fees come from the existing shared portfolio fields, independently of strategy parameters.
    private static PortfolioConfig VolumePortfolio(decimal participation = 100) => Portfolio(participation) with { FixedBuyFee = 1m, FixedSellFee = 1m };

    // Supply the asymmetric completed pattern and its preceding same-session bar, without a VWAP feature.
    private static OhlcvBar[] VolumeBars(string direction = "long", int minute = 10)
    {
        var bars = Bars();
        bars[minute / 5 - 2] = Bar(minute - 10, 100, 101.5m, 98.5m, 101);
        bars[minute / 5 - 1] = Bar(minute - 5, direction == "long" ? 99.9m : 100.1m, 100.5m, 99.5m, 100);
        return bars;
    }

    // Freeze an ordinary typed alternative with midpoint entry reference, synthetic quote evidence and fixed executable zone levels.
    private static IntradayZoneSignal VolumeSignal(OhlcvBar[] bars, string direction = "long", decimal bottom = 99m,
        decimal top = 99.5m, int index = 0, int minute = 10, decimal? target = null, decimal minimumRewardRisk = 1.5m)
    {
        var seed = PythonCandidate(bars, direction: direction, minute: minute).ZoneExecution!;
        var isLong = direction == "long";
        var zone = seed.Evidence.Zone with { ZoneId = index + 1, Bottom = isLong ? bottom : 100.5m, Top = isLong ? top : 101m };
        var targetPrice = target ?? (isLong ? 102.5m : 97.5m);
        var targetZone = seed.Evidence.TargetZone! with { ZoneId = 99, Bottom = isLong ? targetPrice : targetPrice - .5m,
            Top = isLong ? targetPrice + .5m : targetPrice };
        var stop = isLong ? decimal.Floor((zone.Bottom - .15m) / .01m) * .01m : decimal.Ceiling((zone.Top + .15m) / .01m) * .01m;
        var e = seed.Evidence with { Zone = zone, TargetZone = targetZone, Vwap = null, SlotRelativeVolume = 2m,
            SlotMedianVolume = seed.ConfirmationBar.Volume / 2m, SlotRelativeVolumeSampleCount = 20,
            MarketEvidenceProfileVersion = MarketEvidenceProfile.ProductionVersion, RawStopPrice = isLong ? zone.Bottom - .15m : zone.Top + .15m,
            RawTargetPrice = targetPrice, PolicyId = IntradayZoneRules.PythonVolumeSpreadComparisonPolicyId,
            VolumeSpread = new(99.95m, 100.05m, 10m, 20m, minimumRewardRisk, index) };
        return seed with { Evidence = e, InitialStopPrice = stop, FixedTargetPrice = targetPrice, EntryCutoffUtc = Start.AddMinutes(35) };
    }

    // Represent the entire frozen confirmation as one candidate; admission owns selection and only then publishes the chosen geometry.
    private static BacktestCandidateTrade VolumeCandidate(IReadOnlyList<IntradayZoneSignal> alternatives, string id = "volume")
    {
        var signal = alternatives[0];
        return new(signal.ConfirmationBar.Ticker, "volume spread", signal.Direction, signal.ActiveAtUtc, signal.EntryReferencePrice,
            signal.InitialStopPrice, signal.FixedTargetPrice ?? 0m, signal.SessionDeadlineUtc, null, "session_deadline",
            Math.Abs(signal.EntryReferencePrice - signal.InitialStopPrice), CandidateId: id)
        {
            ZoneExecution = signal, ZoneAlternatives = alternatives,
            ExecutionReplay = new(ExecutionReplayPolicy.PythonZoneVolumeSpreadV1, signal.ConfirmationBar.Timestamp, signal.ActiveAtUtc, signal.SessionDeadlineUtc, []),
            IntradayExecution = new(signal.EntryReferencePrice, signal.TickSize, signal.ActiveAtUtc, signal.EntryCutoffUtc!.Value,
                signal.SessionDeadlineUtc, null, null, signal.ConfirmationBar.Timestamp, signal.ActiveAtUtc, signal.Pattern)
            { EntryOrderMode = IntradayEntryOrderMode.NextBarMarket, EntryCutoffUtc = signal.EntryCutoffUtc, MaxFilledParentTradesPerSession = 2 }
        };
    }
}
