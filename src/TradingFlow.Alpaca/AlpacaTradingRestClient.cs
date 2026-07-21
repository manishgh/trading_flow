using System.Net.Http.Headers;
using System.Text.Json;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Alpaca;

/// <summary>
/// Owns authenticated Alpaca trading REST transport and makes raw-before-interpretation mandatory
/// for every response. Higher-level broker and operator services consume only archived payloads.
/// </summary>
public sealed class AlpacaTradingRestClient : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly AlpacaRawResponseArchiver responseArchiver;

    public AlpacaTradingRestClient(
        HttpClient httpClient,
        AlpacaOptions options,
        IRawArchiveWriter rawArchiveWriter)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(options);
        responseArchiver = new AlpacaRawResponseArchiver(rawArchiveWriter);

        this.httpClient.BaseAddress = options.BaseUrl;
        SetHeader("APCA-API-KEY-ID", options.KeyId);
        SetHeader("APCA-API-SECRET-KEY", options.SecretKey);
        if (!this.httpClient.DefaultRequestHeaders.Accept.Any(value =>
                value.MediaType?.Equals("application/json", StringComparison.OrdinalIgnoreCase) == true))
        {
            this.httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    public async Task<ArchivedAlpacaResponse> SendAsync(
        HttpRequestMessage request,
        string artifactType,
        string? providerRecordId = null,
        string? correlationId = null,
        IReadOnlyDictionary<string, string>? attributes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        return await responseArchiver.ArchiveAsync(
            response,
            artifactType,
            providerRecordId,
            correlationId,
            attributes,
            cancellationToken);
    }

    public static string DescribeFailure(string operation, ArchivedAlpacaResponse response)
    {
        var providerMessage = NormalizeProviderMessage(TryReadProviderMessage(response.Payload));
        return String.IsNullOrWhiteSpace(providerMessage)
            ? $"Alpaca {operation} failed with HTTP {(Int32)response.StatusCode}. RawArchiveId={response.Archive.Manifest.ArchiveId}."
            : $"Alpaca {operation} failed with HTTP {(Int32)response.StatusCode}: {providerMessage}. RawArchiveId={response.Archive.Manifest.ArchiveId}.";
    }

    public void Dispose() => httpClient.Dispose();

    private void SetHeader(string name, string value)
    {
        httpClient.DefaultRequestHeaders.Remove(name);
        if (!String.IsNullOrWhiteSpace(value))
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }
    }

    private static string? TryReadProviderMessage(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
            // Unstructured provider failures remain available in the raw archive.
        }

        return null;
    }

    private static string? NormalizeProviderMessage(string? message)
    {
        if (String.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 512 ? singleLine : singleLine[..512];
    }
}
