using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Data.Catalysts;

public sealed class CachedCatalystProvider(
    ICatalystProvider innerProvider,
    string cacheRoot,
    string cachePolicy) : ICatalystProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string ProviderName => innerProvider.ProviderName;

    public async Task<IReadOnlyList<CatalystEvent>> GetCatalystsAsync(
        string ticker,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        var path = GetPath(ticker, windowStart, windowEnd);
        if (!cachePolicy.Equals("refresh", StringComparison.OrdinalIgnoreCase))
        {
            // Exact-window hit, else any cached file whose fetched window COVERS the requested window
            // (ReadAsync filters by timestamp), so a sub-window backtest reuses a broader cached fetch
            // instead of missing the cache and going online.
            if (File.Exists(path))
            {
                return await ReadAsync(path, windowStart, windowEnd, cancellationToken);
            }

            var covering = FindCoveringCacheFile(ticker, windowStart, windowEnd);
            if (covering is not null)
            {
                return await ReadAsync(covering, windowStart, windowEnd, cancellationToken);
            }
        }

        var catalysts = await innerProvider.GetCatalystsAsync(ticker, windowStart, windowEnd, cancellationToken);
        await WriteAsync(path, catalysts, cancellationToken);
        return catalysts;
    }

    private string? FindCoveringCacheFile(string ticker, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var directory = Path.Combine(cacheRoot, ticker.Trim().ToUpperInvariant());
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var prefix = $"catalysts_{ProviderName.Trim().ToLowerInvariant()}_";
        string? best = null;
        var bestSpan = TimeSpan.MaxValue;
        foreach (var file in Directory.EnumerateFiles(directory, $"{prefix}*.json"))
        {
            var stamps = Path.GetFileNameWithoutExtension(file)[prefix.Length..].Split('_');
            if (stamps.Length != 2 ||
                !TryParseStamp(stamps[0], out var fileStart) ||
                !TryParseStamp(stamps[1], out var fileEnd))
            {
                continue;
            }

            if (fileStart <= windowStart && fileEnd >= windowEnd && (fileEnd - fileStart) < bestSpan)
            {
                bestSpan = fileEnd - fileStart;
                best = file;
            }
        }

        return best;
    }

    private static bool TryParseStamp(string stamp, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(
            stamp,
            "yyyyMMddHHmm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);

    private string GetPath(string ticker, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var safeTicker = ticker.Trim().ToUpperInvariant();
        var safeProvider = ProviderName.Trim().ToLowerInvariant();
        var start = windowStart.UtcDateTime.ToString("yyyyMMddHHmm");
        var end = windowEnd.UtcDateTime.ToString("yyyyMMddHHmm");
        return Path.Combine(cacheRoot, safeTicker, $"catalysts_{safeProvider}_{start}_{end}.json");
    }

    private static async Task<IReadOnlyList<CatalystEvent>> ReadAsync(
        string path,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var catalysts = await JsonSerializer.DeserializeAsync<List<CatalystEvent>>(
            stream,
            JsonOptions,
            cancellationToken);

        return (catalysts ?? [])
            .Where(catalyst => catalyst.Timestamp >= windowStart && catalyst.Timestamp <= windowEnd)
            .OrderBy(catalyst => catalyst.Timestamp)
            .ToArray();
    }

    private static async Task WriteAsync(
        string path,
        IReadOnlyList<CatalystEvent> catalysts,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Open(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            var json = JsonSerializer.Serialize(
                catalysts.OrderBy(catalyst => catalyst.Timestamp).ToArray(),
                JsonOptions);
            await writer.WriteAsync(json.AsMemory(), cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }
}
