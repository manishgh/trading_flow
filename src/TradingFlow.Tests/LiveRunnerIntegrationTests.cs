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
                    var ticker = tickers.Single();
                    return ticker.Equals("SIVEF", StringComparison.OrdinalIgnoreCase)
                        ? YieldBars(Array.Empty<OhlcvBar>(), token)
                        : YieldBars(CreateFiveMinuteBars(ticker), token);
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
    public async Task RunAsync_UsesConfiguredWorkerCount_WhenProcessingTickers()
    {
        var resultsRoot = CreateTempDirectory();
        try
        {
            var activeProviders = 0;
            var maxActiveProviders = 0;
            var provider = new Mock<IMarketDataProvider>();
            provider
                .Setup(x => x.GetBarsAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> _, DateTimeOffset _, DateTimeOffset _, CancellationToken token) =>
                    YieldTrackedBars(tickers.Single(), token, () =>
                    {
                        var active = Interlocked.Increment(ref activeProviders);
                        maxActiveProviders = Math.Max(maxActiveProviders, active);
                    },
                    () => Interlocked.Decrement(ref activeProviders)));

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

            Assert.Equal(1, maxActiveProviders);
        }
        finally
        {
            TryDeleteDirectory(resultsRoot);
        }
    }

    private static LiveRunner CreateRunner(
        IMarketDataProvider provider,
        ITickerLockService? lockService,
        Mock<IDecisionAuditRepository>? auditRepoMock = null)
    {
        var orderRepo = new Mock<IOrderStateRepository>();
        orderRepo
            .Setup(x => x.GetActiveOrdersByTickerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PersistedOrder>());
        var auditRepo = auditRepoMock ?? new Mock<IDecisionAuditRepository>();
        auditRepo
            .Setup(x => x.SaveAuditAsync(It.IsAny<DecisionAuditRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new LiveRunner(
            provider,
            catalystProvider: null,
            brokerClient: null,
            lockService,
            orderRepo.Object,
            auditRepo.Object,
            NullLogger<LiveRunner>.Instance);
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
        string derivedSource)
    {
        return new BacktestRunConfig(
            RunName: "LiveRunnerTest",
            Mode: "paper",
            Engine: new EngineConfig("tpl", workerCount, 100, 50, false),
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
                new YahooProviderConfig("", new YahooHeaderConfig("", "", ""), 30, 0, 0),
                new AlpacaProviderConfig("sip"),
                new TradingViewProviderConfig(false)),
            Portfolio: new PortfolioConfig(100000m, 1m, 20m, 5, 0m, 0m, 1, true),
            SignalSource: new SignalSourceConfig("internal_candles", false, "", 300, 0),
            Execution: new ExecutionConfig("simulated", "none", true, false, "market", "day", "market"),
            News: new NewsConfig(false, "none", 0, 0),
            Screener: new ScreenerConfig(false, "none", []),
            Strategies: []);
    }

    private static StrategyDefinition CreateStrategy(string signalTimeframe, string executionTimeframe)
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
            Confluence: new ConfluenceRules(false, "1h", 20, "none"),
            ExitRules: new ExitRules(1.0m, 2.0m, 24m, false, 0m, 0m, false, false, false, 0),
            Execution: new ExecutionRules(executionTimeframe, 1.0m),
            Session: new SessionRules("America/New_York", 0, 0, 0));
    }

    private static IReadOnlyList<OhlcvBar> CreateFiveMinuteBars(string ticker)
    {
        var start = DateTimeOffset.UtcNow.AddHours(-8);
        return Enumerable.Range(0, 96)
            .Select(i =>
            {
                var close = 100m + i * 0.1m;
                return new OhlcvBar(
                    ticker,
                    start.AddMinutes(i * 5),
                    "5m",
                    close - 0.05m,
                    close + 0.25m,
                    close - 0.25m,
                    close,
                    100000m + i);
            })
            .ToArray();
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

    private static async IAsyncEnumerable<OhlcvBar> YieldTrackedBars(
        string ticker,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        Action onStart,
        Action onStop)
    {
        onStart();
        try
        {
            await Task.Delay(50, cancellationToken);
            foreach (var bar in CreateFiveMinuteBars(ticker))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return bar;
            }
        }
        finally
        {
            onStop();
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
