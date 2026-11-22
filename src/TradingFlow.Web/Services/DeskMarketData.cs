using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Regime;

namespace TradingFlow.Web.Services;

/// <summary>
/// Shared read-side rules for the workspace status strip, the options/ADR cards and the sector ranking, so all three
/// decide "is this bar finished" and "which exchange sessions exist" the same way.
/// </summary>
public static class DeskMarketData
{
    /// <summary>
    /// True only when a bar has both finished its timeframe window and become known by <paramref name="now"/>; a daily bar
    /// dated D is finished at D + 1 day (UTC), never during its own session.
    /// </summary>
    public static bool IsCompletedBar(OhlcvBar bar, DateTimeOffset now) =>
        bar.Timestamp.Add(TimeframeParser.Parse(bar.Timeframe)) <= now &&
        (bar.KnownAtUtc is null || bar.KnownAtUtc <= now);

    /// <summary>
    /// Same as <see cref="TryLoadSchedulesAsync"/> but reuses a successful calendar for the rest of the New York day, so the
    /// one-minute status-strip refresh does not call (and archive) the provider's calendar endpoint every minute.
    /// Failures are not cached.
    /// </summary>
    public static async Task<(bool Succeeded, IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? Schedules)> TryLoadSchedulesCachedAsync(
        IMarketDataProvider provider,
        DateTimeOffset now,
        int daysBack,
        IMemoryCache cache,
        CancellationToken deadlineToken,
        ILogger? logger = null)
    {
        var nyToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, VixRegime.NewYorkTimeZone).DateTime);
        var key = $"exchange-calendar:{nyToday:yyyy-MM-dd}:{daysBack}";
        if (cache.TryGetValue(key, out IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? hit) && hit is not null) return (true, hit);
        var loaded = await TryLoadSchedulesAsync(provider, now, daysBack, deadlineToken, logger: logger);
        if (loaded.Succeeded && loaded.Schedules is { } schedules) cache.Set(key, schedules, TimeSpan.FromHours(6));
        return loaded;
    }

    /// <summary>
    /// Loads the provider's exchange calendar from <paramref name="daysBack"/> New York days before <paramref name="now"/>
    /// to tomorrow. Providers without a calendar succeed with no schedules (callers then use bar dates). An empty result,
    /// an error or the deadline fails closed with <c>Succeeded = false</c> and a logged reason; only the caller's own
    /// cancellation is rethrown.
    /// </summary>
    public static async Task<(bool Succeeded, IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? Schedules)> TryLoadSchedulesAsync(
        IMarketDataProvider provider,
        DateTimeOffset now,
        int daysBack,
        CancellationToken deadlineToken,
        CancellationToken callerToken = default,
        ILogger? logger = null)
    {
        if (provider is not IMarketSessionScheduleProvider scheduleProvider)
        {
            return (true, null);
        }

        var nyToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, VixRegime.NewYorkTimeZone).DateTime);
        try
        {
            var schedules = await scheduleProvider.LoadMarketSessionSchedulesAsync(nyToday.AddDays(-daysBack), nyToday.AddDays(1), deadlineToken);
            if (schedules.Count > 0) return (true, schedules);
            logger?.LogWarning("Exchange calendar for {From}..{To} came back empty; dependent values are shown as unavailable.",
                nyToday.AddDays(-daysBack), nyToday.AddDays(1));
            return (false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !callerToken.IsCancellationRequested)
        {
            logger?.LogWarning(ex, "Exchange calendar for {From}..{To} could not be read ({ErrorType}); dependent values are shown as unavailable.",
                nyToday.AddDays(-daysBack), nyToday.AddDays(1), ex.GetType().Name);
            return (false, null);
        }
    }
}
