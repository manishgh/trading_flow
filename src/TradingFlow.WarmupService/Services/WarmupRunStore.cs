using Microsoft.Extensions.Options;
using TradingFlow.WarmupService.Models;
using TradingFlow.Engine.Storage;
using System.Text.Json;

namespace TradingFlow.WarmupService.Services;

public sealed class WarmupRunStore(IOptions<WarmupOptions> options)
    : WarmupJsonStore<WarmupRunRecord>(options, "runs.json")
{
    private readonly string capturesRoot = Path.Combine(options.Value.DataRoot, "state", "captures");

    /// <summary>Records completed cache capture metadata before archival, so recovery never pairs revised provider rows with an older archive.</summary>
    public Task SaveTickerCaptureAsync(string runId, WarmupTickerResult capture, CancellationToken token) =>
        AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(CapturePath(runId, capture.Ticker),
            JsonSerializer.Serialize(capture, JsonOptions), token);

    /// <summary>Restores the original counts, dates and immutable input paths when a process stopped before publishing the full run.</summary>
    public async Task<WarmupTickerResult?> ReadTickerCaptureAsync(string runId, string ticker, CancellationToken token)
    {
        var path = CapturePath(runId, ticker);
        if (!File.Exists(path)) return null;
        var result = JsonSerializer.Deserialize<WarmupTickerResult>(await File.ReadAllTextAsync(path, token), JsonOptions)
            ?? throw new InvalidDataException("Saved warmup capture is empty.");
        if (!result.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) || !result.CacheCompleted || result.ArtifactPaths.Count == 0)
            throw new InvalidDataException("Saved warmup capture does not describe the requested completed ticker.");
        return result;
    }

    /// <summary>Restricts capture metadata to this host's local state directory.</summary>
    private string CapturePath(string runId, string ticker)
    {
        if (String.IsNullOrWhiteSpace(runId) || runId != Path.GetFileName(runId) || runId is "." or ".." ||
            String.IsNullOrWhiteSpace(ticker) || ticker != Path.GetFileName(ticker) || ticker is "." or "..")
            throw new ArgumentException("Warmup capture IDs must be simple path segments.");
        return Path.Combine(capturesRoot, runId, ticker.ToUpperInvariant() + ".json");
    }

    /// <summary>Keeps every failed/pending archive retry target while trimming only completed delivery history.</summary>
    public Task AddAsync(WarmupRunRecord run, CancellationToken cancellationToken)
    {
        return MutateAsync(items =>
        {
            items.RemoveAll(x => x.RunId.Equals(run.RunId, StringComparison.OrdinalIgnoreCase));
            items.Add(run);
            items.Sort((left, right) => right.StartedAtUtc.CompareTo(left.StartedAtUtc));
            var completedHistory = items.Where(item => !item.Tickers.Any(ticker => ticker.ArchiveStatus is "remote_pending" or "local_failed"))
                .Skip(250).Select(item => item.RunId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            items.RemoveAll(item => completedHistory.Contains(item.RunId));

            return Array.Empty<WarmupRunRecord>();
        }, cancellationToken);
    }
}
