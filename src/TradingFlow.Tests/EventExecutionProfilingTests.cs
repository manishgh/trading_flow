using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Risk;
using Xunit.Abstractions;

namespace TradingFlow.Tests;

[CollectionDefinition(EventExecutionProfilingCollection.Name, DisableParallelization = true)]
public sealed class EventExecutionProfilingCollection
{
    public const string Name = "Event execution profiling";
}

/// <summary>Keep allocation-heavy profiling out of ordinary tests unless the operator explicitly selects it.</summary>
public sealed class EventExecutionProfileAttribute : FactAttribute
{
    /// <summary>Enable this reproducible measurement only when the dedicated environment switch is set.</summary>
    public EventExecutionProfileAttribute()
    {
        if (Environment.GetEnvironmentVariable("TRADINGFLOW_PROFILE_EVENT_EXECUTION") != "1")
            Skip = "Set TRADINGFLOW_PROFILE_EVENT_EXECUTION=1 and select EventExecutionProfilingTests to measure.";
    }
}

/// <summary>Measures existing book and portfolio paths without changing event semantics or asserting machine-dependent speed.</summary>
[Collection(EventExecutionProfilingCollection.Name)]
public sealed class EventExecutionProfilingTests(ITestOutputHelper output)
{
    private const long Epoch = 1_800_000_000_000_000_000L;
    private const long Tick = 1000;
    private const int Symbols = 5;
    private const int QuoteOnlyFrames = 5000;
    private static readonly string[] SymbolNames = ["PROFILE-A", "PROFILE-B", "PROFILE-C", "PROFILE-D", "PROFILE-E"];
    private static readonly ExecutionEventSimulationPolicy Policy = new(new(100, 0, 0, 0, 1, 1, .0000278m, .000166m, 8.3m),
        ExecutionProtectionActivation.NativeParentFullyFilled, 1_000_000_000L, 0, true, 1, 1, "isolated-profiling-fixture-v1");
    private sealed record Step(ExecutionEventBatch Batch, IReadOnlyList<ExecutionEventOrderCommand> Commands);
    private sealed record Phase(double ElapsedMilliseconds, long AllocatedBytes,
        long ProcessPeakWorkingSetBytes, long ManagedBytesWithoutCollection);
    private sealed record BookRun(ExecutionEventBookState State, IReadOnlyList<TimedSimulatedExecutionEvent> Events,
        Phase History, Phase QuoteOnly, string FullStateAndEventsSha256);

    /// <summary>Isolates copying of retained completed-order history under the same five-symbol quote-only workload.</summary>
    [EventExecutionProfile]
    [Trait("Category", "Performance")]
    public void MeasureRetainedBookHistory_AndVerifyCompleteReplayParity()
    {
        WriteEnvironment();
        RunBook(History(2), Tail(2, 20)); // Warm the actual matcher before recording the representative fixtures.
        foreach (var parents in new[] { 10, 100, 500 })
        {
            var history = History(parents);
            var tail = Tail(parents, QuoteOnlyFrames);
            var first = RunBook(history, tail);
            var second = RunBook(history, tail);
            Assert.Equal(first.FullStateAndEventsSha256, second.FullStateAndEventsSha256);
            Assert.Equal(2 * parents, first.Events.Count(item => item.Event.LastFillQuantity > 0));
            Assert.Equal(2 * parents, first.State.Book.KnownOrderContracts.Count);
            Assert.Empty(first.State.Book.WorkingOrders);
            Assert.All(first.State.Book.Positions.Values, position => Assert.Equal(0, position.SignedQuantity));
            Assert.Equal(2 * parents, ReceiptCount(first.State.FillReceipts));
            Assert.Equal(parents, ReceiptCount(first.State.ParentEntryReceipts));
            Assert.Equal(parents, ReceiptCount(first.State.ParentReductionReceipts));
            output.WriteLine(JsonSerializer.Serialize(new {
                Fixture = "direct_event_book_retained_completed_history", CompletedParents = parents, SymbolCount = Symbols,
                HistoryFrames = history.Length, QuoteOnlyFrames = tail.Length, SourceQuotes = parents + tail.Length,
                SourceTrades = 2 * parents, SubmittedOrders = 2 * parents, RetainedContracts = first.State.Book.KnownOrderContracts.Count,
                OrderReceiptKeys = first.State.FillReceipts!.Count, OrderReceiptItems = ReceiptCount(first.State.FillReceipts),
                EntryReceiptKeys = first.State.ParentEntryReceipts!.Count, EntryReceiptItems = ReceiptCount(first.State.ParentEntryReceipts),
                ReductionReceiptKeys = first.State.ParentReductionReceipts!.Count, ReductionReceiptItems = ReceiptCount(first.State.ParentReductionReceipts),
                FixtureSha256 = Hash(new { history, tail, Policy }), FirstHistory = first.History, FirstQuoteOnly = first.QuoteOnly,
                SecondHistory = second.History, SecondQuoteOnly = second.QuoteOnly,
                first.FullStateAndEventsSha256, FillAndFeeSha256 = Hash(first.Events.Where(item => item.Event.LastFillQuantity > 0).ToArray()),
                AllocationMeaning = "Direct synchronous book allocations are measured on the current thread; fixture construction and hashing are excluded.",
                MemoryMeaning = "Process peak is the test-host lifetime high-water mark, not a retained-memory delta; managed bytes include uncollected garbage."
            }));
        }
    }

    /// <summary>Measures the normal coordinator separately because it skips matcher steps when closed books receive only quotes.</summary>
    [EventExecutionProfile]
    [Trait("Category", "Performance")]
    public async Task MeasurePortfolioCoordinator_WithNativeProtectionAndClosedParents()
    {
        WriteEnvironment();
        const int parents = 50;
        var strategy = StrategyDecisionKernelTests.CreateRequest().Strategy;
        strategy = strategy with { Execution = strategy.Execution with { SlippageBps = 0 } };
        var candidates = Enumerable.Range(0, parents).Select(index => Candidate(index, strategy.StrategyId, strategy.StrategyName)).ToArray();
        var request = new EventPortfolioReplayRequest("isolated-performance-fixture", new(10000, 1, 25, Symbols, 1, 1, 1, true),
            [strategy], candidates, [], new Dictionary<string, string>());
        var history = History(parents);
        var tail = Tail(parents, QuoteOnlyFrames);
        var frames = history.Concat(tail).Select(step => new SipPortfolioExecutionFrame(step.Batch.EventTimeNanoseconds,
            step.Batch.KnownAtNanoseconds, [new(step.Batch, true, "fixture-session", [], [], [])])).ToArray();
        var first = await MeasurePortfolio(request, frames);
        var second = await MeasurePortfolio(request, frames);
        Assert.Empty(first.Outcome.UnavailableReasons);
        Assert.Empty(first.Outcome.Failures);
        Assert.Equal(parents, first.Outcome.Trades.Count);
        Assert.Equal(2 * parents, first.Outcome.Events.Count(item => item.Event.LastFillQuantity > 0));
        Assert.Equal(Hash(first.Outcome), Hash(second.Outcome));
        output.WriteLine(JsonSerializer.Serialize(new {
            Fixture = "normal_event_portfolio_native_protection", CompletedParents = parents, SymbolCount = Symbols,
            Frames = frames.Length, SourceQuotes = parents + tail.Length, SourceTrades = 2 * parents,
            SubmittedParents = parents, CommittedNativeChildren = 2 * parents, ScheduledLiquidations = parents,
            FixtureSha256 = Hash(new { request, frames, Policy }), First = first.Metrics, Second = second.Metrics,
            FullOutcomeSha256 = Hash(first.Outcome), FillAndFeeSha256 = Hash(first.Outcome.Events.Where(item => item.Event.LastFillQuantity > 0).ToArray()),
            AllocationMeaning = "Portfolio allocations are process-wide across the async replay; process peak is a host-lifetime high-water mark."
        }));
    }

    /// <summary>Measures only existing matcher steps; fixture construction and semantic hashing remain outside timed phases.</summary>
    private static BookRun RunBook(Step[] history, Step[] tail)
    {
        var simulator = new DeterministicExecutionSimulator();
        var state = ExecutionEventBookState.Empty;
        var events = new List<TimedSimulatedExecutionEvent>();
        Phase RunPhase(Step[] steps)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            foreach (var step in steps)
            {
                var result = simulator.AdvanceEvent(new(state, step.Batch, step.Commands, Policy));
                if (result.UnavailableReasons.Count > 0) throw new InvalidOperationException(String.Join("; ", result.UnavailableReasons));
                state = result.State;
                events.AddRange(result.Events);
            }
            timer.Stop();
            return Capture(timer, GC.GetAllocatedBytesForCurrentThread() - allocated);
        }
        var historyMetrics = RunPhase(history);
        var tailMetrics = RunPhase(tail);
        var orderedFeeGroups = new SortedSet<string>(state.Book.ChargedFixedFeeGroupIds, StringComparer.Ordinal);
        var stableState = state with { Book = state.Book with { ChargedFixedFeeGroupIds = orderedFeeGroups } };
        return new(state, events, historyMetrics, tailMetrics, Hash(new { State = stableState, Events = events }));
    }

    /// <summary>Records normal portfolio replay without a custom driver or provider/archive writes.</summary>
    private static async Task<(EventPortfolioReplayOutcome Outcome, Phase Metrics)> MeasurePortfolio(EventPortfolioReplayRequest request,
        SipPortfolioExecutionFrame[] frames)
    {
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew();
        var outcome = Assert.Single(await EventPortfolioExecution.ReplayManyAsync([request], Timeline(frames), _ => Policy));
        timer.Stop();
        return (outcome, Capture(timer, GC.GetTotalAllocatedBytes(precise: true) - allocated));
    }

    /// <summary>Reports observable memory quantities with their exact meanings rather than implying precise retained-state size.</summary>
    private static Phase Capture(Stopwatch timer, long allocated)
    {
        using var process = Process.GetCurrentProcess();
        return new(timer.Elapsed.TotalMilliseconds, allocated, process.PeakWorkingSet64, GC.GetTotalMemory(forceFullCollection: false));
    }

    /// <summary>Prebuilds alternating symbols with a prior quote, entry, qualified print, later reduction and qualified exit print.</summary>
    private static Step[] History(int parents)
    {
        var steps = new List<Step>(parents * 5);
        for (var index = 0; index < parents; index++)
        {
            var at = Epoch + index * 10 * Tick;
            var symbol = SymbolNames[index % Symbols];
            var parent = "parent-" + index;
            steps.Add(new(Batch(symbol, at, quotes: [Quote(at)]), []));
            steps.Add(new(Batch(symbol, at + Tick), [Command(parent, symbol, at + Tick, false)]));
            steps.Add(new(Batch(symbol, at + 2 * Tick, trades: [Trade(at + 2 * Tick)]), []));
            steps.Add(new(Batch(symbol, at + 3 * Tick), [Command("exit-" + index, symbol, at + 3 * Tick, true, parent)]));
            steps.Add(new(Batch(symbol, at + 4 * Tick, trades: [Trade(at + 4 * Tick)]), []));
        }
        return steps.ToArray();
    }

    /// <summary>Uses the identical quote count and symbol distribution after each history size, while all positions are flat.</summary>
    private static Step[] Tail(int parents, int count) => Enumerable.Range(0, count).Select(index => {
        var at = Epoch + (parents * 10L + index) * Tick;
        return new Step(Batch(SymbolNames[index % Symbols], at, quotes: [Quote(at)]), []);
    }).ToArray();

    /// <summary>Preserves source sequence and nanosecond clocks without synthesizing OHLC or execution volume.</summary>
    private static ExecutionEventBatch Batch(string symbol, long at, ExecutionQuoteTick[]? quotes = null, ExecutionTradeTick[]? trades = null) =>
        new(symbol, at, at, at - Epoch, quotes ?? [], trades ?? [], true);

    /// <summary>Provides a stable eligible spread with enough displayed depth for both measured paths.</summary>
    private static ExecutionQuoteTick Quote(long at) => new("quote-" + at, at, 9.99m, 10, 100000, 100000, true);

    /// <summary>Gates each actual execution on an eligible observed print rather than a quote update.</summary>
    private static ExecutionTradeTick Trade(long at) => new("trade-" + at, at, 9.995m, 100000, true);

    /// <summary>Uses immutable unique IDs and a later known same-parent reduction, retaining the existing protocol rules.</summary>
    private static ExecutionEventOrderCommand Command(string id, string symbol, long at, bool sell, string? parent = null) =>
        new(new(ExecutionCommandKind.Submit, id, symbol, sell ? SimulatedOrderSide.Sell : SimulatedOrderSide.Buy,
            SimulatedOrderType.Market, 10, ExecutionEventTime.ToUtc(at), at - Epoch, sell, SimulatedTimeInForce.GoodTillCanceled), at, parent);

    /// <summary>Provides candidate clocks aligned with the same source sequence used by the low-level book sample.</summary>
    private static BacktestCandidateTrade Candidate(int index, string strategyId, string strategyName)
    {
        var at = Epoch + index * 10 * Tick;
        return new(SymbolNames[index % Symbols], strategyName, "long", ExecutionEventTime.ToUtc(at + Tick), 10, 9.5m, 11.25m,
            ExecutionEventTime.ToUtc(at + 3 * Tick), 0, "time_exit", .5m, StrategyId: strategyId, CandidateId: "candidate-" + index) {
            ExecutionReplay = new(ExecutionReplayPolicy.DailyRthSipPortfolioV1, ExecutionEventTime.ToUtc(Epoch - Tick),
                ExecutionEventTime.ToUtc(at + Tick), ExecutionEventTime.ToUtc(at + 3 * Tick), []) {
                EventExecutionEndUtc = ExecutionEventTime.ToUtc(at + 1_000_000_000L)
            }
        };
    }

    /// <summary>Supplies in-memory captured-shape frames to the ordinary asynchronous portfolio runner.</summary>
    private static async IAsyncEnumerable<SipPortfolioExecutionFrame> Timeline(SipPortfolioExecutionFrame[] frames)
    { foreach (var frame in frames) { yield return frame; await Task.CompletedTask; } }

    /// <summary>Counts retained receipt items separately from dictionary keys so partial-fill history growth is observable.</summary>
    private static int ReceiptCount(IReadOnlyDictionary<string, ImmutableArray<ExecutionEventFillReceipt>>? receipts) =>
        receipts?.Values.Sum(items => items.Length) ?? 0;

    /// <summary>Records the exact runtime and architecture alongside fixture and semantic hashes for reproducibility.</summary>
    private void WriteEnvironment() => output.WriteLine(JsonSerializer.Serialize(new {
        Runtime = RuntimeInformation.FrameworkDescription, Environment.Version, RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture, Environment.ProcessorCount,
        ServerGC = System.Runtime.GCSettings.IsServerGC, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        Configuration = typeof(EventExecutionProfilingTests).Assembly.GetCustomAttributes(false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>().SingleOrDefault()?.Configuration,
        MemoryMeaning = "No forced GC and no wall-time assertions; compare measurements in the same isolated test host."
    }));

    /// <summary>Sorts every object/dictionary property before hashing; complete ordered events and all book-state fields remain included.</summary>
    private static string Hash<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value);
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) WriteCanonical(writer, element);
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    /// <summary>Normalizes JSON property order only; array/event ordering and all numeric values remain unchanged.</summary>
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
}
