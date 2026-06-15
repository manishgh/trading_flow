using System.Net;
using Microsoft.Extensions.Options;
using TradingFlow.Engine.Storage;
using TradingFlow.WarmupService.Models;

namespace TradingFlow.WarmupService.Services;

public interface IWarmupArchiveSink
{
    Task PublishAsync(string runId, string ticker, IReadOnlyList<string> localPaths, CancellationToken cancellationToken);
}

public sealed class CompositeWarmupArchiveSink(params IWarmupArchiveSink[] sinks) : IWarmupArchiveSink
{
    public async Task PublishAsync(string runId, string ticker, IReadOnlyList<string> localPaths, CancellationToken cancellationToken)
    {
        foreach (var sink in sinks)
        {
            await sink.PublishAsync(runId, ticker, localPaths, cancellationToken);
        }
    }
}

public sealed class LocalWarmupArchiveSink(string archiveRoot) : IWarmupArchiveSink
{
    public async Task PublishAsync(string runId, string ticker, IReadOnlyList<string> localPaths, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(archiveRoot);
        var manifestPath = Path.Combine(archiveRoot, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{WarmupText.NormalizeSegment(ticker)}-{runId}.json");
        await AtomicFileArtifactWriter.Instance.WriteTextAsync(
            manifestPath,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                RunId = runId,
                Ticker = ticker,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Files = localPaths.Select(Path.GetFullPath).ToArray()
            }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);
    }
}

public sealed class SasAzureBlobWarmupArchiveSink(
    HttpClient httpClient,
    string? containerSasUrl,
    string blobPrefix,
    ILogger<SasAzureBlobWarmupArchiveSink> logger) : IWarmupArchiveSink
{
    public async Task PublishAsync(string runId, string ticker, IReadOnlyList<string> localPaths, CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(containerSasUrl))
        {
            logger.LogDebug("Blob archive skipped for {Ticker}; Warmup:BlobContainerSasUrl is not configured.", ticker);
            return;
        }

        foreach (var path in localPaths.Where(File.Exists))
        {
            var blobName = BuildBlobName(runId, ticker, path);
            await UploadAsync(path, blobName, cancellationToken);
        }
    }

    private async Task UploadAsync(string path, string blobName, CancellationToken cancellationToken)
    {
        var uploadUri = BuildBlobUri(blobName);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUri)
        {
            Content = new StreamContent(stream)
        };
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        request.Headers.Add("x-ms-version", "2023-11-03");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Blob upload failed for {blobName}: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
    }

    private string BuildBlobName(string runId, string ticker, string path)
    {
        var name = Path.GetFileName(path);
        var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? "root");
        return String.Join(
            '/',
            new[]
            {
                blobPrefix.Trim('/'),
                DateTimeOffset.UtcNow.ToString("yyyy/MM/dd"),
                WarmupText.NormalizeSegment(ticker),
                runId,
                WarmupText.NormalizeSegment(parent),
                WarmupText.NormalizeSegment(name)
            }.Where(x => !String.IsNullOrWhiteSpace(x)));
    }

    private Uri BuildBlobUri(string blobName)
    {
        var sas = new Uri(containerSasUrl!, UriKind.Absolute);
        var separator = String.IsNullOrEmpty(sas.Query) ? "?" : "";
        var baseWithoutQuery = sas.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri($"{baseWithoutQuery}/{Uri.EscapeDataString(blobName).Replace("%2F", "/")}{separator}{sas.Query}");
    }
}
