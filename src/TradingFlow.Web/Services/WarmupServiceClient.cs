using System.Net.Http.Json;
using System.Text.Json;
using TradingFlow.Contracts.V1;
using TradingFlow.Contracts.Transport;

namespace TradingFlow.Web.Services;

public sealed class WarmupServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private readonly string baseUrl;

    /// <summary>Uses the private host credential only on the configured loopback or cluster service origin; redirects cannot forward it.</summary>
    public WarmupServiceClient(IConfiguration configuration)
    {
        baseUrl = (configuration["WarmupService:BaseUrl"] ?? "http://127.0.0.1:53120").TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) || !String.IsNullOrEmpty(origin.UserInfo) ||
            !String.IsNullOrEmpty(origin.Query) || !String.IsNullOrEmpty(origin.Fragment) || origin.AbsolutePath != "/" ||
            origin.Scheme != Uri.UriSchemeHttps && !(origin.Scheme == Uri.UriSchemeHttp &&
                (origin.IsLoopback || !origin.Host.Contains('.') || origin.Host.EndsWith(".svc", StringComparison.OrdinalIgnoreCase) ||
                    origin.Host.EndsWith(".svc.cluster.local", StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Warm-up service origin must use HTTPS, loopback HTTP or a private cluster service DNS name.");
        var key = configuration[WarmupServiceAuthentication.EnvironmentKey] ?? configuration[WarmupServiceAuthentication.ConfigurationKey];
        if (!String.IsNullOrWhiteSpace(key)) httpClient.DefaultRequestHeaders.Add(WarmupServiceAuthentication.HeaderName, key);
    }

    public string BaseUrl => baseUrl;

    public Task<IReadOnlyList<WarmupTickerIntentDto>?> GetWatchlistAsync(CancellationToken cancellationToken)
    {
        return httpClient.GetFromJsonAsync<IReadOnlyList<WarmupTickerIntentDto>>(
            $"{baseUrl}/api/warmup/watchlist",
            cancellationToken);
    }

    public async Task<WarmupAcceptedDto?> AddWatchlistAsync(WarmupWatchRequestDto request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"{baseUrl}/api/warmup/watchlist", request, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WarmupAcceptedDto>(JsonOptions, cancellationToken);
    }

    public async Task RemoveAsync(string ticker, CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync($"{baseUrl}/api/warmup/watchlist/{Uri.EscapeDataString(ticker)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<WarmupRunQueuedDto?> RunNowAsync(WarmupRunNowRequestDto request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"{baseUrl}/api/warmup/run-now", request, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WarmupRunQueuedDto>(JsonOptions, cancellationToken);
    }

    public Task<IReadOnlyList<WarmupRunRecordDto>?> GetRunsAsync(CancellationToken cancellationToken)
    {
        return httpClient.GetFromJsonAsync<IReadOnlyList<WarmupRunRecordDto>>(
            $"{baseUrl}/api/warmup/runs",
            cancellationToken);
    }

    /// <summary>Reads accepted durable jobs, including queued/running work with no completed cache record yet.</summary>
    public Task<WarmupQueueStatus[]?> GetJobsAsync(CancellationToken token) =>
        httpClient.GetFromJsonAsync<WarmupQueueStatus[]>($"{baseUrl}/api/warmup/jobs", token);

    /// <summary>Persists cancellation before interrupting the owning warmup worker.</summary>
    public async Task<WarmupQueueStatus?> CancelJobAsync(string id, CancellationToken token)
    {
        using var response = await httpClient.PostAsJsonAsync($"{baseUrl}/api/warmup/jobs/{Uri.EscapeDataString(id)}/cancel", new { }, token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<WarmupQueueStatus>(cancellationToken: token);
    }

    /// <summary>Retries the original captured archive files without downloading current replacement data.</summary>
    public async Task<WarmupArchiveRetryResponse?> RetryArchivesAsync(string id, CancellationToken token)
    {
        using var response = await httpClient.PostAsJsonAsync($"{baseUrl}/api/warmup/runs/{Uri.EscapeDataString(id)}/archive/retry", new { }, token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<WarmupArchiveRetryResponse>(cancellationToken: token);
    }

    /// <summary>Shows the service's concrete rejection reason instead of rendering its JSON envelope as user text.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message)) body = message.GetString() ?? body;
        }
        catch (JsonException) { }
        throw new InvalidOperationException(String.IsNullOrWhiteSpace(body)
            ? $"Warmup service returned {(int)response.StatusCode}."
            : body);
    }
}

public sealed record WarmupWatchRequestDto(
    IReadOnlyCollection<string> Tickers,
    string? Reason = null,
    string? RequestedBy = null,
    int? WarmupDays = null,
    int? NewsLookbackDays = null,
    IReadOnlyCollection<string>? Timeframes = null,
    bool? IncludeNews = null,
    bool RunNow = false,
    string? IdempotencyKey = null);

public sealed record WarmupRunNowRequestDto(
    IReadOnlyCollection<string>? Tickers = null,
    string? Reason = null,
    string? IdempotencyKey = null);

public sealed record WarmupAcceptedDto(
    int Accepted,
    IReadOnlyList<string> Tickers,
    bool RunQueued);

public sealed record WarmupRunQueuedDto(
    string RunId,
    bool Queued);

public sealed record WarmupTickerIntentDto(
    string Ticker,
    string Reason,
    string RequestedBy,
    DateTimeOffset RequestedAtUtc,
    int WarmupDays,
    int NewsLookbackDays,
    IReadOnlyList<string> Timeframes,
    bool IncludeNews,
    bool Active,
    DateTimeOffset? LastWarmedAtUtc,
    string LastStatus,
    string? LastError);

public sealed record WarmupRunRecordDto(
    string RunId,
    string Reason,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string Status,
    int RequestedTickerCount,
    int SucceededTickerCount,
    int FailedTickerCount,
    IReadOnlyList<WarmupTickerResultDto> Tickers);

public sealed record WarmupTickerResultDto(
    string Ticker,
    bool Succeeded,
    int BarCount,
    int CatalystCount,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    IReadOnlyList<string> Timeframes,
    IReadOnlyList<string> ArtifactPaths,
    string? Error,
    bool CacheCompleted = false,
    string ArchiveStatus = "not_attempted",
    string? ArchiveManifestPath = null,
    string? ArchiveError = null);
