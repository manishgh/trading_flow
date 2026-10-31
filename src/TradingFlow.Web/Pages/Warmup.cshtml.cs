using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Web.Services;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Web.Pages;

public sealed class WarmupModel(WarmupServiceClient warmupClient) : PageModel
{
    [BindProperty] public string? TickersCsv { get; set; }
    [BindProperty] public string Reason { get; set; } = "possible trend tomorrow";
    [BindProperty] public int WarmupDays { get; set; } = 60;
    [BindProperty] public int NewsLookbackDays { get; set; } = 14;
    [BindProperty] public string TimeframesCsv { get; set; } = "1m,5m,15m,1h,1d";
    [BindProperty] public bool IncludeNews { get; set; } = true;
    [BindProperty] public bool RunNow { get; set; }
    [BindProperty] public string? Ticker { get; set; }
    [BindProperty] public string RunNowCommandKey { get; set; } = Guid.NewGuid().ToString("N");
    [BindProperty] public string AddCommandKey { get; set; } = Guid.NewGuid().ToString("N");

    public IReadOnlyList<WarmupTickerIntentDto> Watchlist { get; private set; } = [];
    public IReadOnlyList<WarmupRunRecordDto> Runs { get; private set; } = [];
    public IReadOnlyList<WarmupQueueStatus> Jobs { get; private set; } = [];
    public string ServiceBaseUrl => warmupClient.BaseUrl;
    public string? StatusMessage { get; private set; }
    public bool HasError { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    /// <summary>Reuses an add-and-run key after an uncertain response and rotates it only after acceptance or an operator edit.</summary>
    public async Task<IActionResult> OnPostAddAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tickers = SplitCsv(TickersCsv).ToArray();
            if (tickers.Length == 0)
            {
                StatusMessage = "Add at least one ticker.";
                HasError = true;
                await LoadAsync(cancellationToken);
                return Page();
            }

            var accepted = await warmupClient.AddWatchlistAsync(
                new WarmupWatchRequestDto(
                    tickers,
                    Reason,
                    "web",
                    WarmupDays,
                    NewsLookbackDays,
                    SplitCsv(TimeframesCsv).ToArray(),
                    IncludeNews,
                    RunNow,
                    AddCommandKey),
                cancellationToken);
            StatusMessage = accepted is null
                ? "Warmup request submitted."
                : $"Accepted {accepted.Accepted} ticker(s). Run queued: {accepted.RunQueued}.";
            AddCommandKey = Guid.NewGuid().ToString("N");
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            HasError = true;
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    /// <summary>Preserves the posted command key on errors; a successful submission renders a fresh key for the next deliberate run.</summary>
    public async Task<IActionResult> OnPostRunNowAsync(CancellationToken cancellationToken)
    {
        try
        {
            var queued = await warmupClient.RunNowAsync(new WarmupRunNowRequestDto(null, "web-run-all", RunNowCommandKey), cancellationToken);
            StatusMessage = queued is null ? "Warmup run queued." : $"Warmup run queued: {queued.RunId}.";
            RunNowCommandKey = Guid.NewGuid().ToString("N");
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            HasError = true;
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!String.IsNullOrWhiteSpace(Ticker))
            {
                await warmupClient.RemoveAsync(Ticker, cancellationToken);
                StatusMessage = $"Removed {Ticker}.";
            }
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            HasError = true;
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    /// <summary>Loads queue state independently of cache completion and archive delivery.</summary>
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Watchlist = await warmupClient.GetWatchlistAsync(cancellationToken) ?? [];
            Runs = await warmupClient.GetRunsAsync(cancellationToken) ?? [];
            Jobs = await warmupClient.GetJobsAsync(cancellationToken) ?? [];
        }
        catch (Exception exception)
        {
            Watchlist = [];
            Runs = [];
            Jobs = [];
            StatusMessage ??= $"Warmup service unavailable: {exception.Message}";
            HasError = true;
        }
    }

    /// <summary>Requests cancellation of the exact accepted warmup job.</summary>
    public async Task<IActionResult> OnPostCancelJobAsync(string runId, CancellationToken token)
    {
        try { await warmupClient.CancelJobAsync(runId, token); StatusMessage = "Warmup cancellation requested."; }
        catch (Exception error) { HasError = true; StatusMessage = error.Message; }
        await LoadAsync(token);
        return Page();
    }

    /// <summary>Retries immutable archive files without starting another provider download.</summary>
    public async Task<IActionResult> OnPostRetryArchivesAsync(string runId, CancellationToken token)
    {
        try
        {
            var result = await warmupClient.RetryArchivesAsync(runId, token);
            StatusMessage = result is null ? "Archive result unavailable." : String.Join("; ", result.Tickers.Select(ticker => $"{ticker.Ticker}: {ticker.ArchiveStatus} {ticker.Error}"));
        }
        catch (Exception error) { HasError = true; StatusMessage = error.Message; }
        await LoadAsync(token);
        return Page();
    }

    /// <summary>Normalizes only entered symbols before passing them to the service.</summary>
    private static IEnumerable<string> SplitCsv(string? value)
    {
        return (value ?? String.Empty)
            .Split(',', '\n', '\r')
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
