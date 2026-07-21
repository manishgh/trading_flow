using TradingFlow.Alpaca;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public sealed class AlpacaEndpointResolverTests
{
    [Theory]
    [InlineData(ProductionProfile.Development)]
    [InlineData(ProductionProfile.Paper)]
    public void NonLiveProfilesResolveOnlyPaperTradingEndpoints(ProductionProfile profile)
    {
        var endpoints = AlpacaEndpointResolver.Resolve(profile);
        var options = AlpacaOptions.Create(profile);

        Assert.Equal("https://paper-api.alpaca.markets/", endpoints.TradingRest.AbsoluteUri);
        Assert.Equal("wss://paper-api.alpaca.markets/stream", endpoints.TradingStream.AbsoluteUri);
        Assert.Equal(endpoints.TradingRest, options.BaseUrl);
        Assert.Equal(endpoints.TradingStream, options.ResolveTradingStreamUrl());
    }

    [Fact]
    public void LiveProfileResolvesOnlyLiveTradingEndpoints()
    {
        var endpoints = AlpacaEndpointResolver.Resolve(ProductionProfile.Live);
        var options = AlpacaOptions.Create(ProductionProfile.Live);

        Assert.Equal("https://api.alpaca.markets/", endpoints.TradingRest.AbsoluteUri);
        Assert.Equal("wss://api.alpaca.markets/stream", endpoints.TradingStream.AbsoluteUri);
        Assert.Equal(endpoints.TradingRest, options.BaseUrl);
        Assert.Equal(endpoints.TradingStream, options.ResolveTradingStreamUrl());
    }

    [Theory]
    [InlineData(ProductionProfile.Development)]
    [InlineData(ProductionProfile.Paper)]
    [InlineData(ProductionProfile.Live)]
    public void EveryProfileUsesSharedSipMarketDataEndpoints(ProductionProfile profile)
    {
        var endpoints = AlpacaEndpointResolver.Resolve(profile);
        var options = AlpacaOptions.Create(profile) with { MarketDataFeed = "sip" };

        Assert.Equal("https://data.alpaca.markets/", endpoints.MarketDataRest.AbsoluteUri);
        Assert.Equal("wss://stream.data.alpaca.markets/v2/sip", options.ResolveMarketDataStreamUrl().AbsoluteUri);
    }

    [Fact]
    public void OptionsDoNotExposeAConfigurableTradingBaseUrl()
    {
        var baseUrl = typeof(AlpacaOptions).GetProperty(nameof(AlpacaOptions.BaseUrl));

        Assert.NotNull(baseUrl);
        Assert.False(baseUrl!.CanWrite);
        Assert.DoesNotContain(
            typeof(AlpacaOptions).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(Uri));
    }

    [Fact]
    public void ProductionSourceCannotBypassProfileEndpointResolver()
    {
        var root = TestRepository.FindRoot();
        var sourceRoot = Path.Combine(root, "src");
        var resolverPath = Path.GetFullPath(Path.Combine(sourceRoot, "TradingFlow.Alpaca", "AlpacaEndpointResolver.cs"));
        var forbiddenHosts = new[]
        {
            "paper-api.alpaca.markets",
            "api.alpaca.markets",
            "data.alpaca.markets",
            "stream.data.alpaca.markets"
        };

        var productionFiles = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}TradingFlow.Tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}TradingFlow.Etoro{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFullPath(path).Equals(resolverPath, StringComparison.OrdinalIgnoreCase));

        foreach (var path in productionFiles)
        {
            var source = File.ReadAllText(path);
            foreach (var host in forbiddenHosts)
            {
                Assert.DoesNotContain(host, source, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
