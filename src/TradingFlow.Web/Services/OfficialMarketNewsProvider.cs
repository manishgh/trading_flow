using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TradingFlow.Domain.Market;

namespace TradingFlow.Web.Services;

/// <summary>
/// Reads official Federal Reserve releases and SEC current filings. SEC access
/// is disabled unless the operator supplies an identifying SEC_USER_AGENT, as
/// required by SEC fair-access guidance.
/// </summary>
public sealed partial class OfficialMarketNewsProvider
{
    internal const string FederalReserveFeedUrl = "https://www.federalreserve.gov/feeds/press_monetary.xml";
    internal const string SecCurrentFilingsUrl = "https://www.sec.gov/cgi-bin/browse-edgar?action=getcurrent&owner=exclude&count=100&output=atom";
    internal const string SecCompanyTickersUrl = "https://www.sec.gov/files/company_tickers.json";

    private static readonly TimeSpan TickerMapLifetime = TimeSpan.FromHours(24);
    private static readonly HashSet<string> RelevantForms = new(StringComparer.OrdinalIgnoreCase)
    {
        "8-K", "10-Q", "10-K", "6-K", "20-F"
    };

    private readonly HttpClient httpClient;
    private readonly ILogger<OfficialMarketNewsProvider> logger;
    private readonly SemaphoreSlim tickerMapGate = new(1, 1);
    private IReadOnlyDictionary<string, string> tickerByCik = new Dictionary<string, string>();
    private DateTimeOffset tickerMapExpiresAtUtc;

    public OfficialMarketNewsProvider(HttpClient httpClient, ILogger<OfficialMarketNewsProvider> logger, TimeProvider? clock = null)
    {
        this.httpClient = httpClient;
        this.logger = logger;
        newsClock = clock ?? TimeProvider.System;
    }

    public bool IsSecConfigured => !String.IsNullOrWhiteSpace(
        Environment.GetEnvironmentVariable("SEC_USER_AGENT") ??
        Environment.GetEnvironmentVariable("SEC_USER_AGENT", EnvironmentVariableTarget.User));

    // Read each official source independently so one failure cannot masquerade as an empty official feed.
    internal async Task<IReadOnlyList<NewsCollection>> ReadSourcesAsync(
        IReadOnlyCollection<string> earningsTickers,
        DateTimeOffset windowStartUtc,
        CancellationToken cancellationToken)
    {
        var sources = new List<NewsCollection>();
        sources.Add(await ReadFederalReserveAsync(windowStartUtc, cancellationToken));
        sources.AddRange(await GetSupplementalEventsAsync(windowStartUtc, cancellationToken));
        sources.Add(await ReadSecAsync(earningsTickers, windowStartUtc, cancellationToken));
        return sources;
    }

    // SEC's current-filings endpoint is bounded; even a success does not cover the whole news period.
    private async Task<NewsCollection> ReadSecAsync(IReadOnlyCollection<string> earningsTickers,
        DateTimeOffset windowStartUtc, CancellationToken cancellationToken)
    {
        const string source = "SEC filings";
        var secUserAgent = Environment.GetEnvironmentVariable("SEC_USER_AGENT")
            ?? Environment.GetEnvironmentVariable("SEC_USER_AGENT", EnvironmentVariableTarget.User);
        if (String.IsNullOrWhiteSpace(secUserAgent))
            return NewsFeedService.NotQueried(source, "SEC_USER_AGENT is not configured.");

        try
        {
            var tickerMap = await GetTickerMapAsync(secUserAgent, cancellationToken);
            using var request = CreateSecRequest(SecCurrentFilingsUrl, secUserAgent);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            var events = ParseSecAtom(xml, tickerMap, earningsTickers, windowStartUtc);
            return new(events, new(source, "success", newsClock.GetUtcNow(), events.Count,
                "Current filings returned for the bounded feed and selected scope; full window coverage is unverified."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Official SEC current-filings refresh failed.");
            return new([], new(source, "failed", newsClock.GetUtcNow(), 0, NewsFeedService.NewsRequestFailure(exception)));
        }
    }

    // A recent RSS response is evidence of receipt, without a claim of historical completeness.
    private async Task<NewsCollection> ReadFederalReserveAsync(
        DateTimeOffset windowStartUtc,
        CancellationToken cancellationToken)
    {
        const string source = "Federal Reserve";
        try
        {
            var xml = await httpClient.GetStringAsync(FederalReserveFeedUrl, cancellationToken);
            var events = ParseFederalReserveRss(xml, windowStartUtc);
            return new(events, new(source, "success", newsClock.GetUtcNow(), events.Count,
                "Rolling monetary policy feed returned; earlier publications may be outside the feed."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Official Federal Reserve news refresh failed.");
            return new([], new(source, "failed", newsClock.GetUtcNow(), 0, NewsFeedService.NewsRequestFailure(exception)));
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> GetTickerMapAsync(
        string userAgent,
        CancellationToken cancellationToken)
    {
        if (tickerByCik.Count > 0 && tickerMapExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            return tickerByCik;
        }

        await tickerMapGate.WaitAsync(cancellationToken);
        try
        {
            if (tickerByCik.Count > 0 && tickerMapExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return tickerByCik;
            }

            using var request = CreateSecRequest(SecCompanyTickersUrl, userAgent);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            tickerByCik = ParseSecTickerMap(json);
            tickerMapExpiresAtUtc = DateTimeOffset.UtcNow.Add(TickerMapLifetime);
            return tickerByCik;
        }
        finally
        {
            tickerMapGate.Release();
        }
    }

    private static HttpRequestMessage CreateSecRequest(string url, string userAgent)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent.Trim());
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
        return request;
    }

    internal static IReadOnlyList<CatalystEvent> ParseFederalReserveRss(
        string xml,
        DateTimeOffset windowStartUtc)
    {
        var document = XDocument.Parse(xml);
        return document.Descendants("item")
            .Select(item =>
            {
                var timestamp = ParseTimestamp(item.Element("pubDate")?.Value);
                var headline = item.Element("title")?.Value.Trim();
                var url = item.Element("link")?.Value.Trim();
                var externalId = item.Element("guid")?.Value.Trim() ?? url;
                var summary = item.Element("description")?.Value.Trim();
                return timestamp is null || String.IsNullOrWhiteSpace(headline)
                    ? null
                    : new CatalystEvent(
                        "MARKET",
                        timestamp.Value.ToUniversalTime(),
                        CatalystType.NewsReport,
                        headline,
                        0m,
                        "federal_reserve",
                        externalId,
                        summary,
                        "Federal Reserve",
                        url,
                        DateTimeOffset.UtcNow);
            })
            .Where(item => item is not null && item.Timestamp >= windowStartUtc)
            .Cast<CatalystEvent>()
            .OrderByDescending(item => item.Timestamp)
            .ToArray();
    }

    internal static IReadOnlyDictionary<string, string> ParseSecTickerMap(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var company in document.RootElement.EnumerateObject())
        {
            if (!company.Value.TryGetProperty("cik_str", out var cikElement) ||
                !company.Value.TryGetProperty("ticker", out var tickerElement))
            {
                continue;
            }

            var ticker = tickerElement.GetString()?.Trim().ToUpperInvariant();
            if (!String.IsNullOrWhiteSpace(ticker))
            {
                result[cikElement.GetInt64().ToString(CultureInfo.InvariantCulture)] = ticker;
            }
        }

        return result;
    }

    internal static IReadOnlyList<CatalystEvent> ParseSecAtom(
        string xml,
        IReadOnlyDictionary<string, string> tickerByCik,
        IReadOnlyCollection<string> allowedTickers,
        DateTimeOffset windowStartUtc)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var allowed = allowedTickers
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var document = XDocument.Parse(xml);
        return document.Descendants(atom + "entry")
            .Select(entry =>
            {
                var title = entry.Element(atom + "title")?.Value.Trim();
                var updated = ParseTimestamp(entry.Element(atom + "updated")?.Value);
                var form = ExtractSecForm(title);
                var cikMatch = CikPattern().Match(title ?? String.Empty);
                var cik = cikMatch.Success ? cikMatch.Groups[1].Value.TrimStart('0') : null;
                cik = String.IsNullOrEmpty(cik) ? "0" : cik;
                var ticker = cik is not null && tickerByCik.TryGetValue(cik, out var mappedTicker)
                    ? mappedTicker
                    : null;
                var url = entry.Elements(atom + "link")
                    .FirstOrDefault(link => (string?)link.Attribute("rel") is "alternate" or null)
                    ?.Attribute("href")?.Value;
                var id = entry.Element(atom + "id")?.Value.Trim() ?? url;
                return updated is null || String.IsNullOrWhiteSpace(title) ||
                    String.IsNullOrWhiteSpace(ticker) || !RelevantForms.Contains(form) ||
                    !allowed.Contains(ticker)
                    ? null
                    : new CatalystEvent(
                        ticker,
                        updated.Value.ToUniversalTime(),
                        CatalystType.RegulatoryFiling,
                        title,
                        0m,
                        "sec_edgar",
                        id,
                        $"Official SEC {form} filing.",
                        "SEC EDGAR",
                        url,
                        DateTimeOffset.UtcNow);
            })
            .Where(item => item is not null && item.Timestamp >= windowStartUtc)
            .Cast<CatalystEvent>()
            .OrderByDescending(item => item.Timestamp)
            .ToArray();
    }

    private static string ExtractSecForm(string? title)
    {
        if (String.IsNullOrWhiteSpace(title))
        {
            return String.Empty;
        }

        return RelevantForms.FirstOrDefault(form =>
            title.StartsWith(form, StringComparison.OrdinalIgnoreCase) ||
            title.Contains($" {form} ", StringComparison.OrdinalIgnoreCase)) ?? String.Empty;
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var timestamp)
            ? timestamp
            : null;

    [GeneratedRegex(@"\((?:CIK\s*)?0*(\d+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CikPattern();
}
