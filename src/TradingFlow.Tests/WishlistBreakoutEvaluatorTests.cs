using TradingFlow.Domain.Market;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

public sealed class WishlistBreakoutEvaluatorTests
{
    [Fact]
    public void Evaluate_WhenTrendMomentumAndParticipationAligns_ProducesBreakoutAlert()
    {
        var evaluator = new WishlistBreakoutEvaluator();
        var current = Snapshot(price: 10.40m, volume: 120_000m, vwap: 10.05m, atr: 0.25m, ema10: 10.20m, ema20: 10.10m, macd: 0.08m, sessionRvol: 1.8m);
        var previous = Snapshot(price: 10.10m, volume: 80_000m, vwap: 10.02m, atr: 0.25m, ema10: 10.08m, ema20: 10.07m, macd: 0.03m, sessionRvol: 1.2m);

        var result = evaluator.Evaluate(new WishlistMarketSnapshot("test", current, previous, RecentHigh: 10.35m, SessionOpen: 10.00m));

        Assert.True(result.ShouldAlert);
        Assert.Equal("wishlist_breakout", result.SignalType);
        Assert.Equal("TEST", result.Ticker);
        Assert.Contains("MACD", result.Reason);
    }

    [Fact]
    public void Evaluate_WhenPriceIsTooExtendedFromVwap_RejectsChase()
    {
        var evaluator = new WishlistBreakoutEvaluator(new WishlistBreakoutEvaluatorOptions(MaxVwapExtensionAtr: 2.0m));
        var current = Snapshot(price: 11.00m, volume: 200_000m, vwap: 10.00m, atr: 0.20m, ema10: 10.70m, ema20: 10.20m, macd: 0.20m, sessionRvol: 4.0m);
        var previous = Snapshot(price: 10.70m, volume: 100_000m, vwap: 9.95m, atr: 0.20m, ema10: 10.40m, ema20: 10.10m, macd: 0.10m, sessionRvol: 2.0m);

        var result = evaluator.Evaluate(new WishlistMarketSnapshot("TDIC", current, previous, RecentHigh: 10.80m, SessionOpen: 9.80m));

        Assert.False(result.ShouldAlert);
        Assert.Equal("wishlist_watch_rejected", result.SignalType);
        Assert.Contains("extension too high", result.Reason);
    }

    [Fact]
    public async Task Monitor_EvaluatesOnlyActiveWishlistTickersWithProvidedSnapshots()
    {
        var wishlistId = Guid.NewGuid();
        var repository = new InMemoryWishlistRepository(new Wishlist
        {
            Id = wishlistId,
            Name = "Active",
            Items =
            {
                new WishlistItem { Id = Guid.NewGuid(), WishlistId = wishlistId, Ticker = "POET", Active = true },
                new WishlistItem { Id = Guid.NewGuid(), WishlistId = wishlistId, Ticker = "MXL", Active = false }
            }
        });
        var monitor = new WishlistMarketMonitor(repository, new WishlistBreakoutEvaluator());
        var snapshots = new Dictionary<string, WishlistMarketSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["POET"] = new("POET", Snapshot(14.2m, 20_000m, 14.0m, 0.2m, 14.1m, 14.0m, 0.05m, 1.4m), null, 14.1m, 13.8m),
            ["MXL"] = new("MXL", Snapshot(80m, 20_000m, 79m, 0.5m, 79.5m, 79m, 0.05m, 2m), null, 79.9m, 78m)
        };

        var results = await monitor.EvaluateAsync(wishlistId, snapshots, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("POET", result.Ticker);
    }

    private static IndicatorSnapshot Snapshot(decimal price, decimal volume, decimal vwap, decimal atr, decimal ema10, decimal ema20, decimal macd, decimal sessionRvol)
    {
        return new IndicatorSnapshot(
            "TEST",
            DateTimeOffset.UtcNow,
            "1m",
            price,
            volume,
            vwap,
            Rsi: 55m,
            atr,
            ema20,
            Ema50: null,
            Ema200: null,
            BollingerMiddle: null,
            BollingerUpper: null,
            BollingerLower: null,
            RelativeVolume: sessionRvol,
            MacdLine: macd,
            MacdSignal: 0m,
            MacdHistogram: macd,
            Ema10: ema10,
            SessionRelativeVolume: sessionRvol);
    }

    private sealed class InMemoryWishlistRepository : IWishlistRepository
    {
        private readonly Dictionary<Guid, Wishlist> wishlists;

        public InMemoryWishlistRepository(params Wishlist[] wishlists)
        {
            this.wishlists = wishlists.ToDictionary(wishlist => wishlist.Id);
        }

        public Task<IReadOnlyList<Wishlist>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Wishlist>>(wishlists.Values.ToArray());
        public Task<Wishlist?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(wishlists.TryGetValue(id, out var wishlist) ? wishlist : null);
        public Task<Wishlist?> GetByNameAsync(string name, CancellationToken cancellationToken) => Task.FromResult(wishlists.Values.FirstOrDefault(wishlist => wishlist.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        public Task<Wishlist> SaveAsync(Wishlist wishlist, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetObservedAsync(Guid id, bool isObserved, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WishlistItem> AddOrUpdateItemAsync(Guid wishlistId, string ticker, string? displayName, string? notes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveItemAsync(Guid wishlistId, string ticker, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WishlistSignal> AddSignalAsync(WishlistSignal signal, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WishlistSignal>> GetSignalsAsync(Guid? wishlistId, string? ticker, DateTimeOffset sinceUtc, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WishlistSignal>>(Array.Empty<WishlistSignal>());
        public Task AcknowledgeSignalAsync(Guid signalId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
