using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

public sealed class WishlistObserverServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 14, 2, 0, TimeSpan.Zero);

    [Fact]
    public async Task ObserveOnce_UnchangedCompletedBarIsIdempotentAcrossPassesAndRestart()
    {
        var wishlist = ObservedWishlist("TEST");
        var repository = new FakeWishlistRepository(wishlist);
        var subscriptions = new FakeSubscriptionSink();
        var marketState = new FakeMarketStateProvider(
            new Dictionary<string, TickerMarketState> { ["TEST"] = ReadyState("TEST") });

        var first = CreateObserver(repository, subscriptions, marketState);
        await first.ObserveOnceAsync(CancellationToken.None);
        await first.ObserveOnceAsync(CancellationToken.None);

        Assert.Single(repository.Signals);
        repository.Signals[0].Acknowledged = true;

        var restarted = CreateObserver(repository, subscriptions, marketState);
        await restarted.ObserveOnceAsync(CancellationToken.None);

        Assert.Single(repository.Signals);
        Assert.Equal(3, subscriptions.Replacements.Count);
        Assert.All(subscriptions.Replacements, replacement => Assert.Contains("TEST", replacement));
    }

    [Fact]
    public async Task ObserveOnce_PenultimateBarRevisionReevaluatesDecisionEvidence()
    {
        var wishlist = ObservedWishlist("TEST");
        var repository = new FakeWishlistRepository(wishlist);
        var subscriptions = new FakeSubscriptionSink();
        var marketState = new FakeMarketStateProvider(
            new Dictionary<string, TickerMarketState> { ["TEST"] = ReadyState("TEST", 0.03m) });
        var observer = CreateObserver(repository, subscriptions, marketState);

        await observer.ObserveOnceAsync(CancellationToken.None);
        marketState.States["TEST"] = ReadyState("TEST", 0.02m);
        await observer.ObserveOnceAsync(CancellationToken.None);

        Assert.Equal(2, repository.SignalQueryCount);
        Assert.Single(repository.Signals);
    }

    [Fact]
    public async Task ObserveOnce_FailingTickerDoesNotStarveHealthyTicker()
    {
        var wishlist = ObservedWishlist("BAD", "GOOD");
        var repository = new FakeWishlistRepository(wishlist);
        var subscriptions = new FakeSubscriptionSink();
        var marketState = new FakeMarketStateProvider(
            new Dictionary<string, TickerMarketState> { ["GOOD"] = ReadyState("GOOD") },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BAD" });

        var observer = CreateObserver(repository, subscriptions, marketState);
        await observer.ObserveOnceAsync(CancellationToken.None);

        var signal = Assert.Single(repository.Signals);
        Assert.Equal("GOOD", signal.Ticker);
        Assert.Contains("BAD", Assert.Single(subscriptions.Replacements));
        Assert.Contains("GOOD", Assert.Single(subscriptions.Replacements));
    }

    [Fact]
    public async Task ObserveOnce_NoObservedWishlistRemovesSubscriptionScope()
    {
        var repository = new FakeWishlistRepository(new Wishlist
        {
            Id = Guid.NewGuid(),
            Name = "Dormant",
            IsObserved = false
        });
        var subscriptions = new FakeSubscriptionSink();
        var observer = CreateObserver(
            repository,
            subscriptions,
            new FakeMarketStateProvider(new Dictionary<string, TickerMarketState>()));

        await observer.ObserveOnceAsync(CancellationToken.None);

        Assert.Equal(1, subscriptions.RemoveCount);
        Assert.Empty(subscriptions.Replacements);
    }

    private static WishlistObserverService CreateObserver(
        FakeWishlistRepository repository,
        FakeSubscriptionSink subscriptions,
        IMarketStateSnapshotProvider marketState) =>
        new(
            repository,
            new WishlistMarketMonitor(
                repository,
                new WishlistBreakoutEvaluator(),
                new FixedTimeProvider(Now)),
            new EmptyNewsRepository(),
            subscriptions,
            marketState,
            new FixedTimeProvider(Now),
            NullLogger<WishlistObserverService>.Instance);

    private static Wishlist ObservedWishlist(params string[] tickers)
    {
        var id = Guid.NewGuid();
        return new Wishlist
        {
            Id = id,
            Name = "Observed",
            IsObserved = true,
            Items = tickers.Select(ticker => new WishlistItem
            {
                Id = Guid.NewGuid(),
                WishlistId = id,
                Ticker = ticker,
                Active = true
            }).ToArray()
        };
    }

    private static TickerMarketState ReadyState(string ticker, decimal previousMacd = 0.03m)
    {
        var previousTime = Now.AddMinutes(-3);
        var currentTime = Now.AddMinutes(-2);
        IReadOnlyList<OhlcvBar> bars =
        [
            new(ticker, previousTime, "1m", 10m, 10.20m, 9.95m, 10.10m, 80_000m, "sip", "all", previousTime.AddMinutes(1), previousTime.AddMinutes(1)),
            new(ticker, currentTime, "1m", 10.10m, 10.60m, 10.05m, 10.50m, 120_000m, "sip", "all", currentTime.AddMinutes(1), currentTime.AddMinutes(1))
        ];
        IReadOnlyList<IndicatorSnapshot> snapshots =
        [
            Snapshot(ticker, previousTime, 10.10m, 10.02m, 10.08m, 10.07m, previousMacd, 1.2m),
            Snapshot(ticker, currentTime, 10.50m, 10.05m, 10.20m, 10.10m, 0.08m, 1.8m)
        ];
        return new TickerMarketState(
            ticker,
            new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase) { ["1m"] = bars },
            new Dictionary<string, IReadOnlyList<IndicatorSnapshot>>(StringComparer.OrdinalIgnoreCase) { ["1m"] = snapshots });
    }

    private static IndicatorSnapshot Snapshot(
        string ticker,
        DateTimeOffset timestamp,
        decimal price,
        decimal vwap,
        decimal ema10,
        decimal ema20,
        decimal macd,
        decimal rvol) =>
        new(
            ticker,
            timestamp,
            "1m",
            price,
            100_000m,
            vwap,
            Rsi: 55m,
            Atr: 0.25m,
            Ema20: ema20,
            Ema50: null,
            Ema200: null,
            BollingerMiddle: null,
            BollingerUpper: null,
            BollingerLower: null,
            RelativeVolume: rvol,
            MacdLine: macd,
            MacdSignal: 0m,
            MacdHistogram: macd,
            Ema10: ema10);

    private sealed class FakeMarketStateProvider(
        IReadOnlyDictionary<string, TickerMarketState> states,
        IReadOnlySet<string>? failures = null) : IMarketStateSnapshotProvider
    {
        public Dictionary<string, TickerMarketState> States { get; } =
            new(states, StringComparer.OrdinalIgnoreCase);

        public Task<TickerMarketState?> GetTickerStateAsync(
            string symbol,
            IReadOnlyCollection<string> requiredTimeframes,
            int minimumBarsPerTimeframe,
            DateTimeOffset asOfUtc,
            CancellationToken cancellationToken)
        {
            if (failures?.Contains(symbol) == true)
            {
                throw new InvalidOperationException($"Simulated pipeline failure for {symbol}.");
            }

            return Task.FromResult(States.GetValueOrDefault(symbol));
        }
    }

    private sealed class FakeSubscriptionSink : IDiscoverySubscriptionSink
    {
        public List<IReadOnlySet<string>> Replacements { get; } = [];
        public int RemoveCount { get; private set; }

        public Task ReplaceScopeAsync(Guid scopeId, IReadOnlyCollection<string> symbols, CancellationToken cancellationToken)
        {
            Replacements.Add(symbols.ToHashSet(StringComparer.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        public Task RemoveScopeAsync(Guid scopeId, CancellationToken cancellationToken)
        {
            RemoveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWishlistRepository(params Wishlist[] seed) : IWishlistRepository
    {
        private readonly Dictionary<Guid, Wishlist> wishlists = seed.ToDictionary(item => item.Id);
        public List<WishlistSignal> Signals { get; } = [];
        public int SignalQueryCount { get; private set; }

        public Task<IReadOnlyList<Wishlist>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Wishlist>>(wishlists.Values.ToArray());

        public Task<Wishlist?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(wishlists.GetValueOrDefault(id));

        public Task<Wishlist?> GetByNameAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(wishlists.Values.FirstOrDefault(item => item.Name == name));

        public Task<WishlistSignal> AddSignalAsync(WishlistSignal signal, CancellationToken cancellationToken)
        {
            signal.Id = signal.Id == Guid.Empty ? Guid.NewGuid() : signal.Id;
            Signals.Add(signal);
            return Task.FromResult(signal);
        }

        public Task<IReadOnlyList<WishlistSignal>> GetSignalsAsync(
            Guid? wishlistId,
            string? ticker,
            DateTimeOffset sinceUtc,
            int limit,
            CancellationToken cancellationToken)
        {
            SignalQueryCount++;
            return Task.FromResult<IReadOnlyList<WishlistSignal>>(Signals
                .Where(signal => signal.DetectedAtUtc >= sinceUtc)
                .Where(signal => !wishlistId.HasValue || signal.WishlistId == wishlistId)
                .Where(signal => ticker is null || signal.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(signal => signal.DetectedAtUtc)
                .Take(limit)
                .ToArray());
        }

        public Task<Wishlist> SaveAsync(Wishlist wishlist, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetObservedAsync(Guid id, bool isObserved, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WishlistItem> AddOrUpdateItemAsync(Guid wishlistId, string ticker, string? displayName, string? notes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveItemAsync(Guid wishlistId, string ticker, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AcknowledgeSignalAsync(Guid signalId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EmptyNewsRepository : INewsFeedRepository
    {
        public Task<IReadOnlyList<PersistedNewsItem>> GetRecentAsync(DateTimeOffset windowStart, int limit, string? ticker, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PersistedNewsItem>>([]);
        public Task<IReadOnlyList<PersistedNewsItem>> GetRecentForTickersAsync(DateTimeOffset windowStart, int limit, IReadOnlyCollection<string> tickers, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PersistedNewsItem>>([]);
        public Task<IReadOnlyList<PersistedNewsItem>> GetIngestedSinceForTickersAsync(DateTimeOffset ingestedSinceUtc, int limit, IReadOnlyCollection<string> tickers, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PersistedNewsItem>>([]);
        public Task UpsertAsync(IReadOnlyCollection<CatalystEvent> items, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
