using TradingFlow.Data.Catalysts;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Tests;

public sealed class CachedCatalystProviderTests
{
    [Fact]
    public async Task GetCatalystsAsync_ReusesExactWindowCache()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "trading-flow-catalyst-cache", Guid.NewGuid().ToString("N"));
        var inner = new CountingCatalystProvider();
        var provider = new CachedCatalystProvider(inner, tempRoot, "use_cache");
        var start = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
        var end = DateTimeOffset.Parse("2026-06-02T00:00:00Z");

        try
        {
            var first = await provider.GetCatalystsAsync("POET", start, end, CancellationToken.None);
            var second = await provider.GetCatalystsAsync("POET", start, end, CancellationToken.None);

            Assert.Single(first);
            Assert.Single(second);
            Assert.Equal(1, inner.CallCount);
            Assert.Equal(first[0], second[0]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class CountingCatalystProvider : ICatalystProvider
    {
        public int CallCount { get; private set; }

        public string ProviderName => "test";

        public Task<IReadOnlyList<CatalystEvent>> GetCatalystsAsync(
            string ticker,
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            CancellationToken cancellationToken)
        {
            CallCount++;
            IReadOnlyList<CatalystEvent> catalysts =
            [
                new CatalystEvent(
                    ticker,
                    windowStart.AddHours(1),
                    CatalystType.NewsReport,
                    "POET wins optical engine order",
                    0.82m,
                    ProviderName,
                    "article-1",
                    "Customer demand improved.",
                    "testwire",
                    "https://example.test/poet")
            ];
            return Task.FromResult(catalysts);
        }
    }
}
