using System.Net;
using System.Text;
using System.Text.Json;
using TradingFlow.Engine.Storage;
using TradingFlow.Finviz;

namespace TradingFlow.Tests;

public sealed class FinvizClientTests : IDisposable
{
    private readonly string archiveRoot = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-finviz-archive-tests",
        Guid.NewGuid().ToString("N"));

    private IRawArchiveWriter CreateArchiveWriter() =>
        new FileSystemRawArchiveWriter(new RawArchiveOptions(archiveRoot));

    [Fact]
    public async Task GetScreenerRowsAsync_ParsesTickerAndRelativeVolume()
    {
        using var client = new FinvizClient(
            new HttpClient(new CsvHandler("""
No.,Ticker,Company,Rel Volume,Price
1,SPCE,Virgin Galactic,2.35,6.31
2,OUST,Ouster,-,40.50
""")),
            new FinvizOptions(new Uri("https://finviz.com"), "test"),
            CreateArchiveWriter());

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
            new FinvizOptions(new Uri("https://finviz.com"), "test"),
            CreateArchiveWriter());

        var tickers = await client.GetScreenerTickersAsync("v=111", CancellationToken.None);

        Assert.Equal(["RDW"], tickers);
    }

    [Fact]
    public async Task GetScreenerRowsAsync_AcceptsFullFinvizScreenerUrl()
    {
        var profilerScopeId = $"finviz-secret-test-{Guid.NewGuid():N}";
        using var profilerScope = TradingFlow.Domain.Logging.ApiProfiler.BeginScope(profilerScopeId);
        var handler = new CsvHandler("""
No.,Ticker,Company,Rel Volume
1,VELO,Velo3D,2.75
""");
        using var client = new FinvizClient(
            new HttpClient(handler),
            new FinvizOptions(new Uri("https://finviz.com"), "test-token"),
            CreateArchiveWriter());

        var rows = await client.GetScreenerRowsAsync("https://finviz.com/screener.ashx?v=111&f=sh_relvol_o2&auth=old-token", CancellationToken.None);

        Assert.Equal("VELO", rows.Single().Ticker);
        Assert.Equal("/export?v=111&f=sh_relvol_o2&auth=test-token", handler.LastRequestPathAndQuery);

        var payloadPath = Assert.Single(Directory.EnumerateFiles(archiveRoot, "*.csv", SearchOption.AllDirectories));
        Assert.Equal(handler.ResponseBytes, await File.ReadAllBytesAsync(payloadPath));
        var manifestPath = Assert.Single(Directory.EnumerateFiles(archiveRoot, "*.manifest.json", SearchOption.AllDirectories));
        var manifestText = await File.ReadAllTextAsync(manifestPath);
        Assert.DoesNotContain("test-token", manifestText, StringComparison.Ordinal);
        var manifest = JsonSerializer.Deserialize<RawArchiveManifest>(
            manifestText,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(manifest);
        Assert.StartsWith("adhoc-query-", manifest.PresetId, StringComparison.Ordinal);
        var profiler = TradingFlow.Domain.Logging.ApiProfiler.GetSummary("Finviz", profilerScopeId);
        Assert.Single(profiler.Endpoints);
        Assert.DoesNotContain("test-token", profiler.Endpoints[0].Route, StringComparison.Ordinal);
        Assert.DoesNotContain("auth=", profiler.Endpoints[0].Route, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetScreenerRowsAsync_ArchivesNonSuccessBodyBeforeThrowing()
    {
        var handler = new CsvHandler("rate limit response", HttpStatusCode.TooManyRequests);
        using var client = new FinvizClient(
            new HttpClient(handler),
            new FinvizOptions(new Uri("https://finviz.com"), "test-token"),
            CreateArchiveWriter());

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetScreenerRowsAsync("v=111", CancellationToken.None));

        var payloadPath = Assert.Single(Directory.EnumerateFiles(archiveRoot, "*.csv", SearchOption.AllDirectories));
        Assert.Equal(handler.ResponseBytes, await File.ReadAllBytesAsync(payloadPath));
        var manifestPath = Assert.Single(Directory.EnumerateFiles(archiveRoot, "*.manifest.json", SearchOption.AllDirectories));
        var manifest = JsonSerializer.Deserialize<RawArchiveManifest>(
            await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(manifest);
        Assert.Equal(429, manifest.Http!.StatusCode);
    }

    [Fact]
    public async Task GetScreenerRowsAsync_DoesNotParseWhenArchiveCommitFails()
    {
        using var client = new FinvizClient(
            new HttpClient(new CsvHandler("<html>invalid csv</html>")),
            new FinvizOptions(new Uri("https://finviz.com"), "test-token"),
            new FailingRawArchiveWriter());

        var exception = await Assert.ThrowsAsync<IOException>(
            () => client.GetScreenerRowsAsync("v=111", CancellationToken.None));

        Assert.Equal("simulated archive failure", exception.Message);
    }

    [Fact]
    public void ParseNewsExportCsv_MapsTickerColumnToSeparateEvents()
    {
        const string csv = """
"Title","Source","Date","Url","Category","Ticker"
"AI deal moves chip names","Reuters",2026-06-28 10:02:53,"https://example.com/news","Stock","NVDA,MU"
""";

        var items = FinvizClient.ParseNewsExportCsv(csv, "test-news");

        Assert.Equal(["NVDA", "MU"], items.Select(x => x.Ticker).ToArray());
        Assert.All(items, item =>
        {
            Assert.Equal("finviz", item.Provider);
            Assert.Equal("Reuters", item.Source);
            Assert.Equal("Stock", item.Summary);
            Assert.Equal(DateTimeOffset.Parse("2026-06-28T14:02:53Z"), item.Timestamp);
        });
    }

    [Fact]
    public void ParseNewsExportCsv_UsesMarketTickerWhenNoTickerColumnExists()
    {
        const string csv = """
"Title","Source","Date","Url","Category"
"Macro headline","MarketWatch",2026-06-28 09:42:35,"https://example.com/market","Market"
""";

        var item = Assert.Single(FinvizClient.ParseNewsExportCsv(csv, "test-market"));

        Assert.Equal("MARKET", item.Ticker);
        Assert.Equal("Macro headline", item.Headline);
        Assert.Equal("Market", item.Summary);
    }

    [Fact]
    public void ParseNewsExportCsv_UsesFallbackTitleWhenFinvizTitleIsBlank()
    {
        const string csv = """
"Title","Source","Date","Url","Category","Ticker"
"","Bloomberg",2026-06-28 18:33:58,"https://example.com/blank-title","Market",""
""";

        var item = Assert.Single(FinvizClient.ParseNewsExportCsv(csv, "test-blank"));

        Assert.Equal("MARKET", item.Ticker);
        Assert.Equal("Bloomberg update", item.Headline);
        Assert.Equal("Bloomberg", item.Source);
        Assert.Equal("Market", item.Summary);
    }

    private sealed class CsvHandler(
        string csv,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public byte[] ResponseBytes { get; } = Encoding.UTF8.GetBytes(csv);

        public string? LastRequestPathAndQuery { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestPathAndQuery = request.RequestUri?.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(ResponseBytes)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv") { CharSet = "utf-8" } }
                }
            });
        }
    }

    private sealed class FailingRawArchiveWriter : IRawArchiveWriter
    {
        public Task<RawArchiveReceipt> ArchiveAsync(
            RawArchiveRequest request,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default) =>
            Task.FromException<RawArchiveReceipt>(new IOException("simulated archive failure"));

        public Task<RawArchiveRetentionResult> EnforceRetentionAsync(
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(archiveRoot))
        {
            Directory.Delete(archiveRoot, recursive: true);
        }
    }
}
