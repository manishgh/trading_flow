using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Audit;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Locking;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public class LiveRunnerIntegrationTests
{
    private static readonly Guid TestExecutionRunId =
        Guid.Parse("10000000-0000-0000-0000-000000000001");

    [Theory]
    [InlineData("accepted")]
    [InlineData("accepted_for_bidding")]
    [InlineData("pending_cancel")]
    [InlineData("pending_replace")]
    public async Task RunAsync_PendingShortEntryRemainsInUniverseAfterDiscoveryDropsSymbol(string status)
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var requestedTickers = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var provider = CreateRecordingProvider(requestedTickers);
            var pendingShort = new ActiveBrokerOrder(
                "broker-short-1",
                "RGTI",
                "sell",
                status,
                "limit",
                10m,
                null,
                100m,
                DateTimeOffset.UtcNow.AddMinutes(-31),
                "TEST-S-RGTI-20260721-001-12345678",
                0m,
                null,
                DateTimeOffset.UtcNow);
            var broker = new Mock<IBrokerClient>();
            broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([pendingShort]);
            broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var runner = CreateRunner(
                provider.Object,
                lockService: null,
                brokerClient: broker.Object,
                discoverySession: new ScriptedLiveDiscoverySession([[]]),
                iterationInterval: TimeSpan.FromMilliseconds(10));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var progress = CancelAfterIterations(cts, 1);

            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["RGTI"], ["5m"], workerCount: 1, derivedSource: "5m"),
                [CreateStrategy(primaryTimeframe: "1d", executionTimeframe: "5m")],
                cts,
                progress);

            Assert.Contains("RGTI", requestedTickers);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    [Fact]
    public async Task RunAsync_PartialBrokerSnapshotBlocksAllOrderDecisions()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var provider = CreateRecordingProvider(new System.Collections.Concurrent.ConcurrentQueue<string>());
            var pendingOrder = new ActiveBrokerOrder(
                "broker-entry-1",
                "AAPL",
                "buy",
                "accepted",
                "limit",
                100m,
                null,
                10m,
                DateTimeOffset.UtcNow.AddMinutes(-31),
                "TEST-B-AAPL-20260721-001-12345678",
                0m,
                null,
                DateTimeOffset.UtcNow);
            var broker = new Mock<IBrokerClient>();
            broker.SetupSequence(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([])
                .ReturnsAsync([pendingOrder]);
            broker.SetupSequence(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([])
                .ThrowsAsync(new InvalidOperationException("positions unavailable"));
            var audit = new Mock<IDecisionAuditRepository>();
            audit.Setup(repository => repository.SaveAuditAsync(
                    It.IsAny<DecisionAuditRecord>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var runner = CreateRunner(
                provider.Object,
                lockService: null,
                auditRepoMock: audit,
                brokerClient: broker.Object,
                discoverySession: new ScriptedLiveDiscoverySession([["AAPL"]]),
                iterationInterval: TimeSpan.FromMilliseconds(10));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var run = CreateRunConfig(
                resultsRoot,
                ["AAPL"],
                ["5m"],
                workerCount: 1,
                derivedSource: "5m");
            run = run with
            {
                Execution = run.Execution! with { OrderExpiration = "active_cancel" }
            };
            await RunUntilCancelledAsync(
                runner,
                run,
                [CreateStrategy(primaryTimeframe: "1d", executionTimeframe: "5m")],
                cts,
                CancelAfterIterations(cts, 1));

            audit.Verify(repository => repository.SaveAuditAsync(
                    It.Is<DecisionAuditRecord>(record =>
                        record.Ticker == "AAPL" &&
                        record.Decision == "Skipped" &&
                        record.RejectionReason == "broker_state_unavailable_for_order_decisions"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
            broker.Verify(
                client => client.CancelOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    [Fact]
    public async Task RunAsync_BrokerPollFailureRetainsLastConfirmedPositionExposure()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var requestedTickers = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var provider = CreateRecordingProvider(requestedTickers);
            var position = new BrokerPosition("AAPL", "long", 10m, 100m, 101m, 10m);
            var broker = new Mock<IBrokerClient>();
            broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            broker.SetupSequence(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([position])
                .ReturnsAsync([position])
                .ThrowsAsync(new InvalidOperationException("broker state unavailable"));
            var runner = CreateRunner(
                provider.Object,
                lockService: null,
                brokerClient: broker.Object,
                discoverySession: new ScriptedLiveDiscoverySession([[], []]),
                iterationInterval: TimeSpan.FromMilliseconds(10));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var progress = CancelAfterIterations(cts, 2);

            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["AAPL"], ["5m"], workerCount: 1, derivedSource: "5m"),
                [CreateStrategy(primaryTimeframe: "1d", executionTimeframe: "5m")],
                cts,
                progress);

            Assert.Equal(2, requestedTickers.Count(ticker => ticker == "AAPL"));
            broker.Verify(
                client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(3));
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    [Fact]
    public async Task RunAsync_AppliesDiscoveryAddAndDropWithoutRestartingRunner()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var requestedTickers = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var provider = new Mock<IMarketDataProvider>();
            provider
                .Setup(x => x.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                {
                    var ticker = Assert.Single(tickers);
                    requestedTickers.Enqueue(ticker);
                    return YieldBars(CreateFiveMinuteBars(ticker), token);
                });
            var start = new DateTimeOffset(2026, 8, 26, 19, 31, 0, TimeSpan.Zero);
            var clock = new MutableTimeProvider(start);
            var discovery = new ScriptedLiveDiscoverySession([["AAPL"], ["MSFT"], ["MSFT"]], clock);
            var runner = CreateRunner(
                provider.Object,
                lockService: null,
                discoverySession: discovery,
                timeProvider: clock,
                iterationInterval: TimeSpan.FromMilliseconds(10));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var completedIterations = 0;
            var progress = new InlineProgress(message =>
            {
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal))
                {
                    clock.Set(clock.GetUtcNow().AddMinutes(1));
                    if (Interlocked.Increment(ref completedIterations) >= 3)
                    {
                        cts.Cancel();
                    }
                }
            });

            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["AAPL"], ["5m"], workerCount: 1, derivedSource: "5m"),
                [CreateStrategy(primaryTimeframe: "1d", executionTimeframe: "5m")],
                cts,
                progress);

            Assert.True(discovery.RefreshCount >= 3);
            var requests = requestedTickers.ToArray();
            Assert.Equal(1, requests.Count(ticker => ticker == "AAPL"));
            Assert.Equal(2, requests.Count(ticker => ticker == "MSFT"));
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    [Fact]
    public async Task RunAsync_OpenEntryOrder_PublishesTypedBrokerSnapshotToLifecycle()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var provider = new Mock<IMarketDataProvider>();
            provider
                .Setup(x => x.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                    YieldBars(CreateFiveMinuteBars(tickers.Single()), token));
            var updatedAt = new DateTimeOffset(2026, 7, 21, 14, 36, 0, TimeSpan.Zero);
            var brokerOrder = new ActiveBrokerOrder(
                "broker-1",
                "AAPL",
                "buy",
                "accepted",
                "limit",
                100m,
                null,
                10m,
                updatedAt.AddMinutes(-1),
                "SWGA-B-AAPL-20260721-001-12345678",
                0m,
                null,
                updatedAt);
            var broker = new Mock<IBrokerClient>();
            broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([brokerOrder]);
            broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var lifecycle = new Mock<IOrderLifecycleService>();
            lifecycle
                .Setup(service => service.ApplyBrokerUpdateAsync(
                    It.IsAny<OrderUpdate>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OrderStateSnapshot(
                    Guid.NewGuid(),
                    brokerOrder.ClientOrderId,
                    brokerOrder.OrderId,
                    OrderState.Acked,
                    DateTimeOffset.UtcNow,
                    updatedAt,
                    null,
                    null,
                    1));
            var runner = CreateRunner(
                provider.Object,
                lockService: null,
                brokerClient: broker.Object,
                orderLifecycleService: lifecycle.Object);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var progress = new Progress<string>(message =>
            {
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal))
                {
                    cts.Cancel();
                }
            });

            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["AAPL"], ["5m"], workerCount: 1, derivedSource: "5m"),
                [CreateStrategy(primaryTimeframe: "1d", executionTimeframe: "5m")],
                cts,
                progress);

            lifecycle.Verify(service => service.ApplyBrokerUpdateAsync(
                It.Is<OrderUpdate>(update =>
                    update.OrderId == brokerOrder.OrderId &&
                    update.ClientOrderId == brokerOrder.ClientOrderId &&
                    update.Status == OrderStatus.Accepted &&
                    update.Timestamp == updatedAt),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    [Fact]
    public async Task RunAsync_DerivesMissingDailyPrimaryTimeframe_FromConfiguredBaseInterval()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var provider = new Mock<IMarketDataProvider>();
            IReadOnlyCollection<string>? requestedIntervals = null;
            provider
                .Setup(x => x.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> intervals, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                {
                    requestedIntervals = intervals.ToArray();
                    return YieldBars(CreateFiveMinuteBars(tickers.Single()), token);
                });

            var runner = CreateRunner(provider.Object, lockService: null);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var progress = new Progress<string>(message =>
            {
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal))
                {
                    cts.Cancel();
                }
            });

            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["AAPL"], ["5m"], workerCount: 2, derivedSource: "5m"),
                [CreateStrategy(primaryTimeframe: "1d", executionTimeframe: "5m")],
                cts,
                progress);

            Assert.NotNull(requestedIntervals);
            Assert.Equal(["5m"], requestedIntervals);
            Assert.True(
                File.Exists(Path.Combine(resultsRoot, "live", "LiveRunnerTest", "AAPL", "AAPL_chart.json")),
                "The daily swing chart should be generated from completed 5m bars.");

            var chartJson = await File.ReadAllTextAsync(Path.Combine(resultsRoot, "live", "LiveRunnerTest", "AAPL", "AAPL_chart.json"));
            using var chart = System.Text.Json.JsonDocument.Parse(chartJson);
            Assert.Equal("1d", chart.RootElement.GetProperty("Timeframe").GetString());
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    // An empty provider response skips the ticker while other market state is still usable.
    [Fact]
    public async Task RunAsync_ContinuesOtherTickerPipelines_WhenOneTickerHasNoSourceBars()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var provider = new Mock<IMarketDataProvider>();
            provider
                .Setup(x => x.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                {
                    var bars = tickers
                        .Where(ticker => !ticker.Equals("SIVEF", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(CreateFiveMinuteBars)
                        .ToArray();
                    return YieldBars(bars, token);
                });

            var auditRepo = new Mock<IDecisionAuditRepository>();
            auditRepo
                .Setup(x => x.SaveAuditAsync(It.IsAny<DecisionAuditRecord>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            LivePipelineHealth? health = null;
            var runner = CreateRunner(
                provider.Object,
                lockService: null,
                auditRepo,
                timeProvider: new FixedTimeProvider(
                    new DateTimeOffset(2026, 8, 26, 19, 56, 0, TimeSpan.Zero)),
                publishPipelineHealth: (updated, _) =>
                {
                    health = updated;
                    return Task.CompletedTask;
                });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var progress = new Progress<string>(message =>
            {
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal))
                {
                    cts.Cancel();
                }
            });

            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["AAPL", "SIVEF"], ["5m"], workerCount: 2, derivedSource: "5m"),
                [CreateStrategy(primaryTimeframe: "1d", executionTimeframe: "5m")],
                cts,
                progress);

            Assert.True(
                File.Exists(Path.Combine(resultsRoot, "live", "LiveRunnerTest", "AAPL", "AAPL_chart.json")),
                "AAPL should still produce live metrics even when SIVEF has no source bars.");

            Assert.Null(health);

            auditRepo.Verify(
                x => x.SaveAuditAsync(
                    It.Is<DecisionAuditRecord>(record =>
                        record.Ticker == "SIVEF" &&
                        record.Decision == "Skipped" &&
                        record.RejectionReason != null &&
                        record.RejectionReason.StartsWith(MarketDataUnavailable.BarsMissing, StringComparison.Ordinal)),
                    It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    // A repeated single-symbol exception cannot latch unrelated candidates. The symbol can return on a good read.
    [Fact]
    public async Task RunAsync_RepeatedTickerExceptionsLeaveOtherEntriesAndRecoveryAvailable()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var poll = 1;
            var provider = new Mock<IMarketDataProvider>();
            provider.Setup(source => source.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                {
                    if (poll < 5 && tickers.Contains("SIVEF"))
                    {
                        throw new InvalidOperationException("Synthetic market read failure.");
                    }

                    return YieldBars(poll <= 3 ? [] : tickers.SelectMany(CreateFiveMinuteBars).ToArray(), token);
                });

            var candidates = new MemoryCandidateRepository();
            var auditRows = new List<DecisionAuditRecord>();
            LivePipelineHealth? persisted = null;
            var runner = CreateRunner(provider.Object, lockService: null, auditRepoMock: CaptureDecisionAudits(auditRows),
                candidateRepository: candidates, iterationInterval: TimeSpan.FromMilliseconds(1),
                publishPipelineHealth: (health, _) => { persisted = health; return Task.CompletedTask; });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var progress = new InlineProgress(message =>
            {
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal) && poll++ == 5)
                {
                    cancellation.Cancel();
                }
            });
            await RunUntilCancelledAsync(
                runner, CreateRunConfig(resultsRoot, ["AAPL", "SIVEF"], ["5m"], workerCount: 1, derivedSource: "5m"),
                [CreateStrategy("1d", "5m")], cancellation, progress);

            Assert.Equal(new[] { "AAPL", "SIVEF" }, candidates.Records.Select(candidate => candidate.Symbol).Order().ToArray());
            Assert.NotNull(persisted);
            Assert.False(persisted.EntriesStopped);
            Assert.Equal(4, persisted.FailureCount);
            Assert.Equal(0, persisted.RunWideFailureCount);
            Assert.Equal(4, auditRows.Count(audit => audit.Ticker == "SIVEF" && audit.Decision == "Failed"));
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    // An incomplete broker snapshot is a shared failure. A later snapshot cannot silently clear the durable stop.
    [Fact]
    public async Task RunAsync_RepeatedBrokerSnapshotFailureLatchesRunBeforeRecovery()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var poll = 1;
            var broker = new Mock<IBrokerClient>();
            broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
                .Returns(() => poll <= 3
                    ? throw new InvalidOperationException("Synthetic broker snapshot failure.")
                    : Task.FromResult<IReadOnlyList<ActiveBrokerOrder>>([]));
            broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var provider = CreateRecordingProvider(new System.Collections.Concurrent.ConcurrentQueue<string>());
            var candidates = new MemoryCandidateRepository();
            var snapshots = new List<LivePipelineHealth>();
            var runner = CreateRunner(provider.Object, lockService: null, brokerClient: broker.Object,
                candidateRepository: candidates, iterationInterval: TimeSpan.FromMilliseconds(1),
                publishPipelineHealth: (health, _) => { snapshots.Add(health); return Task.CompletedTask; });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var progress = new InlineProgress(message =>
            {
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal) && poll++ == 4)
                {
                    cancellation.Cancel();
                }
            });
            await RunUntilCancelledAsync(
                runner, CreateRunConfig(resultsRoot, ["AAPL"], ["5m"], workerCount: 1, derivedSource: "5m"),
                [CreateStrategy("1d", "5m")], cancellation, progress);

            Assert.Equal(new[] { 1, 2, 3, 0 }, snapshots.Select(health => health.ConsecutiveFailingPolls));
            Assert.False(snapshots[1].EntriesStopped);
            Assert.True(snapshots[2].EntriesStopped);
            Assert.True(snapshots[3].EntriesStopped);
            Assert.Equal(3, snapshots[3].RunWideFailureCount);
            Assert.Empty(candidates.Records);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    // A retained failed source cannot admit a symbol unless independent healthy evidence supports it.
    [Fact]
    public async Task RunAsync_SourceFailureSurvivesRestartUntilFreshObservation_AndUsesHealthyAlternative()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var started = new DateTimeOffset(2026, 8, 26, 19, 31, 0, TimeSpan.Zero);
            var clock = new MutableTimeProvider(started);
            var restored = new LivePipelineHealth
            {
                Discovery = new DiscoveryRefreshHealth(null,
                    [new DiscoverySourceRefreshFailure("finviz", "test-screen", started.AddMinutes(-1), "Timed out")])
            };
            restored = JsonSerializer.Deserialize<LivePipelineHealth>(JsonSerializer.Serialize(restored))!;
            var scope = Guid.NewGuid();
            var poll = 1;
            var finviz = new DiscoverySourceEvidence("finviz", "test-screen", started.AddMinutes(-2), started.AddMinutes(3),
                "{}", Guid.NewGuid(), 1, null, false, null, new string('a', 64));
            var wishlist = finviz with
            {
                SourceKind = "wishlist", SourceKey = "test-list", LatestSnapshotId = Guid.NewGuid(), ObservedAtUtc = started
            };
            var discovery = new Mock<ILiveDiscoverySession>();
            discovery.Setup(session => session.RefreshAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var now = clock.GetUtcNow();
                    var recoveredFinviz = poll >= 4 ? finviz with { ObservedAtUtc = now, SourceVersion = 2 } : finviz;
                    return new DiscoveryUniverseSnapshot(scope, now,
                        [
                            new ActiveDiscoveryAggregate(Guid.NewGuid(), scope, "AAPL", "swing", started.AddMinutes(-2),
                                now, started.AddMinutes(3), poll, [recoveredFinviz, wishlist]),
                            new ActiveDiscoveryAggregate(Guid.NewGuid(), scope, "SIVEF", "swing", started.AddMinutes(-2),
                                recoveredFinviz.ObservedAtUtc, started.AddMinutes(3), poll, [recoveredFinviz])
                        ], [], [])
                    {
                        SourceRefreshOutcomes = poll >= 4
                            ? [new DiscoverySourceRefreshOutcome("finviz", "test-screen", now, now, null)]
                            : []
                    };
                });
            discovery.Setup(session => session.RetainExposureSymbolsAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var provider = new Mock<IMarketDataProvider>();
            provider.Setup(source => source.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                    YieldBars(tickers.SelectMany(CreateFiveMinuteBars), token));

            var candidates = new MemoryCandidateRepository();
            var audits = new List<DecisionAuditRecord>();
            var published = new List<LivePipelineHealth>();
            var runner = CreateRunner(provider.Object, lockService: null, auditRepoMock: CaptureDecisionAudits(audits),
                discoverySession: discovery.Object, timeProvider: clock, candidateRepository: candidates,
                iterationInterval: TimeSpan.FromMilliseconds(1), initialPipelineHealth: restored,
                publishPipelineHealth: (health, _) => { published.Add(health); return Task.CompletedTask; });
            // Finish on the recovery poll. The wall clock only bounds a genuinely stalled test.
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var recoveryPollCompleted = false;
            var progress = new InlineProgress(message =>
            {
                if (!message.StartsWith("Iteration finished.", StringComparison.Ordinal))
                {
                    return;
                }
                if (poll == 4)
                {
                    recoveryPollCompleted = true;
                    cancellation.Cancel();
                }
                else
                {
                    Assert.Equal("AAPL", Assert.Single(candidates.Records).Symbol);
                    clock.Set(started.AddSeconds(poll++));
                }
            });
            await RunUntilCancelledAsync(
                runner, CreateRunConfig(resultsRoot, ["AAPL", "SIVEF"], ["5m"], workerCount: 1, derivedSource: "5m"),
                [CreateStrategy("1d", "5m")], cancellation, progress);

            Assert.True(recoveryPollCompleted, "The test deadline elapsed before the fourth discovery poll completed.");
            Assert.Equal("wishlist:test-list", Assert.Single(candidates.Records, candidate => candidate.Symbol == "AAPL").DiscoverySource);
            Assert.Equal("finviz:test-screen", Assert.Single(candidates.Records, candidate => candidate.Symbol == "SIVEF").DiscoverySource);
            var health = Assert.Single(published);
            Assert.Empty(health.Discovery!.FailedSources);
            Assert.False(health.EntriesStopped);
            Assert.Single(audits, audit => audit.Ticker == "SIVEF" && audit.RejectionReason == "paper_discovery_source_refresh_failed");
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    // A refresh outage is visible and recoverable even while there are no symbols to process.
    [Fact]
    public async Task RunAsync_EmptyUniverseStillPublishesSessionRefreshFailureAndRecovery()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 8, 26, 19, 31, 0, TimeSpan.Zero);
            var session = new Mock<ILiveDiscoverySession>();
            var empty = new DiscoveryUniverseSnapshot(Guid.NewGuid(), now, [], [], []);
            session.SetupSequence(discovery => discovery.RefreshAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(empty)
                .ThrowsAsync(new InvalidOperationException("Synthetic projection outage."))
                .ReturnsAsync(empty);
            session.Setup(discovery => discovery.RetainExposureSymbolsAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var snapshots = new List<LivePipelineHealth>();
            var runner = CreateRunner(new Mock<IMarketDataProvider>().Object, lockService: null,
                discoverySession: session.Object, iterationInterval: TimeSpan.FromMilliseconds(1),
                publishPipelineHealth: (health, _) => { snapshots.Add(health); return Task.CompletedTask; });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await RunUntilCancelledAsync(
                runner, CreateRunConfig(resultsRoot, ["AAPL"], ["5m"], workerCount: 1, derivedSource: "5m"),
                [CreateStrategy("1d", "5m")], cancellation, CancelAfterIterations(cancellation, 3));

            Assert.Equal(2, snapshots.Count);
            Assert.Equal("Synthetic projection outage.", snapshots[0].Discovery?.SessionFailure?.Reason);
            Assert.Null(snapshots[1].Discovery?.SessionFailure);
            Assert.All(snapshots, health =>
            {
                Assert.False(health.EntriesStopped);
                Assert.Equal(0, health.FailureCount);
            });
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    // A long warm-up can renew membership or skip entry without creating a candidate.
    [Theory]
    [InlineData("renewed", 15, null)]
    [InlineData("removed", 15, "paper_discovery_membership_expired")]
    [InlineData("removed", 2, "paper_discovery_membership_removed_after_warmup")]
    [InlineData("refresh-failed", 15, "paper_discovery_refresh_failed_after_warmup")]
    public async Task RunAsync_RechecksSourceAfterLongWarmup(string outcome, int warmupMinutes, string? expectedSkipReason)
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var started = new DateTimeOffset(2026, 8, 26, 19, 31, 0, TimeSpan.Zero);
            var clock = new MutableTimeProvider(started);
            IReadOnlyList<string> refreshed = outcome == "removed" ? [] : ["AAPL"];
            var discovery = new ScriptedLiveDiscoverySession(
                [["AAPL"], refreshed], clock, failAfterFirst: outcome == "refresh-failed");
            var provider = new Mock<IMarketDataProvider>();
            provider.Setup(source => source.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                {
                    clock.Set(started.AddMinutes(warmupMinutes));
                    return YieldBars(CreateFiveMinuteBars(tickers.Single()), token);
                });
            var candidates = new MemoryCandidateRepository();
            var audits = new List<DecisionAuditRecord>();
            var runner = CreateRunner(provider.Object, lockService: null, auditRepoMock: CaptureDecisionAudits(audits),
                discoverySession: discovery, timeProvider: clock, candidateRepository: candidates,
                iterationInterval: TimeSpan.FromMilliseconds(1));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var refreshFailed = outcome == "refresh-failed";
            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["AAPL"], ["5m"], workerCount: 1, derivedSource: "5m"),
                [CreateStrategy("1d", "5m")],
                cancellation,
                CancelAfterIterations(cancellation, refreshFailed ? 2 : 1));

            Assert.Equal(refreshFailed ? 4 : 2, discovery.RefreshCount);
            if (outcome == "renewed")
            {
                var candidate = Assert.Single(candidates.Records);
                Assert.NotEqual(StrategyCandidateState.Expired, candidate.State);
                Assert.Equal(started.AddMinutes(warmupMinutes), candidate.RevalidatedAtUtc);
                Assert.Equal(new DateTimeOffset(2026, 8, 26, 20, 0, 0, TimeSpan.Zero), candidate.ExpiresAtUtc);
                Assert.DoesNotContain(audits, audit => audit.Decision == "Skipped");
            }
            else
            {
                Assert.Empty(candidates.Records);
                var skip = Assert.Single(audits);
                Assert.Equal("Skipped", skip.Decision);
                Assert.Equal(expectedSkipReason, skip.RejectionReason);
                Assert.Equal("LiveRunnerTest", skip.RunName);
                Assert.Equal("AAPL", skip.Ticker);
                Assert.Equal("Test Strategy", skip.StrategyName);
                Assert.Equal(started.AddMinutes(warmupMinutes), skip.Timestamp);
                using var payload = JsonDocument.Parse(skip.SignalJson);
                var evidence = payload.RootElement;
                Assert.Equal(TestExecutionRunId, evidence.GetProperty("runId").GetGuid());
                Assert.Equal("test_strat", evidence.GetProperty("strategyIdentity").GetProperty("StrategyId").GetString());
                Assert.Equal("1.0.0", evidence.GetProperty("strategyIdentity").GetProperty("SemanticVersion").GetString());
                Assert.Equal("before_entry_decision", evidence.GetProperty("stage").GetString());
                Assert.Equal(refreshFailed, evidence.GetProperty("membershipRefreshFailed").GetBoolean());
                Assert.False(evidence.GetProperty("membershipPresentAfterRefresh").GetBoolean());
                Assert.Equal(JsonValueKind.Null, evidence.GetProperty("candidateId").ValueKind);
                var source = Assert.Single(evidence.GetProperty("sourceEvidence").EnumerateArray());
                Assert.Equal(started, source.GetProperty("ObservedAtUtc").GetDateTimeOffset());
                Assert.Equal(started.AddMinutes(3), source.GetProperty("ExpiresAtUtc").GetDateTimeOffset());
            }
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    // Source expiry during context loading is audited before any candidate is evaluated.
    [Fact]
    public async Task RunAsync_RecordsSourceExpiryDuringDecisionContextLoad()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var started = new DateTimeOffset(2026, 8, 26, 19, 31, 0, TimeSpan.Zero);
            var expiredAt = started.AddMinutes(3);
            var clock = new MutableTimeProvider(started);
            var context = new Mock<IStrategyDecisionContextProvider>();
            context.Setup(source => source.ResolveAsync(
                    It.IsAny<StrategyDecisionContextRequest>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    clock.Set(expiredAt);
                    return ValueTask.FromResult<DrawdownDecisionContext?>(null);
                });
            var candidates = new MemoryCandidateRepository();
            var audits = new List<DecisionAuditRecord>();
            var provider = CreateRecordingProvider(new System.Collections.Concurrent.ConcurrentQueue<string>());
            var runner = CreateRunner(provider.Object, lockService: null, auditRepoMock: CaptureDecisionAudits(audits),
                discoverySession: new ScriptedLiveDiscoverySession([["AAPL"]], clock),
                timeProvider: clock, candidateRepository: candidates, decisionContextProvider: context.Object);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["AAPL"], ["5m"], workerCount: 1, derivedSource: "5m"),
                [CreateStrategy("1d", "5m")],
                cancellation,
                CancelAfterIterations(cancellation, 1));

            Assert.Empty(candidates.Records);
            var skip = Assert.Single(audits);
            Assert.Equal("Skipped", skip.Decision);
            Assert.Equal("paper_discovery_membership_expired", skip.RejectionReason);
            Assert.Equal(expiredAt, skip.Timestamp);
            using var payload = JsonDocument.Parse(skip.SignalJson);
            var evidence = payload.RootElement;
            Assert.Equal("before_candidate_evaluation", evidence.GetProperty("stage").GetString());
            Assert.True(evidence.GetProperty("membershipPresentAfterRefresh").GetBoolean());
            Assert.True(evidence.GetProperty("candidateExpiresAtUtc").GetDateTimeOffset() > expiredAt);
            Assert.Equal(JsonValueKind.Null, evidence.GetProperty("candidateId").ValueKind);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    // Restored health or an exit-only lease blocks candidate work despite available market evidence.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_EntryStopPreventsNewCandidateEvaluation(bool rebuilt)
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 8, 26, 19, 31, 0, TimeSpan.Zero);
            var recorder = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var provider = CreateRecordingProvider(recorder);
            var candidates = new MemoryCandidateRepository();
            var runner = CreateRunner(provider.Object, lockService: null,
                timeProvider: new FixedTimeProvider(now), candidateRepository: candidates,
                initialPipelineHealth: rebuilt ? null : new LivePipelineHealth(EntriesStoppedAtUtc: now.AddMinutes(-3)),
                exitOnly: rebuilt);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var progress = new InlineProgress(message =>
            {
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal))
                    cancellation.Cancel();
            });

            await RunUntilCancelledAsync(runner,
                CreateRunConfig(resultsRoot, ["AAPL"], ["5m"], workerCount: 1, derivedSource: "5m"),
                [CreateStrategy("1d", "5m")], cancellation, progress);
            Assert.Contains("AAPL", recorder);
            Assert.True(File.Exists(Path.Combine(resultsRoot, "live", "LiveRunnerTest", "AAPL", "AAPL_chart.json")));
            Assert.Empty(candidates.Records);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    // Freeze the actual next NYSE close across weekends, DST, a holiday and an early close.
    [Theory]
    [InlineData(10, 31, 11, 2, 16, 21)]
    [InlineData(11, 26, 11, 27, 13, 18)]
    public void PaperDecisionCalendar_UsesNextScheduledClose(
        int setupMonth, int setupDay, int sessionMonth, int sessionDay, int closeLocalHour, int closeUtcHour)
    {
        var first = new DateOnly(2026, setupMonth, setupDay);
        var session = new DateOnly(2026, sessionMonth, sessionDay);
        var schedules = Enumerable.Range(first.DayNumber, session.DayNumber - first.DayNumber)
            .Select(DateOnly.FromDayNumber)
            .ToDictionary(day => day, MarketSessionSchedule.Closed);
        schedules[session] = new MarketSessionSchedule(session, true, new TimeOnly(9, 30), new TimeOnly(closeLocalHour, 0));
        var available = new DateTimeOffset(2026, setupMonth, setupDay, 5, 0, 0, TimeSpan.Zero);
        var strategy = CreateStrategy("1d", "5m");
        var expected = new DateTimeOffset(2026, sessionMonth, sessionDay, closeUtcHour, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, PaperDecisionCalendar.ResolveExpiry(available, strategy, schedules));
        Assert.Throws<InvalidOperationException>(() => PaperDecisionCalendar.ResolveExpiry(available, strategy, null));
        schedules.Remove(first);
        Assert.Throws<InvalidOperationException>(() => PaperDecisionCalendar.ResolveExpiry(available, strategy, schedules));
    }

    [Fact]
    public async Task RunAsync_AuditsRejectedSignal_WhenConfluenceGateFails()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var provider = new Mock<IMarketDataProvider>();
            provider
                .Setup(x => x.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                    YieldBars(CreateFallingFiveMinuteBars(tickers.Single()), token));

            var auditRepo = new Mock<IDecisionAuditRepository>();
            auditRepo
                .Setup(x => x.SaveAuditAsync(It.IsAny<DecisionAuditRecord>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var runner = CreateRunner(
                provider.Object,
                lockService: null,
                auditRepo,
                timeProvider: new FixedTimeProvider(
                    new DateTimeOffset(2026, 8, 26, 19, 56, 0, TimeSpan.Zero)));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var progress = new Progress<string>(message =>
            {
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal))
                {
                    cts.Cancel();
                }
            });

            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["AAPL"], ["5m"], workerCount: 2, derivedSource: "5m"),
                [CreateStrategy(primaryTimeframe: "1d", executionTimeframe: "5m", confluenceEnabled: true, confluenceTimeframe: "5m", confluenceEmaPeriod: 20)],
                cts,
                progress);

            auditRepo.Verify(
                x => x.SaveAuditAsync(
                    It.Is<DecisionAuditRecord>(record =>
                        record.Ticker == "AAPL" &&
                        record.Decision == "Rejected" &&
                        record.RejectionReason != null &&
                        record.RejectionReason.StartsWith("confluence_price_below_ema20", StringComparison.Ordinal)),
                    It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    [Fact]
    public async Task RunAsync_PositiveNewsWithoutTechnicalTrigger_CreatesNoOrderIntentOrBrokerRequest()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var bars = CreateFallingFiveMinuteBars("AAPL");
            var provider = new Mock<IMarketDataProvider>(MockBehavior.Strict);
            provider
                .Setup(x => x.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> _, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                    YieldBars(bars, token));

            var catalysts = new Mock<ICatalystProvider>(MockBehavior.Strict);
            catalysts
                .Setup(x => x.GetCatalystsAsync(
                    "AAPL",
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new CatalystEvent(
                        "AAPL",
                        bars[^1].Timestamp.AddMinutes(-10),
                        CatalystType.NewsReport,
                        "AAPL reports a strongly positive operating update",
                        0.90m)
                ]);

            var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
            broker.Setup(x => x.GetOpenOrdersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            broker.Setup(x => x.GetOpenPositionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var submissions = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
            var baseStrategy = CreateStrategy("1d", "5m");
            var catalystStrategy = baseStrategy with
            {
                EntryRules = baseStrategy.EntryRules with
                {
                    SetupType = "catalyst_vwap_breakout",
                    RequirePositiveNews = true,
                    MinNewsSentiment = 0.25m
                }
            };
            var runner = CreateRunner(
                provider.Object,
                lockService: null,
                brokerClient: broker.Object,
                orderSubmissionService: submissions.Object,
                catalystProvider: catalysts.Object);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var progress = new Progress<string>(message =>
            {
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal))
                {
                    cts.Cancel();
                }
            });

            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(
                    resultsRoot,
                    ["AAPL"],
                    ["5m"],
                    workerCount: 1,
                    derivedSource: "5m",
                    dryRun: false,
                    allowLiveOrders: true),
                [catalystStrategy],
                cts,
                progress);

            submissions.Verify(x => x.SubmitEntryOrderAsync(
                It.IsAny<EntryOrderSubmission>(),
                It.IsAny<IBrokerClient>(),
                It.IsAny<CancellationToken>()), Times.Never);
            broker.Verify(x => x.SubmitOrderAsync(
                It.IsAny<BrokerEntryOrder>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    [Fact]
    public async Task RunAsync_BatchedCandlePipeline_WritesMetricsAndAuditsEndToEnd()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var provider = new Mock<IMarketDataProvider>();
            provider
                .Setup(x => x.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                    YieldBars(tickers.SelectMany(CreateFallingFiveMinuteBars), token));

            var audits = new List<DecisionAuditRecord>();
            var auditRepo = new Mock<IDecisionAuditRepository>();
            auditRepo
                .Setup(x => x.SaveAuditAsync(It.IsAny<DecisionAuditRecord>(), It.IsAny<CancellationToken>()))
                .Callback<DecisionAuditRecord, CancellationToken>((record, _) => audits.Add(record))
                .Returns(Task.CompletedTask);

            var runner = CreateRunner(provider.Object, lockService: null, auditRepo);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var progress = new Progress<string>(message =>
            {
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal))
                {
                    cts.Cancel();
                }
            });

            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["AAPL", "MSFT"], ["5m"], workerCount: 2, derivedSource: "5m"),
                [CreateStrategy(primaryTimeframe: "1d", executionTimeframe: "5m", confluenceEnabled: true, confluenceTimeframe: "5m", confluenceEmaPeriod: 20)],
                cts,
                progress);

            Assert.True(File.Exists(Path.Combine(resultsRoot, "live", "LiveRunnerTest", "AAPL", "AAPL_chart.json")));
            Assert.True(File.Exists(Path.Combine(resultsRoot, "live", "LiveRunnerTest", "MSFT", "MSFT_chart.json")));
            Assert.NotEmpty(audits);
            Assert.Contains(audits, audit => audit.Decision == "Rejected");
            Assert.All(audits, audit => Assert.Contains(audit.Ticker, new[] { "AAPL", "MSFT" }));
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    [Fact]
    public async Task RunAsync_ReleasesTickerLock_AfterTickerPipelineCompletes()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var provider = new Mock<IMarketDataProvider>();
            provider
                .Setup(x => x.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                    YieldBars(CreateFiveMinuteBars(tickers.Single()), token));

            var lockService = new Mock<ITickerLockService>();
            lockService
                .Setup(x => x.TryAcquireLockAsync("AAPL", It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var runner = CreateRunner(provider.Object, lockService.Object);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var progress = new Progress<string>(message =>
            {
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal))
                {
                    cts.Cancel();
                }
            });

            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["AAPL"], ["5m"], workerCount: 2, derivedSource: "5m"),
                [CreateStrategy(primaryTimeframe: "1d", executionTimeframe: "5m")],
                cts,
                progress);

            lockService.Verify(
                x => x.ReleaseLockAsync("AAPL", It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    [Fact]
    public async Task RunAsync_UsesSingleBatchedProviderRead_BeforeTickerWorkers()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var providerCallCount = 0;
            IReadOnlyCollection<string>? requestedTickers = null;
            var provider = new Mock<IMarketDataProvider>();
            provider
                .Setup(x => x.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                {
                    Interlocked.Increment(ref providerCallCount);
                    requestedTickers = tickers.ToArray();
                    return YieldBars(tickers.SelectMany(CreateFiveMinuteBars), token);
                });

            var runner = CreateRunner(provider.Object, lockService: null);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var progress = new Progress<string>(message =>
            {
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal))
                {
                    cts.Cancel();
                }
            });

            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["AAPL", "MSFT", "NVDA"], ["5m"], workerCount: 1, derivedSource: "5m"),
                [CreateStrategy(primaryTimeframe: "1d", executionTimeframe: "5m")],
                cts,
                progress);

            Assert.Equal(1, providerCallCount);
            Assert.NotNull(requestedTickers);
            Assert.Equal(["AAPL", "MSFT", "NVDA"], requestedTickers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    // Use the execution price on success, and audit either clock if it expires during sizing.
    [Theory]
    [InlineData(null, 56, 56)]
    [InlineData("paper_discovery_membership_expired", 56, 59)]
    [InlineData("paper_setup_deadline_elapsed_before_order_submission", 58, 60)]
    public async Task RunAsync_RechecksEntryClocksBeforeSubmittingOrder(
        string? expectedSkipReason, int startedMinute, int submissionMinute)
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            const decimal executionClose = 123.45m;
            var hour = new DateTimeOffset(2026, 8, 26, 19, 0, 0, TimeSpan.Zero);
            var startedAt = hour.AddMinutes(startedMinute);
            var submissionTime = hour.AddMinutes(submissionMinute);
            var clock = new MutableTimeProvider(startedAt);
            var provider = new Mock<IMarketDataProvider>();
            provider
                .Setup(x => x.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                {
                    var ticker = tickers.Single();
                    var fiveMinuteBars = CreateFiveMinuteBars(ticker);
                    var oneMinuteBars = CreateOneMinuteBars(
                        ticker,
                        fiveMinuteBars[0].Timestamp,
                        fiveMinuteBars[^1].Timestamp.AddMinutes(5),
                        executionClose);
                    return YieldBars(fiveMinuteBars.Concat(oneMinuteBars), token);
                });

            FinalizedOrder? submittedOrder = null;
            var broker = new Mock<IBrokerClient>();
            broker
                .Setup(x => x.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            broker
                .Setup(x => x.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            broker
                .As<IBrokerAccountProvider>()
                .Setup(x => x.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    clock.Set(submissionTime);
                    return new BrokerAccountSnapshot(
                        "paper-account",
                        "ACTIVE",
                        AccountBlocked: false,
                        TradingBlocked: false,
                        TradeSuspendedByUser: false,
                        ShortingEnabled: true,
                        BuyingPower: 100_000m,
                        RegulationTBuyingPower: 100_000m,
                        Equity: 100_000m,
                        LongMarketValue: 0m,
                        ShortMarketValue: 0m,
                        RequestedAtUtc: startedAt,
                        ObservedAtUtc: submissionTime);
                });
            broker
                .Setup(x => x.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()))
                .Callback<BrokerEntryOrder, CancellationToken>((order, _) => submittedOrder = order.Order)
                .ReturnsAsync(new BrokerOrderReceipt("order-1", DateTimeOffset.UtcNow));

            var audits = new List<DecisionAuditRecord>();
            var candidates = new MemoryCandidateRepository();
            var runner = CreateRunner(
                provider.Object,
                lockService: null,
                auditRepoMock: CaptureDecisionAudits(audits),
                brokerClient: broker.Object,
                orderSubmissionService: CreatePassThroughSubmissionService(),
                discoverySession: expectedSkipReason is null ? null : new ScriptedLiveDiscoverySession([["AAPL"]], clock),
                timeProvider: clock,
                candidateRepository: candidates);
            // Finish after actual runner work. Keep a separate test deadline to diagnose a stalled iteration.
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var iterationCompleted = false;
            var progressMessages = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var progress = new InlineProgress(message =>
            {
                progressMessages.Enqueue(message);
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal))
                {
                    iterationCompleted = true;
                    cts.Cancel();
                }
            });

            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["AAPL"], ["1m", "5m"], workerCount: 2, derivedSource: "1m", dryRun: false, allowLiveOrders: true),
                [CreateStrategy(primaryTimeframe: "1d", executionTimeframe: "1m")],
                cts,
                progress);

            Assert.True(iterationCompleted, "The test deadline elapsed before the first live runner iteration completed. " +
                String.Join(Environment.NewLine, progressMessages));
            if (expectedSkipReason is null)
            {
                Assert.True(submittedOrder is not null, String.Join(Environment.NewLine, progressMessages));
                Assert.Equal(decimal.Round(executionClose * 1.0001m, 4), submittedOrder.LimitPrice);
                Assert.DoesNotContain(audits, audit => audit.Decision == "Skipped");
                return;
            }

            Assert.Null(submittedOrder);
            broker.Verify(client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()),
                Times.Never);
            Assert.Equal(new[] { "Accepted", "Skipped" }, audits.Select(audit => audit.Decision));
            var skip = audits[1];
            Assert.Equal(expectedSkipReason, skip.RejectionReason);
            Assert.Equal(submissionTime, skip.Timestamp);
            using var payload = JsonDocument.Parse(skip.SignalJson);
            var evidence = payload.RootElement;
            Assert.Equal("before_order_submission", evidence.GetProperty("stage").GetString());
            Assert.Equal(Assert.Single(candidates.Records).CandidateId, evidence.GetProperty("candidateId").GetGuid());
            Assert.Equal(hour.AddHours(1), evidence.GetProperty("candidateExpiresAtUtc").GetDateTimeOffset());
            var source = Assert.Single(evidence.GetProperty("sourceEvidence").EnumerateArray());
            Assert.Equal(startedAt.AddMinutes(3), source.GetProperty("ExpiresAtUtc").GetDateTimeOffset());
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    // Exit-only recovery cannot cancel an opening order for another run through the polling expiry rule.
    [Fact]
    public async Task RunAsync_ExitOnlySkipsAccountWideEntryExpiry()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var broker = new Mock<IBrokerClient>();
            broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
                [new ActiveBrokerOrder("old-opening-order", "AAPL", "buy", "accepted", "limit", 10m,
                    null, 100m, DateTimeOffset.UtcNow.AddHours(-2), "foreign-opening-order", 0m, null, DateTimeOffset.UtcNow)]);
            broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var submission = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
            var provider = CreateRecordingProvider(new System.Collections.Concurrent.ConcurrentQueue<string>());
            var runner = CreateRunner(provider.Object, lockService: null, brokerClient: broker.Object,
                orderSubmissionService: submission.Object, iterationInterval: TimeSpan.FromMilliseconds(1), exitOnly: true);
            var run = CreateRunConfig(resultsRoot, ["AAPL"], ["5m"], workerCount: 1, derivedSource: "5m");
            run = run with { Execution = run.Execution! with { OrderExpiration = "active_cancel" } };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await RunUntilCancelledAsync(runner, run, [CreateStrategy("1d", "5m")], cancellation, CancelAfterIterations(cancellation, 1));

            submission.Verify(service => service.RequestCancelAsync(It.IsAny<OrderCancellationSubmission>(),
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    // Pipeline stops and exit-only rebuilds still submit a technical exit using original position ownership.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_SubmitsTechnicalExitClose_ForActiveStrategyPosition(bool rebuilt)
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var provider = new Mock<IMarketDataProvider>();
            provider
                .Setup(x => x.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                    YieldBars(tickers.SelectMany(CreateFallingFiveMinuteBars), token));

            var broker = new Mock<IBrokerClient>();
            broker
                .Setup(x => x.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new ActiveBrokerOrder(
                        "sell-leg-1",
                        "MXL",
                        "sell",
                        "new",
                        "stop",
                        120m,
                        null,
                        100m,
                        DateTimeOffset.UtcNow.AddHours(-2),
                        "broker-generated-exit",
                        0m,
                        null,
                        DateTimeOffset.UtcNow)
                ]);
            broker
                .Setup(x => x.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new BrokerPosition("MXL", "long", 100m, 108m, 103m, -500m)
                ]);
            broker
                .Setup(x => x.GetSessionAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TradingSessionSnapshot(
                    new DateOnly(2026, 8, 26),
                    EquityTradingSession.Regular,
                    new DateTimeOffset(2026, 8, 26, 19, 31, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 26, 13, 30, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 26, 20, 0, 0, TimeSpan.Zero)));

            var submissions = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
            submissions.Setup(service => service.RequestCancelAsync(
                    It.Is<OrderCancellationSubmission>(cancel =>
                        cancel.ClientOrderId == "broker-generated-exit" &&
                        cancel.BrokerOrderId == "sell-leg-1" &&
                        cancel.Reason.Contains("technical_exit_below_ema20", StringComparison.Ordinal)),
                    broker.Object,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OrderStateSnapshot(
                    Guid.NewGuid(),
                    "broker-generated-exit",
                    "sell-leg-1",
                    OrderState.Canceled,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    null,
                    1));
            submissions.Setup(service => service.SubmitPositionExitAsync(
                    It.Is<PositionExitSubmission>(exit =>
                        exit.Symbol == "MXL" &&
                        exit.RunContext.RunId == TestExecutionRunId &&
                        exit.RunContext.CodeVersion == new string('b', 40) &&
                        exit.Quantity == 100m &&
                        exit.Reason == "technical_exit_below_ema20"),
                    broker.Object,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OrderSubmissionResult(
                    "exit-1",
                    "TEST-S-MXL-20260826-001-12345678",
                    Guid.NewGuid(),
                    new DateTimeOffset(2026, 8, 26, 19, 31, 0, TimeSpan.Zero)));

            var journal = CreateFilledPositionJournal(
                "MXL",
                100m,
                108m,
                80m,
                128m,
                new DateTimeOffset(2026, 8, 20, 13, 30, 0, TimeSpan.Zero));

            var audits = new List<DecisionAuditRecord>();
            var auditRepo = new Mock<IDecisionAuditRepository>();
            auditRepo
                .Setup(x => x.SaveAuditAsync(It.IsAny<DecisionAuditRecord>(), It.IsAny<CancellationToken>()))
                .Callback<DecisionAuditRecord, CancellationToken>((record, _) => audits.Add(record))
                .Returns(Task.CompletedTask);

            var streams = new Mock<IMarketStateSnapshotProvider>();
            streams.Setup(source => source.GetTickerStateAsync(
                    "BROKE", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<int>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Synthetic stream read failure."));
            streams.Setup(source => source.GetTickerStateAsync(
                    "MXL", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<int>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((TickerMarketState?)null);
            var runner = CreateRunner(
                provider.Object,
                lockService: null,
                auditRepo,
                broker.Object,
                orderSubmissionService: submissions.Object,
                orderIntentRepository: journal.Intents.Object,
                orderEventRepository: journal.Events.Object,
                positionLedgerRepository: journal.Positions.Object,
                marketStateSnapshots: streams.Object,
                initialPipelineHealth: rebuilt ? null : new LivePipelineHealth(
                    EntriesStoppedAtUtc: new DateTimeOffset(2026, 8, 26, 19, 0, 0, TimeSpan.Zero)),
                exitOnly: rebuilt);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var progress = new Progress<string>(message =>
            {
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal))
                {
                    cts.Cancel();
                }
            });

            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["MXL", "BROKE"], ["5m"], workerCount: 2, derivedSource: "5m", dryRun: false, allowLiveOrders: true),
                [CreateStrategy(
                    primaryTimeframe: "1d",
                    executionTimeframe: "5m",
                    exitOnCloseBelowEma20: true,
                    minHoldBarsBeforeTechnicalExit: 4)],
                cts,
                progress);

            submissions.Verify(service => service.RequestCancelAsync(
                It.IsAny<OrderCancellationSubmission>(),
                broker.Object,
                It.IsAny<CancellationToken>()), Times.Never);
            submissions.Verify(service => service.SubmitPositionExitAsync(
                It.IsAny<PositionExitSubmission>(),
                broker.Object,
                It.IsAny<CancellationToken>()), Times.Once);
            Assert.Contains(audits, audit =>
                audit.Ticker == "MXL" &&
                audit.Decision == "ExitSubmitted" &&
                audit.RejectionReason == "technical_exit_below_ema20");
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    [Fact]
    public async Task RunAsync_RaisesBrokerStop_WhenAtrTrailingStopActivates()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var risingBars = CreateRisingFiveMinuteBars("RGNT");
            var provider = new Mock<IMarketDataProvider>();
            provider
                .Setup(x => x.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> _, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                    YieldBars(risingBars, token));

            var broker = new Mock<IBrokerClient>();
            broker
                .Setup(x => x.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new ActiveBrokerOrder(
                        "stop-leg-1",
                        "RGNT",
                        "sell",
                        "new",
                        "stop",
                        null,
                        95m,
                        100m,
                        risingBars[^20].Timestamp,
                        "broker-generated-exit",
                        0m,
                        null,
                        risingBars[^1].Timestamp)
                ]);
            broker
                .Setup(x => x.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new BrokerPosition("RGNT", "long", 100m, 100m, 118m, 1800m)
                ]);
            var submissions = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
            submissions.Setup(service => service.ReplaceProtectiveStopAsync(
                    It.Is<ProtectiveStopReplacementSubmission>(replace =>
                        replace.OwnerClientOrderId == "broker-generated-exit" &&
                        replace.BrokerOrderId == "stop-leg-1" &&
                        replace.StopPrice > 95m),
                    broker.Object,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProtectiveStopReplacementSubmission replace, IBrokerClient _, CancellationToken _) =>
                    new ActiveBrokerOrder(
                        replace.BrokerOrderId,
                        replace.Symbol,
                        "sell",
                        "new",
                        "stop",
                        null,
                        replace.StopPrice,
                        100m,
                        risingBars[^20].Timestamp,
                        replace.OwnerClientOrderId,
                        0m,
                        null,
                        risingBars[^1].Timestamp));

            var journal = CreateFilledPositionJournal(
                "RGNT",
                100m,
                100m,
                95m,
                1000m,
                risingBars[^20].Timestamp);
            journal.Intents
                .Setup(repository => repository.GetByClientOrderIdAsync(
                    "broker-generated-exit",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OrderIntentRecord
                {
                    IntentId = Guid.NewGuid(),
                    Kind = OrderIntentKind.ProtectiveStop,
                    ClientOrderId = "broker-generated-exit",
                    AccountId = "paper-account",
                    StrategyId = "BACKSTOP",
                    Symbol = "RGNT",
                    PositionGenerationEventId = 1,
                    RunId = TestExecutionRunId
                });

            var runner = CreateRunner(
                provider.Object,
                lockService: null,
                brokerClient: broker.Object,
                orderSubmissionService: submissions.Object,
                orderIntentRepository: journal.Intents.Object,
                orderEventRepository: journal.Events.Object,
                positionLedgerRepository: journal.Positions.Object,
                timeProvider: new FixedTimeProvider(risingBars[^1].Timestamp.AddHours(1)));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var progress = new Progress<string>(message =>
            {
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal))
                {
                    cts.Cancel();
                }
            });

            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["RGNT"], ["5m"], workerCount: 2, derivedSource: "5m", dryRun: false, allowLiveOrders: true),
                [CreateStrategy(
                    primaryTimeframe: "1d",
                    executionTimeframe: "5m",
                    enableAtrTrailingStop: true,
                    trailingStopAtrMultiple: 2.0m,
                    trailingActivationR: 1.0m)],
                cts,
                progress);

            submissions.Verify(service => service.ReplaceProtectiveStopAsync(
                It.IsAny<ProtectiveStopReplacementSubmission>(),
                broker.Object,
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    [Fact]
    public async Task RunAsync_CountsPositionWithSellExitLeg_AsSingleActiveExposure()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var provider = new Mock<IMarketDataProvider>();
            provider
                .Setup(x => x.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                    YieldBars(tickers.SelectMany(CreateFiveMinuteBars), token));

            var broker = new Mock<IBrokerClient>();
            broker
                .Setup(x => x.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new ActiveBrokerOrder(
                        "sell-exit-leg",
                        "AMD",
                        "sell",
                        "new",
                        "limit",
                        10m,
                        null,
                        120m,
                        DateTimeOffset.UtcNow.AddMinutes(-10),
                        "broker-generated-exit",
                        0m,
                        null,
                        DateTimeOffset.UtcNow)
                ]);
            broker
                .Setup(x => x.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new BrokerPosition("AMD", "long", 10m, 100m, 101m, 10m)
                ]);

            var audits = new List<DecisionAuditRecord>();
            var auditRepo = new Mock<IDecisionAuditRepository>();
            auditRepo
                .Setup(x => x.SaveAuditAsync(It.IsAny<DecisionAuditRecord>(), It.IsAny<CancellationToken>()))
                .Callback<DecisionAuditRecord, CancellationToken>((record, _) => audits.Add(record))
                .Returns(Task.CompletedTask);

            var runner = CreateRunner(provider.Object, lockService: null, auditRepo, broker.Object);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var progress = new Progress<string>(message =>
            {
                if (message.StartsWith("Iteration finished.", StringComparison.Ordinal))
                {
                    cts.Cancel();
                }
            });

            await RunUntilCancelledAsync(
                runner,
                CreateRunConfig(resultsRoot, ["AMD"], ["5m"], workerCount: 2, derivedSource: "5m"),
                [CreateStrategy(primaryTimeframe: "1d", executionTimeframe: "5m")],
                cts,
                progress);

            Assert.Contains(audits, audit =>
                audit.Ticker == "AMD" &&
                audit.Decision == "Skipped" &&
                audit.RejectionReason == "max_open_trades_per_ticker_reached (Actual: 1, Allowed: 1)");
            Assert.DoesNotContain(audits, audit =>
                audit.Ticker == "AMD" &&
                audit.RejectionReason?.Contains("Actual: 2", StringComparison.OrdinalIgnoreCase) == true);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    private static FilledPositionJournal CreateFilledPositionJournal(
        string symbol,
        decimal quantity,
        decimal entryPrice,
        decimal stopPrice,
        decimal takeProfitPrice,
        DateTimeOffset filledAtUtc)
    {
        const string entryClientOrderId = "entry-client";
        var intent = new OrderIntentRecord
        {
            IntentId = Guid.NewGuid(),
            Kind = OrderIntentKind.StrategyEntry,
            ClientOrderId = entryClientOrderId,
            AccountId = "paper-account",
            StrategyId = "test_strat",
            Symbol = symbol,
            Side = "buy",
            OrderType = "limit",
            TimeInForce = "day",
            RequestedQuantity = quantity,
            LimitPrice = entryPrice,
            StopPrice = stopPrice,
            CreatedAtUtc = filledAtUtc,
            RequestJson = System.Text.Json.JsonSerializer.Serialize(new { takeProfitPrice }),
            RunId = TestExecutionRunId
        };
        var position = new PositionLedgerSnapshot(
            "paper-account",
            symbol,
            quantity,
            "test_strat",
            filledAtUtc,
            filledAtUtc,
            PositionEventId: 1,
            PositionGenerationEventId: 1,
            PositionGenerationClientOrderId: entryClientOrderId,
            LatestClientOrderId: entryClientOrderId,
            LatestFillPrice: entryPrice,
            LatestFillSide: "buy");
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByClientOrderIdAsync(
                entryClientOrderId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(intent);
        var events = new Mock<IOrderEventRepository>();
        events.Setup(repository => repository.GetCurrentAsync(
                entryClientOrderId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderStateSnapshot(
                TestExecutionRunId,
                entryClientOrderId,
                "entry-broker-order",
                OrderState.Filled,
                filledAtUtc,
                filledAtUtc,
                quantity,
                entryPrice,
                EventId: 1));
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.ListCurrentForRunAsync(
                TestExecutionRunId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new RunOwnedPositionLedgerSnapshot(position, TestExecutionRunId)
            ]);
        return new FilledPositionJournal(intents, events, positions);
    }

    // Supply an authoritative synthetic exchange schedule while keeping market bars test-owned.
    // Capture the runner's persisted decision rows without creating an operator database.
    private static Mock<IDecisionAuditRepository> CaptureDecisionAudits(List<DecisionAuditRecord> audits)
    {
        var repository = new Mock<IDecisionAuditRepository>();
        repository.Setup(audit => audit.SaveAuditAsync(It.IsAny<DecisionAuditRecord>(), It.IsAny<CancellationToken>()))
            .Callback<DecisionAuditRecord, CancellationToken>((record, _) => audits.Add(record))
            .Returns(Task.CompletedTask);
        return repository;
    }

    // Supply deterministic scheduling, execution identity and optional evidence loaders for runner tests.
    private static LiveRunner CreateRunner(
        IMarketDataProvider provider,
        ITickerLockService? lockService,
        Mock<IDecisionAuditRepository>? auditRepoMock = null,
        IBrokerClient? brokerClient = null,
        IOrderSubmissionService? orderSubmissionService = null,
        IOrderLifecycleService? orderLifecycleService = null,
        ICatalystProvider? catalystProvider = null,
        ILiveDiscoverySession? discoverySession = null,
        TimeProvider? timeProvider = null,
        TimeSpan? iterationInterval = null,
        IOrderIntentRepository? orderIntentRepository = null,
        IOrderEventRepository? orderEventRepository = null,
        IPositionLedgerRepository? positionLedgerRepository = null,
        ICandidateRepository? candidateRepository = null,
        LivePipelineHealth? initialPipelineHealth = null,
        Func<LivePipelineHealth, CancellationToken, Task>? publishPipelineHealth = null,
        IMarketStateSnapshotProvider? marketStateSnapshots = null,
        IStrategyDecisionContextProvider? decisionContextProvider = null,
        bool exitOnly = false)
    {
        var auditRepo = auditRepoMock ?? new Mock<IDecisionAuditRepository>();
        if (auditRepoMock is null)
        {
            auditRepo
                .Setup(x => x.SaveAuditAsync(It.IsAny<DecisionAuditRecord>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        return new LiveRunner(
            new CalendarFixtureProvider(provider),
            catalystProvider,
            brokerClient,
            lockService,
            orderIntentRepository,
            orderEventRepository,
            positionLedgerRepository,
            auditRepo.Object,
            NullLogger<LiveRunner>.Instance,
            orderSubmissionService: orderSubmissionService,
            executionRunContext: new ExecutionRunContext(
                TestExecutionRunId,
                "paper",
                new string('a', 64),
                new string('b', 40),
                new DateTimeOffset(2026, 7, 21, 13, 0, 0, TimeSpan.Zero)),
            orderLifecycleService: orderLifecycleService,
            discoverySession: discoverySession,
            marketStateSnapshots: marketStateSnapshots,
            candidateRepository: candidateRepository ?? new MemoryCandidateRepository(),
            timeProvider: timeProvider ?? new FixedTimeProvider(
                new DateTimeOffset(2026, 8, 26, 19, 31, 0, TimeSpan.Zero)),
            iterationInterval: iterationInterval,
            decisionContextProvider: decisionContextProvider,
            initialPipelineHealth: initialPipelineHealth,
            publishPipelineHealth: publishPipelineHealth,
            exitOnly: exitOnly);
    }

    private sealed record FilledPositionJournal(
        Mock<IOrderIntentRepository> Intents,
        Mock<IOrderEventRepository> Events,
        Mock<IPositionLedgerRepository> Positions);

    private static IOrderSubmissionService CreatePassThroughSubmissionService()
    {
        var service = new Mock<IOrderSubmissionService>();
        service
            .Setup(candidate => candidate.SubmitEntryOrderAsync(
                It.IsAny<EntryOrderSubmission>(),
                It.IsAny<IBrokerClient>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                EntryOrderSubmission submission,
                IBrokerClient broker,
                CancellationToken cancellationToken) =>
            {
                var clientOrderId = $"TEST-B-{submission.Order.Ticker}-20260721-001-12345678";
                var receipt = await broker.SubmitOrderAsync(
                    new BrokerEntryOrder(
                        submission.Order with { ClientOrderId = clientOrderId },
                        "buy",
                        submission.OrderType,
                        submission.TimeInForce,
                        SubmitOutsideRegularHours: false),
                    cancellationToken);
                return new OrderSubmissionResult(
                    receipt.BrokerOrderId,
                    clientOrderId,
                    submission.IntentId,
                    receipt.BrokerAcceptedAtUtc);
            });
        return service.Object;
    }

    private static async Task RunUntilCancelledAsync(
        LiveRunner runner,
        BacktestRunConfig run,
        StrategyDefinition[] strategies,
        CancellationTokenSource cts,
        IProgress<string> progress)
    {
        try
        {
            var authorized = strategies
                .Select(strategy =>
                {
                    const string semanticVersion = "1.0.0";
                    var identity = new StrategyArtifactIdentity(
                        strategy.StrategyId,
                        semanticVersion,
                        EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
                            strategy.StrategyId,
                            semanticVersion,
                            strategy,
                            StrategyAdmissionProfiles.DeterministicV1)));
                    return new AuthorizedRuntimeStrategy(
                        identity,
                        StrategySelectionMode.RunPaperShadow,
                        strategy,
                        StrategyAdmissionProfiles.DeterministicV1);
                })
                .ToArray();
            await runner.RunAsync(run, authorized, cts.Token, progress);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
    }

    private sealed class MemoryCandidateRepository : ICandidateRepository
    {
        private readonly object sync = new();
        private readonly Dictionary<Guid, CandidateRecord> candidates = [];
        private readonly Dictionary<Guid, List<CandidateTransitionRecord>> transitions = [];

        public IReadOnlyList<CandidateRecord> Records
        {
            get
            {
                lock (sync)
                {
                    return candidates.Values.ToArray();
                }
            }
        }

        public Task<CandidateRecord> UpsertDiscoveryAsync(
            ProductionRun run,
            CandidateRecord candidate,
            CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                if (!candidates.TryGetValue(candidate.CandidateId, out var existing))
                {
                    candidates[candidate.CandidateId] = candidate;
                    return Task.FromResult(candidate);
                }

                existing.RevalidatedAtUtc = candidate.RevalidatedAtUtc;
                existing.ExpiresAtUtc = candidate.ExpiresAtUtc;
                return Task.FromResult(existing);
            }
        }

        public Task<CandidateRecord> ApplyDecisionAsync(
            ProductionRun run,
            Guid candidateId,
            int expectedVersion,
            string semanticDecisionSha256,
            DateTimeOffset expiresAtUtc,
            IReadOnlyList<CandidateTransitionAppendRequest> appended,
            CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                var candidate = candidates[candidateId];
                Assert.Equal(expectedVersion, candidate.Version);
                var history = transitions.GetValueOrDefault(candidateId);
                if (history is null)
                {
                    history = [];
                    transitions[candidateId] = history;
                }

                foreach (var transition in appended)
                {
                    StrategyCandidateStateMachine.RequireTransition(candidate.State, transition.NewState);
                    candidate.Version++;
                    history.Add(new CandidateTransitionRecord
                    {
                        CandidateId = candidateId,
                        Sequence = candidate.Version,
                        PreviousState = candidate.State,
                        NewState = transition.NewState,
                        OccurredAtUtc = transition.OccurredAtUtc,
                        ReasonCode = transition.ReasonCode,
                        Source = transition.Source,
                        SemanticDecisionSha256 = semanticDecisionSha256,
                        EvidenceJson = transition.EvidenceJson,
                        RunId = run.RunId,
                        ConfigHash = run.ConfigHash,
                        CodeVersion = run.CodeVersion
                    });
                    candidate.State = transition.NewState;
                }

                candidate.SemanticDecisionSha256 = semanticDecisionSha256;
                candidate.ExpiresAtUtc = expiresAtUtc;
                candidate.RevalidatedAtUtc = appended[^1].OccurredAtUtc;
                return Task.FromResult(candidate);
            }
        }

        public Task<CandidateRecord?> GetAsync(
            Guid candidateId,
            CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                return Task.FromResult(candidates.GetValueOrDefault(candidateId));
            }
        }

        public Task<IReadOnlyList<CandidateTransitionRecord>> GetTransitionsAsync(
            Guid candidateId,
            CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                return Task.FromResult<IReadOnlyList<CandidateTransitionRecord>>(
                    transitions.GetValueOrDefault(candidateId)?.ToArray() ?? []);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CalendarFixtureProvider(IMarketDataProvider provider) : IMarketDataProvider, IMarketSessionScheduleProvider
    {
        // The underlying fixture still supplies all market observations.
        public IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken) =>
            provider.GetBarsAsync(tickers, timeframes, start, end, cancellationToken);

        // Existing live integration scenarios use ordinary weekday fixture sessions.
        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDateInclusive, DateOnly endDateInclusive, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var schedules = Enumerable.Range(startDateInclusive.DayNumber, endDateInclusive.DayNumber - startDateInclusive.DayNumber + 1)
                .Select(DateOnly.FromDayNumber)
                .ToDictionary(day => day, day => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                    ? MarketSessionSchedule.Closed(day)
                    : MarketSessionSchedule.RegularDay(day));
            return Task.FromResult<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>(schedules);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private long ticks = current.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref ticks, value.UtcTicks);
    }

    private sealed class ScriptedLiveDiscoverySession(
        IEnumerable<IReadOnlyList<string>> snapshots, TimeProvider? clock = null, bool failAfterFirst = false)
        : ILiveDiscoverySession
    {
        private readonly Queue<IReadOnlyList<string>> snapshots = new(snapshots);
        private HashSet<string> current = new(StringComparer.Ordinal);
        private DiscoveryUniverseSnapshot? previous;
        private DateTimeOffset nextRefresh;

        public Guid ScopeId { get; } = Guid.NewGuid();
        public int RefreshCount { get; private set; }

        // Match the one-minute source schedule while allowing any number of reads between captures.
        public Task<DiscoveryUniverseSnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = clock?.GetUtcNow() ?? new DateTimeOffset(2026, 8, 26, 19, 31, 0, TimeSpan.Zero);
            if (previous is not null && now < nextRefresh)
            {
                return Task.FromResult(previous with { ResolvedAtUtc = now, Added = [], Dropped = [] });
            }
            RefreshCount++;
            if (failAfterFirst && RefreshCount > 1)
            {
                throw new InvalidOperationException("Synthetic source refresh failure.");
            }
            var next = (snapshots.Count > 0 ? snapshots.Dequeue() : current.ToArray())
                .ToHashSet(StringComparer.Ordinal);
            var members = next.Select(symbol => new ActiveDiscoveryAggregate(
                Guid.NewGuid(),
                ScopeId,
                symbol,
                "swing",
                now,
                now,
                now.AddMinutes(3),
                RefreshCount,
                [new DiscoverySourceEvidence(
                    DiscoverySourceKinds.Wishlist, "test-list", now, now.AddMinutes(3), "{}",
                    Guid.NewGuid(), RefreshCount, null, false, null, new string('a', 64))])).ToArray();
            var result = new DiscoveryUniverseSnapshot(
                ScopeId,
                now,
                members,
                next.Except(current, StringComparer.Ordinal).ToArray(),
                current.Except(next, StringComparer.Ordinal).ToArray());
            current = next;
            previous = result;
            nextRefresh = now.AddMinutes(1);
            return Task.FromResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task RetainExposureSymbolsAsync(
            IReadOnlyCollection<string> symbols,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InlineProgress(Action<string> callback) : IProgress<string>
    {
        public void Report(string value) => callback(value);
    }

    private static Mock<IMarketDataProvider> CreateRecordingProvider(
        System.Collections.Concurrent.ConcurrentQueue<string> requestedTickers)
    {
        var provider = new Mock<IMarketDataProvider>();
        provider
            .Setup(x => x.GetBarsAsync(
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
            {
                var ticker = Assert.Single(tickers);
                requestedTickers.Enqueue(ticker);
                return YieldBars(CreateFiveMinuteBars(ticker), token);
            });
        return provider;
    }

    private static IProgress<string> CancelAfterIterations(
        CancellationTokenSource cancellation,
        int targetCount)
    {
        var completed = 0;
        return new InlineProgress(message =>
        {
            if (message.StartsWith("Iteration finished.", StringComparison.Ordinal) &&
                Interlocked.Increment(ref completed) >= targetCount)
            {
                cancellation.Cancel();
            }
        });
    }

    private static BacktestRunConfig CreateRunConfig(
        string resultsRoot,
        IReadOnlyList<string> tickers,
        IReadOnlyList<string> intervals,
        int workerCount,
        string derivedSource,
        bool dryRun = true,
        bool allowLiveOrders = false)
    {
        return new BacktestRunConfig(
            RunName: "LiveRunnerTest",
            Mode: "paper",
            Engine: new EngineConfig("tpl", workerCount, 100, 5, 120, false),
            TimeWindow: new TimeWindowConfig("rolling", 10, null, null),
            Tickers: tickers,
            Provider: "test",
            Intervals: intervals,
            RawRoot: "",
            NormalizedRoot: "",
            ResultsRoot: resultsRoot,
            CachePolicy: "refresh",
            DerivedTimeframes: new DerivedTimeframeConfig(derivedSource),
            Validation: new ValidationConfig(
                new OutOfSampleConfig(false, 0),
                new WalkForwardConfig(false, 0, 0),
                new BenchmarkConfig(false, ""),
                new DataQualityConfig(false, 0, 0, 0),
                new BiasRiskConfig("test", null, "none")),
            Providers: new ProviderConfig(
                new AlpacaProviderConfig("sip")),
            Portfolio: new PortfolioConfig(100000m, 1m, 20m, 5, 0m, 0m, 1, true),
            SignalSource: new SignalSourceConfig("internal_candles", false, "", 300, 0),
            Execution: new ExecutionConfig("simulated", "none", dryRun, allowLiveOrders, "market", "day", "market", AllowExtendedHoursTrading: true),
            News: new NewsConfig(false, "none", 0, 0),
            Screener: new ScreenerConfig(false, "none", []),
            Artifacts: new ArtifactRetentionConfig("summary"),
            Strategies: [],
            Universe: new UniverseConfig(
                UniverseConfig.ResolvedSnapshotMode,
                [],
                0m,
                0m,
                20,
                null,
                null));
    }

    private static StrategyDefinition CreateStrategy(
        string primaryTimeframe,
        string executionTimeframe,
        bool confluenceEnabled = false,
        string confluenceTimeframe = "1h",
        int confluenceEmaPeriod = 20,
        string confluenceMacdFilter = "none",
        bool exitOnCloseBelowEma20 = false,
        bool exitOnCloseBelowVwap = false,
        bool exitOnMacdHistogramNegative = false,
        int minHoldBarsBeforeTechnicalExit = 0,
        bool enableAtrTrailingStop = false,
        decimal trailingStopAtrMultiple = 0m,
        decimal trailingActivationR = 0m)
    {
        return new StrategyDefinition(
            StrategyId: "test_strat",
            StrategyName: "Test Strategy",
            Source: "test",
            Version: 1,
            Timeframe: primaryTimeframe,
            Direction: "long",
            EntryRules: new EntryRules(
                SetupType: "momentum",
                MinVolumeSpike: 0.0m,
                MinEntryRsi: 0.0m,
                MaxEntryRsi: 100.0m,
                TrendFilter: "none",
                MacdFilter: "none",
                RequirePriceAboveBollingerMiddle: false,
                RequireMacdHistogramPositive: false,
                RequirePriceAboveVwap: false,
                RequirePriceAboveEma20: false,
                RequirePriceAboveEma50: false,
                RequireEma20AboveEma50: false,
                MaxVwapExtensionAtr: null,
                RecentHighLookbackBars: 20,
                VolatilityContractionLookbackBars: 10,
                VolumeConfirmationMode: "none"),
            Confluence: new ConfluenceRules(confluenceEnabled, confluenceTimeframe, confluenceEmaPeriod, confluenceMacdFilter),
            ExitRules: new ExitRules(
                1.0m,
                2.0m,
                24m,
                enableAtrTrailingStop,
                trailingStopAtrMultiple,
                trailingActivationR,
                exitOnCloseBelowEma20,
                exitOnCloseBelowVwap,
                exitOnMacdHistogramNegative,
                minHoldBarsBeforeTechnicalExit),
            Execution: new ExecutionRules(executionTimeframe, 1.0m),
            Session: new SessionRules("America/New_York", 0, 0, 0));
    }

    private static IReadOnlyList<OhlcvBar> CreateFiveMinuteBars(string ticker)
    {
        // Fixed, exchange-aligned bars keep the fixture deterministic and model
        // real provider timestamps. Arbitrary wall-clock seconds would create
        // incomplete 15-minute buckets and must be rejected by the engine.
        var start = new DateTimeOffset(2026, 7, 20, 13, 30, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();
        for (var day = 0; day < 30; day++)
        {
            var sessionStart = AddWeekdays(start, day);
            for (var i = 0; i < 96; i++)
            {
                var sequence = (day * 96) + i;
                var close = 100m + sequence * 0.01m;
                bars.Add(new OhlcvBar(
                    ticker,
                    sessionStart.AddMinutes(i * 5),
                    "5m",
                    close - 0.05m,
                    close + 0.25m,
                    close - 0.25m,
                    close,
                    100000m + i));
            }
        }

        return bars;
    }

    private static DateTimeOffset AddWeekdays(DateTimeOffset start, int weekdays)
    {
        var result = start;
        for (var remaining = weekdays; remaining > 0;)
        {
            result = result.AddDays(1);
            if (result.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                remaining--;
            }
        }

        return result;
    }

    private static IReadOnlyList<OhlcvBar> CreateOneMinuteBars(
        string ticker,
        DateTimeOffset start,
        DateTimeOffset end,
        decimal close)
    {
        var bars = new List<OhlcvBar>();
        for (var timestamp = start; timestamp <= end; timestamp = timestamp.AddMinutes(1))
        {
            bars.Add(new OhlcvBar(
                ticker,
                timestamp,
                "1m",
                close - 0.05m,
                close + 0.10m,
                close - 0.10m,
                close,
                250000m));
        }

        return bars;
    }

    private static IReadOnlyList<OhlcvBar> CreateFallingFiveMinuteBars(string ticker)
    {
        var start = new DateTimeOffset(2026, 7, 20, 13, 30, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();
        for (var day = 0; day < 30; day++)
        {
            var sessionStart = AddWeekdays(start, day);
            for (var i = 0; i < 96; i++)
            {
                var sequence = (day * 96) + i;
                var close = 110m - sequence * 0.01m;
                bars.Add(new OhlcvBar(
                    ticker,
                    sessionStart.AddMinutes(i * 5),
                    "5m",
                    close + 0.05m,
                    close + 0.25m,
                    close - 0.25m,
                    close,
                    100000m + i));
            }
        }

        return bars;
    }

    private static IReadOnlyList<OhlcvBar> CreateRisingFiveMinuteBars(string ticker)
    {
        var start = new DateTimeOffset(2026, 7, 20, 13, 30, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();
        for (var day = 0; day < 30; day++)
        {
            var sessionStart = AddWeekdays(start, day);
            for (var i = 0; i < 96; i++)
            {
                var sequence = (day * 96) + i;
                var close = 100m + sequence * 0.04m;
                bars.Add(new OhlcvBar(
                    ticker,
                    sessionStart.AddMinutes(i * 5),
                    "5m",
                    close - 0.02m,
                    close + 0.20m,
                    close - 0.20m,
                    close,
                    200000m + i));
            }
        }

        return bars;
    }

    private static async IAsyncEnumerable<OhlcvBar> YieldBars(
        IEnumerable<OhlcvBar> bars,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var bar in bars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return bar with
            {
                DataFeed = "sip",
                AdjustmentPolicy = "all",
                KnownAtUtc = bar.Timestamp.Add(TradingFlow.Engine.Market.TimeframeParser.Parse(bar.Timeframe))
            };
            await Task.Yield();
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "trading-flow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
