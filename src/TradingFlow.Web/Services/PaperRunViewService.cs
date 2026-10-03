using System.Text.Json;
using TradingFlow.Domain.Audit;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

/// <summary>Bounded read projections over one durable paper attempt.</summary>
public sealed class PaperRunViewService(ProjectPaths paths, ConfigCatalogService catalog, IDecisionAuditRepository audits)
{
    public async Task<List<DecisionAuditRecord>> LoadRecentAuditsAsync(BacktestJobSnapshot job, CancellationToken token)
    {
        var runName = ReadConfig(job)?.Config.RunName ??
            (job.ArtifactDirectory is null ? job.RunName : Path.GetFileName(Path.TrimEndingDirectorySeparator(job.ArtifactDirectory)));
        return (await audits.GetAuditPageAsync(runName, null, null, 0, 50, token)).Records.ToList();
    }

    private RunConfigSummary? ReadConfig(BacktestJobSnapshot job)
    {
        try { return catalog.GetConfig(job.ConfigPath); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        { return null; }
    }

    public List<ChartDataPoint> LoadChartData(BacktestJobSnapshot job)
    {
        var config = ReadConfig(job);
        // ArtifactDirectory is server-persisted attempt metadata, never a request path.
        // It remains authoritative after the historical generated config is removed.
        if (config is null && job.ArtifactDirectory is null) return [];
        var root = config is null ? Path.GetDirectoryName(job.ArtifactDirectory!)! :
            paths.ResolveRepositoryPath(config.Config.ResultsRoot);
        var directory = job.ArtifactDirectory ?? Path.Combine(root, "live", config!.Config.RunName);
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full)) return [];
        var allowed = (config?.Config.Tickers ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var timeframes = (config?.Strategies ?? []).Select(item => item.Definition)
            .GroupBy(item => item.StrategyName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Timeframe, StringComparer.OrdinalIgnoreCase);
        var result = new List<ChartDataPoint>();
        foreach (var tickerDirectory in Directory.EnumerateDirectories(full).Take(500))
        foreach (var file in Directory.EnumerateFiles(tickerDirectory, "*_chart.json").Take(100))
        {
            if (result.Count >= 500) return result;
            try
            {
                if (new FileInfo(file).Length > 1_048_576) continue;
                var point = JsonSerializer.Deserialize<ChartDataPoint>(File.ReadAllText(file));
                if (point is null || (allowed.Count > 0 && !allowed.Contains(point.Ticker))) continue;
                if (String.IsNullOrWhiteSpace(point.Timeframe) && timeframes.TryGetValue(point.StrategyName, out var timeframe))
                    point.Timeframe = timeframe;
                result.Add(point);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return result;
    }
}
