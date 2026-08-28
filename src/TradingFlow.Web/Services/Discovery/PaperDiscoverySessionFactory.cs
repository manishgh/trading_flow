using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TradingFlow.Backtesting.Discovery;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Earnings;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services.Discovery;

public interface IPaperDiscoverySessionFactory
{
    ILiveDiscoverySession Create(Guid scopeId, BacktestRunConfig run, string horizon);
}

/// <summary>
/// Composes run-scoped source adapters. Provider calls stay outside LiveRunner;
/// the runner sees only the durable, deduplicated discovery universe.
/// </summary>
public sealed class PaperDiscoverySessionFactory(
    IDiscoveryRepository repository,
    IWishlistRepository wishlists,
    IScreenerSnapshotSource screener,
    INewsFeedRepository news,
    IEarningsRepository earnings,
    StockPulseReceiverService stockPulses,
    IDiscoverySubscriptionSink subscriptions,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : IPaperDiscoverySessionFactory
{
    public ILiveDiscoverySession Create(Guid scopeId, BacktestRunConfig run, string horizon)
    {
        ArgumentNullException.ThrowIfNull(run);
        var config = run.Discovery;
        var sources = new List<IDiscoverySource>();
        if (config is { Enabled: true })
        {
            var refresh = TimeSpan.FromSeconds(config.RefreshSeconds);
            var ttl = TimeSpan.FromSeconds(config.SourceExpirySeconds);
            if (config.WishlistId is { } wishlistId)
            {
                sources.Add(new WishlistDiscoverySource(
                    scopeId,
                    wishlistId,
                    config.WishlistTickers,
                    horizon,
                    refresh,
                    ttl,
                    repository,
                    wishlists,
                    timeProvider));
            }

            if (!String.IsNullOrWhiteSpace(config.FinvizQuery))
            {
                sources.Add(new FinvizDiscoverySource(
                    scopeId,
                    config.FinvizQuery,
                    config.FinvizTickers,
                    horizon,
                    refresh,
                    ttl,
                    config.WishlistId,
                    repository,
                    screener,
                    timeProvider));
            }

            AddConfigured(sources, DiscoverySourceKinds.Operator, config.OperatorTickers, horizon, refresh, ttl);
            if (config.AlertTickers.Count > 0)
            {
                sources.Add(new AlertDiscoverySource(
                    config.AlertTickers,
                    horizon,
                    refresh,
                    ttl,
                    stockPulses,
                    timeProvider));
            }

            if (config.NewsTickers.Count > 0)
            {
                sources.Add(new NewsDiscoverySource(
                    config.NewsTickers,
                    horizon,
                    refresh,
                    ttl,
                    news,
                    timeProvider));
            }

            if (config.EarningsTickers.Count > 0)
            {
                sources.Add(new EarningsDiscoverySource(
                    config.EarningsTickers,
                    horizon,
                    refresh,
                    ttl,
                    earnings,
                    timeProvider));
            }
        }

        if (sources.Count == 0)
        {
            AddConfigured(
                sources,
                DiscoverySourceKinds.Operator,
                run.Tickers,
                horizon,
                TimeSpan.FromSeconds(DiscoveryRuntimeConfig.DefaultRefreshSeconds),
                TimeSpan.FromSeconds(DiscoveryRuntimeConfig.DefaultSourceExpirySeconds));
        }

        return new DurableDiscoverySession(
            scopeId,
            repository,
            sources,
            subscriptions,
            timeProvider,
            loggerFactory.CreateLogger<DurableDiscoverySession>());
    }

    private void AddConfigured(
        ICollection<IDiscoverySource> target,
        string sourceKind,
        IReadOnlyList<string> symbols,
        string horizon,
        TimeSpan refresh,
        TimeSpan ttl)
    {
        if (symbols.Count > 0)
        {
            target.Add(new ConfiguredDiscoverySource(
                sourceKind,
                $"run-{sourceKind}",
                symbols,
                horizon,
                refresh,
                ttl,
                timeProvider));
        }
    }
}

internal abstract class DiscoverySourceBase(
    string sourceKind,
    string sourceKey,
    string horizon,
    TimeSpan refreshInterval,
    TimeSpan timeToLive,
    TimeProvider timeProvider) : IDiscoverySource
{
    protected TimeProvider TimeProvider { get; } = timeProvider;

    public string SourceKind { get; } = sourceKind;
    public string SourceKey { get; } = sourceKey;
    public string Horizon { get; } = horizon;
    public TimeSpan RefreshInterval { get; } = refreshInterval;
    public TimeSpan TimeToLive { get; } = timeToLive;
    public virtual bool IsDiagnostic => false;

    public abstract Task<DiscoveryCapture> CaptureAsync(CancellationToken cancellationToken);

    protected DiscoveryCapture Capture(
        DateTimeOffset observedAtUtc,
        IEnumerable<string> symbols,
        DateTimeOffset? providerTimestampUtc = null,
        string? rawReference = null)
    {
        var normalized = symbols
            .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
            .Select(symbol => symbol.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .Select(symbol => new DiscoverySymbolObservation(symbol))
            .ToArray();
        var identity = BuildObservationIdentity(
            observedAtUtc,
            normalized,
            providerTimestampUtc,
            rawReference);
        return new DiscoveryCapture(
            DeterministicGuid(identity),
            observedAtUtc.ToUniversalTime(),
            normalized,
            providerTimestampUtc?.ToUniversalTime(),
            rawReference);
    }

    protected DiscoveryCapture CaptureObservations(
        DateTimeOffset observedAtUtc,
        IReadOnlyCollection<DiscoverySymbolObservation> observations,
        DateTimeOffset? providerTimestampUtc = null,
        string? rawReference = null)
    {
        var normalized = observations
            .Where(item => !String.IsNullOrWhiteSpace(item.Symbol))
            .Select(item => item with { Symbol = item.Symbol.Trim().ToUpperInvariant() })
            .GroupBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.Symbol, StringComparer.Ordinal)
            .ToArray();
        var identity = BuildObservationIdentity(
            observedAtUtc,
            normalized,
            providerTimestampUtc,
            rawReference);
        return new DiscoveryCapture(
            DeterministicGuid(identity),
            observedAtUtc.ToUniversalTime(),
            normalized,
            providerTimestampUtc?.ToUniversalTime(),
            rawReference);
    }

    private string BuildObservationIdentity(
        DateTimeOffset observedAtUtc,
        IReadOnlyCollection<DiscoverySymbolObservation> observations,
        DateTimeOffset? providerTimestampUtc,
        string? rawReference) => String.Join(
            '\u001f',
            SourceKind,
            SourceKey,
            observedAtUtc.ToUniversalTime().UtcTicks,
            providerTimestampUtc?.ToUniversalTime().UtcTicks,
            rawReference,
            String.Join(',', observations.Select(item => $"{item.Symbol}:{item.MetadataJson}")));

    private static Guid DeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

internal sealed class ConfiguredDiscoverySource(
    string sourceKind,
    string sourceKey,
    IReadOnlyList<string> symbols,
    string horizon,
    TimeSpan refreshInterval,
    TimeSpan timeToLive,
    TimeProvider timeProvider)
    : DiscoverySourceBase(sourceKind, sourceKey, horizon, refreshInterval, timeToLive, timeProvider)
{
    public override Task<DiscoveryCapture> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Capture(TimeProvider.GetUtcNow(), symbols));
    }
}

internal sealed class WishlistDiscoverySource(
    Guid scopeId,
    Guid wishlistId,
    IReadOnlyList<string> initialSymbols,
    string horizon,
    TimeSpan refreshInterval,
    TimeSpan timeToLive,
    IDiscoveryRepository repository,
    IWishlistRepository wishlists,
    TimeProvider timeProvider)
    : DiscoverySourceBase(
        DiscoverySourceKinds.Wishlist,
        wishlistId.ToString("D"),
        horizon,
        refreshInterval,
        timeToLive,
        timeProvider)
{
    public override async Task<DiscoveryCapture> CaptureAsync(CancellationToken cancellationToken)
    {
        var observedAt = TimeProvider.GetUtcNow();
        if (initialSymbols.Count > 0 && await repository.GetSourceVersionAsync(
                scopeId,
                SourceKind,
                SourceKey,
                cancellationToken) == 0)
        {
            return Capture(observedAt, initialSymbols, rawReference: $"wishlist:{wishlistId}:run-start");
        }

        var wishlist = await wishlists.GetByIdAsync(wishlistId, cancellationToken)
            ?? throw new InvalidOperationException($"Wishlist {wishlistId} no longer exists.");
        return Capture(
            observedAt,
            wishlist.Items.Where(item => item.Active).Select(item => item.Ticker),
            providerTimestampUtc: wishlist.UpdatedAtUtc,
            rawReference: $"wishlist:{wishlistId}");
    }
}

internal sealed class FinvizDiscoverySource(
    Guid scopeId,
    string query,
    IReadOnlyList<string> initialSymbols,
    string horizon,
    TimeSpan refreshInterval,
    TimeSpan timeToLive,
    Guid? wishlistId,
    IDiscoveryRepository repository,
    IScreenerSnapshotSource screener,
    TimeProvider timeProvider)
    : DiscoverySourceBase(
        DiscoverySourceKinds.Finviz,
        query,
        horizon,
        refreshInterval,
        timeToLive,
        timeProvider)
{
    public override async Task<DiscoveryCapture> CaptureAsync(CancellationToken cancellationToken)
    {
        var observedAt = TimeProvider.GetUtcNow();
        var mayUseRunStartFallback = initialSymbols.Count > 0 &&
            await repository.GetSourceVersionAsync(
                scopeId,
                SourceKind,
                SourceKey,
                cancellationToken) == 0;

        var scope = Horizon.Equals("swing", StringComparison.OrdinalIgnoreCase)
            ? ScreenerScope.Swing
            : ScreenerScope.Intraday;
        var result = await screener.PreviewAsync(SourceKey, scope, wishlistId, cancellationToken);
        if (!result.Succeeded)
        {
            if (mayUseRunStartFallback)
            {
                return Capture(
                    observedAt,
                    initialSymbols,
                    rawReference: $"finviz:{SourceKey}:run-start-fallback");
            }

            throw new InvalidOperationException(result.Error ?? "Finviz discovery refresh failed.");
        }

        var observations = result.SymbolDetails.Count > 0
            ? result.SymbolDetails.Select(item => new DiscoverySymbolObservation(
                    item.Symbol,
                    System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, decimal?>
                    {
                        ["vendor_reported_rvol"] = item.VendorReportedRvol
                    })))
                .ToArray()
            : result.Symbols.Select(symbol => new DiscoverySymbolObservation(symbol)).ToArray();
        return CaptureObservations(
            result.SyncedAtUtc,
            observations,
            providerTimestampUtc: result.ProviderTimestampUtc ?? result.SyncedAtUtc,
            rawReference: result.RawReference ?? $"finviz:{result.NormalizedQuery}");
    }
}

internal sealed class AlertDiscoverySource(
    IReadOnlyList<string> allowedTickers,
    string horizon,
    TimeSpan refreshInterval,
    TimeSpan timeToLive,
    StockPulseReceiverService pulses,
    TimeProvider timeProvider)
    : DiscoverySourceBase(
        DiscoverySourceKinds.Alert,
        "stock-pulse",
        horizon,
        refreshInterval,
        timeToLive,
        timeProvider)
{
    public override Task<DiscoveryCapture> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = TimeProvider.GetUtcNow().ToUniversalTime();
        var recent = pulses.GetRecent(now.Subtract(TimeToLive), allowedTickers);
        if (recent.Count == 0)
        {
            throw new DiscoveryNoObservationException(
                "No Stock Pulse observation exists inside the configured source TTL.");
        }

        var observations = recent
            .GroupBy(item => item.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group.MaxBy(item => item.ObservedAtUtc)!;
                return new DiscoverySymbolObservation(
                    group.Key,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        alertId = latest.Id,
                        latest.PulseType,
                        latest.Message,
                        latest.ObservedAtUtc
                    }));
            })
            .ToArray();
        var latestObservedAt = recent.Max(item => item.ObservedAtUtc).ToUniversalTime();
        return Task.FromResult(CaptureObservations(
            latestObservedAt,
            observations,
            latestObservedAt,
            $"stock-pulse:{recent[^1].Id:D}"));
    }
}

internal sealed class NewsDiscoverySource(
    IReadOnlyList<string> allowedTickers,
    string horizon,
    TimeSpan refreshInterval,
    TimeSpan timeToLive,
    INewsFeedRepository news,
    TimeProvider timeProvider)
    : DiscoverySourceBase(
        DiscoverySourceKinds.News,
        "operational-news",
        horizon,
        refreshInterval,
        timeToLive,
        timeProvider)
{
    public override async Task<DiscoveryCapture> CaptureAsync(CancellationToken cancellationToken)
    {
        var now = TimeProvider.GetUtcNow().ToUniversalTime();
        var recent = await news.GetIngestedSinceForTickersAsync(
            now.Subtract(TimeToLive),
            Math.Max(100, allowedTickers.Count * 20),
            allowedTickers,
            cancellationToken);
        var observations = recent
            .GroupBy(item => item.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group.MaxBy(item => item.IngestedAt)!;
                return new DiscoverySymbolObservation(
                    group.Key,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        newsId = latest.Id,
                        latest.Provider,
                        publishedAtUtc = latest.Timestamp,
                        latest.IngestedAt,
                        latest.Url
                    }));
            })
            .ToArray();
        return CaptureObservations(
            now,
            observations,
            recent.Count > 0 ? recent.Max(item => item.Timestamp) : null,
            recent.Count > 0 ? $"news:{recent.MaxBy(item => item.IngestedAt)!.Id}" : "news:empty");
    }
}

internal sealed class EarningsDiscoverySource(
    IReadOnlyList<string> allowedTickers,
    string horizon,
    TimeSpan refreshInterval,
    TimeSpan timeToLive,
    IEarningsRepository earnings,
    TimeProvider timeProvider)
    : DiscoverySourceBase(
        DiscoverySourceKinds.Earnings,
        "operational-earnings",
        horizon,
        refreshInterval,
        timeToLive,
        timeProvider)
{
    public override async Task<DiscoveryCapture> CaptureAsync(CancellationToken cancellationToken)
    {
        var now = TimeProvider.GetUtcNow().ToUniversalTime();
        var recentCutoff = now.Subtract(TimeToLive);
        var events = await earnings.GetCalendarAsync(
            DateOnly.FromDateTime(now.UtcDateTime.Date.AddDays(-2)),
            DateOnly.FromDateTime(now.UtcDateTime.Date.AddDays(2)),
            allowedTickers,
            cancellationToken);
        var recent = events
            .Where(item => item.LastSeenAtUtc >= recentCutoff || item.ResultFirstSeenAtUtc >= recentCutoff)
            .ToArray();
        var observations = recent
            .GroupBy(item => item.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group.MaxBy(item => item.LastSeenAtUtc)!;
                return new DiscoverySymbolObservation(
                    group.Key,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        earningsEventId = latest.Id,
                        latest.Provider,
                        latest.ScheduledAtUtc,
                        latest.ResultFirstSeenAtUtc,
                        latest.LastSeenAtUtc,
                        latest.SourceArtifactSha256
                    }));
            })
            .ToArray();
        return CaptureObservations(
            now,
            observations,
            recent.Length > 0 ? recent.Max(item => item.LastSeenAtUtc) : null,
            recent.Length > 0 ? $"earnings:{recent.MaxBy(item => item.LastSeenAtUtc)!.Id}" : "earnings:empty");
    }
}
