using System.Net.Http.Headers;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Alpaca;

/// <summary>
/// Authenticated market-data REST transport with the same raw-before-parse guarantee
/// as trading REST calls.
/// </summary>
public sealed class AlpacaMarketDataRestClient : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly AlpacaRawResponseArchiver responseArchiver;

    public AlpacaMarketDataRestClient(
        HttpClient httpClient,
        AlpacaOptions options,
        IRawArchiveWriter rawArchiveWriter)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(options);
        responseArchiver = new AlpacaRawResponseArchiver(rawArchiveWriter);
        this.httpClient.BaseAddress = AlpacaEndpointResolver.Resolve(options.Profile).MarketDataRest;
        SetHeader("APCA-API-KEY-ID", options.KeyId);
        SetHeader("APCA-API-SECRET-KEY", options.SecretKey);
        this.httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ArchivedAlpacaResponse> SendAsync(
        HttpRequestMessage request,
        string artifactType,
        string? providerRecordId = null,
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
            cancellationToken: cancellationToken);
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
}
