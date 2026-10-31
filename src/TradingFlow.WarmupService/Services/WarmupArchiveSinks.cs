using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Engine.Storage;
using TradingFlow.WarmupService.Models;

namespace TradingFlow.WarmupService.Services;

public interface IWarmupArchiveSink
{
    Task<WarmupArchivePublication> PublishAsync(string runId, string ticker, IReadOnlyList<string> localPaths, CancellationToken cancellationToken);
}

/// <summary>Freezes local bytes first; cloud failures leave an explicit retryable receipt.</summary>
public sealed class CompositeWarmupArchiveSink(params IWarmupArchiveSink[] sinks) : IWarmupArchiveSink
{
    /// <summary>Uploads verified immutable files, never the replaceable ticker cache.</summary>
    public async Task<WarmupArchivePublication> PublishAsync(string runId, string ticker, IReadOnlyList<string> localPaths, CancellationToken cancellationToken)
    {
        if (sinks.FirstOrDefault() is not LocalWarmupArchiveSink local)
            throw new InvalidOperationException("Warmup archives require a local immutable bundle before remote publication.");
        var frozen = await local.PublishAsync(runId, ticker, localPaths, cancellationToken);
        var receipt = await local.ReadReceiptAsync(frozen.ManifestPath!, cancellationToken);
        if (receipt?.Status == "remote_committed") return frozen with { Status = receipt.Status };
        var attempted = false;
        try
        {
            foreach (var sink in sinks.Skip(1))
            {
                if (sink is SasAzureBlobWarmupArchiveSink { IsConfigured: false }) continue;
                attempted = true;
                await local.WriteReceiptAsync(frozen with { Status = "remote_pending" }, cancellationToken);
                await sink.PublishAsync(runId, ticker, [.. frozen.FrozenPaths, frozen.ManifestPath!], cancellationToken);
            }
            var result = !attempted && receipt?.Status == "remote_pending"
                ? frozen with { Status = "remote_pending", Error = "Remote delivery remains pending; no remote archive is configured." }
                : frozen with { Status = attempted ? "remote_committed" : "local_committed" };
            await local.WriteReceiptAsync(result, cancellationToken);
            return result;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var pending = frozen with { Status = "remote_pending", Error = error is HttpRequestException
                ? "Archive transport failed; the captured files remain pending for retry." : error.Message };
            await local.WriteReceiptAsync(pending, cancellationToken);
            return pending;
        }
    }
}

/// <summary>Stores immutable copied bytes and hashes; cache paths are not archived evidence.</summary>
public sealed class LocalWarmupArchiveSink(string archiveRoot) : IWarmupArchiveSink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Commits a complete directory atomically; a retry verifies and reuses the original frozen bytes.</summary>
    public async Task<WarmupArchivePublication> PublishAsync(string runId, string ticker, IReadOnlyList<string> localPaths, CancellationToken cancellationToken)
    {
        ValidateSegment(runId);
        ValidateSegment(ticker);
        var destination = Path.GetFullPath(Path.Combine(archiveRoot, "runs", runId, ticker.ToUpperInvariant()));
        var manifestPath = Path.Combine(destination, "manifest.json");
        if (Directory.Exists(destination)) return await ReadAndVerifyAsync(manifestPath, runId, ticker, cancellationToken);
        if (localPaths.Count == 0) throw new InvalidDataException("Archive publication requires captured files.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var stage = destination + ".preparing-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            var files = new List<WarmupArchiveFile>();
            foreach (var path in localPaths)
            {
                var name = $"{files.Count:D4}-{Path.GetFileName(path)}";
                var target = Path.Combine(stage, name);
                await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous))
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
                {
                    await input.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                }
                await using var verified = File.OpenRead(target);
                files.Add(new(name, verified.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(verified, cancellationToken))));
            }
            var manifest = new WarmupArchiveManifest(runId, ticker.ToUpperInvariant(), DateTimeOffset.UtcNow, files);
            await AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(manifest, Json), cancellationToken);
            try { Directory.Move(stage, destination); }
            catch (IOException) when (Directory.Exists(destination)) { }
            return await ReadAndVerifyAsync(manifestPath, runId, ticker, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                if (!Path.GetFullPath(stage).StartsWith(destination + ".preparing-", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Archive staging cleanup escaped its owned directory.");
                Directory.Delete(stage, true);
            }
        }
    }

    /// <summary>Verifies every immutable payload file before a first upload or retry.</summary>
    private static async Task<WarmupArchivePublication> ReadAndVerifyAsync(string path, string runId, string ticker, CancellationToken token)
    {
        var manifest = JsonSerializer.Deserialize<WarmupArchiveManifest>(await File.ReadAllTextAsync(path, token), Json)
            ?? throw new InvalidDataException("Archive manifest is empty.");
        if (manifest.RunId != runId || !manifest.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) || manifest.Files.Count == 0)
            throw new InvalidDataException("Archive manifest identity differs from the requested run.");
        var paths = new List<string>();
        foreach (var file in manifest.Files)
        {
            if (Path.GetFileName(file.Name) != file.Name) throw new InvalidDataException("Archive file escaped its manifest directory.");
            var source = Path.Combine(Path.GetDirectoryName(path)!, file.Name);
            await using var stream = File.OpenRead(source);
            if (stream.Length != file.Length || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)) != file.Sha256)
                throw new InvalidDataException("Frozen archive bytes differ from their committed hash.");
            paths.Add(source);
        }
        return new("local_committed", path, paths);
    }

    /// <summary>Persists delivery status separately from the immutable data manifest.</summary>
    public Task WriteReceiptAsync(WarmupArchivePublication receipt, CancellationToken token) =>
        AtomicFileArtifactWriter.Instance.WriteTextAsync(receipt.ManifestPath! + ".delivery.json", JsonSerializer.Serialize(receipt, Json), token);

    /// <summary>Loads delivery state so successful retries do not republish completed bundles.</summary>
    public async Task<WarmupArchivePublication?> ReadReceiptAsync(string manifestPath, CancellationToken token) =>
        File.Exists(manifestPath + ".delivery.json")
            ? JsonSerializer.Deserialize<WarmupArchivePublication>(await File.ReadAllTextAsync(manifestPath + ".delivery.json", token), Json) : null;

    /// <summary>Prevents run/ticker identifiers from selecting another archive directory.</summary>
    private static void ValidateSegment(string value)
    {
        if (String.IsNullOrWhiteSpace(value) || value.Length > 100 || value.Any(character => !Char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')) || value is "." or "..")
            throw new ArgumentException("Archive run and ticker identifiers must be simple path segments.");
    }
}

public sealed class SasAzureBlobWarmupArchiveSink(HttpClient httpClient, string? containerSasUrl,
    string blobPrefix, ILogger<SasAzureBlobWarmupArchiveSink> logger) : IWarmupArchiveSink
{
    public bool IsConfigured => !String.IsNullOrWhiteSpace(containerSasUrl);

    /// <summary>Publishes immutable files to stable names, refusing a different existing blob.</summary>
    public async Task<WarmupArchivePublication> PublishAsync(string runId, string ticker, IReadOnlyList<string> localPaths, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            logger.LogDebug("Blob archive not configured for {Ticker}.", ticker);
            return new("not_configured", null, []);
        }
        foreach (var path in localPaths) await UploadAsync(path, BuildBlobName(runId, ticker, path), cancellationToken);
        return new("remote_committed", null, localPaths);
    }

    /// <summary>Uses create-only uploads and verifies matching hashes when a retry finds an existing blob.</summary>
    private async Task UploadAsync(string path, string blobName, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
        stream.Position = 0;
        var uri = BuildBlobUri(blobName);
        using var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = new StreamContent(stream) };
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        request.Headers.Add("x-ms-version", "2023-11-03");
        request.Headers.Add("x-ms-meta-sha256", hash);
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var response = await httpClient.SendAsync(request, token);
        if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK) return;
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, uri);
            using var existing = await httpClient.SendAsync(head, token);
            if (existing.IsSuccessStatusCode && existing.Content.Headers.ContentLength == stream.Length &&
                existing.Headers.TryGetValues("x-ms-meta-sha256", out var values) && values.SingleOrDefault() == hash) return;
            throw new InvalidDataException($"Existing archive blob differs from the captured bytes: {blobName}.");
        }
        throw new InvalidOperationException($"Blob upload failed for {blobName}: HTTP {(int)response.StatusCode}.");
    }

    /// <summary>Builds stable retry names without today's date or a mutable cache location.</summary>
    private string BuildBlobName(string runId, string ticker, string path) => String.Join('/',
        new[] { blobPrefix.Trim('/'), Uri.EscapeDataString(ticker), Uri.EscapeDataString(runId), Uri.EscapeDataString(Path.GetFileName(path)) }
            .Where(value => !String.IsNullOrWhiteSpace(value)));

    /// <summary>Adds a blob path while retaining the SAS query without exposing it in messages.</summary>
    private Uri BuildBlobUri(string blobName)
    {
        var sas = new Uri(containerSasUrl!, UriKind.Absolute);
        return new Uri(sas.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + blobName + sas.Query);
    }
}
