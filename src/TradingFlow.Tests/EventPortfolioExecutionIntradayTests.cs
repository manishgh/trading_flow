using TradingFlow.Alpaca;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed partial class EventPortfolioExecutionTests
{
    private const long FiveMinutes = 300_000_000_000L;

    // Protective targets use actual bid/ask fills in either direction, preserving the confirmation candle stop.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Intraday_ActualGapFillDefinesOriginalThreeRTarget(bool shortSide)
    {
        var candidate = IntradayCandidate("gap", shortSide);
        var entry = shortSide ? 9.8m : 10.2m;
        var target = shortSide ? 7.7m : 12.3m;
        var result = await RunIntraday([candidate], [],
            Frame(-1, Quote("A", -1, shortSide ? entry : entry - .01m, shortSide ? entry + .01m : entry) with { InRegularSession = false }),
            Frame(0, Batch("A", 0)), Frame(1, Trade("A", 1, entry, 10000)),
            Frame(2, Quote("A", 2, shortSide ? target - .01m : target, shortSide ? target : target + .01m)),
            Frame(3, Trade("A", 3, target, 10000)), Frame(4, Batch("A", 4)));
        var trade = Assert.Single(result.Trades);
        Assert.Equal(entry, trade.EntryPrice);
        Assert.Equal(target, trade.TakeProfitPrice);
        Assert.Equal(candidate.StopLossPrice, trade.StopLossPrice);
        Assert.Equal("take_profit", trade.ExitReason);
        Assert.Empty(result.UnavailableReasons);
    }

    // Invalid gap fills remain in accounting and are reduced on a later executable event, rather than erased or rejected retrospectively.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Intraday_GapThroughStructuralStopRetainsFillAndReducesActualExposure(bool shortSide)
    {
        var entry = shortSide ? 10.6m : 9.4m;
        var result = await RunIntraday([IntradayCandidate("through-stop", shortSide)], [],
            Frame(-1, Quote("A", -1, shortSide ? entry : entry - .01m, shortSide ? entry + .01m : entry) with { InRegularSession = false }),
            Frame(0, Batch("A", 0)), Frame(1, Trade("A", 1, entry, 10000)),
            Frame(2, Trade("A", 2, entry, 10000)), Frame(3, Batch("A", 3)));
        var trade = Assert.Single(result.Trades);
        Assert.Equal(entry, trade.EntryPrice);
        Assert.Equal("gap_fill_through_structural_stop", trade.ExitReason);
        Assert.Equal(trade.ShareQuantity, result.Events.Where(value => !value.Event.IsRiskReducing).Sum(value => value.Event.LastFillQuantity));
        Assert.Equal(trade.ShareQuantity, result.Events.Where(value => value.Event.IsRiskReducing).Sum(value => value.Event.LastFillQuantity));
        Assert.Empty(result.UnavailableReasons);
    }

    // Both patterns debit the same symbol/session counter, and only actual first fills consume a parent slot.
    [Fact]
    public async Task Intraday_TwoFilledParentsCapIsSharedAcrossPatterns()
    {
        var first = IntradayCandidate("one");
        var second = IntradayCandidate("two", entryOffset: 1000, pattern: "breakout_retest");
        var third = IntradayCandidate("three", entryOffset: 2000);
        var result = await RunIntraday([first, second, third], [],
            Frame(-1, Quote("A", -1, 9.99m, 10) with { InRegularSession = false }),
            Frame(0, Batch("A", 0)), Frame(1, Trade("A", 1, 10, 10000)),
            Frame(2, Quote("A", 2, 11.5m, 11.51m)), Frame(3, Trade("A", 3, 11.5m, 10000)), Frame(4, Batch("A", 4)),
            Frame(999, Quote("A", 999, 9.99m, 10)), Frame(1000, Batch("A", 1000)),
            Frame(1001, Trade("A", 1001, 10, 10000)), Frame(1002, Quote("A", 1002, 11.5m, 11.51m)),
            Frame(1003, Trade("A", 1003, 11.5m, 10000)), Frame(1004, Batch("A", 1004)),
            Frame(1999, Quote("A", 1999, 9.99m, 10)), Frame(2000, Batch("A", 2000)));
        Assert.Equal(2, result.Trades.Count);
        Assert.Equal("maximum_filled_parents_per_session", Assert.Single(result.Dispositions, value => !value.Accepted).Reason);
        Assert.Empty(result.UnavailableReasons);
    }

    // Missing and over-wide prior quotes reject the intent without manufacturing an entry or candle fallback.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Intraday_AdmissionRequiresFreshNarrowPriorQuote(bool wideQuote)
    {
        var frames = new List<SipPortfolioExecutionFrame>();
        if (wideQuote) frames.Add(Frame(-1, Quote("A", -1, 9.5m, 10) with { InRegularSession = false }));
        frames.Add(Frame(0, Batch("A", 0)));
        frames.Add(Frame(1, Trade("A", 1, 10, 10000)));
        var result = await RunIntraday([IntradayCandidate("quote")], [], frames.ToArray());
        Assert.Empty(result.Trades);
        Assert.Equal(wideQuote ? "entry_spread_above_common_limit" : "entry_prior_fresh_quote_unavailable",
            Assert.Single(result.Dispositions).Reason);
        Assert.DoesNotContain(result.Events, value => value.Event.LastFillQuantity > 0);
    }

    // Stop-election batch fills remain real; subsequent cancellation and reduction must cover every actually owned share.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Intraday_PartialParentStopCannotReopenExposure(bool shortSide)
    {
        var stop = shortSide ? 10.5m : 9.5m;
        var result = await RunIntraday([IntradayCandidate("partial-stop", shortSide)], [],
            Frame(-1, Quote("A", -1, shortSide ? 10m : 9.99m, shortSide ? 10.01m : 10m) with { InRegularSession = false }),
            Frame(0, Batch("A", 0)), Frame(1, Trade("A", 1, 10, 10)),
            Frame(2, Quote("A", 2, shortSide ? stop - .01m : stop, shortSide ? stop : stop + .01m)),
            Frame(3, Trade("A", 3, stop, 10000)), Frame(4, Trade("A", 4, stop, 10000)),
            Frame(5, Batch("A", 5)));
        var trade = Assert.Single(result.Trades);
        var entries = result.Events.Where(value => !value.Event.IsRiskReducing && value.Event.LastFillQuantity > 0).ToArray();
        Assert.True(trade.ShareQuantity >= 10);
        Assert.Equal("stop_loss", trade.ExitReason);
        Assert.Equal(trade.ShareQuantity, entries.Sum(value => value.Event.LastFillQuantity));
        Assert.DoesNotContain(entries, value => value.OccurredAtNanoseconds > Epoch + 3);
        Assert.Equal(trade.ShareQuantity, result.Events.Where(value => value.Event.IsRiskReducing).Sum(value => value.Event.LastFillQuantity));
        Assert.Empty(result.UnavailableReasons);
    }

    // Timer management requires each completely held candle; a later quote cannot hide a missing completed candle.
    [Fact]
    public async Task Intraday_MissingCompletedManagementCandleMakesLaterSuffixUnavailable()
    {
        var result = await RunIntraday([IntradayCandidate("missing-management")], [],
            Frame(-1, Quote("A", -1, 9.99m, 10) with { InRegularSession = false }),
            Frame(0, Batch("A", 0)), Frame(1, Trade("A", 1, 10, 10000)),
            Frame(2, Batch("A", 2)), Frame(FiveMinutes * 2 + 1, Quote("A", FiveMinutes * 2 + 1, 10, 10.01m)));
        Assert.Contains(result.UnavailableReasons, reason => reason.Contains("completed_management_candle_unavailable", StringComparison.Ordinal));
        Assert.Equal(Epoch + FiveMinutes * 2, result.UnavailableFromNanoseconds);
    }

    // Completed-close changes occur after equal-time prints; gap timers activate protection before the first strictly later print.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Intraday_ManagementClockPreservesOldProtectionForEqualTimePrint(bool equalTimePrint)
    {
        var managementAt = FiveMinutes * 2;
        var heldBar = new OhlcvBar("A", ExecutionEventTime.ToUtc(Epoch + FiveMinutes), "5m", 10, 11.3m, 9.7m, 11.25m, 10000, "sip", "raw");
        var frames = new List<SipPortfolioExecutionFrame>
        {
            Frame(-1, Quote("A", -1, 9.99m, 10) with { InRegularSession = false }),
            Frame(0, Batch("A", 0)), Frame(1, Trade("A", 1, 10, 10000)), Frame(2, Batch("A", 2)),
            Frame(managementAt - 1, Quote("A", managementAt - 1, 9.74m, 9.75m))
        };
        if (equalTimePrint) frames.Add(Frame(managementAt, Trade("A", managementAt, 9.75m, 10000)));
        frames.Add(Frame(managementAt + 1, Batch("A", managementAt + 1)));
        frames.Add(Frame(managementAt + 2, Trade("A", managementAt + 2, 9.75m, 10000)));
        frames.Add(Frame(managementAt + 3, Trade("A", managementAt + 3, 9.75m, 10000)));
        frames.Add(Frame(managementAt + 4, Batch("A", managementAt + 4)));
        var result = await RunIntraday([IntradayCandidate("management")], [heldBar], frames.ToArray());
        Assert.Single(result.Trades);
        Assert.DoesNotContain(result.Events, value => value.Event.IsRiskReducing && value.Event.LastFillQuantity > 0 && value.OccurredAtNanoseconds <= Epoch + managementAt);
        Assert.Contains(result.Events, value => value.Event.State == "triggered" && value.OccurredAtNanoseconds == Epoch + managementAt + 2);
        Assert.Empty(result.UnavailableReasons);
    }

    // Closing liquidation is committed at the deadline even when the first later executable print arrives after a source gap.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Intraday_DeadlineExitUsesOnlyStrictlyLaterExecutablePrint(bool shortSide)
    {
        var deadline = FiveMinutes;
        var candidate = IntradayCandidate("deadline", shortSide, deadlineOffset: deadline);
        var result = await RunIntraday([candidate], [],
            Frame(-1, Quote("A", -1, shortSide ? 10m : 9.99m, shortSide ? 10.01m : 10m) with { InRegularSession = false }),
            Frame(0, Batch("A", 0)), Frame(1, Trade("A", 1, 10, 10000)), Frame(2, Batch("A", 2)),
            Frame(deadline - 1, Quote("A", deadline - 1, 9.99m, 10m)),
            Frame(deadline + 1, Trade("A", deadline + 1, 10m, 10000)), Frame(deadline + 2, Batch("A", deadline + 2)));
        var trade = Assert.Single(result.Trades);
        Assert.Equal("session_deadline", trade.ExitReason);
        Assert.Equal(ExecutionEventTime.ToUtc(Epoch + deadline + 1), trade.ExitTimestamp);
        Assert.Empty(result.UnavailableReasons);
    }

    // A target report cancels the residual parent while preserving same-event entry fills and protecting their remaining shares.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Intraday_PartialParentTargetCancelsRemainingEntry(bool shortSide)
    {
        var target = shortSide ? 8.5m : 11.5m;
        var result = await RunIntraday([IntradayCandidate("partial-target", shortSide)], [],
            Frame(-1, Quote("A", -1, shortSide ? 10m : 9.99m, shortSide ? 10.01m : 10m) with { InRegularSession = false }),
            Frame(0, Batch("A", 0)), Frame(1, Trade("A", 1, 10, 10)),
            Frame(2, Quote("A", 2, shortSide ? target - .01m : target, shortSide ? target : target + .01m)),
            Frame(3, Trade("A", 3, target, 10000)),
            Frame(4, Quote("A", 4, shortSide ? 10.49m : 9.5m, shortSide ? 10.5m : 9.51m)),
            Frame(5, Trade("A", 5, shortSide ? 10.5m : 9.5m, 10000)),
            Frame(6, Trade("A", 6, shortSide ? 10.5m : 9.5m, 10000)), Frame(7, Batch("A", 7)));
        var trade = Assert.Single(result.Trades);
        var entries = result.Events.Where(value => !value.Event.IsRiskReducing && value.Event.LastFillQuantity > 0).ToArray();
        Assert.True(trade.ShareQuantity >= 10);
        Assert.Equal(trade.ShareQuantity, entries.Sum(value => value.Event.LastFillQuantity));
        Assert.DoesNotContain(entries, value => value.OccurredAtNanoseconds > Epoch + 3);
        Assert.Contains(result.Events, value => value.Event.ClientOrderId.Contains(":target:", StringComparison.Ordinal) && value.Event.LastFillQuantity > 0);
        Assert.Equal(trade.ShareQuantity, result.Events.Where(value => value.Event.IsRiskReducing).Sum(value => value.Event.LastFillQuantity));
        Assert.Empty(result.UnavailableReasons);
    }

    // Exercise the same shared event controller with the ordinary approved strategy and isolated synthetic event frames.
    private static async Task<EventPortfolioReplayOutcome> RunIntraday(BacktestCandidateTrade[] candidates, OhlcvBar[] bars,
        params SipPortfolioExecutionFrame[] frames)
    {
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies", "intraday-zone.bt-v1.yaml"));
        var portfolio = Request(candidates).Portfolio with { MaxConcurrentPositions = 1 };
        var request = new EventPortfolioReplayRequest("zone-fixture", portfolio, [strategy], candidates, bars, new Dictionary<string, string>());
        var policy = Policy() with { ProtectionActivation = ExecutionProtectionActivation.CustomPerFill, MaximumEntrySpreadBps = 20 };
        return Assert.Single(await EventPortfolioExecution.ReplayManyAsync([request], Timeline(frames), _ => policy));
    }

    // Freeze a typed completed confirmation independently of later quote prices and executed quantities.
    private static BacktestCandidateTrade IntradayCandidate(string id, bool shortSide = false, long entryOffset = 0,
        string pattern = "rejection_bounce", long deadlineOffset = FiveMinutes * 4)
    {
        var at = ExecutionEventTime.ToUtc(Epoch + entryOffset);
        var deadline = ExecutionEventTime.ToUtc(Epoch + deadlineOffset);
        var stop = shortSide ? 10.5m : 9.5m;
        var bar = new OhlcvBar("A", at.AddMinutes(-5), "5m", 10, shortSide ? stop : 10.1m, shortSide ? 9.9m : stop, 10, 2000, "sip", "raw");
        var pivot = new IntradayZonePivotEvidence(shortSide ? IntradayZonePivotKind.Peak : IntradayZonePivotKind.Valley,
            at.AddHours(-2), at.AddHours(-2).AddMinutes(15), at.AddHours(-1), DateOnly.FromDateTime(at.DateTime), 9.9m, 10.1m);
        var zone = new IntradayZoneEvidence(1, 1, shortSide ? IntradayZoneRole.Resistance : IntradayZoneRole.Support,
            9.9m, 10.1m, 1, at.AddHours(-1), at.AddHours(-1), pivot.PivotBarStartUtc, pivot.PivotSession, pivot);
        var signal = new IntradayZoneSignal(shortSide ? "short" : "long", pattern, bar, 10, stop, .01m, at, deadline,
            new(zone, pattern == "breakout_retest" ? at.AddMinutes(-15) : null, at, 10, 2, 1000, 20, MarketEvidenceProfile.ProductionVersion));
        return new("A", "Strategy 1 Intraday", signal.Direction, at, 10, stop, shortSide ? 8.5m : 11.5m,
            deadline, null, "session_deadline_pending", .5m, 2000, StrategyId: "technical.zone-intraday", CandidateId: id)
        {
            ZoneExecution = signal,
            ExecutionReplay = new(ExecutionReplayPolicy.IntradayRthFiveMinuteQuoteMarketV1, at, deadline, deadline, [])
                { EventExecutionEndUtc = deadline.AddMinutes(5) }
        };
    }
}
