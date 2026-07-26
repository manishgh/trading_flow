using System.Text;
using TradingFlow.Data.Evidence.Collection;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class AlpacaIdentityEvidenceRequestAdapterTests
{
    private static readonly DateTimeOffset Start = new(
        2026,
        6,
        1,
        0,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void Assets_UsesInjectedTradingHostAndCurrentSnapshotParameters()
    {
        var request = new EvidenceCollectionRequest(
            "assets",
            "alpaca",
            "https://untrusted.example/v2/assets",
            [],
            null,
            null,
            "alpaca-trading",
            "raw",
            "USD",
            new DateOnly(2026, 7, 2),
            new Dictionary<string, string>
            {
                ["status"] = "active",
                ["asset_class"] = "us_equity",
                ["exchange"] = "NASDAQ",
                ["attributes"] = "overnight_tradable,has_options"
            });

        using var message = new AlpacaAssetsEvidenceRequestAdapter(
                new Uri("https://paper.injected.test"))
            .CreateRequest(request, null);
        var query = ParseQuery(message.RequestUri!);

        Assert.Equal("paper.injected.test", message.RequestUri!.Host);
        Assert.Equal("/v2/assets", message.RequestUri.AbsolutePath);
        Assert.Equal("active", query["status"]);
        Assert.Equal("us_equity", query["asset_class"]);
        Assert.Equal("NASDAQ", query["exchange"]);
        Assert.Equal("has_options,overnight_tradable", query["attributes"]);
    }

    [Fact]
    public void Assets_RejectsHistoricalRangesSymbolsAndPagination()
    {
        var ranged = new EvidenceCollectionRequest(
            "assets",
            "alpaca",
            "/v2/assets",
            ["AAPL"],
            Start,
            Start.AddDays(1),
            "alpaca-trading",
            "raw",
            "USD",
            new DateOnly(2026, 7, 2));
        var adapter = new AlpacaAssetsEvidenceRequestAdapter(
            new Uri("https://paper.injected.test"));

        Assert.Throws<InvalidOperationException>(() => adapter.CreateRequest(ranged, null));
        Assert.Throws<InvalidOperationException>(() =>
            adapter.CreateRequest(
                AssetRequest(),
                "not-supported"));
    }

    [Fact]
    public async Task Assets_MetadataRequiresAnArray()
    {
        var adapter = new AlpacaAssetsEvidenceRequestAdapter(
            new Uri("https://paper.injected.test"));

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(async () =>
            await adapter.ReadPublishedPageMetadataAsync(
                null!,
                Encoding.UTF8.GetBytes("""{"assets":[]}"""),
                CancellationToken.None));
        var metadata = await adapter.ReadPublishedPageMetadataAsync(
            null!,
            Encoding.UTF8.GetBytes("[]"),
            CancellationToken.None);
        Assert.Null(metadata.NextPageToken);
    }

    [Fact]
    public void CorporateActions_UsesInjectedMarketDataHostAndDatePaginationContract()
    {
        var request = new EvidenceCollectionRequest(
            "actions",
            "alpaca",
            "https://untrusted.example/v1/corporate-actions",
            ["AAPL", "MSFT"],
            Start,
            Start.AddMonths(1),
            "alpaca-market-data",
            "raw",
            "USD",
            new DateOnly(2026, 7, 2),
            new Dictionary<string, string>
            {
                ["types"] = "name_change,forward_split,cash_dividend",
                ["limit"] = "500",
                ["sort"] = "desc"
            });

        using var message = new AlpacaCorporateActionsEvidenceRequestAdapter(
                new Uri("https://data.injected.test"))
            .CreateRequest(request, "opaque-page");
        var query = ParseQuery(message.RequestUri!);

        Assert.Equal("data.injected.test", message.RequestUri!.Host);
        Assert.Equal("/v1/corporate-actions", message.RequestUri.AbsolutePath);
        Assert.Equal("2026-06-01", query["start"]);
        Assert.Equal("2026-06-30", query["end"]);
        Assert.Equal("AAPL,MSFT", query["symbols"]);
        Assert.Equal("cash_dividend,forward_split,name_change", query["types"]);
        Assert.Equal("us", query["region"]);
        Assert.Equal("500", query["limit"]);
        Assert.Equal("desc", query["sort"]);
        Assert.Equal("opaque-page", query["page_token"]);
    }

    [Fact]
    public void CorporateActions_DefaultsToEveryDocumentedTypeAndRejectsUnknownTypes()
    {
        using var message = new AlpacaCorporateActionsEvidenceRequestAdapter(
                new Uri("https://data.injected.test"))
            .CreateRequest(CorporateActionRequest(), null);
        var types = ParseQuery(message.RequestUri!)["types"]
            .Split(',', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(15, types.Length);
        Assert.Contains("partial_call", types);
        Assert.Contains("reorganization", types);

        var invalid = CorporateActionRequest(
            new Dictionary<string, string> { ["types"] = "cash_dividend,unknown" });
        Assert.Throws<InvalidOperationException>(() =>
            new AlpacaCorporateActionsEvidenceRequestAdapter(
                    new Uri("https://data.injected.test"))
                .CreateRequest(invalid, null));
    }

    [Fact]
    public async Task CorporateActions_MetadataRejectsMalformedPagination()
    {
        var adapter = new AlpacaCorporateActionsEvidenceRequestAdapter(
            new Uri("https://data.injected.test"));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(async () =>
            await adapter.ReadPublishedPageMetadataAsync(
                null!,
                Encoding.UTF8.GetBytes(
                    """{"corporate_actions":{},"next_page_token":42}"""),
                CancellationToken.None));

        var metadata = await adapter.ReadPublishedPageMetadataAsync(
            null!,
            Encoding.UTF8.GetBytes(
                """{"corporate_actions":{},"next_page_token":"next"}"""),
            CancellationToken.None);
        Assert.Equal("next", metadata.NextPageToken);
    }

    private static EvidenceCollectionRequest AssetRequest() =>
        new(
            "assets",
            "alpaca",
            "/v2/assets",
            [],
            null,
            null,
            "alpaca-trading",
            "raw",
            "USD",
            new DateOnly(2026, 7, 2));

    private static EvidenceCollectionRequest CorporateActionRequest(
        IReadOnlyDictionary<string, string>? parameters = null) =>
        new(
            "actions",
            "alpaca",
            "/v1/corporate-actions",
            [],
            Start,
            Start.AddMonths(1),
            "alpaca-market-data",
            "raw",
            "USD",
            new DateOnly(2026, 7, 2),
            parameters);

    private static IReadOnlyDictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => Uri.UnescapeDataString(part.Length == 2 ? part[1] : String.Empty),
                StringComparer.Ordinal);
}
