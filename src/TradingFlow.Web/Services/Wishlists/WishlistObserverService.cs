using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Text.Json;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Pipeline;

namespace TradingFlow.Web.Services.Wishlists;

/// <summary>
/// Durable server-side observer for named wishlist groups. The mobile app only
/// toggles intent; this service owns the 24x7 polling loop so monitoring
/// survives app close and evaluates completed daily swing evidence.
/// </summary>
public sealed class WishlistObserverService : BackgroundService
{
    private static readonly Guid ObservationScope =
        Guid.Parse("d1a12e6e-20f8-42f8-8c1d-5166d80b3fc8");
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NewsWindow = TimeSpan.FromHours(4);
    private readonly IWishlistRepository wishlists;
    private readonly WishlistMarketMonitor monitor;
    private readonly INewsFeedRepository newsRepository;
    private readonly IDiscoverySubscriptionSink subscriptions;
    private readonly IMarketStateSnapshotProvider marketState;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<WishlistObserverService> logger;
    private readonly ConcurrentDictionary<(Guid WishlistId, string Ticker), string> lastEvaluatedEvidence = [];

    public WishlistObserverService(
        IWishlistRepository wishlists,
        WishlistMarketMonitor monitor,
        INewsFeedRepository newsRepository,
        IDiscoverySubscriptionSink subscriptions,
        IMarketStateSnapshotProvider marketState,
        TimeProvider timeProvider,
        ILogger<WishlistObserverService> logger)
    {
        this.wishlists = wishlists;
        this.monitor = monitor;
        this.newsRepository = newsRepository;
        this.subscriptions = subscriptions;
        this.marketState = marketState;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ObserveOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Wishlist observer iteration failed.");
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        finally
        {
            await subscriptions.RemoveScopeAsync(ObservationScope, CancellationToken.None);
        }
    }

    public async Task ObserveOnceAsync(CancellationToken cancellationToken)
    {
        var observed = (await wishlists.ListAsync(cancellationToken))
            .Where(wishlist => wishlist.IsObserved)
            .ToArray();
        if (observed.Length == 0)
        {
            await subscriptions.RemoveScopeAsync(ObservationScope, cancellationToken);
            return;
        }

        var tickers = observed
            .SelectMany(wishlist => wishlist.Items)
            .Where(item => item.Active)
            .Select(item => item.Ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tickers.Length == 0)
        {
            await subscriptions.RemoveScopeAsync(ObservationScope, cancellationToken);
            return;
        }

        await subscriptions.ReplaceScopeAsync(ObservationScope, tickers, cancellationToken);
        var end = timeProvider.GetUtcNow();
        var preparedByTicker = new Dictionary<string, PreparedWishlistSnapshot>(StringComparer.OrdinalIgnoreCase);
        var failedTickerCount = 0;
        foreach (var ticker in tickers)
        {
            try
            {
                var state = await marketState.GetTickerStateAsync(
                    ticker,
                    ["1d"],
                    minimumBarsPerTimeframe: 260,
                    asOfUtc: end,
                    cancellationToken: cancellationToken);
                if (state is null)
                {
                    continue;
                }

                var prepared = await BuildSnapshotAsync(ticker, state, cancellationToken);
                if (prepared is not null)
                {
                    preparedByTicker[ticker] = prepared;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failedTickerCount++;
                logger.LogWarning(
                    exception,
                    "Wishlist observer could not prepare market evidence for {Ticker}; other tickers will continue.",
                    ticker);
            }
        }

        var activeKeys = observed
            .SelectMany(wishlist => wishlist.Items
                .Where(item => item.Active)
                .Select(item => (wishlist.Id, item.Ticker.Trim().ToUpperInvariant())))
            .ToHashSet();
        foreach (var existing in lastEvaluatedEvidence.Keys.Where(key => !activeKeys.Contains(key)))
        {
            lastEvaluatedEvidence.TryRemove(existing, out _);
        }

        var evaluatedTickerCount = 0;
        foreach (var wishlist in observed)
        {
            var snapshots = new Dictionary<string, WishlistMarketSnapshot>(StringComparer.OrdinalIgnoreCase);
            var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ticker in wishlist.Items.Where(item => item.Active).Select(item => item.Ticker))
            {
                var normalizedTicker = ticker.Trim().ToUpperInvariant();
                if (!preparedByTicker.TryGetValue(normalizedTicker, out var prepared))
                {
                    continue;
                }

                var key = (wishlist.Id, normalizedTicker);
                if (!lastEvaluatedEvidence.TryGetValue(key, out var priorFingerprint) ||
                    !String.Equals(priorFingerprint, prepared.EvidenceFingerprint, StringComparison.Ordinal))
                {
                    snapshots[normalizedTicker] = prepared.Snapshot;
                    fingerprints[normalizedTicker] = prepared.EvidenceFingerprint;
                }
            }

            if (snapshots.Count == 0)
            {
                continue;
            }

            var result = await monitor.EvaluateAndPersistAlertsAsync(wishlist.Id, snapshots, cancellationToken);
            evaluatedTickerCount += snapshots.Count;
            foreach (var fingerprint in fingerprints)
            {
                lastEvaluatedEvidence[(wishlist.Id, fingerprint.Key)] = fingerprint.Value;
            }

            if (result.PersistedSignals.Count > 0)
            {
                logger.LogInformation(
                    "Wishlist observer persisted {SignalCount} alert(s) for {Wishlist}.",
                    result.PersistedSignals.Count,
                    wishlist.Name);
            }
        }

        logger.LogInformation(
            "Wishlist observer completed. Requested={RequestedTickerCount}, ready={ReadyTickerCount}, evaluated={EvaluatedTickerCount}, unavailable_or_warming={UnavailableTickerCount}, failed={FailedTickerCount}.",
            tickers.Length,
            preparedByTicker.Count,
            evaluatedTickerCount,
            tickers.Length - preparedByTicker.Count - failedTickerCount,
            failedTickerCount);
    }

    private async Task<PreparedWishlistSnapshot?> BuildSnapshotAsync(
        string ticker,
        TickerMarketState marketState,
        CancellationToken cancellationToken)
    {
        var bars = marketState.BarsByTimeframe.GetValueOrDefault("1d") ?? [];
        var snapshots = marketState.SnapshotsByTimeframe.GetValueOrDefault("1d") ?? [];
        if (bars.Count == 0 || snapshots.Count < 2)
        {
            return null;
        }

        var current = snapshots[^1];
        var previous = snapshots[^2];
        var catalyst = await GetLatestCatalystAsync(ticker, cancellationToken);
        if (catalyst is not null)
        {
            current = current with { Catalyst = catalyst };
        }

        var recentHigh = bars
            .Take(Math.Max(0, bars.Count - 1))
            .TakeLast(20)
            .Select(bar => (decimal?)bar.High)
            .Max();

        var snapshot = new WishlistMarketSnapshot(ticker, current, previous, recentHigh);
        return new PreparedWishlistSnapshot(snapshot, BuildEvidenceFingerprint(snapshot));
    }

    private async Task<CatalystEvent?> GetLatestCatalystAsync(string ticker, CancellationToken cancellationToken)
    {
        var recent = await newsRepository.GetRecentAsync(
            timeProvider.GetUtcNow().Subtract(NewsWindow),
            10,
            ticker,
            cancellationToken);
        var item = recent.FirstOrDefault(news => news.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase))
            ?? recent.FirstOrDefault(news => news.Ticker.Equals("MARKET", StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return null;
        }

        return new CatalystEvent(
            ticker,
            item.Timestamp,
            CatalystType.NewsReport,
            item.Headline,
            item.SentimentScore,
            Provider: item.Provider,
            Summary: item.Summary,
            Source: item.Source,
            Url: item.Url,
            ReceivedAt: item.IngestedAt);
    }

    private static string BuildEvidenceFingerprint(WishlistMarketSnapshot input) =>
        JsonSerializer.Serialize(new
        {
            current = DecisionFields(input.Current),
            previousMacdHistogram = input.Previous?.MacdHistogram,
            input.RecentHigh
        });

    private static object DecisionFields(IndicatorSnapshot snapshot) => new
    {
        timestampUtc = snapshot.Timestamp.ToUniversalTime(),
        snapshot.CurrentPrice,
        snapshot.CurrentVolume,
        snapshot.Vwap,
        snapshot.Atr,
        snapshot.Ema10,
        snapshot.Ema20,
        snapshot.MacdHistogram,
        snapshot.RelativeVolume,
        catalyst = snapshot.Catalyst is null ? null : new
        {
            timestampUtc = snapshot.Catalyst.Timestamp.ToUniversalTime(),
            snapshot.Catalyst.Headline,
            snapshot.Catalyst.Provider,
            snapshot.Catalyst.Source,
            snapshot.Catalyst.Url,
            snapshot.Catalyst.SentimentScore,
            receivedAtUtc = snapshot.Catalyst.ReceivedAt?.ToUniversalTime()
        }
    };

    private sealed record PreparedWishlistSnapshot(
        WishlistMarketSnapshot Snapshot,
        string EvidenceFingerprint);
}
