using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Web.Services;

public sealed record NewsStockMember(string Symbol, IReadOnlyList<string> Wishlists, bool? InFinviz);
public sealed record NewsStockSelection(IReadOnlyList<string>? Symbols, string Description, bool Available = true)
{
    public IReadOnlyList<NewsStockMember> Members { get; init; } = [];
}
public sealed record NewsStockPage(IReadOnlyList<NewsStockMember> Items, int Total, int Page, int PageCount);

/// <summary>Observational stock membership for stored news; it never collects news or admits a trade.</summary>
public sealed class NewsStockSourceService(DeskSources desk)
{
    public async Task<IReadOnlyList<DeskSource>> CatalogAsync(CancellationToken token) =>
        new[] {
            new DeskSource("all", "All news / all stocks", "", "All stored stock and market news."),
            new DeskSource("wishlists", "All wishlist stocks", "", "All active ordinary-wishlist stocks, without duplicates."),
            new DeskSource("finviz", "All Finviz stocks", "", "All stocks returned by the Finviz all-stocks screen."),
            new DeskSource("wishlists-finviz", "All wishlist + Finviz stocks", "", "Union of all wishlist stocks and Finviz all-stocks results.")
        }.Concat((await desk.CatalogAsync(token)).Where(x => x.Key != "all")).ToArray();

    public async Task<NewsStockSelection> ResolveAsync(string key, CancellationToken token)
    {
        var source = (await CatalogAsync(token)).SingleOrDefault(x => x.Key == key)
            ?? throw new InvalidOperationException("This stock source no longer exists. Choose another wishlist or Finviz screen.");
        var lists = await desk.FollowedListsAsync(token);
        var allWishlistMembers = DeskSources.Merge(lists).ToDictionary(x => x.Symbol, StringComparer.OrdinalIgnoreCase);
        NewsStockSelection Selection(IReadOnlyList<string> selected, string description, IReadOnlySet<string>? finviz = null) =>
            new(selected, description) { Members = selected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => new NewsStockMember(x, allWishlistMembers.GetValueOrDefault(x)?.Wishlists.Select(w => w.Name).ToArray() ?? [],
                    finviz is null ? null : finviz.Contains(x))).ToArray() };
        var symbols = DeskSources.Merge(key.StartsWith("wishlist:", StringComparison.Ordinal)
            ? lists.Where(x => "wishlist:" + x.Id == key) : lists).Select(x => x.Symbol).ToArray();
        if (key == "wishlists" || key.StartsWith("wishlist:", StringComparison.Ordinal))
            return Selection(symbols, $"{source.Name}: {symbols.Length} active wishlist stocks. News filtered by these stocks.");
        // An earnings group is the earnings monitor's reporting list, never a Finviz screen.
        if (key.StartsWith("earnings:", StringComparison.Ordinal))
        {
            var reporting = (await desk.EarningsGroupAsync(key, token)).Select(x => x.Symbol).ToArray();
            return Selection(reporting, $"{source.Name}: {reporting.Length} stocks. News filtered by these stocks.");
        }
        var screen = await desk.ReadScreenAsync(source.Query ?? "ind_stocksonly", token);
        if (!screen.Succeeded) return new([], "Finviz stock source unavailable. Check the provider configuration or connection and retry; the selected scope has not been replaced.", false);
        var returned = (screen.AllSymbols.Count > 0 ? screen.AllSymbols : screen.Symbols)
            .Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (screen.TotalCount > returned.Length)
            return new([], "Finviz membership is incomplete. Retry the selected source; partial returned stocks are not presented as the full scope.", false);
        var merged = key == "wishlists-finviz" ? symbols.Concat(returned).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : returned;
        return Selection(merged, $"{source.Name}: {merged.Length} stocks; Finviz returned {returned.Length} at {screen.SyncedAtUtc:yyyy-MM-dd HH:mm:ss} UTC. News filtered by current membership, not historical membership.", returned.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    public static NewsStockPage Page(NewsStockSelection selection, string? ticker, int page)
    {
        var members = selection.Members.Where(x => String.IsNullOrWhiteSpace(ticker) || x.Symbol.Equals(ticker.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        const int size = 100;
        var pages = Math.Max(1, (members.Length + size - 1) / size);
        page = Math.Clamp(page, 1, pages);
        return new(members.Skip((page - 1) * size).Take(size).ToArray(), members.Length, page, pages);
    }
}
