using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Alpaca;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Tests;

[CollectionDefinition("Market stream lifecycle", DisableParallelization = true)]
public sealed class MarketStreamLifecycleCollection;

[Collection("Market stream lifecycle")]
public sealed class AlpacaMarketStateStreamLifecycleTests
{
    // The coordinator expires or stops its own REST acquisition, then removes only that market subscription.
    [Theory]
    [InlineData("stop")]
    [InlineData("takeover")]
    [InlineData("expire")]
    public async Task ChartAcceptanceStopsExclusiveWarmAndKeepsExistingScope(string ending)
    {
        var clock = new PreviewClock();
        var socket = new DispatchStream(clock);
        var provider = new BlockingDiagnosticProvider();
        using var service = CreateDiagnosticService(clock, socket, provider);
        using var lifetime = new DiagnosticHostLifetime();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var coordinator = new ChartAcceptanceCoordinator(new(true, true), new AllowDiagnosticStart(clock), service,
            service, new ChartAcceptanceSessionTests.MemoryArchive(), clock, lifetime, NullLogger<ChartAcceptanceCoordinator>.Instance);
        var otherScope = Guid.NewGuid();
        await service.ReplaceScopeAsync(otherScope, ["MSFT"], token);
        await service.ReplaceDisplayQuotesAsync(otherScope, ["NVDA"], token);
        await service.StartAsync(token);
        try
        {
            await WaitForInitialSubscriptionsAsync(service, socket, token);
            var start = Assert.IsType<ChartAcceptanceStart>((await coordinator.StartAsync("admin", token)).Start);
            Assert.Equal(clock.GetUtcNow().AddMinutes(5), start.DeadlineUtc);
            Assert.Null((await coordinator.StartAsync("admin", token)).Start);
            await provider.DiagnosticStarted.Task.WaitAsync(token);
            Assert.Equal("idle", coordinator.Stop("other-admin").State);
            Assert.Equal("observing", coordinator.GetStatus("admin").State);

            if (ending == "takeover")
            {
                await service.ReplaceScopeAsync(otherScope, ["AAPL", "MSFT"], token);
                await provider.NormalWarmStarted.Task.WaitAsync(token);
                Assert.DoesNotContain("market:unsubscribe:AAPL", socket.Commands);
                Assert.Equal(new[] { "AAPL", "MSFT" }, await service.GetDesiredSymbolsAsync(token));
            }
            else
            {
                if (ending == "expire")
                    clock.Set(start.DeadlineUtc.ToString("O"));
                else
                    Assert.Contains(coordinator.Stop("admin").State, new[] { "finalizing", "incomplete" });
                await socket.AaplRemovalSent.Task.WaitAsync(token);
                Assert.Equal(new[] { "MSFT" }, await service.GetDesiredSymbolsAsync(token));
            }
            await provider.DiagnosticCancelled.Task.WaitAsync(token);
            await coordinator.StopAsync(token);
            var result = coordinator.GetStatus("admin");
            Assert.Equal("incomplete", result.State);
            Assert.True(result.ScopeRemovalRequested);
            Assert.Equal(ending == "takeover" ? (bool?)null : true, result.RemovalAcknowledged);
            Assert.NotNull(result.ResultPath);
            Assert.Equal(1, socket.Connections);
            Assert.Null((await coordinator.StartAsync("admin", token)).Start);
        }
        finally { await service.StopAsync(token); }
    }

    // Failing the optional AAPL acknowledgement never tears down the ordinary MSFT market-state connection.
    [Fact]
    public async Task ChartAcceptanceAcknowledgementFailureDoesNotRecycleSharedSocket()
    {
        var clock = new PreviewClock();
        var socket = new DispatchStream(clock) { RejectDiagnosticSubscribe = true };
        using var service = CreateDiagnosticService(clock, socket, new EmptyMarketDataProvider());
        using var lifetime = new DiagnosticHostLifetime();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var coordinator = new ChartAcceptanceCoordinator(new(true, true), new AllowDiagnosticStart(clock), service,
            service, new ChartAcceptanceSessionTests.MemoryArchive(), clock, lifetime, NullLogger<ChartAcceptanceCoordinator>.Instance);
        var normalScope = Guid.NewGuid();
        await service.ReplaceScopeAsync(normalScope, ["MSFT"], token);
        await service.ReplaceDisplayQuotesAsync(normalScope, ["NVDA"], token);
        await service.StartAsync(token);
        try
        {
            await WaitForInitialSubscriptionsAsync(service, socket, token);
            Assert.NotNull((await coordinator.StartAsync("admin", token)).Start);
            await socket.AaplRemovalSent.Task.WaitAsync(token);
            await coordinator.StopAsync(token);
            Assert.Equal("incomplete", coordinator.GetStatus("admin").State);
            Assert.Equal(true, coordinator.GetStatus("admin").RemovalAcknowledged);
            Assert.Equal(new[] { "MSFT" }, await service.GetDesiredSymbolsAsync(token));
            Assert.Equal(1, socket.Connections);
            Assert.False(service.RuntimeCompletion.IsCompleted);
            Assert.Contains("market:subscribe:MSFT", socket.Commands);
            Assert.DoesNotContain("market:unsubscribe:MSFT", socket.Commands);
        }
        finally { await service.StopAsync(token); }
    }

    // Display quotes start on the leased socket while execution history is still warming, and only quotes under an
    // acknowledged subscription on this connection, with a plausible provider clock, reach the display path.
    [Fact]
    public async Task DisplayQuotesStartBeforeExecutionWarmingAndRequireProviderAcknowledgement()
    {
        var clock = new PreviewClock();
        var socket = new DispatchStream(clock) { QuoteReply = true };
        var provider = new GatedWarmProvider();
        using var service = CreateDiagnosticService(clock, socket, provider);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var scope = Guid.NewGuid();
        await service.ReplaceScopeAsync(scope, ["MSFT"], token);
        await service.ReplaceDisplayQuotesAsync(scope, ["NVDA"], token);
        await service.StartAsync(token);
        try
        {
            await provider.WarmStarted.Task.WaitAsync(token);
            await socket.ScopeReady.Task.WaitAsync(token);
            await WaitUntilAsync(() => service.ReadDisplayQuote("NVDA").State == DisplayQuoteState.Acknowledged, token);
            Assert.False(service.InitialSubscriptionsPublished);
            Assert.Null(service.ReadDisplayQuote("NVDA").Entry);

            await socket.DispatchAsync("""{"T":"q","S":"NVDA","bp":180.10,"bs":2,"ap":180.20,"as":3,"t":"2026-11-13T14:30:09Z"}""", token);
            Assert.Equal(180.15m, service.ReadDisplayQuote("NVDA").Entry!.Quote.MidPrice);
            await socket.DispatchAsync("""{"T":"q","S":"NVDA","bp":1,"bs":1,"ap":2,"as":1,"t":"2026-11-13T14:31:00Z"}""", token);
            Assert.Equal(180.15m, service.ReadDisplayQuote("NVDA").Entry!.Quote.MidPrice);
            await socket.DispatchAsync("""{"T":"q","S":"AMD","bp":1,"bs":1,"ap":2,"as":1,"t":"2026-11-13T14:30:09Z"}""", token);
            Assert.Equal(DisplayQuoteState.NotSubscribed, service.ReadDisplayQuote("AMD").State);

            provider.Release();
            await WaitUntilAsync(() => service.InitialSubscriptionsPublished, token);
        }
        finally { await service.StopAsync(token); }
        Assert.Equal(DisplayQuoteState.UpstreamUnavailable, service.ReadDisplayQuote("NVDA").State);
    }

    // A display subscription Alpaca never confirms is sent twice, then shown as unconfirmed; the shared socket stays.
    [Fact]
    public async Task UnacknowledgedDisplayQuotesAreReissuedOnceAndNeverReplaceTheSocket()
    {
        var clock = new PreviewClock();
        var socket = new DispatchStream(clock) { QuoteReply = false };
        using var service = CreateDiagnosticService(clock, socket, new EmptyMarketDataProvider());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var scope = Guid.NewGuid();
        await service.ReplaceScopeAsync(scope, ["MSFT"], token);
        await service.ReplaceDisplayQuotesAsync(scope, ["NVDA"], token);
        await service.StartAsync(token);
        try
        {
            await WaitForInitialSubscriptionsAsync(service, socket, token);
            await WaitUntilAsync(() => service.ReadDisplayQuote("NVDA").State == DisplayQuoteState.Unacknowledged, token);
            Assert.Equal(2, socket.Commands.Count(command => command == "quotes:subscribe:NVDA"));
            await socket.DispatchAsync("""{"T":"q","S":"NVDA","bp":180.10,"bs":2,"ap":180.20,"as":3,"t":"2026-11-13T14:30:09Z"}""", token);
            Assert.Null(service.ReadDisplayQuote("NVDA").Entry);
            Assert.Equal(1, socket.Connections);
            Assert.False(service.RuntimeCompletion.IsCompleted);
            Assert.DoesNotContain("market:unsubscribe:MSFT", socket.Commands);
        }
        finally { await service.StopAsync(token); }
    }

    // New Desk symbols may not grow the display union past its bound; existing owners keep theirs and shared symbols are free.
    [Fact]
    public async Task BoundedDeskDisplayQuotesShareTheUnionAndNeverEvictExistingOwners()
    {
        var clock = new PreviewClock();
        using var service = CreateDiagnosticService(clock, new DispatchStream(clock), new EmptyMarketDataProvider());
        var token = CancellationToken.None;
        await service.ReplaceDisplayQuotesAsync(Guid.NewGuid(), ["AAPL", "MSFT", "NVDA"], token);

        var desk = await service.ReplaceBoundedDisplayQuotesAsync(Guid.NewGuid(), ["MSFT", "TSLA", "AMD"], 4, token);
        Assert.Equal(new[] { "AMD", "MSFT" }, desk.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "AAPL", "AMD", "MSFT", "NVDA" }, (await service.GetDesiredDisplayQuotesAsync(token)).Order(StringComparer.Ordinal));

        var overLimit = await service.ReplaceBoundedDisplayQuotesAsync(Guid.NewGuid(), ["NVDA", "QQQ"], 2, token);
        Assert.Equal(new[] { "NVDA" }, overLimit);
        Assert.Contains("AAPL", await service.GetDesiredDisplayQuotesAsync(token));
    }

    // Startup subscribes display quotes before execution warming completes, so wait for both explicitly.
    private static async Task WaitForInitialSubscriptionsAsync(AlpacaMarketStateStreamService service, DispatchStream socket,
        CancellationToken token)
    {
        await socket.ScopeReady.Task.WaitAsync(token);
        await WaitUntilAsync(() => service.InitialSubscriptionsPublished, token);
    }

    // Poll a cheap in-memory condition until it holds or the test deadline cancels.
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }

    // Holds execution warming open until released, so a test can observe what happens meanwhile.
    private sealed class GatedWarmProvider : EmptyMarketDataProvider
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WarmStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Let warming finish.
        public void Release() => release.TrySetResult();

        // Block every history read until released or cancelled.
        public override async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> intervals, DateTimeOffset start, DateTimeOffset end,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            WarmStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            yield break;
        }
    }

    // Compose the genuine stream and in-memory processor with a synthetic transport and REST source.
    private static AlpacaMarketStateStreamService CreateDiagnosticService(PreviewClock clock, DispatchStream socket,
        IMarketDataProvider provider)
    {
        var credentials = new AlpacaCredentialProvider(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Alpaca:KeyId"] = "test", ["Alpaca:SecretKey"] = "test" }).Build());
        var processor = new StreamingMarketStateProcessor(NullCandleStore.Instance,
            new CandleStoreContext("test", "acceptance", "alpaca-sip"), StreamingMarketStateOptions.Default, clock,
            NullLogger<StreamingMarketStateProcessor>.Instance);
        return new(credentials, socket, NullLogger<AlpacaMarketStateStreamService>.Instance,
            new LosingLeaseRepository(clock, keepLease: true), processor, new(() => provider),
            new("test:chart:acceptance", 600, 300), clock, chartAcceptanceOptions: new(true, true));
    }

    private sealed class AllowDiagnosticStart(TimeProvider clock) : IChartAcceptanceAdmission
    {
        // Use an already-verified synthetic close for stream and lifecycle fixtures.
        public Task<(DateTimeOffset? RegularCloseUtc, string? Denial)> VerifyAsync(CancellationToken token) =>
            Task.FromResult<(DateTimeOffset?, string?)>((clock.GetUtcNow().AddHours(1), null));
    }

    private sealed class DiagnosticHostLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => default;
        // Drive the same stopping registration as an application host.
        public void StopApplication() => stopping.Cancel();
        // Dispose only the synthetic lifetime.
        public void Dispose() => stopping.Dispose();
    }

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
            await WaitForInitialSubscriptionsAsync(service, socket, token);
            var baselineCommands = socket.Commands.ToArray();
            Assert.Equal(new[] { "market:subscribe:AAPL", "quotes:subscribe:MSFT" }, baselineCommands.Order(StringComparer.Ordinal));
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
        private ManualExpiryTimer? diagnosticExpiry;

        // Advance the synthetic exchange minute without changing the wall clock or triggering lease expiry.
        public void Set(string iso)
        {
            var now = DateTimeOffset.Parse(iso);
            Interlocked.Exchange(ref ticks, now.UtcTicks);
            Volatile.Read(ref diagnosticExpiry)?.Advance(now);
        }

        // Share the same UTC instant across the stream, processor and test lease.
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        // Run the acceptance one-shot timer on synthetic UTC while preserving the ordinary lease timer.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != ChartAcceptanceSession.Lifetime || period != Timeout.InfiniteTimeSpan)
                return base.CreateTimer(callback, state, dueTime, period);
            var expiry = new ManualExpiryTimer(callback, state, GetUtcNow() + dueTime);
            Volatile.Write(ref diagnosticExpiry, expiry);
            return expiry;
        }

        private sealed class ManualExpiryTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            private int completed;

            // Fire the fixed deadline once even when a test advances past it twice.
            public void Advance(DateTimeOffset now)
            {
                if (now >= due && Interlocked.Exchange(ref completed, 1) == 0) callback(state);
            }

            // The coordinator creates a fixed one-shot and never reschedules it.
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            // Retire the synthetic deadline on a normal stop.
            public void Dispose() => Interlocked.Exchange(ref completed, 1);

            // Support both timer disposal forms.
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class DispatchStream(TimeProvider? receiptClock = null) : IAlpacaMarketStateStreamClient, IAlpacaMarketStateStreamClientFactory
    {
        private readonly Channel<(JsonElement Frame, TaskCompletionSource Handled)> incoming =
            Channel.CreateUnbounded<(JsonElement Frame, TaskCompletionSource Handled)>();
        private Task acknowledgement = Task.CompletedTask;
        private readonly HashSet<string> subscriptions = new(StringComparer.Ordinal);
        private IAlpacaMarketDataReceiptObserver? observer;
        public bool RejectDiagnosticSubscribe { get; init; }
        // What the synthetic provider reports for display quote subscriptions; null means it cannot report replies.
        public bool? QuoteReply { get; init; }
        public long CurrentArrivalOrdinal { get; private set; }
        public DateTimeOffset? CurrentBatchReceivedAtUtc { get; private set; }
        public TaskCompletionSource AaplRemovalSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            subscriptions.UnionWith(tickers);
            acknowledgement = SendAcknowledgementAsync(token);
            return Task.CompletedTask;
        }

        // Surface any unexpected market-scope removal in the command assertions.
        public Task UnsubscribeMarketStateAsync(IReadOnlyCollection<string> tickers, CancellationToken token)
        {
            Commands.Enqueue("market:unsubscribe:" + String.Join(",", tickers));
            subscriptions.ExceptWith(tickers);
            acknowledgement = SendAcknowledgementAsync(token);
            if (tickers.Contains("AAPL")) AaplRemovalSent.TrySetResult();
            return Task.CompletedTask;
        }

        // Use the same complete provider channel shape for subscription and removal acknowledgements.
        private Task SendAcknowledgementAsync(CancellationToken token) => DispatchAsync(JsonSerializer.Serialize(new
        {
            T = "subscription", trades = subscriptions, corrections = subscriptions, cancelErrors = subscriptions,
            bars = subscriptions, updatedBars = subscriptions, statuses = subscriptions
        }), token);

        // The synthetic transport can expose its exact test payloads before typed processing.
        public bool TryInstallReceiptObserver(IAlpacaMarketDataReceiptObserver value)
        {
            observer = value;
            return true;
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

        // Report the configured provider outcome for a display quote change immediately.
        public Task<bool?> WaitForQuoteSubscriptionAsync(long afterArrivalOrdinal, IReadOnlyCollection<string> subscribed,
            IReadOnlyCollection<string> removed, TimeSpan timeout, CancellationToken token) => Task.FromResult(QuoteReply);

        // Reuse the existing acknowledgement barrier and allow the real warm path to continue.
        public Task WaitForSubscriptionAcknowledgementAsync(IReadOnlyCollection<string> expected, CancellationToken token) =>
            acknowledgement.WaitAsync(token);

        // Reject just the optional subscription when requested, leaving the reader and normal symbols healthy.
        public async Task<AlpacaDiagnosticSubscriptionAcknowledgement> WaitForDiagnosticSubscriptionAcknowledgementAsync(
            IReadOnlyCollection<string> expected, string diagnostic, long afterArrivalOrdinal, CancellationToken token)
        {
            await acknowledgement.WaitAsync(token);
            if (RejectDiagnosticSubscribe && expected.Contains(diagnostic))
                throw new InvalidOperationException("Synthetic diagnostic acknowledgement rejection.");
            Assert.True(CurrentArrivalOrdinal > afterArrivalOrdinal);
            return new(CurrentArrivalOrdinal, CurrentBatchReceivedAtUtc!.Value, true);
        }

        // Confirm each frame was dispatched before the test asserts on the resulting status or preview.
        public async IAsyncEnumerable<JsonElement> ReadMessagesAsync([EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var (frame, handled) in incoming.Reader.ReadAllAsync(token))
            {
                CurrentArrivalOrdinal++;
                CurrentBatchReceivedAtUtc = receiptClock?.GetUtcNow();
                if (observer is { IsCapturing: true } && CurrentBatchReceivedAtUtc is { } received)
                    observer.Observe(frame, Encoding.UTF8.GetBytes(frame.GetRawText()), received, CurrentArrivalOrdinal);
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

    private class EmptyMarketDataProvider : IMarketDataProvider, IMarketSessionScheduleProvider
    {
        public virtual async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
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

    private sealed class BlockingDiagnosticProvider : EmptyMarketDataProvider
    {
        private int aaplCalls;
        public TaskCompletionSource DiagnosticStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DiagnosticCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NormalWarmStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The exclusive acquisition blocks until cancellation; a normal takeover must receive a fresh request.
        public override async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> intervals, DateTimeOffset start, DateTimeOffset end,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!tickers.Contains("AAPL")) yield break;
            Assert.Equal(45, (end - start).TotalDays);
            Assert.Equal(new[] { "1m" }, intervals);
            if (Interlocked.Increment(ref aaplCalls) == 1)
            {
                DiagnosticStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                finally { DiagnosticCancelled.TrySetResult(); }
            }
            else
            {
                Assert.True(DiagnosticCancelled.Task.IsCompleted);
                NormalWarmStarted.TrySetResult();
            }
        }
    }
}
