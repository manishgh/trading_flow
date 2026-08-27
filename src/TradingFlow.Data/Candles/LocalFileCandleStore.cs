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
public sealed class LocalFileCandleStore : IFencedCandleStore
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

    public async Task AdvanceFencingTokenAsync(
        CandleStoreContext context,
        string source,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        await using var fence = await AcquireFenceAsync(
            new CandleStoreWriteRequest(context, source, [], fencingToken),
            fencingToken,
            cancellationToken);
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

        if (request.Source.Equals("stream", StringComparison.OrdinalIgnoreCase) &&
            request.FencingToken is null)
        {
            throw new InvalidOperationException(
                "Stream candle persistence requires a positive fencing token.");
        }

        if (request.FencingToken is { } fencingToken)
        {
            await using var fence = await AcquireFenceAsync(request, fencingToken, cancellationToken);
            await UpsertCoreAsync(request, cancellationToken);
            return;
        }

        await UpsertCoreAsync(request, cancellationToken);
    }

    private async Task UpsertCoreAsync(CandleStoreWriteRequest request, CancellationToken cancellationToken)
    {

        var groups = request.Bars
            .GroupBy(bar => new CandleFileKey(
                NormalizeSegment(bar.Ticker),
                NormalizeSegment(bar.Timeframe),
                DateOnly.FromDateTime(bar.Timestamp.UtcDateTime.Date)))
            .Select(group => new
            {
                group.Key,
                Bars = group.OrderBy(bar => bar.Timestamp).ToArray(),
                Path = ResolveCandlePath(request.Context, request.Source, group.Key)
            })
            .ToArray();
        var writtenFiles = groups.Select(group => group.Path).ToArray();

        // Persist a unique write intent before candle I/O. It is not visible to
        // the archive consumer until CommitArchiveIntentAsync runs after every
        // affected candle file has been flushed.
        var archiveIntent = await WriteArchiveIntentAsync(request, writtenFiles, cancellationToken);
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.FencingToken.HasValue)
            {
                await AppendFileAsync(group.Path, group.Bars, cancellationToken);
            }
            else
            {
                await UpsertFileAsync(group.Path, group.Bars, cancellationToken);
            }
        }

        await CommitArchiveIntentAsync(archiveIntent, cancellationToken);
    }

    private async Task<FileStream> AcquireFenceAsync(
        CandleStoreWriteRequest request,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        if (fencingToken <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fencingToken), "A persisted stream fencing token must be positive.");
        }

        var directory = Path.Combine(
            rootPath,
            NormalizeSegment(request.Context.Scope),
            NormalizeSegment(request.Context.RunName),
            NormalizeSegment(request.Context.ProviderName),
            NormalizeSegment(request.Source));
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, "_stream-fence.lock");
        var tokenPath = Path.Combine(directory, "_stream-fence.token");
        var stream = await AcquireExclusiveLockAsync(lockPath, cancellationToken);

        try
        {
            var current = await ReadFenceAsync(tokenPath, cancellationToken);
            if (fencingToken < current)
            {
                throw new StaleCandleStoreWriteException(
                    $"Stream fence {fencingToken} is stale; durable fence is {current}.");
            }

            if (fencingToken > current)
            {
                await WriteTextAtomicAsync(
                    tokenPath,
                    fencingToken.ToString(CultureInfo.InvariantCulture),
                    cancellationToken);
            }

            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static async Task<long> ReadFenceAsync(string tokenPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(tokenPath))
        {
            return 0;
        }

        var text = await File.ReadAllTextAsync(tokenPath, cancellationToken);
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException("The durable market-stream fence is corrupt.");
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
            .Select(group => group.Last())
            .OrderBy(bar => bar.Timestamp)
            .ToArray();
    }

    private static async Task AppendFileAsync(
        string path,
        IEnumerable<OhlcvBar> incomingBars,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await RepairIncompleteTailAsync(path, cancellationToken);
        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            16 * 1024,
            FileOptions.WriteThrough | FileOptions.Asynchronous);
        await using var writer = new StreamWriter(stream, Utf8NoBom);
        foreach (var bar in incomingBars.OrderBy(value => value.Timestamp))
        {
            var normalized = bar with
            {
                Ticker = bar.Ticker.Trim().ToUpperInvariant(),
                Timeframe = bar.Timeframe.Trim().ToLowerInvariant(),
                Timestamp = bar.Timestamp.ToUniversalTime()
            };
            var dto = new CandleLine(
                normalized.Ticker,
                normalized.Timestamp,
                normalized.Timeframe,
                normalized.Open,
                normalized.High,
                normalized.Low,
                normalized.Close,
                normalized.Volume);
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(dto, JsonOptions).AsMemory(),
                cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
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
        var line = await reader.ReadLineAsync(cancellationToken);
        while (line is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextLine = await reader.ReadLineAsync(cancellationToken);
            if (String.IsNullOrWhiteSpace(line))
            {
                line = nextLine;
                continue;
            }

            CandleLine? dto;
            try
            {
                dto = JsonSerializer.Deserialize<CandleLine>(line, JsonOptions);
            }
            catch (JsonException) when (nextLine is null)
            {
                // A reader can observe the final record while the append writer is
                // still flushing, or after a process crash. The next fenced append
                // truncates this incomplete tail before writing another record.
                yield break;
            }

            if (dto is null)
            {
                line = nextLine;
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
            line = nextLine;
        }
    }

    private static async Task RepairIncompleteTailAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            4 * 1024,
            FileOptions.WriteThrough | FileOptions.Asynchronous);
        if (stream.Length == 0)
        {
            return;
        }

        var finalByte = new byte[1];
        stream.Position = stream.Length - 1;
        await stream.ReadExactlyAsync(finalByte, cancellationToken);
        if (finalByte[0] != (byte)'\n')
        {
            var lastCompleteNewline = await FindPreviousNewlineAsync(
                stream,
                stream.Length,
                cancellationToken);
            stream.SetLength(lastCompleteNewline + 1);
            stream.Flush(flushToDisk: true);
            return;
        }

        var previousNewline = await FindPreviousNewlineAsync(
            stream,
            stream.Length - 1,
            cancellationToken);
        var lineStart = previousNewline + 1;
        var lineLength = checked((int)(stream.Length - 1 - lineStart));
        var lineBytes = new byte[lineLength];
        stream.Position = lineStart;
        await stream.ReadExactlyAsync(lineBytes, cancellationToken);
        if (lineBytes.Length > 0 && lineBytes[^1] == (byte)'\r')
        {
            Array.Resize(ref lineBytes, lineBytes.Length - 1);
        }

        try
        {
            if (lineBytes.Length == 0 ||
                JsonSerializer.Deserialize<CandleLine>(lineBytes, JsonOptions) is null)
            {
                throw new JsonException("Final candle record is empty.");
            }
        }
        catch (JsonException)
        {
            stream.SetLength(lineStart);
            stream.Flush(flushToDisk: true);
        }
    }

    private static async Task<long> FindPreviousNewlineAsync(
        FileStream stream,
        long beforeExclusive,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4 * 1024];
        var searchEnd = beforeExclusive;
        while (searchEnd > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, searchEnd);
            var start = searchEnd - count;
            stream.Position = start;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
            for (var index = count - 1; index >= 0; index--)
            {
                if (buffer[index] == (byte)'\n')
                {
                    return start + index;
                }
            }

            searchEnd = start;
        }

        return -1;
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

    private async Task<ArchiveIntentHandle> WriteArchiveIntentAsync(
        CandleStoreWriteRequest request,
        IReadOnlyList<string> writtenFiles,
        CancellationToken cancellationToken)
    {
        var intentRoot = Path.Combine(rootPath, "_archive-preparing");
        Directory.CreateDirectory(intentRoot);
        var now = DateTimeOffset.UtcNow;
        var intent = new CandleArchiveIntent(
            Guid.NewGuid().ToString("N"),
            now,
            request.Context.Scope,
            request.Context.RunName,
            request.Context.ProviderName,
            request.Source,
            writtenFiles
                .Select(path => Path.GetRelativePath(rootPath, path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        var intentPath = Path.Combine(intentRoot, $"{now:yyyyMMddHHmmssfffffff}-{intent.IntentId}.json");
        await WriteTextAtomicAsync(
            intentPath,
            JsonSerializer.Serialize(intent, JsonOptions),
            cancellationToken);
        return new ArchiveIntentHandle(intentPath, intent);
    }

    private async Task CommitArchiveIntentAsync(
        ArchiveIntentHandle handle,
        CancellationToken cancellationToken)
    {
        var intent = handle.Intent;
        var manifestRoot = Path.Combine(rootPath, "_archive-pending");
        Directory.CreateDirectory(manifestRoot);
        var manifestPath = Path.Combine(
            manifestRoot,
            $"{NormalizeSegment(intent.Scope)}-{NormalizeSegment(intent.RunName)}-" +
            $"{NormalizeSegment(intent.ProviderName)}-{NormalizeSegment(intent.Source)}-{intent.CreatedAtUtc:yyyyMMddHHmm}.json");
        // A future archive consumer must acquire this same lock before reading
        // and acknowledging a manifest. The pending document is consumer-visible
        // only after candle durability has been established.
        var gate = FileLocks.GetOrAdd(manifestPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var crossProcessLock = await AcquireExclusiveLockAsync(
                $"{manifestPath}.lock",
                cancellationToken);
            CandleArchiveManifest? existing = null;
            if (File.Exists(manifestPath))
            {
                existing = JsonSerializer.Deserialize<CandleArchiveManifest>(
                    await File.ReadAllTextAsync(manifestPath, cancellationToken),
                    JsonOptions);
            }

            var committedIntentIds = (existing?.CommittedIntentIds ?? [])
                .Append(intent.IntentId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var files = (existing?.Files ?? [])
                .Concat(intent.Files)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var manifest = new CandleArchiveManifest(
                existing?.ManifestId ?? Guid.NewGuid().ToString("N"),
                existing?.CreatedAtUtc ?? intent.CreatedAtUtc,
                intent.Scope,
                intent.RunName,
                intent.ProviderName,
                intent.Source,
                committedIntentIds,
                files);

            await WriteTextAtomicAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken);
            TryDelete(handle.Path);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<FileStream> AcquireExclusiveLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    128,
                    FileOptions.WriteThrough | FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
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

        var sourceDirectories = request.Source is { Length: > 0 }
            ? [Path.Combine(basePath, NormalizeSegment(request.Source))]
            : Directory.EnumerateDirectories(basePath);
        foreach (var sourceDirectory in sourceDirectories
                     .OrderBy(GetSourcePrecedence)
                     .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
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

    private static int GetSourcePrecedence(string sourceDirectory) =>
        Path.GetFileName(sourceDirectory).ToLowerInvariant() switch
        {
            "derived" => 100,
            "provider" => 200,
            "stream" => 300,
            _ => 0
        };

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
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(destinationPath))
                {
                    File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, destinationPath, overwrite: false);
                }
                return;
            }
            catch (IOException)
            {
                if (attempt == 2) throw;
                Thread.Sleep(50);
            }
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
        IReadOnlyList<string> CommittedIntentIds,
        IReadOnlyList<string> Files);

    private sealed record CandleArchiveIntent(
        string IntentId,
        DateTimeOffset CreatedAtUtc,
        string Scope,
        string RunName,
        string ProviderName,
        string Source,
        IReadOnlyList<string> Files);

    private sealed record ArchiveIntentHandle(string Path, CandleArchiveIntent Intent);
}
