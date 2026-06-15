using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Web.Services;

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

    public IReadOnlyList<WarmupTickerIntentDto> Watchlist { get; private set; } = [];
    public IReadOnlyList<WarmupRunRecordDto> Runs { get; private set; } = [];
    public string ServiceBaseUrl => warmupClient.BaseUrl;
    public string? StatusMessage { get; private set; }
    public bool HasError { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

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
                    RunNow),
                cancellationToken);
            StatusMessage = accepted is null
                ? "Warmup request submitted."
                : $"Accepted {accepted.Accepted} ticker(s). Run queued: {accepted.RunQueued}.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            HasError = true;
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRunNowAsync(CancellationToken cancellationToken)
    {
        try
        {
            var queued = await warmupClient.RunNowAsync(new WarmupRunNowRequestDto(null, "web-run-all"), cancellationToken);
            StatusMessage = queued is null ? "Warmup run queued." : $"Warmup run queued: {queued.RunId}.";
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

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Watchlist = await warmupClient.GetWatchlistAsync(cancellationToken) ?? [];
            Runs = await warmupClient.GetRunsAsync(cancellationToken) ?? [];
        }
        catch (Exception exception)
        {
            Watchlist = [];
            Runs = [];
            StatusMessage ??= $"Warmup service unavailable: {exception.Message}";
            HasError = true;
        }
    }

    private static IEnumerable<string> SplitCsv(string? value)
    {
        return (value ?? String.Empty)
            .Split(',', '\n', '\r')
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
