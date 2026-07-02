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
    private readonly WishlistBreakoutEvaluator evaluator;

    public WishlistMarketMonitor(IWishlistRepository wishlists, WishlistBreakoutEvaluator evaluator)
    {
        this.wishlists = wishlists;
        this.evaluator = evaluator;
    }

    public async Task<IReadOnlyList<WishlistBreakoutEvaluation>> EvaluateAsync(
        Guid wishlistId,
        IReadOnlyDictionary<string, WishlistMarketSnapshot> snapshotsByTicker,
        CancellationToken cancellationToken)
    {
        var wishlist = await wishlists.GetByIdAsync(wishlistId, cancellationToken) ??
            throw new InvalidOperationException($"Wishlist {wishlistId} does not exist.");

        var results = new List<WishlistBreakoutEvaluation>();
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
        var dedupeWindowStart = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(15));

        foreach (var evaluation in evaluations.Where(evaluation => evaluation.ShouldAlert))
        {
            var recent = await wishlists.GetSignalsAsync(wishlistId, evaluation.Ticker, dedupeWindowStart, 20, cancellationToken);
            var duplicate = recent.Any(signal =>
                signal.SignalType.Equals(evaluation.SignalType, StringComparison.OrdinalIgnoreCase) &&
                !signal.Acknowledged);
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
                DetectedAtUtc = DateTimeOffset.UtcNow,
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
    IReadOnlyList<WishlistBreakoutEvaluation> Evaluations,
    IReadOnlyList<WishlistSignal> PersistedSignals);



