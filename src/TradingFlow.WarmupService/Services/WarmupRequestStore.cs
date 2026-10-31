using Microsoft.Extensions.Options;
using TradingFlow.WarmupService.Models;

namespace TradingFlow.WarmupService.Services;

public sealed class WarmupRequestStore(IOptions<WarmupOptions> options)
    : WarmupJsonStore<WarmupTickerIntent>(options, "watchlist.json")
{
    private readonly WarmupOptions options = options.Value;

    /// <summary>Resolves all accepted ticker settings once so queue restart cannot pick up later watchlist edits.</summary>
    public async Task<IReadOnlyList<WarmupTickerIntent>> ResolveIntentsAsync(WarmupJobRequest job, CancellationToken token)
    {
        var all = (await ReadAsync(token)).Where(item => item.Active).ToArray();
        if (job.Tickers is null || job.Tickers.Count == 0) return all;
        var requested = job.Tickers.Select(WarmupText.NormalizeTicker).Where(ticker => ticker.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = all.Where(item => requested.Contains(item.Ticker)).ToList();
        existing.AddRange(requested.Except(existing.Select(item => item.Ticker), StringComparer.OrdinalIgnoreCase)
            .Select(ticker => new WarmupTickerIntent(ticker, job.Reason, "run-now", job.QueuedAtUtc,
                options.DefaultWarmupDays, options.DefaultNewsLookbackDays,
                ResolveTimeframes(null), options.IncludeNewsByDefault, true)));
        return existing.OrderBy(item => item.Ticker, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task<IReadOnlyList<WarmupTickerIntent>> UpsertAsync(
        WarmupWatchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.WarmupDays is <= 0 || request.NewsLookbackDays is < 0)
            throw new ArgumentException("Warmup days must be positive and news lookback cannot be negative.");
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
                    request.WarmupDays ?? options.DefaultWarmupDays,
                    request.NewsLookbackDays ?? options.DefaultNewsLookbackDays,
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

    /// <summary>Marks only the accepted settings warmed; a later watchlist edit cannot inherit an older job's success.</summary>
    public Task MarkResultsAsync(IReadOnlyList<WarmupTickerResult> results, IReadOnlyList<WarmupTickerIntent> accepted,
        CancellationToken cancellationToken)
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
                var original = accepted.FirstOrDefault(item => item.Ticker.Equals(result.Ticker, StringComparison.OrdinalIgnoreCase));
                if (original is null || items[index].RequestedAtUtc != original.RequestedAtUtc ||
                    items[index].WarmupDays != original.WarmupDays || items[index].NewsLookbackDays != original.NewsLookbackDays ||
                    items[index].IncludeNews != original.IncludeNews || !items[index].Timeframes.SequenceEqual(original.Timeframes, StringComparer.OrdinalIgnoreCase))
                    continue;

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

    /// <summary>Uses the configured warmup intervals once when accepting a request.</summary>
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
