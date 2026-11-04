using TradingFlow.Domain.News;

namespace TradingFlow.Web.Services;

/// <summary>One story told by one or more stored records: the same article filed for several stocks, or the
/// same event carried by more than one provider.</summary>
public sealed record NewsStory(PersistedNewsItem Lead, IReadOnlyList<string> Tickers, IReadOnlyList<PersistedNewsItem> Records);

/// <summary>
/// What a screen asks the news service for: its stocks, how far back, how many records, whether broad market
/// context belongs, and optionally a provider whose copy should lead a story it shares with others. Earnings
/// asks for Alpaca because its stream delivers results the moment they publish; other screens ask for none.
/// </summary>
public sealed record NewsStoryQuery(
    IReadOnlyCollection<string> Symbols,
    DateTimeOffset SinceUtc,
    int Limit,
    bool IncludeMarketContext = false,
    string? PreferredProvider = null);

/// <summary>The one place stored news becomes stories, shared by every screen that shows news.</summary>
public static class NewsStories
{
    /// <summary>Alpaca's news stream is the fastest source for company results.</summary>
    public const string RealTimeProvider = "alpaca";

    /// <summary>The copy that speaks for a story: the preferred provider's when asked for, else the newest.</summary>
    public static PersistedNewsItem Lead(IEnumerable<PersistedNewsItem> copies, string? preferredProvider = null) => copies
        .OrderBy(item => preferredProvider is not null && String.Equals(item.Provider, preferredProvider, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenByDescending(item => item.Timestamp)
        .First();

    public static IReadOnlyList<NewsStory> Combine(IEnumerable<PersistedNewsItem> records, string? preferredProvider = null) => records
        .GroupBy(item => NewsFeedService.NormalizeHeadlineIdentity(item.Headline), StringComparer.Ordinal)
        .Select(group => new NewsStory(
            Lead(group, preferredProvider),
            group.Select(item => item.Ticker?.Trim().ToUpperInvariant())
                .Where(ticker => !String.IsNullOrEmpty(ticker))
                .Select(ticker => ticker!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            group.ToArray()))
        .OrderByDescending(story => story.Lead.Timestamp)
        .ToArray();
}
