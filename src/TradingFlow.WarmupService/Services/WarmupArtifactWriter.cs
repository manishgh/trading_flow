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

    /// <summary>Captures one run's bytes in a unique directory so a later warmup cannot replace its retry inputs.</summary>
    public async Task<WarmupArtifacts> WriteAsync(string runId, WarmupTickerPayload payload, CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(runId) || runId != Path.GetFileName(runId) || runId is "." or "..")
            throw new ArgumentException("Warmup run ID must be a simple path segment.");
        var captureRoot = Path.Combine(artifactRoot, "runs", runId, Guid.NewGuid().ToString("N"));
        var paths = new List<string>();
        foreach (var group in payload.Bars.GroupBy(x => x.Timeframe, StringComparer.OrdinalIgnoreCase))
        {
            var orderedBars = group.OrderBy(x => x.Timestamp).ToArray();
            var timeframe = WarmupText.NormalizeSegment(group.Key);
            var barsPath = Path.Combine(captureRoot, payload.Intent.Ticker, timeframe, "bars.jsonl");
            await AtomicFileArtifactWriter.Instance.WriteTextAsync(
                barsPath,
                ToJsonLines(orderedBars),
                cancellationToken);
            paths.Add(barsPath);

            var indicatorsPath = Path.Combine(captureRoot, payload.Intent.Ticker, timeframe, "indicators.jsonl");
            await AtomicFileArtifactWriter.Instance.WriteTextAsync(
                indicatorsPath,
                ToJsonLines(indicatorEngine.Compute(orderedBars)),
                cancellationToken);
            paths.Add(indicatorsPath);
        }

        var newsPath = Path.Combine(captureRoot, payload.Intent.Ticker, "news", "catalysts.jsonl");
        await AtomicFileArtifactWriter.Instance.WriteTextAsync(
            newsPath,
            ToJsonLines(payload.Catalysts.OrderBy(x => x.Timestamp)),
            cancellationToken);
        paths.Add(newsPath);

        var manifestPath = Path.Combine(captureRoot, payload.Intent.Ticker, "warmup-manifest.json");
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
                Paths = paths.Select(path => Path.GetRelativePath(captureRoot, path)).ToArray(),
                CreatedAtUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);
        paths.Add(manifestPath);

        return new WarmupArtifacts(payload.Intent.Ticker, paths);
    }

    /// <summary>Serializes captured rows without changing their timestamps or inventing missing observations.</summary>
    private static string ToJsonLines<T>(IEnumerable<T> values)
    {
        return String.Join(Environment.NewLine, values.Select(value => JsonSerializer.Serialize(value, JsonOptions))) + Environment.NewLine;
    }
}
