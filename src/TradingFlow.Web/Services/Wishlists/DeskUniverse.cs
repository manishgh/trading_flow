using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services.Wishlists;

/// <summary>Where the desk's rows come from.</summary>
public enum DeskUniverseKind
{
    /// <summary>Every active symbol across every wishlist, plus any held position.</summary>
    AllWishlists,

    /// <summary>The active symbols of one wishlist.</summary>
    Wishlist,

    /// <summary>The symbols a screener returned.</summary>
    Screener
}

/// <summary>
/// One symbol on the desk and the wishlists it belongs to.
/// </summary>
/// <param name="Ticker">Upper-case symbol.</param>
/// <param name="Item">The wishlist entry backing the row, or a transient one for screener and position-only rows.</param>
/// <param name="Lists">Names of the wishlists holding the symbol, in name order. Empty when it is on none.</param>
/// <param name="IsPositionOnly">True when the symbol is on the desk only because a position is open in it.</param>
public sealed record DeskUniverseMember(
    string Ticker,
    WishlistItem Item,
    IReadOnlyList<string> Lists,
    bool IsPositionOnly = false);

/// <summary>
/// The set of symbols the desk monitors, resolved before any quote, signal or
/// news is read. Pure: every factory works on already-loaded wishlists, so the
/// membership rules are testable without a database or a provider.
/// </summary>
public sealed record DeskUniverse(
    DeskUniverseKind Kind,
    string Label,
    IReadOnlyList<DeskUniverseMember> Members,
    Guid? WishlistId)
{
    /// <summary>
    /// Label a position-only row carries in place of wishlist names, so a holding
    /// that is on no list still says why it is on the desk.
    /// </summary>
    public const string HeldPositionLabel = "Held position";

    public static DeskUniverse Empty { get; } = new(DeskUniverseKind.Wishlist, "No wishlist", [], null);

    /// <summary>Upper-case tickers in the universe.</summary>
    /// <remarks>
    /// Computed rather than initialised: a property initialiser would not re-run
    /// on a <c>with</c> copy and would go stale after <see cref="WithHeldPositions"/>.
    /// </remarks>
    public IReadOnlySet<string> Tickers => Members
        .Select(member => member.Ticker)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether open positions outside the lists join the universe. Only the
    /// all-wishlists view does this: a single wishlist or a screener is an
    /// explicit narrowing the operator chose, and a holding appearing there would
    /// read as membership it does not have.
    /// </summary>
    public bool IncludesHeldPositions => Kind == DeskUniverseKind.AllWishlists;

    /// <summary>Every active symbol across <paramref name="wishlists"/>, one row per symbol.</summary>
    public static DeskUniverse ForAllWishlists(IEnumerable<Wishlist> wishlists)
    {
        var lists = wishlists.ToArray();
        var members = Index(lists)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new DeskUniverseMember(entry.Key, entry.Value.Item, entry.Value.Lists))
            .ToArray();
        return new DeskUniverse(DeskUniverseKind.AllWishlists, "All wishlists", members, null);
    }

    /// <summary>The active symbols of one wishlist.</summary>
    public static DeskUniverse ForWishlist(Wishlist wishlist)
    {
        var members = wishlist.Items
            .Where(item => item.Active)
            .Select(item => (Ticker: Normalize(item.Ticker), Item: item))
            .Where(entry => entry.Ticker.Length > 0)
            .GroupBy(entry => entry.Ticker, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new DeskUniverseMember(group.Key, group.First().Item, [wishlist.Name]))
            .ToArray();
        return new DeskUniverse(DeskUniverseKind.Wishlist, wishlist.Name, members, wishlist.Id);
    }

    /// <summary>
    /// The screener's symbols, each annotated with the wishlists that already hold
    /// it. A symbol on no list gets a transient entry that is never persisted.
    /// </summary>
    public static DeskUniverse ForScreener(
        string label,
        IEnumerable<string> symbols,
        IEnumerable<Wishlist> wishlists)
    {
        var index = Index(wishlists);
        var members = symbols
            .Select(Normalize)
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ticker => index.TryGetValue(ticker, out var known)
                ? new DeskUniverseMember(ticker, known.Item, known.Lists)
                : new DeskUniverseMember(ticker, Transient(ticker), []))
            .ToArray();
        return new DeskUniverse(DeskUniverseKind.Screener, label, members, null);
    }

    /// <summary>
    /// Adds held tickers that are not already members, marked position-only.
    /// Returns this universe unchanged when it does not take held positions.
    /// </summary>
    public DeskUniverse WithHeldPositions(IEnumerable<string> heldTickers)
    {
        if (!IncludesHeldPositions)
        {
            return this;
        }

        var existing = Tickers;
        var added = heldTickers
            .Select(Normalize)
            .Where(ticker => ticker.Length > 0 && !existing.Contains(ticker))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ticker => ticker, StringComparer.Ordinal)
            .Select(ticker => new DeskUniverseMember(ticker, Transient(ticker), [HeldPositionLabel], IsPositionOnly: true))
            .ToArray();
        return added.Length == 0 ? this : this with { Members = [.. Members, .. added] };
    }

    private static Dictionary<string, (WishlistItem Item, IReadOnlyList<string> Lists)> Index(IEnumerable<Wishlist> wishlists)
    {
        var byTicker = new Dictionary<string, (WishlistItem Item, List<string> Lists)>(StringComparer.OrdinalIgnoreCase);
        foreach (var wishlist in wishlists.OrderBy(list => list.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var item in wishlist.Items.Where(item => item.Active))
            {
                var ticker = Normalize(item.Ticker);
                if (ticker.Length == 0)
                {
                    continue;
                }

                if (!byTicker.TryGetValue(ticker, out var entry))
                {
                    entry = (item, []);
                    byTicker[ticker] = entry;
                }
                else if (String.IsNullOrWhiteSpace(entry.Item.DisplayName) && !String.IsNullOrWhiteSpace(item.DisplayName))
                {
                    // Keep the first entry that names the company, so a symbol
                    // listed bare in one list and named in another reads by name.
                    entry = (item, entry.Lists);
                    byTicker[ticker] = entry;
                }

                if (!entry.Lists.Contains(wishlist.Name, StringComparer.OrdinalIgnoreCase))
                {
                    entry.Lists.Add(wishlist.Name);
                }
            }
        }

        return byTicker.ToDictionary(
            pair => pair.Key,
            pair => (pair.Value.Item, (IReadOnlyList<string>)pair.Value.Lists),
            StringComparer.OrdinalIgnoreCase);
    }

    private static WishlistItem Transient(string ticker) => new()
    {
        Ticker = ticker,
        Active = true
    };

    private static string Normalize(string? ticker) => (ticker ?? String.Empty).Trim().ToUpperInvariant();
}

/// <summary>
/// Account-wide totals over every open position, independent of the desk view.
/// </summary>
/// <param name="Positions">Open position count.</param>
/// <param name="CostBasis">Sum of quantity times entry price.</param>
/// <param name="MarketValue">Sum of quantity times current price.</param>
/// <param name="UnrealizedPl">Sum of open P/L as reported per position.</param>
public sealed record DeskPortfolioSummary(
    int Positions,
    decimal CostBasis,
    decimal MarketValue,
    decimal UnrealizedPl)
{
    public static DeskPortfolioSummary Empty { get; } = new(0, 0m, 0m, 0m);

    /// <summary>Open P/L as a percentage of cost basis, or null with nothing invested.</summary>
    public decimal? UnrealizedPlPct => CostBasis == 0m ? null : UnrealizedPl / CostBasis * 100m;

    public static DeskPortfolioSummary From(IEnumerable<MobileRunningTrade> trades)
    {
        var open = trades.ToArray();
        return new DeskPortfolioSummary(
            open.Length,
            open.Sum(trade => Math.Abs(trade.Quantity) * trade.EntryPrice),
            open.Sum(trade => Math.Abs(trade.Quantity) * trade.CurrentPrice),
            open.Sum(trade => trade.UnrealizedPl));
    }
}
