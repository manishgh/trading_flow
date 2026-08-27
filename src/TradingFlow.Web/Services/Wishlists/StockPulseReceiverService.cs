using System.Collections.Concurrent;

namespace TradingFlow.Web.Services.Wishlists;

public sealed record StockPulseObservation(
    Guid Id,
    string Ticker,
    string PulseType,
    string Message,
    DateTimeOffset ObservedAtUtc);

public sealed class StockPulseReceiverService(TimeProvider timeProvider)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(2);
    private readonly ConcurrentQueue<StockPulseObservation> recentPulses = new();

    public void RegisterPulse(string ticker, string? pulseType = null, string? message = null)
    {
        if (!string.IsNullOrWhiteSpace(ticker))
        {
            var now = timeProvider.GetUtcNow().ToUniversalTime();
            recentPulses.Enqueue(new StockPulseObservation(
                Guid.NewGuid(),
                ticker.Trim().ToUpperInvariant(),
                pulseType?.Trim() ?? String.Empty,
                message?.Trim() ?? String.Empty,
                now));
            Prune(now.Subtract(Retention));
        }
    }

    public IReadOnlyList<StockPulseObservation> GetRecent(
        DateTimeOffset sinceUtc,
        IReadOnlyCollection<string> allowedTickers)
    {
        var allowed = allowedTickers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutoff = sinceUtc.ToUniversalTime();
        Prune(timeProvider.GetUtcNow().ToUniversalTime().Subtract(Retention));
        return recentPulses
            .Where(item => item.ObservedAtUtc >= cutoff && allowed.Contains(item.Ticker))
            .OrderBy(item => item.ObservedAtUtc)
            .ToArray();
    }

    private void Prune(DateTimeOffset cutoffUtc)
    {
        while (recentPulses.TryPeek(out var oldest) && oldest.ObservedAtUtc < cutoffUtc)
        {
            recentPulses.TryDequeue(out _);
        }
    }
}
