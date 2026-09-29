using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Pages.Shared;

/// <summary>
/// The side-by-side evidence pair: TradingFlow's decision on the left, the
/// model's evidence on the right.
///
/// They are rendered at equal width because they are equally worth reading, not
/// because they carry equal authority. Only eligibility can authorise an entry,
/// and each panel says which of the two it is.
/// </summary>
/// <param name="Row">The ranked row, carrying both sides.</param>
/// <param name="Compact">Rail sizing rather than focus-column sizing.</param>
public sealed record DeskEvidenceModel(RankedDeskRow Row, bool Compact)
{
    public decimal? Probability => Row.Evidence.Swing?.Probability;

    public string CatalystStatus => Row.Evidence.Swing?.Catalyst.Status ?? "unknown";

    public string CatalystDirection => Row.Evidence.Swing?.Catalyst.Direction ?? "unknown";
}

/// <summary>
/// The inline order ticket.
///
/// It lives on the desk so a reviewed order does not require leaving the
/// evidence behind, and on the Orders screen so another order can be placed
/// beside the journal. Stage one previews and the server re-checks quote, spread,
/// session, account, exposure and duplicates; stage two confirms against the
/// token that preview issued. The checklist renders that response - it is a
/// preview of what the server checked, never a substitute for it.
/// </summary>
/// <param name="Row">The selected desk row: quote, position and identity. No ranking is needed to trade.</param>
/// <param name="Env">Environment slug carried through the post.</param>
/// <param name="Horizon">Ticket horizon, following the desk's own horizon.</param>
/// <param name="Plan">
/// Opening limit, stop and target from market data and the strategy's own rules.
/// A value that cannot be derived is empty on the ticket, with the reason shown.
/// </param>
/// <param name="HasPosition">
/// Whether a tracked position exists. SELL renders only when it does, so the
/// control cannot invite an accidental short.
/// </param>
/// <param name="Preview">Server preview, or null before one has been requested.</param>
/// <param name="Confirmation">Accepted order, or null.</param>
public sealed record DeskTicketModel(
    WishlistDeskRow Row,
    string? Env,
    string Horizon,
    DeskTicketPlan Plan,
    bool HasPosition,
    ManualOrderTicketPreview? Preview,
    ManualOrderTicketConfirmation? Confirmation)
{
    /// <summary>
    /// The desk view the ticket was opened from - scope, wishlist or screener,
    /// filters and the selected ticker. Both ticket posts carry it, so a preview
    /// or confirm re-renders the same universe; without it a screener hit that is
    /// on no wishlist would drop out of the view mid-ticket.
    /// </summary>
    public IDictionary<string, string> ViewRoute { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Side the ticket opens on before any preview. Replace on the Orders screen
    /// opens a sell ticket for a sell order; it only takes effect where a tracked
    /// position exists, because SELL is not offered otherwise.
    /// </summary>
    public string DefaultSide { get; init; } = "buy";

    /// <summary>
    /// The six checks the ticket states, mirroring what the server checks at
    /// submit. Rendered from the preview response rather than recomputed
    /// client-side, so the screen cannot disagree with the server.
    /// </summary>
    public IReadOnlyList<(string Label, bool Passed, string Value)> Checks
    {
        get
        {
            if (Preview is not { } preview)
            {
                return [];
            }

            var rejections = preview.Rejections;
            bool NoRejection(params string[] fragments) => !rejections.Any(rejection =>
                fragments.Any(fragment => rejection.Contains(fragment, StringComparison.OrdinalIgnoreCase)));

            return
            [
                ("Quote age", NoRejection("quote", "stale"),
                    preview.QuoteAgeMilliseconds is { } age ? $"{age} ms" : "unknown"),
                ("Spread", NoRejection("spread"),
                    preview.SpreadBps is { } spread ? $"{spread:0.0} bps" : "unknown"),
                ("Session", NoRejection("session", "hours"), preview.Session),
                ("Notional", NoRejection("notional", "exposure", "buying power"),
                    preview.Notional.ToString("C2")),
                ("Duplicate", NoRejection("duplicate", "already"),
                    NoRejection("duplicate", "already") ? "no open position" : "position already open"),
                ("Protection", NoRejection("stop", "protect", "bracket"),
                    preview.StopLossPrice is { } stop ? $"stop {stop:C2}" : "no stop attached")
            ];
        }
    }

    /// <summary>Failing check labels, named in the blocked note.</summary>
    public IReadOnlyList<string> FailingChecks =>
        Checks.Where(check => !check.Passed).Select(check => check.Label).ToArray();

    public bool IsBlocked => Preview is not null && !Preview.CanSubmit;
}

/// <summary>
/// Inline ticket fields, posted by both the desk and the Orders screen. Maps
/// one-to-one onto <see cref="ManualOrderDraft"/>.
/// </summary>
public sealed class DeskTicketForm
{
    public string Ticker { get; set; } = String.Empty;
    public string Side { get; set; } = "buy";
    public decimal Quantity { get; set; } = 1m;
    public decimal LimitPrice { get; set; }
    public decimal? StopLossPrice { get; set; }
    public decimal? TakeProfitPrice { get; set; }
    public string Horizon { get; set; } = "swing";
    public string OrderType { get; set; } = "limit";
    public decimal? TriggerPrice { get; set; }
    public string TimeInForce { get; set; } = "day";
    public bool AllowExtendedHoursTrading { get; set; }

    public bool IsExit => String.Equals(Side, "sell", StringComparison.OrdinalIgnoreCase);

    public ManualOrderDraft ToDraft() => new(
        Ticker.Trim().ToUpperInvariant(),
        IsExit ? "sell" : "buy",
        Quantity,
        LimitPrice,
        // An exit carries no bracket: the protection belonged to the entry.
        IsExit ? null : StopLossPrice,
        IsExit ? null : TakeProfitPrice,
        "swing",
        AllowExtendedHoursTrading,
        "sip",
        IsExit ? OrderType : "limit",
        TriggerPrice,
        TimeInForce);
}
