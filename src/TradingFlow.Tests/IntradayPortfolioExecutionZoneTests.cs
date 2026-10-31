using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public sealed partial class IntradayPortfolioExecutionTests
{
    // Static zone protection survives changing weighted fills under the actual fifteen-minute research recipe.
    [Theory]
    [InlineData("long", 100.2)]
    [InlineData("short", 99.8)]
    public void PythonZone_PartialFillsRetainFixedStopAndTarget(string direction, double laterOpening)
    {
        var bars = Bars();
        bars[1] = bars[1] with { Volume = 50 };
        bars[2] = Bar(10, 100, 100.4m, 99.6m, 100, 50);
        bars[3] = Bar(15, (decimal)laterOpening, 100.4m, 99.6m, 100, 10000);
        var candidate = PythonCandidate(bars, direction: direction);
        var result = RunPython(bars, [candidate], Portfolio(10));
        var entries = result.PortfolioState.Fills.Where(x => x.IsEntry).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, x => Assert.Equal(5, x.Quantity));
        Assert.Equal(Start.AddMinutes(15), entries[1].TimestampUtc);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(candidate.StopLossPrice, trade.StopLossPrice);
        Assert.Equal(candidate.TakeProfitPrice, trade.TakeProfitPrice);
        Assert.All(result.Events.Where(x => x.State == "protection_initialized"), x =>
        {
            Assert.Equal(candidate.StopLossPrice, x.StopPrice);
            Assert.Equal(candidate.TakeProfitPrice, x.TargetPrice);
        });
        Assert.DoesNotContain(result.Events, x => x.State == "protection_updated");
        Assert.Empty(result.Failures);
    }

    // Shared adverse OHLC ordering selects the fixed stop when both zone exits occur in the entry interval.
    [Theory]
    [InlineData("long", 98.85)]
    [InlineData("short", 101.15)]
    public void PythonZone_EntryIntervalUsesAdverseFixedProtection(string direction, double stop)
    {
        var bars = Bars();
        bars[2] = Bar(10, 100, 104, 96, 100);
        var result = RunPython(bars, [PythonCandidate(bars, direction: direction)]);
        var trade = Assert.Single(result.Trades);
        Assert.Equal("stop_loss", trade.ExitReason);
        Assert.Equal((decimal)stop, trade.ExitPrice);
        Assert.Equal(Start.AddMinutes(10), trade.EntryTimestamp);
        Assert.Equal(Start.AddMinutes(15), trade.ExitTimestamp);
    }

    // Committed market entries preserve real gaps beyond either fixed boundary and reduce them at an observed opening.
    [Theory]
    [InlineData("long", 98, "gap_fill_through_structural_stop")]
    [InlineData("short", 102, "gap_fill_through_structural_stop")]
    [InlineData("long", 103, "actual_fill_zone_target_not_favorable")]
    [InlineData("short", 97, "actual_fill_zone_target_not_favorable")]
    public void PythonZone_InvalidGapRetainsActualFillAndReducesRisk(string direction, double gap, string reason)
    {
        var bars = Bars();
        bars[2] = Bar(10, (decimal)gap, (decimal)gap, (decimal)gap, (decimal)gap);
        var candidate = PythonCandidate(bars, direction: direction);
        var result = RunPython(bars, [candidate]);
        var trade = Assert.Single(result.Trades);
        Assert.Equal((decimal)gap, trade.EntryPrice);
        Assert.Equal((decimal)gap, trade.ExitPrice);
        Assert.Equal(trade.EntryTimestamp, trade.ExitTimestamp);
        Assert.Equal(reason, trade.ExitReason);
        Assert.Equal(candidate.TakeProfitPrice, trade.TakeProfitPrice);
        Assert.Empty(result.Failures);
    }

    // Equality permits the last opening while partial quantity is canceled there and no later opening can add risk.
    [Fact]
    public void PythonZone_InclusiveCutoffAllowsLastOpeningAndCancelsRemainder()
    {
        var bars = Bars();
        bars[2] = bars[2] with { Volume = 50 };
        var equality = PythonCandidate(bars, minute: 15);
        var result = RunPython(bars, [equality, PythonCandidate(bars, "late", minute: 20)], Portfolio(10));
        var entry = Assert.Single(result.PortfolioState.Fills, x => x.IsEntry);
        Assert.Equal(Start.AddMinutes(15), entry.TimestampUtc);
        Assert.Equal(5, entry.Quantity);
        Assert.Contains(result.Events, x => x.State == "entry_remainder_canceled" &&
            x.TimestampUtc == Start.AddMinutes(15) && x.Reason == "inclusive_entry_cutoff_remainder_canceled");
        Assert.Contains(result.Dispositions, x => x.CandidateId == "late" && !x.Accepted &&
            x.Reason == "invalid_or_stale_intraday_order_clock");
        Assert.Equal(Start.AddMinutes(40), Assert.Single(result.Trades).ExitTimestamp);
    }

    // Zero capacity at the cutoff expires an untouched parent instead of shifting its fill into a later candle.
    [Fact]
    public void PythonZone_NoFillAtCutoffCannotMoveToLaterOpening()
    {
        var bars = Bars();
        bars[2] = bars[2] with { Volume = 0 };
        var result = RunPython(bars, [PythonCandidate(bars, minute: 15)], Portfolio(10));
        Assert.Empty(result.PortfolioState.Fills);
        Assert.Empty(result.Trades);
        Assert.Empty(result.PortfolioState.PendingOrders);
        Assert.Empty(result.Failures);
    }

    // Fresh zone decisions after resolution share the two-filled-parent cap without the unrelated ORB rearm rule.
    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public void PythonZone_TwoFilledParentsShareDailyLimitWithoutRangeRearm(string direction)
    {
        var bars = Enumerable.Range(0, 19).Select(i => Bar(i * 5, 100, 100.5m, 99.5m, 100)).ToArray();
        bars[2] = Bar(10, 100, 102, 98, 100);
        bars[4] = Bar(20, 100, 102, 98, 100);
        var candidates = new[] { PythonCandidate(bars, direction: direction, deadlineMinute: 90),
            PythonCandidate(bars, "second", direction, 20, 90), PythonCandidate(bars, "third", direction, 30, 90) };
        var result = RunPython(bars, candidates, longSession: true);
        Assert.Equal(2, result.Trades.Count);
        Assert.Contains(result.Dispositions, x => x.CandidateId == "third" && x.Reason == "filled_parent_session_limit_reached");
        Assert.DoesNotContain(result.Dispositions, x => x.Reason == "completed_return_inside_opening_range_required");
        Assert.Empty(result.Failures);
    }

    // The zone book reserves one parent slot before fills even if the selected portfolio exposes more slots.
    [Fact]
    public void PythonZone_RejectsOverlappingOtherTickerAndRetainsSharedFees()
    {
        var bars = Bars();
        var otherBars = bars.Select(x => x with { Ticker = "Y" }).ToArray();
        var candidates = new[] { PythonCandidate(bars, direction: "short"), PythonCandidate(otherBars, "other", ticker: "Y") };
        var portfolio = Portfolio() with { FixedBuyFee = 1m, FixedSellFee = 2m };
        var result = RunPython(bars.Concat(otherBars).ToArray(), candidates, portfolio);
        Assert.Single(result.PortfolioState.AdmittedOrders);
        Assert.Equal(3m, Assert.Single(result.Trades).Fees);
        Assert.Contains(result.Dispositions, x => x.CandidateId == "other" && x.Reason == "max_concurrent_positions_reached");
        Assert.Empty(result.Failures);
    }

    // Existing opening protection cancels a pending entry remainder before a favorable gap could add more shares.
    [Fact]
    public void PythonZone_ExistingOpeningTargetCancelsPartialEntryRemainder()
    {
        var bars = Bars();
        bars[1] = bars[1] with { Volume = 50 };
        bars[3] = Bar(15, 103, 103, 103, 103);
        var result = RunPython(bars, [PythonCandidate(bars)], Portfolio(10));
        var trade = Assert.Single(result.Trades);
        Assert.Equal(5, trade.ShareQuantity);
        Assert.Equal("take_profit", trade.ExitReason);
        Assert.Equal(Start.AddMinutes(15), trade.ExitTimestamp);
        Assert.Empty(result.Failures);
    }

    // An absent active candle leaves exposure unknown; the existing simulator cannot infer either an entry or an exit.
    [Fact]
    public void PythonZone_MissingExecutionBarPreservesFailureEvidence()
    {
        var bars = Bars();
        var candidate = PythonCandidate(bars);
        var result = RunPython(bars.Where(x => x.Timestamp != Start.AddMinutes(10)).ToArray(), [candidate]);
        Assert.Empty(result.PortfolioState.Fills);
        Assert.Equal("required_five_minute_execution_bar_missing", Assert.Single(result.Failures).Reason);
    }

    // Volume remains evaluator evidence; a long with no optional RVOL can use the same genuine source and fixed levels.
    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("clock")]
    [InlineData("zone_receipt")]
    [InlineData("raw_stop")]
    [InlineData("missing_target_zone")]
    [InlineData("session")]
    public void PythonZone_RejectsSubstitutedEvidence(string mutation)
    {
        var bars = Bars();
        var candidate = PythonCandidate(bars);
        var signal = candidate.ZoneExecution!;
        if (mutation == "source") signal = signal with { ConfirmationBar = signal.ConfirmationBar with { Close = 100.1m } };
        if (mutation == "target") candidate = candidate with { TakeProfitPrice = 102.6m };
        if (mutation == "clock") signal = signal with { ActiveAtUtc = signal.ActiveAtUtc.AddMinutes(-5) };
        if (mutation == "zone_receipt") signal = signal with { Evidence = signal.Evidence with { Zone = signal.Evidence.Zone with { KnownAtUtc = Start.AddMinutes(10) } } };
        if (mutation == "raw_stop") signal = signal with { Evidence = signal.Evidence with { RawStopPrice = 98.8m }, InitialStopPrice = 98.8m };
        if (mutation == "missing_target_zone") signal = signal with { Evidence = signal.Evidence with { TargetZone = null } };
        if (mutation == "session") signal = signal with { Evidence = signal.Evidence with { SessionOpenUtc = Start.AddMinutes(-5) } };
        candidate = candidate with { ZoneExecution = signal };
        var result = RunPython(bars, [candidate]);
        Assert.Empty(result.PortfolioState.Fills);
        Assert.False(Assert.Single(result.Dispositions).Accepted);
    }

    // Legacy market orders still require their actual previous-session/ORB context after zone fields become optional.
    [Fact]
    public void PythonZone_DoesNotLoosenLegacyMarketClock()
    {
        var bars = Bars();
        var candidate = MarketCandidate(bars);
        candidate = candidate with { IntradayExecution = candidate.IntradayExecution! with { PreviousSessionHigh = null } };
        var result = Run(bars, [candidate]);
        Assert.Empty(result.PortfolioState.Fills);
        Assert.False(Assert.Single(result.Dispositions).Accepted);
        var zoneJson = JsonSerializer.Serialize(PythonCandidate(bars).IntradayExecution);
        Assert.DoesNotContain("PreviousSessionHigh", zoneJson);
        Assert.DoesNotContain("OpeningRangeHigh", zoneJson);
    }

    // Load the actual fifteen-minute recipe and route its orders through the ordinary shared replay and account ledger.
    private static IntradayPortfolioReplayOutcome RunPython(OhlcvBar[] bars, BacktestCandidateTrade[] candidates,
        PortfolioConfig? portfolio = null, bool longSession = false)
    {
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies", "python-zone-comparison.bt-v1.yaml"));
        Assert.Equal("15m", strategy.Timeframe);
        strategy = strategy with { Execution = strategy.Execution with { SlippageBps = 0.000000001m } };
        var sessions = longSession
            ? new Dictionary<DateOnly, MarketSessionSchedule> { [Day] = new(Day, true, new(9, 30), new(11, 5)) }
            : Sessions();
        var end = Start.AddMinutes(longSession ? 95 : 45);
        var selected = candidates.Select(x => x with { StrategyId = strategy.StrategyId }).ToArray();
        var result = IntradayPortfolioExecution.Replay(new(portfolio ?? Portfolio(), [strategy], selected, bars, sessions, Start, end));
        NativeDailySessionLedger.VerifyRecordedState((portfolio ?? Portfolio()).StartingCapital, result.PortfolioState,
            new(Start, end, sessions, true));
        return result;
    }

    // Transport actual immutable zone and source evidence without inventing previous-session prices or an ORB range.
    private static BacktestCandidateTrade PythonCandidate(OhlcvBar[] bars, string id = "first", string direction = "long", int minute = 10,
        int deadlineMinute = 40, string ticker = "X")
    {
        var active = Start.AddMinutes(minute);
        var cutoff = Start.AddMinutes(deadlineMinute - 25);
        var source = bars.Single(x => x.Timestamp == active.AddMinutes(-5));
        var previous = bars.Single(x => x.Timestamp == active.AddMinutes(-10));
        var isLong = direction == "long";
        var pivot = new IntradayZonePivotEvidence(IntradayZonePivotKind.Valley, Start.AddDays(-1), Start.AddDays(-1).AddMinutes(15),
            Start, Day.AddDays(-1), 99, 99.5m);
        var zone = new IntradayZoneEvidence(1, 1, isLong ? IntradayZoneRole.Support : IntradayZoneRole.Resistance,
            isLong ? 99 : 100.5m, isLong ? 99.5m : 101, 2, Start, Start, Start.AddDays(-1), Day, pivot);
        var targetZone = zone with { ZoneId = 2, Bottom = isLong ? 102.5m : 97m, Top = isLong ? 103m : 97.5m };
        var stop = isLong ? 98.85m : 101.15m;
        var target = isLong ? 102.5m : 97.5m;
        var pattern = isLong ? "python_support_bounce" : "python_zone_breakdown";
        var signalEvidence = new IntradayZoneSignalEvidence(zone, null, active, 100, null, null, 0, null,
            previous, 1, targetZone, stop, target, IntradayZoneRules.PythonComparisonPolicyId, Start, Start.AddMinutes(deadlineMinute + 5));
        var signal = new IntradayZoneSignal(direction, pattern, source, source.Close, stop, .01m,
            active, Start.AddMinutes(deadlineMinute), signalEvidence, 2, cutoff, target);
        return new(ticker, "python zone", direction, active, source.Close, stop, target, signal.SessionDeadlineUtc,
            null, "session_deadline", Math.Abs(source.Close - stop), CandidateId: id)
        {
            ZoneExecution = signal,
            ExecutionReplay = new(ExecutionReplayPolicy.PythonZoneBounceBreakdownV1, source.Timestamp, active, signal.SessionDeadlineUtc, []),
            IntradayExecution = new(source.Close, .01m, active, cutoff, signal.SessionDeadlineUtc,
                null, null, source.Timestamp, active, pattern)
            {
                EntryOrderMode = IntradayEntryOrderMode.NextBarMarket, EntryCutoffUtc = cutoff,
                MaxFilledParentTradesPerSession = 2
            }
        };
    }
}
