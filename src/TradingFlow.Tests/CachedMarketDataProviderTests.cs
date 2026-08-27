using Moq;
using TradingFlow.Data.Csv;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Tests;

public sealed class CachedMarketDataProviderTests
{
    [Fact]
    public void CacheWithoutCoverageManifest_DoesNotClaimAuthoritativeOmissionSemantics()
    {
        var inner = new Mock<IMarketDataProvider>();
        var provider = new CachedMarketDataProvider(inner.Object, "unused", "reuse");

        Assert.IsNotAssignableFrom<IMarketDataCompletenessProvider>(provider);
    }
}
