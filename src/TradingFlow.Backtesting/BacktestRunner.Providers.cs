using TradingFlow.Data.Catalysts;
using TradingFlow.Data.Csv;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Backtesting;

// Research cache wrappers around the shared raw data provider composition.
public sealed partial class BacktestRunner
{
    /// <summary>Wraps ordinary strict news in an isolated cache; a frozen diagnostic package never fetches or attaches strict catalysts.</summary>
    private ICatalystProvider? CreateNewsProvider(BacktestRunConfig run)
    {
        if (!run.News.Enabled || _historicalNewsProvider is not null) return null;
        var provider = _dataProviders.CreateNews(run, RequireRawArchiveWriter());

        if (provider is null ||
            run.CachePolicy.Equals("bypass", StringComparison.OrdinalIgnoreCase))
        {
            return provider;
        }

        return new CachedCatalystProvider(
            provider,
            Path.Combine(run.NormalizedRoot, GetCacheWindowSegment(run.TimeWindow)),
            run.CachePolicy,
            RequireRawArchiveWriter(),
            run.RawRoot);
    }

    /// <summary>Reuses the shared adapter wiring while retaining research cache policy and frozen provider overrides.</summary>
    private IMarketDataProvider CreateProvider(BacktestRunConfig run)
    {
        if (preparedData is not null) return preparedData.CreateProvider(run);
        // Research replay may supply a frozen, provenance-bound source. The decision engine is unchanged.
        if (marketDataProviderFactory is not null) return marketDataProviderFactory(run);
        var provider = _dataProviders.CreateMarket(run, RequireRawArchiveWriter(), _csvSourceRoot);

        if (run.Provider.Equals("csv", StringComparison.OrdinalIgnoreCase) ||
            run.CachePolicy.Equals("bypass", StringComparison.OrdinalIgnoreCase))
        {
            return provider;
        }

        return new CachedMarketDataProvider(
            provider,
            Path.Combine(run.NormalizedRoot, GetCacheWindowSegment(run.TimeWindow)),
            run.CachePolicy);
    }

    private IRawArchiveWriter RequireRawArchiveWriter() =>
        _rawArchiveWriter ?? throw new InvalidOperationException(
            "Provider access requires a raw archive writer so required responses are durable before parsing.");

    private static string GetCacheWindowSegment(TimeWindowConfig timeWindow)
    {
        if (timeWindow.Type.Equals("rolling", StringComparison.OrdinalIgnoreCase))
        {
            return $"{timeWindow.LookbackDays + Math.Max(timeWindow.WarmupLookbackDays, 0)}d";
        }

        if (timeWindow.Start is not null && timeWindow.End is not null)
        {
            return $"{timeWindow.Start:yyyyMMdd}-{timeWindow.End:yyyyMMdd}";
        }

        return "custom-window";
    }

}
