using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Data.Csv;

/// <summary>
/// Non-secret identity required to decide whether a cache was produced by the
/// currently requested provider/feed/adjustment contract.
/// </summary>
public sealed record MarketDataCacheSourceDescriptor
{
    public MarketDataCacheSourceDescriptor(
        string providerIdentity,
        string dataFeed,
        string adjustmentPolicy,
        string restatementRevision = "unversioned")
    {
        ProviderIdentity = Normalize(providerIdentity, nameof(providerIdentity), 200);
        DataFeed = Normalize(dataFeed, nameof(dataFeed), 64);
        AdjustmentPolicy = Normalize(adjustmentPolicy, nameof(adjustmentPolicy), 64);
        RestatementRevision = Normalize(restatementRevision, nameof(restatementRevision), 128);
    }

    public string ProviderIdentity { get; }
    public string DataFeed { get; }
    public string AdjustmentPolicy { get; }

    /// <summary>
    /// Non-secret provider or research-dataset revision. Changing it invalidates
    /// otherwise matching adjusted history immediately, including after splits.
    /// Time-based freshness remains mandatory when a provider has no revision ID.
    /// </summary>
    public string RestatementRevision { get; }

    internal bool MatchesBar(OhlcvBar bar) =>
        String.Equals(DataFeed, bar.DataFeed, StringComparison.OrdinalIgnoreCase) &&
        String.Equals(AdjustmentPolicy, bar.AdjustmentPolicy, StringComparison.OrdinalIgnoreCase);

    internal static MarketDataCacheSourceDescriptor FromDownloadedBars(
        IMarketDataProvider provider,
        IReadOnlyCollection<OhlcvBar> bars)
    {
        var feeds = bars
            .Select(x => x.DataFeed)
            .Where(x => !String.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var adjustments = bars
            .Select(x => x.AdjustmentPolicy)
            .Where(x => !String.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (bars.Count == 0 || feeds.Length != 1 || adjustments.Length != 1)
        {
            throw new InvalidDataException(
                "A reusable cache requires one explicit data feed and adjustment policy. " +
                "Provide a cache source descriptor when an exact request can validly return no bars.");
        }

        return new MarketDataCacheSourceDescriptor(
            provider.GetType().FullName ?? provider.GetType().Name,
            feeds[0],
            adjustments[0]);
    }

    private static string Normalize(string value, string parameterName, int maximumLength)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? String.Empty;
        if (normalized.Length == 0 || normalized.Length > maximumLength ||
            normalized.Any(character =>
                !Char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_' and not '+'))
        {
            throw new ArgumentException(
                "Cache source metadata must be a non-secret identifier containing only letters, digits, '.', '-', '_', or '+'.",
                parameterName);
        }

        return normalized;
    }
}

public interface IMarketDataCacheSourceDescriptorProvider
{
    MarketDataCacheSourceDescriptor CacheSourceDescriptor { get; }
}
