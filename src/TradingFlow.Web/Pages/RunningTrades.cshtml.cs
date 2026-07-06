using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

public sealed class RunningTradesModel : PageModel
{
    private readonly PaperJobService paperJobs;
    private readonly MobileAutomationService automation;

    public RunningTradesModel(PaperJobService paperJobs, MobileAutomationService automation)
    {
        this.paperJobs = paperJobs;
        this.automation = automation;
    }

    [BindProperty(SupportsGet = true)] public string Source { get; set; } = "all";

    public IReadOnlyList<MobileRunningTrade> Trades { get; private set; } = Array.Empty<MobileRunningTrade>();

    public decimal TotalPl { get; private set; }

    public static IReadOnlyList<(string Key, string Label)> Sources { get; } = new[]
    {
        ("all", "All"),
        ("wishlist", "Wishlist"),
        ("stockpulse", "Stock Pulse"),
        ("manual", "Manual")
    };

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostCloseAsync(string closeKind, Guid? jobId, Guid? sessionId, string ticker)
    {
        if (closeKind == "paper_job" && jobId is { } job)
        {
            await paperJobs.ClosePositionAsync(job, ticker);
        }
        else if (closeKind == "automation" && sessionId is { } session)
        {
            await automation.CloseAsync(session);
        }

        return RedirectToPage(new { Source });
    }

    private async Task LoadAsync()
    {
        var all = await RunningTradesBuilder.BuildAsync(paperJobs, automation);
        Trades = RunningTradesBuilder.Filter(all, Source);
        TotalPl = Trades.Sum(trade => trade.UnrealizedPl);
    }
}
