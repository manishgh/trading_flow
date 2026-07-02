using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TradingFlow.Web.Services;

public sealed partial class ArticleTextFetcher
{
    private const int MaxTextCharacters = 6000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);
    private readonly HttpClient httpClient;
    private readonly ILogger<ArticleTextFetcher> logger;

    public ArticleTextFetcher(ILogger<ArticleTextFetcher> logger)
    {
        this.logger = logger;
        httpClient = new HttpClient
        {
            Timeout = RequestTimeout
        };
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 TradingFlow.News/1.0");
    }

    public async Task<string?> FetchTextAsync(string? url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        try
        {
            using var response = await httpClient.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null &&
                !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return ExtractReadableText(html);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Article text fetch timed out for {Url}.", url);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Article text fetch failed for {Url}.", url);
            return null;
        }
    }

    private static string? ExtractReadableText(string html)
    {
        if (String.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var articleBody = ExtractJsonLdArticleBody(html);
        if (!String.IsNullOrWhiteSpace(articleBody))
        {
            return Clamp(articleBody);
        }

        var metaDescription = ExtractMetaDescription(html);
        if (!String.IsNullOrWhiteSpace(metaDescription))
        {
            return Clamp(metaDescription);
        }

        var paragraphText = ExtractParagraphText(html);
        if (!String.IsNullOrWhiteSpace(paragraphText))
        {
            return Clamp(paragraphText);
        }

        var text = ScriptAndStyleRegex().Replace(html, " ");
        text = HtmlTagRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        if (text.Length == 0)
        {
            return null;
        }

        return Clamp(text);
    }

    private static string? ExtractJsonLdArticleBody(string html)
    {
        foreach (Match match in ArticleBodyRegex().Matches(html))
        {
            var raw = match.Groups["body"].Value;
            try
            {
                var decoded = JsonSerializer.Deserialize<string>($"\"{raw}\"");
                decoded = NormalizeText(decoded);
                if (!String.IsNullOrWhiteSpace(decoded) && decoded.Length >= 80)
                {
                    return decoded;
                }
            }
            catch (JsonException)
            {
                // Some publishers emit non-standard JSON-LD; fall back to meta/paragraph extraction.
            }
        }

        return null;
    }

    private static string? ExtractMetaDescription(string html)
    {
        foreach (Match match in MetaTagRegex().Matches(html))
        {
            var tag = match.Value;
            if (!tag.Contains("description", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = ContentAttributeRegex().Match(tag);
            if (!content.Success)
            {
                continue;
            }

            var text = NormalizeText(content.Groups["content"].Value);
            if (!String.IsNullOrWhiteSpace(text) && text.Length >= 40)
            {
                return text;
            }
        }

        return null;
    }

    private static string? ExtractParagraphText(string html)
    {
        var paragraphs = ParagraphRegex()
            .Matches(html)
            .Select(match => NormalizeText(HtmlTagRegex().Replace(match.Groups["text"].Value, " ")))
            .Where(text => !String.IsNullOrWhiteSpace(text) && text.Length >= 60)
            .Take(8)
            .ToArray();
        return paragraphs.Length == 0 ? null : String.Join(" ", paragraphs);
    }

    private static string? NormalizeText(string? value)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return WhitespaceRegex().Replace(WebUtility.HtmlDecode(value), " ").Trim();
    }

    private static string Clamp(string text)
    {
        return text.Length <= MaxTextCharacters ? text : text[..MaxTextCharacters];
    }

    [GeneratedRegex("<script[\\s\\S]*?</script>|<style[\\s\\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptAndStyleRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("\"articleBody\"\\s*:\\s*\"(?<body>(?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ArticleBodyRegex();

    [GeneratedRegex("<meta\\s+[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTagRegex();

    [GeneratedRegex("content\\s*=\\s*[\"'](?<content>[^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex ContentAttributeRegex();

    [GeneratedRegex("<p\\b[^>]*>(?<text>[\\s\\S]*?)</p>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphRegex();
}
