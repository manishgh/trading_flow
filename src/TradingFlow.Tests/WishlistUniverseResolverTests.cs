using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class WishlistUniverseResolverTests
{
    [Fact]
    public async Task ResolveAsync_MergesStaticTickersWithActiveWishlistItems()
    {
        var wishlistId = Guid.NewGuid();
        var repository = new InMemoryWishlistRepository(new Wishlist
        {
            Id = wishlistId,
            Name = "Runners",
            IncludeExtendedHours = true,
            Items =
            {
                new WishlistItem { Id = Guid.NewGuid(), WishlistId = wishlistId, Ticker = "poet", Active = true },
                new WishlistItem { Id = Guid.NewGuid(), WishlistId = wishlistId, Ticker = "MXL", Active = false },
                new WishlistItem { Id = Guid.NewGuid(), WishlistId = wishlistId, Ticker = "RGTI", Active = true }
            }
        });
        var resolver = new WishlistUniverseResolver(repository);

        var tickers = await resolver.ResolveAsync([" rgti ", "NVTS"], wishlistId, CancellationToken.None);

        Assert.Equal(["NVTS", "POET", "RGTI"], tickers);
    }

    [Fact]
    public async Task ResolveAsync_ThrowsWhenWishlistDoesNotExist()
    {
        var resolver = new WishlistUniverseResolver(new InMemoryWishlistRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync([], Guid.NewGuid(), CancellationToken.None));
    }

    private sealed class InMemoryWishlistRepository : IWishlistRepository
    {
        private readonly Dictionary<Guid, Wishlist> wishlists;

        public InMemoryWishlistRepository(params Wishlist[] wishlists)
        {
            this.wishlists = wishlists.ToDictionary(wishlist => wishlist.Id);
        }

        public Task<IReadOnlyList<Wishlist>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Wishlist>>(wishlists.Values.ToArray());

        public Task<Wishlist?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(wishlists.TryGetValue(id, out var wishlist) ? wishlist : null);

        public Task<Wishlist?> GetByNameAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(wishlists.Values.FirstOrDefault(wishlist => wishlist.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

        public Task<Wishlist> SaveAsync(Wishlist wishlist, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetObservedAsync(Guid id, bool isObserved, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WishlistItem> AddOrUpdateItemAsync(Guid wishlistId, string ticker, string? displayName, string? notes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RemoveItemAsync(Guid wishlistId, string ticker, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WishlistSignal> AddSignalAsync(WishlistSignal signal, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<WishlistSignal>> GetSignalsAsync(Guid? wishlistId, string? ticker, DateTimeOffset sinceUtc, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WishlistSignal>>(Array.Empty<WishlistSignal>());

        public Task AcknowledgeSignalAsync(Guid signalId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
