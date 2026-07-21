using System.Net;
using System.Text;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Alpaca;

/// <summary>
/// Represents an Alpaca response whose exact payload has been durably archived. Consumers parse
/// <see cref="Payload"/>, which is reread from the committed archive rather than from the wire.
/// </summary>
public sealed record ArchivedAlpacaResponse(
    HttpStatusCode StatusCode,
    byte[] Payload,
    RawArchiveReceipt Archive,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Headers,
    string? CharacterSet)
{
    public bool IsSuccessStatusCode => (Int32)StatusCode is >= 200 and <= 299;

    public string ReadAsString()
    {
        if (!String.IsNullOrWhiteSpace(CharacterSet))
        {
            try
            {
                return Encoding.GetEncoding(CharacterSet).GetString(Payload);
            }
            catch (ArgumentException)
            {
                // Invalid provider charset metadata falls back to UTF-8 below.
            }
        }

        return Encoding.UTF8.GetString(Payload);
    }

    public IReadOnlyList<string> GetHeaderValues(string name) =>
        Headers.TryGetValue(name, out var values) ? values : Array.Empty<string>();
}

/// <summary>
/// Commits byte-exact Alpaca HTTP responses before callers inspect status or parse payloads.
/// </summary>
public sealed class AlpacaRawResponseArchiver
{
    private readonly IRawArchiveWriter rawArchiveWriter;

    public AlpacaRawResponseArchiver(IRawArchiveWriter rawArchiveWriter)
    {
        this.rawArchiveWriter = rawArchiveWriter ?? throw new ArgumentNullException(nameof(rawArchiveWriter));
    }

    public async Task<ArchivedAlpacaResponse> ArchiveAsync(
        HttpResponseMessage response,
        string artifactType,
        string? providerRecordId = null,
        string? correlationId = null,
        IReadOnlyDictionary<string, string>? attributes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var receivedAtUtc = DateTimeOffset.UtcNow;
        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var headers = CollectHeaders(response);
        var contentType = response.Content.Headers.ContentType;
        var receipt = await rawArchiveWriter.ArchiveAsync(
            new RawArchiveRequest(
                "alpaca",
                artifactType,
                receivedAtUtc,
                ResolveExtension(contentType?.MediaType),
                contentType?.ToString(),
                ProviderRecordId: providerRecordId,
                CorrelationId: correlationId,
                Http: new RawArchiveHttpMetadata(
                    response.RequestMessage?.RequestUri,
                    (Int32)response.StatusCode,
                    headers),
                Attributes: attributes),
            payload,
            cancellationToken);

        // Parsing from the committed copy makes raw-before-parse an enforceable boundary.
        var committedPayload = await File.ReadAllBytesAsync(receipt.PayloadPath, cancellationToken);
        return new ArchivedAlpacaResponse(
            response.StatusCode,
            committedPayload,
            receipt,
            headers,
            contentType?.CharSet);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CollectHeaders(HttpResponseMessage response)
    {
        var headers = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            headers[header.Key] = Array.AsReadOnly(header.Value.ToArray());
        }

        foreach (var header in response.Content.Headers)
        {
            headers[header.Key] = Array.AsReadOnly(header.Value.ToArray());
        }

        return headers;
    }

    private static string ResolveExtension(string? mediaType)
    {
        if (mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "json";
        }

        if (mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "txt";
        }

        return "bin";
    }
}
