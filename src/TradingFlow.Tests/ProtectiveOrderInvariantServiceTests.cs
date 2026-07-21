using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Tests;

public sealed class ProtectiveOrderInvariantServiceTests
{
    [Fact]
    public async Task MissingStop_UsesPersistedStructuralStop_AndSubmitsOppositeSideGtcOrder()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var local = Snapshot("MSFT", 10m, "SWGA-B-MSFT-20260721-001-12345678", 100m, "buy", now);
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByClientOrderIdAsync(local.LatestClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord { StopPrice = 98m });
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync("MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(local);
        ProtectiveStopSubmission? captured = null;
        var submissions = new Mock<IOrderSubmissionService>();
        submissions.Setup(service => service.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopSubmission>(),
                It.IsAny<IBrokerClient>(),
                It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopSubmission, IBrokerClient, CancellationToken>((submission, _, _) => captured = submission)
            .ReturnsAsync(new OrderSubmissionResult("stop-1", "BACKSTOP-S-MSFT-20260721-001-12345678", Guid.NewGuid(), now));
        var service = CreateService(intents.Object, positions.Object, submissions.Object, new EmptyMarketDataProvider(), now);

        var repairs = await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            [new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m)],
            [],
            CancellationToken.None);

        Assert.True(Assert.Single(repairs).Succeeded);
        Assert.NotNull(captured);
        Assert.Equal("sell", captured.Side);
        Assert.Equal(10m, captured.Quantity);
        Assert.Equal(98m, captured.StopPrice);
    }

    [Fact]
    public async Task ExistingStopCoveringFullPosition_DoesNotSubmitAnotherOrder()
    {
        var submissions = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
        var service = CreateService(
            Mock.Of<IOrderIntentRepository>(),
            Mock.Of<IPositionLedgerRepository>(),
            submissions.Object,
            new EmptyMarketDataProvider(),
            DateTimeOffset.UtcNow);
        var position = new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m);
        var stop = BrokerOrder("MSFT", "sell", "stop", 10m, 98m);

        var repairs = await service.EnsureAsync(Mock.Of<IBrokerClient>(), [position], [stop]);

        Assert.Empty(repairs);
    }

    [Fact]
    public async Task ActivatedStrategyStop_CancelsOnlyRedundantTradingFlowBackstop()
    {
        var submissions = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.CancelOrderAsync("backstop-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = CreateService(
            Mock.Of<IOrderIntentRepository>(),
            Mock.Of<IPositionLedgerRepository>(),
            submissions.Object,
            new EmptyMarketDataProvider(),
            DateTimeOffset.UtcNow);
        var position = new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m);
        var strategyStop = BrokerOrder("MSFT", "sell", "stop", 10m, 98m);
        var backstop = BrokerOrder("MSFT", "sell", "stop", 10m, 97m) with
        {
            OrderId = "backstop-1",
            ClientOrderId = "BACKSTOP-S-MSFT-20260721-001-12345678"
        };

        var repair = Assert.Single(await service.EnsureAsync(
            broker.Object,
            [position],
            [strategyStop, backstop]));

        Assert.True(repair.Succeeded);
        Assert.Contains("Canceled redundant", repair.Detail, StringComparison.Ordinal);
        broker.VerifyAll();
    }

    [Fact]
    public async Task PartialStopCoverage_IsNotAcceptedAsProtected()
    {
        var now = DateTimeOffset.UtcNow;
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync("MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot("MSFT", 10m, "SWGA-B-MSFT-20260721-001-12345678", 100m, "buy", now));
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByClientOrderIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord { StopPrice = 98m });
        var submissions = new Mock<IOrderSubmissionService>();
        submissions.Setup(service => service.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopSubmission>(), It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderSubmissionResult("stop-2", "BACKSTOP-S-MSFT-20260721-001-12345678", Guid.NewGuid(), now));
        var service = CreateService(intents.Object, positions.Object, submissions.Object, new EmptyMarketDataProvider(), now);

        var repairs = await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            [new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m)],
            [BrokerOrder("MSFT", "sell", "stop", 4m, 98m)]);

        Assert.True(Assert.Single(repairs).Succeeded);
        submissions.Verify(service => service.SubmitProtectiveStopAsync(
            It.Is<ProtectiveStopSubmission>(submission => submission.Quantity == 6m),
            It.IsAny<IBrokerClient>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FractionalPosition_FailsClosedInsteadOfSubmittingUnsupportedGtcStop()
    {
        var submissions = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
        var service = CreateService(
            Mock.Of<IOrderIntentRepository>(),
            Mock.Of<IPositionLedgerRepository>(),
            submissions.Object,
            new EmptyMarketDataProvider(),
            DateTimeOffset.UtcNow);

        var repair = Assert.Single(await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            [new BrokerPosition("MSFT", "long", 1.5m, 100m, 101m, 1.5m)],
            []));

        Assert.False(repair.Succeeded);
        Assert.Contains("fractional", repair.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingStructuralStop_UsesOnlyHistoricalBarsAvailableAtReconciliationTime()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 80)
            .Select(index => new OhlcvBar(
                "MSFT",
                now.AddDays(index - 80),
                "1d",
                100m + index,
                102m + index,
                99m + index,
                101m + index,
                1_000_000m))
            .ToArray();
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync("MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PositionLedgerSnapshot?)null);
        ProtectiveStopSubmission? captured = null;
        var submissions = new Mock<IOrderSubmissionService>();
        submissions.Setup(service => service.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopSubmission>(), It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopSubmission, IBrokerClient, CancellationToken>((submission, _, _) => captured = submission)
            .ReturnsAsync(new OrderSubmissionResult("stop-atr", "BACKSTOP-S-MSFT-20260721-001-12345678", Guid.NewGuid(), now));
        var service = CreateService(
            Mock.Of<IOrderIntentRepository>(),
            positions.Object,
            submissions.Object,
            new FixedMarketDataProvider(bars),
            now);

        var repair = Assert.Single(await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            [new BrokerPosition("MSFT", "long", 10m, 180m, 181m, 10m)],
            []));

        Assert.True(repair.Succeeded);
        Assert.NotNull(captured);
        Assert.True(captured.StopPrice > 0m);
        Assert.True(captured.StopPrice < 180m);
    }

    [Fact]
    public async Task AtrFallback_DiscardsProviderBarsAtOrAfterReconciliationTime()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 40)
            .Select(index => new OhlcvBar(
                "MSFT",
                now.AddDays(index - 40),
                "1d",
                100m,
                102m,
                99m,
                101m,
                1_000_000m))
            .Append(new OhlcvBar(
                "MSFT",
                now.AddMinutes(1),
                "1d",
                100m,
                1_000m,
                1m,
                900m,
                100_000_000m))
            .ToArray();
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync("MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PositionLedgerSnapshot?)null);
        ProtectiveStopSubmission? captured = null;
        var submissions = new Mock<IOrderSubmissionService>();
        submissions.Setup(service => service.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopSubmission>(), It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopSubmission, IBrokerClient, CancellationToken>((submission, _, _) => captured = submission)
            .ReturnsAsync(new OrderSubmissionResult("stop-atr", "BACKSTOP-S-MSFT-20260721-001-12345678", Guid.NewGuid(), now));
        var service = CreateService(
            Mock.Of<IOrderIntentRepository>(),
            positions.Object,
            submissions.Object,
            new UnfilteredMarketDataProvider(bars),
            now);

        var repair = Assert.Single(await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            [new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m)],
            []));

        Assert.True(repair.Succeeded);
        Assert.NotNull(captured);
        Assert.InRange(captured.StopPrice, 95m, 100m);
    }

    private static ProtectiveOrderInvariantService CreateService(
        IOrderIntentRepository intents,
        IPositionLedgerRepository positions,
        IOrderSubmissionService submissions,
        IMarketDataProvider marketData,
        DateTimeOffset now)
    {
        var run = new ProductionRun
        {
            RunId = Guid.NewGuid(),
            Profile = "paper",
            Status = "running",
            StartedAtUtc = now,
            ConfigHash = new string('a', 64),
            CodeVersion = new string('b', 40)
        };
        return new ProtectiveOrderInvariantService(
            intents,
            positions,
            submissions,
            new Lazy<IMarketDataProvider>(() => marketData),
            new IndicatorEngine(),
            new ProtectiveOrderOptions(1.5m),
            new ReconciliationRunContext(run),
            new FixedTimeProvider(now),
            NullLogger<ProtectiveOrderInvariantService>.Instance);
    }

    private static PositionLedgerSnapshot Snapshot(
        string symbol,
        decimal quantity,
        string clientOrderId,
        decimal fillPrice,
        string side,
        DateTimeOffset now) =>
        new(symbol, quantity, now, now, 1, clientOrderId, fillPrice, side);

    private static ActiveBrokerOrder BrokerOrder(
        string symbol,
        string side,
        string type,
        decimal quantity,
        decimal stopPrice) =>
        new(Guid.NewGuid().ToString("N"), symbol, side, "new", type, null, stopPrice, quantity,
            DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), 0m, null, DateTimeOffset.UtcNow);

    private sealed class EmptyMarketDataProvider : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FixedMarketDataProvider(IReadOnlyList<OhlcvBar> bars) : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var bar in bars.Where(bar => bar.Timestamp >= start && bar.Timestamp <= end))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return bar;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class UnfilteredMarketDataProvider(IReadOnlyList<OhlcvBar> bars) : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var bar in bars)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return bar;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
