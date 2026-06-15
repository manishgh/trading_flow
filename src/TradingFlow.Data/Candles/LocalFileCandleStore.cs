using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Data.Candles;

/// <summary>
/// Local JSONL implementation of <see cref="ICandleStore"/>. It is designed
/// for paper/live recovery and later blob archival: the hot path writes to
/// local disk quickly, while a separate process can copy files listed in the
/// pending archive manifests to Azure Blob.
/// </summary>
public sealed class LocalFileCandleStore : ICandleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string rootPath;

    public LocalFileCandleStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = Path.GetFullPath(rootPath);
    }

    /// <summary>
    /// Persists candles grouped by ticker/timeframe/UTC date. We upsert instead
    /// of append because paper/live warmup windows overlap on every iteration.
    /// </summary>
    public async Task UpsertBarsAsync(CandleStoreWriteRequest request, CancellationToken cancellationToken)
    {
        if (request.Bars.Count == 0)
        {
            return;
        }

        var writtenFiles = new List<string>();
        foreach (var group in request.Bars
                     .GroupBy(bar => new CandleFileKey(
                         NormalizeSegment(bar.Ticker),
                         NormalizeSegment(bar.Timeframe),
                         DateOnly.FromDateTime(bar.Timestamp.UtcDateTime.Date))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveCandlePath(request.Context, request.Source, group.Key);
            await UpsertFileAsync(path, group, cancellationToken);
            writtenFiles.Add(path);
        }

        await WritePendingArchiveManifestAsync(request, writtenFiles, cancellationToken);
    }

    /// <summary>
    /// Reads all physical sources for a ticker/timeframe window and de-duplicates
    /// by timestamp so callers do not need to know whether bars came from the
    /// provider feed or a derived timeframe folder.
    /// </summary>
    public async Task<IReadOnlyList<OhlcvBar>> ReadBarsAsync(CandleStoreReadRequest request, CancellationToken cancellationToken)
    {
        var bars = new List<OhlcvBar>();
        var startDate = DateOnly.FromDateTime(request.Start.UtcDateTime.Date);
        var endDate = DateOnly.FromDateTime(request.End.UtcDateTime.Date);
        var ticker = NormalizeSegment(request.Ticker);
        var timeframe = NormalizeSegment(request.Timeframe);

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            foreach (var source in EnumerateSources(request, timeframe, ticker, date))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(source))
                {
                    continue;
                }

                await foreach (var bar in ReadFileAsync(source, cancellationToken))
                {
                    if (bar.Timestamp >= request.Start &&
                        bar.Timestamp <= request.End &&
                        bar.Ticker.Equals(request.Ticker, StringComparison.OrdinalIgnoreCase) &&
                        bar.Timeframe.Equals(request.Timeframe, StringComparison.OrdinalIgnoreCase))
                    {
                        bars.Add(bar);
                    }
                }
            }
        }

        return bars
            .GroupBy(bar => bar.Timestamp)
            .Select(group => group.OrderByDescending(bar => bar.Timestamp).First())
            .OrderBy(bar => bar.Timestamp)
            .ToArray();
    }

    private async Task UpsertFileAsync(string path, IEnumerable<OhlcvBar> incomingBars, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var gate = FileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Merge in memory under a per-file lock. This avoids duplicate rows
            // when Alpaca lookback reads overlap while keeping each file ordered.
            var barsByTimestamp = new SortedDictionary<DateTimeOffset, OhlcvBar>();
            if (File.Exists(path))
            {
                await foreach (var existing in ReadFileAsync(path, cancellationToken))
                {
                    barsByTimestamp[existing.Timestamp] = existing;
                }
            }

            foreach (var bar in incomingBars)
            {
                barsByTimestamp[bar.Timestamp] = bar with
                {
                    Ticker = bar.Ticker.Trim().ToUpperInvariant(),
                    Timeframe = bar.Timeframe.Trim().ToLowerInvariant(),
                    Timestamp = bar.Timestamp.ToUniversalTime()
                };
            }

            await WriteFileAtomicAsync(path, barsByTimestamp.Values, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async IAsyncEnumerable<OhlcvBar> ReadFileAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, Utf8NoBom);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (String.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var dto = JsonSerializer.Deserialize<CandleLine>(line, JsonOptions);
            if (dto is null)
            {
                continue;
            }

            yield return new OhlcvBar(
                dto.Ticker,
                dto.Timestamp,
                dto.Timeframe,
                dto.Open,
                dto.High,
                dto.Low,
                dto.Close,
                dto.Volume);
        }
    }

    private static async Task WriteFileAtomicAsync(string path, IEnumerable<OhlcvBar> bars, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            // Write-through plus replace/move gives readers either the old full
            // file or the new full file; never a half-written candle file.
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                foreach (var bar in bars.OrderBy(x => x.Timestamp))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dto = new CandleLine(
                        bar.Ticker,
                        bar.Timestamp.ToUniversalTime(),
                        bar.Timeframe,
                        bar.Open,
                        bar.High,
                        bar.Low,
                        bar.Close,
                        bar.Volume);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(dto, JsonOptions).AsMemory(), cancellationToken);
                }

                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            ReplaceOrMove(temporaryPath, path);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async Task WritePendingArchiveManifestAsync(
        CandleStoreWriteRequest request,
        IReadOnlyList<string> writtenFiles,
        CancellationToken cancellationToken)
    {
        if (writtenFiles.Count == 0)
        {
            return;
        }

        var manifestRoot = Path.Combine(rootPath, "_archive-pending");
        Directory.CreateDirectory(manifestRoot);
        var manifestPath = Path.Combine(manifestRoot, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
        // The manifest is the future handoff contract for an Azure Blob copier.
        // Keeping it separate from candle writes prevents cloud latency from
        // slowing down live strategy evaluation.
        var manifest = new CandleArchiveManifest(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            request.Context.Scope,
            request.Context.RunName,
            request.Context.ProviderName,
            request.Source,
            request.Bars.Count,
            writtenFiles.Select(path => Path.GetRelativePath(rootPath, path)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());

        await WriteTextAtomicAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);
    }

    private static async Task WriteTextAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            ReplaceOrMove(temporaryPath, path);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private string ResolveCandlePath(CandleStoreContext context, string source, CandleFileKey key)
    {
        return Path.Combine(
            rootPath,
            NormalizeSegment(context.Scope),
            NormalizeSegment(context.RunName),
            NormalizeSegment(context.ProviderName),
            NormalizeSegment(source),
            key.Timeframe,
            key.Ticker,
            $"{key.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.jsonl");
    }

    private IEnumerable<string> EnumerateSources(CandleStoreReadRequest request, string timeframe, string ticker, DateOnly date)
    {
        var basePath = Path.Combine(
            rootPath,
            NormalizeSegment(request.Scope),
            NormalizeSegment(request.RunName),
            NormalizeSegment(request.ProviderName));

        if (!Directory.Exists(basePath))
        {
            yield break;
        }

        foreach (var sourceDirectory in Directory.EnumerateDirectories(basePath))
        {
            // Read across all source folders. This lets recovery callers ask for
            // "AMD 5m" without knowing whether it was provider-loaded or derived.
            yield return Path.Combine(
                sourceDirectory,
                timeframe,
                ticker,
                $"{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.jsonl");
        }
    }

    private static string NormalizeSegment(string value)
    {
        var normalized = String.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim().ToLowerInvariant();

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            normalized = normalized.Replace(invalid, '_');
        }

        return normalized;
    }

    private static void ReplaceOrMove(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return;
        }

        try
        {
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private readonly record struct CandleFileKey(string Ticker, string Timeframe, DateOnly Date);

    private sealed record CandleLine(
        string Ticker,
        DateTimeOffset Timestamp,
        string Timeframe,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal Volume);

    private sealed record CandleArchiveManifest(
        string ManifestId,
        DateTimeOffset CreatedAtUtc,
        string Scope,
        string RunName,
        string ProviderName,
        string Source,
        int BarCount,
        IReadOnlyList<string> Files);
}
