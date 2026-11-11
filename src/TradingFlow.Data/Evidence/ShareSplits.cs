using System.Globalization;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Data.Evidence;

/// <summary>Turns corporate-action evidence rows into each stock's share splits, shared by the daily screen and backtests.</summary>
public static class ShareSplits
{
    /// <summary>
    /// Forward and reverse splits with a symbol and usable old/new rates, grouped by symbol. The ex-date falls back to the
    /// effective date and then the process date; the ratio is new shares per old share.
    /// </summary>
    public static Dictionary<string, IReadOnlyList<ShareSplit>> FromCorporateActions(IEnumerable<CorporateActionEvidenceRow> actions) =>
        actions.Where(action => action.ActionType is CorporateActionEvidenceType.ForwardSplit or CorporateActionEvidenceType.ReverseSplit &&
                action.PrimarySymbol is not null &&
                action.ProviderFields.TryGetValue("old_rate", out var oldRate) && action.ProviderFields.TryGetValue("new_rate", out var newRate) &&
                decimal.TryParse(oldRate, NumberStyles.Number, CultureInfo.InvariantCulture, out var oldValue) && oldValue > 0m &&
                decimal.TryParse(newRate, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            .GroupBy(action => action.PrimarySymbol!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ShareSplit>)group
                .Select(action => new ShareSplit(action.ExDate ?? action.EffectiveDate ?? action.ProcessDate,
                    decimal.Parse(action.ProviderFields["new_rate"], CultureInfo.InvariantCulture) /
                    decimal.Parse(action.ProviderFields["old_rate"], CultureInfo.InvariantCulture)))
                .Distinct().ToArray(), StringComparer.Ordinal);
}
