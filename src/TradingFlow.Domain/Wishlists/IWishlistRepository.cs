namespace TradingFlow.Domain.Wishlists;

/// <summary>
/// Stores user-managed ticker universes and monitor signals. Paper/live runners
/// consume these lists without duplicating ticker state in mode-specific YAML.
/// </summary>
public interface IWishlistRepository
{
    Task<IReadOnlyList<Wishlist>> ListAsync(CancellationToken cancellationToken);

    Task<Wishlist?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<Wishlist?> GetByNameAsync(string name, CancellationToken cancellationToken);

    Task<Wishlist> SaveAsync(Wishlist wishlist, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    Task SetObservedAsync(Guid id, bool isObserved, CancellationToken cancellationToken);

    Task<WishlistItem> AddOrUpdateItemAsync(Guid wishlistId, string ticker, string? displayName, string? notes, CancellationToken cancellationToken);

    Task RemoveItemAsync(Guid wishlistId, string ticker, CancellationToken cancellationToken);

    Task<WishlistSignal> AddSignalAsync(WishlistSignal signal, CancellationToken cancellationToken);

    Task<IReadOnlyList<WishlistSignal>> GetSignalsAsync(Guid? wishlistId, string? ticker, DateTimeOffset sinceUtc, int limit, CancellationToken cancellationToken);

    Task AcknowledgeSignalAsync(Guid signalId, CancellationToken cancellationToken);
}
