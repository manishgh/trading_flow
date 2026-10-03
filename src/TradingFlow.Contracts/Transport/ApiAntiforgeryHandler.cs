using System.Text.Json;

namespace TradingFlow.Contracts.Transport;

/// <summary>Fetches a session-bound token for each mutation, including login/logout.</summary>
public sealed class ApiAntiforgeryHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { IsAbsoluteUri: true } uri && uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) &&
            request.Method != HttpMethod.Get && request.Method != HttpMethod.Head && request.Method != HttpMethod.Options)
        {
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/api/auth/csrf"));
            if (request.Headers.TryGetValues("ngrok-skip-browser-warning", out var bypass))
                tokenRequest.Headers.TryAddWithoutValidation("ngrok-skip-browser-warning", bypass);
            using var tokenResponse = await base.SendAsync(tokenRequest, cancellationToken);
            tokenResponse.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
            var token = json.RootElement.GetProperty("requestToken").GetString()
                ?? throw new HttpRequestException("The server returned no session security token.");
            request.Headers.Remove("X-CSRF-TOKEN");
            request.Headers.Add("X-CSRF-TOKEN", token);
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
