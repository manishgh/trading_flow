namespace TradingFlow.Domain.News;

public sealed record NewsFeedQuery(DateTimeOffset SinceUtc, string? Ticker = null,
    IReadOnlyList<string>? ScopeTickers = null, string? Provider = null, string? Source = null,
    string? Classification = null, string? Sentiment = null, string? Search = null,
    bool LinkedOnly = false, int Skip = 0, int Take = 50, string? Category = null, string? Sector = null,
    bool IncludeMarketNews = false, DateTimeOffset? UntilUtc = null, bool StockLinkedOnly = false);
public sealed record NewsFeedPage(IReadOnlyList<PersistedNewsItem> Records, int TotalMatching);

public static class NewsEvidenceClassification
{
    public static readonly string[] MacroKeywords = ["inflation", "cpi", "ppi", "jobs", "payroll", "unemployment",
        "gdp", "yield", "treasury", "interest rate", "currency", "dollar", "oil", "energy", "tariff"];
    public static string Classify(PersistedNewsItem item) =>
        item.Provider.Equals("sec_edgar", StringComparison.OrdinalIgnoreCase) ? "SEC filing" :
        item.Provider.Equals("federal_reserve", StringComparison.OrdinalIgnoreCase) ? "Fed" :
        !item.Ticker.Equals("MARKET", StringComparison.OrdinalIgnoreCase) ? "Company" :
        MacroKeywords.Any(word => item.Headline.Contains(word, StringComparison.OrdinalIgnoreCase)) ? "Macro" : "Market";
}
