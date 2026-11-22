using System.Globalization;
using System.Text;

namespace TradingFlow.Domain.News;

/// <summary>
/// Decides whether a stored link can be presented as the headline's own article. Alpaca's fiscal.ai feed attaches links
/// that often point to another story, a list page or a stock page, so an Alpaca link must share words with its headline.
/// Other providers link their own articles and are trusted as given. The stored link itself is never changed.
/// </summary>
public static class NewsLinkMatch
{
    public const string Article = "article";
    public const string Related = "related";
    public const string None = "none";
    private const int MinimumSharedWords = 2;
    private const decimal MinimumSharedShare = 0.5m;
    private const int MinimumWordLength = 4;

    // Path parts of pages about a stock rather than a story; a two-word company name alone would otherwise match them.
    private static readonly HashSet<string> StockPageSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "company", "companies", "profile", "quote", "quotes", "stock", "symbol", "symbols", "ticker", "tickers"
    };

    // Words too common in market headlines and publisher paths to show that two texts are about the same story.
    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "about", "after", "agreement", "announces", "announcement", "article", "articles", "business", "company", "corp",
        "corporation", "earnings", "financial", "first", "fiscal", "fourth", "from", "group", "holdings", "home", "html",
        "into", "limited", "nasdaq", "news", "nyse", "press", "price", "quarter", "rating", "receives", "release",
        "releases", "report", "reports", "results", "second", "shares", "stock", "stocks", "story", "target", "third",
        "with", "year"
    };

    /// <summary>
    /// Returns "article" when the link may be shown as the source article, "related" when it may be a different page, and
    /// "none" when there is no usable web link.
    /// </summary>
    public static string Classify(string? provider, string? headline, string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return None;
        if (!String.Equals(provider, "alpaca", StringComparison.OrdinalIgnoreCase)) return Article;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(StockPageSegments.Contains)) return Related;
        var headlineWords = SignificantWords(headline);
        var shared = headlineWords.Intersect(SignificantWords(path), StringComparer.Ordinal).Count();
        return shared >= MinimumSharedWords && shared >= headlineWords.Count * MinimumSharedShare ? Article : Related;
    }

    // Lower-cases, removes accents and keeps words of four or more letters or digits that are not common or years.
    private static HashSet<string> SignificantWords(string? text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var word = new StringBuilder();
        foreach (var character in (text ?? String.Empty).Normalize(NormalizationForm.FormD).ToLowerInvariant() + " ")
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (Char.IsAsciiLetterOrDigit(character))
            {
                word.Append(character);
                continue;
            }

            var candidate = word.ToString();
            word.Clear();
            var isYear = candidate.Length == 4 && candidate.StartsWith("20", StringComparison.Ordinal) && candidate.All(Char.IsAsciiDigit);
            if (candidate.Length >= MinimumWordLength && !isYear && !CommonWords.Contains(candidate)) words.Add(candidate);
        }

        return words;
    }
}
