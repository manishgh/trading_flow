using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TradingFlow.Mobile.Services;

public sealed class TradingFlowApiClient
{
    public const string NgrokDefaultUrl = "https://875a-2001-1c00-820b-7600-d54-b611-ef7-128f.ngrok-free.app";
    public const string PhysicalDeviceDefaultUrl = "http://192.168.178.238:53017";
    public const string AndroidEmulatorDefaultUrl = "http://10.0.2.2:53017";

    private static readonly JsonSerializerOptions StreamJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient = CreateHttpClient(TimeSpan.FromSeconds(45));
    // Server-sent-event streams are long-lived, so they need an unbounded timeout and
    // rely on the caller's CancellationToken to stop instead of the request timeout.
    private readonly HttpClient streamClient = CreateHttpClient(Timeout.InfiniteTimeSpan);

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient
        {
            Timeout = timeout
        };

        // Free ngrok tunnels may return a browser warning page unless API clients send this header.
        client.DefaultRequestHeaders.TryAddWithoutValidation("ngrok-skip-browser-warning", "true");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "TradingFlow.Mobile/1.0");
        return client;
    }

    public string BaseUrl
    {
        get => Preferences.Get("TradingFlowBackendUrl", NgrokDefaultUrl).TrimEnd('/');
        set => Preferences.Set("TradingFlowBackendUrl", value.Trim().TrimEnd('/'));
    }

    public void UseNgrokDefault()
    {
        BaseUrl = NgrokDefaultUrl;
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
        if (IsLocalOrOldDefault(current))
        {
            BaseUrl = NgrokDefaultUrl;
        }
    }

    private static bool IsLocalOrOldDefault(string current)
    {
        return current.Contains("10.0.2.2", StringComparison.OrdinalIgnoreCase) ||
            current.Contains("10.0.0.2", StringComparison.OrdinalIgnoreCase) ||
            current.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            current.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            current.Contains("192.168.178.238", StringComparison.OrdinalIgnoreCase) ||
            (current.Contains(".ngrok-free.app", StringComparison.OrdinalIgnoreCase) &&
                !current.Equals(NgrokDefaultUrl, StringComparison.OrdinalIgnoreCase));
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

    public Task<MobileNewsFeedResponse?> GetRollingNewsFeedAsync(
        string? ticker = null,
        int hours = 4,
        CancellationToken cancellationToken = default)
    {
        var query = $"hours={hours}";
        if (!String.IsNullOrWhiteSpace(ticker))
        {
            query += $"&ticker={Uri.EscapeDataString(ticker.Trim().ToUpperInvariant())}";
        }

        return httpClient.GetFromJsonAsync<MobileNewsFeedResponse>(
            $"{BaseUrl}/api/mobile/news/feed?{query}",
            cancellationToken);
    }

    public async Task RefreshRollingNewsFeedAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync($"{BaseUrl}/api/mobile/news/refresh", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<IReadOnlyList<MobileWishlistResponse>?> GetWishlistsAsync(CancellationToken cancellationToken = default)
    {
        return httpClient.GetFromJsonAsync<IReadOnlyList<MobileWishlistResponse>>($"{BaseUrl}/api/mobile/wishlists", cancellationToken);
    }

    public async Task<MobileWishlistResponse?> SaveWishlistAsync(MobileWishlistSaveRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"{BaseUrl}/api/mobile/wishlists", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<MobileWishlistResponse>(cancellationToken);
    }

    public async Task<MobileWishlistResponse?> SetWishlistObservedAsync(Guid wishlistId, bool isObserved, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"{BaseUrl}/api/mobile/wishlists/{wishlistId}/observe",
            new MobileWishlistObserveRequest(isObserved),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<MobileWishlistResponse>(cancellationToken);
    }

    public async Task<MobileWishlistItemResponse?> AddWishlistTickerAsync(Guid wishlistId, MobileWishlistItemRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"{BaseUrl}/api/mobile/wishlists/{wishlistId}/items", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<MobileWishlistItemResponse>(cancellationToken);
    }

    public async Task DeleteWishlistTickerAsync(Guid wishlistId, string ticker, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.DeleteAsync($"{BaseUrl}/api/mobile/wishlists/{wishlistId}/items/{Uri.EscapeDataString(ticker)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<IReadOnlyList<MobileWishlistSignalResponse>?> GetWishlistSignalsAsync(Guid? wishlistId = null, int hours = 48, CancellationToken cancellationToken = default)
    {
        var query = $"hours={hours}";
        if (wishlistId is not null)
        {
            query += $"&wishlistId={wishlistId.Value}";
        }

        return httpClient.GetFromJsonAsync<IReadOnlyList<MobileWishlistSignalResponse>>(
            $"{BaseUrl}/api/mobile/wishlists/signals?{query}",
            cancellationToken);
    }

    public Task<MobileWishlistDeskResponse?> GetWishlistDeskAsync(
        Guid wishlistId,
        int signalMinutes = 20,
        int newsHours = 4,
        CancellationToken cancellationToken = default)
    {
        var query = $"signalMinutes={signalMinutes}&newsHours={newsHours}";
        return httpClient.GetFromJsonAsync<MobileWishlistDeskResponse>(
            $"{BaseUrl}/api/mobile/wishlists/{wishlistId}/desk?{query}",
            cancellationToken);
    }

    public Task<MobileSymbolIntelligenceResponse?> GetSymbolIntelligenceAsync(
        Guid wishlistId,
        string ticker,
        string mode = "unified",
        string horizon = "auto",
        CancellationToken cancellationToken = default)
    {
        var symbol = Uri.EscapeDataString(ticker.Trim().ToUpperInvariant());
        var query = $"mode={Uri.EscapeDataString(mode)}&horizon={Uri.EscapeDataString(horizon)}";
        return httpClient.GetFromJsonAsync<MobileSymbolIntelligenceResponse>(
            $"{BaseUrl}/api/mobile/wishlists/{wishlistId}/symbols/{symbol}/intelligence?{query}",
            cancellationToken);
    }

    public async Task<MobileOrderTicketPreview?> PreviewManualOrderAsync(
        MobileOrderPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"{BaseUrl}/api/mobile/orders/preview", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<MobileOrderTicketPreview>(cancellationToken);
    }

    public async Task<MobileOrderTicketConfirmation?> ConfirmManualOrderAsync(
        string ticketToken,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"{BaseUrl}/api/mobile/orders/confirm",
            new MobileOrderConfirmRequest(ticketToken),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<MobileOrderTicketConfirmation>(cancellationToken);
    }

    public async Task AcknowledgeWishlistSignalAsync(Guid signalId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync($"{BaseUrl}/api/mobile/wishlists/signals/{signalId}/ack", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<IReadOnlyList<MobilePaperPositionResponse>?> GetPaperPositionsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        return httpClient.GetFromJsonAsync<IReadOnlyList<MobilePaperPositionResponse>>(
            $"{BaseUrl}/api/mobile/paper/jobs/{jobId}/positions",
            cancellationToken);
    }

    public Task<MobileRunningTradesResponse?> GetRunningTradesAsync(string source = "all", CancellationToken cancellationToken = default)
    {
        return httpClient.GetFromJsonAsync<MobileRunningTradesResponse>(
            $"{BaseUrl}/api/mobile/running-trades?source={Uri.EscapeDataString(source)}",
            cancellationToken);
    }

    public async Task ClosePaperPositionAsync(Guid jobId, string ticker, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync(
            $"{BaseUrl}/api/mobile/paper/jobs/{jobId}/positions/{Uri.EscapeDataString(ticker)}/close",
            null,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task CloseAutomationPositionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync(
            $"{BaseUrl}/api/mobile/automation/sessions/{sessionId}/close",
            null,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    // Subscribes to the wishlist live-quote SSE stream. onQuotes is invoked once per stream
    // tick with the latest bid/ask/mid for every active ticker in the wishlist. The task runs
    // until cancellationToken is cancelled or the stream faults (the caller handles reconnect).
    public Task StreamWishlistQuotesAsync(
        Guid wishlistId,
        Func<IReadOnlyList<WishlistQuoteUpdate>, Task> onQuotes,
        CancellationToken cancellationToken)
    {
        return StreamServerSentEventsAsync(
            $"{BaseUrl}/api/wishlists/{wishlistId}/quotes/stream",
            async (eventName, data) =>
            {
                if (!eventName.Equals("quotes", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var updates = JsonSerializer.Deserialize<IReadOnlyList<WishlistQuoteUpdate>>(data, StreamJsonOptions);
                if (updates is { Count: > 0 })
                {
                    await onQuotes(updates);
                }
            },
            cancellationToken);
    }

    // Subscribes to the wishlist activity SSE stream (last-20-minute signals + rolling-4h news
    // already scoped to the wishlist's tickers).
    public Task StreamWishlistActivityAsync(
        Guid wishlistId,
        Func<WishlistActivityUpdate, Task> onActivity,
        CancellationToken cancellationToken)
    {
        return StreamServerSentEventsAsync(
            $"{BaseUrl}/api/wishlists/{wishlistId}/activity/stream",
            async (eventName, data) =>
            {
                if (!eventName.Equals("activity", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var update = JsonSerializer.Deserialize<WishlistActivityUpdate>(data, StreamJsonOptions);
                if (update is not null)
                {
                    await onActivity(update);
                }
            },
            cancellationToken);
    }

    private async Task StreamServerSentEventsAsync(
        string url,
        Func<string, string, Task> onEvent,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

        using var response = await streamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var eventName = "message";
        var dataBuilder = new StringBuilder();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break; // Stream closed by the server.
            }

            if (line.Length == 0)
            {
                // Blank line terminates an event: dispatch what we have buffered.
                if (dataBuilder.Length > 0)
                {
                    await onEvent(eventName, dataBuilder.ToString());
                }

                eventName = "message";
                dataBuilder.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (dataBuilder.Length > 0)
                {
                    dataBuilder.Append('\n');
                }

                dataBuilder.Append(line["data:".Length..].TrimStart());
            }
            // Other SSE fields (id:, retry:) and comment lines (starting with ':') are ignored.
        }
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
        if (body.Length > 700)
        {
            body = body[..700] + "...";
        }

        if (body.Contains("ngrok", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("browser", StringComparison.OrdinalIgnoreCase))
        {
            body = "Ngrok returned its browser warning page instead of the TradingFlow API. Re-test Settings -> Backend URL or restart the ngrok tunnel.";
        }

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
    public override string ToString()
    {
        var mode = string.IsNullOrWhiteSpace(Mode) ? "run" : Mode;
        var provider = string.IsNullOrWhiteSpace(Provider) ? "provider" : Provider;
        var intervals = Intervals.Count == 0 ? "auto" : string.Join(",", Intervals);
        return $"{mode} / {provider} / {intervals}";
    }
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
    bool UsesNews,
    decimal? LastAuditedReturnPct = null,
    decimal? LastAuditedMaxDrawdownPct = null,
    int? LastAuditedTrades = null,
    decimal? LastAuditedWinRatePct = null,
    string? LastAuditedAverageHold = null,
    string? LastAuditedResultPath = null)
{
    public override string ToString()
    {
        var audit = LastAuditedReturnPct is null
            ? "not audited"
            : $"{LastAuditedReturnPct:0.##}% / DD {LastAuditedMaxDrawdownPct:0.##}% / {LastAuditedAverageHold}";
        return $"{StrategyName} ({Timeframe}->{ExecutionTimeframe}) - {audit}";
    }
}

public sealed record MobilePaperRunRequest(
    string BaseConfigPath,
    string StrategyPath,
    string RunName,
    IReadOnlyList<string> Tickers,
    string? ScreenerFilter,
    bool AllowExtendedHoursTrading,
    bool NewsEnabled,
    string OrderExpiration,
    string EntryOrderType,
    Guid? WishlistId = null);

public sealed record MobileBacktestRunRequest(
    string BaseConfigPath,
    string RunName,
    int LookbackDays,
    Guid? WishlistId,
    IReadOnlyList<string> StrategyPaths,
    decimal StartingCapital,
    decimal AccountRiskBudgetPct,
    decimal MaxPositionNotionalPct,
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

    public string DisplayHeadline => FirstUsefulText(Headline, Summary, Source, Provider) ?? "News update";

    public string DisplaySummary => String.Equals(Summary?.Trim(), DisplayHeadline, StringComparison.OrdinalIgnoreCase)
        ? String.Empty
        : Summary?.Trim() ?? String.Empty;

    public bool HasSummary => !String.IsNullOrWhiteSpace(DisplaySummary);

    public string DisplayTimestamp => Timestamp.LocalDateTime.ToString("dd/MM HH:mm");

    public string DisplayProvider => String.IsNullOrWhiteSpace(Provider) ? "news" : Provider.Trim();

    public string DisplaySource => String.IsNullOrWhiteSpace(Source) ? DisplayProvider : Source.Trim();

    private static string? FirstUsefulText(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = value?.Trim();
            if (String.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (text.Equals("Market", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("Stock", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("News", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return text;
        }

        return null;
    }
}

public sealed record MobileWishlistResponse(
    Guid Id,
    string Name,
    string? Description,
    bool IsDefault,
    bool IncludeExtendedHours,
    bool IsObserved,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<MobileWishlistItemResponse> Items)
{
    public override string ToString() => $"{Name} ({ActiveItemCount})";

    public int ActiveItemCount => Items.Count(item => item.Active);

    public string DetailText => $"{ActiveItemCount} active ticker(s) | {(IncludeExtendedHours ? "extended hours" : "regular hours")} | {(IsObserved ? "observing" : "paused")}";
}

public sealed record MobileWishlistItemResponse(
    Guid Id,
    Guid WishlistId,
    string Ticker,
    string? DisplayName,
    string? Notes,
    bool Active,
    DateTimeOffset AddedAtUtc)
{
    public string DisplayTitle => String.IsNullOrWhiteSpace(DisplayName) ? Ticker : $"{Ticker} - {DisplayName}";

    public string DetailText => String.IsNullOrWhiteSpace(Notes) ? "Ready for paper runs and breakout alerts." : Notes!;
}

public sealed record MobileWishlistSignalResponse(
    Guid Id,
    Guid WishlistId,
    string Ticker,
    string SignalType,
    string Severity,
    DateTimeOffset DetectedAtUtc,
    decimal Price,
    string Reason,
    string SnapshotJson,
    string? NewsHeadline,
    string? NewsUrl,
    string? NewsProvider,
    bool Acknowledged)
{
    public string DisplayTime => DetectedAtUtc.LocalDateTime.ToString("dd/MM HH:mm");

    public string PriceText => Price <= 0 ? String.Empty : Price.ToString("C2");

    public string NewsText => String.IsNullOrWhiteSpace(NewsHeadline)
        ? "No matched news"
        : NewsHeadline.Trim();

    public bool HasNewsLink => !String.IsNullOrWhiteSpace(NewsUrl);

    public string StatusText => Acknowledged ? "read" : Severity;
}

public sealed record MobileWishlistDeskResponse(
    MobileWishlistResponse Wishlist,
    IReadOnlyList<MobileWishlistDeskRowResponse> Rows,
    IReadOnlyList<MobileWishlistSignalResponse> RecentSignals,
    IReadOnlyList<MobileNewsItem> RelatedNews,
    IReadOnlyList<MobileRunningTrade> RunningTrades,
    decimal TotalUnrealizedPl)
{
    public string TotalText => $"Open P/L {(TotalUnrealizedPl >= 0 ? "+" : String.Empty)}{TotalUnrealizedPl:C2}";
}

public sealed record MobileWishlistDeskRowResponse(
    MobileWishlistItemResponse Item,
    string Ticker,
    string DisplayName,
    decimal? BidPrice,
    decimal? AskPrice,
    decimal? MidPrice,
    string DisplayBid,
    string DisplayAsk,
    string DisplayPrice,
    string BuyCaption,
    string SellCaption,
    DateTimeOffset? QuoteTimestamp,
    bool HasQuote,
    bool HasTrade,
    bool HasSignal,
    bool HasNews,
    string EligibilityLabel,
    string EligibilityReason,
    MobileWishlistSignalResponse? LatestSignal,
    MobileNewsItem? LatestNews,
    MobileRunningTrade? Trade)
{
    public string StatusText => HasTrade ? "In trade" : HasSignal ? "Eligible" : "Watching";

    public string DetailText => HasTrade && Trade is not null
        ? Trade.PlText
        : HasSignal
        ? EligibilityReason
        : HasNews && LatestNews is not null
            ? LatestNews.DisplayHeadline
            : Item.DetailText;
}

public sealed record MobileSymbolIntelligenceResponse(
    string Ticker,
    DateTimeOffset FetchedAtUtc,
    MobileTradingFlowDecisionResponse TradingFlowDecision,
    MobileModelIntelligenceResponse ModelIntelligence);

public sealed record MobileOrderPreviewRequest(
    string Ticker,
    decimal Quantity,
    decimal LimitPrice,
    decimal StopLossPrice,
    decimal TakeProfitPrice,
    string Horizon,
    bool AllowExtendedHoursTrading);

public sealed record MobileOrderConfirmRequest(string TicketToken);

public sealed record MobileOrderTicketPreview(
    bool CanSubmit,
    string? TicketToken,
    Guid TicketId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Environment,
    string Ticker,
    string Side,
    decimal Quantity,
    decimal LimitPrice,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    decimal Notional,
    decimal? BidPrice,
    decimal? AskPrice,
    DateTimeOffset? QuoteTimestampUtc,
    long? QuoteAgeMilliseconds,
    decimal? SpreadBps,
    string Session,
    string TimeInForce,
    bool AllowExtendedHoursTrading,
    string Policy,
    IReadOnlyList<string> Rejections);

public sealed record MobileOrderTicketConfirmation(
    string OrderId,
    Guid TicketId,
    string Ticker,
    string Side,
    decimal Quantity,
    decimal LimitPrice);

public sealed record MobileTradingFlowDecisionResponse(
    string EligibilityLabel,
    string EligibilityReason,
    bool HasOpenTrade,
    MobileRunningTrade? Trade,
    MobileWishlistSignalResponse? LatestSignal,
    decimal? BidPrice,
    decimal? AskPrice,
    decimal? MidPrice,
    DateTimeOffset? QuoteTimestamp)
{
    public string QuoteText => $"Bid {Format(BidPrice)} | Mid {Format(MidPrice)} | Ask {Format(AskPrice)}";
    public string QuoteAgeText => QuoteTimestamp is null
        ? "Quote timestamp unavailable"
        : $"Quote {QuoteTimestamp.Value.LocalDateTime:dd/MM HH:mm:ss}";

    private static string Format(decimal? value) => value.HasValue ? value.Value.ToString("C2") : "--";
}

public sealed record MobileModelIntelligenceResponse(
    string ContractVersion,
    string AvailabilityStatus,
    string? AvailabilityReason,
    bool IsValidPromotedEvidence,
    string Mode,
    string RequestedHorizon,
    string ResolvedHorizon,
    string FinalSignal,
    string ReadinessStatus,
    IReadOnlyList<string> ReadinessReasons,
    string? RequestId,
    string? SnapshotId,
    DateTimeOffset? GeneratedAtUtc,
    string? ModelStatus,
    string? ModelType,
    string? ModelSchemaVersion,
    string? ModelTarget,
    string? ModelArtifactSha256,
    string? ModelTrainingDataEnd,
    MobileSwingIntelligenceResponse? Swing,
    MobileIntradayIntelligenceResponse? Intraday)
{
    public string StatusText => AvailabilityStatus == "available"
        ? $"{ReadinessStatus} | {FinalSignal} | {ResolvedHorizon}"
        : $"{AvailabilityStatus} | {AvailabilityReason}";
    public string GeneratedText => GeneratedAtUtc is null
        ? "No prediction timestamp"
        : $"Generated {GeneratedAtUtc.Value:dd/MM HH:mm:ss} UTC";
    public string ModelText => $"Model {ModelStatus ?? "unknown"} | {ModelType ?? "type unavailable"}";
}

public sealed record MobileSwingIntelligenceResponse(
    decimal? Probability,
    decimal? DecisionScore,
    string Signal,
    int? Rank,
    decimal? Return1D,
    decimal? VolumeZ20,
    MobileCatalystIntelligenceResponse Catalyst,
    decimal GlobalContextImpact,
    IReadOnlyList<string> ActiveFlashpoints,
    string ReadinessStatus,
    IReadOnlyList<string> ReadinessReasons,
    string? LatestPriceDate,
    string PriceFeed)
{
    public string ProbabilityText => $"Probability {FormatPercent(Probability)} | score {Format(DecisionScore)}";
    public string ContextText => $"Catalyst {Catalyst.Status}/{Catalyst.Direction} | context {GlobalContextImpact:0.00}";

    private static string FormatPercent(decimal? value) => value.HasValue ? value.Value.ToString("P1") : "--";
    private static string Format(decimal? value) => value.HasValue ? value.Value.ToString("0.00") : "--";
}

public sealed record MobileIntradayIntelligenceResponse(
    decimal? OpportunityProbability,
    decimal? DownsideProbability,
    decimal? DecisionScore,
    string Signal,
    int? Rank,
    decimal? RelativeVolume,
    decimal? Rsi14,
    decimal? MacdSignalDiff,
    decimal? EntryStopPct,
    decimal? EntryTargetPct,
    MobileCatalystIntelligenceResponse Catalyst,
    string ReadinessStatus,
    IReadOnlyList<string> ReadinessReasons,
    string? LatestPriceDate,
    string PriceFeed)
{
    public string ProbabilityText => $"Opportunity {FormatPercent(OpportunityProbability)} | downside {FormatPercent(DownsideProbability)}";
    public string TechnicalText => $"RVOL {Format(RelativeVolume)} | RSI {Format(Rsi14)} | MACD {Format(MacdSignalDiff, "0.0000")}";

    private static string FormatPercent(decimal? value) => value.HasValue ? value.Value.ToString("P1") : "--";
    private static string Format(decimal? value, string pattern = "0.00") => value.HasValue ? value.Value.ToString(pattern) : "--";
}

public sealed record MobileCatalystIntelligenceResponse(
    string Status,
    string Direction,
    decimal Score,
    int EventCount,
    decimal Relevance,
    decimal? MinutesSinceLatest,
    IReadOnlyList<string> Reasons);

public sealed record MobileWishlistSaveRequest(
    Guid? Id,
    string Name,
    string? Description,
    bool? IsDefault,
    bool? IncludeExtendedHours,
    bool? IsObserved);

public sealed record MobileWishlistObserveRequest(bool IsObserved);

public sealed record MobilePaperPositionResponse(
    string Ticker,
    string Side,
    decimal Qty,
    decimal EntryPrice,
    decimal CurrentPrice,
    decimal UnrealizedPl)
{
    public string HeaderText => $"{Ticker} - {Side} x{Qty:0.####}";

    public string DetailText => $"Entry {EntryPrice:C2} | Last {CurrentPrice:C2}";

    public string PnlText => $"{(UnrealizedPl >= 0 ? "+" : String.Empty)}{UnrealizedPl:C2}";

    public bool IsProfit => UnrealizedPl >= 0;
}

public sealed record MobileRunningTradesResponse(
    IReadOnlyList<MobileRunningTrade> Trades,
    decimal TotalUnrealizedPl,
    int Count)
{
    public string TotalText => $"Total P/L {(TotalUnrealizedPl >= 0 ? "+" : String.Empty)}{TotalUnrealizedPl:C2}";

    public bool IsTotalProfit => TotalUnrealizedPl >= 0;
}

public sealed record MobileRunningTrade(
    string Source,
    string Ticker,
    decimal Quantity,
    decimal EntryPrice,
    decimal CurrentPrice,
    decimal UnrealizedPl,
    decimal UnrealizedPlPct,
    string Status,
    string Reference,
    Guid? JobId,
    Guid? SessionId,
    string CloseKind,
    string StrategyName,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    string? ExitReason,
    DateTimeOffset? UpdatedAtUtc,
    string ProtectionSummary)
{
    public string SourceLabel => Source switch
    {
        "wishlist" => "Wishlist",
        "stockpulse" => "Stock Pulse",
        "manual" => "Manual",
        _ => Source
    };

    public bool IsProfit => UnrealizedPl >= 0;

    public string HeaderText => $"{Ticker}  x{Quantity:0.####}";

    public string PriceText => $"Entry {EntryPrice:C2} -> {CurrentPrice:C2}";

    public string PlText => $"{(UnrealizedPl >= 0 ? "+" : String.Empty)}{UnrealizedPl:C2} ({UnrealizedPlPct:0.00}%)";

    public string DetailText => $"{SourceLabel} · {Reference}";
}

// Payload of the /api/wishlists/{id}/quotes/stream SSE feed (one per active ticker).
public sealed record WishlistQuoteUpdate(
    string Ticker,
    decimal? BidPrice,
    decimal? AskPrice,
    decimal? MidPrice,
    string? BidText,
    string? AskText,
    string? MidText,
    string? BuyCaption,
    string? SellCaption,
    DateTimeOffset? Timestamp);

// Payload of the /api/wishlists/{id}/activity/stream SSE feed.
public sealed record WishlistActivityUpdate(
    IReadOnlyList<WishlistActivitySignal>? Signals,
    IReadOnlyList<WishlistActivityNews>? News);

public sealed record WishlistActivitySignal(
    Guid Id,
    string Ticker,
    string SignalType,
    string Reason,
    DateTimeOffset DetectedAt,
    string DetectedAtText);

public sealed record WishlistActivityNews(
    string Ticker,
    string? Headline,
    string? Summary,
    string? Provider,
    string? Source,
    string? Url,
    DateTimeOffset Timestamp,
    string TimestampText)
{
    public string DisplayHeadline => String.IsNullOrWhiteSpace(Headline) ? "News update" : Headline!.Trim();

    public string DisplaySummary => String.IsNullOrWhiteSpace(Summary) ||
        String.Equals(Summary!.Trim(), DisplayHeadline, StringComparison.OrdinalIgnoreCase)
        ? String.Empty
        : Summary!.Trim();

    public bool HasSummary => DisplaySummary.Length > 0;

    public string SourceLine => String.IsNullOrWhiteSpace(Source)
        ? $"{Ticker} | {Provider} | {TimestampText}"
        : $"{Ticker} | {Provider} - {Source} | {TimestampText}";

    public bool HasLink => !String.IsNullOrWhiteSpace(Url);
}

public sealed record MobileWishlistItemRequest(
    string Ticker,
    string? DisplayName,
    string? Notes);

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
    string? SourceMessage,
    string EntryMode = "validate_strategy",
    bool AllowExtendedHoursTrading = false,
    string EntryOrderType = "market");

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
    public string ShortSessionId => SessionId.ToString("N")[..8];
    public string DisplayTitle => $"{Ticker} - {Source} #{ShortSessionId}";
    public string StrategyFileName => Path.GetFileName(StrategyPath);
    public string RunDetail => $"{RunName} | {Status} | {StrategyFileName}";
}
