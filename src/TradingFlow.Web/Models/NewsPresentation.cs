using TradingFlow.Domain.News;

namespace TradingFlow.Web.Models;

/// <summary>The presentation contract shared by server and live News surfaces.</summary>
public sealed record NewsPresentation(string Tickers, DateTimeOffset Timestamp, string Headline,
    decimal? SentimentScore, string? Provider, string? Source, string? Url, string? Summary,
    string Classification, string? WhyItMatters = null, IReadOnlyList<string>? Categories = null,
    IReadOnlyList<string>? Sectors = null, string? CategoryRationale = null, string? CategoryVersion = null)
{
    public string? SafeUrl => Uri.TryCreate(Url,UriKind.Absolute,out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    public string Key => SafeUrl ?? $"{Headline}|{Timestamp:O}";
    public string GroupableTickers => String.Join(", ",Tickers.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
        .Where(x=>x != "MARKET" && x.Length is >= 1 and <= 16 && x.All(c=>Char.IsAsciiLetterOrDigit(c) || c is '.' or '-')));
    public string SentimentLabel => SentimentScore is null ? "Sentiment unavailable" : SentimentScore > .15m ? "Positive" : SentimentScore < -.15m ? "Negative" : "Neutral";
    public string SentimentClass => SentimentScore is null ? "" : SentimentScore > .15m ? "is-positive" : SentimentScore < -.15m ? "is-negative" : "";
    public string SymbolsLabel => String.Join(", ", Tickers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => x == "MARKET" ? "Broad market (MARKET)" : x));
    public IReadOnlyList<string> CategoryLabels => Labels(Categories, NewsDisplayTaxonomy.Categories);
    public IReadOnlyList<string> SectorLabels => Labels(Sectors, NewsDisplayTaxonomy.Sectors);
    public bool HasTopics => CategoryLabels.Count > 0 || SectorLabels.Count > 0;
    private static IReadOnlyList<string> Labels(IReadOnlyList<string>? keys, IReadOnlyList<NewsDisplayOption> options) =>
        options.Where(x => x.Key != "all" && keys?.Contains(x.Key, StringComparer.OrdinalIgnoreCase) == true).Select(x => x.Label).ToArray();
    public static NewsPresentation From(MobileNewsItem item) => new(
        String.Join(", ",item.Ticker.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(x=>x.ToUpperInvariant()).Distinct().Order()),
        item.Timestamp,item.Headline,item.SentimentScore,item.Provider,item.Source,item.Url,item.Summary,
        item.Classification ?? NewsEvidenceClassification.Classify(new() {Ticker=item.Ticker,Headline=item.Headline,Provider=item.Provider ?? ""}),
        Categories: item.Categories, Sectors: item.Sectors, CategoryRationale: item.CategoryRationale, CategoryVersion: item.CategoryVersion);
}
