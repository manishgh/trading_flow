using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Polly;
using Polly.Bulkhead;
using TradingFlow.Etoro.Authentication;
using TradingFlow.Etoro.Configuration;

namespace TradingFlow.Etoro.Http;

public sealed class EtoroApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly HttpClient httpClient;
    private readonly EtoroOptions options;
    private readonly EtoroCredentialsProvider credentialsProvider;
    private readonly EtoroRateLimiter rateLimiter;
    private readonly AsyncBulkheadPolicy<HttpResponseMessage> readBulkhead;
    private readonly AsyncBulkheadPolicy<HttpResponseMessage> writeBulkhead;
    private readonly Random jitterRandom = new();

    public EtoroApiClient(
        HttpClient httpClient,
        EtoroOptions options,
        EtoroCredentialsProvider credentialsProvider,
        EtoroRateLimiter rateLimiter)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.credentialsProvider = credentialsProvider;
        this.rateLimiter = rateLimiter;
        readBulkhead = TradingFlow.Domain.Http.RateLimiterFactory.CreateBulkhead(
            options.RateLimits.ReadConcurrency,
            options.RateLimits.ReadQueueLimit);
        writeBulkhead = TradingFlow.Domain.Http.RateLimiterFactory.CreateBulkhead(
            options.RateLimits.WriteConcurrency,
            options.RateLimits.WriteQueueLimit);

        this.httpClient.BaseAddress ??= EnsureTrailingSlash(options.RestBaseUrl);
        this.httpClient.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
    }

    public Task<T> GetAsync<T>(string requestPath, CancellationToken cancellationToken)
    {
        return SendAsync<T>(HttpMethod.Get, requestPath, null, EtoroApiOperation.Read, allowUnsafeWriteRetry: false, cancellationToken);
    }

    public Task<T> PostAsync<T>(string requestPath, object body, bool allowUnsafeWriteRetry, CancellationToken cancellationToken)
    {
        return SendAsync<T>(HttpMethod.Post, requestPath, body, EtoroApiOperation.Write, allowUnsafeWriteRetry, cancellationToken);
    }

    public async Task SendAsync(HttpMethod method, string requestPath, object? body, EtoroApiOperation operation, bool allowUnsafeWriteRetry, CancellationToken cancellationToken)
    {
        _ = await SendAsync<JsonElement>(method, requestPath, body, operation, allowUnsafeWriteRetry, cancellationToken);
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string requestPath,
        object? body,
        EtoroApiOperation operation,
        bool allowUnsafeWriteRetry,
        CancellationToken cancellationToken)
    {
        var maxAttempts = operation == EtoroApiOperation.Write && !allowUnsafeWriteRetry
            ? 1
            : Math.Max(1, options.MaxRetries + 1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await rateLimiter.WaitAsync(operation, cancellationToken);
            var requestId = Guid.NewGuid().ToString("D");
            using var request = CreateRequest(method, requestPath, body, requestId);

            HttpResponseMessage? response = null;
            string responseBody = String.Empty;
            try
            {
                response = await SendThroughBulkheadAsync(operation, request, cancellationToken);
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    if (typeof(T) == typeof(string))
                    {
                        return (T)(object)responseBody;
                    }

                    if (String.IsNullOrWhiteSpace(responseBody))
                    {
                        return default!;
                    }

                    var parsed = JsonSerializer.Deserialize<T>(responseBody, JsonOptions);
                    if (parsed is null)
                    {
                        throw new EtoroApiException(response.StatusCode, requestPath, responseBody, requestId);
                    }

                    return parsed;
                }

                if (!ShouldRetry(response.StatusCode) || attempt == maxAttempts)
                {
                    throw new EtoroApiException(response.StatusCode, requestPath, responseBody, requestId);
                }

                await DelayBeforeRetryAsync(response, attempt, cancellationToken);
            }
            catch (BulkheadRejectedException) when (attempt == maxAttempts)
            {
                throw;
            }
            catch (BulkheadRejectedException) when (attempt < maxAttempts && operation == EtoroApiOperation.Read)
            {
                await DelayBeforeRetryAsync(response, attempt, cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < maxAttempts)
            {
                await DelayBeforeRetryAsync(response, attempt, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                await DelayBeforeRetryAsync(response, attempt, cancellationToken);
            }
            finally
            {
                response?.Dispose();
            }
        }

        throw new InvalidOperationException("eToro retry loop exited unexpectedly.");
    }

    private Task<HttpResponseMessage> SendThroughBulkheadAsync(
        EtoroApiOperation operation,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var bulkhead = operation == EtoroApiOperation.Read ? readBulkhead : writeBulkhead;
        return bulkhead.ExecuteAsync(
            token => TradingFlow.Domain.Logging.ApiProfiler.ProfileAsync("eToro", request.RequestUri?.ToString() ?? "unknown", request.Method.ToString(), () => httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)),
            cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string requestPath, object? body, string requestId)
    {
        var credentials = credentialsProvider.GetCredentials();
        var request = new HttpRequestMessage(method, NormalizeRequestUri(requestPath));
        request.Headers.TryAddWithoutValidation("x-request-id", requestId);
        request.Headers.TryAddWithoutValidation("x-api-key", credentials.ApiKey);
        request.Headers.TryAddWithoutValidation("x-user-key", credentials.UserKey);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return request;
    }

    private async Task DelayBeforeRetryAsync(HttpResponseMessage? response, int attempt, CancellationToken cancellationToken)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            await Task.Delay(Clamp(delta), cancellationToken);
            return;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(Clamp(delay), cancellationToken);
                return;
            }
        }

        var exponentialMs = options.BaseBackoffMs * Math.Pow(2, Math.Max(0, attempt - 1));
        var jitterMs = options.UseJitter ? jitterRandom.Next(0, Math.Max(1, options.BaseBackoffMs)) : 0;
        await Task.Delay(Clamp(TimeSpan.FromMilliseconds(exponentialMs + jitterMs)), cancellationToken);
    }

    private TimeSpan Clamp(TimeSpan delay)
    {
        var max = TimeSpan.FromSeconds(options.MaxBackoffSeconds);
        if (delay > max)
        {
            return max;
        }

        return delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : delay;
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.TooManyRequests or
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
    }

    private Uri NormalizeRequestUri(string requestPath)
    {
        if (Uri.TryCreate(requestPath, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        if (requestPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            var baseAddress = httpClient.BaseAddress ?? throw new InvalidOperationException("HttpClient.BaseAddress is required.");
            return new Uri($"{baseAddress.Scheme}://{baseAddress.Host}{requestPath}", UriKind.Absolute);
        }

        return new Uri(requestPath.TrimStart('/'), UriKind.Relative);
    }
}
