using TradingFlow.Domain.Market;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

/// <summary>
/// Provides an operator-facing preview of the same catalyst provider used by
/// paper/live runners. The preview is intentionally read-only and never places
/// orders; it lets us verify news availability and sentiment before enabling
/// catalyst-aware strategies in production.
/// </summary>
public sealed class NewsFeedService
{
    private readonly SimpleYamlReader yamlReader;
    private readonly PaperRuntimeFactory runtimeFactory;
    private readonly ProjectPaths paths;

    public NewsFeedService(
        SimpleYamlReader yamlReader,
        PaperRuntimeFactory runtimeFactory,
        ProjectPaths paths)
    {
        this.yamlReader = yamlReader;
        this.runtimeFactory = runtimeFactory;
        this.paths = paths;
    }

    public async Task<MobileNewsFeedResponse> GetLatestAsync(
        string configPath,
        IReadOnlyList<string> tickers,
        int hours,
        CancellationToken cancellationToken)
    {
        var resolvedConfigPath = paths.ResolveRepositoryPath(configPath);
        var run = runtimeFactory.ResolveRunPaths(yamlReader.ReadBacktestRun(resolvedConfigPath));
        if (!run.News.Enabled)
        {
            return new MobileNewsFeedResponse(false, run.News.ProviderName, "News is disabled in this run config.", Array.Empty<MobileNewsItem>());
        }

        var provider = runtimeFactory.CreateNewsProvider(run);
        if (provider is null)
        {
            return new MobileNewsFeedResponse(false, run.News.ProviderName, "No news provider is configured.", Array.Empty<MobileNewsItem>());
        }

        var symbols = tickers
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        if (symbols.Length == 0)
        {
            symbols = run.Tickers.Take(20).ToArray();
        }

        var windowEnd = DateTimeOffset.UtcNow;
        var windowStart = windowEnd.AddHours(-Math.Clamp(hours, 1, 168));
        var tasks = symbols.Select(async ticker =>
        {
            var catalysts = await provider.GetCatalystsAsync(ticker, windowStart, windowEnd, cancellationToken);
            return catalysts.Select(ToMobileNewsItem);
        });

        var items = (await Task.WhenAll(tasks))
            .SelectMany(item => item)
            .GroupBy(item => $"{item.Ticker}|{item.Url ?? item.Headline}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.Timestamp)
            .Take(80)
            .ToArray();

        return new MobileNewsFeedResponse(true, provider.ProviderName, null, items);
    }

    private static MobileNewsItem ToMobileNewsItem(CatalystEvent item)
    {
        return new MobileNewsItem(
            item.Ticker,
            item.Timestamp,
            item.Headline,
            item.SentimentScore,
            item.Provider,
            item.Source,
            item.Url,
            item.Summary);
    }
}
