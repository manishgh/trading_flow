using System.Collections.Immutable;
using TradingFlow.Alpaca;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Regime;

namespace TradingFlow.Tests;

public sealed partial class EventPortfolioExecutionTests
{
    private const long Epoch = 1_800_000_000_000_000_000L;

    /// <summary>The ordinary workspace forwards its injected event binding and journals nanosecond fill receipts exactly once.</summary>
    [Fact]
    public async Task OrdinaryWorkspaceUsesBoundOutcomeAndWritesTimedEvents()
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var profile = reader.ReadBacktestRun(Path.Combine(root, "configs/backtest/swing-backtest-profile.yaml"));
        var calls = 0;
        var timed = new TimedSimulatedExecutionEvent(new("A:entry", "A", "filled", 1, 1, 0,
            10m, 1m, 0, 0, 0, 1, 0, ExecutionEventTime.ToUtc(Epoch + 123), "fixture", SimulatedOrderSide.Buy, false), Epoch + 123, Epoch + 123);
        var trade = new BacktestTrade("A", "test", "long", ExecutionEventTime.ToUtc(Epoch), ExecutionEventTime.ToUtc(Epoch + 1000),
            1, 10, 11, 9.5m, 11.25m, "test", 1, 0, 1, StrategyId: "test", CandidateId: "A");
        var expected = new EventPortfolioReplayOutcome([trade], [], [], [timed], null, []);
        var binding = new EventPortfolioReplayBinding((request, token) => { calls++; return Task.FromResult(expected); });
        var runner = new BacktestRunner(reader, new StrategyArtifactCatalog(root, Path.Combine(root, "configs/strategy-catalog.json"), reader),
            eventPortfolioReplay: binding);
        var scratch = Path.Combine(Path.GetTempPath(), "tf-event-bridge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = runner.CreateWorkspace(profile, Path.Combine(scratch, "result.json"));
            var journal = new Journal();
            var actual = await workspace.ExecuteAsync(() => workspace.Runner.ExecuteEventPortfolioAsync(profile, [Strategy()],
                [Candidate("A")], [], null, new Dictionary<string, RegimeCalendar>(), journal, CancellationToken.None));
            Assert.Equal(1, calls);
            Assert.Equal(trade, Assert.Single(actual.Trades));
            Assert.Contains((Epoch + 123).ToString(), Assert.Single(journal.Events).EvidenceJson);
            Assert.NotEmpty(Directory.EnumerateDirectories(scratch, ".run-inputs-*"));
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    /// <summary>Explicit SIP policy cannot silently call the candle executor when its event source is absent.</summary>
    [Fact]
    public async Task OrdinaryRunRejectsSipPolicyWithoutBindingBeforeProviderReads()
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var profile = reader.ReadBacktestRun(Path.Combine(root, "configs/backtest/swing-backtest-profile.yaml"));
        var run = profile with { Universe = UniverseConfig.StaticUniverse, Tickers = ["A"],
            Engine = profile.Engine with { ExecutionReplayPolicyId = ExecutionReplayPolicy.DailyRthSipPortfolioV1,
                PrecommittedLiquidationSourceUtc = DateTimeOffset.Parse("2026-10-06T04:00:00Z") } };
        var runner = new BacktestRunner(reader, new StrategyArtifactCatalog(root, Path.Combine(root, "configs/strategy-catalog.json"), reader),
            marketDataProviderFactory: _ => throw new InvalidOperationException("provider must not be read"));
        var scratch = Path.Combine(Path.GetTempPath(), "tf-event-no-source-" + Guid.NewGuid().ToString("N"));
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(run, [Strategy()], DateTimeOffset.UtcNow,
                Path.Combine(scratch, "result.json"), CancellationToken.None));
            Assert.Contains("execution source manifest", error.Message);
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    private sealed class Journal : IExecutionEventSink
    {
        public List<ExecutionEvent> Events { get; } = [];
        /// <summary>Captures only task-owned journal events for bridge assertions.</summary>
        public void Append(ExecutionEvent item) => Events.Add(item);
    }

    /// <summary>Competing equal-open entries must reserve four slots before any opening fills are observed.</summary>
    [Fact]
    public async Task EqualOpenEntriesReserveSlotsImmediately()
    {
        var candidates = new[] { "A", "B", "C", "D", "E" }.Select(Candidate).ToArray();
        var outcome = await Run(candidates, Frame(0, candidates.Select(c => Batch(c.Ticker, 0)).ToArray()),
            Frame(300_000_000_001L, candidates.Select(c => Batch(c.Ticker, 300_000_000_001L)).ToArray()));
        Assert.Equal(4, outcome.Dispositions.Count(x => x.Accepted));
        Assert.Equal("max_concurrent_positions_reached", Assert.Single(outcome.Dispositions, x => !x.Accepted).Reason);
        Assert.Empty(outcome.UnavailableReasons);
    }

    /// <summary>Pending commands count as exposure when a second setup requests the same stock at the same time.</summary>
    [Fact]
    public async Task EqualOpenSameStockCannotBypassOverlapGate()
    {
        var outcome = await Run([Candidate("A"), Candidate("A") with { CandidateId = "A-second" }],
            Frame(0, Batch("A", 0)), Frame(300_000_000_001L, Batch("A", 300_000_000_001L)));
        Assert.Single(outcome.Dispositions, x => x.Accepted);
        Assert.Equal("overlapping_ticker_position", Assert.Single(outcome.Dispositions, x => !x.Accepted).Reason);
    }

    /// <summary>An accepted route with no symbol observations cannot become a synthetic nonfill.</summary>
    [Fact]
    public async Task MissingAcceptedSymbolIsUnavailableFromItsEntry()
    {
        var outcome = await Run([Candidate("A")], Frame(0, Batch("B", 0)), Frame(400_000_000_000L, Batch("B", 400_000_000_000L)));
        Assert.NotEmpty(outcome.Failures);
        Assert.Equal(Epoch, outcome.UnavailableFromNanoseconds);
        Assert.Empty(outcome.Trades);
    }

    /// <summary>The first executable batch must not discard an unknown print merely because the parent is pending.</summary>
    [Fact]
    public async Task FirstRouteUnknownEvidenceIsUnavailable()
    {
        var batch = Batch("A", 1) with { UnknownEvidenceReasons = ["unknown_trade_condition"] };
        var outcome = await Run([Candidate("A")], Frame(1, batch));
        Assert.Contains(outcome.UnavailableReasons, x => x.Contains("relevant_unknown_source", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.Events, x => x.Event.LastFillQuantity > 0);
    }

    /// <summary>Two physically repeated identical rows use the shared quote transition rather than false ambiguity.</summary>
    [Fact]
    public async Task RepeatedIdenticalQuoteRowsRetainPriorNbbo()
    {
        var quote = new ExecutionQuoteTick("q", Epoch - 1, 10m, 10.01m, 1000, 1000, true);
        var prefix = Batch("A", -1) with { InRegularSession = false,
            Market = Batch("A", -1).Market with { Quotes = [quote, quote] } };
        var outcome = await Run([Candidate("A")], Frame(-1, prefix), Frame(0, Batch("A", 0)),
            Frame(1, Trade("A", 1, 10m, 10000)));
        Assert.Contains(outcome.Events, x => !x.Event.IsRiskReducing && x.Event.LastFillQuantity > 0);
        Assert.DoesNotContain(outcome.UnavailableReasons, x => x.Contains("prior_fresh_nbbo", StringComparison.Ordinal));
    }

    /// <summary>A complete entry and independently later native stop use one book without any candle prerequisite.</summary>
    [Fact]
    public async Task PhysicalFillActivatesLaterNativeStop()
    {
        var prefix = Quote("A", -1, 9.5m, 10m) with { InRegularSession = false };
        var outcome = await Run([Candidate("A")], Frame(-1, prefix), Frame(0, Batch("A", 0)),
            Frame(100, Trade("A", 100, 9.75m, 10000)), Frame(200, Trade("A", 200, 9.5m, 10000)),
            Frame(300, Trade("A", 300, 9.5m, 10000)));
        var trade = Assert.Single(outcome.Trades);
        Assert.Equal("stop_loss", trade.ExitReason);
        Assert.Equal(10m, trade.EntryPrice);
        Assert.Equal(9.5m, trade.ExitPrice);
        Assert.Empty(outcome.UnavailableReasons);
        Assert.True(trade.ExitTimestamp > trade.EntryTimestamp);
    }

    /// <summary>A daily next-open liquidation is already committed before the first later eligible trade.</summary>
    [Fact]
    public async Task DailyExitCanUseFirstStrictlyLaterPrint()
    {
        var candidate = Candidate("A") with { ExitTimestamp = ExecutionEventTime.ToUtc(Epoch + 1000) };
        var outcome = await Run([candidate], Frame(-1, Quote("A", -1, 9.5m, 10m) with { InRegularSession = false }),
            Frame(0, Batch("A", 0)), Frame(1, Trade("A", 1, 9.75m, 10000)),
            Frame(1001, Trade("A", 1001, 9.75m, 10000)));
        var trade = Assert.Single(outcome.Trades);
        Assert.Equal(ExecutionEventTime.ToUtc(Epoch + 1001), trade.ExitTimestamp);
        Assert.Empty(outcome.UnavailableReasons);
    }

    /// <summary>A branch that fails cannot prevent the shared source from being fully consumed and verified.</summary>
    [Fact]
    public async Task UnavailableBranchStillConsumesSource()
    {
        var consumed = 0;
        async IAsyncEnumerable<SipPortfolioExecutionFrame> Frames()
        {
            for (var index = 0; index < 3; index++)
            {
                consumed++;
                yield return Frame(index, Batch("A", index) with { UnknownEvidenceReasons = ["unknown"] });
                await Task.CompletedTask;
            }
        }
        await EventPortfolioExecution.ReplayManyAsync([Request([Candidate("A")])], Frames(), _ => Policy());
        Assert.Equal(3, consumed);
    }

    /// <summary>Committed expiry releases a zero-fill reservation before another symbol's later admission.</summary>
    [Fact]
    public async Task BrokerExpiryAdvancesWithoutAnotherObservationForExpiredSymbol()
    {
        var next = Candidate("B") with { EntryTimestamp = ExecutionEventTime.ToUtc(Epoch + 400_000_000_000L) };
        var request = Request([Candidate("A"), next]);
        request = request with { Portfolio = request.Portfolio with { MaxConcurrentPositions = 1 } };
        var outcome = Assert.Single(await EventPortfolioExecution.ReplayManyAsync([request], Timeline([
            Frame(0, Batch("A", 0)), Frame(400_000_000_000L, Batch("B", 400_000_000_000L)),
            Frame(800_000_000_000L, Batch("B", 800_000_000_000L))]), _ => Policy()));
        Assert.Equal(2, outcome.Dispositions.Count(x => x.Accepted));
        Assert.Empty(outcome.UnavailableReasons);
    }

    /// <summary>Unresolved positions cannot cross an unobserved archive gap and use a much later exit print.</summary>
    [Fact]
    public async Task ExecutionHorizonPreventsFillInLaterSeparateWindow()
    {
        var candidate = Candidate("A") with { ExitTimestamp = ExecutionEventTime.ToUtc(Epoch + 500),
            ExecutionReplay = Candidate("A").ExecutionReplay! with { EventExecutionEndUtc = ExecutionEventTime.ToUtc(Epoch + 1000) } };
        var outcome = await Run([candidate], Frame(-1, Quote("A", -1, 9.5m, 10m) with { InRegularSession = false }),
            Frame(0, Batch("A", 0)), Frame(100, Trade("A", 100, 9.75m, 10000)), Frame(2000, Trade("A", 2000, 9.75m, 10000)));
        Assert.Contains("execution_horizon_ends_with_unresolved_route_or_exposure", outcome.UnavailableReasons);
        Assert.DoesNotContain(outcome.Events, x => x.Event.IsRiskReducing && x.Event.LastFillQuantity > 0);
        Assert.Empty(outcome.Trades);
    }

    /// <summary>A known zero-fill expiry resolves before a later source jump reaches the intent horizon.</summary>
    [Fact]
    public async Task UnfilledBrokerExpiryResolvesBeforeHorizonCheck()
    {
        var outcome = await Run([Candidate("A")], Frame(0, Batch("A", 0)), Frame(3_000_000_000_000L, Batch("B", 3_000_000_000_000L)));
        Assert.Empty(outcome.UnavailableReasons);
        Assert.Empty(outcome.Trades);
        Assert.Contains(outcome.Events, x => x.Event.ClientOrderId == "A:entry" && x.Event.Reason == "route_expired");
    }

    /// <summary>One held parent owns its actual exposure and fees; later setups on that ticker do not inherit them.</summary>
    [Fact]
    public async Task FailureEvidenceDoesNotDuplicateTickerExposureOntoLaterCandidate()
    {
        var later = Candidate("A") with { CandidateId = "A-later", EntryTimestamp = ExecutionEventTime.ToUtc(Epoch + 1000) };
        var outcome = await Run([Candidate("A"), later], Frame(-1, Quote("A", -1, 9.5m, 10m) with { InRegularSession = false }),
            Frame(0, Batch("A", 0)), Frame(100, Trade("A", 100, 9.75m, 10000)),
            Frame(200, Batch("A", 200) with { UnknownEvidenceReasons = ["unknown"] }));
        var held = Assert.Single(outcome.Failures, x => x.CandidateId == "A");
        var future = Assert.Single(outcome.Failures, x => x.CandidateId == "A-later");
        Assert.True(held.OpenSignedQuantity > 0);
        Assert.Equal(held.EntryFilledQuantity * 10m, held.EntryFilledNotional);
        Assert.Equal(0, future.OpenSignedQuantity);
        Assert.True(future.EvidenceUnavailable);
    }

    /// <summary>Runs a small fixed event fixture through the actual portfolio coordinator.</summary>
    private static async Task<EventPortfolioReplayOutcome> Run(BacktestCandidateTrade[] candidates, params SipPortfolioExecutionFrame[] frames)
        => Assert.Single(await EventPortfolioExecution.ReplayManyAsync([Request(candidates)], Timeline(frames), _ => Policy()));

    /// <summary>Produces ordered in-memory frames without provider, broker or database writes.</summary>
    private static async IAsyncEnumerable<SipPortfolioExecutionFrame> Timeline(SipPortfolioExecutionFrame[] frames)
    { foreach (var frame in frames) { yield return frame; await Task.CompletedTask; } }

    /// <summary>Builds an independent four-slot portfolio with the same shared risk planner used by production.</summary>
    private static EventPortfolioReplayRequest Request(BacktestCandidateTrade[] candidates) => new("fixture",
        new(10000, 1, 25, 4, 0, 0, 1, true), [Strategy()], candidates, [], new Dictionary<string, string>());

    /// <summary>Uses raw fixture prices with full print participation to isolate admission and causal order handling.</summary>
    private static ExecutionEventSimulationPolicy Policy() => new(new(100, 0, 0, 0, 0, 0, 0, 0, 0),
        ExecutionProtectionActivation.NativeParentFullyFilled, 30_000_000_000L, 0, true, 1, 1, "raw-fixture");

    /// <summary>Creates a daily setup whose structural stop and target were fixed before its entry route.</summary>
    private static BacktestCandidateTrade Candidate(string symbol) => new(symbol, "test", "long",
        ExecutionEventTime.ToUtc(Epoch), 10m, 9.5m, 11.25m, ExecutionEventTime.ToUtc(Epoch + 1_000_000_000_000L),
        0, "time_exit", .5m, StrategyId: "test", CandidateId: symbol)
        { ExecutionReplay = new(ExecutionReplayPolicy.DailyRthSipPortfolioV1, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(1), DateTimeOffset.UnixEpoch.AddDays(2), [])
            { EventExecutionEndUtc = ExecutionEventTime.ToUtc(Epoch + 2_000_000_000_000L) } };

    /// <summary>Keeps exact nanoseconds separate from the legacy display timestamp.</summary>
    private static SipPortfolioExecutionFrame Frame(long ns, params SipPortfolioExecutionBatch[] batches)
        => new(Epoch + ns, Epoch + ns, batches.ToImmutableArray());

    /// <summary>Creates a verified empty event batch used for route and timer observations.</summary>
    private static SipPortfolioExecutionBatch Batch(string symbol, long ns)
        => new(new(symbol, Epoch + ns, Epoch + ns, ns + 10, [], [], true), true, "session", [], [], []);

    /// <summary>Adds one prior two-sided quote to a fixed batch.</summary>
    private static SipPortfolioExecutionBatch Quote(string symbol, long ns, decimal bid, decimal ask)
        => Batch(symbol, ns) with { Market = Batch(symbol, ns).Market with {
            Quotes = [new("q" + ns, Epoch + ns, bid, ask, 10000, 10000, true)] } };

    /// <summary>Adds a qualified ordinary print whose volume can be consumed once by the shared matcher.</summary>
    private static SipPortfolioExecutionBatch Trade(string symbol, long ns, decimal price, int shares)
        => Batch(symbol, ns) with { Market = Batch(symbol, ns).Market with {
            Trades = [new("t" + ns, Epoch + ns, price, shares, true)] } };

    /// <summary>Provides the minimal unchanged strategy contract needed by the portfolio request.</summary>
    private static StrategyDefinition Strategy() => new("test", "test", "test", 1, "1d", "long",
        new("test", 0, 0, 100, "none", "none", false, false, false, false, false, false, 0, 0, 0),
        new(false, "1d", 20, "none"), new(1, 2, 1, false, 1, 1, false, false, false, 0),
        new("1d", 0), new("America/New_York", 0, 0, 0));
}
