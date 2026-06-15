using System.Net;
using TradingFlow.Finviz;

namespace TradingFlow.Tests;

public sealed class FinvizClientTests
{
    [Fact]
    public async Task GetScreenerRowsAsync_ParsesTickerAndRelativeVolume()
    {
        using var client = new FinvizClient(
            new HttpClient(new CsvHandler("""
No.,Ticker,Company,Rel Volume,Price
1,SPCE,Virgin Galactic,2.35,6.31
2,OUST,Ouster,-,40.50
""")),
            new FinvizOptions(new Uri("https://finviz.com"), "test"));

        var rows = await client.GetScreenerRowsAsync("v=111&f=sh_relvol_o2", CancellationToken.None);

        Assert.Equal(["SPCE", "OUST"], rows.Select(x => x.Ticker).ToArray());
        Assert.Equal(2.35m, rows[0].RelativeVolume);
        Assert.Null(rows[1].RelativeVolume);
    }

    [Fact]
    public async Task GetScreenerTickersAsync_PreservesExistingTickerOnlyContract()
    {
        using var client = new FinvizClient(
            new HttpClient(new CsvHandler("""
No.,Ticker,Company,Rel Volume
1,RDW,Redwire,3.10
""")),
            new FinvizOptions(new Uri("https://finviz.com"), "test"));

        var tickers = await client.GetScreenerTickersAsync("v=111", CancellationToken.None);

        Assert.Equal(["RDW"], tickers);
    }

    [Fact]
    public async Task GetScreenerRowsAsync_AcceptsFullFinvizScreenerUrl()
    {
        var handler = new CsvHandler("""
No.,Ticker,Company,Rel Volume
1,VELO,Velo3D,2.75
""");
        using var client = new FinvizClient(
            new HttpClient(handler),
            new FinvizOptions(new Uri("https://finviz.com"), "test-token"));

        var rows = await client.GetScreenerRowsAsync("https://finviz.com/screener.ashx?v=111&f=sh_relvol_o2&auth=old-token", CancellationToken.None);

        Assert.Equal("VELO", rows.Single().Ticker);
        Assert.Equal("/export?v=111&f=sh_relvol_o2&auth=test-token", handler.LastRequestPathAndQuery);
    }

    private sealed class CsvHandler(string csv) : HttpMessageHandler
    {
        public string? LastRequestPathAndQuery { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestPathAndQuery = request.RequestUri?.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(csv)
            });
        }
    }
}
