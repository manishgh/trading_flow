using System.Net.Http.Json;
using System.Text.Json;

namespace TradingFlow.Web.Services;

public sealed class WarmupServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private readonly string baseUrl;

    public WarmupServiceClient(IConfiguration configuration)
    {
        baseUrl = (configuration["WarmupService:BaseUrl"] ?? "http://127.0.0.1:53120").TrimEnd('/');
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

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
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
    bool RunNow = false);

public sealed record WarmupRunNowRequestDto(
    IReadOnlyCollection<string>? Tickers = null,
    string? Reason = null);

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
    string? Error);
