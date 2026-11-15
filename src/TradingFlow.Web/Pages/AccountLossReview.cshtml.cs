using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Data.Identity;
using TradingFlow.Domain.Execution;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Pages;

[Authorize(Roles = TradingFlowRoles.Administrator)]
public sealed class AccountLossReviewModel(IAccountLossReviewService reviews) : PageModel
{
    public AccountLossReviewSnapshot? Snapshot { get; private set; }
    public string? Message { get; private set; }
    [BindProperty] public long ExpectedRevision { get; set; }
    [BindProperty] public string ReviewAction { get; set; } = "reset_peak";
    [BindProperty] public string Reason { get; set; } = "";

    // Display server-bound account state without changing any latch.
    public async Task OnGetAsync(CancellationToken token) => await LoadAsync(token);

    // Razor antiforgery and role authorization protect this explicit administrator action.
    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actor)) return Forbid();
        try
        {
            Snapshot = await reviews.ReviewAsync(new(ExpectedRevision, ReviewAction, Reason), actor, token);
            ExpectedRevision = Snapshot.Revision;
            ModelState.Remove(nameof(ExpectedRevision));
            Message = "Review recorded. Daily/weekly limits and other entry blocks still apply.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            Message = exception.Message;
            await LoadAsync(token);
        }
        return Page();
    }

    // Expose provider failures without offering a reset on unknown evidence.
    private async Task LoadAsync(CancellationToken token)
    {
        try
        {
            Snapshot = await reviews.GetAsync(token);
            ExpectedRevision = Snapshot.Revision;
            ModelState.Remove(nameof(ExpectedRevision));
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        { Message ??= "Account review could not load: " + exception.Message; }
    }
}
