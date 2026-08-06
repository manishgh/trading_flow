using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

/// <summary>
/// The earnings monitor.
///
/// Advisory only: this screen cannot route an order, and it says so on itself
/// rather than relying on the absence of a button to imply it.
///
/// Anonymous by design - the calendar and its evidence are readable without an
/// account. The one thing that needs a signed-in operator is the "symbols you
/// hold" lane, which is empty for an anonymous visitor because there is no book
/// to compare against.
/// </summary>
[AllowAnonymous]
public sealed class EarningsModel : PageModel
{
    private readonly PaperJobService paperJobs;
    private readonly MobileAutomationService automation;

    public EarningsModel(PaperJobService paperJobs, MobileAutomationService automation)
    {
        this.paperJobs = paperJobs;
        this.automation = automation;
    }

    /// <summary>
    /// Tickers the operator currently holds, upper-cased. Empty for an anonymous
    /// visitor: the held lane then counts zero rather than guessing.
    /// </summary>
    public IReadOnlyList<string> HeldTickers { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var trades = await RunningTradesBuilder.BuildAsync(paperJobs, automation);
        HeldTickers = trades
            .Select(trade => trade.Ticker.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ticker => ticker, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
