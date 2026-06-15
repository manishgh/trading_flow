using System.Text.Json;
using Microsoft.Extensions.Options;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Storage;
using TradingFlow.WarmupService.Models;

namespace TradingFlow.WarmupService.Services;

public sealed class WarmupArtifactWriter(IOptions<WarmupOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IndicatorEngine indicatorEngine = new();
    private readonly string artifactRoot = Path.Combine(options.Value.CacheRoot, "artifacts");

    public async Task<WarmupArtifacts> WriteAsync(WarmupTickerPayload payload, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        foreach (var group in payload.Bars.GroupBy(x => x.Timeframe, StringComparer.OrdinalIgnoreCase))
        {
            var orderedBars = group.OrderBy(x => x.Timestamp).ToArray();
            var timeframe = WarmupText.NormalizeSegment(group.Key);
            var barsPath = Path.Combine(artifactRoot, payload.Intent.Ticker, timeframe, "bars.jsonl");
            await AtomicFileArtifactWriter.Instance.WriteTextAsync(
                barsPath,
                ToJsonLines(orderedBars),
                cancellationToken);
            paths.Add(barsPath);

            var indicatorsPath = Path.Combine(artifactRoot, payload.Intent.Ticker, timeframe, "indicators.jsonl");
            await AtomicFileArtifactWriter.Instance.WriteTextAsync(
                indicatorsPath,
                ToJsonLines(indicatorEngine.Compute(orderedBars)),
                cancellationToken);
            paths.Add(indicatorsPath);
        }

        var newsPath = Path.Combine(artifactRoot, payload.Intent.Ticker, "news", "catalysts.jsonl");
        await AtomicFileArtifactWriter.Instance.WriteTextAsync(
            newsPath,
            ToJsonLines(payload.Catalysts.OrderBy(x => x.Timestamp)),
            cancellationToken);
        paths.Add(newsPath);

        var manifestPath = Path.Combine(artifactRoot, payload.Intent.Ticker, "warmup-manifest.json");
        await AtomicFileArtifactWriter.Instance.WriteTextAsync(
            manifestPath,
            JsonSerializer.Serialize(new
            {
                payload.Intent.Ticker,
                payload.StartUtc,
                payload.EndUtc,
                payload.Intent.WarmupDays,
                payload.Intent.NewsLookbackDays,
                payload.Intent.Timeframes,
                BarCount = payload.Bars.Count,
                CatalystCount = payload.Catalysts.Count,
                Paths = paths.Select(path => Path.GetRelativePath(artifactRoot, path)).ToArray(),
                CreatedAtUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);
        paths.Add(manifestPath);

        return new WarmupArtifacts(payload.Intent.Ticker, paths);
    }

    private static string ToJsonLines<T>(IEnumerable<T> values)
    {
        return String.Join(Environment.NewLine, values.Select(value => JsonSerializer.Serialize(value, JsonOptions))) + Environment.NewLine;
    }
}
