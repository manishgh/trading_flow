using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Data.Candles;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Pipeline;

namespace TradingFlow.Tests;

public sealed class StreamingIndicatorCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 27, 14, 30, 0, TimeSpan.Zero);

    /// <summary>Equal completed inputs reuse one calculation, and neither returned bars nor snapshots can corrupt the retained input key.</summary>
    [Fact]
    public async Task RepeatedCompletedInputs_ReuseCalculationAndProtectCachedSnapshots()
    {
        var calculator = new CountingCalculator();
        var processor = await Ready(calculator);
        await Add(processor, Bar(0));
        var first = await Read(processor, Start.AddMinutes(1));
        var second = await Read(processor, Start.AddMinutes(1).AddSeconds(20));
        Assert.Equal(1, calculator.Calls);
        Assert.Same(first!.SnapshotsByTimeframe["1m"], second!.SnapshotsByTimeframe["1m"]);
        var list = Assert.IsAssignableFrom<IList<IndicatorSnapshot>>(first.SnapshotsByTimeframe["1m"]);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(list[0]));
        ((OhlcvBar[])first.BarsByTimeframe["1m"])[0] = Bar(0) with { Close = 200 };
        var third = await Read(processor, Start.AddMinutes(1).AddSeconds(30));
        Assert.Equal(100m, Assert.Single(third!.SnapshotsByTimeframe["1m"]).CurrentPrice);
        Assert.Equal(1, calculator.Calls);
    }

    /// <summary>Revisions changing values or only provenance must not reuse the preceding record's indicators.</summary>
    [Theory]
    [InlineData("price")]
    [InlineData("adjustment")]
    [InlineData("known-at")]
    [InlineData("coverage")]
    public async Task ChangedBarRecord_RecomputesIncludingProvenanceOnlyRevisions(string change)
    {
        var calculator = new CountingCalculator();
        var processor = await Ready(calculator);
        var original = Bar(0);
        await Add(processor, original);
        Assert.NotNull(await Read(processor, Start.AddMinutes(1)));
        var revision = change switch {
            "price" => original with { Close = 101, KnownAtUtc = Start.AddMinutes(1).AddSeconds(30) },
            "adjustment" => original with { AdjustmentPolicy = "raw", KnownAtUtc = Start.AddMinutes(1).AddSeconds(30) },
            "known-at" => original with { KnownAtUtc = Start.AddMinutes(1).AddSeconds(30) },
            _ => original with { CoverageVerifiedThroughUtc = Start.AddMinutes(2), KnownAtUtc = Start.AddMinutes(1).AddSeconds(30) }
        };
        await Add(processor, revision, MarketBarEventKind.ProviderRevision);
        var updated = await Read(processor, Start.AddMinutes(1).AddSeconds(45));
        Assert.NotNull(updated);
        Assert.Equal(2, calculator.Calls);
        Assert.Equal(revision, Assert.Single(calculator.LastInput));
        Assert.Equal(revision.AdjustmentPolicy, Assert.Single(updated.SnapshotsByTimeframe["1m"]).AdjustmentPolicy);
    }

    /// <summary>A later known revision cannot leak through the cache when the next read requests an earlier evidence time.</summary>
    [Fact]
    public async Task HistoricalAsOfAfterRevision_ReturnsEarlierKnownValues()
    {
        var calculator = new CountingCalculator();
        var processor = await Ready(calculator);
        var original = Bar(0);
        await Add(processor, original);
        await Add(processor, original with { Close = 101, KnownAtUtc = Start.AddMinutes(1).AddSeconds(30) }, MarketBarEventKind.ProviderRevision);
        var before = await Read(processor, Start.AddMinutes(1).AddSeconds(15));
        var after = await Read(processor, Start.AddMinutes(1).AddSeconds(45));
        var earlierAgain = await Read(processor, Start.AddMinutes(1).AddSeconds(15));
        Assert.Equal(100m, Assert.Single(before!.SnapshotsByTimeframe["1m"]).CurrentPrice);
        Assert.Equal(101m, Assert.Single(after!.SnapshotsByTimeframe["1m"]).CurrentPrice);
        Assert.Equal(100m, Assert.Single(earlierAgain!.SnapshotsByTimeframe["1m"]).CurrentPrice);
        Assert.Equal(3, calculator.Calls);
    }

    /// <summary>Applying a future-completing bar leaves the earlier cache valid until that bar is complete and known.</summary>
    [Fact]
    public async Task CompletionBoundary_UsesActualSelectedCompletedBars()
    {
        var calculator = new CountingCalculator();
        var processor = await Ready(calculator);
        await Add(processor, Bar(0));
        Assert.NotNull(await Read(processor, Start.AddMinutes(1)));
        await Add(processor, Bar(1));
        var earlier = await Read(processor, Start.AddMinutes(1).AddSeconds(30));
        Assert.Single(earlier!.SnapshotsByTimeframe["1m"]);
        Assert.Equal(1, calculator.Calls);
        var completed = await Read(processor, Start.AddMinutes(2));
        Assert.Equal(2, completed!.SnapshotsByTimeframe["1m"].Count);
        Assert.Equal(2, calculator.Calls);
    }

    /// <summary>Repair remains mandatory despite cached math, and confirmed sparse intervals rebuild derived bars using only actual observations.</summary>
    [Fact]
    public async Task SparseRestRepair_RecomputesChangedInputsAndDoesNotBypassGapChecks()
    {
        var calculator = new CountingCalculator();
        var processor = await Ready(calculator);
        await Add(processor, Bar(0));
        Assert.NotNull(await Read(processor, Start.AddMinutes(1)));
        await Add(processor, Bar(4));
        Assert.True(processor.RequiresRepair("CACHE"));
        Assert.Null(await Read(processor, Start.AddMinutes(5)));
        Assert.Equal(1, calculator.Calls);
        await processor.BackfillSymbolAsync("CACHE", new SparseProvider([Bar(0), Bar(2), Bar(3), Bar(4)]), default);
        Assert.False(processor.RequiresRepair("CACHE"));
        var repaired = await Read(processor, Start.AddMinutes(5));
        Assert.Equal(4, repaired!.SnapshotsByTimeframe["1m"].Count);
        Assert.Equal(2, calculator.Calls);
        var derived = await processor.GetTickerStateAsync("CACHE", ["5m"], 1, Start.AddMinutes(5), default);
        Assert.Equal(4000m, Assert.Single(derived!.SnapshotsByTimeframe["5m"]).CurrentVolume);
        Assert.Equal(3, calculator.Calls);
    }

    /// <summary>Evicting a symbol also evicts its cache; recreating identical source inputs must calculate afresh.</summary>
    [Fact]
    public async Task Eviction_DiscardsCachedIndicatorSeries()
    {
        var calculator = new CountingCalculator();
        var processor = await Ready(calculator);
        await Add(processor, Bar(0));
        Assert.NotNull(await Read(processor, Start.AddMinutes(1)));
        Assert.True(await processor.EvictSymbolAsync("CACHE"));
        await processor.EnsureSymbolReadyAsync("CACHE", default);
        await Add(processor, Bar(0));
        Assert.NotNull(await Read(processor, Start.AddMinutes(1)));
        Assert.Equal(2, calculator.Calls);
    }

    /// <summary>Computation outside the queue cannot publish from an evicted symbol, including when a replacement has the same stream fence.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvictionDuringCalculation_RejectsRemovedOrReplacedSymbolOwner(bool recreate)
    {
        var calculator = new CountingCalculator(block: true);
        var processor = await Ready(calculator);
        await Add(processor, Bar(0));
        var read = Read(processor, Start.AddMinutes(1));
        await calculator.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Assert.True(await processor.EvictSymbolAsync("CACHE").WaitAsync(TimeSpan.FromSeconds(3)));
            if (recreate)
            {
                await processor.EnsureSymbolReadyAsync("CACHE", default);
                await Add(processor, Bar(0));
            }
        }
        finally { calculator.Release.TrySetResult(); }
        Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(3)));
        if (recreate)
        {
            Assert.NotNull(await Read(processor, Start.AddMinutes(1)));
            Assert.Equal(2, calculator.Calls);
        }
        else Assert.DoesNotContain("CACHE", processor.GetTrackedSymbols());
    }

    /// <summary>An installed calendar/calculator pair invalidates the previous profile's cache even with identical candles.</summary>
    [Fact]
    public async Task ProfileReplacement_DoesNotReuseEarlierCalculatorSnapshots()
    {
        var calculator = new CountingCalculator();
        var processor = await Ready(calculator);
        await Add(processor, Bar(0));
        var before = await Read(processor, Start.AddMinutes(1));
        processor.UpdateMarketSessionSchedules(Schedules());
        var after = await Read(processor, Start.AddMinutes(1));
        var repeated = await Read(processor, Start.AddMinutes(1));
        Assert.NotSame(before!.SnapshotsByTimeframe["1m"], after!.SnapshotsByTimeframe["1m"]);
        Assert.Same(after.SnapshotsByTimeframe["1m"], repeated!.SnapshotsByTimeframe["1m"]);
        Assert.Equal(1, calculator.Calls);
    }

    /// <summary>A profile refresh during computation rejects the old in-flight series and permits a fresh consistent-profile read.</summary>
    [Fact]
    public async Task ProfileReplacementDuringCalculation_RejectsInFlightSnapshot()
    {
        var calculator = new CountingCalculator(block: true);
        var processor = await Ready(calculator);
        await Add(processor, Bar(0));
        var read = Read(processor, Start.AddMinutes(1));
        await calculator.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try { processor.UpdateMarketSessionSchedules(Schedules()); }
        finally { calculator.Release.TrySetResult(); }
        Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.NotNull(await Read(processor, Start.AddMinutes(1)));
    }

    /// <summary>Cached math never authorizes a read after its stream ownership is invalidated.</summary>
    [Fact]
    public async Task OwnershipInvalidation_RejectsCachedRead()
    {
        var calculator = new CountingCalculator();
        var processor = await Ready(calculator);
        await Add(processor, Bar(0));
        Assert.NotNull(await Read(processor, Start.AddMinutes(1)));
        processor.InvalidateOwnership(1);
        Assert.Null(await Read(processor, Start.AddMinutes(1)));
        Assert.Equal(1, calculator.Calls);
    }

    /// <summary>Creates a fenced in-memory pipeline without provider calls or persistence outside NullCandleStore.</summary>
    private static async Task<StreamingMarketStateProcessor> Ready(IIndicatorCalculator calculator)
    {
        var processor = new StreamingMarketStateProcessor(NullCandleStore.Instance,
            new("market-state", "cache-fixture", "alpaca-sip"), new(32, 2, 45, ["5m"]),
            new Clock(Start.AddMinutes(5)), NullLogger<StreamingMarketStateProcessor>.Instance, calculator);
        processor.AdvanceFencingFloor(1);
        processor.ActivateOwnership(1);
        await processor.EnsureSymbolReadyAsync("CACHE", default);
        processor.MarkSnapshotsReady(1);
        return processor;
    }

    /// <summary>Produces valid immutable one-minute fixture bars with explicit source and coverage provenance.</summary>
    private static OhlcvBar Bar(int minute) => new("CACHE", Start.AddMinutes(minute), "1m", 99, 102, 98, 100,
        1000, "sip", "all", Start.AddMinutes(minute + 1), Start.AddMinutes(minute + 1));

    /// <summary>Uses the normal live revision checks and accepts only the expected fixture dispositions.</summary>
    private static async Task Add(StreamingMarketStateProcessor processor, OhlcvBar bar, MarketBarEventKind kind = MarketBarEventKind.CompletedBar)
    {
        var result = await processor.ProcessAsync(new("alpaca", "sip", bar, kind, bar.KnownAtUtc!.Value, 1));
        Assert.Contains(result.Disposition, new[] { MarketBarDisposition.Accepted, MarketBarDisposition.RevisionAccepted, MarketBarDisposition.GapDetected });
    }

    /// <summary>Requests only already completed fixture bars at an explicit evidence cutoff.</summary>
    private static Task<TickerMarketState?> Read(StreamingMarketStateProcessor processor, DateTimeOffset asOf) =>
        processor.GetTickerStateAsync("CACHE", ["1m"], 1, asOf, default);

    /// <summary>Provides an explicit exchange profile so the production replacement constructs matching indicator/resampler tools.</summary>
    private static IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Schedules() =>
        new Dictionary<DateOnly, MarketSessionSchedule> { [new(2026, 8, 27)] = MarketSessionSchedule.RegularDay(new(2026, 8, 27)) };

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        /// <summary>Fixes repair coverage and freshness decisions at the fixture's known end.</summary>
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CountingCalculator(bool block = false) : IIndicatorCalculator
    {
        private readonly IndicatorEngine inner = new();
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public IReadOnlyList<OhlcvBar> LastInput { get; private set; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Counts real indicator evaluations and optionally pauses one evaluation for profile/ownership race checks.</summary>
        public IReadOnlyList<IndicatorSnapshot> Compute(IReadOnlyList<OhlcvBar> inputBars)
        {
            Interlocked.Increment(ref calls);
            LastInput = inputBars.ToArray();
            Started.TrySetResult();
            if (block) Release.Task.GetAwaiter().GetResult();
            return inner.Compute(inputBars);
        }
    }

    private sealed class SparseProvider(IReadOnlyList<OhlcvBar> bars) : IMarketDataProvider, IMarketDataCompletenessProvider
    {
        public bool OmittedSubDailyIntervalsMeanNoQualifyingTrades => true;

        /// <summary>Returns only isolated fixture bars; explicit sparse semantics let the existing repair path confirm omitted no-trade minutes.</summary>
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> intervals,
            DateTimeOffset start, DateTimeOffset end, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var bar in bars) { cancellationToken.ThrowIfCancellationRequested(); yield return bar; await Task.CompletedTask; }
        }
    }
}
