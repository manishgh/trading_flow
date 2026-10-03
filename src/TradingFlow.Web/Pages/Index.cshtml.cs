using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace TradingFlow.Web.Pages;
public sealed class IndexModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Desk");
}
