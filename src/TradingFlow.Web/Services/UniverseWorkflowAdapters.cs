using System.Text.Json;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Strategies;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services;

/// <summary>
/// Adapts operational wishlist and Finviz reads to the application-owned
/// point-in-time universe workflow. It does not persist or admit candidates.
/// </summary>
public sealed class UniverseSourceResolver(
    IWishlistRepository wishlists,
    IScreenerSnapshotSource screener,
    TimeProvider timeProvider) : IUniverseSourceResolver
{
    private static readonly TimeSpan SourceLifetime = TimeSpan.FromMinutes(3);

    public async Task<UniverseSourceCapture> ResolveAsync(
        UniverseSourceSelectionRequest source,
        string horizon,
        CancellationToken cancellationToken = default)
    {
        if (!horizon.Equals("swing", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only swing universe resolution is supported.");
        }

        return source.SourceKind.Trim().ToLowerInvariant() switch
        {
            DiscoverySourceKinds.Wishlist => await ResolveWishlistAsync(source, cancellationToken),
            DiscoverySourceKinds.Finviz => await ResolveFinvizAsync(source, cancellationToken),
            _ => throw new InvalidOperationException(
                $"Universe source '{source.SourceKind}' is not supported by the operator preview API.")
        };
    }

    private async Task<UniverseSourceCapture> ResolveWishlistAsync(
        UniverseSourceSelectionRequest source,
        CancellationToken cancellationToken)
    {
        var id = source.WishlistId ??
                 (Guid.TryParse(source.SourceKey, out var parsed) ? parsed : Guid.Empty);
        if (id == Guid.Empty)
        {
            throw new InvalidOperationException("Wishlist source requires a wishlist ID.");
        }
        var wishlist = await wishlists.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Wishlist {id:D} was not found.");
        var observed = timeProvider.GetUtcNow().ToUniversalTime();
        var symbols = wishlist.Items
            .Where(item => item.Active)
            .Select(item => new DiscoverySymbolObservation(
                item.Ticker.Trim().ToUpperInvariant(),
                JsonSerializer.Serialize(new { wishlistId = id, wishlist.UpdatedAtUtc })))
            .ToArray();
        return new UniverseSourceCapture(
            DiscoverySourceKinds.Wishlist,
            id.ToString("D"),
            observed,
            observed + SourceLifetime,
            symbols,
            ProviderTimestampUtc: wishlist.UpdatedAtUtc.ToUniversalTime(),
            ProviderReference: $"wishlist:{id:D}");
    }

    private async Task<UniverseSourceCapture> ResolveFinvizAsync(
        UniverseSourceSelectionRequest source,
        CancellationToken cancellationToken)
    {
        var query = String.IsNullOrWhiteSpace(source.Query) ? source.SourceKey : source.Query;
        if (String.IsNullOrWhiteSpace(query))
        {
            throw new InvalidOperationException("Finviz source requires a saved-view key or query.");
        }
        var result = await screener.PreviewAsync(query, ScreenerScope.Swing, source.WishlistId, cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.Error ?? "Finviz swing screen failed.");
        }
        var observed = result.SyncedAtUtc.ToUniversalTime();
        var symbols = result.SymbolDetails.Count > 0
            ? result.SymbolDetails.Select(item => new DiscoverySymbolObservation(
                item.Symbol,
                JsonSerializer.Serialize(new { vendorReportedRvol = item.VendorReportedRvol }))).ToArray()
            : result.Symbols.Select(symbol => new DiscoverySymbolObservation(symbol)).ToArray();
        return new UniverseSourceCapture(
            DiscoverySourceKinds.Finviz,
            result.NormalizedQuery,
            observed,
            observed + SourceLifetime,
            symbols,
            ProviderTimestampUtc: result.ProviderTimestampUtc?.ToUniversalTime(),
            ProviderReference: result.RawReference);
    }
}

public sealed class StrategyIdentityResolver(ConfigCatalogService catalog) : IStrategyIdentityResolver
{
    public async Task RequireSwingStrategyAsync(
        StrategyReference strategy,
        string mode,
        CancellationToken cancellationToken = default)
    {
        var selectionMode = mode.Trim().ToLowerInvariant() switch
        {
            "backtest" => StrategySelectionMode.Backtest,
            "paper_experiment" => StrategySelectionMode.RunPaperExperiment,
            "paper_shadow" => StrategySelectionMode.RunPaperShadow,
            "live" => StrategySelectionMode.RunLive,
            "diagnostic_replay" => StrategySelectionMode.DiagnosticReplay,
            _ => throw new InvalidOperationException($"Candidate mode '{mode}' is invalid.")
        };
        var option = (await catalog.GetStrategiesAsync(selectionMode, cancellationToken))
            .SingleOrDefault(item =>
                item.Identity.StrategyId.Equals(strategy.StrategyId, StringComparison.Ordinal) &&
                item.Identity.SemanticVersion.Equals(strategy.SemanticVersion, StringComparison.Ordinal) &&
                item.Identity.ContentSha256.Equals(strategy.ContentSha256, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Strategy '{strategy.StrategyId}@{strategy.SemanticVersion}' is not authorized with the supplied content hash.");
        if (!option.Definition.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only daily-primary swing strategy artifacts are supported.");
        }
    }
}
