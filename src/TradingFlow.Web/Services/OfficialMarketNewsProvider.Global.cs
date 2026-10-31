using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TradingFlow.Domain.Market;

namespace TradingFlow.Web.Services;

public sealed partial class OfficialMarketNewsProvider
{
    internal const string UnitedNationsFeedUrl = "https://news.un.org/feed/subscribe/en/news/all/rss.xml";
    internal const string GovernmentDocumentsFeedUrl = "https://www.govinfo.gov/rss/dcpd.xml";
    private const int MaximumFeedBytes = 2 * 1024 * 1024;
    internal TimeSpan PublicContextRequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    private readonly TimeProvider newsClock;
    private readonly SemaphoreSlim supplementalGate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset ExpiresAt, IReadOnlyList<CatalystEvent> Items)> supplementalCache = [];

    // These are public context feeds. Their display categories never qualify a strategy.
    // Publication time controls the news window; refreshing an old document cannot make it recent.
    private static readonly (string Url, string Provider, string Source, TimeSpan Lifetime)[] SupplementalFeeds =
    [
        (UnitedNationsFeedUrl, "un_news", "UN News", TimeSpan.FromMinutes(5)),
        (GovernmentDocumentsFeedUrl, "us_government", "US Government Publishing Office", TimeSpan.FromHours(1))
    ];

    private async Task<IReadOnlyList<CatalystEvent>> GetSupplementalEventsAsync(
        DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        await supplementalGate.WaitAsync(cancellationToken);
        try
        {
            var now = newsClock.GetUtcNow();
            var pending = SupplementalFeeds.Where(feed =>
                !supplementalCache.TryGetValue(feed.Provider, out var cached) || cached.ExpiresAt <= now).ToArray();
            var fetched = await Task.WhenAll(pending.Select(async feed =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(PublicContextRequestTimeout);
                try
                {
                    using var response = await httpClient.GetAsync(feed.Url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is > MaximumFeedBytes)
                        throw new InvalidDataException("Public news feed exceeds its size limit.");
                    await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
                    using var output = new MemoryStream();
                    var buffer = new byte[8192];
                    int count;
                    while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0)
                    {
                        if (output.Length + count > MaximumFeedBytes)
                            throw new InvalidDataException("Public news feed exceeds its size limit.");
                        output.Write(buffer, 0, count);
                    }
                    var receivedAt = newsClock.GetUtcNow();
                    var items = ParsePublicRss(Encoding.UTF8.GetString(output.ToArray()), feed.Provider, feed.Source, receivedAt);
                    return (feed.Provider, ExpiresAt: receivedAt.Add(feed.Lifetime), Items: items);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    logger.LogWarning(error, "Public context news refresh failed for {Provider}; other feeds continue.", feed.Provider);
                    // Do not silently present a failed fetch as newly received evidence.
                    return (feed.Provider, ExpiresAt: now.AddMinutes(1), Items: (IReadOnlyList<CatalystEvent>)[]);
                }
            }));
            foreach (var entry in fetched)
                supplementalCache[entry.Provider] = (entry.ExpiresAt, entry.Items);
            var availableAt = newsClock.GetUtcNow();
            return supplementalCache.Values.SelectMany(value => value.Items)
                .Where(item => item.Timestamp >= sinceUtc && item.Timestamp <= availableAt)
                .OrderByDescending(item => item.Timestamp).ToArray();
        }
        finally { supplementalGate.Release(); }
    }

    internal static IReadOnlyList<CatalystEvent> ParsePublicRss(
        string xml, string provider, string source, DateTimeOffset receivedAtUtc)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumFeedBytes
        });
        var document = XDocument.Load(reader);
        return document.Descendants().Where(node => node.Name.LocalName is "item" or "entry")
            .Take(200).Select(node =>
            {
                string? Field(string name) => node.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value.Trim();
                var headline = PlainText(Field("title"));
                var publishedText = Field("pubDate") ?? Field("published");
                var linkNode = node.Elements().FirstOrDefault(child => child.Name.LocalName == "link" &&
                    (child.Attribute("rel") is null || child.Attribute("rel")?.Value == "alternate"));
                var url = linkNode?.Attribute("href")?.Value ?? linkNode?.Value.Trim();
                // Date-only publications and update-only dates cannot support a four-hour publication claim.
                if (String.IsNullOrWhiteSpace(headline) || String.IsNullOrWhiteSpace(publishedText) || !publishedText.Contains(':') ||
                    !DateTimeOffset.TryParse(publishedText, CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var published) ||
                    published > receivedAtUtc || !Uri.TryCreate(url, UriKind.Absolute, out var articleUri) ||
                    articleUri.Scheme is not ("https" or "http")) return null;
                return new CatalystEvent("MARKET", published.ToUniversalTime(), CatalystType.NewsReport,
                    headline, 0m, provider, Field("guid") ?? Field("id") ?? url,
                    PlainText(Field("description") ?? Field("summary")), source, articleUri.AbsoluteUri,
                    receivedAtUtc.ToUniversalTime());
            }).Where(item => item is not null).Cast<CatalystEvent>()
            .GroupBy(item => item.ExternalId, StringComparer.Ordinal).Select(group => group.First())
            .OrderByDescending(item => item.Timestamp).ToArray();
    }

    private static string? PlainText(string? text)
    {
        if (String.IsNullOrWhiteSpace(text)) return null;
        var plain = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", " "));
        return System.Text.RegularExpressions.Regex.Replace(plain, @"\s+", " ").Trim();
    }
}
