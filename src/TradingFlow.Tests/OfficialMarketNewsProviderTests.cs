using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class OfficialMarketNewsProviderTests
{
    [Fact]
    public void FederalReserveRss_ParsesPublicationAsUtcMarketEvidence()
    {
        const string xml = """
            <rss><channel><item>
              <title>Federal Reserve issues FOMC statement</title>
              <link>https://www.federalreserve.gov/newsevents/pressreleases/monetary20260731a.htm</link>
              <guid>fed-1</guid>
              <description>Policy statement.</description>
              <pubDate>Fri, 31 Jul 2026 14:00:00 -0400</pubDate>
            </item></channel></rss>
            """;

        var result = OfficialMarketNewsProvider.ParseFederalReserveRss(
            xml,
            DateTimeOffset.Parse("2026-07-31T17:00:00Z"));

        var item = Assert.Single(result);
        Assert.Equal("MARKET", item.Ticker);
        Assert.Equal("federal_reserve", item.Provider);
        Assert.Equal(DateTimeOffset.Parse("2026-07-31T18:00:00Z"), item.Timestamp);
        Assert.Equal(TimeSpan.Zero, item.Timestamp.Offset);
    }

    [Fact]
    public void SecAtom_MapsRelevantFilingToAllowedEarningsTicker()
    {
        const string xml = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <title>8-K - Microsoft Corp (0000789019) (Filer)</title>
                <updated>2026-07-31T20:15:00-04:00</updated>
                <id>urn:sec:msft-8k</id>
                <link rel="alternate" href="https://www.sec.gov/Archives/edgar/data/789019/example.htm" />
              </entry>
              <entry>
                <title>4 - Microsoft Corp (0000789019) (Filer)</title>
                <updated>2026-07-31T20:16:00-04:00</updated>
                <id>urn:sec:ignored-form</id>
              </entry>
            </feed>
            """;
        var map = new Dictionary<string, string> { ["789019"] = "MSFT" };

        var result = OfficialMarketNewsProvider.ParseSecAtom(
            xml,
            map,
            ["MSFT"],
            DateTimeOffset.Parse("2026-07-31T00:00:00Z"));

        var item = Assert.Single(result);
        Assert.Equal("MSFT", item.Ticker);
        Assert.Equal("sec_edgar", item.Provider);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T00:15:00Z"), item.Timestamp);
        Assert.Equal(TimeSpan.Zero, item.Timestamp.Offset);
    }

    [Fact]
    public void SecTickerMap_UsesUnpaddedCikKeys()
    {
        const string json = """{"0":{"cik_str":789019,"ticker":"MSFT","title":"MICROSOFT CORP"}}""";

        var result = OfficialMarketNewsProvider.ParseSecTickerMap(json);

        Assert.Equal("MSFT", result["789019"]);
    }
}
