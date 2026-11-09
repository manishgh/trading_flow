using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Desk;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

public sealed class AlpacaDisplaySnapshotTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T15:00:00Z");

    // The same response supplies an intraday volume and a separate completed-session close.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parser_CombinesFactsAndPreservesVolumeProvenance(bool wrapped)
    {
        var body = SnapshotBody(["mu"], 12345m);
        if (wrapped) body = "{\"snapshots\":" + body + "}";
        var value = AlpacaQuoteService.ParseDisplaySnapshots(body, new(2026, 9, 29), Now, " SIP ")["MU"];
        Assert.Equal(100m, value.PreviousClose!.Close);
        Assert.Equal(new DateOnly(2026, 9, 28), value.PreviousClose.SessionDate);
        var volume = Assert.IsType<AlpacaSessionVolume>(value.SessionVolume);
        Assert.Equal("MU", volume.Ticker); Assert.Equal(12345m, volume.Volume);
        Assert.Equal(DateTimeOffset.Parse("2026-09-29T04:00:00Z"), volume.BarTimestamp);
        Assert.Equal(new DateOnly(2026, 9, 29), volume.SessionDate);
        Assert.Equal(Now, volume.AsOfUtc); Assert.Equal("sip", volume.Feed);
    }

    // Missing or unusable daily volume remains unknown, even with a valid previous bar volume.
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"t\":\"2026-09-29T04:00:00Z\"}")]
    [InlineData("{\"t\":\"2026-09-29T04:00:00Z\",\"v\":null}")]
    [InlineData("{\"t\":\"2026-09-29T04:00:00Z\",\"v\":-1}")]
    [InlineData("{\"t\":\"2026-09-29T04:00:00Z\",\"v\":\"NaN\"}")]
    [InlineData("{\"t\":\"2026-09-29T04:00:00Z\",\"v\":1e100}")]
    [InlineData("{\"t\":\"invalid\",\"v\":1}")]
    [InlineData("{\"t\":\"2026-09-29T15:00:01Z\",\"v\":1}")]
    public void Parser_RejectsInvalidVolumeWithoutFallingBack(string daily)
    {
        var body = "{\"MU\":{\"dailyBar\":" + daily + ",\"prevDailyBar\":{\"t\":\"2026-09-28T04:00:00Z\",\"c\":100,\"v\":900}}}";
        var value = AlpacaQuoteService.ParseDisplaySnapshots(body, new(2026, 9, 29), Now, "sip")["MU"];
        Assert.Null(value.SessionVolume); Assert.Equal(100m, value.PreviousClose!.Close);
    }

    // Explicit zero is valid; a past daily bar retains its real New York session date over weekends.
    [Theory]
    [InlineData("0", 0)]
    [InlineData("\"2500\"", 2500)]
    public void Parser_PreservesZeroAndPastSessionDate(string shares, int expected)
    {
        var body = "{\"MU\":{\"dailyBar\":{\"t\":\"2026-09-25T04:00:00Z\",\"c\":100,\"v\":" + shares + "}}}";
        var receipt = DateTimeOffset.Parse("2026-09-27T15:00:00Z");
        var value = AlpacaQuoteService.ParseDisplaySnapshots(body, new(2026, 9, 27), receipt, "sip")["MU"];
        Assert.Equal((decimal)expected, value.SessionVolume!.Volume);
        Assert.Equal(new DateOnly(2026, 9, 25), value.SessionVolume.SessionDate);
        Assert.Equal(receipt, value.SessionVolume.AsOfUtc);
    }

    // Volume refreshes after 30 seconds while the existing previous-close API remains cached for the day.
    [Fact]
    public async Task Reader_RefreshesVolumeAndSharesTheExistingPreviousCloseCache()
    {
        var clock = new CandidateWorkflowTestClock(Now);
        using var handler = new SnapshotHandler(); using var client = new HttpClient(handler) { BaseAddress = new("https://data.alpaca.markets") };
        var service = Service(client, clock);
        var first = (await service.GetDisplaySnapshotsAsync(["MU"], "sip", clock.Now, default))["MU"];
        clock.Now = Now.AddSeconds(29); handler.Volume = 222m;
        var cached = (await service.GetDisplaySnapshotsAsync(["mu", " MU "], "sip", clock.Now, default))["MU"];
        Assert.Equal(first.SessionVolume, cached.SessionVolume); Assert.Single(handler.Requests);
        clock.Now = Now.AddSeconds(30);
        var refreshed = (await service.GetDisplaySnapshotsAsync(["MU"], "sip", clock.Now, default))["MU"];
        Assert.Equal(222m, refreshed.SessionVolume!.Volume); Assert.Equal(clock.Now, refreshed.SessionVolume.AsOfUtc);
        Assert.Equal(2, handler.Requests.Count);
        var close = await service.GetPreviousClosesAsync(["MU"], "sip", Now.AddHours(1), default);
        Assert.Equal(100m, close["MU"].Close); Assert.Equal(2, handler.Requests.Count);
    }

    // A failed refresh drops volume instead of renewing an old receipt, and suppresses immediate poll retries.
    [Fact]
    public async Task Reader_NegativeCachesFailuresWithoutInventingFreshVolume()
    {
        var clock = new CandidateWorkflowTestClock(Now);
        using var handler = new SnapshotHandler(); using var client = new HttpClient(handler) { BaseAddress = new("https://data.alpaca.markets") };
        var service = Service(client, clock);
        await service.GetDisplaySnapshotsAsync(["MU"], "sip", clock.Now, default);
        clock.Now = Now.AddSeconds(30); handler.Fail = true;
        var failed = (await service.GetDisplaySnapshotsAsync(["MU"], "sip", clock.Now, default))["MU"];
        Assert.Null(failed.SessionVolume); Assert.Equal(100m, failed.PreviousClose!.Close);
        clock.Now = Now.AddSeconds(35); handler.Fail = false;
        Assert.Null((await service.GetDisplaySnapshotsAsync(["MU"], "sip", clock.Now, default))["MU"].SessionVolume);
        Assert.Equal(2, handler.Requests.Count);
        clock.Now = Now.AddSeconds(40);
        Assert.NotNull((await service.GetDisplaySnapshotsAsync(["MU"], "sip", clock.Now, default))["MU"].SessionVolume);
        Assert.Equal(3, handler.Requests.Count);
    }

    // Concurrent pages share a single batch read rather than fetching the same symbols in parallel.
    [Fact]
    public async Task Reader_CoalescesConcurrentReads()
    {
        var clock = new CandidateWorkflowTestClock(Now);
        using var handler = new SnapshotHandler { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var client = new HttpClient(handler) { BaseAddress = new("https://data.alpaca.markets") };
        var service = Service(client, clock);
        var first = service.GetDisplaySnapshotsAsync(["MU"], "sip", clock.Now, default);
        await handler.Entered.Task;
        var second = service.GetDisplaySnapshotsAsync(["MU"], "sip", clock.Now, default);
        handler.Release!.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Single(handler.Requests); Assert.Equal(results[0]["MU"], results[1]["MU"]);
    }

    // Feed and New York date identify separate cache entries even when the 30-second lifetime has not elapsed.
    [Fact]
    public async Task Reader_IsolatesFeedsAndNewYorkDates()
    {
        var clock = new CandidateWorkflowTestClock(DateTimeOffset.Parse("2026-09-30T03:59:55Z"));
        using var handler = new SnapshotHandler(); using var client = new HttpClient(handler) { BaseAddress = new("https://data.alpaca.markets") };
        var service = Service(client, clock);
        Assert.Equal("sip", (await service.GetDisplaySnapshotsAsync(["MU"], "sip", clock.Now, default))["MU"].SessionVolume!.Feed);
        Assert.Equal("iex", (await service.GetDisplaySnapshotsAsync(["MU"], "iex", clock.Now, default))["MU"].SessionVolume!.Feed);
        clock.Now = clock.Now.AddSeconds(10);
        await service.GetDisplaySnapshotsAsync(["MU"], "sip", clock.Now, default);
        Assert.Equal(3, handler.Requests.Count);
    }

    // Large selections use batches of at most 100; a bad symbol does not hide the other symbols or trigger per-row reads.
    [Fact]
    public async Task Reader_BatchesAndIsolatesInvalidSymbols()
    {
        var clock = new CandidateWorkflowTestClock(Now);
        using var handler = new SnapshotHandler(); using var client = new HttpClient(handler) { BaseAddress = new("https://data.alpaca.markets") };
        var service = Service(client, clock);
        var symbols = new[] { "BAD" }.Concat(Enumerable.Range(0, 104).Select(i => "STOCK" + i)).ToArray();
        var results = await service.GetDisplaySnapshotsAsync(symbols, "sip", clock.Now, default);
        Assert.Equal(105, results.Count); Assert.Null(results["BAD"].SessionVolume);
        Assert.All(results.Where(pair => pair.Key != "BAD"), pair => Assert.Equal(123m, pair.Value.SessionVolume!.Volume));
        Assert.Equal(3, handler.Requests.Count); Assert.All(handler.Requests, batch => Assert.InRange(batch.Length, 1, 100));
        await service.GetDisplaySnapshotsAsync(symbols, "sip", clock.Now, default); Assert.Equal(3, handler.Requests.Count);
    }

    // The wire projection appends dated session volume without requiring it on older rows.
    [Fact]
    public void DeskRow_UsesTheOptionalDatedSessionVolumeContract()
    {
        var row = new DeskRow(new("MU", null, []), null, "unavailable", null, null, false, [], new("loading", "Loading"));
        Assert.Null(row.SessionVolume);
        var volume = new AlpacaSessionVolume("MU", 123m, Now.AddHours(-11), new(2026, 9, 29), Now, "sip");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(row with { SessionVolume = volume }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var projected = json.RootElement.GetProperty("sessionVolume");
        Assert.Equal(123m, projected.GetProperty("volume").GetDecimal());
        Assert.Equal("2026-09-29", projected.GetProperty("sessionDate").GetString());
        Assert.Equal(Now, projected.GetProperty("asOfUtc").GetDateTimeOffset());
        Assert.Equal(volume.BarTimestamp, projected.GetProperty("barTimestamp").GetDateTimeOffset());
        Assert.Equal("sip", projected.GetProperty("feed").GetString());
    }

    // Build one response for every requested symbol with deliberately distinct current and previous-day facts.
    private static string SnapshotBody(IEnumerable<string> symbols, decimal volume) => JsonSerializer.Serialize(symbols.ToDictionary(
        symbol => symbol, _ => new { dailyBar = new { t = "2026-09-29T04:00:00Z", c = 110, v = volume },
            prevDailyBar = new { t = "2026-09-28T04:00:00Z", c = 100, v = 900 } }));

    // The production reader uses an injected clock and HTTP handler; tests never contact a provider.
    private static AlpacaQuoteService Service(HttpClient client, TimeProvider clock) => new(
        new AlpacaCredentialProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Alpaca:KeyId"] = "fixture", ["Alpaca:SecretKey"] = "fixture" }).Build()),
        new Factory(client), NullLogger<AlpacaQuoteService>.Instance, clock: clock);

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        // Reuse the owned synthetic transport for every client request.
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class SnapshotHandler : HttpMessageHandler
    {
        public List<string[]> Requests { get; } = [];
        public decimal Volume { get; set; } = 123m;
        public bool Fail { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release { get; init; }

        // Capture the batch and emulate errors without creating any persisted or live market data.
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/v2/stocks/snapshots", request.RequestUri!.AbsolutePath);
            var symbols = Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?').Split('&').Single(x => x.StartsWith("symbols="))[8..]).Split(',');
            Requests.Add(symbols); Entered.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            if (Fail) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Unavailable") };
            if (symbols.Contains("BAD")) return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"message\":\"invalid symbol: BAD\"}") };
            return new(HttpStatusCode.OK) { Content = new StringContent(SnapshotBody(symbols, Volume)) };
        }
    }
}
