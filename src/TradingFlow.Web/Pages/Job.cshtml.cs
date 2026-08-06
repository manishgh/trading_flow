using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TradingFlow.Web.Pages;

/// <summary>
/// Legacy run route.
/// </summary>
/// <remarks>
/// Configure, Running and Results are three phases of one screen now, so this
/// only forwards to it. The route is kept because run links were shared and
/// bookmarked while it was a page of its own.
/// </remarks>
public sealed class JobModel : PageModel
{
    public IActionResult OnGet(Guid id) => RedirectToPage("/Backtests", new { jobId = id });
}
