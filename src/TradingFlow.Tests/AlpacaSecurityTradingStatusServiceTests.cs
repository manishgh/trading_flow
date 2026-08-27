using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class AlpacaMarketStateStreamServiceTests
{
    [Fact]
    public async Task DiscoveryScopes_AreReferenceCountedBeforeUnsubscribe()
    {
        var service = CreateService();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await service.ReplaceScopeAsync(first, ["AAPL", "MSFT"], default);
        await service.ReplaceScopeAsync(second, ["AAPL"], default);
        await service.RemoveScopeAsync(first, default);

        Assert.Equal(["AAPL"], await service.GetDesiredSymbolsAsync(default));

        await service.RemoveScopeAsync(second, default);
        Assert.Empty(await service.GetDesiredSymbolsAsync(default));
    }

    [Fact]
    public async Task EntryStatusObservation_IsReleasedAfterGateSnapshot()
    {
        var service = CreateService();

        await service.EnsureObservedAsync("AAPL", default);
        await service.EnsureObservedAsync("AAPL", default);
        Assert.Equal(["AAPL"], await service.GetDesiredSymbolsAsync(default));

        await service.ReleaseObservationAsync("AAPL", default);
        Assert.Equal(["AAPL"], await service.GetDesiredSymbolsAsync(default));

        await service.ReleaseObservationAsync("AAPL", default);
        Assert.Empty(await service.GetDesiredSymbolsAsync(default));
    }

    [Fact]
    public async Task ApplyMessage_CompletedBarAndUpdatedBarUseSharedRevisionPolicy()
    {
        var service = CreateService();
        var observed = new DateTimeOffset(2026, 7, 22, 14, 31, 0, TimeSpan.Zero);

        await service.ApplyMessageAsync(Parse("""{"T":"b","S":"AAPL","t":"2026-07-22T14:30:00Z","o":100,"h":101,"l":99,"c":100.5,"v":1000}"""), observed, 4);
        await service.ApplyMessageAsync(Parse("""{"T":"u","S":"AAPL","t":"2026-07-22T14:30:00Z","o":100,"h":102,"l":99,"c":101.5,"v":1100}"""), observed.AddMinutes(1), 4);

        Assert.Equal(SecurityTradingState.Unknown, service.GetStatus("AAPL").State);
    }

    [Fact]
    public async Task ApplyMessage_TradeCannotClearObservedHalt_ButProviderResumeCan()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 7, 22, 14, 30, 0, TimeSpan.Zero);

        await service.ApplyMessageAsync(Parse("""{"T":"t","S":"MSFT","t":"2026-07-22T14:30:00Z"}"""), now, 1);
        Assert.Equal(SecurityTradingState.TradingObserved, service.GetStatus("MSFT").State);

        await service.ApplyMessageAsync(Parse("""{"T":"s","S":"MSFT","sc":"H","rc":"M","t":"2026-07-22T14:30:01Z"}"""), now.AddSeconds(1), 1);
        Assert.Equal(SecurityTradingState.Halted, service.GetStatus("MSFT").State);

        await service.ApplyMessageAsync(Parse("""{"T":"t","S":"MSFT","t":"2026-07-22T14:30:02Z"}"""), now.AddSeconds(2), 1);
        Assert.Equal(SecurityTradingState.Halted, service.GetStatus("MSFT").State);

        await service.ApplyMessageAsync(Parse("""{"T":"s","S":"MSFT","sc":"T","t":"2026-07-22T14:30:03Z"}"""), now.AddSeconds(3), 1);
        Assert.Equal(SecurityTradingState.TradingObserved, service.GetStatus("MSFT").State);
    }

    [Fact]
    public async Task ApplyMessage_QuotationResumption_DoesNotClaimTradingResumed()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 7, 22, 14, 30, 0, TimeSpan.Zero);

        await service.ApplyMessageAsync(Parse("""{"T":"s","S":"MSFT","sc":"Q","t":"2026-07-22T14:30:00Z"}"""), now, 1);

        Assert.Equal(SecurityTradingState.Unknown, service.GetStatus("MSFT").State);
    }

    [Fact]
    public async Task ApplyMessage_RecognizedMalformedBarIsExplicitlyRejectedAndLogged()
    {
        var logger = new Mock<ILogger<AlpacaMarketStateStreamService>>();
        var service = CreateService(logger: logger.Object);

        await service.ApplyMessageAsync(
            Parse("""{"T":"b","S":"AAPL","t":"2026-07-22T14:30:00Z","o":100}"""),
            new DateTimeOffset(2026, 7, 22, 14, 31, 0, TimeSpan.Zero),
            1);

        logger.Verify(value => value.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains("Rejected malformed Alpaca market bar", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ReleaseLeaseSafely_RepositoryFailureDoesNotEscapeHostedService()
    {
        var repository = new Mock<IMarketStreamLeaseRepository>();
        repository.Setup(value => value.TryReleaseAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var service = CreateService(repository.Object);
        var now = DateTimeOffset.UtcNow;

        var released = await service.ReleaseLeaseSafelyAsync(
            new MarketStreamLease("alpaca:sip", "owner", 7, now, now, now.AddSeconds(30)));

        Assert.False(released);
    }

    private static AlpacaMarketStateStreamService CreateService(
        IMarketStreamLeaseRepository? leaseRepository = null,
        ILogger<AlpacaMarketStateStreamService>? logger = null)
    {
        var provider = new Mock<IMarketDataProvider>();
        provider.Setup(value => value.GetBarsAsync(
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyBars());
        var marketState = new StreamingMarketStateProcessor(
            NullCandleStore.Instance,
            new CandleStoreContext("test", "test", "alpaca-sip"),
            StreamingMarketStateOptions.Default,
            TimeProvider.System,
            NullLogger<StreamingMarketStateProcessor>.Instance);
        marketState.AdvanceFencingFloor(1);
        marketState.ActivateOwnership(1);
        marketState.MarkSnapshotsReady(1);
        return new AlpacaMarketStateStreamService(
            new AlpacaCredentialProvider(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            Mock.Of<IAlpacaMarketStateStreamClientFactory>(),
            logger ?? NullLogger<AlpacaMarketStateStreamService>.Instance,
            leaseRepository ?? Mock.Of<IMarketStreamLeaseRepository>(),
            marketState,
            new Lazy<IMarketDataProvider>(() => provider.Object),
            AlpacaMarketStateStreamOptions.Default,
            TimeProvider.System);
    }

    private static async IAsyncEnumerable<OhlcvBar> EmptyBars()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
