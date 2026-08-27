using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services;

/// <summary>
/// The immutable universe admitted to a paper run, including the discovery
/// evidence needed to reproduce why each ticker was present.
/// </summary>
public sealed record PaperRunUniverseSnapshot(
    IReadOnlyList<string> Tickers,
    IReadOnlyList<string> SelectedSourceTickers,
    IReadOnlyList<string> ScreenerTickers,
    string Source,
    string? ScreenerQuery,
    DateTimeOffset ResolvedAtUtc);

/// <summary>
/// Resolves every dynamic paper-run source before the run artifact is written.
/// The live runner therefore receives a frozen symbol list and never changes
/// its universe by re-running a screen after admission.
/// </summary>
public interface IPaperRunUniverseSnapshotResolver
{
    Task<PaperRunUniverseSnapshot> ResolveAsync(
        IEnumerable<string> selectedTickers,
        bool includeSelectedTickers,
        string selectedTickerSource,
        string? screenerInput,
        bool includeScreener,
        Guid? wishlistId,
        string strategyPath,
        CancellationToken cancellationToken);
}

public sealed class PaperRunUniverseSnapshotResolver(
    IScreenerSnapshotSource screener,
    SimpleYamlReader yamlReader,
    TimeProvider timeProvider) : IPaperRunUniverseSnapshotResolver
{
    public async Task<PaperRunUniverseSnapshot> ResolveAsync(
        IEnumerable<string> selectedTickers,
        bool includeSelectedTickers,
        string selectedTickerSource,
        string? screenerInput,
        bool includeScreener,
        Guid? wishlistId,
        string strategyPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedTickers);
        if (String.IsNullOrWhiteSpace(strategyPath))
        {
            throw new InvalidOperationException("Select a strategy before resolving the run universe.");
        }

        var selected = includeSelectedTickers
            ? Normalize(selectedTickers)
            : [];
        IReadOnlyList<string> screened = [];
        string? normalizedQuery = null;
        DateTimeOffset? screenedAtUtc = null;

        if (includeScreener)
        {
            if (String.IsNullOrWhiteSpace(screenerInput))
            {
                throw new InvalidOperationException("Choose a saved screen or paste a Finviz query for a screener universe.");
            }

            var strategy = yamlReader.ReadStrategy(strategyPath);
            var scope = strategy.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase)
                ? ScreenerScope.Swing
                : ScreenerScope.Intraday;
            var result = await screener.PreviewAsync(screenerInput, scope, wishlistId, cancellationToken);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Error ?? "Finviz screener resolution failed.");
            }

            screened = Normalize(result.Symbols);
            normalizedQuery = result.NormalizedQuery;
            screenedAtUtc = result.SyncedAtUtc;
        }

        var tickers = selected
            .Concat(screened)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ticker => ticker, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tickers.Length == 0)
        {
            throw new InvalidOperationException("The selected wishlist and Finviz screen resolved to no active tickers.");
        }

        var source = includeScreener
            ? includeSelectedTickers && selected.Count > 0
                ? $"{selectedTickerSource}+finviz"
                : "finviz"
            : selectedTickerSource;

        return new PaperRunUniverseSnapshot(
            tickers,
            selected,
            screened,
            source,
            normalizedQuery,
            screenedAtUtc ?? timeProvider.GetUtcNow());
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> tickers) => tickers
        .Where(ticker => !String.IsNullOrWhiteSpace(ticker))
        .Select(ticker => ticker.Trim().ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(ticker => ticker, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
