using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

public sealed class OrderTicketModel(ManualOrderTicketService tickets) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid? WishlistId { get; set; }
    [BindProperty(SupportsGet = true)] public string Ticker { get; set; } = String.Empty;
    [BindProperty] public decimal Quantity { get; set; } = 1m;
    [BindProperty(SupportsGet = true)] public decimal LimitPrice { get; set; }
    [BindProperty] public decimal StopLossPrice { get; set; }
    [BindProperty] public decimal TakeProfitPrice { get; set; }
    [BindProperty] public string Horizon { get; set; } = "intraday";
    [BindProperty] public bool AllowExtendedHoursTrading { get; set; }
    [BindProperty] public string? TicketToken { get; set; }

    public ManualOrderTicketPreview? Preview { get; private set; }
    public ManualOrderTicketConfirmation? Confirmation { get; private set; }
    public string? ErrorMessage { get; private set; }

    public void OnGet()
    {
        Ticker = Ticker.Trim().ToUpperInvariant();
    }

    public async Task OnPostPreviewAsync(CancellationToken cancellationToken)
    {
        try
        {
            Preview = await tickets.PreviewAsync(BuildDraft(), cancellationToken);
            TicketToken = Preview.TicketToken;
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    public async Task OnPostConfirmAsync(CancellationToken cancellationToken)
    {
        try
        {
            Confirmation = await tickets.ConfirmAsync(TicketToken ?? String.Empty, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    private ManualOrderDraft BuildDraft() => new(
        Ticker,
        "buy",
        Quantity,
        LimitPrice,
        StopLossPrice,
        TakeProfitPrice,
        Horizon,
        AllowExtendedHoursTrading);
}
