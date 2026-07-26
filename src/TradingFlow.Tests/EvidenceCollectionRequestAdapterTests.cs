using TradingFlow.Data.Evidence.Collection;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class EvidenceCollectionRequestAdapterTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 1, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public void AlpacaBarsBuilder_EmitsEveryPointInTimeControlExplicitly()
    {
        var request = new EvidenceCollectionRequest(
            "bars",
            "alpaca",
            "/v2/stocks/bars",
            ["MSFT", "AAPL"],
            Start,
            Start.AddDays(1),
            "sip",
            "split",
            "USD",
            new DateOnly(2026, 7, 2),
            new Dictionary<string, string>
            {
                ["timeframe"] = "5Min",
                ["limit"] = "9000",
                ["sort"] = "asc"
            });

        using var message = new AlpacaBarsEvidenceRequestAdapter(
                new Uri("https://example.test"))
            .CreateRequest(request, "opaque-page");
        var query = ParseQuery(message.RequestUri!);

        Assert.Equal("AAPL,MSFT", query["symbols"]);
        Assert.Equal("5Min", query["timeframe"]);
        Assert.Equal(Start.ToString("O"), query["start"]);
        Assert.Equal(Start.AddDays(1).AddTicks(-1).ToString("O"), query["end"]);
        Assert.Equal("sip", query["feed"]);
        Assert.Equal("split", query["adjustment"]);
        Assert.Equal("USD", query["currency"]);
        Assert.Equal("2026-07-02", query["asof"]);
        Assert.Equal("asc", query["sort"]);
        Assert.Equal("9000", query["limit"]);
        Assert.Equal("opaque-page", query["page_token"]);
    }

    [Fact]
    public void AlpacaNewsBuilder_UsesOnlySupportedNewsParameters()
    {
        var request = new EvidenceCollectionRequest(
            "news",
            "alpaca",
            "/v1beta1/news",
            ["NVDA"],
            Start,
            Start.AddHours(4),
            "sip",
            "raw",
            "USD",
            new DateOnly(2026, 7, 1),
            new Dictionary<string, string>
            {
                ["limit"] = "50",
                ["sort"] = "asc",
                ["include_content"] = "true",
                ["exclude_contentless"] = "true"
            });

        using var message = new AlpacaNewsEvidenceRequestAdapter(
                new Uri("https://example.test"))
            .CreateRequest(request, null);
        var query = ParseQuery(message.RequestUri!);

        Assert.Equal("NVDA", query["symbols"]);
        Assert.Equal("50", query["limit"]);
        Assert.Equal("asc", query["sort"]);
        Assert.Equal(Start.AddHours(4).AddTicks(-1).ToString("O"), query["end"]);
        Assert.Equal("true", query["include_content"]);
        Assert.Equal("true", query["exclude_contentless"]);
        Assert.DoesNotContain("feed", query.Keys);
        Assert.DoesNotContain("adjustment", query.Keys);
        Assert.DoesNotContain("currency", query.Keys);
        Assert.DoesNotContain("asof", query.Keys);
    }

    [Fact]
    public async Task AlpacaCalendarBuilder_UsesInclusiveProviderDatesAndNoPagination()
    {
        var request = new EvidenceCollectionRequest(
            "calendar",
            "alpaca",
            "/v2/calendar",
            [],
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.Zero),
            "alpaca-trading",
            "raw",
            "USD",
            new DateOnly(2026, 7, 8));
        var adapter = new AlpacaExchangeCalendarEvidenceRequestAdapter(
            new Uri("https://paper-api.alpaca.markets"));

        using var message = adapter.CreateRequest(request, null);
        var query = ParseQuery(message.RequestUri!);
        var metadata = await adapter.ReadPublishedPageMetadataAsync(
            null!,
            """[{"date":"2026-07-01","open":"09:30","close":"16:00"}]"""u8.ToArray(),
            CancellationToken.None);

        Assert.Equal("paper-api.alpaca.markets", message.RequestUri!.Host);
        Assert.Equal("/v2/calendar", message.RequestUri.AbsolutePath);
        Assert.Equal("2026-07-01", query["start"]);
        Assert.Equal("2026-07-07", query["end"]);
        Assert.Null(metadata.NextPageToken);
        Assert.Throws<InvalidOperationException>(() =>
            adapter.CreateRequest(request, "unexpected-page"));
    }

    [Fact]
    public void FinvizBuilder_PreservesRawEndpointAndSafeScreenParameters()
    {
        var request = new EvidenceCollectionRequest(
            "finviz",
            "finviz",
            "/export.ashx",
            [],
            null,
            null,
            "finviz-elite",
            "raw",
            "USD",
            new DateOnly(2026, 7, 1),
            new Dictionary<string, string>
            {
                ["v"] = "111",
                ["f"] = "cap_mega,sh_avgvol_o500"
            });

        using var message = new FinvizRawEvidenceRequestAdapter()
            .CreateRequest(request, null);
        var query = ParseQuery(message.RequestUri!);

        Assert.Equal("elite.finviz.com", message.RequestUri!.Host);
        Assert.Equal("/export.ashx", message.RequestUri.AbsolutePath);
        Assert.Equal("111", query["v"]);
        Assert.Equal("cap_mega,sh_avgvol_o500", query["f"]);
    }

    [Theory]
    [InlineData(200, EvidenceHttpStatusDisposition.Success)]
    [InlineData(206, EvidenceHttpStatusDisposition.Success)]
    [InlineData(408, EvidenceHttpStatusDisposition.Retryable)]
    [InlineData(425, EvidenceHttpStatusDisposition.Retryable)]
    [InlineData(429, EvidenceHttpStatusDisposition.Retryable)]
    [InlineData(503, EvidenceHttpStatusDisposition.Retryable)]
    [InlineData(400, EvidenceHttpStatusDisposition.Quarantine)]
    [InlineData(401, EvidenceHttpStatusDisposition.Quarantine)]
    public void StatusClassifier_IsExplicit(int status, EvidenceHttpStatusDisposition expected) =>
        Assert.Equal(expected, EvidenceHttpStatusClassifier.Classify((System.Net.HttpStatusCode)status));

    private static IReadOnlyDictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => Uri.UnescapeDataString(part.Length == 2 ? part[1] : String.Empty),
                StringComparer.Ordinal);
}
