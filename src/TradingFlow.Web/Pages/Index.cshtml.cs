using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace TradingFlow.Web.Pages;
public sealed class IndexModel : PageModel
{
    // The Trading Desk is the landing page; signing in is required and handled by the Desk's authorization.
    public IActionResult OnGet() => RedirectToPage("/Desk");
}
