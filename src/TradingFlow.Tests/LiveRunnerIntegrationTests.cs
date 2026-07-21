using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Audit;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Locking;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public class LiveRunnerIntegrationTests
{
    [Fact]
    public async Task RunAsync_DerivesMissingStrategyTimeframe_FromConfiguredBaseInterval()
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
                [CreateStrategy(signalTimeframe: "15m", executionTimeframe: "5m")],
                cts,
                progress);

            Assert.NotNull(requestedIntervals);
            Assert.Equal(["5m"], requestedIntervals);
            Assert.True(
                File.Exists(Path.Combine(resultsRoot, "live", "LiveRunnerTest", "AAPL", "AAPL_chart.json")),
                "The 15m strategy chart should be generated from derived 5m bars.");

            var chartJson = await File.ReadAllTextAsync(Path.Combine(resultsRoot, "live", "LiveRunnerTest", "AAPL", "AAPL_chart.json"));
            using var chart = System.Text.Json.JsonDocument.Parse(chartJson);
            Assert.Equal("15m", chart.RootElement.GetProperty("Timeframe").GetString());
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

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
                CreateRunConfig(resultsRoot, ["AAPL", "SIVEF"], ["5m"], workerCount: 2, derivedSource: "5m"),
                [CreateStrategy(signalTimeframe: "15m", executionTimeframe: "5m")],
                cts,
                progress);

            Assert.True(
                File.Exists(Path.Combine(resultsRoot, "live", "LiveRunnerTest", "AAPL", "AAPL_chart.json")),
                "AAPL should still produce live metrics even when SIVEF has no source bars.");

            auditRepo.Verify(
                x => x.SaveAuditAsync(
                    It.Is<DecisionAuditRecord>(record =>
                        record.Ticker == "SIVEF" &&
                        record.Decision == "Rejected" &&
                        record.RejectionReason != null &&
                        record.RejectionReason.StartsWith("ticker_pipeline_failed", StringComparison.Ordinal)),
                    It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
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
                CreateRunConfig(resultsRoot, ["AAPL"], ["5m"], workerCount: 2, derivedSource: "5m"),
                [CreateStrategy(signalTimeframe: "5m", executionTimeframe: "5m", confluenceEnabled: true, confluenceTimeframe: "5m", confluenceEmaPeriod: 20)],
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
                [CreateStrategy(signalTimeframe: "5m", executionTimeframe: "5m", confluenceEnabled: true, confluenceTimeframe: "5m", confluenceEmaPeriod: 20)],
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
                [CreateStrategy(signalTimeframe: "5m", executionTimeframe: "5m")],
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
                [CreateStrategy(signalTimeframe: "5m", executionTimeframe: "5m")],
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

    [Fact]
    public async Task RunAsync_SubmitsOrderUsingExecutionTimeframePrice_WhenSignalTimeframeDiffers()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            const decimal executionClose = 123.45m;
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
                .Setup(x => x.SubmitOrderAsync(It.IsAny<FinalizedOrder>(), It.IsAny<CancellationToken>()))
                .Callback<FinalizedOrder, CancellationToken>((order, _) => submittedOrder = order)
                .ReturnsAsync("order-1");

            var orderRepo = new Mock<IOrderStateRepository>();
            orderRepo
                .Setup(x => x.GetActiveOrdersByTickerAsync("AAPL", It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            orderRepo
                .Setup(x => x.GetOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PersistedOrder?)null);
            orderRepo
                .Setup(x => x.SaveOrderAsync(It.IsAny<PersistedOrder>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var runner = CreateRunner(
                provider.Object,
                lockService: null,
                brokerClient: broker.Object,
                orderStateRepository: orderRepo.Object,
                orderSubmissionService: CreatePassThroughSubmissionService());
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
                CreateRunConfig(resultsRoot, ["AAPL"], ["1m", "5m"], workerCount: 2, derivedSource: "1m", dryRun: false, allowLiveOrders: true),
                [CreateStrategy(signalTimeframe: "5m", executionTimeframe: "1m")],
                cts,
                progress);

            Assert.NotNull(submittedOrder);
            Assert.Equal(decimal.Round(executionClose * 1.0001m, 4), submittedOrder.LimitPrice);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    [Fact]
    public async Task RunAsync_SubmitsTechnicalExitClose_ForActiveStrategyPosition()
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
                    new ActiveBrokerOrder("sell-leg-1", "MXL", "sell", "new", "limit", 120m, null, 100m, DateTimeOffset.UtcNow.AddHours(-2))
                ]);
            broker
                .Setup(x => x.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new BrokerPosition("MXL", "long", 100m, 108m, 103m, -500m)
                ]);
            broker
                .Setup(x => x.CancelOrderAsync("sell-leg-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            broker
                .Setup(x => x.ClosePositionAsync("MXL", 100, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var orderRepo = new Mock<IOrderStateRepository>();
            orderRepo
                .Setup(x => x.GetActiveOrdersByTickerAsync("MXL", It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new PersistedOrder
                    {
                        OrderId = "entry-1",
                        Ticker = "MXL",
                        StrategyName = "Test Strategy",
                        Broker = "alpaca",
                        Status = "exit_submitted",
                        EntryPrice = 108m,
                        StopLossPrice = 98m,
                        TakeProfitPrice = 128m,
                        ShareQuantity = 100,
                        CreatedAt = DateTimeOffset.UtcNow.AddDays(-6),
                        UpdatedAt = DateTimeOffset.UtcNow.AddDays(-6)
                    }
                ]);
            orderRepo
                .Setup(x => x.GetOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PersistedOrder?)null);
            orderRepo
                .Setup(x => x.UpdateOrderStatusAsync("entry-1", "technical_exit_submitted", It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var audits = new List<DecisionAuditRecord>();
            var auditRepo = new Mock<IDecisionAuditRepository>();
            auditRepo
                .Setup(x => x.SaveAuditAsync(It.IsAny<DecisionAuditRecord>(), It.IsAny<CancellationToken>()))
                .Callback<DecisionAuditRecord, CancellationToken>((record, _) => audits.Add(record))
                .Returns(Task.CompletedTask);

            var runner = CreateRunner(
                provider.Object,
                lockService: null,
                auditRepo,
                broker.Object,
                orderRepo.Object);
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
                CreateRunConfig(resultsRoot, ["MXL"], ["5m"], workerCount: 2, derivedSource: "5m", dryRun: false, allowLiveOrders: true),
                [CreateStrategy(
                    signalTimeframe: "5m",
                    executionTimeframe: "5m",
                    exitOnCloseBelowEma20: true,
                    minHoldBarsBeforeTechnicalExit: 4)],
                cts,
                progress);

            broker.Verify(x => x.CancelOrderAsync("sell-leg-1", It.IsAny<CancellationToken>()), Times.Once);
            broker.Verify(x => x.ClosePositionAsync("MXL", 100, It.IsAny<CancellationToken>()), Times.Once);
            orderRepo.Verify(x => x.UpdateOrderStatusAsync("entry-1", "technical_exit_submitted", It.IsAny<CancellationToken>()), Times.Once);
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
                    new ActiveBrokerOrder("stop-leg-1", "RGNT", "sell", "new", "stop", null, 95m, 100m, risingBars[^20].Timestamp)
                ]);
            broker
                .Setup(x => x.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new BrokerPosition("RGNT", "long", 100m, 100m, 118m, 1800m)
                ]);
            broker
                .Setup(x => x.ModifyOrderAsync("stop-leg-1", It.IsAny<decimal>(), 0m, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var persistedOrder = new PersistedOrder
            {
                OrderId = "entry-1",
                Ticker = "RGNT",
                StrategyName = "Test Strategy",
                Broker = "alpaca",
                Status = "exit_submitted",
                EntryPrice = 100m,
                StopLossPrice = 95m,
                TakeProfitPrice = 1000m,
                ShareQuantity = 100,
                CreatedAt = risingBars[^20].Timestamp,
                UpdatedAt = risingBars[^20].Timestamp
            };

            PersistedOrder? savedOrder = null;
            var orderRepo = new Mock<IOrderStateRepository>();
            orderRepo
                .Setup(x => x.GetActiveOrdersByTickerAsync("RGNT", It.IsAny<CancellationToken>()))
                .ReturnsAsync([persistedOrder]);
            orderRepo
                .Setup(x => x.GetOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PersistedOrder?)null);
            orderRepo
                .Setup(x => x.SaveOrderAsync(It.IsAny<PersistedOrder>(), It.IsAny<CancellationToken>()))
                .Callback<PersistedOrder, CancellationToken>((order, _) => savedOrder = order)
                .Returns(Task.CompletedTask);

            var runner = CreateRunner(
                provider.Object,
                lockService: null,
                brokerClient: broker.Object,
                orderStateRepository: orderRepo.Object);
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
                    signalTimeframe: "5m",
                    executionTimeframe: "5m",
                    enableAtrTrailingStop: true,
                    trailingStopAtrMultiple: 2.0m,
                    trailingActivationR: 1.0m)],
                cts,
                progress);

            broker.Verify(x => x.ModifyOrderAsync("stop-leg-1", It.Is<decimal>(value => value > 95m), 0m, It.IsAny<CancellationToken>()), Times.Once);
            broker.Verify(x => x.ClosePositionAsync("RGNT", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.NotNull(savedOrder);
            Assert.True(savedOrder.StopLossPrice > 95m);
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
                    new ActiveBrokerOrder("sell-exit-leg", "AMD", "sell", "new", "limit", 10m, null, 120m, DateTimeOffset.UtcNow.AddMinutes(-10))
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
                [CreateStrategy(signalTimeframe: "5m", executionTimeframe: "5m")],
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

    private static LiveRunner CreateRunner(
        IMarketDataProvider provider,
        ITickerLockService? lockService,
        Mock<IDecisionAuditRepository>? auditRepoMock = null,
        IBrokerClient? brokerClient = null,
        IOrderStateRepository? orderStateRepository = null,
        IOrderSubmissionService? orderSubmissionService = null)
    {
        var defaultOrderRepo = new Mock<IOrderStateRepository>();
        defaultOrderRepo
            .Setup(x => x.GetActiveOrdersByTickerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PersistedOrder>());
        var auditRepo = auditRepoMock ?? new Mock<IDecisionAuditRepository>();
        if (auditRepoMock is null)
        {
            auditRepo
                .Setup(x => x.SaveAuditAsync(It.IsAny<DecisionAuditRecord>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        return new LiveRunner(
            provider,
            catalystProvider: null,
            brokerClient,
            lockService,
            orderStateRepository ?? defaultOrderRepo.Object,
            auditRepo.Object,
            NullLogger<LiveRunner>.Instance,
            orderSubmissionService: orderSubmissionService,
            executionRunContext: orderSubmissionService is null
                ? null
                : new ExecutionRunContext(
                    Guid.Parse("10000000-0000-0000-0000-000000000001"),
                    "paper",
                    new string('a', 64),
                    new string('b', 40),
                    new DateTimeOffset(2026, 7, 21, 13, 0, 0, TimeSpan.Zero)));
    }

    private static IOrderSubmissionService CreatePassThroughSubmissionService()
    {
        var service = new Mock<IOrderSubmissionService>();
        service
            .Setup(candidate => candidate.SubmitBracketOrderAsync(
                It.IsAny<BracketOrderSubmission>(),
                It.IsAny<IBrokerClient>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                BracketOrderSubmission submission,
                IBrokerClient broker,
                CancellationToken cancellationToken) =>
            {
                var clientOrderId = $"TEST-B-{submission.Order.Ticker}-20260721-001-12345678";
                var brokerOrderId = await broker.SubmitOrderAsync(
                    submission.Order with { ClientOrderId = clientOrderId },
                    cancellationToken);
                return new OrderSubmissionResult(brokerOrderId, clientOrderId, submission.IntentId);
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
            await runner.RunAsync(run, strategies, cts.Token, progress);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
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
            Engine: new EngineConfig("tpl", workerCount, 100, 50, 120, false),
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
            Execution: new ExecutionConfig("simulated", "none", dryRun, allowLiveOrders, "market", "day", "market", ExtendedHours: true),
            News: new NewsConfig(false, "none", 0, 0),
            Screener: new ScreenerConfig(false, "none", []),
            Artifacts: new ArtifactRetentionConfig("summary"),
            Strategies: []);
    }

    private static StrategyDefinition CreateStrategy(
        string signalTimeframe,
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
            Timeframe: signalTimeframe,
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
                OpeningRangeMinutes: 0,
                RecentHighLookbackBars: 20,
                VolatilityContractionLookbackBars: 10),
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
        var start = DateTimeOffset.UtcNow.AddDays(-7).AddHours(-8);
        var bars = new List<OhlcvBar>();
        for (var day = 0; day < 7; day++)
        {
            for (var i = 0; i < 96; i++)
            {
                var sequence = (day * 96) + i;
                var close = 100m + sequence * 0.01m;
                bars.Add(new OhlcvBar(
                    ticker,
                    start.AddDays(day).AddMinutes(i * 5),
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
        var start = DateTimeOffset.UtcNow.AddDays(-7).AddHours(-8);
        var bars = new List<OhlcvBar>();
        for (var day = 0; day < 7; day++)
        {
            for (var i = 0; i < 96; i++)
            {
                var sequence = (day * 96) + i;
                var close = 110m - sequence * 0.01m;
                bars.Add(new OhlcvBar(
                    ticker,
                    start.AddDays(day).AddMinutes(i * 5),
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
        var start = DateTimeOffset.UtcNow.AddDays(-7).AddHours(-8);
        var bars = new List<OhlcvBar>();
        for (var day = 0; day < 7; day++)
        {
            for (var i = 0; i < 96; i++)
            {
                var sequence = (day * 96) + i;
                var close = 100m + sequence * 0.04m;
                bars.Add(new OhlcvBar(
                    ticker,
                    start.AddDays(day).AddMinutes(i * 5),
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
            yield return bar;
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
