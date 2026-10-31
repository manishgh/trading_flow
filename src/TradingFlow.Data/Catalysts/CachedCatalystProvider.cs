using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Data.Csv;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Catalysts;

/// <summary>Retains new normalized assessments in the existing immutable archive before caching them; legacy originals stay protected.</summary>
public sealed class CachedCatalystProvider(
    ICatalystProvider innerProvider,
    string cacheRoot,
    string cachePolicy,
    IRawArchiveWriter archiveWriter,
    string durableArchiveRoot) : ICatalystProvider
{
    public const string ArchiveReceiptSuffix = ".archive-receipt.json";
    public const string AssessmentArtifactType = "normalized-catalyst-assessments";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly string archiveRoot = new RawArchiveOptions(durableArchiveRoot).RootPath;
    private sealed record CacheIdentity(string PayloadSha256, string ReceiptSha256);

    public string ProviderName => innerProvider.ProviderName;

    /// <summary>Reuses original assessments or archives new exact bytes before publishing a disposable cache copy.</summary>
    public async Task<IReadOnlyList<CatalystEvent>> GetCatalystsAsync(
        string ticker,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        var path = GetPath(ticker, windowStart, windowEnd);
        RequireSeparateArchiveOwner(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RejectReparseAncestors(path + ".lock");
        await using var cacheLock = await CachedMarketDataProvider.AcquireExclusiveFileLockAsync(path + ".lock", cancellationToken);
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
                RejectReparseAncestors(covering + ".lock");
                await using var coveringLock = await CachedMarketDataProvider.AcquireExclusiveFileLockAsync(covering + ".lock", cancellationToken);
                return await ReadAsync(covering, windowStart, windowEnd, cancellationToken);
            }

            if (cachePolicy.Equals("cache_only", StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNotFoundException(
                    $"No cached {ProviderName} catalysts cover {ticker} from " +
                    $"{windowStart:O} through {windowEnd:O}.",
                    path);
            }
        }

        var previous = await ReadReplacementIdentityAsync(path, cancellationToken);
        var catalysts = await innerProvider.GetCatalystsAsync(ticker, windowStart, windowEnd, cancellationToken);
        await WriteAsync(path, catalysts, previous, cancellationToken);
        return catalysts;
    }

    /// <summary>Chooses the smallest previously fetched window covering the requested timestamps without reassessment.</summary>
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

    /// <summary>Reads the UTC minute precision used by existing cache filenames.</summary>
    private static bool TryParseStamp(string stamp, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(
            stamp,
            "yyyyMMddHHmm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);

    /// <summary>Keeps existing cache locations unchanged for legacy reads.</summary>
    private string GetPath(string ticker, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var safeTicker = ticker.Trim().ToUpperInvariant();
        var safeProvider = ProviderName.Trim().ToLowerInvariant();
        var start = windowStart.UtcDateTime.ToString("yyyyMMddHHmm");
        var end = windowEnd.UtcDateTime.ToString("yyyyMMddHHmm");
        return Path.Combine(cacheRoot, safeTicker, $"catalysts_{safeProvider}_{start}_{end}.json");
    }

    /// <summary>Verifies archive-bound bytes before reading; legacy bytes are read without migration or replacement.</summary>
    private async Task<IReadOnlyList<CatalystEvent>> ReadAsync(
        string path,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        RejectReparseAncestors(path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (File.Exists(path + ArchiveReceiptSuffix))
            await VerifyArchiveAsync(path, bytes, cancellationToken);
        var catalysts = JsonSerializer.Deserialize<List<CatalystEvent>>(bytes, JsonOptions);

        return (catalysts ?? [])
            .Where(catalyst => catalyst.Timestamp >= windowStart && catalyst.Timestamp <= windowEnd)
            .OrderBy(catalyst => catalyst.Timestamp)
            .ToArray();
    }

    /// <summary>Refuses replacement of unarchived originals or damaged archive-bound cache copies before fetching.</summary>
    private async Task<CacheIdentity?> ReadReplacementIdentityAsync(string path, CancellationToken cancellationToken)
    {
        RejectReparseAncestors(path);
        RejectReparseAncestors(path + ArchiveReceiptSuffix);
        if (!File.Exists(path))
        {
            if (File.Exists(path + ArchiveReceiptSuffix))
                throw new InvalidDataException("Catalyst archive receipt has no matching cache; preserve it for review.");
            return null;
        }
        if (!File.Exists(path + ArchiveReceiptSuffix))
            throw new InvalidOperationException("Cannot refresh an original catalyst cache without verified archival proof. Preserve its receipt and assessment clocks.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var receiptBytes = await File.ReadAllBytesAsync(path + ArchiveReceiptSuffix, cancellationToken);
        await VerifyArchiveAsync(path, bytes, cancellationToken);
        return new(Hash(bytes), Hash(receiptBytes));
    }

    /// <summary>Checks the cache hash and length against both the durable assessment payload and its original manifest.</summary>
    private async Task VerifyArchiveAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        RejectReparseAncestors(path + ArchiveReceiptSuffix);
        var receipt = JsonSerializer.Deserialize<RawArchiveReceipt>(
            await File.ReadAllBytesAsync(path + ArchiveReceiptSuffix, cancellationToken), JsonOptions)
            ?? throw new InvalidDataException("Catalyst archive receipt is empty.");
        await VerifyReceiptAsync(receipt, bytes, cancellationToken);
    }

    /// <summary>Requires exact archived bytes and complete matching metadata; archive time never substitutes for event clocks.</summary>
    private async Task VerifyReceiptAsync(RawArchiveReceipt receipt, byte[] bytes, CancellationToken cancellationToken)
    {
        var manifest = receipt.Manifest;
        if (manifest is null || manifest.SchemaVersion != 1 || String.IsNullOrWhiteSpace(manifest.ArchiveId) ||
            !Path.IsPathFullyQualified(receipt.PayloadPath) ||
            !Path.IsPathFullyQualified(receipt.ManifestPath) ||
            receipt.ManifestPath != receipt.PayloadPath + ".manifest.json" ||
            manifest.ArtifactType != AssessmentArtifactType ||
            manifest.Source != ProviderName.Trim().ToLowerInvariant() ||
            manifest.ReceivedAtUtc == default || manifest.ReceivedAtUtc.Offset != TimeSpan.Zero ||
            manifest.PayloadFileName != Path.GetFileName(receipt.PayloadPath) ||
            manifest.ByteLength != bytes.LongLength ||
            !String.Equals(manifest.Sha256, Hash(bytes), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Catalyst cache does not match its normalized-assessment archive receipt.");
        RequireDurableArchivePath(receipt.PayloadPath);
        RequireDurableArchivePath(receipt.ManifestPath);
        var dayDirectory = Path.Combine(archiveRoot, manifest.Source, manifest.ReceivedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var archivePrefix = manifest.ReceivedAtUtc.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + "-";
        if (!String.Equals(Path.GetDirectoryName(receipt.PayloadPath), dayDirectory, PathComparison) ||
            !manifest.ArchiveId.StartsWith(archivePrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(manifest.ArchiveId[archivePrefix.Length..], "N", out _) ||
            manifest.PayloadFileName != $"{manifest.ArchiveId}-{AssessmentArtifactType}.json" ||
            manifest.ContentType != "application/json")
            throw new InvalidDataException("Catalyst assessment proof does not match the configured archive's original writer identity.");
        var storedManifest = JsonSerializer.Deserialize<RawArchiveManifest>(
            await File.ReadAllBytesAsync(receipt.ManifestPath, cancellationToken), JsonOptions);
        if (storedManifest is null || !JsonSerializer.SerializeToUtf8Bytes(storedManifest, JsonOptions)
                .AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions)) ||
            !(await File.ReadAllBytesAsync(receipt.PayloadPath, cancellationToken)).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("Archived normalized assessments or their manifest changed; preserve the cache for review.");
    }

    /// <summary>Requires archive ownership separate from this cache and any enclosing disposable run input workspace.</summary>
    private void RequireSeparateArchiveOwner(string cachePath)
    {
        var root = Path.GetFullPath(cacheRoot);
        if (IsWithin(root, archiveRoot) || IsWithin(archiveRoot, root))
            throw new InvalidDataException("Durable catalyst archive and disposable cache roots must be separate.");
        for (var current = archiveRoot; current is not null; current = Path.GetDirectoryName(current))
            if (Path.GetFileName(current).StartsWith(".run-inputs-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A run input workspace cannot own the durable catalyst archive.");
        RejectReparseAncestors(archiveRoot);
        RejectReparseAncestors(cachePath);
    }

    /// <summary>Accepts only canonical payload/manifest paths beneath the configured durable archive with no link aliases.</summary>
    private void RequireDurableArchivePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!String.Equals(path, fullPath, PathComparison) || !IsWithin(fullPath, archiveRoot) ||
            IsWithin(fullPath, Path.GetFullPath(cacheRoot)))
            throw new InvalidDataException("Catalyst assessment proof must reference the configured durable archive outside the cache.");
        RejectReparseAncestors(fullPath);
    }

    /// <summary>Compares directory boundaries rather than accepting similarly prefixed sibling paths.</summary>
    private static bool IsWithin(string path, string root) => String.Equals(path, root, PathComparison) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>Refuses existing file links and junctions from the named path through every ancestor.</summary>
    private static void RejectReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Catalyst cache and archive paths cannot traverse links or junctions.");
    }

    /// <summary>Computes the exact byte identity retained across collection and the protected commit boundary.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Archives one sorted UTF8 representation first, then publishes its proof and cache; every archive version remains durable.</summary>
    private async Task WriteAsync(
        string path,
        IReadOnlyList<CatalystEvent> catalysts,
        CacheIdentity? previous,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(catalysts.OrderBy(catalyst => catalyst.Timestamp).ToArray(), JsonOptions);
        var receipt = await archiveWriter.ArchiveAsync(new RawArchiveRequest(
            ProviderName.Trim().ToLowerInvariant(), AssessmentArtifactType, DateTimeOffset.UtcNow,
            "json", "application/json", Attributes: new Dictionary<string, string>
            {
                ["clock_policy"] = "archive-write-time; event receipt and assessment clocks retained in payload"
            }), bytes, cancellationToken);
        await VerifyReceiptAsync(receipt, bytes, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var receiptPath = path + ArchiveReceiptSuffix;
        var tempReceiptPath = $"{receiptPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
            await File.WriteAllBytesAsync(tempReceiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions), cancellationToken);
            // Revalidate the exact old bytes after staging while the same-path filesystem lease remains held.
            if (await ReadReplacementIdentityAsync(path, cancellationToken) != previous)
                throw new InvalidDataException("Catalyst cache changed during collection; preserve the newly appeared original.");
            PublishCachePair(tempPath, path, tempReceiptPath, receiptPath, previous is not null);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (File.Exists(tempReceiptPath)) File.Delete(tempReceiptPath);
        }
    }

    /// <summary>Publishes initial files exclusively; refresh replaces only the proven pair under the caller's same-path lease.</summary>
    private static void PublishCachePair(string stagedCache, string cachePath, string stagedReceipt, string receiptPath, bool refresh)
    {
        // Initial publication must never replace a legacy original appearing after the last identity check.
        File.Move(stagedCache, cachePath, overwrite: refresh);
        File.Move(stagedReceipt, receiptPath, overwrite: refresh);
    }
}
