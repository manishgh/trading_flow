using System.Net.Http.Json;

namespace TradingFlow.Mobile.Services;

public sealed class TradingFlowApiClient
{
    public const string PhysicalDeviceDefaultUrl = "http://192.168.178.238:53017";
    public const string AndroidEmulatorDefaultUrl = "http://10.0.2.2:53017";

    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public string BaseUrl
    {
        get => Preferences.Get("TradingFlowBackendUrl", PhysicalDeviceDefaultUrl).TrimEnd('/');
        set => Preferences.Set("TradingFlowBackendUrl", value.Trim().TrimEnd('/'));
    }

    public void UsePhysicalDeviceDefault()
    {
        BaseUrl = PhysicalDeviceDefaultUrl;
    }

    public void UseAndroidEmulatorDefault()
    {
        BaseUrl = AndroidEmulatorDefaultUrl;
    }

    public void NormalizeBackendUrlForDevice()
    {
        var current = BaseUrl;
        if (DeviceInfo.DeviceType != DeviceType.Virtual &&
            (current.Contains("10.0.2.2", StringComparison.OrdinalIgnoreCase) ||
             current.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
             current.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)))
        {
            BaseUrl = PhysicalDeviceDefaultUrl;
        }
    }

    public Task<MobileCatalogResponse?> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        return httpClient.GetFromJsonAsync<MobileCatalogResponse>($"{BaseUrl}/api/mobile/catalog", cancellationToken);
    }

    public Task<IReadOnlyList<BacktestJobSnapshot>?> GetPaperJobsAsync(CancellationToken cancellationToken = default)
    {
        return httpClient.GetFromJsonAsync<IReadOnlyList<BacktestJobSnapshot>>($"{BaseUrl}/api/mobile/paper/jobs", cancellationToken);
    }

    public Task<IReadOnlyList<MobileAutomationSessionSnapshot>?> GetAutomationSessionsAsync(CancellationToken cancellationToken = default)
    {
        return httpClient.GetFromJsonAsync<IReadOnlyList<MobileAutomationSessionSnapshot>>($"{BaseUrl}/api/mobile/automation/sessions", cancellationToken);
    }

    public Task<MobileAutomationSessionSnapshot?> GetAutomationSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return httpClient.GetFromJsonAsync<MobileAutomationSessionSnapshot>($"{BaseUrl}/api/mobile/automation/sessions/{sessionId}", cancellationToken);
    }

    public Task<BacktestJobSnapshot?> GetPaperJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        return httpClient.GetFromJsonAsync<BacktestJobSnapshot>($"{BaseUrl}/api/mobile/paper/jobs/{jobId}", cancellationToken);
    }

    public async Task<BacktestJobSnapshot?> StartPaperRunAsync(MobilePaperRunRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"{BaseUrl}/api/mobile/paper/runs", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<BacktestJobSnapshot>(cancellationToken);
    }

    public async Task CancelPaperJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync($"{BaseUrl}/api/mobile/paper/jobs/{jobId}/cancel", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task CancelBrokerOrdersAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync($"{BaseUrl}/api/mobile/paper/jobs/{jobId}/cancel-broker-orders", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<MobileAutomationSessionSnapshot?> StartAutomationEntryAsync(MobileAutomationStartRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"{BaseUrl}/api/mobile/automation/entry", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<MobileAutomationSessionSnapshot>(cancellationToken);
    }

    public async Task CancelAutomationSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync($"{BaseUrl}/api/mobile/automation/sessions/{sessionId}/cancel", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<IReadOnlyList<BacktestJobSnapshot>?> GetBacktestJobsAsync(CancellationToken cancellationToken = default)
    {
        return httpClient.GetFromJsonAsync<IReadOnlyList<BacktestJobSnapshot>>($"{BaseUrl}/api/mobile/backtests/jobs", cancellationToken);
    }

    public async Task<BacktestJobSnapshot?> StartBacktestRunAsync(MobileBacktestRunRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"{BaseUrl}/api/mobile/backtests/runs", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<BacktestJobSnapshot>(cancellationToken);
    }

    public async Task CancelBacktestJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync($"{BaseUrl}/api/mobile/backtests/jobs/{jobId}/cancel", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<IReadOnlyList<MobileNotificationItem>?> GetNotificationsAsync(CancellationToken cancellationToken = default)
    {
        return httpClient.GetFromJsonAsync<IReadOnlyList<MobileNotificationItem>>($"{BaseUrl}/api/mobile/notifications", cancellationToken);
    }

    public Task<MobileNewsFeedResponse?> GetNewsFeedAsync(
        string configPath,
        IReadOnlyList<string> tickers,
        int hours = 24,
        CancellationToken cancellationToken = default)
    {
        var tickerCsv = Uri.EscapeDataString(String.Join(",", tickers));
        var path = Uri.EscapeDataString(configPath);
        return httpClient.GetFromJsonAsync<MobileNewsFeedResponse>(
            $"{BaseUrl}/api/mobile/news/latest?configPath={path}&tickers={tickerCsv}&hours={hours}",
            cancellationToken);
    }

    public Task<IReadOnlyList<WarmupTickerIntent>?> GetWarmupWatchlistAsync(CancellationToken cancellationToken = default)
    {
        return httpClient.GetFromJsonAsync<IReadOnlyList<WarmupTickerIntent>>($"{BaseUrl}/api/mobile/warmup/watchlist", cancellationToken);
    }

    public Task<IReadOnlyList<WarmupRunRecord>?> GetWarmupRunsAsync(CancellationToken cancellationToken = default)
    {
        return httpClient.GetFromJsonAsync<IReadOnlyList<WarmupRunRecord>>($"{BaseUrl}/api/mobile/warmup/runs", cancellationToken);
    }

    public async Task<WarmupAccepted?> AddWarmupWatchlistAsync(WarmupWatchRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"{BaseUrl}/api/mobile/warmup/watchlist", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WarmupAccepted>(cancellationToken);
    }

    public async Task<WarmupRunQueued?> RunWarmupNowAsync(WarmupRunNowRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"{BaseUrl}/api/mobile/warmup/run-now", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WarmupRunQueued>(cancellationToken);
    }

    public async Task RemoveWarmupTickerAsync(string ticker, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.DeleteAsync($"{BaseUrl}/api/mobile/warmup/watchlist/{Uri.EscapeDataString(ticker)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync($"{BaseUrl}/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(String.IsNullOrWhiteSpace(body)
            ? $"TradingFlow API returned {(int)response.StatusCode}."
            : body);
    }
}

public sealed record MobileCatalogResponse(
    IReadOnlyList<MobileRunConfigOption> BacktestConfigs,
    IReadOnlyList<MobileRunConfigOption> PaperConfigs,
    IReadOnlyList<MobileStrategyOption> Strategies);

public sealed record MobileRunConfigOption(
    string Path,
    string FileName,
    string RunName,
    string Mode,
    string Provider,
    IReadOnlyList<string> Tickers,
    IReadOnlyList<string> Intervals)
{
    public override string ToString() => FileName;
}

public sealed record MobileStrategyOption(
    string Path,
    string FileName,
    string StrategyId,
    string StrategyName,
    int Version,
    string Direction,
    string Timeframe,
    string ExecutionTimeframe,
    string SetupType,
    bool UsesNews)
{
    public override string ToString() => $"{StrategyName} ({Timeframe}->{ExecutionTimeframe})";
}

public sealed record MobilePaperRunRequest(
    string BaseConfigPath,
    string StrategyPath,
    string RunName,
    IReadOnlyList<string> Tickers,
    string? ScreenerFilter,
    bool ExtendedHours,
    bool NewsEnabled,
    string OrderExpiration,
    string EntryOrderType);

public sealed record MobileBacktestRunRequest(
    string BaseConfigPath,
    string RunName,
    int LookbackDays,
    IReadOnlyList<string> Tickers,
    IReadOnlyList<string> StrategyPaths,
    decimal StartingCapital,
    decimal RiskPerTradePct,
    decimal MaxPositionValuePct,
    int MaxConcurrentPositions,
    string CachePolicy);

public sealed record BacktestJobSnapshot(
    Guid JobId,
    string RunName,
    string ConfigPath,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorMessage,
    string CurrentStage,
    int CompletedTickerCount,
    int TotalTickerCount,
    IReadOnlyList<string> Events,
    BacktestResultSnapshot? Result,
    IReadOnlyList<BacktestStrategyRunGroup>? StrategyGroups = null)
{
    public string ProgressText => TotalTickerCount <= 0
        ? CurrentStage
        : $"{CurrentStage} - {CompletedTickerCount}/{TotalTickerCount}";

    public string LatestEvent => Events.LastOrDefault() ?? "No events yet.";
    public string ResultSummary => Result is null
        ? ErrorMessage ?? LatestEvent
        : $"P/L {Result.NetProfit:C2}, return {Result.TotalReturnPct:F2}%, daily {Result.AverageDailyReturnPct:F2}%, DD {Result.MaxDrawdownPct:F2}%, trades {Result.AcceptedTradeCount}/{Result.CandidateTradeCount}";
}

public sealed record BacktestResultSnapshot(
    string? RunName,
    string? ResultPath,
    decimal StartingCapital,
    decimal EndingCapital,
    decimal NetProfit,
    decimal TotalReturnPct,
    decimal AverageDailyReturnPct,
    int TradingDayCount,
    decimal MaxDrawdownPct,
    WinnerStrategySummary? Winner,
    int ProcessedBarCount,
    int CandidateTradeCount,
    int AcceptedTradeCount,
    int RejectedTradeCount,
    int WinningTradeCount,
    int LosingTradeCount);

public sealed record WinnerStrategySummary(
    string StrategyId,
    string StrategyName,
    decimal EndingCapital,
    decimal NetProfit,
    decimal TotalReturnPct,
    decimal AverageDailyReturnPct,
    decimal MaxDrawdownPct,
    int AcceptedTradeCount,
    int RejectedTradeCount);

public sealed record BacktestStrategyRunGroup(
    string StrategyName,
    string Status,
    int CompletedTickerCount,
    int TotalTickerCount,
    string? CurrentTicker,
    IReadOnlyList<string> RecentEvents);

public sealed record MobileNotificationItem(
    DateTimeOffset Timestamp,
    string Severity,
    string Title,
    string Message,
    string? RunName,
    Guid? JobId);

public sealed record MobileNewsFeedResponse(
    bool Enabled,
    string Provider,
    string? Message,
    IReadOnlyList<MobileNewsItem> Items);

public sealed record MobileNewsItem(
    string Ticker,
    DateTimeOffset Timestamp,
    string Headline,
    decimal SentimentScore,
    string? Provider,
    string? Source,
    string? Url,
    string? Summary)
{
    public string SentimentText => SentimentScore >= 0.15m
        ? $"Bullish {SentimentScore:F2}"
        : SentimentScore <= -0.15m
            ? $"Bearish {SentimentScore:F2}"
            : $"Neutral {SentimentScore:F2}";
}

public sealed record WarmupWatchRequest(
    IReadOnlyCollection<string> Tickers,
    string? Reason,
    string? RequestedBy,
    int? WarmupDays,
    int? NewsLookbackDays,
    IReadOnlyCollection<string>? Timeframes,
    bool? IncludeNews,
    bool RunNow);

public sealed record WarmupRunNowRequest(
    IReadOnlyCollection<string>? Tickers,
    string? Reason);

public sealed record WarmupAccepted(
    int Accepted,
    IReadOnlyList<string> Tickers,
    bool RunQueued);

public sealed record WarmupRunQueued(
    string RunId,
    bool Queued);

public sealed record WarmupTickerIntent(
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
    string? LastError)
{
    public string DetailText => $"{LastStatus} | {WarmupDays}d | {(IncludeNews ? $"news {NewsLookbackDays}d" : "news off")} | {String.Join(",", Timeframes)}";
    public string LastWarmText => LastWarmedAtUtc is null ? "Not warmed yet" : $"Last warmed {LastWarmedAtUtc.Value.LocalDateTime:MMM d HH:mm}";
}

public sealed record WarmupRunRecord(
    string RunId,
    string Reason,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string Status,
    int RequestedTickerCount,
    int SucceededTickerCount,
    int FailedTickerCount,
    IReadOnlyList<WarmupTickerResult> Tickers)
{
    public string SummaryText => $"{Status}: {SucceededTickerCount}/{RequestedTickerCount} succeeded, {FailedTickerCount} failed";
    public string StartedText => StartedAtUtc.LocalDateTime.ToString("MMM d HH:mm");
}

public sealed record WarmupTickerResult(
    string Ticker,
    bool Succeeded,
    int BarCount,
    int CatalystCount,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    IReadOnlyList<string> Timeframes,
    IReadOnlyList<string> ArtifactPaths,
    string? Error);

public sealed record MobileAutomationStartRequest(
    string ConfigPath,
    string StrategyPath,
    string Ticker,
    string RunName,
    string Source,
    string? SourcePackage,
    string? SourceTitle,
    string? SourceMessage);

public sealed record MobileAutomationSessionSnapshot(
    Guid SessionId,
    string RunName,
    string ConfigPath,
    string StrategyPath,
    string Ticker,
    string Source,
    string? SourcePackage,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? EntrySubmittedAt,
    DateTimeOffset? ExitSubmittedAt,
    string? ErrorMessage,
    string CurrentStage,
    string? EntryOrderId,
    decimal? EntryPrice,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    int? ShareQuantity,
    decimal? LastObservedPrice,
    decimal? UnrealizedPl,
    string? ExitReason,
    IReadOnlyList<string> Events,
    string? SourceTitle,
    string? SourceMessage)
{
    public string ProgressText => $"{Ticker} {Status} - {CurrentStage}";
    public string LatestEvent => Events.LastOrDefault() ?? "No events yet.";
}
