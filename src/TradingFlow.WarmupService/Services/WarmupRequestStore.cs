using Microsoft.Extensions.Options;
using TradingFlow.WarmupService.Models;

namespace TradingFlow.WarmupService.Services;

public sealed class WarmupRequestStore(IOptions<WarmupOptions> options)
    : WarmupJsonStore<WarmupTickerIntent>(options, "watchlist.json")
{
    private readonly WarmupOptions options = options.Value;

    public Task<IReadOnlyList<WarmupTickerIntent>> UpsertAsync(
        WarmupWatchRequest request,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var requestedBy = String.IsNullOrWhiteSpace(request.RequestedBy) ? "manual" : request.RequestedBy!.Trim();
        var reason = String.IsNullOrWhiteSpace(request.Reason) ? "possible trend candidate" : request.Reason!.Trim();
        var timeframes = ResolveTimeframes(request.Timeframes);
        var tickers = request.Tickers
            .Select(WarmupText.NormalizeTicker)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return MutateAsync(items =>
        {
            var upserted = new List<WarmupTickerIntent>();
            foreach (var ticker in tickers)
            {
                var updated = new WarmupTickerIntent(
                    ticker,
                    reason,
                    requestedBy,
                    now,
                    Math.Max(1, request.WarmupDays ?? options.DefaultWarmupDays),
                    Math.Max(0, request.NewsLookbackDays ?? options.DefaultNewsLookbackDays),
                    timeframes,
                    request.IncludeNews ?? options.IncludeNewsByDefault,
                    Active: true);

                var index = items.FindIndex(x => x.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    var existing = items[index];
                    items[index] = updated with
                    {
                        LastWarmedAtUtc = existing.LastWarmedAtUtc,
                        LastStatus = existing.LastStatus,
                        LastError = existing.LastError
                    };
                }
                else
                {
                    items.Add(updated);
                }

                upserted.Add(updated);
            }

            return upserted;
        }, cancellationToken);
    }

    public async Task<bool> RemoveAsync(string ticker, CancellationToken cancellationToken)
    {
        var normalized = WarmupText.NormalizeTicker(ticker);
        var removed = false;
        await MutateAsync(items =>
        {
            removed = items.RemoveAll(x => x.Ticker.Equals(normalized, StringComparison.OrdinalIgnoreCase)) > 0;
            return Array.Empty<WarmupTickerIntent>();
        }, cancellationToken);
        return removed;
    }

    public Task MarkResultsAsync(IReadOnlyList<WarmupTickerResult> results, CancellationToken cancellationToken)
    {
        return MutateAsync(items =>
        {
            foreach (var result in results)
            {
                var index = items.FindIndex(x => x.Ticker.Equals(result.Ticker, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    continue;
                }

                items[index] = items[index] with
                {
                    LastWarmedAtUtc = DateTimeOffset.UtcNow,
                    LastStatus = result.Succeeded ? "warmed" : "failed",
                    LastError = result.Error
                };
            }

            return Array.Empty<WarmupTickerIntent>();
        }, cancellationToken);
    }

    private IReadOnlyList<string> ResolveTimeframes(IReadOnlyCollection<string>? requested)
    {
        return (requested is { Count: > 0 } ? requested : options.DefaultTimeframes)
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
