using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

[CollectionDefinition("Market stream lifecycle", DisableParallelization = true)]
public sealed class MarketStreamLifecycleCollection;

[Collection("Market stream lifecycle")]
public sealed class AlpacaMarketStateStreamLifecycleTests
{
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

    private sealed class LosingLeaseRepository : IMarketStreamLeaseRepository
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
                if (fencingToken == 1 && ++firstFenceRenewals > 1)
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

        private static MarketStreamLease Create(
            string resourceKey,
            string ownerId,
            long fencingToken,
            TimeSpan duration)
        {
            var now = DateTimeOffset.UtcNow;
            return new MarketStreamLease(resourceKey, ownerId, fencingToken, now, now, now.Add(duration));
        }
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
