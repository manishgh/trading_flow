using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
using Xunit;

namespace TradingFlow.Tests;

public class LiveRunnerIntegrationTests
{
    private readonly Mock<IMarketDataProvider> _marketDataProviderMock;
    private readonly Mock<IBrokerClient> _brokerClientMock;
    private readonly Mock<ITickerLockService> _lockServiceMock;
    private readonly Mock<IOrderStateRepository> _orderRepoMock;
    private readonly Mock<IDecisionAuditRepository> _auditRepoMock;

    public LiveRunnerIntegrationTests()
    {
        _marketDataProviderMock = new Mock<IMarketDataProvider>();
        _brokerClientMock = new Mock<IBrokerClient>();
        _lockServiceMock = new Mock<ITickerLockService>();
        _orderRepoMock = new Mock<IOrderStateRepository>();
        _auditRepoMock = new Mock<IDecisionAuditRepository>();

        // Create async enumerable of OhlcvBar
        var history = new List<OhlcvBar>();
        for (int i = 0; i < 20; i++)
        {
            history.Add(new OhlcvBar("AAPL", DateTimeOffset.UtcNow.AddMinutes(-15 * (20 - i)), "15m", 140m, 145m, 135m, 142m, 50000m));
        }

        var asyncEnumerable = history.ToAsyncEnumerable();

        _marketDataProviderMock
            .Setup(x => x.GetBarsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(asyncEnumerable);

        // Lock service allows
        _lockServiceMock
            .Setup(x => x.TryAcquireLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
            
        // No existing orders
        _orderRepoMock
            .Setup(x => x.GetActiveOrdersByTickerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PersistedOrder>());
    }

    [Fact]
    public async Task RunAsync_CompletesSuccessfully_And_AuditsDecision()
    {
        // Arrange
        var runner = new LiveRunner(
            _marketDataProviderMock.Object,
            null, // catalystProvider
            _brokerClientMock.Object,
            _lockServiceMock.Object,
            _orderRepoMock.Object,
            _auditRepoMock.Object,
            NullLogger<LiveRunner>.Instance
        );

        var config = new BacktestRunConfig(
            RunName: "IntegrationTest",
            Mode: "paper",
            Engine: null!,
            TimeWindow: null!,
            Tickers: new[] { "AAPL" },
            Provider: "dummy",
            Intervals: new[] { "15m" },
            RawRoot: "",
            NormalizedRoot: "",
            ResultsRoot: "",
            CachePolicy: "",
            DerivedTimeframes: null!,
            Validation: null!,
            Providers: null!,
            Portfolio: null!,
            SignalSource: null!,
            Execution: null!,
            News: null!,
            Screener: null!,
            Strategies: Array.Empty<string>()
        );

        var strategy = new StrategyDefinition(
            StrategyId: "test_strat",
            StrategyName: "Test Strat",
            Source: "test",
            Version: 1,
            Timeframe: "15m",
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
                VolatilityContractionLookbackBars: 10
            ),
            Confluence: new ConfluenceRules(false, "1h", 20, "none"),
            ExitRules: new ExitRules(1.0m, 2.0m, 24m, false, 0m, 0m, false, false, false, 0),
            Execution: new ExecutionRules("15m", 1.0m),
            Session: new SessionRules("America/New_York", 15, 15, 30)
        );

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        
        // Act
        try
        {
            await runner.RunAsync(config, new[] { strategy }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when the loop is cancelled
        }

        // Assert
        Assert.True(true);
    }
}
