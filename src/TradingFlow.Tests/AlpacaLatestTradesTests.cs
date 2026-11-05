using System.Net;
using TradingFlow.Alpaca;

namespace TradingFlow.Tests;

/// <summary>The latest-trades read behind the live price on the earnings cards.</summary>
public sealed class AlpacaLatestTradesTests : IDisposable
{
    private readonly OwnedAlpacaTestArchive archive = new();

    /// <summary>Releases the unique synthetic archive even when a test fails.</summary>
    public void Dispose() => archive.Dispose();

    /// <summary>
    /// One request carries every stock in Alpaca's dot notation on the configured feed. Each trade comes back
    /// under the ticker as it was requested, a stock Alpaca has no trade for is left out, and a symbol that
    /// was not asked for is ignored.
    /// </summary>
    [Fact]
    public async Task Latest_trades_are_keyed_by_the_requested_ticker()
    {
        using var handler = new LatestTradesHandler("""
            {
              "trades": {
                "SONY": { "t": "2026-11-05T18:08:41.123456789Z", "p": 27.565, "s": 100 },
                "BRK.B": { "t": "2026-11-05T13:08:41.5-05:00", "p": 480.10, "s": 5 },
                "EXTRA": { "t": "2026-11-05T18:00:00Z", "p": 1.0, "s": 1 }
              }
            }
            """);
        using var http = new HttpClient(handler);
        var provider = new AlpacaMarketDataProvider(http,
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
            {
                KeyId = "fixture", SecretKey = "fixture", MarketDataFeed = "sip"
            },
            archive.Writer);

        var trades = await provider.GetLatestTradesAsync(["SONY", "brk-b", "QUIET"], CancellationToken.None);

        var uri = Assert.Single(handler.RequestUris);
        Assert.Equal("/v2/stocks/trades/latest", uri.AbsolutePath);
        Assert.Contains("symbols=SONY,BRK.B,QUIET", Uri.UnescapeDataString(uri.Query));
        Assert.Contains("feed=sip", uri.Query);
        Assert.Equal(["SONY", "brk-b"], trades.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(27.565m, trades["sony"].Price);
        Assert.Equal(DateTimeOffset.Parse("2026-11-05T18:08:41.1234567Z"), trades["SONY"].TradedAtUtc);
        Assert.Equal(TimeSpan.Zero, trades["BRK-B"].TradedAtUtc.Offset);
        Assert.Equal(DateTimeOffset.Parse("2026-11-05T18:08:41.5Z"), trades["BRK-B"].TradedAtUtc);
    }

    /// <summary>An error status is reported to the caller instead of being read as "no trades".</summary>
    [Fact]
    public async Task An_error_status_throws()
    {
        using var handler = new LatestTradesHandler("""{"message":"forbidden"}""", HttpStatusCode.Forbidden);
        using var http = new HttpClient(handler);
        var provider = new AlpacaMarketDataProvider(http,
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with { KeyId = "fixture", SecretKey = "fixture" },
            archive.Writer);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetLatestTradesAsync(["SONY"], CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    /// <summary>Answers every request with the same body and status, recording each request address.</summary>
    private sealed class LatestTradesHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
