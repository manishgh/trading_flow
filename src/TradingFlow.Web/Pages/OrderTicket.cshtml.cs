using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

public sealed class OrderTicketModel(
    ManualOrderTicketService tickets,
    TradingEnvironmentService environments) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid? WishlistId { get; set; }
    [BindProperty(SupportsGet = true)] public string Ticker { get; set; } = String.Empty;
    [BindProperty(SupportsGet = true)] public string? Env { get; set; }
    [BindProperty(SupportsGet = true)] public string Side { get; set; } = "buy";
    [BindProperty] public decimal Quantity { get; set; } = 1m;
    [BindProperty(SupportsGet = true)] public decimal LimitPrice { get; set; }
    [BindProperty] public decimal StopLossPrice { get; set; }
    [BindProperty] public decimal TakeProfitPrice { get; set; }
    [BindProperty] public string Horizon { get; set; } = "swing";
    [BindProperty] public string OrderType { get; set; } = "limit";
    [BindProperty] public decimal? TriggerPrice { get; set; }
    [BindProperty] public string TimeInForce { get; set; } = "day";
    [BindProperty] public bool AllowExtendedHoursTrading { get; set; }
    [BindProperty] public string? TicketToken { get; set; }

    public ManualOrderTicketPreview? Preview { get; private set; }
    public ManualOrderTicketConfirmation? Confirmation { get; private set; }
    public string? ErrorMessage { get; private set; }

    public TradingEnvironmentState EnvironmentState => environments.GetState(environments.Parse(Env));

    /// <summary>True when this ticket closes an existing position rather than opening one.</summary>
    public bool IsExit => NormalizedSide == "sell";

    private string NormalizedSide =>
        String.Equals(Side, "sell", StringComparison.OrdinalIgnoreCase) ? "sell" : "buy";

    public void OnGet()
    {
        Ticker = Ticker.Trim().ToUpperInvariant();
    }

    public async Task OnPostPreviewAsync(CancellationToken cancellationToken)
    {
        // The locked-environment check is repeated here rather than relying on the
        // view hiding the form. A POST can be issued without ever rendering it.
        if (!EnvironmentState.IsEnabled)
        {
            ErrorMessage = EnvironmentState.LockReason;
            return;
        }

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
        if (!EnvironmentState.IsEnabled)
        {
            ErrorMessage = EnvironmentState.LockReason;
            return;
        }

        try
        {
            Confirmation = await tickets.ConfirmAsync(TicketToken ?? String.Empty, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    /// <summary>Order types available for this side.</summary>
    /// <remarks>
    /// An entry is always a bracketed limit so the stop and target stay attached.
    /// The wider set belongs to exits.
    /// </remarks>
    public IReadOnlyList<(string Key, string Label)> OrderTypes => IsExit
        ?
        [
            ("limit", "Limit"),
            ("market", "Market"),
            ("stop", "Stop"),
            ("stop_limit", "Stop limit")
        ]
        : [("limit", "Limit (protected entry)")];

    public static IReadOnlyList<(string Key, string Label)> TimesInForce { get; } =
    [
        ("day", "Day"),
        ("gtc", "Good till cancelled"),
        ("ioc", "Immediate or cancel"),
        ("fok", "Fill or kill")
    ];

    /// <summary>Whether the chosen type needs a limit price.</summary>
    public bool NeedsLimitPrice => OrderType is "limit" or "stop_limit";

    /// <summary>Whether the chosen type needs a stop trigger.</summary>
    public bool NeedsTriggerPrice => OrderType is "stop" or "stop_limit";

    private ManualOrderDraft BuildDraft() => new(
        Ticker,
        NormalizedSide,
        Quantity,
        LimitPrice,
        // An exit carries no bracket: the protection belonged to the entry.
        IsExit ? null : StopLossPrice,
        IsExit ? null : TakeProfitPrice,
        Horizon,
        AllowExtendedHoursTrading,
        "sip",
        IsExit ? OrderType : "limit",
        TriggerPrice,
        TimeInForce);
}
