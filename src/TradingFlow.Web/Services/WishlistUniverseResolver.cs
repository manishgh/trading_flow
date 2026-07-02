using TradingFlow.Domain.Wishlists;

namespace TradingFlow.Web.Services;

/// <summary>
/// Resolves a UI/API universe selection into the concrete ticker set used by a run.
/// The run config stores these resolved tickers so later analysis is reproducible
/// even if the wishlist changes after the run starts.
/// </summary>
public sealed class WishlistUniverseResolver
{
    private readonly IWishlistRepository wishlists;

    public WishlistUniverseResolver(IWishlistRepository wishlists)
    {
        this.wishlists = wishlists;
    }

    public async Task<IReadOnlyList<string>> ResolveAsync(
        IEnumerable<string> requestTickers,
        Guid? wishlistId,
        CancellationToken cancellationToken)
    {
        var resolved = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ticker in requestTickers.Select(NormalizeTicker).Where(ticker => ticker.Length > 0))
        {
            resolved.Add(ticker);
        }

        if (wishlistId is { } id)
        {
            var wishlist = await wishlists.GetByIdAsync(id, cancellationToken);
            if (wishlist is null)
            {
                throw new InvalidOperationException($"Wishlist {id} does not exist.");
            }

            foreach (var ticker in wishlist.Items.Where(item => item.Active).Select(item => NormalizeTicker(item.Ticker)).Where(ticker => ticker.Length > 0))
            {
                resolved.Add(ticker);
            }
        }

        return resolved.ToArray();
    }

    private static string NormalizeTicker(string? ticker)
    {
        return String.IsNullOrWhiteSpace(ticker)
            ? String.Empty
            : ticker.Trim().ToUpperInvariant();
    }
}
