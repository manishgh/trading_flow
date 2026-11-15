using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Alpaca;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

[CollectionDefinition("Market stream lifecycle", DisableParallelization = true)]
public sealed class MarketStreamLifecycleCollection;

[Collection("Market stream lifecycle")]
public sealed class AlpacaMarketStateStreamLifecycleTests
{
    // Exercise the real dispatch loop while the fake socket records commands and releases each received frame.
    [Fact]
    public async Task FormingReadsPreserveTradeStatusAndSubscriptionCommandsAndRejectAStaleCanonicalCopy()
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Alpaca:KeyId"] = "test-key",
            ["Alpaca:SecretKey"] = "test-secret"
        }).Build();
        var clock = new PreviewClock();
        var leases = new LosingLeaseRepository(clock, keepLease: true);
        var socket = new DispatchStream();
        var processor = new StreamingMarketStateProcessor(NullCandleStore.Instance,
            new CandleStoreContext("test", "chart-sip-dispatch", "alpaca-sip"),
            StreamingMarketStateOptions.Default, clock, NullLogger<StreamingMarketStateProcessor>.Instance);
        using var service = new AlpacaMarketStateStreamService(new AlpacaCredentialProvider(settings), socket,
            NullLogger<AlpacaMarketStateStreamService>.Instance, leases, processor,
            new Lazy<IMarketDataProvider>(() => new EmptyMarketDataProvider()),
            new AlpacaMarketStateStreamOptions("test:chart:sip", 600, 300), clock);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var scope = Guid.NewGuid();
        await service.ReplaceScopeAsync(scope, ["AAPL"], token);
        await service.ReplaceDisplayQuotesAsync(scope, ["MSFT"], token);
        await service.StartAsync(token);
        try
        {
            await socket.ScopeReady.Task.WaitAsync(token);
            var baselineCommands = socket.Commands.ToArray();
            Assert.Equal(new[] { "market:subscribe:AAPL", "quotes:subscribe:MSFT" }, baselineCommands);
            clock.Set("2026-11-13T14:34:30Z");
            var start = DateTimeOffset.Parse("2026-11-13T14:31:00Z");
            var baseBar = new OhlcvBar("AAPL", start, "1m", 100, 102, 99, 101, 50, "sip", "all", start.AddMinutes(1));
            var canonical = new MarketBarEvent("alpaca", "sip", baseBar, MarketBarEventKind.CompletedBar,
                baseBar.KnownAtUtc!.Value, 1);
            Assert.Equal(MarketBarDisposition.Accepted, (await processor.ProcessAsync(canonical, token)).Disposition);

            await socket.DispatchAsync(
                """{"T":"t","S":"AAPL","t":"2026-11-13T14:34:20Z","i":21,"p":108,"s":15,"z":"C","x":"D","c":["@"]}""", token);
            var statusWithoutPreview = service.GetStatus("AAPL");
            Assert.Equal(SecurityTradingState.TradingObserved, statusWithoutPreview.State);
            var copy = Assert.IsType<ChartCanonicalSession>(
                await service.ReadChartCanonicalAsync("AAPL", start, clock.GetUtcNow(), clock.GetUtcNow(), token));
            var key = new ChartSeriesKey("AAPL", "1m", "regular");
            var preview = service.ReadChartForming(key, copy);
            Assert.Equal("forming", preview.Status);
            Assert.Equal(108m, preview.Open);
            Assert.Equal("not_streaming", service.ReadChartForming(key with { Symbol = "MSFT" }, null).Status);

            await socket.DispatchAsync(
                """{"T":"t","S":"AAPL","t":"2026-11-13T14:34:21Z","i":22,"p":110,"s":5,"z":"C","x":"D","c":["@"]}""", token);
            var statusWithPreview = service.GetStatus("AAPL");
            Assert.Equal(statusWithoutPreview.State, statusWithPreview.State);
            Assert.True(statusWithPreview.ProviderTimestampUtc > statusWithoutPreview.ProviderTimestampUtc);
            await socket.DispatchAsync("""{"T":"s","S":"AAPL","t":"2026-11-13T14:34:22Z","sc":"H"}""", token);
            await socket.DispatchAsync(
                """{"T":"t","S":"AAPL","t":"2026-11-13T14:34:23Z","i":23,"p":109,"s":3,"z":"C","x":"D","c":["@"]}""", token);
            Assert.Equal(SecurityTradingState.Halted, service.GetStatus("AAPL").State);
            Assert.Equal(23m, service.ReadChartForming(key, copy).EligibleVolume);
            Assert.Equal(baselineCommands, socket.Commands.ToArray());
            Assert.Equal(1, socket.Connections);
            var after = Assert.IsType<ChartCanonicalSession>(
                await service.ReadChartCanonicalAsync("AAPL", start, clock.GetUtcNow(), clock.GetUtcNow(), token));
            Assert.Equal(copy.Bars, after.Bars);

            // Set the gap in the processor before the service's asynchronous repair notification can run.
            var gap = baseBar with { Timestamp = start.AddMinutes(2), KnownAtUtc = start.AddMinutes(3) };
            await processor.ProcessAsync(canonical with { Bar = gap, ObservedAtUtc = gap.KnownAtUtc!.Value }, token);
            Assert.True(processor.RequiresRepair("AAPL"));
            Assert.False(copy.NeedsRepair);
            var suppressed = service.ReadChartForming(key, copy);
            Assert.Equal("coverage_unverified", suppressed.Status);
            Assert.Null(suppressed.Open);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(stop.Token);
        }
    }

    [Fact]
    public async Task LeaseLoss_StopsAndDisposesSocketBeforeTakeover()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alpaca:KeyId"] = "test-key",
                ["Alpaca:SecretKey"] = "test-secret"
            })
            .Build();
        var leases = new LosingLeaseRepository();
        var factory = new RecordingStreamFactory();
        var service = new AlpacaMarketStateStreamService(
            new AlpacaCredentialProvider(configuration),
            factory,
            NullLogger<AlpacaMarketStateStreamService>.Instance,
            leases,
            new StreamingMarketStateProcessor(
                NullCandleStore.Instance,
                new CandleStoreContext("test", "lifecycle", "alpaca-sip"),
                StreamingMarketStateOptions.Default,
                TimeProvider.System,
                NullLogger<StreamingMarketStateProcessor>.Instance),
            new Lazy<IMarketDataProvider>(() => new EmptyMarketDataProvider()),
            new AlpacaMarketStateStreamOptions("test:alpaca:sip", 3, 1),
            TimeProvider.System);

        await service.StartAsync(default);
        try
        {
            await factory.SecondClientCreated.Task.WaitAsync(TimeSpan.FromSeconds(6));

            Assert.False(factory.OverlapDetected);
            Assert.True(factory.Clients[0].ReadStopped);
            Assert.True(factory.Clients[0].Disposed);
            Assert.Contains(1, leases.ReleasedFences);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(stop.Token);
            service.Dispose();
        }
    }

    [Fact]
    public async Task DrainTimeout_StopsHostFailClosedWithoutCreatingAnotherSocket()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alpaca:KeyId"] = "test-key",
                ["Alpaca:SecretKey"] = "test-secret"
            })
            .Build();
        var leases = new LosingLeaseRepository();
        var store = new NonCancellingCandleStore();
        var factory = new SingleBarStreamFactory();
        var service = new AlpacaMarketStateStreamService(
            new AlpacaCredentialProvider(configuration),
            factory,
            NullLogger<AlpacaMarketStateStreamService>.Instance,
            leases,
            new StreamingMarketStateProcessor(
                store,
                new CandleStoreContext("test", "drain-timeout", "alpaca-sip"),
                StreamingMarketStateOptions.Default,
                TimeProvider.System,
                NullLogger<StreamingMarketStateProcessor>.Instance),
            new Lazy<IMarketDataProvider>(() => new EmptyMarketDataProvider()),
            new AlpacaMarketStateStreamOptions("test:alpaca:sip", 2, 1),
            TimeProvider.System);
        await service.ReplaceScopeAsync(Guid.NewGuid(), ["AAPL"], default);

        await service.StartAsync(default);
        try
        {
            await store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var runtimeCompletion = service.RuntimeCompletion;
            var completed = await Task.WhenAny(
                runtimeCompletion,
                Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(
                completed == runtimeCompletion,
                $"Service did not fail closed. stage={service.RuntimeStage}, renewals={leases.RenewCalls}, clients={factory.CreateCount}, releases={leases.ReleasedFences.Count}.");
            await runtimeCompletion;

            Assert.Equal(1, factory.CreateCount);
            Assert.Empty(leases.ReleasedFences);
        }
        finally
        {
            store.ReleaseWrite.TrySetResult();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(stop.Token);
            service.Dispose();
        }
    }

    [Fact]
    public async Task LeaseReleaseFailure_StopsHostFailClosedWithoutCreatingAnotherSocket()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alpaca:KeyId"] = "test-key",
                ["Alpaca:SecretKey"] = "test-secret"
            })
            .Build();
        var leases = new LosingLeaseRepository { FailRelease = true };
        var factory = new RecordingStreamFactory();
        var service = new AlpacaMarketStateStreamService(
            new AlpacaCredentialProvider(configuration),
            factory,
            NullLogger<AlpacaMarketStateStreamService>.Instance,
            leases,
            new StreamingMarketStateProcessor(
                NullCandleStore.Instance,
                new CandleStoreContext("test", "release-failure", "alpaca-sip"),
                StreamingMarketStateOptions.Default,
                TimeProvider.System,
                NullLogger<StreamingMarketStateProcessor>.Instance),
            new Lazy<IMarketDataProvider>(() => new EmptyMarketDataProvider()),
            new AlpacaMarketStateStreamOptions("test:alpaca:sip", 3, 1),
            TimeProvider.System);

        await service.StartAsync(default);
        try
        {
            await service.RuntimeCompletion.WaitAsync(TimeSpan.FromSeconds(7));

            Assert.Single(factory.Clients);
            Assert.True(factory.Clients[0].ReadStopped);
            Assert.True(factory.Clients[0].Disposed);
            Assert.Empty(leases.ReleasedFences);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(stop.Token);
            service.Dispose();
        }
    }

    private sealed class LosingLeaseRepository(TimeProvider? clock = null, bool keepLease = false) : IMarketStreamLeaseRepository
    {
        private readonly object gate = new();
        private int acquisitions;
        private int firstFenceRenewals;

        public List<long> ReleasedFences { get; } = [];
        public int RenewCalls { get; private set; }
        public bool FailRelease { get; set; }

        public Task<MarketStreamLease?> TryAcquireAsync(
            string resourceKey,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                acquisitions++;
                if (acquisitions > 2)
                {
                    return Task.FromResult<MarketStreamLease?>(null);
                }

                return Task.FromResult<MarketStreamLease?>(
                    Create(resourceKey, ownerId, acquisitions, leaseDuration));
            }
        }

        public Task<MarketStreamLease?> TryRenewAsync(
            string resourceKey,
            string ownerId,
            long fencingToken,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                RenewCalls++;
                if (!keepLease && fencingToken == 1 && ++firstFenceRenewals > 1)
                {
                    return Task.FromResult<MarketStreamLease?>(null);
                }

                return Task.FromResult<MarketStreamLease?>(
                    Create(resourceKey, ownerId, fencingToken, leaseDuration));
            }
        }

        public Task<bool> TryReleaseAsync(
            string resourceKey,
            string ownerId,
            long fencingToken,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (FailRelease)
                {
                    throw new InvalidOperationException("lease store unavailable");
                }

                ReleasedFences.Add(fencingToken);
                return Task.FromResult(true);
            }
        }

        // Match the test's observation clock when an isolated replay supplies one.
        private MarketStreamLease Create(
            string resourceKey,
            string ownerId,
            long fencingToken,
            TimeSpan duration)
        {
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            return new MarketStreamLease(resourceKey, ownerId, fencingToken, now, now, now.Add(duration));
        }
    }

    private sealed class PreviewClock : TimeProvider
    {
        private long ticks = DateTimeOffset.Parse("2026-11-13T14:30:10Z").UtcTicks;

        // Advance the synthetic exchange minute without changing the wall clock or triggering lease expiry.
        public void Set(string iso) => Interlocked.Exchange(ref ticks, DateTimeOffset.Parse(iso).UtcTicks);

        // Share the same UTC instant across the stream, processor and test lease.
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    }

    private sealed class DispatchStream : IAlpacaMarketStateStreamClient, IAlpacaMarketStateStreamClientFactory
    {
        private readonly Channel<(JsonElement Frame, TaskCompletionSource Handled)> incoming =
            Channel.CreateUnbounded<(JsonElement Frame, TaskCompletionSource Handled)>();
        private Task acknowledgement = Task.CompletedTask;
        public TaskCompletionSource ScopeReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Collections.Concurrent.ConcurrentQueue<string> Commands { get; } = new();
        public int Connections { get; private set; }

        // Return one fake socket to the ordinary owner.
        public IAlpacaMarketStateStreamClient Create() => this;

        // Record every connection attempt without opening a real transport.
        public Task ConnectAsync(CancellationToken token) { Connections++; return Task.CompletedTask; }

        // Send the existing full trade-channel acknowledgement through the receive loop.
        public Task SubscribeMarketStateAsync(IReadOnlyCollection<string> tickers, CancellationToken token)
        {
            Commands.Enqueue("market:subscribe:" + String.Join(",", tickers));
            acknowledgement = DispatchAsync(
                """{"T":"subscription","trades":["AAPL"],"corrections":["AAPL"],"cancelErrors":["AAPL"]}""", token);
            return Task.CompletedTask;
        }

        // Surface any unexpected market-scope removal in the command assertions.
        public Task UnsubscribeMarketStateAsync(IReadOnlyCollection<string> tickers, CancellationToken token)
        {
            Commands.Enqueue("market:unsubscribe:" + String.Join(",", tickers));
            return Task.CompletedTask;
        }

        // Startup only reaches quote forwarding once canonical membership is published.
        public Task SubscribeQuotesAsync(IReadOnlyCollection<string> tickers, CancellationToken token)
        {
            Commands.Enqueue("quotes:subscribe:" + String.Join(",", tickers));
            ScopeReady.TrySetResult();
            return Task.CompletedTask;
        }

        // Surface any unexpected quote-scope removal in the command assertions.
        public Task UnsubscribeQuotesAsync(IReadOnlyCollection<string> tickers, CancellationToken token)
        {
            Commands.Enqueue("quotes:unsubscribe:" + String.Join(",", tickers));
            return Task.CompletedTask;
        }

        // Reuse the existing acknowledgement barrier and allow the real warm path to continue.
        public Task WaitForSubscriptionAcknowledgementAsync(IReadOnlyCollection<string> expected, CancellationToken token) =>
            acknowledgement.WaitAsync(token);

        // Confirm each frame was dispatched before the test asserts on the resulting status or preview.
        public async IAsyncEnumerable<JsonElement> ReadMessagesAsync([EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var (frame, handled) in incoming.Reader.ReadAllAsync(token))
            {
                yield return frame;
                handled.SetResult();
            }
        }

        // Write one exact wire-shaped frame and wait for the real single-reader loop.
        public async Task DispatchAsync(string json, CancellationToken token)
        {
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await incoming.Writer.WriteAsync((JsonSerializer.Deserialize<JsonElement>(json), handled), token);
            await handled.Task.WaitAsync(token);
        }

        // Retire the synthetic transport once the owner releases it.
        public void Dispose() => incoming.Writer.TryComplete();
    }

    private sealed class RecordingStreamFactory : IAlpacaMarketStateStreamClientFactory
    {
        public List<BlockingStreamClient> Clients { get; } = [];
        public TaskCompletionSource SecondClientCreated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool OverlapDetected { get; private set; }

        public IAlpacaMarketStateStreamClient Create()
        {
            if (Clients.Count > 0 && (!Clients[^1].ReadStopped || !Clients[^1].Disposed))
            {
                OverlapDetected = true;
            }

            var client = new BlockingStreamClient();
            Clients.Add(client);
            if (Clients.Count == 2)
            {
                SecondClientCreated.TrySetResult();
            }

            return client;
        }
    }

    private sealed class BlockingStreamClient : IAlpacaMarketStateStreamClient
    {
        public bool ReadStopped { get; private set; }
        public bool Disposed { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SubscribeMarketStateAsync(
            IReadOnlyCollection<string> tickers,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UnsubscribeMarketStateAsync(
            IReadOnlyCollection<string> tickers,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task WaitForSubscriptionAcknowledgementAsync(
            IReadOnlyCollection<string> expectedSymbols,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                ReadStopped = true;
            }

            yield break;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class SingleBarStreamFactory : IAlpacaMarketStateStreamClientFactory
    {
        public int CreateCount { get; private set; }

        public IAlpacaMarketStateStreamClient Create()
        {
            CreateCount++;
            return new SingleBarStreamClient();
        }
    }

    private sealed class SingleBarStreamClient : IAlpacaMarketStateStreamClient
    {
        private readonly TaskCompletionSource subscribed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SubscribeMarketStateAsync(
            IReadOnlyCollection<string> tickers,
            CancellationToken cancellationToken)
        {
            subscribed.TrySetResult();
            return Task.CompletedTask;
        }

        public Task UnsubscribeMarketStateAsync(
            IReadOnlyCollection<string> tickers,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task WaitForSubscriptionAcknowledgementAsync(
            IReadOnlyCollection<string> expectedSymbols,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await subscribed.Task.WaitAsync(cancellationToken);
            yield return JsonSerializer.Deserialize<JsonElement>(
                """{"T":"b","S":"AAPL","t":"2026-08-27T14:30:00Z","o":100,"h":101,"l":99,"c":100.5,"v":1000}""");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public void Dispose()
        {
        }
    }

    private sealed class NonCancellingCandleStore : IFencedCandleStore
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
            await ReleaseWrite.Task;
        }

        public Task<IReadOnlyList<OhlcvBar>> ReadBarsAsync(
            CandleStoreReadRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OhlcvBar>>([]);
    }

    private sealed class EmptyMarketDataProvider : IMarketDataProvider, IMarketSessionScheduleProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> intervals,
            DateTimeOffset start,
            DateTimeOffset end,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDateInclusive,
            DateOnly endDateInclusive,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var schedules = new Dictionary<DateOnly, MarketSessionSchedule>();
            for (var date = startDateInclusive; date <= endDateInclusive; date = date.AddDays(1))
            {
                schedules[date] = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                    ? MarketSessionSchedule.Closed(date)
                    : MarketSessionSchedule.RegularDay(date);
            }

            return Task.FromResult<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>(schedules);
        }
    }
}
