using Microsoft.Extensions.Caching.Memory;
using TradingFlow.Data.News;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services.Desk;

public sealed record DeskArticle(string Headline, string? Url, string Provider, DateTimeOffset PublishedAtUtc, IReadOnlyList<string> Symbols);
public sealed record DeskRow(DeskMembership Membership, AlpacaLatestQuote? Quote, string QuoteFreshness,
    IndicatorSnapshot? Technical, DeskHolding? Holding, bool HoldingsKnown, IReadOnlyList<DeskArticle> News, DailyObservationResult Observation);
public sealed record DeskSnapshot(DeskSelection Selection, IReadOnlyList<DeskRow> Rows, DeskAccount Account,
    IReadOnlyList<DeskArticle> News, string NewsCoverage, string Session, DateTimeOffset ReadAtUtc, string Feed);

public sealed class DeskReadService(AlpacaQuoteService quotes, IDeskAccountReader account,
    IDailyObservationService daily, SqliteNewsFeedRepository news, OperationalStatusService operations,
    IMemoryCache cache, TimeProvider clock, ILogger<DeskReadService> logger)
{
    public async Task<DeskSnapshot> ReadAsync(DeskSelection selection, CancellationToken ct)
    {
        var symbols = selection.Members.Select(x => x.Symbol).ToArray();
        var quoteTask = quotes.GetLatestQuotesAsync(symbols, "sip", ct);
        var accountTask = account.ReadAsync(ct);
        var sessionTask = operations.GetTradingSessionAsync(ct);
        var newsTask = ReadNewsAsync(symbols, ct);
        await Task.WhenAll(quoteTask, accountTask, sessionTask, newsTask);
        var holdings = (await accountTask).Holdings.ToDictionary(x => x.Symbol, StringComparer.OrdinalIgnoreCase);
        var quoteMap = await quoteTask;
        var newsResult = await newsTask;
        var rows = new List<DeskRow>();
        foreach (var member in selection.Members)
        {
            var quote = quoteMap.GetValueOrDefault(member.Symbol);
            var observation = await daily.GetAsync(member.Symbol, ct);
            var technical = observation.Snapshot;
            rows.Add(new(member, quote, Freshness(quote?.Timestamp, clock.GetUtcNow()), technical,
                holdings.GetValueOrDefault(member.Symbol), (await accountTask).Available,
                newsResult.Items.Where(x => x.Symbols.Contains(member.Symbol)).Take(5).ToArray(), observation));
        }
        return new(selection, rows, await accountTask, newsResult.Items.Take(40).ToArray(), newsResult.Detail,
            (await sessionTask)?.Session.ToString() ?? "Unknown", clock.GetUtcNow(), "SIP");
    }

    public static string Freshness(DateTimeOffset? timestamp, DateTimeOffset now) => timestamp is null ? "unavailable"
        : timestamp > now.AddSeconds(5) ? "clock mismatch" : now - timestamp <= TimeSpan.FromSeconds(20) ? "current"
        : now - timestamp <= TimeSpan.FromMinutes(2) ? "delayed" : "stale";

    private sealed record NewsResult(IReadOnlyList<DeskArticle> Items, string Detail);
    private async Task<NewsResult> ReadNewsAsync(string[] symbols, CancellationToken ct) =>
        (await cache.GetOrCreateAsync("desk-news:" + String.Join(',', symbols), async entry => {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15);
            try
            {
                var items = await news.GetRecentForTickersAsync(clock.GetUtcNow().AddHours(-72), 1000, symbols, ct);
                return new NewsResult(AggregateNews(items), "Matched stored news · last 72h · up to 1,000 symbol records. Refreshes while connected; historical coverage may be incomplete.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogWarning(ex, "Desk news read failed."); return new NewsResult([], "News is unavailable. Retry when the connection recovers."); }
        }))!;
    public static IReadOnlyList<DeskArticle> AggregateNews(IEnumerable<PersistedNewsItem> items) => items
        .GroupBy(x => String.IsNullOrWhiteSpace(x.Url) ? x.Provider + ":" + x.Headline : x.Url, StringComparer.OrdinalIgnoreCase)
        .Select(group => { var latest = group.MaxBy(x => x.Timestamp)!; return new DeskArticle(latest.Headline, latest.Url,
            latest.Source ?? latest.Provider, latest.Timestamp, group.Select(x => x.Ticker).Distinct().Order().ToArray()); })
        .OrderByDescending(x => x.PublishedAtUtc).ToArray();
}
