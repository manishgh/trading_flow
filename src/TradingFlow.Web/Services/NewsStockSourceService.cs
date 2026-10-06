using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Web.Services;

public sealed record NewsStockSelection(IReadOnlyList<string>? Symbols, string Description, bool Available = true);

/// <summary>Observational stock membership for stored news; it never collects news or admits a trade.</summary>
public sealed class NewsStockSourceService(DeskSources desk)
{
    public async Task<IReadOnlyList<DeskSource>> CatalogAsync(CancellationToken token) =>
        new[] {
            new DeskSource("all", "All news / all stocks", "Combined sources", "All stored stock and market news."),
            new DeskSource("wishlists", "All wishlist stocks", "Combined sources", "All active ordinary-wishlist stocks, without duplicates."),
            new DeskSource("finviz", "All Finviz stocks", "Combined sources", "All stocks returned by the Finviz all-stocks screen."),
            new DeskSource("wishlists-finviz", "All wishlist + Finviz stocks", "Combined sources", "Union of all wishlist stocks and Finviz all-stocks results."),
            new DeskSource("positions", "My positions", "Combined sources", "Stocks held in the current account.")
        }.Concat((await desk.CatalogAsync(token)).Where(x => x.Key != "all")).ToArray();

    public async Task<NewsStockSelection> ResolveAsync(string key, CancellationToken token)
    {
        var source = (await CatalogAsync(token)).SingleOrDefault(x => x.Key == key)
            ?? throw new InvalidOperationException("This stock source no longer exists. Choose another wishlist or Finviz screen.");
        var lists = await desk.FollowedListsAsync(token);
        var symbols = DeskSources.Merge(key.StartsWith("wishlist:", StringComparison.Ordinal)
            ? lists.Where(x => "wishlist:" + x.Id == key) : lists).Select(x => x.Symbol).ToArray();
        if (key == "wishlists" || key.StartsWith("wishlist:", StringComparison.Ordinal))
            return new(symbols, $"{source.Name}: {symbols.Length} active wishlist stocks. Filtering stored news only.");
        var screen = await desk.ReadScreenAsync(source.Query ?? "ind_stocksonly", token);
        if (!screen.Succeeded) return new([], "Finviz stock source unavailable. Check the provider configuration or connection and retry; the selected scope has not been replaced.", false);
        var returned = (screen.AllSymbols.Count > 0 ? screen.AllSymbols : screen.Symbols)
            .Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (screen.TotalCount > returned.Length)
            return new([], "Finviz membership is incomplete. Retry the selected source; partial returned stocks are not presented as the full scope.", false);
        var merged = key == "wishlists-finviz" ? symbols.Concat(returned).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : returned;
        return new(merged, $"{source.Name}: {merged.Length} stocks; Finviz returned {returned.Length} at {screen.SyncedAtUtc:yyyy-MM-dd HH:mm:ss} UTC. Filtering stored news only; current membership is not historical membership.");
    }
}
