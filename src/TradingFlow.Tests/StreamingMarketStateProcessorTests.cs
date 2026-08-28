global using TradingFlow.Engine.Indicators;

using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Data.Candles;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;

namespace TradingFlow.Tests;

public sealed class StreamingMarketStateProcessorTests
{
    [Theory]
    [InlineData(0, 45)]
    [InlineData(2, 0)]
    public void Options_WhenEvidenceWindowIsNotPositive_RejectsConfiguration(
        int revisionMinutes,
        int recoveryDays)
    {
        var options = StreamingMarketStateOptions.Default with
        {
            RevisionAcceptanceMinutes = revisionMinutes,
            RecoveryLookbackDays = recoveryDays
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public async Task ProcessAsync_DeduplicatesAndAcceptsOnlyTimelyProviderRevision()
    {
        var clock = new FixedTimeProvider(Utc(2026, 8, 27, 14, 35));
        var processor = CreateProcessor(NullCandleStore.Instance, clock);
        var original = Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m);

        Assert.Equal(MarketBarDisposition.Accepted, (await processor.ProcessAsync(
            Event(original, MarketBarEventKind.CompletedBar, original.Timestamp.AddMinutes(1), 1))).Disposition);
        Assert.Equal(MarketBarDisposition.Duplicate, (await processor.ProcessAsync(
            Event(original, MarketBarEventKind.CompletedBar, original.Timestamp.AddMinutes(1), 1))).Disposition);

        var revised = original with { Close = 101m, High = 101m, Volume = 1_100m };
        Assert.Equal(MarketBarDisposition.RevisionAccepted, (await processor.ProcessAsync(
            Event(revised, MarketBarEventKind.ProviderRevision, original.Timestamp.AddMinutes(2.5), 1))).Disposition);

        var tooLate = original with { Close = 102m, High = 102m, Volume = 1_200m };
        Assert.Equal(MarketBarDisposition.RevisionOutsideWindow, (await processor.ProcessAsync(
            Event(tooLate, MarketBarEventKind.ProviderRevision, original.Timestamp.AddMinutes(3.01), 1))).Disposition);

        var state = await processor.GetCompletedStateAsync("AAPL", original.Timestamp.AddMinutes(5));
        Assert.Equal(101m, Assert.Single(state.BarsByTimeframe["1m"]).Close);
    }

    [Fact]
    public async Task ProcessAsync_RejectsEventsFromOwnerWithOlderFencingToken()
    {
        var processor = CreateProcessor(NullCandleStore.Instance, new FixedTimeProvider(Utc(2026, 8, 27, 15, 0)));
        Activate(processor, 2);
        var first = Bar("MSFT", Utc(2026, 8, 27, 14, 30), 400m);
        var stale = Bar("MSFT", Utc(2026, 8, 27, 14, 31), 401m);

        Assert.Equal(MarketBarDisposition.Accepted, (await processor.ProcessAsync(
            Event(first, MarketBarEventKind.CompletedBar, first.Timestamp.AddMinutes(1), 2))).Disposition);
        Assert.Equal(MarketBarDisposition.StaleLease, (await processor.ProcessAsync(
            Event(stale, MarketBarEventKind.CompletedBar, stale.Timestamp.AddMinutes(1), 1))).Disposition);

        var state = await processor.GetCompletedStateAsync("MSFT", Utc(2026, 8, 27, 15, 0));
        Assert.Single(state.BarsByTimeframe["1m"]);
        Assert.Equal(2, state.FencingToken);
    }

    [Fact]
    public async Task AdvanceFencingFloor_RejectsStaleOwnerForNewAndExistingPipelines()
    {
        var processor = CreateProcessor(
            NullCandleStore.Instance,
            new FixedTimeProvider(Utc(2026, 8, 27, 15, 0)));

        await processor.AdvanceFencingFloorAsync(8);
        processor.ActivateOwnership(8);
        processor.MarkSnapshotsReady(8);
        var newSymbolBar = Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m);
        Assert.Equal(MarketBarDisposition.StaleLease, (await processor.ProcessAsync(
            Event(newSymbolBar, MarketBarEventKind.CompletedBar, newSymbolBar.Timestamp.AddMinutes(1), 7))).Disposition);

        var accepted = Bar("MSFT", Utc(2026, 8, 27, 14, 30), 400m);
        Assert.Equal(MarketBarDisposition.Accepted, (await processor.ProcessAsync(
            Event(accepted, MarketBarEventKind.CompletedBar, accepted.Timestamp.AddMinutes(1), 8))).Disposition);

        await processor.AdvanceFencingFloorAsync(9);
        var stale = Bar("MSFT", Utc(2026, 8, 27, 14, 31), 401m);
        Assert.Equal(MarketBarDisposition.StaleLease, (await processor.ProcessAsync(
            Event(stale, MarketBarEventKind.CompletedBar, stale.Timestamp.AddMinutes(1), 8))).Disposition);
        Assert.Equal(9, (await processor.GetCompletedStateAsync("MSFT", Utc(2026, 8, 27, 15, 0))).FencingToken);
    }

    [Fact]
    public async Task RestartWithDurableFencingFloor_RejectsPreviousOwnerBeforeFirstNewEvent()
    {
        var root = Path.Combine(Path.GetTempPath(), "tradingflow-fence-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new FixedTimeProvider(Utc(2026, 8, 27, 15, 0));
            var store = new LocalFileCandleStore(root);
            var original = CreateProcessor(store, clock);
            Activate(original, 11);
            var first = Bar("NVDA", Utc(2026, 8, 27, 14, 30), 180m);
            Assert.Equal(MarketBarDisposition.Accepted, (await original.ProcessAsync(
                Event(first, MarketBarEventKind.CompletedBar, first.Timestamp.AddMinutes(1), 11))).Disposition);

            var restarted = CreateProcessor(store, clock);
            await restarted.AdvanceFencingFloorAsync(12);
            await restarted.EnsureSymbolReadyAsync("NVDA", default);

            var stale = Bar("NVDA", Utc(2026, 8, 27, 14, 31), 181m);
            Assert.Equal(MarketBarDisposition.StaleLease, (await restarted.ProcessAsync(
                Event(stale, MarketBarEventKind.CompletedBar, stale.Timestamp.AddMinutes(1), 11))).Disposition);
            var recovered = await restarted.GetCompletedStateAsync("NVDA", clock.GetUtcNow());
            Assert.Single(recovered.BarsByTimeframe["1m"]);
            Assert.Equal(12, recovered.FencingToken);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProcessAsync_RejectsMalformedOhlcvWithoutChangingState()
    {
        var processor = CreateProcessor(NullCandleStore.Instance, new FixedTimeProvider(Utc(2026, 8, 27, 15, 0)));
        var malformed = Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m) with
        {
            High = 99m,
            Volume = -1m
        };

        var result = await processor.ProcessAsync(
            Event(malformed, MarketBarEventKind.CompletedBar, malformed.Timestamp.AddMinutes(1), 1));

        Assert.Equal(MarketBarDisposition.Invalid, result.Disposition);
        var state = await processor.GetCompletedStateAsync("AAPL", Utc(2026, 8, 27, 15, 0));
        Assert.Empty(state.BarsByTimeframe["1m"]);
    }

    [Fact]
    public async Task BackfillSymbolsAsync_EmptyUniverseDoesNotCallProvider()
    {
        var processor = CreateProcessor(NullCandleStore.Instance, new FixedTimeProvider(Utc(2026, 8, 27, 15, 0)));
        var provider = new ThrowingMarketDataProvider();

        await processor.BackfillSymbolsAsync([], provider, default);
    }

    [Fact]
    public async Task GetTickerStateAsync_ReturnsNullUntilReadyAndWhileRepairIsRequired()
    {
        var clock = new FixedTimeProvider(Utc(2026, 8, 27, 15, 0));
        var store = new FailingOnceCandleStore(failRead: true);
        var processor = CreateProcessor(store, clock);

        Assert.Null(await processor.GetTickerStateAsync(
            "AAPL", ["1m"], 0, clock.GetUtcNow(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.EnsureSymbolReadyAsync("AAPL", default));
        Assert.True(processor.RequiresRepair("AAPL"));
        Assert.Null(await processor.GetTickerStateAsync(
            "AAPL", ["1m"], 0, clock.GetUtcNow(), default));
    }

    [Fact]
    public async Task DerivedBars_ExcludeBucketsWithMissingMinuteAndAcceptShortSegmentFinalBucket()
    {
        var timezone = ResolveNewYork();
        var premarketNine = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2026, 8, 27, 9, 0, 0),
            timezone);
        var start = new DateTimeOffset(premarketNine, TimeSpan.Zero);
        var clock = new FixedTimeProvider(start.AddMinutes(31));
        var complete = CreateProcessor(NullCandleStore.Instance, clock);
        var gapped = CreateProcessor(NullCandleStore.Instance, clock);

        for (var minute = 0; minute < 30; minute++)
        {
            var bar = Bar("AAPL", start.AddMinutes(minute), 100m + minute);
            Assert.Equal(MarketBarDisposition.Accepted, (await complete.ProcessAsync(
                Event(bar, MarketBarEventKind.Replay, bar.Timestamp.AddMinutes(1), 0))).Disposition);
            if (minute != 17)
            {
                var expected = minute == 18
                    ? MarketBarDisposition.GapDetected
                    : MarketBarDisposition.Accepted;
                Assert.Equal(expected, (await gapped.ProcessAsync(
                    Event(bar, MarketBarEventKind.Replay, bar.Timestamp.AddMinutes(1), 0))).Disposition);
            }
        }

        var completeState = await complete.GetCompletedStateAsync("AAPL", start.AddMinutes(30));
        var gappedState = await gapped.GetCompletedStateAsync("AAPL", start.AddMinutes(30));
        Assert.Single(completeState.BarsByTimeframe["1h"]);
        Assert.Empty(gappedState.BarsByTimeframe["1h"]);
    }

    [Fact]
    public async Task LiveIntraSessionGap_RequiresRestRepairBeforeStateIsReady()
    {
        var clock = new FixedTimeProvider(Utc(2026, 8, 27, 14, 33));
        var processor = CreateProcessor(NullCandleStore.Instance, clock);
        Activate(processor, 4);
        var first = Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m);
        var missing = Bar("AAPL", Utc(2026, 8, 27, 14, 31), 100.5m);
        var latest = Bar("AAPL", Utc(2026, 8, 27, 14, 32), 101m);

        Assert.Equal(MarketBarDisposition.Accepted, (await processor.ProcessAsync(
            Event(first, MarketBarEventKind.CompletedBar, first.Timestamp.AddMinutes(1), 4))).Disposition);
        Assert.Equal(MarketBarDisposition.GapDetected, (await processor.ProcessAsync(
            Event(latest, MarketBarEventKind.CompletedBar, latest.Timestamp.AddMinutes(1), 4))).Disposition);
        Assert.True(processor.RequiresRepair("AAPL"));
        Assert.Null(await processor.GetTickerStateAsync("AAPL", ["1m"], 1, clock.GetUtcNow(), default));

        await processor.BackfillSymbolAsync(
            "AAPL",
            new FixedMarketDataProvider([first, missing, latest]),
            default);

        Assert.False(processor.RequiresRepair("AAPL"));
        var state = await processor.GetCompletedStateAsync("AAPL", clock.GetUtcNow());
        Assert.Equal(3, state.BarsByTimeframe["1m"].Count);
        Assert.Null(await processor.GetTickerStateAsync(
            "AAPL", ["5m"], 0, clock.GetUtcNow(), default));

        foreach (var minute in new[] { 33, 34 })
        {
            var bar = Bar("AAPL", Utc(2026, 8, 27, 14, minute), 101m + (minute - 32));
            Assert.Equal(MarketBarDisposition.Accepted, (await processor.ProcessAsync(
                Event(bar, MarketBarEventKind.CompletedBar, bar.Timestamp.AddMinutes(1), 4))).Disposition);
        }

        Assert.NotNull(await processor.GetTickerStateAsync(
            "AAPL", ["5m"], 1, Utc(2026, 8, 27, 14, 35), default));
    }

    [Fact]
    public async Task RestRepair_CanConfirmLegitimateNoTradeMinuteWithoutInventingBar()
    {
        var clock = new FixedTimeProvider(Utc(2026, 8, 27, 14, 33));
        var processor = CreateProcessor(NullCandleStore.Instance, clock);
        Activate(processor, 4);
        var first = Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m);
        var latest = Bar("AAPL", Utc(2026, 8, 27, 14, 32), 101m);

        _ = await processor.ProcessAsync(
            Event(first, MarketBarEventKind.CompletedBar, first.Timestamp.AddMinutes(1), 4));
        Assert.Equal(MarketBarDisposition.GapDetected, (await processor.ProcessAsync(
            Event(latest, MarketBarEventKind.CompletedBar, latest.Timestamp.AddMinutes(1), 4))).Disposition);

        await processor.BackfillSymbolAsync(
            "AAPL",
            new FixedMarketDataProvider([first, latest]),
            default);

        Assert.False(processor.RequiresRepair("AAPL"));
        var state = await processor.GetCompletedStateAsync("AAPL", clock.GetUtcNow());
        Assert.Equal([first.Timestamp, latest.Timestamp],
            state.BarsByTimeframe["1m"].Select(bar => bar.Timestamp));
    }

    [Fact]
    public async Task RestRepair_ConfirmedNoTradeMinuteCompletesDerivedBucketFromRealBarsOnly()
    {
        var clock = new FixedTimeProvider(Utc(2026, 8, 27, 14, 35));
        var processor = CreateProcessor(NullCandleStore.Instance, clock);
        var bars = new[]
        {
            Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m),
            Bar("AAPL", Utc(2026, 8, 27, 14, 32), 102m),
            Bar("AAPL", Utc(2026, 8, 27, 14, 33), 103m),
            Bar("AAPL", Utc(2026, 8, 27, 14, 34), 104m)
        };

        _ = await processor.ProcessAsync(
            Event(bars[0], MarketBarEventKind.CompletedBar, bars[0].Timestamp.AddMinutes(1), 1));
        Assert.Equal(MarketBarDisposition.GapDetected, (await processor.ProcessAsync(
            Event(bars[^1], MarketBarEventKind.CompletedBar, bars[^1].Timestamp.AddMinutes(1), 1))).Disposition);

        await processor.BackfillSymbolAsync(
            "AAPL",
            new FixedMarketDataProvider(bars),
            default);

        var state = await processor.GetCompletedStateAsync("AAPL", clock.GetUtcNow());
        Assert.Equal(4, state.BarsByTimeframe["1m"].Count);
        var derived = Assert.Single(state.BarsByTimeframe["5m"]);
        Assert.Equal(4_000m, derived.Volume);
        Assert.Equal(99m, derived.Open);
        Assert.Equal(104m, derived.Close);
    }

    [Fact]
    public async Task ColdRestWarmup_ConfirmsSparseNoTradeMinuteAndCompletesDerivedBucket()
    {
        var clock = new FixedTimeProvider(Utc(2026, 8, 27, 14, 35));
        var processor = CreateProcessor(NullCandleStore.Instance, clock);
        var bars = new[]
        {
            Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m),
            Bar("AAPL", Utc(2026, 8, 27, 14, 32), 102m),
            Bar("AAPL", Utc(2026, 8, 27, 14, 33), 103m),
            Bar("AAPL", Utc(2026, 8, 27, 14, 34), 104m)
        };

        await processor.BackfillSymbolAsync(
            "AAPL",
            new FixedMarketDataProvider(bars),
            default);

        Assert.False(processor.RequiresRepair("AAPL"));
        var state = await processor.GetCompletedStateAsync("AAPL", clock.GetUtcNow());
        Assert.Equal(4, state.BarsByTimeframe["1m"].Count);
        Assert.Equal(4_000m, Assert.Single(state.BarsByTimeframe["5m"]).Volume);
    }

    [Fact]
    public async Task RepeatedRestWarmup_ReplacesPreviouslyCachedAdjustedHistory()
    {
        var timestamp = Utc(2026, 8, 27, 14, 30);
        var clock = new FixedTimeProvider(timestamp.AddMinutes(5));
        var processor = CreateProcessor(NullCandleStore.Instance, clock);
        var beforeSplitRefresh = Bar("AAPL", timestamp, 100m) with { KnownAtUtc = null };
        var afterSplitRefresh = Bar("AAPL", timestamp, 50m) with { KnownAtUtc = null };

        await processor.BackfillSymbolAsync(
            "AAPL",
            new FixedMarketDataProvider([beforeSplitRefresh]),
            default);
        await processor.BackfillSymbolAsync(
            "AAPL",
            new FixedMarketDataProvider([afterSplitRefresh]),
            default);

        var state = await processor.GetCompletedStateAsync("AAPL", clock.GetUtcNow());
        Assert.Equal(50m, Assert.Single(state.BarsByTimeframe["1m"]).Close);
    }

    [Fact]
    public async Task RestWarmup_DoesNotReplaceOverlappingBufferedLiveBar()
    {
        var timestamp = Utc(2026, 8, 27, 14, 30);
        var clock = new FixedTimeProvider(timestamp.AddMinutes(5));
        var processor = CreateProcessor(NullCandleStore.Instance, clock);
        Activate(processor, 4);
        var liveBar = Bar("AAPL", timestamp, 100m) with
        {
            KnownAtUtc = timestamp.AddMinutes(1)
        };
        Assert.Equal(MarketBarDisposition.Accepted, (await processor.ProcessAsync(
            Event(liveBar, MarketBarEventKind.CompletedBar, liveBar.KnownAtUtc.Value, 4))).Disposition);

        await processor.BackfillSymbolAsync(
            "AAPL",
            new FixedMarketDataProvider([liveBar with { Close = 50m, KnownAtUtc = null }]),
            default);

        var state = await processor.GetCompletedStateAsync("AAPL", clock.GetUtcNow());
        Assert.Equal(100m, Assert.Single(state.BarsByTimeframe["1m"]).Close);
    }

    [Fact]
    public async Task RestWarmup_DoesNotPersistCurrentIncompleteProviderBar()
    {
        var barStart = Utc(2026, 8, 27, 14, 30);
        var clock = new FixedTimeProvider(barStart.AddSeconds(30));
        var processor = CreateProcessor(NullCandleStore.Instance, clock);

        await processor.BackfillSymbolAsync(
            "AAPL",
            new FixedMarketDataProvider([Bar("AAPL", barStart, 100m)]),
            default);

        Assert.Empty((await processor.GetCompletedStateAsync("AAPL", clock.GetUtcNow()))
            .BarsByTimeframe["1m"]);
    }

    [Fact]
    public async Task RestRepair_UnknownOmissionSemanticsKeepsGapFailClosed()
    {
        var clock = new FixedTimeProvider(Utc(2026, 8, 27, 14, 33));
        var processor = CreateProcessor(NullCandleStore.Instance, clock);
        var first = Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m);
        var latest = Bar("AAPL", Utc(2026, 8, 27, 14, 32), 101m);

        _ = await processor.ProcessAsync(
            Event(first, MarketBarEventKind.CompletedBar, first.Timestamp.AddMinutes(1), 1));
        _ = await processor.ProcessAsync(
            Event(latest, MarketBarEventKind.CompletedBar, latest.Timestamp.AddMinutes(1), 1));

        await processor.BackfillSymbolAsync(
            "AAPL",
            new UnknownCompletenessMarketDataProvider([first, latest]),
            default);

        Assert.True(processor.RequiresRepair("AAPL"));
        Assert.Null(await processor.GetTickerStateAsync(
            "AAPL", ["1m"], 1, clock.GetUtcNow(), default));
    }

    [Fact]
    public async Task LateBarThatContradictsNoTradeConfirmationRequiresRestReconciliation()
    {
        var clock = new FixedTimeProvider(Utc(2026, 8, 27, 14, 33));
        var processor = CreateProcessor(NullCandleStore.Instance, clock);
        var first = Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m);
        var missing = Bar("AAPL", Utc(2026, 8, 27, 14, 31), 100.5m);
        var latest = Bar("AAPL", Utc(2026, 8, 27, 14, 32), 101m);

        _ = await processor.ProcessAsync(
            Event(first, MarketBarEventKind.CompletedBar, first.Timestamp.AddMinutes(1), 1));
        _ = await processor.ProcessAsync(
            Event(latest, MarketBarEventKind.CompletedBar, latest.Timestamp.AddMinutes(1), 1));
        await processor.BackfillSymbolAsync(
            "AAPL",
            new FixedMarketDataProvider([first, latest]),
            default);
        Assert.False(processor.RequiresRepair("AAPL"));

        var result = await processor.ProcessAsync(
            Event(missing, MarketBarEventKind.CompletedBar, clock.GetUtcNow(), 1));

        Assert.Equal(MarketBarDisposition.OutOfOrder, result.Disposition);
        Assert.True(processor.RequiresRepair("AAPL"));
    }

    [Fact]
    public async Task InvalidateOwnership_ImmediatelyMakesSnapshotsUnavailable()
    {
        var now = Utc(2026, 8, 27, 14, 32);
        var processor = CreateProcessor(NullCandleStore.Instance, new FixedTimeProvider(now));
        await processor.EnsureSymbolReadyAsync("AAPL", default);
        var bar = Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m);
        _ = await processor.ProcessAsync(
            Event(bar, MarketBarEventKind.CompletedBar, bar.Timestamp.AddMinutes(1), 1));

        Assert.NotNull(await processor.GetTickerStateAsync(
            "AAPL", ["1m"], 1, now, default));

        processor.InvalidateOwnership(1);

        Assert.Null(await processor.GetTickerStateAsync(
            "AAPL", ["1m"], 1, now, default));
    }

    [Fact]
    public async Task ActivateOwnership_WithholdsSnapshotsUntilInitialWarmupIsPublished()
    {
        var now = Utc(2026, 8, 27, 14, 32);
        var processor = new StreamingMarketStateProcessor(
            NullCandleStore.Instance,
            new CandleStoreContext("market-state", "shared", "alpaca-sip"),
            StreamingMarketStateOptions.Default,
            new FixedTimeProvider(now),
            NullLogger<StreamingMarketStateProcessor>.Instance);
        processor.AdvanceFencingFloor(1);
        processor.ActivateOwnership(1);
        await processor.EnsureSymbolReadyAsync("AAPL", default);
        var bar = Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m);
        _ = await processor.ProcessAsync(
            Event(bar, MarketBarEventKind.CompletedBar, bar.Timestamp.AddMinutes(1), 1));

        Assert.Null(await processor.GetTickerStateAsync(
            "AAPL", ["1m"], 1, now, default));

        processor.MarkSnapshotsReady(1);

        Assert.NotNull(await processor.GetTickerStateAsync(
            "AAPL", ["1m"], 1, now, default));
    }

    [Fact]
    public async Task OwnershipChangeDuringIndicatorComputationRejectsInFlightSnapshot()
    {
        var now = Utc(2026, 8, 27, 14, 32);
        var calculator = new BlockingIndicatorCalculator();
        var processor = new StreamingMarketStateProcessor(
            NullCandleStore.Instance,
            new CandleStoreContext("market-state", "shared", "alpaca-sip"),
            StreamingMarketStateOptions.Default,
            new FixedTimeProvider(now),
            NullLogger<StreamingMarketStateProcessor>.Instance,
            calculator);
        processor.AdvanceFencingFloor(1);
        processor.ActivateOwnership(1);
        await processor.EnsureSymbolReadyAsync("AAPL", default);
        var bar = Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m);
        _ = await processor.ProcessAsync(
            Event(bar, MarketBarEventKind.CompletedBar, bar.Timestamp.AddMinutes(1), 1));
        processor.MarkSnapshotsReady(1);

        var read = processor.GetTickerStateAsync("AAPL", ["1m"], 1, now, default);
        await calculator.ComputeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        processor.InvalidateOwnership(1);
        calculator.ReleaseCompute.TrySetResult();

        Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task EnsureSymbolReadyAsync_TrimsBarsToBoundedRollingRetention()
    {
        var end = Utc(2026, 8, 27, 15, 0);
        var bars = Enumerable.Range(0, 1_500)
            .Select(index => Bar("AAPL", end.AddMinutes(-1_499 + index), 100m + index / 100m))
            .ToArray();
        var processor = CreateProcessor(
            new SeededCandleStore(bars),
            new FixedTimeProvider(end.AddMinutes(1)),
            recoveryLookbackDays: 1);

        await processor.EnsureSymbolReadyAsync("AAPL", default);
        var state = await processor.GetCompletedStateAsync("AAPL", end.AddMinutes(1));

        Assert.Equal(1_440, state.BarsByTimeframe["1m"].Count);
        Assert.Equal(bars[^1].Timestamp, state.BarsByTimeframe["1m"][^1].Timestamp);
    }

    [Fact]
    public async Task EvictSymbolAsync_DrainsAcceptedWorkBeforeRemovingState()
    {
        var clock = new FixedTimeProvider(Utc(2026, 8, 27, 15, 0));
        var store = new BlockingCandleStore();
        var processor = CreateProcessor(store, clock);
        var bar = Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m);

        var processing = processor.ProcessAsync(
            Event(bar, MarketBarEventKind.CompletedBar, bar.Timestamp.AddMinutes(1), 1));
        await store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var eviction = processor.EvictSymbolAsync("AAPL");
        Assert.False(eviction.IsCompleted);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            processor.GetCompletedStateAsync("AAPL", clock.GetUtcNow())
                .WaitAsync(TimeSpan.FromSeconds(2)));

        store.ReleaseWrite.TrySetResult();
        Assert.Equal(MarketBarDisposition.Accepted, (await processing).Disposition);
        Assert.True(await eviction);
        Assert.Empty((await processor.GetCompletedStateAsync("AAPL", clock.GetUtcNow()))
            .BarsByTimeframe["1m"]);
    }

    [Fact]
    public async Task ProcessAsync_WhenBoundedQueueIsFull_RejectsWithoutHanging()
    {
        var clock = new FixedTimeProvider(Utc(2026, 8, 27, 15, 0));
        var store = new BlockingCandleStore();
        var processor = CreateProcessor(store, clock, symbolPipelineCapacity: 1);
        var first = Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m);
        var second = Bar("AAPL", Utc(2026, 8, 27, 14, 31), 101m);

        var processing = processor.ProcessAsync(
            Event(first, MarketBarEventKind.CompletedBar, first.Timestamp.AddMinutes(1), 1));
        await store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var rejected = await processor.ProcessAsync(
                Event(second, MarketBarEventKind.CompletedBar, second.Timestamp.AddMinutes(1), 1))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MarketBarDisposition.BackpressureRejected, rejected.Disposition);
        Assert.True(processor.RequiresRepair("AAPL"));

        store.ReleaseWrite.TrySetResult();
        Assert.Equal(MarketBarDisposition.Accepted, (await processing).Disposition);
    }

    [Fact]
    public async Task DelegateException_CompletesCallerMarksRepairAndLeavesQueueOperational()
    {
        var clock = new FixedTimeProvider(Utc(2026, 8, 27, 15, 0));
        var store = new FailingOnceCandleStore(failRead: false);
        var processor = CreateProcessor(store, clock);
        var first = Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m);

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(
            Event(first, MarketBarEventKind.CompletedBar, first.Timestamp.AddMinutes(1), 1)));
        Assert.True(processor.RequiresRepair("AAPL"));

        var second = Bar("AAPL", Utc(2026, 8, 27, 14, 31), 101m);
        var result = await processor.ProcessAsync(
            Event(second, MarketBarEventKind.CompletedBar, second.Timestamp.AddMinutes(1), 1))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MarketBarDisposition.Accepted, result.Disposition);
    }

    [Fact]
    public async Task RestartAndReplay_ReconstructSameCompletedStateFingerprint()
    {
        var root = Path.Combine(Path.GetTempPath(), "tradingflow-stream-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new FixedTimeProvider(Utc(2026, 8, 27, 15, 0));
            var store = new LocalFileCandleStore(root);
            var live = CreateProcessor(store, clock);
            Activate(live, 7);
            var bars = Enumerable.Range(0, 5)
                .Select(index => Bar("NVDA", Utc(2026, 8, 27, 14, 30).AddMinutes(index), 180m + index))
                .ToArray();
            foreach (var bar in bars)
            {
                Assert.Equal(MarketBarDisposition.Accepted, (await live.ProcessAsync(
                    Event(bar, MarketBarEventKind.CompletedBar, bar.Timestamp.AddMinutes(1), 7))).Disposition);
            }

            var uninterrupted = await live.GetCompletedStateAsync("NVDA", clock.GetUtcNow());

            var restarted = CreateProcessor(store, clock);
            await restarted.EnsureSymbolReadyAsync("NVDA", default);
            var recovered = await restarted.GetCompletedStateAsync("NVDA", clock.GetUtcNow());

            var replayed = CreateProcessor(NullCandleStore.Instance, clock);
            var adapter = new MarketDataReplayAdapter();
            await foreach (var marketEvent in adapter.ReplayAsync(bars, "alpaca", "sip"))
            {
                Assert.Equal(MarketBarDisposition.Accepted, (await replayed.ProcessAsync(marketEvent)).Disposition);
            }
            var replayState = await replayed.GetCompletedStateAsync("NVDA", clock.GetUtcNow());

            Assert.Equal(uninterrupted.Fingerprint, recovered.Fingerprint);
            Assert.Equal(uninterrupted.Fingerprint, replayState.Fingerprint);
            Assert.Single(recovered.BarsByTimeframe["5m"]);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReplayAsync_AppliesProviderRevisionOnlyWhenItBecameKnown()
    {
        var timestamp = Utc(2026, 8, 27, 14, 30);
        var original = Bar("NVDA", timestamp, 180m) with
        {
            KnownAtUtc = timestamp.AddMinutes(1)
        };
        var revision = original with
        {
            Close = 181m,
            KnownAtUtc = timestamp.AddMinutes(2)
        };
        var events = new List<MarketBarEvent>();
        await foreach (var marketEvent in new MarketDataReplayAdapter().ReplayAsync(
                           [revision, original],
                           "alpaca",
                           "sip"))
        {
            events.Add(marketEvent);
        }

        var processor = CreateProcessor(
            NullCandleStore.Instance,
            new FixedTimeProvider(timestamp.AddMinutes(3)));
        Assert.Equal(MarketBarEventKind.Replay, events[0].Kind);
        Assert.Equal(MarketBarDisposition.Accepted, (await processor.ProcessAsync(events[0])).Disposition);
        var beforeRevision = await processor.GetCompletedStateAsync(
            "NVDA",
            timestamp.AddMinutes(1).AddSeconds(30));

        Assert.Equal(MarketBarEventKind.ReplayRevision, events[1].Kind);
        Assert.Equal(MarketBarDisposition.RevisionAccepted, (await processor.ProcessAsync(events[1])).Disposition);
        var earlierStateAfterRevisionWasAccepted = await processor.GetCompletedStateAsync(
            "NVDA",
            timestamp.AddMinutes(1).AddSeconds(30));
        var afterRevision = await processor.GetCompletedStateAsync("NVDA", timestamp.AddMinutes(3));

        Assert.Equal(180m, Assert.Single(beforeRevision.BarsByTimeframe["1m"]).Close);
        Assert.Equal(180m, Assert.Single(earlierStateAfterRevisionWasAccepted.BarsByTimeframe["1m"]).Close);
        Assert.Equal(181m, Assert.Single(afterRevision.BarsByTimeframe["1m"]).Close);
    }

    [Fact]
    public async Task ReplayStoreAsync_PreservesDurableRevisionOrderAndEarlierAsOfState()
    {
        var root = Path.Combine(Path.GetTempPath(), "tradingflow-revision-replay-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var timestamp = Utc(2026, 8, 27, 14, 30);
            var store = new LocalFileCandleStore(root);
            var clock = new FixedTimeProvider(timestamp.AddMinutes(3));
            var live = CreateProcessor(store, clock);
            Activate(live, 11);
            var original = Bar("NVDA", timestamp, 180m) with
            {
                KnownAtUtc = timestamp.AddMinutes(1)
            };
            var revision = original with
            {
                Close = 181m,
                KnownAtUtc = timestamp.AddMinutes(2)
            };

            Assert.Equal(MarketBarDisposition.Accepted, (await live.ProcessAsync(
                Event(original, MarketBarEventKind.CompletedBar, original.KnownAtUtc.Value, 11))).Disposition);
            Assert.Equal(MarketBarDisposition.RevisionAccepted, (await live.ProcessAsync(
                Event(revision, MarketBarEventKind.ProviderRevision, revision.KnownAtUtc.Value, 11))).Disposition);

            var request = new CandleStoreReadRequest(
                "market-state",
                "shared",
                "alpaca-sip",
                "NVDA",
                "1m",
                timestamp,
                timestamp.AddMinutes(3),
                "stream");
            var replayed = CreateProcessor(NullCandleStore.Instance, clock);
            var replayEvents = new List<MarketBarEvent>();
            var dispositions = new List<MarketBarDisposition>();
            await foreach (var marketEvent in new MarketDataReplayAdapter().ReplayStoreAsync(
                               store,
                               request,
                               "alpaca",
                               "sip"))
            {
                replayEvents.Add(marketEvent);
                dispositions.Add((await replayed.ProcessAsync(marketEvent)).Disposition);
            }

            Assert.Collection(
                replayEvents,
                value =>
                {
                    Assert.Equal(MarketBarEventKind.Replay, value.Kind);
                    Assert.Equal(original.KnownAtUtc, value.ObservedAtUtc);
                },
                value =>
                {
                    Assert.Equal(MarketBarEventKind.ReplayRevision, value.Kind);
                    Assert.Equal(revision.KnownAtUtc, value.ObservedAtUtc);
                });
            Assert.Equal(
                [MarketBarDisposition.Accepted, MarketBarDisposition.RevisionAccepted],
                dispositions);
            var beforeRevision = await replayed.GetCompletedStateAsync(
                "NVDA",
                timestamp.AddMinutes(1).AddSeconds(30));
            var afterRevision = await replayed.GetCompletedStateAsync(
                "NVDA",
                timestamp.AddMinutes(3));

            Assert.Equal(180m, Assert.Single(beforeRevision.BarsByTimeframe["1m"]).Close);
            Assert.Equal(181m, Assert.Single(afterRevision.BarsByTimeframe["1m"]).Close);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RestartWithProductionRecoveryDepth_PreservesSameTimeRvol()
    {
        var root = Path.Combine(Path.GetTempPath(), "tradingflow-stream-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var now = Utc(2026, 8, 28, 14, 32);
            var clock = new FixedTimeProvider(now);
            var store = new LocalFileCandleStore(root);
            var live = CreateProcessor(store, clock, recoveryLookbackDays: 45);
            Activate(live, 7);
            var start = Utc(2026, 7, 30, 14, 30);
            var bars = new List<OhlcvBar>();
            var tradeDate = start;
            while (bars.Count < 21)
            {
                if (tradeDate.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                {
                    bars.Add(Bar("NVDA", tradeDate, 180m + bars.Count) with
                    {
                        Volume = bars.Count == 20 ? 2_000m : 1_000m
                    });
                }

                tradeDate = tradeDate.AddDays(1);
            }

            foreach (var bar in bars)
            {
                Assert.Equal(MarketBarDisposition.Accepted, (await live.ProcessAsync(
                    Event(bar, MarketBarEventKind.CompletedBar, bar.Timestamp.AddMinutes(1), 7))).Disposition);
            }

            var liveState = await live.GetCompletedStateAsync("NVDA", now);
            var evidenceEngine = new TradingFlow.Engine.Indicators.IndicatorEngine(
                new TradingFlow.Engine.Indicators.MarketEvidenceProfile(
                    "restart_reconstruction_test_v1",
                    20,
                    20,
                    "America/New_York"));
            var liveRvol = evidenceEngine
                .Compute(liveState.BarsByTimeframe["1m"])[^1]
                .RelativeVolume;

            var restarted = CreateProcessor(store, clock, recoveryLookbackDays: 45);
            await restarted.EnsureSymbolReadyAsync("NVDA", default);
            var recoveredState = await restarted.GetCompletedStateAsync("NVDA", now);
            var recoveredRvol = evidenceEngine
                .Compute(recoveredState.BarsByTimeframe["1m"])[^1]
                .RelativeVolume;

            Assert.Equal(2m, liveRvol);
            Assert.Equal(liveRvol, recoveredRvol);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProcessAsync_WhenLiveFeedDiffersFromWarmState_RejectsAndRequiresRepair()
    {
        var clock = new FixedTimeProvider(Utc(2026, 8, 27, 14, 32));
        var processor = CreateProcessor(NullCandleStore.Instance, clock);
        var warm = Bar("AAPL", Utc(2026, 8, 27, 14, 30), 100m);
        await processor.BackfillSymbolAsync(
            "AAPL",
            new FixedMarketDataProvider([warm]),
            default);
        var incoming = Bar("AAPL", Utc(2026, 8, 27, 14, 31), 101m) with { DataFeed = "iex" };
        var result = await processor.ProcessAsync(new MarketBarEvent(
            "alpaca",
            "iex",
            incoming,
            MarketBarEventKind.CompletedBar,
            incoming.Timestamp.AddMinutes(1),
            1));

        Assert.Equal(MarketBarDisposition.Invalid, result.Disposition);
        Assert.Contains("does not match warm-state feed", result.Detail, StringComparison.Ordinal);
        Assert.True(processor.RequiresRepair("AAPL"));
    }

    [Fact]
    public void Resample_OneHourBarsNeverMixRegularAndPostmarketSessions()
    {
        var timezone = ResolveNewYork();
        var regular = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 8, 27, 15, 59, 0), timezone);
        var postmarket = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 8, 27, 16, 0, 0), timezone);
        var bars = new[]
        {
            Bar("AAPL", new DateTimeOffset(regular, TimeSpan.Zero), 100m),
            Bar("AAPL", new DateTimeOffset(postmarket, TimeSpan.Zero), 101m)
        };

        var result = new BarResampler().Resample(bars, "1h");

        Assert.Equal(2, result.Count);
        Assert.Equal(100m, result[0].Close);
        Assert.Equal(101m, result[1].Close);
    }

    private static StreamingMarketStateProcessor CreateProcessor(
        ICandleStore store,
        TimeProvider clock,
        int recoveryLookbackDays = 45,
        int symbolPipelineCapacity = 32) =>
        CreateReadyProcessor(
            store,
            new CandleStoreContext("market-state", "shared", "alpaca-sip"),
            new StreamingMarketStateOptions(
                symbolPipelineCapacity,
                2,
                recoveryLookbackDays,
                ["5m", "1h"]),
            clock,
            NullLogger<StreamingMarketStateProcessor>.Instance);

    private static StreamingMarketStateProcessor CreateReadyProcessor(
        ICandleStore store,
        CandleStoreContext context,
        StreamingMarketStateOptions options,
        TimeProvider clock,
        Microsoft.Extensions.Logging.ILogger<StreamingMarketStateProcessor> logger)
    {
        var processor = new StreamingMarketStateProcessor(store, context, options, clock, logger);
        processor.AdvanceFencingFloor(1);
        processor.ActivateOwnership(1);
        processor.MarkSnapshotsReady(1);
        return processor;
    }

    private static void Activate(StreamingMarketStateProcessor processor, long fencingToken)
    {
        processor.AdvanceFencingFloor(fencingToken);
        processor.ActivateOwnership(fencingToken);
        processor.MarkSnapshotsReady(fencingToken);
    }

    private static MarketBarEvent Event(
        OhlcvBar bar,
        MarketBarEventKind kind,
        DateTimeOffset observedAt,
        long fence) => new("alpaca", "sip", bar, kind, observedAt, fence);

    private static OhlcvBar Bar(string ticker, DateTimeOffset timestamp, decimal close) =>
        new(
            ticker,
            timestamp,
            "1m",
            close - 1m,
            close + 1m,
            close - 2m,
            close,
            1_000m,
            "sip",
            "all",
            CoverageVerifiedThroughUtc: timestamp.AddMinutes(1));

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static TimeZoneInfo ResolveNewYork()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class BlockingIndicatorCalculator : TradingFlow.Engine.Indicators.IIndicatorCalculator
    {
        private readonly TradingFlow.Engine.Indicators.IndicatorEngine inner = new();
        public TaskCompletionSource ComputeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCompute { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<IndicatorSnapshot> Compute(IReadOnlyList<OhlcvBar> inputBars)
        {
            ComputeStarted.TrySetResult();
            ReleaseCompute.Task.GetAwaiter().GetResult();
            return inner.Compute(inputBars);
        }
    }

    private sealed class ThrowingMarketDataProvider : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> intervals,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("Provider must not be called for an empty universe.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class FixedMarketDataProvider(IReadOnlyList<OhlcvBar> bars) :
        IMarketDataProvider,
        IMarketDataCompletenessProvider
    {
        public bool OmittedIntradayIntervalsMeanNoQualifyingTrades => true;

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> intervals,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var bar in bars)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return bar;
                await Task.Yield();
            }
        }
    }

    private sealed class UnknownCompletenessMarketDataProvider(IReadOnlyList<OhlcvBar> bars) :
        IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> intervals,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var bar in bars)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return bar;
                await Task.Yield();
            }
        }
    }

    private sealed class SeededCandleStore(IReadOnlyList<OhlcvBar> bars) : IFencedCandleStore
    {
        public Task AdvanceFencingTokenAsync(
            CandleStoreContext context,
            string source,
            long fencingToken,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertBarsAsync(
            CandleStoreWriteRequest request,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<OhlcvBar>> ReadBarsAsync(
            CandleStoreReadRequest request,
            CancellationToken cancellationToken) => Task.FromResult(bars);
    }

    private sealed class BlockingCandleStore : IFencedCandleStore
    {
        public TaskCompletionSource WriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AdvanceFencingTokenAsync(
            CandleStoreContext context,
            string source,
            long fencingToken,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task UpsertBarsAsync(
            CandleStoreWriteRequest request,
            CancellationToken cancellationToken)
        {
            WriteStarted.TrySetResult();
            await ReleaseWrite.Task.WaitAsync(cancellationToken);
        }

        public Task<IReadOnlyList<OhlcvBar>> ReadBarsAsync(
            CandleStoreReadRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OhlcvBar>>([]);
    }

    private sealed class FailingOnceCandleStore(bool failRead) : IFencedCandleStore
    {
        private int writes;

        public Task AdvanceFencingTokenAsync(
            CandleStoreContext context,
            string source,
            long fencingToken,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertBarsAsync(
            CandleStoreWriteRequest request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                throw new InvalidOperationException("Injected write failure.");
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OhlcvBar>> ReadBarsAsync(
            CandleStoreReadRequest request,
            CancellationToken cancellationToken) =>
            failRead
                ? throw new InvalidOperationException("Injected read failure.")
                : Task.FromResult<IReadOnlyList<OhlcvBar>>([]);
    }
}
