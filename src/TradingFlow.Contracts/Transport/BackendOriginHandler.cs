namespace TradingFlow.Contracts.Transport;

/// <summary>Rejects insecure origins on every request, including saved settings from an earlier application version.</summary>
public sealed class BackendOriginHandler(HttpMessageHandler inner, bool allowDevelopmentLoopback = false) : DelegatingHandler(inner)
{
    /// <summary>Checks the origin before any session cookie or authentication body reaches the transport.</summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } uri)
            throw new ArgumentException("Configure an absolute HTTPS backend origin in Settings.");
        BackendOrigin.Normalize(uri.GetLeftPart(UriPartial.Authority), allowDevelopmentLoopback);
        return base.SendAsync(request, cancellationToken);
    }
}
