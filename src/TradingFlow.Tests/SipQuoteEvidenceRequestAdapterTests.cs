using TradingFlow.Data.Evidence.Collection;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class SipQuoteEvidenceRequestAdapterTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 6, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public void AlpacaQuotesBuilder_EmitsSipRangeSymbolsAndOpaquePageToken()
    {
        var request = Request("sip");

        using var message = new AlpacaQuotesEvidenceRequestAdapter(
                new Uri("https://example.test"))
            .CreateRequest(request, "next-token");
        var query = ParseQuery(message.RequestUri!);

        Assert.Equal("/v2/stocks/quotes", message.RequestUri!.AbsolutePath);
        Assert.Equal("MSFT,NVDA", query["symbols"]);
        Assert.Equal(Start.ToString("O"), query["start"]);
        Assert.Equal(Start.AddHours(2).AddTicks(-1).ToString("O"), query["end"]);
        Assert.Equal("sip", query["feed"]);
        Assert.Equal("USD", query["currency"]);
        Assert.Equal("asc", query["sort"]);
        Assert.Equal("10000", query["limit"]);
        Assert.Equal("next-token", query["page_token"]);
        Assert.DoesNotContain("adjustment", query.Keys);
        Assert.DoesNotContain("asof", query.Keys);
    }

    [Fact]
    public void AlpacaQuotesBuilder_RejectsNonSipEvidence()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new AlpacaQuotesEvidenceRequestAdapter(
                new Uri("https://example.test")).CreateRequest(Request("iex"), null));

        Assert.Contains("SIP", exception.Message, StringComparison.Ordinal);
    }

    private static EvidenceCollectionRequest Request(string feed) =>
        new(
            "quotes",
            "alpaca",
            "/v2/stocks/quotes",
            ["NVDA", "MSFT"],
            Start,
            Start.AddHours(2),
            feed,
            "raw",
            "USD",
            new DateOnly(2026, 7, 6),
            new Dictionary<string, string>
            {
                ["limit"] = "10000",
                ["sort"] = "asc"
            });

    private static IReadOnlyDictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => Uri.UnescapeDataString(part.Length == 2 ? part[1] : String.Empty),
                StringComparer.Ordinal);
}
