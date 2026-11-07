using System.Net;
using Microsoft.Extensions.Configuration;
using TradingFlow.Alpaca;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Finviz;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class ConfiguredDataProvidersTests
{
    /// <summary>Standalone tools accept only their own environment and preserve a deliberately blank secret.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("standalone-key")]
    public void StandaloneCredentials_DoNotBorrowAnotherHostsSettings(string? key)
    {
        var providers = ConfiguredDataProviders.FromEnvironment(name => name switch {
            "ALPACA_KEY_ID" => key, "ALPACA_SECRET_KEY" => "standalone-secret", _ => null
        });
        var options = providers.AlpacaOptionsFor(Run());
        Assert.Equal(key ?? String.Empty, options.KeyId);
        Assert.Equal("standalone-secret", options.SecretKey);
        Assert.Equal("otc", options.MarketDataFeed);
    }

    /// <summary>Paper and research adapters consume the same feed, host credential and article cap through real adapter HTTP parsing.</summary>
    [Fact]
    public async Task PaperAndResearch_UseIdenticalAcceptedProviderSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "tf-provider-composition-" + Guid.NewGuid().ToString("N"));
        using var handler = new ProviderHandler();
        var clients = new List<HttpClient>();
        // Each adapter owns its client, matching production construction while sharing only the recording handler.
        HttpClient CreateClient()
        {
            var client = new HttpClient(handler, disposeHandler: false);
            clients.Add(client);
            return client;
        }
        var factory = new ConfiguredDataProviders(
            () => AlpacaOptions.Create(ProductionProfile.Paper) with { KeyId = "host-key", SecretKey = "host-secret" },
            FinvizOptions.CreateDefault, () => null, CreateClient);
        var archive = new FileSystemRawArchiveWriter(new RawArchiveOptions(root));
        var paper = new PaperRuntimeFactory(new(new ConfigurationBuilder().Build(), _ => null), new(root),
            rawArchiveWriter: archive, dataProviders: factory);
        try
        {
            var run = Run();
            var markets = new[] { factory.CreateMarket(run, archive), paper.CreateProvider(run) };
            foreach (var market in markets)
            {
                var bars = new List<TradingFlow.Domain.Market.OhlcvBar>();
                await foreach (var bar in market.GetBarsAsync(["AAPL"], ["1d"],
                    DateTimeOffset.Parse("2026-06-09T00:00:00Z"), DateTimeOffset.Parse("2026-06-10T00:00:00Z"), default)) bars.Add(bar);
                Assert.Single(bars);
            }
            var news = new[] { factory.CreateNews(run, archive), paper.CreateNewsProvider(run) };
            foreach (var source in news)
            {
                var provider = Assert.IsAssignableFrom<ICatalystEvidenceProvider>(source);
                var result = await provider.GetCatalystEvidenceAsync("AAPL", DateTimeOffset.Parse("2026-06-09T13:00:00Z"),
                    DateTimeOffset.Parse("2026-06-09T15:00:00Z"), default);
                Assert.Single(result.Events);
                Assert.False(result.Complete);
                Assert.Contains("article limit", result.Detail);
            }
            Assert.Equal(4, handler.Calls);
            Assert.Equal(2, handler.MarketCalls);
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>Unsupported sources and disabled news fail or remain disabled before creating any HTTP transport.</summary>
    [Fact]
    public void UnsupportedOrDisabledSources_DoNotCreateNetworkClients()
    {
        var factory = new ConfiguredDataProviders(() => AlpacaOptions.Create(ProductionProfile.Paper), FinvizOptions.CreateDefault,
            () => null, () => throw new InvalidOperationException("No HTTP client should be created."));
        var archive = new Moq.Mock<IRawArchiveWriter>().Object;
        Assert.Null(factory.CreateNews(Run() with { News = Run().News with { Enabled = false } }, archive));
        Assert.Throws<NotSupportedException>(() => factory.CreateMarket(Run() with { Provider = "invalid" }, archive));
        Assert.Throws<NotSupportedException>(() => factory.CreateNews(Run() with { News = Run().News with { ProviderName = "invalid" } }, archive));
    }

    /// <summary>Raw execution policies retain their original source basis; ordinary research and paper keep adjusted prices.</summary>
    [Theory]
    [InlineData(null, "all")]
    [InlineData(ExecutionReplayPolicy.DailyRthFiveMinuteV1, "raw")]
    [InlineData(ExecutionReplayPolicy.DailyRthSipPortfolioV1, "raw")]
    [InlineData(ExecutionReplayPolicy.IntradayRthFiveMinuteNextBarMarketV1, "all")]
    public void ExecutionPolicy_SelectsTheActualProviderPriceBasis(string? executionPolicy, string expectedAdjustment)
    {
        using var http = new HttpClient();
        var factory = new ConfiguredDataProviders(() => AlpacaOptions.Create(ProductionProfile.Paper), FinvizOptions.CreateDefault,
            () => null, () => http);
        var run = Run();
        var market = factory.CreateMarket(run with { Engine = run.Engine with { ExecutionReplayPolicyId = executionPolicy } },
            new Moq.Mock<IRawArchiveWriter>().Object);
        Assert.Equal(expectedAdjustment, Assert.IsAssignableFrom<IMarketDataProvenanceProvider>(market).MarketDataProvenance.AdjustmentPolicy);
    }

    /// <summary>Provides only fields read by adapter composition; no trading or portfolio executor runs in this fixture.</summary>
    private static BacktestRunConfig Run() => new("provider-fixture", "backtest", new("bounded", 1, 1, 1, 10, true), null!, ["AAPL"], "alpaca", ["1d"],
        "raw", "normalized", "results", "bypass", null!, null!, new(new("otc")), null!, null!, null!,
        new(true, "alpaca", 60, -.5m, MaxArticlesPerTicker: 1, SentimentTimeoutSeconds: 1), null!, null!, []);

    private sealed class ProviderHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int MarketCalls { get; private set; }

        /// <summary>Checks outbound provider settings and returns deterministic local payloads instead of contacting Alpaca.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("host-key", request.Headers.GetValues("APCA-API-KEY-ID").Single());
            Assert.Equal("host-secret", request.Headers.GetValues("APCA-API-SECRET-KEY").Single());
            var market = request.RequestUri!.AbsolutePath.Contains("/bars", StringComparison.Ordinal);
            if (market)
            {
                MarketCalls++;
                Assert.Contains("feed=otc", request.RequestUri.Query);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(market ?
                """{"bars":{"AAPL":[{"t":"2026-06-09T04:00:00Z","o":100,"h":102,"l":99,"c":101,"v":1000}]},"next_page_token":null}""" :
                """{"news":[{"id":"composition-one","headline":"Company reports growth","created_at":"2026-06-09T14:00:00Z","symbols":["AAPL"]},{"id":"composition-two","headline":"Company update","created_at":"2026-06-09T14:01:00Z","symbols":["AAPL"]}],"next_page_token":null}""") });
        }
    }
}
