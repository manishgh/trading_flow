using TradingFlow.Domain.Market;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

public sealed class WishlistSwingWatchEvaluatorTests
{
    [Fact]
    public void Evaluate_WhenDailyTrendMomentumAndParticipationAlign_ProducesSwingWatch()
    {
        var evaluator = new WishlistSwingWatchEvaluator();
        var current = Snapshot(104m, 1_200_000m, 2.5m, 102m, 100m, 0.8m, 1.8m);
        var previous = Snapshot(101m, 800_000m, 2.4m, 100m, 99m, 0.3m, 1.2m);

        var result = evaluator.Evaluate(new WishlistMarketSnapshot("test", current, previous, 105m));

        Assert.True(result.ShouldAlert);
        Assert.Equal("swing_breakout_watch", result.SignalType);
        Assert.Equal("TEST", result.Ticker);
        Assert.Contains("daily trend", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_WhenPriceIsTooExtendedAboveDailyEma20_RejectsWatch()
    {
        var evaluator = new WishlistSwingWatchEvaluator(
            new WishlistSwingWatchEvaluatorOptions(MaxEma20ExtensionAtr: 2m));
        var current = Snapshot(110m, 2_000_000m, 2m, 107m, 100m, 1m, 4m);
        var previous = Snapshot(106m, 1_000_000m, 2m, 103m, 100m, 0.5m, 2m);

        var result = evaluator.Evaluate(new WishlistMarketSnapshot("TDIC", current, previous, 111m));

        Assert.False(result.ShouldAlert);
        Assert.Equal("swing_watch_not_ready", result.SignalType);
        Assert.Contains("too extended", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_WhenDailyRvolIsUnavailable_DoesNotSubstituteBarGrowth()
    {
        var evaluator = new WishlistSwingWatchEvaluator();
        var current = Snapshot(104m, 5_000_000m, 2.5m, 102m, 100m, 0.8m, null);
        var previous = Snapshot(101m, 10_000m, 2.4m, 100m, 99m, 0.3m, 1.2m);

        var result = evaluator.Evaluate(new WishlistMarketSnapshot("test", current, previous, 105m));

        Assert.False(result.ShouldAlert);
        Assert.Null(result.DailyRelativeVolume);
        Assert.Contains("relative volume", result.Reason, StringComparison.OrdinalIgnoreCase);
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
        var monitor = new WishlistMarketMonitor(repository, new WishlistSwingWatchEvaluator());
        var snapshots = new Dictionary<string, WishlistMarketSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["POET"] = new("POET", Snapshot(14.2m, 20_000m, 0.4m, 14.1m, 14m, 0.05m, 1.4m), null, 14.3m),
            ["MXL"] = new("MXL", Snapshot(80m, 20_000m, 1m, 79.5m, 79m, 0.05m, 2m), null, 81m)
        };

        var results = await monitor.EvaluateAsync(wishlistId, snapshots, CancellationToken.None);

        Assert.Equal("POET", Assert.Single(results).Ticker);
    }

    private static IndicatorSnapshot Snapshot(
        decimal price,
        decimal volume,
        decimal atr,
        decimal ema10,
        decimal ema20,
        decimal macd,
        decimal? dailyRelativeVolume) =>
        new(
            "TEST",
            DateTimeOffset.UtcNow,
            "1d",
            price,
            volume,
            Vwap: null,
            Rsi: 55m,
            Atr: atr,
            Ema20: ema20,
            Ema50: null,
            Ema200: null,
            BollingerMiddle: null,
            BollingerUpper: null,
            BollingerLower: null,
            RelativeVolume: dailyRelativeVolume,
            MacdLine: macd,
            MacdSignal: 0m,
            MacdHistogram: macd,
            Ema10: ema10);

    private sealed class InMemoryWishlistRepository(params Wishlist[] values) : IWishlistRepository
    {
        private readonly Dictionary<Guid, Wishlist> wishlists = values.ToDictionary(wishlist => wishlist.Id);

        public Task<IReadOnlyList<Wishlist>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Wishlist>>(wishlists.Values.ToArray());
        public Task<Wishlist?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(wishlists.GetValueOrDefault(id));
        public Task<Wishlist?> GetByNameAsync(string name, CancellationToken cancellationToken) => Task.FromResult(wishlists.Values.FirstOrDefault(wishlist => wishlist.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        public Task<Wishlist> SaveAsync(Wishlist wishlist, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetObservedAsync(Guid id, bool isObserved, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WishlistItem> AddOrUpdateItemAsync(Guid wishlistId, string ticker, string? displayName, string? notes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveItemAsync(Guid wishlistId, string ticker, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WishlistSignal> AddSignalAsync(WishlistSignal signal, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WishlistSignal>> GetSignalsAsync(Guid? wishlistId, string? ticker, DateTimeOffset sinceUtc, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WishlistSignal>>([]);
        public Task AcknowledgeSignalAsync(Guid signalId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
