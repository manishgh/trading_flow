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
