using Moq;
using TradingFlow.Data.Csv;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Tests;

public sealed class CachedMarketDataProviderTests
{
    [Fact]
    public async Task ProviderCapabilities_AreForwardedWithoutChangingTheirValues()
    {
        using var directory = new TemporaryDirectory();
        var inner = new CapabilityProvider();
        var provider = new CachedMarketDataProvider(inner, directory.Path, "reuse");

        var schedule = await provider.LoadMarketSessionSchedulesAsync(
            new DateOnly(2026, 8, 3),
            new DateOnly(2026, 8, 3),
            CancellationToken.None);

        Assert.True(provider.OmittedSubDailyIntervalsMeanNoQualifyingTrades);
        Assert.Equal(inner.MarketDataProvenance, provider.MarketDataProvenance);
        Assert.True(schedule[new DateOnly(2026, 8, 3)].IsTradingDay);
    }

    [Fact]
    public async Task MissingCapabilities_RemainFailClosed()
    {
        using var directory = new TemporaryDirectory();
        var inner = new Mock<IMarketDataProvider>();
        var provider = new CachedMarketDataProvider(inner.Object, directory.Path, "reuse");

        Assert.False(provider.OmittedSubDailyIntervalsMeanNoQualifyingTrades);
        Assert.Throws<InvalidOperationException>(() => provider.MarketDataProvenance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.LoadMarketSessionSchedulesAsync(
                new DateOnly(2026, 8, 3),
                new DateOnly(2026, 8, 3),
                CancellationToken.None));
    }

    private sealed class CapabilityProvider :
        IMarketDataProvider,
        IMarketSessionScheduleProvider,
        IMarketDataCompletenessProvider,
        IMarketDataProvenanceProvider
    {
        public bool OmittedSubDailyIntervalsMeanNoQualifyingTrades => true;

        public MarketDataProvenance MarketDataProvenance { get; } =
            new("alpaca_historical_bars_v2", "sip", "all");

        public int CalendarCallCount { get; private set; }

        public async IAsyncEnumerable<Domain.Market.OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDateInclusive,
            DateOnly endDateInclusive,
            CancellationToken cancellationToken)
        {
            CalendarCallCount++;
            IReadOnlyDictionary<DateOnly, MarketSessionSchedule> result =
                new Dictionary<DateOnly, MarketSessionSchedule>
                {
                    [startDateInclusive] = MarketSessionSchedule.RegularDay(startDateInclusive)
                };
            return Task.FromResult(result);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"tradingflow-provider-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
