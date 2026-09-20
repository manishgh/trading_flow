using TradingFlow.Domain.Wishlists;

namespace TradingFlow.Web.Services.Wishlists;

/// <summary>
/// Evaluates every active ticker in a wishlist against the latest prepared market
/// snapshots. This service is intentionally ingestion-agnostic: today it can be
/// fed by REST/backtest state, and later by the single websocket fan-out service.
/// </summary>
public sealed class WishlistMarketMonitor
{
    private readonly IWishlistRepository wishlists;
    private readonly WishlistSwingWatchEvaluator evaluator;
    private readonly TimeProvider timeProvider;

    public WishlistMarketMonitor(IWishlistRepository wishlists, WishlistSwingWatchEvaluator evaluator)
        : this(wishlists, evaluator, TimeProvider.System)
    {
    }

    public WishlistMarketMonitor(
        IWishlistRepository wishlists,
        WishlistSwingWatchEvaluator evaluator,
        TimeProvider timeProvider)
    {
        this.wishlists = wishlists;
        this.evaluator = evaluator;
        this.timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<WishlistSwingWatchEvaluation>> EvaluateAsync(
        Guid wishlistId,
        IReadOnlyDictionary<string, WishlistMarketSnapshot> snapshotsByTicker,
        CancellationToken cancellationToken)
    {
        var wishlist = await wishlists.GetByIdAsync(wishlistId, cancellationToken) ??
            throw new InvalidOperationException($"Wishlist {wishlistId} does not exist.");

        var results = new List<WishlistSwingWatchEvaluation>();
        foreach (var item in wishlist.Items.Where(item => item.Active).OrderBy(item => item.Ticker))
        {
            if (snapshotsByTicker.TryGetValue(item.Ticker, out var snapshot))
            {
                results.Add(evaluator.Evaluate(snapshot));
            }
        }

        return results;
    }
    public async Task<WishlistMonitorPersistenceResult> EvaluateAndPersistAlertsAsync(
        Guid wishlistId,
        IReadOnlyDictionary<string, WishlistMarketSnapshot> snapshotsByTicker,
        CancellationToken cancellationToken)
    {
        var evaluations = await EvaluateAsync(wishlistId, snapshotsByTicker, cancellationToken);
        var persisted = new List<WishlistSignal>();
        var now = timeProvider.GetUtcNow();

        foreach (var evaluation in evaluations.Where(evaluation => evaluation.ShouldAlert))
        {
            var dedupeWindowStart = evaluation.SourceBarTimestampUtc < now.Subtract(TimeSpan.FromMinutes(15))
                ? evaluation.SourceBarTimestampUtc
                : now.Subtract(TimeSpan.FromMinutes(15));
            var recent = await wishlists.GetSignalsAsync(
                wishlistId,
                evaluation.Ticker,
                dedupeWindowStart,
                100,
                cancellationToken);
            var duplicate = recent.Any(signal =>
                signal.SignalType.Equals(evaluation.SignalType, StringComparison.OrdinalIgnoreCase) &&
                (String.Equals(signal.SnapshotJson, evaluation.SnapshotJson, StringComparison.Ordinal) ||
                 (!signal.Acknowledged && signal.DetectedAtUtc >= now.Subtract(TimeSpan.FromMinutes(15)))));
            if (duplicate)
            {
                continue;
            }

            persisted.Add(await wishlists.AddSignalAsync(new WishlistSignal
            {
                WishlistId = wishlistId,
                Ticker = evaluation.Ticker,
                SignalType = evaluation.SignalType,
                Severity = evaluation.Severity,
                DetectedAtUtc = now,
                Price = evaluation.Price,
                Reason = evaluation.Reason,
                SnapshotJson = evaluation.SnapshotJson,
                NewsHeadline = evaluation.NewsHeadline,
                NewsUrl = evaluation.NewsUrl,
                NewsProvider = evaluation.NewsProvider,
                Acknowledged = false
            }, cancellationToken));
        }

        return new WishlistMonitorPersistenceResult(evaluations, persisted);
    }
}

public sealed record WishlistMonitorPersistenceResult(
    IReadOnlyList<WishlistSwingWatchEvaluation> Evaluations,
    IReadOnlyList<WishlistSignal> PersistedSignals);



